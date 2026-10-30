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

    /// <summary>Feeds one state sample and returns the events to raise (usually none).</summary>
    public IReadOnlyList<CookEvent> Evaluate(bool cooking, double? waterC, double? targetC, double? remaining)
    {
        var events = new List<CookEvent>(2);

        if (targetC is not null && waterC is not null)
        {
            if (waterC.Value < targetC.Value - ArmBelowTargetBy)
            {
                // Only a cook that genuinely starts below target may later "reach" it,
                // so an idle cooker holding a stale target cannot fire on startup.
                _targetArmed = true;
            }
            else if (_targetArmed && waterC.Value >= targetC.Value - ReachedWithin)
            {
                _targetArmed = false;
                events.Add(CookEvent.TargetReached);
            }
        }

        if (!cooking && _wasCooking)
            events.Add(remaining is > 0 ? CookEvent.CookStopped : CookEvent.CookComplete);

        _wasCooking = cooking;
        return events;
    }
}

