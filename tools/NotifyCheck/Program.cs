using AnovaDesk;

// Posts a real toast and checks whether the shell actually queued it.
// Action Center history is the only programmatic proof of delivery available to an
// unpackaged app, so the before/after counts are the thing to watch.

Console.WriteLine("exe        : " + (Environment.ProcessPath ?? "(unknown)"));

ToastChannel.ClearHistory();
ToastChannel.Initialize();

Console.WriteLine("init       : " + ToastChannel.Status);
Console.WriteLine("history    : " + ToastChannel.HistoryCount());

var posted = ToastChannel.Show("AnovaDesk probe", "If this is visible, toasts are delivered.");
Console.WriteLine("show       : " + posted);
Console.WriteLine("status     : " + ToastChannel.Status);

await Task.Delay(2000);

var after = ToastChannel.HistoryCount();
Console.WriteLine("history+2s : " + after);
Console.WriteLine(after > 0
    ? "RESULT: toast reached Action Center"
    : "RESULT: nothing in Action Center - toast was not delivered");

return 0;
