using System.Diagnostics;
using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace OfflineSearchHarness;

internal static class FairPlay
{
    private sealed class BenchmarkSelector : ICardSelector
    {
        private readonly Queue<PlanCardChoice> _planned = new();

        public void Plan(IEnumerable<PlanCardChoice> choices)
        {
            _planned.Clear();
            foreach (PlanCardChoice choice in choices)
                _planned.Enqueue(choice);
        }

        public int Fallbacks { get; private set; }

        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            List<CardModel> available = [.. options];
            if (_planned.TryDequeue(out PlanCardChoice? choice))
            {
                List<CardModel> selected = [];
                foreach (PlanCardToken token in choice.Cards)
                {
                    CardModel? card = available.Where(item => CardChoiceSupport.MatchesToken(item, token) && !selected.Contains(item))
                        .FirstOrDefault();
                    if (card != null)
                        selected.Add(card);
                }
                if (selected.Count >= minSelect && selected.Count <= maxSelect)
                    return Task.FromResult<IEnumerable<CardModel>>(selected);
            }
            Fallbacks++;
            int count = Math.Min(available.Count, Math.Max(minSelect, Math.Min(1, maxSelect)));
            return Task.FromResult<IEnumerable<CardModel>>(available.Take(count).ToList());
        }

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
        {
            Fallbacks++;
            return options.Count == 0 ? default : new CardRewardSelection { card = options[0].Card };
        }
    }

    private sealed record DecisionRecord(int Turn, string Mode, double Milliseconds, int Solves, int Candidates,
        string[] Committed, double? MeanUtility, double AllocatedMb, long OwnPhaseMs, long SlowestSolveMs,
        bool Forced, double CpuMs, int Gen0, int Gen1, int Gen2, string[]? Scores, int SafetyStops)
    {
        public double GcPauseMs { get; set; }
        public double MaxGcPauseMs { get; set; }
        public int GcPauses { get; set; }
    }

    private static readonly GcPauseMonitor PauseMonitor = new();

    private static readonly long StartedAt = Environment.TickCount64;

    private static bool TicksMsecPrefix(ref ulong __result)
    {
        __result = (ulong)(Environment.TickCount64 - StartedAt);
        return false;
    }

    private static bool TicksUsecPrefix(ref ulong __result)
    {
        __result = (ulong)(Environment.TickCount64 - StartedAt) * 1000UL;
        return false;
    }

    private static void InstallPlayBypasses()
    {
        HarmonyLib.Harmony harmony = GameBootstrap.Harmony;
        harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Godot.Time), nameof(Godot.Time.GetTicksMsec)),
            prefix: new HarmonyLib.HarmonyMethod(typeof(FairPlay), nameof(TicksMsecPrefix)));
        harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Godot.Time), nameof(Godot.Time.GetTicksUsec)),
            prefix: new HarmonyLib.HarmonyMethod(typeof(FairPlay), nameof(TicksUsecPrefix)));
    }

    internal static void Run(CombatState combat, HarnessOptions options, MainLoopContext loop, string mode)
    {
        if (mode is not ("v2" or "clairvoyant"))
            throw new ArgumentException($"OFFLINE_HARNESS_FAIR_PLAY must be v2 or clairvoyant, not {mode}.");
        InstallPlayBypasses();
        Player player = combat.Players.Single();
        FairPlannerSettings settings = new(
            int.Parse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_K") ?? "12"),
            int.Parse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_CANDIDATES") ?? "4"),
            double.Parse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_DEATH_PENALTY") ?? "200"),
            double.Parse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_ENEMY_WEIGHT") ?? "0.25"),
            double.Parse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_COMMIT_AGREEMENT") ?? "1"),
            double.Parse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_TIE_SE") ?? "1"),
            double.Parse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_TIE_MARGIN") ?? "1"),
            int.Parse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_DOP") ?? "0"));
        bool determinismCheck = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_DETERMINISM") == "1";
        int determinismMismatches = 0;
        SolverSettingsSnapshot solverSettings = SolverSettings.Capture();
        SolverSearchProfile profile = solverSettings.Profile with
        {
            SoftTimeBudgetMilliseconds = options.BudgetMilliseconds,
        };
        BenchmarkSelector selector = new();
        using IDisposable selectorScope = CardSelectCmd.PushSelector(selector, localOnly: true);

        PrepareState(combat, player, loop);
        int stopTurn = int.Parse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_STOP_TURN") ?? "41");
        int startHp = player.Creature.CurrentHp;
        List<DecisionRecord> decisions = [];
        Stopwatch total = Stopwatch.StartNew();
        PauseMonitor.TakeWindow();
        TimeSpan windowPause = GC.GetTotalPauseDuration();
        void CloseWindow()
        {
            if (decisions.Count == 0)
                return;
            TimeSpan pause = GC.GetTotalPauseDuration();
            (double maxPause, int pauses) = PauseMonitor.TakeWindow();
            DecisionRecord last = decisions[^1];
            last.GcPauseMs = (pause - windowPause).TotalMilliseconds;
            last.MaxGcPauseMs = maxPause;
            last.GcPauses = pauses;
            windowPause = pause;
        }
        string outcome = "timeout";
        int stalls = 0;
        while (true)
        {
            string? ended = WaitForDecisionPoint(combat, player, loop);
            CloseWindow();
            if (ended != null)
            {
                outcome = ended;
                break;
            }
            int turn = player.PlayerCombatState!.TurnNumber;
            if (turn > 40 || turn >= stopTurn)
                break;
            SolverDisplayNames names = SolverDisplayNames.Capture(combat);
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
            SearchPolicySnapshot policy = FairDecisionPlanner.IsolatedPolicy(
                SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, includeTurnSetup: false,
                    theftPolicy: SolverController.ResolveTheftPolicy(combat)),
                profile);
            long allocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            TimeSpan cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;
            int[] collectionsAtStart = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            Stopwatch watch = Stopwatch.StartNew();
            long ownPhaseMs = 0, slowestSolveMs = 0;
            int safetyStops = 0;
            IReadOnlyList<PlanAction> route;
            bool forcedDecision = false;
            int solves = 1;
            int candidates = 1;
            double? meanUtility = null;
            string[]? scores = null;
            if (mode == "v2")
            {
                ulong decisionSeed = FairDecisionPlanner.VisibleSeed(combat, player);
                bool forced = FairDecisionPlanner.IsForcedEndTurn(player);
                CombatRootSnapshot[] roots = [.. Enumerable.Range(0, forced ? 1 : settings.Samples).Select(index =>
                    CombatRootSnapshot.Capture(combat, fair: FairObservation.FromSeed(decisionSeed + (ulong)index)))];
                SearchPolicySnapshot decisionPolicy = forced ? FairDecisionPlanner.ForcedEndTurnPolicy(policy) : policy;
                FairDecision? RunDecide(CombatRootSnapshot[] decisionRoots, FairPlannerSettings decisionSettings)
                {
                    Task<FairDecision?> decide = Task.Run(() =>
                    {
                        using ISearchGcScope gcScope = FairDecisionPlanner.EnterGcScope(
                            solverSettings.NoGcRegionBudgetBytes, CancellationToken.None);
                        return FairDecisionPlanner.Decide(
                            decisionRoots,
                            (root, prefix) => FairDecisionPlanner.Solve(root, names, damage, decisionPolicy, prefix, CancellationToken.None),
                            (root, action) => FairDecisionPlanner.RevealedVariants(root, names, damage, decisionPolicy, action, CancellationToken.None),
                            (root, actions) => FairDecisionPlanner.RevealFreeLength(root, names, damage, decisionPolicy, actions, CancellationToken.None),
                            decisionSettings,
                            CancellationToken.None);
                    });
                    loop.RunUntilCompleted(decide, TimeSpan.FromMinutes(10), "fair v2 decision");
                    return decide.GetAwaiter().GetResult();
                }
                string Signature(FairDecision? made) => made == null ? "-" : FairDecisionPlanner.Key(made.Action) + "#" + made.CommitCount + " "
                    + string.Join(';', made.Candidates.Select(score => $"{score.Key}:{score.MeanUtility:R}:{score.DeathRate:R}:{score.ValidSamples}"));
                FairDecision? decision = RunDecide(roots, settings);
                safetyStops = decision?.SafetyStops ?? 0;
                if (determinismCheck)
                {
                    ulong repeatSeed = FairDecisionPlanner.VisibleSeed(combat, player);
                    CombatRootSnapshot[] repeatRoots = [.. Enumerable.Range(0, roots.Length).Select(index =>
                        CombatRootSnapshot.Capture(combat, fair: FairObservation.FromSeed(repeatSeed + (ulong)index)))];
                    FairDecision? repeat = RunDecide(repeatRoots, settings with { MaxParallelism = 3 });
                    if (Signature(repeat) != Signature(decision))
                    {
                        determinismMismatches++;
                        Console.WriteLine($"DETERMINISM_MISMATCH turn={turn} first={Signature(decision)} repeat={Signature(repeat)}");
                    }
                }
                route = decision == null ? [] : [decision.Action, .. decision.Route.BestNode.Actions.Skip(1).Take(decision.CommitCount - 1)];
                if (forced && decision?.Action.Kind != PlanActionKind.EndTurn)
                    throw new InvalidOperationException($"forced end turn solved to {decision?.Action.Kind}");
                solves = decision?.Solves ?? settings.Samples;
                candidates = decision?.Candidates.Count ?? 0;
                meanUtility = decision?.Candidates.Max(score => score.MeanUtility);
                scores = decision?.Candidates.Select(score =>
                    $"{score.Action.Kind}:{score.Action.CardId}{score.Action.PotionId}>{score.Action.TargetCombatId} mean={score.MeanUtility:F2} "
                    + $"death={score.DeathRate:F2} sd={score.StandardDeviation:F1} proposals={score.Proposals} valid={score.ValidSamples}").ToArray();
                ownPhaseMs = decision?.OwnPhaseMilliseconds ?? 0;
                slowestSolveMs = decision?.SlowestSolveMilliseconds ?? 0;
                forcedDecision = forced;
            }
            else
            {
                CombatRootSnapshot root = CombatRootSnapshot.Capture(combat, fair: FairObservation.ClairvoyantForOfflineBenchmark(combat));
                Task<SolverResult> solve = Task.Run(() =>
                    FairDecisionPlanner.Solve(root, names, damage, policy, null, CancellationToken.None));
                loop.RunUntilCompleted(solve, TimeSpan.FromMinutes(10), "fair single solve");
                route = solve.GetAwaiter().GetResult().BestNode.Actions;
            }
            watch.Stop();
            double allocatedMb = (GC.GetTotalAllocatedBytes(precise: false) - allocatedAtStart) / (1024d * 1024d);
            double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpuAtStart).TotalMilliseconds;

            List<string> committed = [];
            Trace($"decision turn={turn} ms={watch.Elapsed.TotalMilliseconds:F0} route={string.Join(' ', route.Select(item => item.Kind + ":" + item.CardId))}");
            foreach (PlanAction action in route)
            {
                if (action.Turn != turn)
                    break;
                Trace($"execute {action.Kind}:{action.CardId}{action.PotionId}>{action.TargetCombatId}");
                FairObservation.RevealCursor cursor = FairObservation.CaptureReveal(combat, player);
                if (!Execute(combat, player, action, selector, loop))
                    break;
                committed.Add(action.Kind + ":" + action.CardId + action.PotionId + ">" + action.TargetCombatId);
                if (action.Kind == PlanActionKind.EndTurn || !CombatManager.Instance.IsInProgress)
                    break;
                if (FairObservation.DescribeReveal(cursor, combat, player) != null)
                    break;
            }
            if (committed.Count == 0)
            {
                stalls++;
                if (stalls >= 2)
                {
                    Execute(combat, player, new PlanAction(PlanActionKind.EndTurn, turn), selector, loop);
                    committed.Add("EndTurn:forced");
                    stalls = 0;
                }
            }
            else
            {
                stalls = 0;
            }
            decisions.Add(new DecisionRecord(turn, mode, watch.Elapsed.TotalMilliseconds, solves, candidates,
                [.. committed], meanUtility, allocatedMb, ownPhaseMs, slowestSolveMs, forcedDecision, cpuMs,
                GC.CollectionCount(0) - collectionsAtStart[0], GC.CollectionCount(1) - collectionsAtStart[1],
                GC.CollectionCount(2) - collectionsAtStart[2], scores, safetyStops));
        }

        int endHp = Math.Max(0, player.Creature.CurrentHp);
        object report = new
        {
            label = options.Label,
            seed = options.Scenario.Seed,
            character = options.Scenario.CharacterId,
            encounter = options.Scenario.EncounterId,
            ascension = options.Scenario.Ascension,
            mode,
            settings,
            beam = profile.BeamWidth,
            nodes = profile.MaxExpandedNodes,
            budgetMs = profile.SoftTimeBudgetMilliseconds,
            outcome,
            startHp,
            endHp,
            hpLost = startHp - endHp,
            turns = player.PlayerCombatState?.TurnNumber ?? decisions.LastOrDefault()?.Turn ?? 0,
            selectorFallbacks = selector.Fallbacks,
            decisionCount = decisions.Count,
            meanDecisionMs = decisions.Count == 0 ? 0 : decisions.Average(item => item.Milliseconds),
            maxDecisionMs = decisions.Count == 0 ? 0 : decisions.Max(item => item.Milliseconds),
            totalSeconds = total.Elapsed.TotalSeconds,
            gcServer = System.Runtime.GCSettings.IsServerGC,
            gcLatency = System.Runtime.GCSettings.LatencyMode.ToString(),
            gcPausesByGeneration = PauseMonitor.PausesByGeneration(),
            determinismChecked = determinismCheck,
            determinismMismatches,
            decisions,
        };
        File.WriteAllText(Path.Combine(options.OutputDirectory, "fair-play.json.tmp"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(Path.Combine(options.OutputDirectory, "fair-play.json.tmp"),
            Path.Combine(options.OutputDirectory, "fair-play.json"), overwrite: true);
        if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_EXPECT_FIRST") is { Length: > 0 } expectedFirst)
        {
            string first = decisions.FirstOrDefault()?.Committed.FirstOrDefault() ?? "-";
            bool passed = first.StartsWith("PlayCard:" + expectedFirst + ">", StringComparison.Ordinal);
            Console.WriteLine($"FAIR_ORDER_CHECK expected={expectedFirst} first={first} status={(passed ? "Passed" : "Failed")}");
        }
        foreach ((string type, long bytes) in PauseMonitor.TopAllocations(60))
            Console.WriteLine($"ALLOC_TICK {bytes / (1024d * 1024d):F0} MB {type}");
        Console.WriteLine($"FAIR_PLAY mode={mode} outcome={outcome} hp_lost={startHp - endHp} decisions={decisions.Count} "
            + $"mean_decision_ms={(decisions.Count == 0 ? 0 : decisions.Average(item => item.Milliseconds)):F0}");
    }

    private static void PrepareState(CombatState combat, Player player, MainLoopContext loop)
    {
        string? Env(string name) => Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_" + name) is { Length: > 0 } value
            ? value
            : null;
        async Task Fill(string cardList, PileType pile)
        {
            foreach (string entry in cardList.Split(','))
            {
                string[] parts = entry.Split('+');
                CardModel canonical = ModelDb.AllCards.Single(card => card.Id.Entry == parts[0]);
                CardModel card = combat.CreateCard(canonical, player);
                for (int level = 0; level < (parts.Length > 1 ? int.Parse(parts[1]) : 0) && card.IsUpgradable; level++)
                    CardCmd.Upgrade(card, CardPreviewStyle.None);
                CardPileAddResult added = await CardPileCmd.AddGeneratedCardToCombat(card, pile, player);
                if (!added.success)
                    throw new InvalidOperationException($"could not add {entry} to {pile}");
            }
        }
        async Task Prepare()
        {
            PlayerCombatState cards = player.PlayerCombatState!;
            if (Env("HAND") is { } hand)
            {
                await CardPileCmd.RemoveFromCombat([.. cards.Hand.Cards], true);
                await Fill(hand, PileType.Hand);
            }
            if (Env("DRAW") is { } draw)
            {
                await CardPileCmd.RemoveFromCombat([.. cards.DrawPile.Cards], true);
                await Fill(draw, PileType.Draw);
            }
            if (Env("DISCARD") is { } discard)
                await Fill(discard, PileType.Discard);
            if (Env("ENEMY_HP") is { } enemyHp)
            {
                int[] values = [.. enemyHp.Split(',').Select(int.Parse)];
                for (int index = 0; index < values.Length; index++)
                    await CreatureCmd.SetCurrentHp(combat.Enemies[index], values[index]);
            }
            if (Env("POWERS") is { } powers)
            {
                foreach (string entry in powers.Split(','))
                {
                    string[] parts = entry.Split(':');
                    PowerModel power = ModelDb.AllPowers.Single(model => model.Id.Entry == parts[0]).ToMutable();
                    power.Target = player.Creature;
                    await PowerCmd.Apply(new BlockingPlayerChoiceContext(), power, player.Creature, int.Parse(parts[1]),
                        player.Creature, null);
                }
            }
            if (Env("HP") is { } hp)
                await CreatureCmd.SetCurrentHp(player.Creature, int.Parse(hp));
            if (Env("BLOCK") is { } block)
                await CreatureCmd.GainBlock(player.Creature, int.Parse(block), MegaCrit.Sts2.Core.ValueProps.ValueProp.Move, null);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        loop.RunUntilCompleted(Prepare(), TimeSpan.FromSeconds(30), "prepare fair state");
        if (Env("ENERGY") is { } energyText)
        {
            int energy = int.Parse(energyText);
            PlayerCombatState state = player.PlayerCombatState!;
            if (state.Energy > energy)
                state.LoseEnergy(state.Energy - energy);
            else if (state.Energy < energy)
                state.GainEnergy(energy - state.Energy);
        }
    }

    private static string? WaitForDecisionPoint(CombatState combat, Player player, MainLoopContext loop)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
            if (player.Creature.IsDead)
                return "defeat";
            if (!CombatManager.Instance.IsInProgress || combat.Enemies.All(enemy => !enemy.IsAlive))
                return "victory";
            ActionExecutor executor = RunManager.Instance.ActionExecutor;
            if (combat.CurrentSide == CombatSide.Player
                && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && !CombatManager.Instance.PlayerActionsDisabled
                && executor.CurrentlyRunningAction == null
                && executor.FinishedExecutingActions().IsCompleted)
                return null;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"fair play stalled side={combat.CurrentSide} phase={player.PlayerCombatState?.Phase}");
            loop.Pump(TimeSpan.FromMilliseconds(5));
        }
    }

    private static bool Execute(CombatState combat, Player player, PlanAction action, BenchmarkSelector selector,
        MainLoopContext loop)
    {
        if (WaitForDecisionPoint(combat, player, loop) != null)
            return false;
        selector.Plan(action.GetActionChoicesInExecutionOrder()
            .Concat(action.TurnStartChoices ?? []));
        Creature? target = combat.GetCreature(action.TargetCombatId);
        Task<GameAction> queued;
        switch (action.Kind)
        {
            case PlanActionKind.EndTurn:
                CombatManager.Instance.OnEndedTurnLocally();
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                    new EndPlayerTurnAction(player, player.PlayerCombatState!.TurnNumber));
                WaitUntil(loop, () => player.Creature.IsDead || !CombatManager.Instance.IsInProgress
                    || combat.CurrentSide != CombatSide.Player || player.PlayerCombatState?.Phase != PlayerTurnPhase.Play);
                return true;
            case PlanActionKind.UsePotion:
                PotionModel? potion = player.GetPotionAtSlotIndex(action.PotionSlot);
                if (potion == null || potion.Id.Entry != action.PotionId)
                    return false;
                queued = SolverController.EnqueueAndCaptureActionAsync(
                    candidate => candidate is UsePotionAction use && ReferenceEquals(use.Player, player)
                        && use.PotionIndex == (uint)action.PotionSlot,
                    () => potion.EnqueueManualUse(target),
                    CancellationToken.None);
                break;
            case PlanActionKind.PlayCard:
                CardModel card;
                try
                {
                    card = SolverController.FindCardForDeployment([.. player.PlayerCombatState!.Hand.Cards], action);
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
                if (!card.CanPlayTargeting(target))
                    return false;
                queued = SolverController.EnqueueAndCaptureActionAsync(
                    candidate => candidate is PlayCardAction play && ReferenceEquals(play.NetCombatCard.ToCardModelOrNull(), card),
                    () => card.TryManualPlay(target),
                    CancellationToken.None);
                break;
            default:
                return false;
        }
        loop.RunUntilCompleted(queued, TimeSpan.FromSeconds(60), $"enqueue {action.Kind}");
        loop.RunUntilCompleted(queued.GetAwaiter().GetResult().CompletionTask, TimeSpan.FromSeconds(60), $"complete {action.Kind}");
        return true;
    }

    private static void WaitUntil(MainLoopContext loop, Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(120);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("fair play wait timed out");
            loop.Pump(TimeSpan.FromMilliseconds(5));
        }
    }

    private static void Trace(string message)
    {
        if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_TRACE") == "1")
        {
            Console.WriteLine("[fair-play] " + message);
            Console.Out.Flush();
        }
    }
}
