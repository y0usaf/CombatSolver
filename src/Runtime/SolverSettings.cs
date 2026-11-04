using System.Globalization;
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

internal enum SolverSearchCompletionNotificationMode
{
    OnlyWhenGameInBackground,
    Always,
}

internal enum SolverOverlayTheme
{
    Dark,
    Light,
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
    public bool AutomaticCalculationEnabled { get; init; } = true;
    public bool AutoEnableFullAuto { get; init; }
    public SolverSpeed Speed { get; init; } = SolverSpeed.Normal;
    [JsonPropertyName("fairForkOnlineStatisticsEnabled")]
    public bool OnlineStatisticsEnabled { get; init; }
    public bool StopFullAutoOnCombatEnd { get; init; }
    public bool StopFullAutoOnDeathTurn { get; init; } = true;
    public bool StopFullAutoOnWorseRecalculation { get; init; } = true;
    public bool EnableDetailedDiagnosticLogs { get; init; }
    public bool RetainAllCombatLogs { get; init; }
    public bool ShowBattleDamagePerformanceHint { get; init; } = true;
    public bool ShowSpeedXWarning { get; init; } = true;
    public bool SearchCompletionNotificationsEnabled { get; init; } = true;
    public SolverSearchCompletionNotificationMode SearchCompletionNotificationMode { get; init; }
        = SolverSearchCompletionNotificationMode.OnlyWhenGameInBackground;
    public SolverDeploymentFastMode DeploymentFastMode { get; init; } = SolverDeploymentFastMode.FollowGame;
    public double? DeploymentInterActionDelaySeconds { get; init; }
    public float? OverlayPositionX { get; init; }
    public float? OverlayPositionY { get; init; }
    public float? OverlayWidth { get; init; } = 1200f;
    public float? OverlayHeight { get; init; } = 700f;
    public string? ReporterContactQq { get; init; }
    public SolverOverlayTheme OverlayTheme { get; init; } = SolverOverlayTheme.Dark;
    public float OverlayOpacity { get; init; } = 0.65f;
}

internal sealed record SolverSettingsSnapshot(
    bool SolverDisabled,
    bool StopFullAutoOnCombatEnd,
    bool StopFullAutoOnDeathTurn,
    bool StopFullAutoOnWorseRecalculation,
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
    public SolverSpeed Speed { get; init; } = SolverSpeed.Normal;
}

internal static class SolverSettings
{
    public const long NoGcRegionBudgetBytes = 16_000_000_000;
    public const float MinimumOverlayWidth = 400f;
    public const float MinimumOverlayHeight = 300f;
    public const float MaximumOverlaySize = 100_000f;
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
            Entry.Logger.Warn(runtimeProfileDescription);
        else
            Entry.Logger.Info(runtimeProfileDescription);
        Entry.Logger.Info(
            $"[CombatSolver/Test] SETTINGS_LOADED persisted={persisted} " +
            $"automatic_calculation={loaded.AutomaticCalculationEnabled.ToString().ToLowerInvariant()} " +
            $"speed={loaded.Speed} " +
            $"solver_disabled={loaded.SolverDisabled} " +
            $"stop_on_combat_end={loaded.StopFullAutoOnCombatEnd} " +
            $"stop_on_death_turn={loaded.StopFullAutoOnDeathTurn} " +
            $"stop_on_worse_recalculation={loaded.StopFullAutoOnWorseRecalculation} " +
            $"detailed_diagnostic_logs={loaded.EnableDetailedDiagnosticLogs} " +
            $"retain_all_combat_logs={loaded.RetainAllCombatLogs} " +
            $"show_battle_damage_performance_hint={loaded.ShowBattleDamagePerformanceHint} " +
            $"show_speedx_warning={loaded.ShowSpeedXWarning} " +
            $"search_notifications_enabled={loaded.SearchCompletionNotificationsEnabled} " +
            $"search_notification_mode={loaded.SearchCompletionNotificationMode} " +
            $"max_dop={effectiveSettings.SearchMaxDegreeOfParallelism} " +
            $"search_budget_ms={effectiveSettings.Profile.SoftTimeBudgetMilliseconds} " +
            $"no_gc_enabled={effectiveSettings.EnableNoGcRegion.ToString().ToLowerInvariant()} " +
            $"no_gc_budget_bytes={effectiveSettings.NoGcRegionBudgetBytes} " +
            $"deployment_fast_mode={loaded.DeploymentFastMode} " +
            $"deployment_delay_seconds={loaded.DeploymentInterActionDelaySeconds ?? 0d:0.###} " +
            $"overlay_theme={loaded.OverlayTheme} " +
            $"overlay_opacity={loaded.OverlayOpacity:0.##}");
    }

    public static SolverSettingsSnapshot Capture()
    {
        SolverSettingsData data = Current;
        SolverSettingsSnapshot snapshot = new(
            data.SolverDisabled,
            data.StopFullAutoOnCombatEnd,
            data.StopFullAutoOnDeathTurn,
            data.StopFullAutoOnWorseRecalculation,
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
            Speed = data.Speed,
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

    public static Vector2? OverlayPosition
    {
        get
        {
            SolverSettingsData data = Current;
            return data.OverlayPositionX is { } x && data.OverlayPositionY is { } y
                ? new Vector2(x, y)
                : null;
        }
    }

    public static void SetOverlayPosition(Vector2 position)
        => Update(Current with
        {
            OverlayPositionX = position.X,
            OverlayPositionY = position.Y,
        });

    public static Vector2? OverlaySize
    {
        get
        {
            SolverSettingsData data = Current;
            return data.OverlayWidth is { } width && data.OverlayHeight is { } height
                ? new Vector2(width, height)
                : null;
        }
    }

    public static void SetOverlayBounds(Vector2 position, Vector2 size)
        => Update(Current with
        {
            OverlayPositionX = position.X,
            OverlayPositionY = position.Y,
            OverlayWidth = size.X,
            OverlayHeight = size.Y,
        });

    public static string FormatSeconds(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

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
        if (!Enum.IsDefined(data.Speed))
            throw new InvalidDataException($"Unknown solver speed {data.Speed}.");
        if (!Enum.IsDefined(data.DeploymentFastMode))
            throw new InvalidDataException($"Unknown deployment fast mode {data.DeploymentFastMode}.");
        if (!Enum.IsDefined(data.SearchCompletionNotificationMode))
        {
            throw new InvalidDataException(
                $"Unknown search completion notification mode {data.SearchCompletionNotificationMode}.");
        }
        ValidateRange(data.DeploymentInterActionDelaySeconds, 0d, 3d,
            nameof(data.DeploymentInterActionDelaySeconds));
        if (data.OverlayPositionX.HasValue != data.OverlayPositionY.HasValue)
            throw new InvalidDataException("OverlayPositionX and OverlayPositionY must both be set or both be null.");
        ValidateRange(data.OverlayPositionX, -100_000f, 100_000f, nameof(data.OverlayPositionX));
        ValidateRange(data.OverlayPositionY, -100_000f, 100_000f, nameof(data.OverlayPositionY));
        if (data.OverlayWidth.HasValue != data.OverlayHeight.HasValue)
            throw new InvalidDataException("OverlayWidth and OverlayHeight must both be set or both be null.");
        ValidateRange(
            data.OverlayWidth,
            MinimumOverlayWidth,
            MaximumOverlaySize,
            nameof(data.OverlayWidth));
        ValidateRange(
            data.OverlayHeight,
            MinimumOverlayHeight,
            MaximumOverlaySize,
            nameof(data.OverlayHeight));
        if (!Enum.IsDefined(data.OverlayTheme))
            throw new InvalidDataException($"Unknown overlay theme {data.OverlayTheme}.");
        ValidateRange(data.OverlayOpacity, 0.25f, 1f, nameof(data.OverlayOpacity));
        if (data.ReporterContactQq is { Length: > 64 })
            throw new InvalidDataException($"{nameof(data.ReporterContactQq)} must be at most 64 characters.");
    }

    private static void ValidateRange(double? value, double minimum, double maximum, string name)
    {
        if (value is { } actual && (actual < minimum || actual > maximum || double.IsNaN(actual)))
            throw new InvalidDataException($"{name} must be between {minimum} and {maximum}.");
    }

    private static void ValidateRange(float? value, float minimum, float maximum, string name)
    {
        if (value is { } actual && (actual < minimum || actual > maximum || float.IsNaN(actual)))
            throw new InvalidDataException($"{name} must be between {minimum} and {maximum}.");
    }
}
