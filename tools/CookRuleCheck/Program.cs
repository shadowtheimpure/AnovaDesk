using AnovaDesk;

// Exercises CookNotificationRules against the sequences the app actually sees,
// including the "idle cooker holding a stale target" case that must stay silent.

var failures = 0;

void Check(string name, IReadOnlyList<CookEvent> actual, params CookEvent[] expected)
{
    var ok = actual.Count == expected.Length && actual.SequenceEqual(expected);
    Console.WriteLine(ok
        ? $"PASS  {name}"
        : $"FAIL  {name}   got=[{string.Join(",", actual)}] expected=[{string.Join(",", expected)}]");
    if (!ok) failures++;
}

// --- idle cooker that merely holds a target: must never fire -----------------
var r = new CookNotificationRules();
Check("idle below target, frame 1", r.Evaluate(false, 22.0, 60, 3600));
Check("idle below target, frame 2", r.Evaluate(false, 25.0, 60, 3600));
Check("idle drifting up, frame 3", r.Evaluate(false, 55.0, 60, 3600));

// --- a real cook: target reached exactly once -------------------------------
r = new CookNotificationRules();
Check("cook starts (arming frame)", r.Evaluate(true, 22.0, 60, 3600));
Check("heating", r.Evaluate(true, 45.0, 60, 3400));
Check("crosses target", r.Evaluate(true, 59.6, 60, 3000), CookEvent.TargetReached);
Check("held at target (no repeat)", r.Evaluate(true, 60.1, 60, 2900));
Check("held at target (no repeat 2)", r.Evaluate(true, 60.0, 60, 2800));
Check("timer running down", r.Evaluate(true, 60.0, 60, 30));
Check("timer hits zero -> complete", r.Evaluate(true, 60.0, 60, 0), CookEvent.CookComplete);
Check("device keeps maintaining (no repeat)", r.Evaluate(true, 60.0, 60, 0));
Check("still maintaining (no repeat 2)", r.Evaluate(true, 60.0, 60, 0));

// --- a cook stopped early ---------------------------------------------------
r = new CookNotificationRules();
r.Evaluate(true, 22.0, 60, 3600);
Check("water never reached target", r.Evaluate(true, 40.0, 60, 3000));
Check("stopped early -> error event", r.Evaluate(false, 40.0, 60, 1200), CookEvent.CookStopped);

// --- a requested stop must not be reported back as a failure ----------------
r = new CookNotificationRules();
r.Evaluate(true, 22.0, 60, 600);
r.RequestStop();
Check("deliberate stop -> no event", r.Evaluate(false, 22.0, 60, 600));
Check("stays idle after stop", r.Evaluate(false, 22.0, 60, 600));
Check("stop flag consumed: new cook arms", r.Evaluate(true, 22.0, 60, 300));
Check("genuine stop still reports", r.Evaluate(false, 22.0, 60, 200), CookEvent.CookStopped);

// --- finishing then stopping must not re-report ------------------------------
r = new CookNotificationRules();
r.Evaluate(true, 22.0, 60, 120);
Check("completes (target then completion in one frame)",
    r.Evaluate(true, 60.0, 60, 0), CookEvent.TargetReached, CookEvent.CookComplete);
r.Reset();
Check("stop after completion -> no event", r.Evaluate(false, 60.0, 60, 120));

// --- device that never enters cook mode: no lifecycle event here -------------
// (the app raises "cook failed to start" from its own start-request watchdog)
r = new CookNotificationRules();
Check("never cooked, frame 1", r.Evaluate(false, 22.0, 60, 3600));
Check("never cooked, frame 2", r.Evaluate(false, 22.0, 60, 3600));

// --- water already at/above target when a cook begins ------------------------
r = new CookNotificationRules();
Check("already above target", r.Evaluate(true, 22.0, 20, 600));

// --- target changed upward mid-cook: must re-arm and fire again --------------
r = new CookNotificationRules();
Check("cook arming", r.Evaluate(true, 22.0, 50, 3600));
Check("reaches 50", r.Evaluate(true, 49.8, 50, 3000), CookEvent.TargetReached);
Check("new higher target, water now below it", r.Evaluate(true, 50.0, 60, 3000));
Check("reaches 60", r.Evaluate(true, 59.7, 60, 1500), CookEvent.TargetReached);

// --- preset captions must follow the selected unit --------------------------
Console.WriteLine();
CheckText("preset C steak", MainWindow.FormatPreset(54.5, "steak", false), "54.5\u00B0 steak");
CheckText("preset C chicken", MainWindow.FormatPreset(65, "chicken", false), "65\u00B0 chicken");
CheckText("preset F steak", MainWindow.FormatPreset(54.5, "steak", true), "130.1\u00B0 steak");
CheckText("preset F chicken", MainWindow.FormatPreset(65, "chicken", true), "149\u00B0 chicken");
CheckText("preset F eggs", MainWindow.FormatPreset(63.5, "eggs", true), "146.3\u00B0 eggs");
CheckText("preset F salmon", MainWindow.FormatPreset(52, "salmon", true), "125.6\u00B0 salmon");
CheckText("unit symbol C", MainWindow.UnitSymbol(false), "\u00B0C");
CheckText("unit symbol F", MainWindow.UnitSymbol(true), "\u00B0F");

Console.WriteLine(failures == 0 ? "\nALL PASS" : $"\n{failures} FAILED");
return failures == 0 ? 0 : 1;

void CheckText(string name, string actual, string expected)
{
    var ok = actual == expected;
    Console.WriteLine(ok ? $"PASS  {name}" : $"FAIL  {name}   got=\"{actual}\" expected=\"{expected}\"");
    if (!ok) failures++;
}
