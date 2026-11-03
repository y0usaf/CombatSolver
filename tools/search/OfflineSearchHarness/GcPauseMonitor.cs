using System.Diagnostics.Tracing;

namespace OfflineSearchHarness;

internal sealed class GcPauseMonitor : EventListener
{
    private readonly object _gate = new();
    private DateTime? _suspendStarted;
    private double _windowMaxMilliseconds;
    private int _windowPauses;
    private int _lastDepth = -1;
    private readonly double[] _maxPauseByDepth = new double[3];
    private readonly int[] _pausesByDepth = new int[3];
    private readonly double[] _totalPauseByDepth = new double[3];
    private readonly Dictionary<string, long> _allocatedByType = new(StringComparer.Ordinal);
    private static readonly bool ProfileAllocations =
        Environment.GetEnvironmentVariable("OFFLINE_HARNESS_ALLOC_PROFILE") == "1";

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(eventSource, ProfileAllocations ? EventLevel.Verbose : EventLevel.Informational, (EventKeywords)0x1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventId == 10 && eventData.Payload != null && eventData.PayloadNames != null)
        {
            int typeIndex = eventData.PayloadNames.IndexOf("TypeName");
            int amountIndex = eventData.PayloadNames.IndexOf("AllocationAmount64");
            if (typeIndex < 0 || amountIndex < 0)
                return;
            string type = eventData.Payload[typeIndex] as string ?? "?";
            long amount = Convert.ToInt64(eventData.Payload[amountIndex]);
            lock (_gate)
                _allocatedByType[type] = _allocatedByType.GetValueOrDefault(type) + amount;
        }
        else if (eventData.EventId == 1 && eventData.Payload != null && eventData.PayloadNames != null)
        {
            int depthIndex = eventData.PayloadNames.IndexOf("Depth");
            if (depthIndex >= 0)
            {
                lock (_gate)
                    _lastDepth = Convert.ToInt32(eventData.Payload[depthIndex]);
            }
        }
        else if (eventData.EventId == 9)
        {
            lock (_gate)
                _suspendStarted = eventData.TimeStamp;
        }
        else if (eventData.EventId == 3)
        {
            lock (_gate)
            {
                if (_suspendStarted is not { } started)
                    return;
                double pause = (eventData.TimeStamp - started).TotalMilliseconds;
                _suspendStarted = null;
                _windowPauses++;
                _windowMaxMilliseconds = Math.Max(_windowMaxMilliseconds, pause);
                if (_lastDepth is >= 0 and <= 2)
                {
                    _pausesByDepth[_lastDepth]++;
                    _totalPauseByDepth[_lastDepth] += pause;
                    _maxPauseByDepth[_lastDepth] = Math.Max(_maxPauseByDepth[_lastDepth], pause);
                }
            }
        }
    }

    public object PausesByGeneration()
    {
        lock (_gate)
            return Enumerable.Range(0, 3).Select(depth => new
            {
                generation = depth,
                pauses = _pausesByDepth[depth],
                totalMs = Math.Round(_totalPauseByDepth[depth], 1),
                maxMs = Math.Round(_maxPauseByDepth[depth], 1),
            }).ToArray();
    }

    public IReadOnlyList<KeyValuePair<string, long>> TopAllocations(int count)
    {
        lock (_gate)
            return [.. _allocatedByType.OrderByDescending(item => item.Value).Take(count)];
    }

    public (double MaxMilliseconds, int Pauses) TakeWindow()
    {
        lock (_gate)
        {
            (double, int) window = (_windowMaxMilliseconds, _windowPauses);
            _windowMaxMilliseconds = 0;
            _windowPauses = 0;
            return window;
        }
    }
}
