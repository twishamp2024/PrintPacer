namespace PrintPacer.Core.Engine;

/// <summary>
/// Time, split into the two kinds this application needs.
///
/// <see cref="UtcNow"/> is wall-clock time. It is what "rest the printer for 60 minutes"
/// really means, and it keeps running while the PC is asleep, which is exactly right:
/// an hour of hibernation is still an hour of the printer cooling down.
///
/// <see cref="Monotonic"/> never jumps. It is used only as a backstop, so that a
/// backwards clock correction cannot strand a job waiting forever.
/// </summary>
public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Time since an arbitrary fixed origin, unaffected by clock changes. Based on
    /// GetTickCount64, which by design excludes time the machine spent suspended.
    /// </summary>
    TimeSpan Monotonic { get; }
}

public sealed class SystemClock : ISystemClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeSpan Monotonic => TimeSpan.FromMilliseconds(Environment.TickCount64);
}

/// <summary>
/// A deadline that survives sleep, hibernation, restarts and clock changes.
///
/// The deadline is stored as an absolute UTC instant, so it is meaningful across a
/// process restart. Two independent conditions can make it due:
///
///   - wall-clock time reaches the deadline (the normal case, and the one that correctly
///     credits time spent asleep against the delay);
///   - the monotonic clock has run for the full planned duration (a backstop, so that a
///     clock that jumps backwards cannot wedge a job indefinitely).
///
/// A detected backwards jump also rebases the deadline, so the remaining time shown to the
/// user stays honest instead of growing.
/// </summary>
public sealed class Deadline
{
    private readonly ISystemClock _clock;
    private readonly TimeSpan _plannedDuration;
    private readonly TimeSpan _monotonicStart;
    private DateTimeOffset _dueUtc;
    private DateTimeOffset _lastObservedUtc;

    /// <summary>Tolerance before a backwards clock movement is treated as a real clock change.</summary>
    private static readonly TimeSpan BackwardsJumpTolerance = TimeSpan.FromSeconds(5);

    public Deadline(ISystemClock clock, DateTimeOffset dueUtc, TimeSpan plannedDuration)
    {
        _clock = clock;
        _dueUtc = dueUtc;
        _plannedDuration = plannedDuration < TimeSpan.Zero ? TimeSpan.Zero : plannedDuration;
        _monotonicStart = clock.Monotonic;
        _lastObservedUtc = clock.UtcNow;
    }

    public DateTimeOffset DueUtc => _dueUtc;

    /// <summary>True when the clock moved backwards since this deadline was created.</summary>
    public bool ClockWentBackwards { get; private set; }

    public TimeSpan Remaining
    {
        get
        {
            Observe();
            var byWallClock = _dueUtc - _clock.UtcNow;
            var byMonotonic = _plannedDuration - (_clock.Monotonic - _monotonicStart);
            var remaining = byWallClock < byMonotonic ? byWallClock : byMonotonic;
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
    }

    public bool IsDue => Remaining <= TimeSpan.Zero;

    private void Observe()
    {
        var now = _clock.UtcNow;

        if (now < _lastObservedUtc - BackwardsJumpTolerance)
        {
            // The system clock was moved back. Re-anchor the deadline to the time that is
            // genuinely still outstanding, measured monotonically.
            var elapsed = _clock.Monotonic - _monotonicStart;
            var outstanding = _plannedDuration - elapsed;
            if (outstanding < TimeSpan.Zero) outstanding = TimeSpan.Zero;

            _dueUtc = now + outstanding;
            ClockWentBackwards = true;
        }

        _lastObservedUtc = now;
    }

    public static string Describe(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "now";
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours} h {remaining.Minutes:00} min {remaining.Seconds:00} s";
        if (remaining.TotalMinutes >= 1)
            return $"{remaining.Minutes} min {remaining.Seconds:00} s";
        return $"{remaining.Seconds} s";
    }
}
