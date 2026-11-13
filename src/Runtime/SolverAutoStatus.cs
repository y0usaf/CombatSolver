using System.Globalization;
using MegaCrit.Sts2.Core.Logging;

namespace CombatSolver;

internal readonly record struct SolverAutoEvent(string Time, string Kind, string Detail);

internal sealed record SolverAutoStatusSnapshot(
    bool Enabled,
    string FailureReason,
    string LastDecision,
    IReadOnlyList<SolverAutoEvent> Events)
{
    public bool Failed => FailureReason.Length > 0;
}

internal enum SolverAutoLogLevel
{
    Info,
    Warning,
    Error,
}

internal static class SolverAutoStatus
{
    public const string Prefix = "[CombatSolver/Auto]";
    private const int MaxEvents = 32;
    private const int MaxDetailLength = 320;
    private static readonly List<SolverAutoEvent> Events = [];
    private static string _lastDecision = "-";
    private static string _failureReason = "";

    public static void ResetCombat()
    {
        Events.Clear();
        _lastDecision = "-";
        _failureReason = "";
    }

    public static void Record(string kind, string detail)
        => Append(kind, Shorten(detail), SolverAutoLogLevel.Info);

    public static void RecordFailure(string kind, string detail)
    {
        string text = Shorten(detail);
        _failureReason = text;
        Append(kind, text, SolverAutoLogLevel.Warning);
    }

    public static void RecordError(string kind, string detail)
    {
        string text = Shorten(detail);
        _failureReason = text;
        Append(kind, text, SolverAutoLogLevel.Error);
    }

    public static void ClearFailure(string detail)
    {
        if (_failureReason.Length == 0)
            return;
        _failureReason = "";
        Record("recovered", detail);
    }

    public static void RecordDecision(
        int turn,
        TimeSpan elapsed,
        int expandedNodes,
        int projectedHpLost,
        int potionCount,
        bool reused)
    {
        string detail =
            $"turn={turn} elapsed_ms={elapsed.TotalMilliseconds:F0} expanded={expandedNodes} " +
            $"projected_hp_lost={projectedHpLost} potions={potionCount} reused={reused.ToString().ToLowerInvariant()}";
        _lastDecision = detail;
        ClearFailure("deployment_started");
        Record("decision", detail);
    }

    public static SolverAutoStatusSnapshot Capture(bool enabled)
        => new(enabled, _failureReason, _lastDecision, Events.ToArray());

    public static string Describe(Exception exception)
    {
        string message = exception.Message.Replace('\n', ' ').Replace('\r', ' ');
        return $"{exception.GetType().Name}: {message}";
    }

    private static void Append(string kind, string detail, SolverAutoLogLevel level)
    {
        string time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Events.Add(new SolverAutoEvent(time, kind, detail));
        if (Events.Count > MaxEvents)
            Events.RemoveRange(0, Events.Count - MaxEvents);
        string line = $"{Prefix} {kind} {detail}";
        Entry.Logger.Info(line);
        switch (level)
        {
            case SolverAutoLogLevel.Error:
                Log.Error(line);
                break;
            case SolverAutoLogLevel.Warning:
                Log.Warn(line);
                break;
            default:
                Log.Info(line);
                break;
        }
    }

    private static string Shorten(string detail)
        => detail.Length <= MaxDetailLength ? detail : detail[..MaxDetailLength] + "…";
}
