using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver.NewSolver;

internal static class NewSolverResultAdapter
{
    public static SolverResult ToSolverResult(
        NewSolverDecision decision,
        CombatRootSnapshot root,
        int knownTop,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        long allocatedBytes,
        int gen0,
        int gen1,
        int gen2,
        TimeSpan gcPause,
        TimeSpan elapsed)
    {
        int turn = root.StartTurnNumber;
        Player player = root.PlayerIdentity;
        IReadOnlyList<PlanAction> actions = decision.CommittedActions.Count > 0
            ? decision.CommittedActions
            : [new PlanAction(PlanActionKind.EndTurn, turn)];

        SimStepState simState = CombatStepEngine.CreateRoot(root, knownTop);
        SimulatedCombatState combat = simState.Combat;
        CombatPredictionSimulator sim = simState.Simulator;

        SimCreatureState startPlayer = sim.State.GetCreature(player.Creature);
        int startHp = startPlayer.CurrentHp;
        int maxHp = startPlayer.MaxHp;
        int startEnemyHp = TotalAliveEnemyHp(sim, combat);
        int maxBlock = startPlayer.Block;
        int actualBlock = startPlayer.Block;
        int energyLeft = sim.State.GetPlayerCombatState(player).Energy;

        Dictionary<int, int> potionCostsBySlot = [];
        foreach (SearchablePotionSlotSnapshot p in root.SearchablePotions)
            potionCostsBySlot[p.Slot] = p.StrategicHpCost;

        List<string> plannedPotionIds = [];
        List<PredictedPotionUse> potionUses = [];
        int explicitPotions = 0;
        int totalPotionStrategicCost = 0;
        Dictionary<int, IReadOnlyList<string>> killsAfterAction = [];
        HashSet<uint> aliveBefore = CaptureAliveEnemyIds(sim, combat);

        for (int i = 0; i < actions.Count; i++)
        {
            PlanAction action = actions[i];
            if (action.Kind == PlanActionKind.UsePotion && !string.IsNullOrEmpty(action.PotionId))
            {
                explicitPotions++;
                plannedPotionIds.Add(action.PotionId);
                int slotCost = potionCostsBySlot.GetValueOrDefault(action.PotionSlot);
                totalPotionStrategicCost += slotCost;
                potionUses.Add(new PredictedPotionUse(
                    action.PotionSlot,
                    action.PotionId,
                    slotCost,
                    Automatic: false));
            }
            if (action.Kind == PlanActionKind.EndTurn)
            {
                SimCreatureState preEnd = sim.State.GetCreature(player.Creature);
                actualBlock = preEnd.Block;
                maxBlock = Math.Max(maxBlock, preEnd.Block);
                energyLeft = sim.State.GetPlayerCombatState(player).Energy;
            }
            CombatStepEngine.TryReplayAction(simState, action, player, out _, out _);
            SimCreatureState afterPlayer = sim.State.GetCreature(player.Creature);
            maxBlock = Math.Max(maxBlock, afterPlayer.Block);
            if (action.Kind != PlanActionKind.EndTurn)
            {
                actualBlock = afterPlayer.Block;
                energyLeft = sim.State.GetPlayerCombatState(player).Energy;
            }
            List<string> newlyKilled = [];
            foreach (Creature enemy in combat.Enemies)
            {
                if (enemy.CombatId is uint cid && aliveBefore.Contains(cid) && !sim.State.GetCreature(enemy).IsAlive)
                {
                    aliveBefore.Remove(cid);
                    newlyKilled.Add(enemy.Monster?.Id.Entry ?? cid.ToString());
                }
            }
            if (newlyKilled.Count > 0)
                killsAfterAction[i] = newlyKilled;
        }

        SimCreatureState endPlayer = sim.State.GetCreature(player.Creature);
        bool playerDead = endPlayer.IsDead || combat.BattlewornDummyTimedOut;
        int endEnemyHp = TotalAliveEnemyHp(sim, combat);
        int aliveEnemyCount = CountAliveEnemies(sim, combat);
        bool allEnemiesDead = aliveEnemyCount == 0 && !combat.BattlewornDummyTimedOut;

        int turnHpLost = Math.Max(0, startHp - endPlayer.CurrentHp);
        int turnHpRecovered = Math.Max(0, endPlayer.CurrentHp - startHp);
        int turnEnemyHpLost = Math.Max(0, startEnemyHp - endEnemyHp);
        int postCombatHeal = allEnemiesDead ? EstimateDisplayPostCombatHeal(combat, player, endPlayer.CurrentHp, endPlayer.MaxHp, root) : 0;

        SimPlayerCombatState endPs = sim.State.GetPlayerCombatState(player);
        double leaderScore = decision.Candidates.Count > 0 ? decision.Candidates[0].CvarUtility : 0d;
        bool endsTurn = actions.Any(a => a.Kind == PlanActionKind.EndTurn || a.EndsPlayerTurn);
        int executableCount = actions.Count(a => a.IsExecutable);
        int commitLimit = endsTurn ? executableCount + 1 : Math.Max(1, executableCount);
        bool onlyDeathRoutes = decision.Candidates.Count > 0
            && decision.Candidates.All(c => c.DeathRate >= 1.0 - 1e-6);

        SolverSnapshot snapshot = new(
            HasRisk: playerDead || (decision.Candidates.Count > 0 && decision.Candidates[0].DeathRate > 0),
            PlayerDead: playerDead,
            AllEnemiesDead: allEnemiesDead,
            PlayerHp: endPlayer.CurrentHp,
            PlayerMaxHp: maxHp,
            CumulativePlayerHpLost: turnHpLost,
            RecoveredPlayerHp: turnHpRecovered,
            DeathSaveRelicHpRestored: 0,
            LongTermResourceValue: 0,
            AngerCopiesGenerated: 0,
            ProjectedPlayerHp: allEnemiesDead ? Math.Min(maxHp, endPlayer.CurrentHp + postCombatHeal) : endPlayer.CurrentHp,
            PlayerBlock: endPlayer.Block,
            EnemyHp: endEnemyHp,
            AliveEnemyCount: aliveEnemyCount,
            Energy: endPs.Energy,
            Stars: endPs.Stars,
            HandCount: endPs.Hand.Cards.Count,
            OutstandingStolenResource: 0,
            Turn: combat.GetPlayerTurnNumber(player),
            ShufflesCrossed: sim.ShuffleEventCount,
            BoundaryReason: SearchBoundaryReason.None,
            PredictionGaps: []);

        int totalNodes = decision.Stats.DeterministicNodesExpanded + decision.Stats.ContinuationNodesExpanded;
        Dictionary<int, int> hpLostByTurn = new() { [turn] = turnHpLost };
        Dictionary<int, int> hpRecoveredByTurn = new() { [turn] = turnHpRecovered };
        Dictionary<int, int> soldHpByTurn = new() { [turn] = 0 };
        Dictionary<int, int> enemyHpLostByTurn = new() { [turn] = turnEnemyHpLost };
        Dictionary<int, int> maxBlockByTurn = new() { [turn] = maxBlock };
        Dictionary<int, int> actualBlockByTurn = new() { [turn] = actualBlock };
        Dictionary<int, int> energyLeftByTurn = new() { [turn] = energyLeft };
        Dictionary<int, int> potionCountByTurn = new() { [turn] = explicitPotions };
        Dictionary<int, int> potionCostByTurn = new() { [turn] = totalPotionStrategicCost };

        SolverResult result = new()
        {
            FairCommitActionLimit = commitLimit,
            SingleSessionSearch = true,
            TotalSearchElapsed = elapsed,
            TotalWorkerAllocatedBytes = allocatedBytes,
            TotalGen0Collections = gen0,
            TotalGen1Collections = gen1,
            TotalGen2Collections = gen2,
            TotalGcPauseDuration = gcPause,
            TotalMaxObservedGcPause = gcPause,
            StartTurnNumber = turn,
            TurnSetupChoices = [],
            BestNode = new SelectedSearchPlan(actions, actions.Count, leaderScore),
            PotionUses = potionUses,
            Snapshot = snapshot,
            Forecast = root.Forecast,
            ExpandedNodes = totalNodes,
            TotalExpandedNodes = totalNodes,
            DominatedActionsPruned = 0,
            TopQueueActionsDropped = 0,
            ActionAdmissionRepresentativesProtected = 0,
            DuplicateCardBranchesPruned = 0,
            ChoiceBranchesEvaluated = 0,
            TotalChoiceBranchesEvaluated = 0,
            ShuffleBranchesPruned = 0,
            SoldHpBranchesPruned = 0,
            HpInvestmentBranchesProtected = 0,
            ReplayCount = 0,
            ForkCount = totalNodes,
            TransitionCount = totalNodes,
            TotalTransitionCount = totalNodes,
            ReusedNodeSnapshots = 0,
            TranspositionBranchesPruned = 0,
            RepeatableNoProgressBranchesPruned = 0,
            StandPatProbes = 0,
            TransitionCacheHits = 0,
            WorkerAllocatedBytes = allocatedBytes,
            Gen0Collections = gen0,
            Gen1Collections = gen1,
            Gen2Collections = gen2,
            GcPauseDuration = gcPause,
            MaxObservedGcPause = gcPause,
            WorkerYieldCount = 0,
            SearchedTurns = 1,
            BoundaryReason = SearchBoundaryReason.None,
            UnavoidableHpLost = turnHpLost,
            SoldHp = 0,
            FutureSoldHp = 0,
            BattleHpLostSoFar = battleDamage.HpLostSoFar,
            BattleHpRecoveredOrGainedSoFar = battleDamage.HpRecoveredOrGainedSoFar,
            ProjectedBattleHpLost = Math.Max(0, battleDamage.HpLostSoFar + turnHpLost - postCombatHeal),
            BattlePotionsUsedSoFar = battleDamage.PotionsUsedSoFar,
            BattlePotionIdsUsedSoFar = battleDamage.PotionIdsUsedSoFar,
            PlannedPotionIds = [.. plannedPotionIds],
            PotionRewardOutlook = root.PotionRewardOutlook,
            PotionCount = explicitPotions,
            ExplicitPotionCount = explicitPotions,
            PotionHpSaved = 0,
            PotionHpRequired = 0,
            PotionBranchesRejected = 0,
            TheftPolicy = policy.TheftPolicy,
            OutstandingStolenResource = 0,
            SoldHpByTurn = soldHpByTurn,
            HpLostByTurn = hpLostByTurn,
            HpRecoveredByTurn = hpRecoveredByTurn,
            PostCombatRelicHeal = postCombatHeal,
            EnemyHpLostByTurn = enemyHpLostByTurn,
            MaxBlockByTurn = maxBlockByTurn,
            ActualBlockByTurn = actualBlockByTurn,
            EnergyLeftByTurn = energyLeftByTurn,
            PotionCountByTurn = potionCountByTurn,
            PotionStrategicCostByTurn = potionCostByTurn,
            KillsAfterAction = killsAfterAction,
            CombatEndedTurn = allEnemiesDead ? turn : null,
            DeathTurn = playerDead ? turn : null,
            OnlyDeathRoutesFound = onlyDeathRoutes,
            IsActEndingBoss = root.IsActEndingBoss,
            BossHpRelief = root.BossHpRelief,
            Elapsed = elapsed,
            Continuations = [],
        };

        result.AssertCompleteTurnOutcomes();
        return result;
    }

    private static int TotalAliveEnemyHp(CombatPredictionSimulator sim, SimulatedCombatState combat)
    {
        int hp = 0;
        foreach (Creature enemy in combat.Enemies)
            hp += PartyEvaluator.KillableHp(combat, enemy, sim.State.GetCreature(enemy));
        return hp;
    }

    private static int CountAliveEnemies(CombatPredictionSimulator sim, SimulatedCombatState combat)
    {
        int count = 0;
        foreach (Creature enemy in combat.Enemies)
        {
            SimCreatureState es = sim.State.GetCreature(enemy);
            if (es.IsAlive || combat.EffectiveEnemyHp(enemy, es) > 0)
                count++;
        }
        return count;
    }

    private static HashSet<uint> CaptureAliveEnemyIds(CombatPredictionSimulator sim, SimulatedCombatState combat)
    {
        HashSet<uint> alive = [];
        foreach (Creature enemy in combat.Enemies)
        {
            if (enemy.CombatId is uint cid && sim.State.GetCreature(enemy).IsAlive)
                alive.Add(cid);
        }
        return alive;
    }

    private static int EstimateDisplayPostCombatHeal(
        SimulatedCombatState combat,
        Player player,
        int currentHp,
        int maxHp,
        CombatRootSnapshot root)
    {
        int heal = root.PostCombatRelicHeal.UnconditionalHeal;
        if (root.PostCombatRelicHeal.WoundedHeal > 0
            && currentHp * 100 <= maxHp * root.PostCombatRelicHeal.WoundedHpPercent)
        {
            heal += root.PostCombatRelicHeal.WoundedHeal;
        }
        if (heal == 0)
        {
            foreach (RelicModel relic in combat.RelicsOf(player))
            {
                if (relic.IsMelted)
                    continue;
                if (relic is BurningBlood or BlackBlood)
                    heal += relic.DynamicVars.Heal.IntValue;
                else if (relic is MeatOnTheBone && currentHp * 2 <= maxHp)
                    heal += relic.DynamicVars.Heal.IntValue;
            }
        }
        return heal;
    }
}
