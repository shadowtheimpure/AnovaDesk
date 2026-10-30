using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

// Drives the real AnovaDesk window through UI Automation so interaction bugs are caught
// before a person finds them. Each scenario launches a fresh instance in an isolated
// profile (--profile smoke --offline), so the signed-in token is never touched and no
// network calls are made.
//
//   dotnet run --project tools\UiSmoke                 (all scenarios)
//   dotnet run --project tools\UiSmoke -- --scenario advanced
//   dotnet run --project tools\UiSmoke -- --exe <path to AnovaDesk.exe>

const string Profile = "smoke";
const string CredTarget = "AnovaDesk/PersonalAccessToken/smoke";
const string FakeToken = "anova-smoketest-not-a-real-token";

var exe = ResolveExe(args);
var wanted = GetArg(args, "--scenario") ?? "all";

Console.WriteLine($"target   : {exe}");
Console.WriteLine($"profile  : {Profile} (isolated, offline)");

// Safety gate. If the target build ignores --profile it would read the signed-in token, and
// the "forget" scenario would delete it. A build that honours the profile shows the
// get-started panel even though a real token exists; one that ignores it does not.
if (!VerifyIsolation(exe))
{
    Console.Error.WriteLine("\nABORT: this build does not honour --profile -- refusing to run,");
    Console.Error.WriteLine("       because 'forget' would delete your real Anova token.");
    CredDelete(CredTarget);
    return 3;
}
Console.WriteLine("isolation: verified (real token unreachable)");

var scenarios = new (string Name, Func<string, bool> Run)[]
{
    ("no-token",       RunNoToken),
    ("forget",         RunForget),
    ("advanced",       RunAdvanced),
    ("close-cancel",   RunCloseCancel),
    ("close-minimize", RunCloseMinimize),
    ("close-exit",     RunCloseExit),
};

var selected = wanted == "all"
    ? scenarios
    : scenarios.Where(s => s.Name == wanted).ToArray();

if (selected.Length == 0)
{
    Console.Error.WriteLine($"unknown scenario '{wanted}'. known: {string.Join(", ", scenarios.Select(s => s.Name))}");
    return 2;
}

var failures = 0;
foreach (var (name, run) in selected)
{
    Console.WriteLine($"\n=== {name} ===");
    bool ok;
    try
    {
        ok = run(exe);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        ok = false;
    }

    Console.WriteLine(ok ? "  RESULT: PASS" : "  RESULT: FAIL");
    if (!ok) failures++;
}

CredDelete(CredTarget);
Console.WriteLine(failures == 0 ? "\nALL PASS" : $"\n{failures} FAILED");
return failures == 0 ? 0 : 1;

// ---------------------------------------------------------------- scenarios

static bool VerifyIsolation(string exe)
{
    CredDelete(CredTarget);
    var app = Launch(exe);
    try
    {
        if (WaitForMainWindow(app) is null) return false;
        return FindByName(app.Id, "Connect your cooker") is not null;
    }
    finally { Kill(app); }
}

static bool RunNoToken(string exe)
{
    CredDelete(CredTarget);
    var app = Launch(exe);
    try
    {
        if (WaitForMainWindow(app) is null) { Console.WriteLine("  main window never appeared"); return false; }

        var intro = FindByName(app.Id, "Connect your cooker") is not null;
        var controls = FindByName(app.Id, "Start cook") is not null;
        Console.WriteLine($"  get-started shown  : {intro}");
        Console.WriteLine($"  cook controls shown: {controls}");
        return intro && !controls;
    }
    finally { Kill(app); }
}

static bool RunForget(string exe)
{
    CredWriteToken(CredTarget, FakeToken);
    var app = Launch(exe);
    try
    {
        if (WaitForMainWindow(app) is null) { Console.WriteLine("  main window never appeared"); return false; }

        var controlsBefore = FindByName(app.Id, "Start cook") is not null;
        var introBefore = FindByName(app.Id, "Connect your cooker") is not null;
        Console.WriteLine($"  before: controls={controlsBefore} intro={introBefore}");
        if (!controlsBefore || introBefore) return false;

        var forget = FindByName(app.Id, "Forget token");
        if (forget is null) { Console.WriteLine("  'Forget token' not found"); return false; }
        if (!Invoke(forget)) { Console.WriteLine("  could not invoke 'Forget token'"); return false; }

        var introAfter = WaitUntil(() => FindByName(app.Id, "Connect your cooker") is not null, 6000);
        var controlsAfter = FindByName(app.Id, "Start cook") is not null;
        Console.WriteLine($"  after : intro={introAfter} controls={controlsAfter}");
        return introAfter && !controlsAfter;
    }
    finally { Kill(app); }
}

static bool RunAdvanced(string exe)
{
    CredWriteToken(CredTarget, FakeToken);
    var app = Launch(exe);
    try
    {
        var toggle = WaitForMainWindow(app);
        if (toggle is null) { Console.WriteLine("  main window never appeared"); return false; }

        var hiddenBefore = PanelsHidden(app.Id);
        Console.WriteLine($"  off -> panels hidden : {hiddenBefore}");

        if (!Toggle(toggle)) { Console.WriteLine("  could not toggle 'Advanced view'"); return false; }
        var shown = WaitUntil(() =>
            FindByName(app.Id, "DEVICE STATE") is not null && FindByName(app.Id, "TRAFFIC") is not null, 6000);
        Console.WriteLine($"  on  -> panels shown  : {shown}");

        var toggleAgain = FindByName(app.Id, "Advanced view");
        if (toggleAgain is null) { Console.WriteLine("  'Advanced view' lost after toggle"); return false; }
        Toggle(toggleAgain);
        var hiddenAgain = WaitUntil(() => PanelsHidden(app.Id), 6000);
        Console.WriteLine($"  off -> hidden again  : {hiddenAgain}");

        return hiddenBefore && shown && hiddenAgain;
    }
    finally { Kill(app); }
}

static bool RunCloseCancel(string exe)
{
    var app = LaunchAndPrompt(exe);
    try
    {
        var cancel = FindByName(app.Id, "Cancel");
        if (cancel is null) { Console.WriteLine("  'Cancel' not found"); return false; }
        Invoke(cancel);

        var promptGone = WaitUntil(() => !HasWindowTitled(app.Id, "Close AnovaDesk"), 6000);
        var alive = !app.HasExited;
        var mainVisible = HasWindowTitled(app.Id, "AnovaDesk");
        Console.WriteLine($"  prompt gone={promptGone} alive={alive} main visible={mainVisible}");
        return promptGone && alive && mainVisible;
    }
    finally { Kill(app); }
}

static bool RunCloseMinimize(string exe)
{
    var app = LaunchAndPrompt(exe);
    try
    {
        var button = FindByName(app.Id, "Minimize to tray");
        if (button is null) { Console.WriteLine("  'Minimize to tray' not found"); return false; }
        Invoke(button);

        var hidden = WaitUntil(() => VisibleTitles(app.Id).Count == 0, 8000);
        var alive = !app.HasExited;
        Console.WriteLine($"  hidden={hidden} alive={alive}");
        return hidden && alive;
    }
    finally { Kill(app); }
}

static bool RunCloseExit(string exe)
{
    var app = LaunchAndPrompt(exe);
    try
    {
        var button = FindByName(app.Id, "Exit");
        if (button is null) { Console.WriteLine("  'Exit' not found"); return false; }
        Invoke(button);

        var exited = app.WaitForExit(10000);
        Console.WriteLine($"  exited={exited} exitCode={(exited ? app.ExitCode : -1)}");
        return exited && app.ExitCode == 0;
    }
    finally { Kill(app); }
}

// ---------------------------------------------------------------- app control

static Process Launch(string exe)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
    psi.ArgumentList.Add("--profile");
    psi.ArgumentList.Add(Profile);
    psi.ArgumentList.Add("--offline");

    var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start AnovaDesk");
    Console.WriteLine($"  launched pid {p.Id}");
    return p;
}

static Process LaunchAndPrompt(string exe)
{
    CredWriteToken(CredTarget, FakeToken);
    var app = Launch(exe);

    if (WaitForMainWindow(app) is null) { Kill(app); throw new Exception("main window never appeared"); }
    if (!app.CloseMainWindow()) { Kill(app); throw new Exception("could not post WM_CLOSE"); }

    if (!WaitUntil(() => HasWindowTitled(app.Id, "Close AnovaDesk"), 8000))
    {
        Kill(app);
        throw new Exception("close prompt did not appear");
    }

    Console.WriteLine("  close prompt appeared");
    return app;
}

static AutomationElement? WaitForMainWindow(Process p, int timeoutMs = 20000)
{
    // "Advanced view" lives in the main window header and nowhere else, so it marks readiness.
    AutomationElement? found = null;
    WaitUntil(() => (found = FindByName(p.Id, "Advanced view")) is not null, timeoutMs);
    return found;
}

static bool PanelsHidden(int pid) =>
    FindByName(pid, "DEVICE STATE") is null && FindByName(pid, "TRAFFIC") is null;

static AutomationElement? FindByName(int pid, string name)
{
    var pidCondition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
    var nameCondition = new PropertyCondition(AutomationElement.NameProperty, name);
    var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, pidCondition);

    foreach (AutomationElement window in windows)
    {
        var hit = window.FindFirst(TreeScope.Descendants, nameCondition);
        if (hit is not null) return hit;
    }
    return null;
}

static bool Invoke(AutomationElement element)
{
    if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) return false;
    ((InvokePattern)pattern).Invoke();
    return true;
}

static bool Toggle(AutomationElement element)
{
    if (!element.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern)) return false;
    ((TogglePattern)pattern).Toggle();
    return true;
}

static bool WaitUntil(Func<bool> condition, int timeoutMs = 8000)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        if (condition()) return true;
        Thread.Sleep(150);
    }
    return condition();
}

static void Kill(Process p)
{
    try
    {
        if (!p.HasExited) p.Kill(entireProcessTree: true);
    }
    catch
    {
        // already gone
    }
    finally
    {
        p.Dispose();
    }
}

static string ResolveExe(string[] args)
{
    var explicitPath = GetArg(args, "--exe");
    if (explicitPath is not null)
    {
        var full = Path.GetFullPath(explicitPath);
        if (!File.Exists(full)) throw new FileNotFoundException($"not found: {full}");
        return full;
    }

    foreach (var dir in new[] { "release", @"AnovaDesk\bin" })
    {
        if (!Directory.Exists(dir)) continue;
        var hits = Directory.GetFiles(dir, "AnovaDesk.exe", SearchOption.AllDirectories);
        if (hits.Length > 0) return hits.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    throw new FileNotFoundException("AnovaDesk.exe not found; pass --exe <path>.");
}

static string? GetArg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// ---------------------------------------------------------------- windows

static List<string> VisibleTitles(int pid)
{
    var titles = new List<string>();
    Shell32.EnumWindows((hWnd, _) =>
    {
        Shell32.GetWindowThreadProcessId(hWnd, out var owner);
        if (owner == pid && Shell32.IsWindowVisible(hWnd))
        {
            var sb = new StringBuilder(256);
            Shell32.GetWindowTextW(hWnd, sb, sb.Capacity);
            if (sb.Length > 0) titles.Add(sb.ToString());
        }
        return true;
    }, IntPtr.Zero);

    return titles;
}

static bool HasWindowTitled(int pid, string title) => VisibleTitles(pid).Contains(title);

// ---------------------------------------------------------------- credentials

static void CredWriteToken(string target, string value)
{
    var blob = Encoding.Unicode.GetBytes(value);
    var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
    try
    {
        var cred = new AdvApi.CREDENTIAL
        {
            Type = 1,
            TargetName = target,
            UserName = Environment.UserName,
            CredentialBlobSize = (uint)blob.Length,
            CredentialBlob = handle.AddrOfPinnedObject(),
            Persist = 2,
            Comment = "AnovaDesk UI smoke test",
        };

        if (!AdvApi.CredWrite(ref cred, 0))
            throw new InvalidOperationException("CredWrite failed: " + Marshal.GetLastWin32Error());
    }
    finally
    {
        handle.Free();
    }
}

static void CredDelete(string target) => AdvApi.CredDelete(target, 1, 0);

// ---------------------------------------------------------------- native

internal static class Shell32
{
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);
}

internal static class AdvApi
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CredDelete(string target, uint type, uint flags);
}
