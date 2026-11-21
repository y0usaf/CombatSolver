using System.Reflection;
using System.Text.Json;
using CombatSolver;
using CombatSolver.NewSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace OfflineSearchHarness;

internal static class NewSolverChecks
{
    private static readonly FieldInfo RunRngField = typeof(RunState)
        .GetField("<Rng>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(RunState).FullName, "<Rng>k__BackingField");
    private static readonly PropertyInfo OddsCurrentValue = typeof(AbstractOdds)
        .GetProperty(nameof(AbstractOdds.CurrentValue))
        ?? throw new MissingMemberException(typeof(AbstractOdds).FullName, nameof(AbstractOdds.CurrentValue));

    internal static void Run(CombatState combat, HarnessOptions options, MainLoopContext loop, string suite)
    {
        Player player = LocalContext.GetMe(combat) ?? combat.Players[0];
        int passed = 0;
        void Check(bool cond, string message)
        {
            if (!cond)
                throw new InvalidOperationException($"NewSolverChecks({suite}) FAILED: {message}");
            passed++;
        }

        switch (suite)
        {
            case "fairness":
                RunFairnessInvariance(combat, player, loop, Check);
                break;
            case "coop-legion":
                RunCoopLegionOfBone(combat, player, loop, Check);
                break;
            case "coop-rally":
                RunCoopRally(combat, player, loop, Check);
                break;
            case "coop-lift":
                RunCoopLift(combat, player, loop, Check);
                break;
            case "coop-energy":
                RunCoopEnergy(combat, player, loop, Check);
                break;
            case "solo-equiv":
                RunSoloEquivalence(combat, player, loop, Check);
                break;
            case "draw-first":
                RunDrawFirst(combat, player, loop, Check);
                break;
            case "transfigure-early":
                RunTransfigureEarly(combat, player, loop, Check);
                break;
            case "transfigure-late":
                RunTransfigureLate(combat, player, loop, Check);
                break;
            case "un-end-turn":
                RunUnEndTurnAndLiveWiring(combat, player, loop, Check);
                break;
            case "coop-new-mirrors":
                RunCoopNewMirrors(combat, player, loop, Check);
                break;
            case "setup-lethality":
                SetupValueChecks.RunLethalityHang(combat, player, loop, Check);
                break;
            case "coop-legion-punch":
                SetupValueChecks.RunLegionPunch(combat, player, loop, Check);
                break;
            case "coop-gift-energy":
                SetupValueChecks.RunGiftEnergy(combat, player, loop, Check, SetupValueChecks.GiftEndTurnMode.FullAuto);
                break;
            case "coop-gift-energy-single":
                SetupValueChecks.RunGiftEnergy(combat, player, loop, Check, SetupValueChecks.GiftEndTurnMode.SolverSingleTurn);
                break;
            case "coop-gift-energy-manual":
                SetupValueChecks.RunGiftEnergy(combat, player, loop, Check, SetupValueChecks.GiftEndTurnMode.ManualEnd);
                break;
            case "coop-gift-energy-retake":
                SetupValueChecks.RunGiftEnergy(combat, player, loop, Check, SetupValueChecks.GiftEndTurnMode.ManualRetake);
                break;
            default:
                throw new ArgumentException($"Unknown NewSolverChecks suite: {suite}");
        }

        Console.WriteLine($"NEW_SOLVER_CHECK suite={suite} players={combat.Players.Count} passed={passed} status=Passed");
        File.WriteAllText(
            Path.Combine(options.OutputDirectory, $"new-solver-{suite}.json"),
            JsonSerializer.Serialize(new { suite, players = combat.Players.Count, passed, status = "Passed" },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RunFairnessInvariance(CombatState combat, Player player, MainLoopContext loop, Action<bool, string> check)
    {
        AddCardsToHand(combat, player, loop, ["PommelStrike", "ShrugItOff"]);
        NewSolverConfig cfg = NewSolverConfig.For(SolverSpeed.Normal) with { MaxParallelism = 4 };
        string liveA = ContinuationStamp.CaptureLive(combat).StateText;
        ulong seedA = NewFairSolver.VisibleSeed(combat, player);
        NewSolverDecision decisionA = NewFairSolver.DecideLive(combat, player, cfg, CancellationToken.None);
        string sigA = Signature(decisionA);

        MutateHiddenState(combat, player);
        string liveB = ContinuationStamp.CaptureLive(combat).StateText;
        check(liveA != liveB, "hidden mutation must alter live clairvoyant state");

        ulong seedB = NewFairSolver.VisibleSeed(combat, player);
        check(seedA == seedB, $"visible seed must be invariant across hidden worlds ({seedA} vs {seedB})");

        NewSolverDecision decisionB = NewFairSolver.DecideLive(combat, player, cfg with { MaxParallelism = 1 }, CancellationToken.None);
        string sigB = Signature(decisionB);
        check(sigA == sigB, $"v3 paired-world decision mismatch: A=[{sigA}] B=[{sigB}]");
    }

    private static void RunCoopLegionOfBone(CombatState combat, Player player, MainLoopContext loop, Action<bool, string> check)
    {
        check(combat.Players.Count >= 2, $"coop-legion requires >=2 players, got {combat.Players.Count}");
        ReplaceHand(combat, player, loop, ["LegionOfBone", "DefendNecrobinder", "StrikeNecrobinder"]);
        SetEnergy(player, 3);
        foreach (Player p in combat.Players)
            loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(p.Creature, 25), TimeSpan.FromSeconds(15), "set party hp");

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat, fair: FairObservation.FromSeed(42));
        SimStepState simState = CombatStepEngine.CreateRoot(root, 0);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        PlanAction legionAct = CombatStepEngine.EnumerateBaseActions(simState, player, names, false)
            .First(a => a.Kind == PlanActionKind.PlayCard && a.CardId == "LEGION_OF_BONE");
        List<StepOutcome> outcomes = CombatStepEngine.ExpandAction(simState, legionAct, player, names, 3);
        check(outcomes.Count == 1, "LegionOfBone expands cleanly");
        SimStepState afterLegion = outcomes[0].State;
        foreach (Player p in combat.Players)
        {
            Creature? osty = afterLegion.Combat.GetOsty(p);
            check(osty != null && afterLegion.Simulator.State.GetCreature(osty).IsAlive
                && afterLegion.Simulator.State.GetCreature(osty).CurrentHp >= 6,
                $"LegionOfBone must summon Osty (>=6 HP) for player {p.NetId}");
        }

        NewSolverConfig cfg = NewSolverConfig.For(SolverSpeed.Fast) with { Samples = 4, MaxParallelism = 2 };
        NewSolverDecision decision = NewFairSolver.DecideLive(combat, player, cfg, CancellationToken.None);
        bool playsLegion = decision.CommittedActions.Any(a => a.Kind == PlanActionKind.PlayCard && a.CardId == "LEGION_OF_BONE");
        string cands = string.Join(" | ", decision.Candidates.Select(c => $"{FormatRoute(c.Actions)}={c.CvarUtility:F2}(m={c.MeanUtility:F2},d={c.DeathRate:F2},w={c.WinRate:F2},n={c.EvaluatedWorlds})"));
        check(playsLegion, $"v3 must play LegionOfBone in {combat.Players.Count}P co-op; chose [{FormatRoute(decision.CommittedActions)}] cands=[{cands}]");
    }

    private static void RunCoopRally(CombatState combat, Player player, MainLoopContext loop, Action<bool, string> check)
    {
        check(combat.Players.Count >= 2, $"coop-rally requires >=2 players, got {combat.Players.Count}");
        ReplaceHand(combat, player, loop, ["Rally", "StrikeIronclad", "StrikeIronclad"]);
        SetEnergy(player, 2);
        foreach (Player p in combat.Players)
            loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(p.Creature, 14), TimeSpan.FromSeconds(15), "set party hp");
        foreach (Creature enemy in combat.Enemies)
            loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(enemy, 120), TimeSpan.FromSeconds(15), "set enemy hp");

        NewSolverConfig cfg = NewSolverConfig.For(SolverSpeed.Fast) with { Samples = 4, MaxParallelism = 2 };
        NewSolverDecision decision = NewFairSolver.DecideLive(combat, player, cfg, CancellationToken.None);
        bool playsRally = decision.CommittedActions.Any(a => a.Kind == PlanActionKind.PlayCard && a.CardId == "RALLY");
        string cands = string.Join(" | ", decision.Candidates.Select(c => $"{FormatRoute(c.Actions)}={c.CvarUtility:F2}(m={c.MeanUtility:F2},d={c.DeathRate:F2},n={c.EvaluatedWorlds})"));
        check(playsRally, $"v3 must play Rally in {combat.Players.Count}P co-op when party is under attack; chose [{FormatRoute(decision.CommittedActions)}] cands=[{cands}]");
    }

    private static void RunCoopLift(CombatState combat, Player player, MainLoopContext loop, Action<bool, string> check)
    {
        check(combat.Players.Count >= 2, $"coop-lift requires >=2 players, got {combat.Players.Count}");
        Player ally = combat.Players.First(p => !ReferenceEquals(p, player));
        ReplaceHand(combat, player, loop, ["Lift", "StrikeIronclad"]);
        SetEnergy(player, 1);
        loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(player.Creature, 70), TimeSpan.FromSeconds(15), "set self hp");
        loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(ally.Creature, 5), TimeSpan.FromSeconds(15), "set ally low hp");
        foreach (Creature enemy in combat.Enemies)
            loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(enemy, 120), TimeSpan.FromSeconds(15), "set enemy hp");

        NewSolverConfig cfg = NewSolverConfig.For(SolverSpeed.Fast) with { Samples = 4, MaxParallelism = 2 };
        NewSolverDecision decision = NewFairSolver.DecideLive(combat, player, cfg, CancellationToken.None);
        PlanAction? lift = decision.CommittedActions.FirstOrDefault(a => a.Kind == PlanActionKind.PlayCard && a.CardId == "LIFT");
        check(lift != null && lift.TargetCombatId == ally.Creature.CombatId,
            $"v3 must play Lift targeting endangered ally {ally.Creature.CombatId}; chose [{FormatRoute(decision.CommittedActions)}]");
    }

    private static void RunCoopEnergy(CombatState combat, Player player, MainLoopContext loop, Action<bool, string> check)
    {
        check(combat.Players.Count >= 2, $"coop-energy requires >=2 players, got {combat.Players.Count}");
        Player ally = combat.Players.First(p => !ReferenceEquals(p, player));
        ReplaceHand(combat, player, loop, ["BelieveInYou"]);
        SetEnergy(player, 0);

        NewSolverConfig cfg = NewSolverConfig.For(SolverSpeed.Fast) with { Samples = 4, MaxParallelism = 2 };
        NewSolverDecision decision = NewFairSolver.DecideLive(combat, player, cfg, CancellationToken.None);
        PlanAction? act = decision.CommittedActions.FirstOrDefault(a => a.Kind == PlanActionKind.PlayCard && a.CardId == "BELIEVE_IN_YOU");
        string cands = string.Join(" | ", decision.Candidates.Select(c => $"{c.Key}={c.CvarUtility:F2}(d={c.DeathRate:F2})"));
        check(act != null && act.TargetCombatId == ally.Creature.CombatId,
            $"v3 must play 0-cost BelieveInYou targeting ally {ally.Creature.CombatId}; chose [{FormatRoute(decision.CommittedActions)}] cands=[{cands}]");
    }

    private static void RunSoloEquivalence(CombatState combat, Player player, MainLoopContext loop, Action<bool, string> check)
    {
        check(combat.Players.Count == 1, "solo-equiv requires 1 player");
        ReplaceHand(combat, player, loop, ["Bash", "StrikeIronclad", "StrikeIronclad", "DefendIronclad"]);
        SetEnergy(player, 3);
        for (int i = 0; i < combat.Enemies.Count; i++)
        {
            Creature enemy = combat.Enemies[i];
            if (i == 0)
                loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(enemy, 16), TimeSpan.FromSeconds(15), "set lethal enemy hp");
            else
                loop.RunUntilCompleted(CreatureCmd.Kill(enemy, force: true), TimeSpan.FromSeconds(15), "remove extra enemy");
        }

        NewSolverConfig cfg = NewSolverConfig.For(SolverSpeed.Fast) with { Samples = 4, MaxParallelism = 2 };
        NewSolverDecision decision = NewFairSolver.DecideLive(combat, player, cfg, CancellationToken.None);
        check(decision.Stats.ImmediateLethalFound,
            $"v3 must find exact deterministic lethal (Bash+Strike = 8+9=17 >= 16); chose [{FormatRoute(decision.CommittedActions)}]");
        check(decision.CommittedActions.Count >= 2
            && decision.CommittedActions[0].CardId == "BASH"
            && decision.CommittedActions[1].CardId == "STRIKE_IRONCLAD",
            $"v3 must play Bash then Strike for exact lethal; chose [{FormatRoute(decision.CommittedActions)}]");
    }

    private static void RunDrawFirst(CombatState combat, Player player, MainLoopContext loop, Action<bool, string> check)
    {
        ReplaceHand(combat, player, loop, ["PommelStrike", "StrikeIronclad", "DefendIronclad"]);
        ReplaceDrawPile(combat, player, loop, ["ShrugItOff", "ShrugItOff", "TwinStrike", "TwinStrike"]);
        SetEnergy(player, 2);
        loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(player.Creature, 10), TimeSpan.FromSeconds(15), "set low hp");
        for (int i = 0; i < combat.Enemies.Count; i++)
        {
            Creature enemy = combat.Enemies[i];
            if (i == 0)
            {
                loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(enemy, 18), TimeSpan.FromSeconds(15), "set lethal-on-draw enemy hp");
                PowerModel str = ModelDb.AllPowers.Single(p => p.Id.Entry == "STRENGTH_POWER").ToMutable();
                str.Target = enemy;
                loop.RunUntilCompleted(PowerCmd.Apply(new BlockingPlayerChoiceContext(), str, enemy, 6, enemy, null), TimeSpan.FromSeconds(15), "apply str");
            }
            else
            {
                loop.RunUntilCompleted(CreatureCmd.Kill(enemy, force: true), TimeSpan.FromSeconds(15), "single enemy");
            }
        }
        NewSolverConfig cfg = NewSolverConfig.For(SolverSpeed.Normal) with { Samples = 8, MaxParallelism = 4 };
        NewSolverDecision decision = NewFairSolver.DecideLive(combat, player, cfg, CancellationToken.None);
        check(decision.CommittedActions.Count > 0 && decision.CommittedActions[0].CardId == "POMMEL_STRIKE",
            $"v3 must play PommelStrike first for value of information; chose [{FormatRoute(decision.CommittedActions)}]");
        NewSolverCandidate pommelFirst = decision.Candidates.First(c => c.Actions.Count > 0 && c.Actions[0].CardId == "POMMEL_STRIKE");
        NewSolverCandidate? bestOther = decision.Candidates.FirstOrDefault(c => c.Actions.Count == 0 || c.Actions[0].CardId != "POMMEL_STRIKE");
        check(bestOther == null || pommelFirst.CvarUtility > bestOther.CvarUtility + 1.0,
            $"PommelStrike first must win by strict expected value (>+1.0 HP), not tiebreak: pommel={pommelFirst.CvarUtility:F3} other={bestOther?.CvarUtility:F3}");
        Console.WriteLine($"DRAW_FIRST_EV pommel={pommelFirst.CvarUtility:F2} best_other={bestOther?.CvarUtility:F2}");
    }

    private static void RunTransfigureEarly(CombatState combat, Player player, MainLoopContext loop, Action<bool, string> check)
    {
        ReplaceHand(combat, player, loop, ["Transfigure", "Inflame", "Bludgeon", "IronWave", "StrikeIronclad", "DefendIronclad"]);
        SetEnergy(player, 3);
        for (int i = 0; i < combat.Enemies.Count; i++)
        {
            if (i == 0)
            {
                loop.RunUntilCompleted(CreatureCmd.SetMaxHp(combat.Enemies[i], 240), TimeSpan.FromSeconds(15), "set long fight max hp");
                loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(combat.Enemies[i], 240), TimeSpan.FromSeconds(15), "set long fight hp");
            }
            else
                loop.RunUntilCompleted(CreatureCmd.Kill(combat.Enemies[i], force: true), TimeSpan.FromSeconds(15), "single target");
        }
        NewSolverConfig cfg = NewSolverConfig.For(SolverSpeed.Normal) with { Samples = 8, MaxParallelism = 4 };
        NewSolverDecision decision = NewFairSolver.DecideLive(combat, player, cfg, CancellationToken.None);
        PlanAction? transfigure = decision.CommittedActions.FirstOrDefault(a => a.Kind == PlanActionKind.PlayCard && a.CardId == "TRANSFIGURE");
        bool targetedInflame = transfigure?.Choice?.Cards.Any(t => t.CardId == "INFLAME") == true;
        bool playedInflame = decision.CommittedActions.Any(a => a.Kind == PlanActionKind.PlayCard && a.CardId == "INFLAME");
        string topCands = string.Join(" | ", decision.Candidates.Take(14).Select(c => $"{FormatRoute(c.Actions)}({c.Actions.FirstOrDefault(a => a.CardId == "TRANSFIGURE")?.Choice?.Cards.FirstOrDefault()?.CardId})={c.CvarUtility:F2}"));
        check(targetedInflame && playedInflame,
            $"v3 must play Transfigure targeting Inflame and play Inflame in long fight; chose [{FormatRoute(decision.CommittedActions)}] choice={transfigure?.Choice?.Cards.FirstOrDefault()?.CardId} top=[{topCands}]");
    }

    private static void RunTransfigureLate(CombatState combat, Player player, MainLoopContext loop, Action<bool, string> check)
    {
        ReplaceHand(combat, player, loop, ["Transfigure", "Inflame", "StrikeIronclad", "StrikeIronclad", "DefendIronclad"]);
        SetEnergy(player, 3);
        for (int i = 0; i < combat.Enemies.Count; i++)
        {
            if (i == 0)
                loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(combat.Enemies[i], 12), TimeSpan.FromSeconds(15), "set late lethal hp");
            else
                loop.RunUntilCompleted(CreatureCmd.Kill(combat.Enemies[i], force: true), TimeSpan.FromSeconds(15), "single target");
        }
        NewSolverConfig cfg = NewSolverConfig.For(SolverSpeed.Normal) with { Samples = 8, MaxParallelism = 4 };
        NewSolverDecision decision = NewFairSolver.DecideLive(combat, player, cfg, CancellationToken.None);
        bool usedTransfigure = decision.CommittedActions.Any(a => a.Kind == PlanActionKind.PlayCard && a.CardId == "TRANSFIGURE");
        check(!usedTransfigure && decision.Stats.ImmediateLethalFound,
            $"v3 must skip Transfigure in late lethal fight; chose [{FormatRoute(decision.CommittedActions)}]");
    }


    private static void RunCoopNewMirrors(CombatState combat, Player player, MainLoopContext loop, Action<bool, string> check)
    {
        check(combat.Players.Count >= 2, $"coop-new-mirrors requires >=2 players, got {combat.Players.Count}");
        Player ally = combat.Players.First(p => !ReferenceEquals(p, player));
        ReplaceHand(combat, player, loop, ["ImitationLearning", "ImitationLearning"]);
        SetEnergy(player, 4);

        CombatRootSnapshot snap = CombatRootSnapshot.Capture(combat, fair: FairObservation.FromSeed(42));
        SimStepState simState = CombatStepEngine.CreateRoot(snap, 0);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        List<PlanAction> actions = CombatStepEngine.EnumerateBaseActions(simState, player, names, includePotions: false);

        PlanAction? imit = actions.FirstOrDefault(a => a.CardId == "IMITATION_LEARNING" && a.TargetCombatId == ally.Creature.CombatId);
        check(imit != null, $"ImitationLearning must enumerate ally target {ally.Creature.CombatId}");
        SimStepState afterImit = simState.Fork();
        check(CombatStepEngine.TryReplayAction(afterImit, imit!, player, out _, out _), "ImitationLearning first play must simulate cleanly");
        List<ImitationLearningPower> imitPowers1 = afterImit.Combat.EffectivePowers().OfType<ImitationLearningPower>()
            .Where(p => ReferenceEquals(p.Owner, player.Creature) && ReferenceEquals(p._playerTarget, ally)).ToList();
        check(imitPowers1.Count == 1 && imitPowers1[0].Amount == 2,
            $"ImitationLearning must apply 2 stacks targeting ally (count={imitPowers1.Count} amt={imitPowers1.FirstOrDefault()?.Amount})");
        List<PlanAction> actions2 = CombatStepEngine.EnumerateBaseActions(afterImit, player, names, includePotions: false);
        PlanAction? imit2 = actions2.FirstOrDefault(a => a.CardId == "IMITATION_LEARNING" && a.TargetCombatId == ally.Creature.CombatId);
        check(imit2 != null, "Second ImitationLearning must be enumerable");
        check(CombatStepEngine.TryReplayAction(afterImit, imit2!, player, out _, out _), "ImitationLearning second play must stack cleanly");
        int totalAmt = afterImit.Combat.EffectivePowers().OfType<ImitationLearningPower>()
            .Where(p => ReferenceEquals(p.Owner, player.Creature) && ReferenceEquals(p._playerTarget, ally))
            .Sum(p => p.Amount);
        check(totalAmt == 4, $"Second ImitationLearning on same ally must stack to 4 (got {totalAmt})");
    }

    private static void RunUnEndTurnAndLiveWiring(CombatState combat, Player player, MainLoopContext loop, Action<bool, string> check)
    {
        CoopEvalSuite.InstallTimeBypass();
        check(combat.Players.Count >= 2, $"un-end-turn requires >=2 players, got {combat.Players.Count}");
        Player ally = combat.Players.First(p => !ReferenceEquals(p, player));
        int turn = player.PlayerCombatState!.TurnNumber;

        ReplaceHand(combat, player, loop, ["Lift", "StrikeIronclad", "DefendIronclad"]);
        SetEnergy(player, 2);
        loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(ally.Creature, 5), TimeSpan.FromSeconds(15), "ally low hp");

        NewSolverConfig cfg = NewSolverConfig.For(SolverSpeed.Fast) with { Samples = 4, MaxParallelism = 2 };
        CombatRootSnapshot rootSnap = CombatRootSnapshot.Capture(combat, fair: FairObservation.FromSeed(1234));
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        NewSolverDecision liftDecision = NewFairSolver.DecideFromWorlds([rootSnap], names, 0, cfg, CancellationToken.None);
        BattleDamageSnapshot dmgSnap = BattleDamageTracker.Observe(combat);
        SearchPolicySnapshot policySnap = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: null);
        SolverResult adapted = NewSolverResultAdapter.ToSolverResult(
            liftDecision, rootSnap, 0, dmgSnap, policySnap, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.FromMilliseconds(5));
        check(adapted.TryValidateTurnOutcomes(out string? valErr), $"NewSolverResultAdapter validation failed: {valErr}");
        PlanAction? liftAct = adapted.BestNode.Actions.FirstOrDefault(a => a.CardId == "LIFT");
        check(liftAct != null && liftAct.TargetCombatId == ally.Creature.CombatId,
            $"Unified NewSolverResultAdapter must include Lift targeting ally {ally.Creature.CombatId}");

        Creature? resolvedAlly = SolverController.ResolveLiveActionTarget(combat, liftAct!);
        check(ReferenceEquals(resolvedAlly, ally.Creature), "ResolveLiveActionTarget must resolve living ally Creature");
        CardModel liveLift = SolverController.FindCardForDeployment([.. player.PlayerCombatState!.Hand.Cards], liftAct!);
        check(liveLift.CanPlayTargeting(resolvedAlly), "Live Lift card must be playable targeting resolved ally");
        int allyBlockBefore = ally.Creature.Block;
        Task<GameAction> playLift = SolverController.EnqueueAndCaptureActionAsync(
            c => c is PlayCardAction p && ReferenceEquals(p.NetCombatCard.ToCardModelOrNull(), liveLift),
            () => liveLift.TryManualPlay(resolvedAlly),
            CancellationToken.None);
        loop.RunUntilCompleted(playLift, TimeSpan.FromSeconds(30), "enqueue live lift");
        loop.RunUntilCompleted(playLift.GetAwaiter().GetResult().CompletionTask, TimeSpan.FromSeconds(30), "complete live lift");
        check(ally.Creature.Block > allyBlockBefore,
            $"Live Lift execution targeting ally must increase ally block ({allyBlockBefore} -> {ally.Creature.Block})");

        ReplaceHand(combat, player, loop, ["StrikeIronclad", "DefendIronclad"]);
        SetEnergy(player, 0);
        SolverController.BeginCombatForTesting(combat, fullAuto: true);

        EndPlayerTurnAction endTurn = new(player, turn);
        CombatManager.Instance.OnEndedTurnLocally();
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(endTurn);
        loop.RunUntilCompleted(endTurn.CompletionTask, TimeSpan.FromSeconds(30), "ready end turn");
        check(CombatManager.Instance.IsPlayerReadyToEndTurn(player), "Local player must be ready to end turn");
        check(SolverController.CanLocalPlayerUnEndTurn(player), "Local player must be allowed to un-end turn");

        Task<UnEndTurnPollOutcome> p1 = SolverController.TryPollAndUnEndTurnForTestingAsync(combat);
        loop.RunUntilCompleted(p1, TimeSpan.FromSeconds(30), "poll baseline");
        check(p1.GetAwaiter().GetResult() == UnEndTurnPollOutcome.BaselineRecorded, "First poll must record baseline");

        Task<UnEndTurnPollOutcome> p2 = SolverController.TryPollAndUnEndTurnForTestingAsync(combat);
        loop.RunUntilCompleted(p2, TimeSpan.FromSeconds(30), "poll unchanged");
        check(p2.GetAwaiter().GetResult() == UnEndTurnPollOutcome.Unchanged, "Second poll with no change must stay unchanged");
        check(CombatManager.Instance.IsPlayerReadyToEndTurn(player), "Unchanged poll must keep ready-to-end state");

        player.PlayerCombatState!.GainEnergy(2);
        string giftFp = SolverController.CaptureReadyToEndTurnFingerprint(combat, player);
        Task<UnEndTurnPollOutcome> p3 = SolverController.TryPollAndUnEndTurnForTestingAsync(combat);
        loop.RunUntilCompleted(p3, TimeSpan.FromSeconds(30), "poll energy gift");
        check(p3.GetAwaiter().GetResult() == UnEndTurnPollOutcome.Triggered, "Energy gift with playable hand must trigger UndoEndPlayerTurnAction");
        check(!CombatManager.Instance.IsPlayerReadyToEndTurn(player), "UndoEndPlayerTurnAction must clear ready-to-end state");
        check(!CombatManager.Instance.PlayerActionsDisabled, "UndoEndPlayerTurnAction must re-enable local player actions");
        check(SolverController.UnEndTurnCountForTesting == 1, $"Expected UnEndTurnCount == 1, got {SolverController.UnEndTurnCountForTesting}");

        EndPlayerTurnAction endTurn2 = new(player, turn);
        CombatManager.Instance.OnEndedTurnLocally();
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(endTurn2);
        loop.RunUntilCompleted(endTurn2.CompletionTask, TimeSpan.FromSeconds(30), "re-ready end turn");
        SetEnergy(player, 0);
        SolverController.RecordLocalReadyToEndTurnBaseline(combat, player, turn);
        player.PlayerCombatState!.GainEnergy(2);
        check(SolverController.CaptureReadyToEndTurnFingerprint(combat, player) == giftFp,
            "Restored identical gift state must produce identical fingerprint");
        Task<UnEndTurnPollOutcome> pDup = SolverController.TryPollAndUnEndTurnForTestingAsync(combat);
        loop.RunUntilCompleted(pDup, TimeSpan.FromSeconds(30), "poll duplicate fingerprint");
        check(pDup.GetAwaiter().GetResult() == UnEndTurnPollOutcome.GuardBlocked,
            "Anti-oscillation guard must block re-triggering on an already-evaluated fingerprint");
        check(CombatManager.Instance.IsPlayerReadyToEndTurn(player), "Blocked duplicate must remain ready-to-end");

        for (int g = 3; g <= 5; g++)
        {
            player.PlayerCombatState!.GainEnergy(1);
            Task<UnEndTurnPollOutcome> pGift = SolverController.TryPollAndUnEndTurnForTestingAsync(combat, (_, _) => true);
            loop.RunUntilCompleted(pGift, TimeSpan.FromSeconds(30), $"poll gift #{g}");
            check(pGift.GetAwaiter().GetResult() == UnEndTurnPollOutcome.Triggered, $"Distinct gift #{g} within cap must trigger");
            EndPlayerTurnAction reEnd = new(player, turn);
            CombatManager.Instance.OnEndedTurnLocally();
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(reEnd);
            loop.RunUntilCompleted(reEnd.CompletionTask, TimeSpan.FromSeconds(30), $"re-end #{g}");
            SolverController.RecordLocalReadyToEndTurnBaseline(combat, player, turn);
        }
        check(SolverController.UnEndTurnCountForTesting == SolverController.MaxUnEndTurnsPerTurn,
            $"UnEndTurnCount must reach MaxUnEndTurnsPerTurn ({SolverController.MaxUnEndTurnsPerTurn})");

        player.PlayerCombatState!.GainEnergy(1);
        Task<UnEndTurnPollOutcome> pCap = SolverController.TryPollAndUnEndTurnForTestingAsync(combat, (_, _) => true);
        loop.RunUntilCompleted(pCap, TimeSpan.FromSeconds(30), "poll over cap");
        check(pCap.GetAwaiter().GetResult() == UnEndTurnPollOutcome.GuardBlocked,
            "Anti-oscillation per-turn cap must block un-ending beyond MaxUnEndTurnsPerTurn");

        SolverController.ResetUnEndTurnStateForTesting();
        CombatManager.Instance.UndoReadyToEndTurn(player);
        CombatManager.Instance.SetReadyToEndTurn(player, canBackOut: false);
        Task<UnEndTurnPollOutcome> pBaseLocked = SolverController.TryPollAndUnEndTurnForTestingAsync(combat);
        loop.RunUntilCompleted(pBaseLocked, TimeSpan.FromSeconds(30), "baseline locked");
        player.PlayerCombatState!.GainEnergy(1);
        Task<UnEndTurnPollOutcome> pLocked = SolverController.TryPollAndUnEndTurnForTestingAsync(combat, (_, _) => true);
        loop.RunUntilCompleted(pLocked, TimeSpan.FromSeconds(30), "poll locked");
        check(pLocked.GetAwaiter().GetResult() == UnEndTurnPollOutcome.CannotUnEnd,
            "CannotUnEndTurn must prevent un-ending when canBackOut is false");
    }

    private static void ReplaceDrawPile(CombatState combat, Player player, MainLoopContext loop, IReadOnlyList<string> cardNames)
    {
        async Task DoReplace()
        {
            PlayerCombatState pcs = player.PlayerCombatState!;
            await CardPileCmd.RemoveFromCombat([.. pcs.DrawPile.Cards], true);
            foreach (string name in cardNames)
            {
                CardModel canonical = ModelDb.AllCards.Single(c => c.GetType().Name == name || c.Id.Entry == name);
                CardModel card = combat.CreateCard(canonical, player);
                await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Draw, player);
            }
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        loop.RunUntilCompleted(DoReplace(), TimeSpan.FromSeconds(30), "replace draw pile");
    }

    private static void ReplaceHand(CombatState combat, Player player, MainLoopContext loop, IReadOnlyList<string> cardNames)
    {
        async Task DoReplace()
        {
            PlayerCombatState pcs = player.PlayerCombatState!;
            await CardPileCmd.RemoveFromCombat([.. pcs.Hand.Cards], true);
            foreach (string name in cardNames)
            {
                CardModel canonical = ModelDb.AllCards.Single(c => c.GetType().Name == name || c.Id.Entry == name);
                CardModel card = combat.CreateCard(canonical, player);
                await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, player);
            }
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        loop.RunUntilCompleted(DoReplace(), TimeSpan.FromSeconds(30), "replace hand");
    }

    private static void AddCardsToHand(CombatState combat, Player player, MainLoopContext loop, IReadOnlyList<string> cardNames)
    {
        async Task DoAdd()
        {
            foreach (string name in cardNames)
            {
                CardModel canonical = ModelDb.AllCards.Single(c => c.GetType().Name == name || c.Id.Entry == name);
                CardModel card = combat.CreateCard(canonical, player);
                await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, player);
            }
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        loop.RunUntilCompleted(DoAdd(), TimeSpan.FromSeconds(30), "add cards");
    }

    private static void SetEnergy(Player player, int energy)
    {
        PlayerCombatState pcs = player.PlayerCombatState!;
        if (pcs.Energy > energy)
            pcs.LoseEnergy(pcs.Energy - energy);
        else if (pcs.Energy < energy)
            pcs.GainEnergy(energy - pcs.Energy);
    }

    private static void MutateHiddenState(CombatState combat, Player player)
    {
        List<CardModel> cards = player.PlayerCombatState!.DrawPile._cards;
        string original = string.Join(',', cards.Select(card => card.Id.Entry));
        for (int turn = 0; turn < cards.Count; turn++)
        {
            cards.Add(cards[0]);
            cards.RemoveAt(0);
            if (string.Join(',', cards.Select(card => card.Id.Entry)) != original)
                break;
        }
        ulong seed = 0xCAFEBABEUL;
        RunRngSet runRng = new("V3WORLDB" + combat.RunState.Rng.StringSeed);
        foreach (RunRngType type in Enum.GetValues<RunRngType>())
            runRng.GetRng(type).LoadFromSerializable(new Rng(seed++).ToSerializable());
        RunRngField.SetValue(combat.RunState, runRng);
        foreach (MonsterModel monster in combat.Enemies.Select(e => e.Monster).OfType<MonsterModel>())
        {
            monster._runRng = runRng;
            monster._rng = new Rng(seed++);
        }
        combat.Encounter!._rng = new Rng(seed++);
        PlayerRngSet playerRng = new(player.PlayerRng.Seed ^ 0xA5A5A5A5UL);
        foreach (PlayerRngType type in Enum.GetValues<PlayerRngType>())
            playerRng.GetRng(type).LoadFromSerializable(new Rng(seed++).ToSerializable());
        PlayerOddsSet odds = new(playerRng);
        OddsCurrentValue.SetValue(odds.PotionReward, 0.85f);
        OddsCurrentValue.SetValue(odds.CardRarity, odds.CardRarity.CurrentValue + 0.09f);
        player.PlayerRng = playerRng;
        player.PlayerOdds = odds;
    }

    private static string Signature(NewSolverDecision decision)
        => string.Join('|', decision.CommittedActions.Select(CombatStepEngine.ActionKey))
            + " :: " + string.Join(';', decision.Candidates.Take(6).Select(c =>
                $"{c.Key}:{c.CvarUtility:R}:{c.MeanUtility:R}:{c.DeathRate:R}:{c.EvaluatedWorlds}"));

    private static string FormatRoute(IReadOnlyList<PlanAction> actions)
        => string.Join(" -> ", actions.Select(a => $"{a.Kind}:{a.CardId}{a.PotionId}>{a.TargetCombatId}"));
}
