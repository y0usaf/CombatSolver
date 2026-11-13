using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace CombatSolver;

internal enum SolverDeploymentFastMode
{
    FollowGame,
    Normal,
    Fast,
    Instant,
}

internal enum SolverSpeed
{
    Fast,
    Normal,
    Thorough,
}

internal enum SolverPotionPolicy
{
    Disabled,
    Smart,
    RequireAtLeastOne,
}

internal enum BossHpStrategy
{
    ProgressionFirst,
    MinimizeHpLoss,
}

internal sealed record SolverSettingsData
{
    public bool SolverDisabled { get; init; }
    public bool AutoPlay { get; init; }
    [JsonPropertyName("fairForkOnlineStatisticsEnabled")]
    public bool OnlineStatisticsEnabled { get; init; }
    public bool EnableDetailedDiagnosticLogs { get; init; }
    public bool RetainAllCombatLogs { get; init; }
    public SolverDeploymentFastMode DeploymentFastMode { get; init; } = SolverDeploymentFastMode.FollowGame;
    public double? DeploymentInterActionDelaySeconds { get; init; }
}

internal sealed record SolverSettingsSnapshot(
    bool SolverDisabled,
    bool EnableDetailedDiagnosticLogs,
    SolverPotionPolicy PotionPolicy,
    BossHpStrategy ActTransitionBossHpStrategy,
    BossHpStrategy FinalBossHpStrategy,
    int AcceptableBattleHpLoss,
    int SearchMaxDegreeOfParallelism,
    SolverSearchProfile Profile,
    bool EnableNoGcRegion,
    long NoGcRegionBudgetBytes,
    SolverDeploymentFastMode DeploymentFastMode,
    double DeploymentInterActionDelaySeconds)
{
    public bool StopAtAcceptableBattleHpLoss { get; init; } = true;
    public bool RelicStrategyEnabled { get; init; }
    public RelicCounterRule[] RelicCounterRules { get; init; } = [];
    public GrowthValues GrowthBudgets { get; init; }
    public int? BrightestFlameMaxHpLossLimit { get; init; }
    public bool IgnoreLongTermRewards { get; init; }
    public bool UseBeamWidthPortfolio { get; init; }
    public bool UseNoveltyPortfolio { get; init; }
    public bool UseEarlyTurnExploration { get; init; }
    public bool PredictPotionReward { get; init; }
    public SolverSpeed Speed { get; init; } = SolverSpeed.Thorough;
}

internal static class SolverSettings
{
    public const long NoGcRegionBudgetBytes = 16_000_000_000;
    private const string SettingsUri = "user://combat_solver_settings.json";
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private static SolverSettingsData _current = new();
    private static Func<SolverSettingsSnapshot, SolverSettingsSnapshot>? _snapshotOverrideForTesting;

    public static SolverSettingsData Current
    {
        get
        {
            lock (Sync)
                return _current;
        }
    }

    internal static byte[] CaptureSerializedSettings()
    {
        lock (Sync)
            return JsonSerializer.SerializeToUtf8Bytes(_current, JsonOptions);
    }

    public static void Load()
    {
        string path = ProjectSettings.GlobalizePath(SettingsUri);
        bool persisted = File.Exists(path);
        string? persistedJson = persisted ? File.ReadAllText(path) : null;
        SolverSettingsData loaded = persistedJson != null
            ? JsonSerializer.Deserialize<SolverSettingsData>(persistedJson, JsonOptions)
                ?? throw new InvalidDataException("CombatSolver settings file contained null.")
            : new SolverSettingsData();
        Validate(loaded);
        lock (Sync)
        {
            _current = loaded;
            if (!string.Equals(persistedJson, JsonSerializer.Serialize(loaded, JsonOptions), StringComparison.Ordinal))
                SaveLocked(loaded);
        }
        RuntimeGcProfileSelection runtimeProfile = RuntimeGcProfile.Current;
        SolverSettingsSnapshot effectiveSettings = Capture();
        string runtimeProfileDescription =
            $"[CombatSolver/Test] RUNTIME_GC_PROFILE clr={System.Environment.Version} " +
            $"server_gc={runtimeProfile.IsServerGc.ToString().ToLowerInvariant()} " +
            $"requested={JsonSerializer.Serialize(runtimeProfile.RequestedProfile)} " +
            $"status={runtimeProfile.Status} " +
            $"effective_no_gc_enabled={effectiveSettings.EnableNoGcRegion.ToString().ToLowerInvariant()} " +
            "profile_settings_persisted=false";
        if (runtimeProfile.Status is RuntimeGcProfileStatus.ServerGcUnavailable
            or RuntimeGcProfileStatus.UnknownProfile)
        {
            Entry.Logger.Warn(runtimeProfileDescription);
        }
        else
        {
            Entry.Logger.Info(runtimeProfileDescription);
        }
        Entry.Logger.Info(
            $"[CombatSolver/Test] SETTINGS_LOADED persisted={persisted} " +
            $"auto_play={loaded.AutoPlay.ToString().ToLowerInvariant()} " +
            $"speed={effectiveSettings.Speed} " +
            $"solver_disabled={loaded.SolverDisabled} " +
            $"detailed_diagnostic_logs={loaded.EnableDetailedDiagnosticLogs} " +
            $"retain_all_combat_logs={loaded.RetainAllCombatLogs} " +
            $"max_dop={effectiveSettings.SearchMaxDegreeOfParallelism} " +
            $"search_budget_ms={effectiveSettings.Profile.SoftTimeBudgetMilliseconds} " +
            $"no_gc_enabled={effectiveSettings.EnableNoGcRegion.ToString().ToLowerInvariant()} " +
            $"no_gc_budget_bytes={effectiveSettings.NoGcRegionBudgetBytes} " +
            $"deployment_fast_mode={loaded.DeploymentFastMode} " +
            $"deployment_delay_seconds={loaded.DeploymentInterActionDelaySeconds ?? 0d:0.###}");
    }

    public static SolverSettingsSnapshot Capture()
    {
        SolverSettingsData data = Current;
        SolverSettingsSnapshot snapshot = new(
            data.SolverDisabled,
            data.EnableDetailedDiagnosticLogs,
            SolverPotionPolicy.Smart,
            BossHpStrategy.ProgressionFirst,
            BossHpStrategy.ProgressionFirst,
            0,
            SolverWeights.DefaultSearchMaxDegreeOfParallelism,
            SolverSearchProfile.Default,
            RuntimeGcProfile.Current.EnableNoGcRegion,
            NoGcRegionBudgetBytes,
            data.DeploymentFastMode,
            data.DeploymentInterActionDelaySeconds ?? 0d)
        {
            Speed = SolverSpeed.Thorough,
            RelicStrategyEnabled = true,
            UseBeamWidthPortfolio = true,
        };
        Func<SolverSettingsSnapshot, SolverSettingsSnapshot>? testOverride;
        lock (Sync)
            testOverride = _snapshotOverrideForTesting;
        return testOverride == null ? snapshot : testOverride(snapshot);
    }

    public static void Update(SolverSettingsData data)
    {
        Validate(data);
        lock (Sync)
        {
            _current = data;
            SaveLocked(data);
        }
    }

    internal static void ApplyForTesting(SolverSettingsData data)
    {
        Validate(data);
        lock (Sync)
            _current = data;
    }

    internal static void OverrideSnapshotForTesting(Func<SolverSettingsSnapshot, SolverSettingsSnapshot>? snapshotOverride)
    {
        lock (Sync)
            _snapshotOverrideForTesting = snapshotOverride;
    }

    internal static SolverSettingsData RoundTripForTesting(SolverSettingsData data)
        => JsonSerializer.Deserialize<SolverSettingsData>(
               JsonSerializer.Serialize(data, JsonOptions),
               JsonOptions)
           ?? throw new InvalidDataException("CombatSolver settings round-trip returned null.");

    internal static SolverSettingsData DeserializeForTesting(string json)
        => JsonSerializer.Deserialize<SolverSettingsData>(json, JsonOptions)
           ?? throw new InvalidDataException("CombatSolver settings test JSON returned null.");

    public static void ResetToDefaults() => Update(new SolverSettingsData());

    private static void SaveLocked(SolverSettingsData data)
    {
        string path = ProjectSettings.GlobalizePath(SettingsUri);
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("CombatSolver settings path has no directory.");
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temporary, path, true);
        Entry.Logger.Info("[CombatSolver/Test] SETTINGS_SAVED");
    }

    private static void Validate(SolverSettingsData data)
    {
        if (!Enum.IsDefined(data.DeploymentFastMode))
            throw new InvalidDataException($"Unknown deployment fast mode {data.DeploymentFastMode}.");
        ValidateRange(data.DeploymentInterActionDelaySeconds, 0d, 3d,
            nameof(data.DeploymentInterActionDelaySeconds));
    }

    private static void ValidateRange(double? value, double minimum, double maximum, string name)
    {
        if (value is { } actual && (actual < minimum || actual > maximum || double.IsNaN(actual)))
            throw new InvalidDataException($"{name} must be between {minimum} and {maximum}.");
    }
}
