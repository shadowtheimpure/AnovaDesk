namespace AnovaDesk;

public enum CookEvent
{
    TargetReached,
    CookComplete,
    CookStopped,
}

/// <summary>
/// Edge-triggered cook notification policy, deliberately free of UI so its behaviour
/// can be reasoned about and exercised independently of the notification surface.
/// </summary>
public sealed class CookNotificationRules
{
    /// <summary>Water must fall this far below target before "reached" can fire.</summary>
    public const double ArmBelowTargetBy = 1.0;

    /// <summary>How close to target counts as reached.</summary>
    public const double ReachedWithin = 0.5;

    private bool _wasCooking;
    private bool _targetArmed;
    private bool _completeNotified;
    private double? _lastRemaining;
    private bool _stopRequested;
    private DateTime _stopRequestedAt;

    /// <summary>Feeds one state sample and returns the events to raise (usually none).</summary>
    public IReadOnlyList<CookEvent> Evaluate(bool cooking, double? waterC, double? targetC, double? remaining)
    {
        var events = new List<CookEvent>(2);

        // 1. Water reached target.
        //    Arm only when the water has genuinely been below target, so an idle cooker that
        //    merely holds a stale target cannot fire this on startup.
        if (targetC is not null && waterC is not null)
        {
            if (waterC.Value < targetC.Value - ArmBelowTargetBy)
            {
                _targetArmed = true;
            }
            else if (_targetArmed && waterC.Value >= targetC.Value - ReachedWithin)
            {
                _targetArmed = false;
                events.Add(CookEvent.TargetReached);
            }
        }

        // 2. Cook complete.
        //    An Anova cooker does not stop when the timer expires: it switches to MAINTAINING
        //    and keeps holding temperature. Completion must therefore be detected from the
        //    countdown reaching zero while the cook is still running, not from it ending.
        if (cooking && !_completeNotified && remaining is 0 && _lastRemaining is > 0)
        {
            _completeNotified = true;
            events.Add(CookEvent.CookComplete);
        }

        // 3. Cook stopped with time left on the clock.
        if (!cooking && _wasCooking)
        {
            // A stop the user asked for is not a failure. The flag must survive the state
            // frames that arrive while the device is still winding down, so it is cleared
            // here (on the transition) rather than when the request is made.
            var requested = _stopRequested &&
                            (DateTime.UtcNow - _stopRequestedAt).TotalSeconds < 120;

            if (remaining is > 0 && !requested)
                events.Add(CookEvent.CookStopped);

            _stopRequested = false;
        }

        _wasCooking = cooking;
        _lastRemaining = remaining;
        return events;
    }

    /// <summary>
    /// Records that the user asked for the cook to stop, so the resulting transition is not
    /// reported back as an unexpected failure. Expires after two minutes in case the stop
    /// never lands.
    /// </summary>
    public void RequestStop()
    {
        _stopRequested = true;
        _stopRequestedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Clears per-cook state. Called when a new cook starts, so a finished cook cannot
    /// re-report against the next one.
    /// </summary>
    public void Reset()
    {
        _wasCooking = false;
        _targetArmed = false;
        _completeNotified = false;
        _lastRemaining = null;
        _stopRequested = false;
    }
}
