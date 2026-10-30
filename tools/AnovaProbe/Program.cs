using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Inspects Anova's developer websocket.
//
//   AnovaProbe [seconds]                       passive dump of every frame
//   AnovaProbe --cycle <cookerId> <type> <tempC> <timerSeconds> [observeSeconds]
//                                              connect, START, observe, STOP
//
// Token comes from ARGV (a value starting with "anova-") or ANOVA_TOKEN.
// The token is never written to disk.

var token = args.FirstOrDefault(a => a.StartsWith("anova-", StringComparison.OrdinalIgnoreCase))
            ?? Environment.GetEnvironmentVariable("ANOVA_TOKEN");

if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("usage: AnovaProbe [seconds]");
    Console.Error.WriteLine("       AnovaProbe --cycle <cookerId> <type> <tempC> <timerSeconds> [observeSeconds]");
    Console.Error.WriteLine("   or: set ANOVA_TOKEN=anova-...");
    return 2;
}

var uri = new Uri($"wss://devices.anovaculinary.io?token={Uri.EscapeDataString(token.Trim())}&supportedAccessories=APC,APO");

using var socket = new ClientWebSocket();
socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

try
{
    await socket.ConnectAsync(uri, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine("connect failed: " + ex.Message);
    return 1;
}

Console.WriteLine("connected.\n");

if (args.Length >= 6 && args[0] == "--cycle")
{
    return await RunCycleAsync(socket,
        cookerId: args[1], type: args[2],
        tempC: double.Parse(args[3], CultureInfo.InvariantCulture),
        timerSeconds: int.Parse(args[4], CultureInfo.InvariantCulture),
        observeSeconds: int.Parse(args[5], CultureInfo.InvariantCulture));
}

var passiveSeconds = 15;
foreach (var a in args)
    if (int.TryParse(a, out var v) && v > 0) { passiveSeconds = v; break; }

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(passiveSeconds));
var loop = ReceiveLoopAsync(socket, cts.Token);
await loop;
Console.WriteLine($"\n[passive window ended]");
return 0;

// ---------------------------------------------------------------- helpers

static async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct, Func<string, bool>? onFrame = null)
{
    var buffer = new byte[32 * 1024];
    var frame = new MemoryStream();
    try
    {
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                Console.WriteLine($"[server close] {socket.CloseStatus} {socket.CloseStatusDescription}");
                return;
            }
            frame.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length);
            frame.SetLength(0);

            var handled = onFrame?.Invoke(text) ?? false;
            if (!handled) PrintFrame(text);
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        Console.WriteLine("[receive ended] " + ex.Message);
    }
}

static void PrintFrame(string text)
{
    JsonNode? node;
    try { node = JsonNode.Parse(text); }
    catch { Console.WriteLine("[raw] " + text); return; }

    var command = node?["command"]?.GetValue<string>() ?? "(none)";

    if (command is "EVENT_APC_WIFI_LIST" or "EVENT_APO_WIFI_LIST" or "EVENT_USER_STATE")
        return;

    var payload = node?["payload"];

    if (command == "EVENT_APC_STATE" && payload?["state"] is JsonObject s)
    {
        var job = s["job"];
        var status = s["job-status"];
        var temps = s["temperature-info"];
        var pins = s["pin-info"];
        Console.WriteLine(
            $"  state  mode={job?["mode"]?.GetValue<string>(),-10} " +
            $"jstate={status?["state"]?.GetValue<string>(),-14} " +
            $"water={temps?["water-temperature"],6} " +
            $"heater={temps?["heater-temperature"],6} " +
            $"tgt={job?["target-temperature"],6}{job?["temperature-unit"]?.GetValue<string>()} " +
            $"cooksecs={job?["cook-time-seconds"],6} " +
            $"rem={status?["cook-time-remaining"],6} " +
            $"start={status?["job-start-systick"]} " +
            $"pins(lvl/leak/hot/fault)={pins?["water-level-critical"]}/{pins?["water-leak"]}/{pins?["water-temp-too-high"]}/{pins?["device-safe"]}");
        return;
    }

    Console.WriteLine($"  >> {command}  {node!.ToJsonString()}");
}

static async Task SendAsync(ClientWebSocket socket, string json)
{
    var bytes = Encoding.UTF8.GetBytes(json);
    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    Console.WriteLine("  << " + json);
}

static async Task<int> RunCycleAsync(ClientWebSocket socket, string cookerId, string type,
                                     double tempC, int timerSeconds, int observeSeconds)
{
    using var cts = new CancellationTokenSource();
    var loop = ReceiveLoopAsync(socket, cts.Token);

    Console.WriteLine("--- baseline ---");
    await Task.Delay(2500);

    var start = new JsonObject
    {
        ["command"] = "CMD_APC_START",
        ["requestId"] = Guid.NewGuid().ToString(),
        ["payload"] = new JsonObject
        {
            ["cookerId"] = cookerId,
            ["type"] = type,
            ["targetTemperature"] = tempC,
            ["unit"] = "C",
            ["timer"] = timerSeconds,
        },
    };

    Console.WriteLine("\n--- CMD_APC_START ---");
    await SendAsync(socket, start.ToJsonString());
    await Task.Delay(TimeSpan.FromSeconds(observeSeconds));

    var stop = new JsonObject
    {
        ["command"] = "CMD_APC_STOP",
        ["requestId"] = Guid.NewGuid().ToString(),
        ["payload"] = new JsonObject { ["cookerId"] = cookerId, ["type"] = type },
    };

    Console.WriteLine("\n--- CMD_APC_STOP ---");
    await SendAsync(socket, stop.ToJsonString());
    await Task.Delay(5000);

    cts.Cancel();
    await loop;
    Console.WriteLine("\n[cycle complete]");
    return 0;
}
