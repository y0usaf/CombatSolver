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
        string[] Committed, double? MeanUtility);

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
        InstallPlayBypasses();
        Player player = combat.Players.Single();
        FairPlannerSettings settings = new(
            int.Parse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_K") ?? "6"),
            int.Parse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_CANDIDATES") ?? "4"),
            double.Parse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_DEATH_PENALTY") ?? "200"),
            double.Parse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_ENEMY_WEIGHT") ?? "0.25"));
        SolverSearchProfile profile = SolverSettings.Capture().Profile with
        {
            SoftTimeBudgetMilliseconds = options.BudgetMilliseconds,
        };
        ulong samplerBase = (ulong)StableHash(options.Label + "|" + options.Scenario.Seed) << 20;
        int sampleCounter = 0;
        BenchmarkSelector selector = new();
        using IDisposable selectorScope = CardSelectCmd.PushSelector(selector, localOnly: true);

        int startHp = player.Creature.CurrentHp;
        List<DecisionRecord> decisions = [];
        Stopwatch total = Stopwatch.StartNew();
        string outcome = "timeout";
        int stalls = 0;
        while (true)
        {
            string? ended = WaitForDecisionPoint(combat, player, loop);
            if (ended != null)
            {
                outcome = ended;
                break;
            }
            int turn = player.PlayerCombatState!.TurnNumber;
            if (turn > 40)
                break;
            SolverDisplayNames names = SolverDisplayNames.Capture(combat);
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
            SearchPolicySnapshot policy = FairDecisionPlanner.IsolatedPolicy(
                SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, includeTurnSetup: false,
                    theftPolicy: SolverController.ResolveTheftPolicy(combat)),
                profile);
            Stopwatch watch = Stopwatch.StartNew();
            IReadOnlyList<PlanAction> route;
            bool commitOne;
            int solves = 1;
            int candidates = 1;
            double? meanUtility = null;
            if (mode == "v2")
            {
                CombatRootSnapshot[] roots = [.. Enumerable.Range(0, settings.Samples).Select(_ =>
                    CombatRootSnapshot.Capture(combat, fair: FairObservation.FromSeed(samplerBase + (ulong)sampleCounter++)))];
                Task<FairDecision?> decide = Task.Run(() => FairDecisionPlanner.Decide(
                    roots,
                    (root, prefix) => FairDecisionPlanner.Solve(root, names, damage, policy, prefix, CancellationToken.None),
                    settings,
                    CancellationToken.None));
                loop.RunUntilCompleted(decide, TimeSpan.FromMinutes(10), "fair v2 decision");
                FairDecision? decision = decide.GetAwaiter().GetResult();
                route = decision == null ? [] : [decision.Action];
                solves = decision?.Solves ?? settings.Samples;
                candidates = decision?.Candidates.Count ?? 0;
                meanUtility = decision?.Candidates.Max(score => score.MeanUtility);
                commitOne = true;
            }
            else
            {
                FairObservation observation = mode == "clairvoyant"
                    ? FairObservation.ClairvoyantForOfflineBenchmark(combat)
                    : FairObservation.FromSeed(samplerBase + (ulong)sampleCounter++);
                CombatRootSnapshot root = CombatRootSnapshot.Capture(combat, fair: observation);
                Task<SolverResult> solve = Task.Run(() =>
                    FairDecisionPlanner.Solve(root, names, damage, policy, null, CancellationToken.None));
                loop.RunUntilCompleted(solve, TimeSpan.FromMinutes(10), "fair single solve");
                route = solve.GetAwaiter().GetResult().BestNode.Actions;
                commitOne = false;
            }
            watch.Stop();

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
                if (action.Kind == PlanActionKind.EndTurn || commitOne || !CombatManager.Instance.IsInProgress)
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
                [.. committed], meanUtility));
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
            decisions,
        };
        File.WriteAllText(Path.Combine(options.OutputDirectory, "fair-play.json.tmp"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(Path.Combine(options.OutputDirectory, "fair-play.json.tmp"),
            Path.Combine(options.OutputDirectory, "fair-play.json"), overwrite: true);
        Console.WriteLine($"FAIR_PLAY mode={mode} outcome={outcome} hp_lost={startHp - endHp} decisions={decisions.Count} "
            + $"mean_decision_ms={(decisions.Count == 0 ? 0 : decisions.Average(item => item.Milliseconds)):F0}");
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

    private static uint StableHash(string text)
    {
        uint hash = 2166136261;
        foreach (char character in text)
            hash = (hash ^ character) * 16777619;
        return hash;
    }
}
