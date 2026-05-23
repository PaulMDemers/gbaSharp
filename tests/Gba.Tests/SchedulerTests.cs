using Gba.Core.Scheduling;

namespace Gba.Tests;

public sealed class SchedulerTests
{
    [Fact]
    public void AdvanceRunsEventsInCycleAndInsertionOrder()
    {
        var scheduler = new Scheduler();
        var events = new List<string>();

        Assert.Equal(long.MaxValue, scheduler.CyclesUntilNextEvent);

        scheduler.Schedule(5, () => events.Add("a"));
        scheduler.Schedule(2, () => events.Add("b"));
        scheduler.Schedule(5, () => events.Add("c"));

        Assert.Equal(2, scheduler.CyclesUntilNextEvent);

        scheduler.Advance(5);

        Assert.Equal(["b", "a", "c"], events);
        Assert.Equal(5, scheduler.Now);
        Assert.Equal(long.MaxValue, scheduler.CyclesUntilNextEvent);
    }

    [Fact]
    public void EventCanScheduleAnotherEventRelativeToCurrentCycle()
    {
        var scheduler = new Scheduler();
        var events = new List<long>();

        scheduler.Schedule(3, () =>
        {
            events.Add(scheduler.Now);
            scheduler.Schedule(2, () => events.Add(scheduler.Now));
        });

        scheduler.Advance(10);

        Assert.Equal([3L, 5L], events);
        Assert.Equal(10, scheduler.Now);
    }
}
