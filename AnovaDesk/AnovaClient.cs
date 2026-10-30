using System.ComponentModel;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnovaDesk;

/// <summary>A Wi-Fi device reported by EVENT_APC_WIFI_LIST / EVENT_APO_WIFI_LIST.</summary>
public sealed class AnovaDevice : INotifyPropertyChanged
{
    private bool _isOnline;

    public string CookerId { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Protocol generation: a3, a4, a5, a6, a7, a8, pro, oven_v1, oven_v2 ...</summary>
    public string Type { get; init; } = "";
    /// <summary>The command family the device belongs to: "APC" (cooker) or "APO" (oven).</summary>
    public string Family { get; init; } = "APC";

    /// <summary>True once this device has reported state during the current session.</summary>
    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (_isOnline == value) return;
            _isOnline = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
        }
    }

    public string Display =>
        $"{(IsOnline ? "\u25CF" : "\u25CB")}  {(string.IsNullOrWhiteSpace(Name) ? Family : Name)}  ({Type})";

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Minimal client for Anova's documented Wi-Fi developer API.
/// Transport: wss://devices.anovaculinary.io?token=...&supportedAccessories=APC,APO
/// </summary>
public sealed class AnovaClient : IAsyncDisposable
{
    private const string Endpoint = "wss://devices.anovaculinary.io";

    private readonly object _gate = new();
    private readonly Dictionary<string, AnovaDevice> _devices = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _wantConnection;

    public event Action<string>? Log;
    public event Action<IReadOnlyList<AnovaDevice>>? DevicesChanged;
    public event Action<string, JsonNode?>? MessageReceived;   // raw command + payload, for anything we don't model
    public event Action<bool, string>? ConnectionChanged;

    public DateTime LastMessageUtc { get; private set; } = DateTime.MinValue;
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public IReadOnlyList<AnovaDevice> Devices
    {
        get { lock (_gate) return _devices.Values.ToList(); }
    }

    public void Start(string token)
    {
        Stop();
        _wantConnection = true;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(token.Trim(), _cts.Token));
    }

    public void Stop()
    {
        _wantConnection = false;
        try { _cts?.Cancel(); } catch { /* already disposed */ }
        try { _socket?.Abort(); } catch { /* best effort */ }
        _cts = null;
        _socket = null;
    }

    private static Uri BuildUri(string token) =>
        new($"{Endpoint}?token={Uri.EscapeDataString(token)}&supportedAccessories=APC,APO");

    private async Task RunLoopAsync(string token, CancellationToken ct)
    {
        var attempt = 0;
        while (_wantConnection && !ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndPumpAsync(token, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke("Connection error: " + ex.Message);
                ConnectionChanged?.Invoke(false, ex.Message);
            }

            if (!_wantConnection || ct.IsCancellationRequested) break;

            attempt++;
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
            Log?.Invoke($"Reconnecting in {delay.TotalSeconds:0}s...");
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        ConnectionChanged?.Invoke(false, "Disconnected");
    }

    private async Task ConnectAndPumpAsync(string token, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _socket = socket;

        Log?.Invoke("Connecting to devices.anovaculinary.io ...");
        await socket.ConnectAsync(BuildUri(token), ct).ConfigureAwait(false);
        Log?.Invoke("WebSocket open.");
        ConnectionChanged?.Invoke(true, "Connected");

        var buffer = new byte[16 * 1024];
        var frame = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            // A quiet socket for ~30 min is closed by the server; reconnect before that happens.
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sinceLast = DateTime.UtcNow - LastMessageUtc;
            var remaining = TimeSpan.FromMinutes(20) - sinceLast;
            idle.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1));

            WebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), idle.Token)
                                         .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        var why = socket.CloseStatusDescription ?? socket.CloseStatus?.ToString() ?? "closed by server";
                        Log?.Invoke("Server closed the connection: " + why);
                        ConnectionChanged?.Invoke(false, why);
                        return;
                    }
                    frame.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log?.Invoke("Idle timeout reached; refreshing session.");
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "refresh", ct)
                            .ConfigureAwait(false);
                return;
            }

            var text = Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length);
            frame.SetLength(0);
            LastMessageUtc = DateTime.UtcNow;

            HandleMessage(text);
        }
    }

    private void HandleMessage(string text)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch
        {
            Log?.Invoke("Unparseable frame: " + text);
            return;
        }

        var command = node?["command"]?.GetValue<string>() ?? "(no command)";
        var payload = node?["payload"];

        // ---- Device inventory -------------------------------------------------
        if (payload is JsonArray list && LooksLikeDeviceList(list))
        {
            var changed = false;
            foreach (var item in list.OfType<JsonObject>())
            {
                var id = item["cookerId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) continue;

                var family = command.Contains("APO", StringComparison.Ordinal) ? "APO" : "APC";
                var device = new AnovaDevice
                {
                    CookerId = id,
                    Name = item["name"]?.GetValue<string>() ?? "",
                    Type = item["type"]?.GetValue<string>() ?? "unknown",
                    Family = family,
                };

                lock (_gate)
                {
                    if (!_devices.TryGetValue(id, out var existing) ||
                        existing.Name != device.Name || existing.Type != device.Type)
                    {
                        _devices[id] = device;
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                var snapshot = Devices;
                Log?.Invoke($"Devices: {string.Join(", ", snapshot.Select(d => d.Display))}");
                DevicesChanged?.Invoke(snapshot);
            }
        }

        if (command.Contains("STATE", StringComparison.OrdinalIgnoreCase))
            Log?.Invoke($"state <- {Shorten(text, 400)}");

        MessageReceived?.Invoke(command, payload);
    }

    private static bool LooksLikeDeviceList(JsonArray array) =>
        array.Count > 0 && array[0] is JsonObject o && o.ContainsKey("cookerId");

    private static string Shorten(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    // ---- Commands ------------------------------------------------------------

    /// <summary>Start a cook. Temperature is always sent in Celsius; the device stores it that way.</summary>
    public Task StartCookAsync(AnovaDevice device, double celsius, int timerSeconds) =>
        SendAsync(new JsonObject
        {
            ["command"] = "CMD_APC_START",
            ["requestId"] = Guid.NewGuid().ToString(),
            ["payload"] = new JsonObject
            {
                ["cookerId"] = device.CookerId,
                ["type"] = device.Type,
                ["targetTemperature"] = Math.Round(celsius, 1),
                ["unit"] = "C",
                ["timer"] = timerSeconds,
            },
        });

    public Task StopCookAsync(AnovaDevice device) =>
        SendAsync(new JsonObject
        {
            ["command"] = "CMD_APC_STOP",
            ["requestId"] = Guid.NewGuid().ToString(),
            ["payload"] = new JsonObject
            {
                ["cookerId"] = device.CookerId,
                ["type"] = device.Type,
            },
        });

    public Task SetUnitAsync(AnovaDevice device, string unit) =>
        SendAsync(new JsonObject
        {
            ["command"] = "CMD_APC_SET_TEMPERATURE_UNIT",
            ["requestId"] = Guid.NewGuid().ToString(),
            ["payload"] = new JsonObject
            {
                ["cookerId"] = device.CookerId,
                ["type"] = device.Type,
                ["unit"] = unit,
            },
        });

    private async Task SendAsync(JsonObject message)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open })
            throw new InvalidOperationException("Not connected.");

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None)
                        .ConfigureAwait(false);
            Log?.Invoke($"-> {message["command"]}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* shutting down */ }
        }
        _sendLock.Dispose();
    }
}
