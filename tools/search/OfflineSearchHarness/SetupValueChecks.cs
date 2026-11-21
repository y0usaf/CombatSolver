using System.Diagnostics;
using System.Text.Json;
using CombatSolver;
using CombatSolver.NewSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;

namespace OfflineSearchHarness;

/// <summary>
/// Regression positions reconstructed from two live co-op misplays (Necrobinder, 2 players):
/// <list type="bullet">
/// <item><c>lethality-hang</c>: turn 1 vs Skulking Colony; the solver played Hang+ and two Defends and let the
/// ethereal Lethality exhaust.</item>
/// <item><c>legion-punch</c>: turn 3 vs Punch Construct (Strong Punch intent) holding Legion of Bone; every plan
/// scored the same and the chosen line's projected HP loss (0) did not match the real loss (3).</item>
/// </list>
/// The teammate is AFK (ends its turn without acting) both in the live harness and in v3's own model, so live
/// outcomes are comparable with the solver's projections.
/// </summary>
internal static class SetupValueChecks
{
    internal const string LethalityHang = "lethality-hang";
    internal const string LegionPunch = "legion-punch";

    internal static void Setup(CombatState combat, Player local, MainLoopContext loop, string scenario)
    {
        if (combat.Players.Count != 2)
            throw new InvalidOperationException($"{scenario} is a 2-player position, got {combat.Players.Count} players.");
        CoopEvalSuite.InstallTimeBypass();
        switch (scenario)
        {
            case LethalityHang:
                RequireEncounterMonster(combat, "SKULKING_COLONY");
                SetHp(local, 51, 66, loop);
                ReplacePile(combat, local, loop, PileType.Hand, ["HANG+1", "LETHALITY", "DEFEND_NECROBINDER", "DEFEND_NECROBINDER", "STRIKE_NECROBINDER"]);
                ReplacePile(combat, local, loop, PileType.Draw, ["UNLEASH", "INJURY", "BODYGUARD", "STRIKE_NECROBINDER", "LEGION_OF_BONE", "DEFEND_NECROBINDER", "SPUR", "DEFEND_NECROBINDER"]);
                ReplacePile(combat, local, loop, PileType.Discard, []);
                loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(combat.Enemies.Single(e => e.IsAlive), 152), TimeSpan.FromSeconds(15), "colony hp");
                SetEnergy(local, 3);
                break;
            case LegionPunch:
                RequireEncounterMonster(combat, "PUNCH_CONSTRUCT");
                Creature construct = combat.Enemies.Single(e => e.IsAlive);
                for (int guard = 0; guard < 4 && construct.Monster!.NextMove.Id != "STRONG_PUNCH_MOVE"; guard++)
                {
                    SetHp(local, 66, 66, loop);
                    EndRoundWithAfkTeammates(combat, local, loop);
                }
                if (construct.Monster!.NextMove.Id != "STRONG_PUNCH_MOVE")
                    throw new InvalidOperationException($"Punch Construct never reached STRONG_PUNCH_MOVE (now {construct.Monster.NextMove.Id}).");
                SetHp(local, 41, 66, loop);
                RemovePowers(local.Creature, loop);
                ApplyPower(local.Creature, "LETHALITY_POWER", 50, loop);
                ApplyPower(local.Creature, "FRAIL_POWER", 1, loop);
                ReplacePile(combat, local, loop, PileType.Hand, ["LEGION_OF_BONE", "INJURY", "BODYGUARD", "DEFEND_NECROBINDER", "STRIKE_NECROBINDER"]);
                ReplacePile(combat, local, loop, PileType.Draw, ["HANG", "UNLEASH", "STRIKE_NECROBINDER", "HANG", "DEFEND_NECROBINDER", "STRIKE_NECROBINDER", "DEFEND_NECROBINDER", "DEFEND_NECROBINDER"]);
                ReplacePile(combat, local, loop, PileType.Discard, []);
                ClearBlock(local.Creature, loop);
                loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(construct, 93), TimeSpan.FromSeconds(15), "construct hp");
                RemovePowers(construct, loop);
                ApplyPower(construct, "HANG_POWER", 2, loop);
                ApplyPower(construct, "DOOM_POWER", 6, loop);
                ClearBlock(construct, loop);
                SetEnergy(local, 3);
                break;
            default:
                throw new ArgumentException($"Unknown setup-value scenario {scenario}.");
        }
        CoopEvalSuite.WaitForQueueIdle(loop);
        Console.WriteLine($"SETUP_VALUE_POSITION scenario={scenario} {Describe(combat, local)}");
    }

    /// <summary>
    /// Decision-level regression for the Lethality-vs-Hang turn. Ground truth comes from full-fight playouts of each
    /// first-turn line (same position, AFK teammate, v3 Normal for the remaining turns, 48 seeds each; see
    /// <see cref="RunPlayout"/>): Hang+Defend+Defend lost 33.3 HP on average, Defend+Lethality+Hang 34.5
    /// (+1.3 +/- 0.9 paired), Lethality+Defend+Defend 35.9 (+2.6 +/- 1.0 paired). Lethality is not the clear
    /// winner the live report assumed (Hardened Shell caps the damage either power adds), so the check requires
    /// that the setup line is kept, priced and simulated, and that the chosen line is not the measured-worse
    /// Lethality+Defend+Defend.
    /// </summary>
    internal static void RunLethalityHang(CombatState combat, Player local, MainLoopContext loop, Action<bool, string> check)
    {
        Setup(combat, local, loop, LethalityHang);
        NewSolverConfig cfg = NewSolverConfig.For(SolverSpeed.Normal) with { MaxParallelism = 4 };
        NewSolverDecision decision = Decide(combat, local, cfg, loop, out CombatRootSnapshot root, out int knownTop);
        string cands = FormatCandidates(decision);
        Console.WriteLine($"LETHALITY_HANG committed=[{FormatRoute(decision.CommittedActions)}] cands=[{cands}]");

        SimStepState rootState = CombatStepEngine.CreateRoot(root, knownTop);
        PartyRootBaseline baseline = PartyRootBaseline.Capture(root, rootState.Simulator);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        PlanAction lethality = CombatStepEngine.EnumerateBaseActions(rootState, local, names, false)
            .First(a => a.Kind == PlanActionKind.PlayCard && a.CardId == "LETHALITY");
        SimStepState afterLethality = CombatStepEngine.ExpandAction(rootState, lethality, local, names, 3, baseline).Single().State;
        int remainingHp = combat.Enemies.Where(e => e.IsAlive).Sum(e => e.CurrentHp);
        double before = PartyEvaluator.EvaluatePartyScaling(rootState.Simulator, rootState.Combat, local, remainingHp);
        double after = PartyEvaluator.EvaluatePartyScaling(afterLethality.Simulator, afterLethality.Combat, local, remainingHp);
        check(after > before, $"Lethality's power must carry future value in the evaluator (before={before:F2} after={after:F2})");

        check(decision.Candidates.Any(c => c.EvaluatedWorlds > 0 && PlaysCard(c.Actions, "LETHALITY")),
            $"a line that plays Lethality must be kept and evaluated; cands=[{cands}]");
        string played = string.Join(',', decision.CommittedActions.Where(a => a.Kind == PlanActionKind.PlayCard).Select(a => a.CardId).Order(StringComparer.Ordinal));
        check(played != "DEFEND_NECROBINDER,DEFEND_NECROBINDER,LETHALITY",
            $"Lethality+Defend+Defend measured worst by playouts; chose [{FormatRoute(decision.CommittedActions)}] cands=[{cands}]");
        check(played is "DEFEND_NECROBINDER,DEFEND_NECROBINDER,HANG" or "DEFEND_NECROBINDER,HANG,LETHALITY",
            $"chosen line must be one of the two measured-best first turns; chose [{FormatRoute(decision.CommittedActions)}]");
    }

    /// <summary>
    /// Prediction regression: the committed line's projected HP loss for this turn must equal the live loss when
    /// the line is executed and the enemy turn resolves; the Legion of Bone line must be kept and scored.
    /// </summary>
    internal static void RunLegionPunch(CombatState combat, Player local, MainLoopContext loop, Action<bool, string> check)
    {
        Setup(combat, local, loop, LegionPunch);
        NewSolverConfig cfg = NewSolverConfig.For(SolverSpeed.Normal) with { MaxParallelism = 4 };
        NewSolverDecision decision = Decide(combat, local, cfg, loop, out CombatRootSnapshot root, out int knownTop);
        string cands = FormatCandidates(decision);
        Console.WriteLine($"LEGION_PUNCH committed=[{FormatRoute(decision.CommittedActions)}] cands=[{cands}]");
        check(decision.Candidates.Any(c => c.EvaluatedWorlds > 0 && PlaysCard(c.Actions, "LEGION_OF_BONE")),
            $"a line that plays Legion of Bone must be kept and evaluated; cands=[{cands}]");
        double spread = decision.Candidates.Where(c => c.EvaluatedWorlds > 0).Select(c => c.MeanUtility).DefaultIfEmpty(0).Max()
            - decision.Candidates.Where(c => c.EvaluatedWorlds > 0).Select(c => c.MeanUtility).DefaultIfEmpty(0).Min();
        check(spread > 0.5, $"candidate scores must distinguish lines (spread={spread:F2}); cands=[{cands}]");
        check(decision.CommittedActions.Count > 0 && decision.CommittedActions[^1].Kind == PlanActionKind.EndTurn,
            $"turn-3 line must be fully committed through EndTurn; chose [{FormatRoute(decision.CommittedActions)}]");

        SolverResult projected = NewSolverResultAdapter.ToSolverResult(
            decision, root, knownTop, BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: null),
            0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero);
        int turn = local.PlayerCombatState!.TurnNumber;
        int predictedLoss = projected.HpLostByTurn[turn];
        Dictionary<ulong, int> predictedPartyHp = TraceSimulatedLine(root, knownTop, decision.CommittedActions, local);
        int hpBefore = local.Creature.CurrentHp;
        foreach (PlanAction action in decision.CommittedActions)
        {
            check(CoopEvalSuite.ExecuteLocalAction(combat, local, action, loop), $"live execution of {action.Kind}:{action.CardId} failed");
            if (action.Kind == PlanActionKind.EndTurn)
                break;
        }
        EndAfkTeammates(combat, local, turn, loop);
        CoopEvalSuite.WaitForNextTurnOrEnd(combat, local, turn, loop);
        Console.WriteLine($"LEGION_PUNCH live_after {Describe(combat, local)}");
        int actualLoss = Math.Max(0, hpBefore - local.Creature.CurrentHp);
        Console.WriteLine($"LEGION_PUNCH predicted_hp_lost={predictedLoss} actual_hp_lost={actualLoss}");
        check(predictedLoss == actualLoss,
            $"projected turn HP loss {predictedLoss} must equal live loss {actualLoss} for [{FormatRoute(decision.CommittedActions)}]");
        foreach (Player p in combat.Players)
        {
            check(predictedPartyHp[p.NetId] == p.Creature.CurrentHp,
                $"projected HP {predictedPartyHp[p.NetId]} of player {p.NetId} must equal live HP {p.Creature.CurrentHp} after the enemy turn");
        }
    }

    /// <summary>
    /// Co-op gift energy after end turn: the local player has ended its turn with an empty energy pool and
    /// playable attacks in hand; the teammate then plays Believe in You on it. The ready-to-end watcher must
    /// un-end the turn (via its real v3 benefit check), and the solver must then spend the gifted energy.
    /// </summary>
    internal enum GiftEndTurnMode
    {
        FullAuto,
        SolverSingleTurn,
        ManualEnd,
        ManualRetake,
    }

    internal static void RunGiftEnergy(CombatState combat, Player local, MainLoopContext loop, Action<bool, string> check, GiftEndTurnMode mode)
    {
        check(combat.Players.Count == 2, $"coop-gift-energy needs 2 players, got {combat.Players.Count}");
        CoopEvalSuite.InstallTimeBypass();
        Player mate = combat.Players.First(p => !ReferenceEquals(p, local));
        int turn = local.PlayerCombatState!.TurnNumber;
        SetHp(local, 60, Math.Max(60, local.Creature.MaxHp), loop);
        ReplacePile(combat, local, loop, PileType.Hand, ["STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD"]);
        ReplacePile(combat, mate, loop, PileType.Hand, ["BELIEVE_IN_YOU"]);
        SetEnergy(local, 0);
        Creature enemy = combat.Enemies.First(e => e.IsAlive);
        foreach (Creature extra in combat.Enemies.Where(e => e.IsAlive && !ReferenceEquals(e, enemy)).ToArray())
            loop.RunUntilCompleted(CreatureCmd.Kill(extra, force: true), TimeSpan.FromSeconds(15), "single enemy");
        loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(enemy, Math.Min(enemy.MaxHp, 40)), TimeSpan.FromSeconds(15), "enemy hp");
        CoopEvalSuite.WaitForQueueIdle(loop);

        SolverController.BeginCombatForTesting(combat, fullAuto: mode == GiftEndTurnMode.FullAuto);
        SolverController.ResetUnEndTurnStateForTesting();
        CombatManager.Instance.OnEndedTurnLocally();
        EndPlayerTurnAction end = new(local, turn);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
        loop.RunUntilCompleted(end.CompletionTask, TimeSpan.FromSeconds(30), "local end turn");
        // The deployment path records this baseline whenever the solver itself ends the turn (Auto or Run This Turn).
        if (mode != GiftEndTurnMode.ManualEnd)
            SolverController.RecordLocalReadyToEndTurnBaseline(combat, local, turn);
        check(CombatManager.Instance.IsPlayerReadyToEndTurn(local), "local player must be ready to end turn before the gift");

        Task<UnEndTurnPollOutcome> before = SolverController.TryPollAndUnEndTurnForTestingAsync(combat);
        loop.RunUntilCompleted(before, TimeSpan.FromSeconds(30), "poll before gift");
        UnEndTurnPollOutcome expectedBefore = mode == GiftEndTurnMode.ManualEnd ? UnEndTurnPollOutcome.Idle : UnEndTurnPollOutcome.Unchanged;
        check(before.GetAwaiter().GetResult() == expectedBefore, $"poll before the gift must be {expectedBefore}, got {before.GetAwaiter().GetResult()}");

        if (mode == GiftEndTurnMode.ManualRetake)
        {
            // The player cancels the solver's end turn and ends it again by hand; the turn is theirs now.
            CombatManager.Instance.TryUndoReadyToEndTurn(local);
            CoopEvalSuite.WaitForQueueIdle(loop);
            check(!CombatManager.Instance.IsPlayerReadyToEndTurn(local), "player must be able to cancel the solver's end turn");
            Task<UnEndTurnPollOutcome> retake = SolverController.TryPollAndUnEndTurnForTestingAsync(combat);
            loop.RunUntilCompleted(retake, TimeSpan.FromSeconds(30), "poll after manual cancel");
            check(retake.GetAwaiter().GetResult() == UnEndTurnPollOutcome.Idle, $"manual cancel must disarm, got {retake.GetAwaiter().GetResult()}");
            CombatManager.Instance.OnEndedTurnLocally();
            EndPlayerTurnAction manualEnd = new(local, turn);
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(manualEnd);
            loop.RunUntilCompleted(manualEnd.CompletionTask, TimeSpan.FromSeconds(30), "manual end turn");
            check(CombatManager.Instance.IsPlayerReadyToEndTurn(local), "manual end turn must leave the player ready");
        }

        CardModel believe = mate.PlayerCombatState!.Hand.Cards.Single(c => c.Id.Entry == "BELIEVE_IN_YOU");
        check(believe.CanPlayTargeting(local.Creature), "teammate must be able to target the local player with Believe in You");
        PlayCardAction gift = new(believe, local.Creature);
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(gift);
        loop.RunUntilCompleted(gift.CompletionTask, TimeSpan.FromSeconds(30), "teammate Believe in You");
        CoopEvalSuite.WaitForQueueIdle(loop);
        int gifted = local.PlayerCombatState!.Energy;
        check(gifted >= 2, $"Believe in You must grant energy to the ready local player (energy={gifted})");
        check(!CombatManager.Instance.IsPlayerReadyToEndTurn(mate), "teammate has not ended its turn yet");

        Task<UnEndTurnPollOutcome> poll = SolverController.TryPollAndUnEndTurnForTestingAsync(combat);
        loop.RunUntilCompleted(poll, TimeSpan.FromSeconds(60), "poll after gift");
        UnEndTurnPollOutcome outcome = poll.GetAwaiter().GetResult();
        Console.WriteLine($"GIFT_ENERGY mode={mode} poll={outcome} energy={gifted}");
        if (mode is GiftEndTurnMode.ManualEnd or GiftEndTurnMode.ManualRetake)
        {
            check(outcome == UnEndTurnPollOutcome.Idle, $"a turn the player ended by hand with Auto off must not be un-ended, got {outcome}");
            check(CombatManager.Instance.IsPlayerReadyToEndTurn(local), "manual end turn must stay ended");
            return;
        }
        check(outcome == UnEndTurnPollOutcome.Triggered, $"gifted energy with playable Strikes must un-end the turn, got {outcome}");
        check(!CombatManager.Instance.IsPlayerReadyToEndTurn(local), "local player must no longer be ready to end turn");
        check(!CombatManager.Instance.PlayerActionsDisabled, "local actions must be enabled after un-ending");

        int enemyHpBefore = enemy.CurrentHp;
        NewSolverDecision decision = Decide(combat, local, NewSolverConfig.For(SolverSpeed.Normal) with { MaxParallelism = 4 }, loop, out _, out _);
        Console.WriteLine($"GIFT_ENERGY committed=[{FormatRoute(decision.CommittedActions)}]");
        check(decision.CommittedActions.Any(a => a.Kind == PlanActionKind.PlayCard),
            $"solver must spend the gifted energy; chose [{FormatRoute(decision.CommittedActions)}]");
        foreach (PlanAction action in decision.CommittedActions)
        {
            if (action.Kind == PlanActionKind.EndTurn)
                break;
            check(CoopEvalSuite.ExecuteLocalAction(combat, local, action, loop), $"live execution of {action.CardId} failed");
        }
        check(local.PlayerCombatState!.Energy < gifted, $"gifted energy must be spent (energy {gifted} -> {local.PlayerCombatState.Energy})");
        check(enemy.CurrentHp < enemyHpBefore || local.Creature.Block > 0,
            $"spent energy must have an effect (enemy {enemyHpBefore}->{enemy.CurrentHp}, block {local.Creature.Block})");
    }

    /// <summary>
    /// Full-fight playout from a scenario: turn 1 either follows the solver or a forced card list, then v3 plays the
    /// rest with an AFK teammate. Used to establish which first-turn line is actually better.
    /// </summary>
    internal static void RunPlayout(CombatState combat, HarnessOptions options, MainLoopContext loop, string arg)
    {
        string[] parts = arg.Split(':', 2);
        string scenario = parts[0];
        string line = parts.Length > 1 ? parts[1] : "solver";
        Player local = LocalContext.GetMe(combat) ?? combat.Players[0];
        Setup(combat, local, loop, scenario);
        SolverSpeed speed = Enum.Parse<SolverSpeed>(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_SPEED") ?? nameof(SolverSpeed.Normal));
        NewSolverConfig cfg = NewSolverConfig.For(speed) with { MaxParallelism = 4 };
        int startHp = local.Creature.CurrentHp;
        int startTurn = local.PlayerCombatState!.TurnNumber;
        List<string> log = [];
        string outcome = "turn_cap";
        int decisions = 0;
        while (true)
        {
            if (CoopEvalSuite.CheckTerminal(combat, local) is { } terminal)
            {
                outcome = terminal;
                break;
            }
            int turn = local.PlayerCombatState!.TurnNumber;
            if (turn > startTurn + 30 || decisions > 200)
                break;
            IReadOnlyList<PlanAction> actions;
            if (turn == startTurn && line != "solver")
            {
                Creature target = combat.Enemies.First(e => e.IsAlive);
                actions = [.. line.Split(',').Select(id => new PlanAction(PlanActionKind.PlayCard, turn, CardId: id,
                        TargetCombatId: local.PlayerCombatState.Hand.Cards.First(c => c.Id.Entry == id).TargetType == TargetType.AnyEnemy ? target.CombatId : null)),
                    new PlanAction(PlanActionKind.EndTurn, turn)];
            }
            else
            {
                actions = Decide(combat, local, cfg, loop, out _, out _).CommittedActions;
                decisions++;
            }
            bool ended = false;
            foreach (PlanAction action in actions)
            {
                FairObservation.RevealCursor cursor = FairObservation.CaptureReveal(combat, local);
                if (!CoopEvalSuite.ExecuteLocalAction(combat, local, action, loop))
                {
                    if (turn == startTurn && line != "solver")
                        throw new InvalidOperationException($"forced action {action.CardId} could not be played");
                    break;
                }
                log.Add($"t{turn}:{action.Kind}:{action.CardId}");
                if (action.Kind == PlanActionKind.EndTurn)
                {
                    ended = true;
                    break;
                }
                if (CoopEvalSuite.CheckTerminal(combat, local) != null || FairObservation.DescribePlanInvalidation(cursor, combat, local) != null)
                    break;
            }
            if (CoopEvalSuite.CheckTerminal(combat, local) != null)
                continue;
            if (!ended)
            {
                if (actions.Count == 0 || decisions > 150)
                {
                    CoopEvalSuite.ExecuteLocalAction(combat, local, new PlanAction(PlanActionKind.EndTurn, turn), loop);
                    ended = true;
                }
                else
                    continue;
            }
            EndAfkTeammates(combat, local, turn, loop);
            CoopEvalSuite.WaitForNextTurnOrEnd(combat, local, turn, loop);
        }
        int hpLost = Math.Max(0, startHp - Math.Max(0, local.Creature.CurrentHp));
        var summary = new { scenario, line, speed = speed.ToString(), outcome, hpLost, turns = local.PlayerCombatState?.TurnNumber ?? 0, log };
        File.WriteAllText(Path.Combine(options.OutputDirectory, $"setup-playout-{scenario}.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"SETUP_PLAYOUT scenario={scenario} line={line} outcome={outcome} hp_lost={hpLost} turns={summary.turns}");
    }

    internal static NewSolverDecision Decide(CombatState combat, Player local, NewSolverConfig cfg, MainLoopContext loop,
        out CombatRootSnapshot root, out int knownTop)
    {
        CoopEvalSuite.WaitForQueueIdle(loop);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        ulong seed = NewFairSolver.VisibleSeed(combat, local);
        int top = FairDrawPileKnowledge.Capture(local.PlayerCombatState!.DrawPile).KnownTop;
        int samples = NewFairSolver.IsForcedEndTurnLive(local) ? 1 : cfg.Samples;
        CombatRootSnapshot[] roots = [.. Enumerable.Range(0, samples).Select(i =>
            CombatRootSnapshot.Capture(combat, fair: FairObservation.FromSeed(seed + (ulong)i)))];
        Task<NewSolverDecision> task = Task.Run(() => NewFairSolver.DecideFromWorlds(roots, names, top, cfg, CancellationToken.None));
        loop.RunUntilCompleted(task, TimeSpan.FromMinutes(5), "setup-value v3 decision");
        root = roots[0];
        knownTop = top;
        return task.GetAwaiter().GetResult();
    }

    private static Dictionary<ulong, int> TraceSimulatedLine(CombatRootSnapshot root, int knownTop, IReadOnlyList<PlanAction> actions, Player local)
    {
        SimStepState state = CombatStepEngine.CreateRoot(root, knownTop);
        void Print(string label)
        {
            var sim = state.Simulator;
            var combat = state.Combat;
            var cs = sim.State.GetCreature(local.Creature);
            string osty = combat.GetOsty(local) is { } o ? $"{sim.State.GetCreature(o).CurrentHp}/{sim.State.GetCreature(o).MaxHp}" : "-";
            string mates = string.Join(';', combat.Players.Where(p => !ReferenceEquals(p, local)).Select(p =>
                $"{sim.State.GetCreature(p.Creature).CurrentHp}b{sim.State.GetCreature(p.Creature).Block}o{(combat.GetOsty(p) is { } mo ? $"{sim.State.GetCreature(mo).CurrentHp}/{sim.State.GetCreature(mo).MaxHp}{(sim.State.GetCreature(mo).IsAlive ? "" : "x")}" : "-")}"));
            string enemies = string.Join(';', combat.Enemies.Select(e => $"{sim.State.GetCreature(e).CurrentHp}b{sim.State.GetCreature(e).Block}"));
            Console.WriteLine($"SIM_TRACE {label} turn={combat.GetPlayerTurnNumber(local)} hp={cs.CurrentHp} block={cs.Block} osty={osty} "
                + $"energy={sim.State.GetPlayerCombatState(local).Energy} powers={string.Join(',', combat.EffectivePowers().Where(p => ReferenceEquals(p.Owner, local.Creature)).Select(p => p.Id.Entry + ":" + p.Amount))} mates={mates} enemies={enemies}");
        }
        Print("root");
        foreach (PlanAction action in actions)
        {
            bool ok = CombatStepEngine.TryReplayAction(state, action, local, out _, out _);
            if (!ok)
                throw new InvalidOperationException($"committed action {action.Kind}:{action.CardId} failed to replay in its own root world");
            Print($"{action.Kind}:{action.CardId}");
        }
        return state.Combat.Players.ToDictionary(p => p.NetId, p => Math.Max(0, state.Simulator.State.GetCreature(p.Creature).CurrentHp));
    }

    private static void EndRoundWithAfkTeammates(CombatState combat, Player local, MainLoopContext loop)
    {
        int turn = local.PlayerCombatState!.TurnNumber;
        if (!CoopEvalSuite.ExecuteLocalAction(combat, local, new PlanAction(PlanActionKind.EndTurn, turn), loop))
            throw new InvalidOperationException("could not end local turn while advancing the position");
        EndAfkTeammates(combat, local, turn, loop);
        CoopEvalSuite.WaitForNextTurnOrEnd(combat, local, turn, loop);
    }

    private static void EndAfkTeammates(CombatState combat, Player local, int turn, MainLoopContext loop)
    {
        foreach (Player mate in combat.Players)
        {
            if (ReferenceEquals(mate, local) || !mate.Creature.IsAlive || CoopEvalSuite.CheckTerminal(combat, local) != null)
                continue;
            if (CombatManager.Instance.IsPlayerReadyToEndTurn(mate))
                continue;
            CoopEvalSuite.WaitForQueueIdle(loop);
            EndPlayerTurnAction end = new(mate, mate.PlayerCombatState!.TurnNumber);
            RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(end);
            loop.RunUntilCompleted(end.CompletionTask, TimeSpan.FromSeconds(30), "afk teammate end turn");
        }
    }

    private static void RequireEncounterMonster(CombatState combat, string monsterId)
    {
        if (!combat.Enemies.Any(e => e.IsAlive && e.Monster?.Id.Entry == monsterId))
            throw new InvalidOperationException($"expected encounter with {monsterId}, got {string.Join(',', combat.Enemies.Select(e => e.Monster?.Id.Entry))}");
    }

    private static void ReplacePile(CombatState combat, Player player, MainLoopContext loop, PileType pileType, IReadOnlyList<string> cards)
    {
        async Task DoReplace()
        {
            PlayerCombatState pcs = player.PlayerCombatState!;
            CardPile pile = CardPile.Get(pileType, player) ?? throw new InvalidOperationException($"missing pile {pileType}");
            if (pile.Cards.Count > 0)
                await CardPileCmd.RemoveFromCombat([.. pile.Cards], true);
            foreach (string entry in cards)
            {
                string[] parts = entry.Split('+');
                CardModel canonical = ModelDb.AllCards.Single(c => c.Id.Entry == parts[0]);
                CardModel card = combat.CreateCard(canonical, player);
                for (int level = 0; level < (parts.Length > 1 ? int.Parse(parts[1]) : 0) && card.IsUpgradable; level++)
                    CardCmd.Upgrade(card, CardPreviewStyle.None);
                CardPileAddResult added = await CardPileCmd.AddGeneratedCardToCombat(card, pileType, player);
                if (!added.success)
                    throw new InvalidOperationException($"could not add {entry} to {pileType}");
            }
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        loop.RunUntilCompleted(DoReplace(), TimeSpan.FromSeconds(30), $"replace {pileType}");
    }

    private static void SetHp(Player player, int hp, int maxHp, MainLoopContext loop)
    {
        loop.RunUntilCompleted(CreatureCmd.SetMaxHp(player.Creature, maxHp), TimeSpan.FromSeconds(15), "set max hp");
        loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(player.Creature, hp), TimeSpan.FromSeconds(15), "set hp");
    }

    private static void SetEnergy(Player player, int energy)
    {
        PlayerCombatState pcs = player.PlayerCombatState!;
        if (pcs.Energy > energy)
            pcs.LoseEnergy(pcs.Energy - energy);
        else if (pcs.Energy < energy)
            pcs.GainEnergy(energy - pcs.Energy);
    }

    private static void ClearBlock(Creature creature, MainLoopContext loop)
    {
        if (creature.Block > 0)
            loop.RunUntilCompleted(CreatureCmd.LoseBlock(new BlockingPlayerChoiceContext(), creature, creature.Block, null), TimeSpan.FromSeconds(15), "clear block");
    }

    private static void RemovePowers(Creature creature, MainLoopContext loop)
    {
        foreach (PowerModel power in creature.Powers.ToArray())
            loop.RunUntilCompleted(PowerCmd.Remove(power), TimeSpan.FromSeconds(15), $"remove {power.Id.Entry}");
    }

    private static void ApplyPower(Creature target, string powerId, int amount, MainLoopContext loop)
    {
        PowerModel power = ModelDb.AllPowers.Single(p => p.Id.Entry == powerId).ToMutable();
        power.Target = target;
        loop.RunUntilCompleted(PowerCmd.Apply(new BlockingPlayerChoiceContext(), power, target, amount, target, null),
            TimeSpan.FromSeconds(15), $"apply {powerId}");
    }

    private static bool PlaysCard(IReadOnlyList<PlanAction> actions, string cardId)
        => actions.Any(a => a.Kind == PlanActionKind.PlayCard && a.CardId == cardId);

    private static string FormatRoute(IReadOnlyList<PlanAction> actions)
        => string.Join(" -> ", actions.Select(a => $"{a.Kind}:{a.CardId}{a.PotionId}>{a.TargetCombatId}"));

    private static string FormatCandidates(NewSolverDecision decision)
        => string.Join(" | ", decision.Candidates.Take(12).Select(c =>
            $"{FormatRoute(c.Actions)}={c.CvarUtility:F2}(m={c.MeanUtility:F2},d={c.DeathRate:F2},n={c.EvaluatedWorlds})"));

    private static string Describe(CombatState combat, Player local)
    {
        PlayerCombatState pcs = local.PlayerCombatState!;
        string osty = local.Osty is { } o ? $"{o.CurrentHp}/{o.MaxHp}{(o.IsAlive ? "" : "dead")}" : "-";
        return $"turn={pcs.TurnNumber} hp={local.Creature.CurrentHp}/{local.Creature.MaxHp} block={local.Creature.Block} energy={pcs.Energy} osty={osty} "
            + $"powers={string.Join(',', local.Creature.Powers.Select(p => p.Id.Entry + ":" + p.Amount))} "
            + $"hand={string.Join(',', pcs.Hand.Cards.Select(c => c.Id.Entry + (c.CurrentUpgradeLevel > 0 ? "+" + c.CurrentUpgradeLevel : "")))} "
            + $"draw={pcs.DrawPile.Cards.Count} discard={pcs.DiscardPile.Cards.Count} "
            + $"enemies={string.Join(';', combat.Enemies.Where(e => e.IsAlive).Select(e => $"{e.Monster?.Id.Entry}:{e.CurrentHp}/{e.MaxHp}@{e.Monster?.NextMove.Id}[{string.Join(',', e.Powers.Select(p => p.Id.Entry + ":" + p.Amount))}]"))} "
            + $"mates={string.Join(';', combat.Players.Where(p => !ReferenceEquals(p, local)).Select(p => $"{p.Character.Id.Entry}:{p.Creature.CurrentHp}o{(p.Osty is { } mo ? $"{mo.CurrentHp}/{mo.MaxHp}{(mo.IsAlive ? "" : "x")}" : "-")}"))}";
    }
}
