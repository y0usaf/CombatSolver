using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver.NewSolver;

internal static class NewFairSolver
{
    public static readonly NewSolverConfig QuickUnEndCheckConfig = new(
        Samples: 4,
        PilotSamples: 4,
        Stage2Survivors: 3,
        DeterministicNodeCap: 96,
        MaxEndTurnCandidates: 6,
        MaxRevealCandidates: 4,
        LookaheadTurns: 4,
        ContinuationTurnWidth: 2,
        ContinuationNodeCap: 24,
        MaxChoiceBranches: 3,
        SoftTimeBudgetMs: 400);

    public static ulong VisibleSeed(CombatState state, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(state, fair: FairObservation.FromSeed(0));
        string visible = ContinuationStamp.CapturePredicted(
            player, root.ForkSimulator(), root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        ulong hash = 14695981039346656037UL;
        foreach (char ch in visible)
            hash = (hash ^ ch) * 1099511628211UL;
        return hash;
    }

    public static NewSolverDecision DecideLive(
        CombatState combat,
        Player player,
        NewSolverConfig config,
        CancellationToken token)
    {
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        ulong seed = VisibleSeed(combat, player);
        int knownTop = FairDrawPileKnowledge.Capture(player.PlayerCombatState!.DrawPile).KnownTop;
        bool forcedEnd = IsForcedEndTurnLive(player);
        int sampleCount = forcedEnd ? 1 : Math.Max(1, config.Samples);
        CombatRootSnapshot[] worlds = new CombatRootSnapshot[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            worlds[i] = CombatRootSnapshot.Capture(combat, fair: FairObservation.FromSeed(seed + (ulong)i));
        return InformationSetSearch.Decide(worlds, names, knownTop, config, token);
    }

    public static NewSolverDecision DecideFromWorlds(
        IReadOnlyList<CombatRootSnapshot> worlds,
        SolverDisplayNames names,
        int knownTop,
        NewSolverConfig config,
        CancellationToken token)
        => InformationSetSearch.Decide(worlds, names, knownTop, config, token);

    public static bool HasBeneficialAction(
        IReadOnlyList<CombatRootSnapshot> worlds,
        SolverDisplayNames names,
        int knownTop,
        CancellationToken token)
    {
        NewSolverDecision decision = InformationSetSearch.Decide(worlds, names, knownTop, QuickUnEndCheckConfig, token);
        return decision.CommittedActions.Any(action => action.Kind != PlanActionKind.EndTurn);
    }

    public static bool IsForcedEndTurnLive(Player player)
        => player.PlayerCombatState is { } combat
            && combat.Hand.Cards.All(card => !card.CanPlay())
            && Enumerable.Range(0, player.PotionSlots.Count).All(slot => player.GetPotionAtSlotIndex(slot) == null);
}
