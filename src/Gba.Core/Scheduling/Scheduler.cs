namespace Gba.Core.Scheduling;

public sealed class Scheduler
{
    private readonly PriorityQueue<ScheduledEvent, ScheduledEventKey> _events = new();
    private long _nextSequence;

    public long Now { get; private set; }

    public bool HasPendingEvents => _events.Count > 0;

    public long CyclesUntilNextEvent
        => _events.TryPeek(out var scheduledEvent, out _)
            ? Math.Max(0, scheduledEvent.DueCycle - Now)
            : long.MaxValue;

    public void Reset()
    {
        _events.Clear();
        _nextSequence = 0;
        Now = 0;
    }

    public void Schedule(long cyclesFromNow, Action callback)
    {
        if (cyclesFromNow < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cyclesFromNow), "Events cannot be scheduled in the past.");
        }

        ArgumentNullException.ThrowIfNull(callback);

        var dueCycle = Now + cyclesFromNow;
        var scheduledEvent = new ScheduledEvent(dueCycle, _nextSequence++, callback);
        _events.Enqueue(scheduledEvent, new ScheduledEventKey(dueCycle, scheduledEvent.Sequence));
    }

    public void Advance(long cycles)
    {
        if (cycles < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cycles), "Cannot advance the scheduler backwards.");
        }

        var target = Now + cycles;
        while (_events.TryPeek(out var scheduledEvent, out _) && scheduledEvent.DueCycle <= target)
        {
            _events.Dequeue();
            Now = scheduledEvent.DueCycle;
            scheduledEvent.Callback();
        }

        Now = target;
    }

    private sealed record ScheduledEvent(long DueCycle, long Sequence, Action Callback);

    private readonly record struct ScheduledEventKey(long DueCycle, long Sequence) : IComparable<ScheduledEventKey>
    {
        public int CompareTo(ScheduledEventKey other)
        {
            var cycleComparison = DueCycle.CompareTo(other.DueCycle);
            return cycleComparison != 0 ? cycleComparison : Sequence.CompareTo(other.Sequence);
        }
    }
}
