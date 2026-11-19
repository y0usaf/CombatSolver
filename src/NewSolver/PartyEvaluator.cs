using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver.NewSolver;

internal sealed record PartyRootBaseline(
    Player LocalPlayer,
    int StartTurnNumber,
    IReadOnlyDictionary<uint, int> InitialPlayerHp,
    IReadOnlyDictionary<uint, int> InitialPlayerMaxHp,
    IReadOnlyDictionary<uint, int> InitialTeammateEnergy,
    IReadOnlyDictionary<uint, int> InitialTeammateHandCount,
    IReadOnlyDictionary<uint, int> InitialTeammateDeckCount,
    IReadOnlyDictionary<int, int> PotionStrategicCosts,
    int InitialBrightestFlameMaxHpSpent,
    int TotalInitialEnemyHp,
    PostCombatRelicHealProfile LocalPostCombatHeal,
    bool IsActEndingBoss)
{
    public static PartyRootBaseline Capture(CombatRootSnapshot root, CombatPredictionSimulator sim)
    {
        SimulatedCombatState combat = (SimulatedCombatState)sim.State.CombatState;
        Dictionary<uint, int> hp = [];
        Dictionary<uint, int> maxHp = [];
        Dictionary<uint, int> energy = [];
        Dictionary<uint, int> hand = [];
        Dictionary<uint, int> deck = [];
        foreach (Player p in combat.Players)
        {
            uint id = p.Creature.CombatId ?? 0;
            SimCreatureState cs = sim.State.GetCreature(p.Creature);
            hp[id] = cs.CurrentHp;
            maxHp[id] = cs.MaxHp;
            SimPlayerCombatState ps = sim.State.GetPlayerCombatState(p);
            energy[id] = ps.Energy;
            hand[id] = ps.Hand.Cards.Count;
            deck[id] = ps.DrawPile.Cards.Count + ps.DiscardPile.Cards.Count + ps.Hand.Cards.Count;
        }
        Dictionary<int, int> potionCosts = [];
        foreach (SearchablePotionSlotSnapshot p in root.SearchablePotions)
            potionCosts[p.Slot] = p.StrategicHpCost;
        int enemyHp = 0;
        foreach (Creature e in combat.Enemies)
        {
            SimCreatureState es = sim.State.GetCreature(e);
            if (es.IsAlive)
                enemyHp += es.CurrentHp;
        }
        return new PartyRootBaseline(
            root.PlayerIdentity,
            root.StartTurnNumber,
            hp,
            maxHp,
            energy,
            hand,
            deck,
            potionCosts,
            root.InitialBrightestFlameMaxHpSpent,
            Math.Max(1, enemyHp),
            root.PostCombatRelicHeal,
            root.IsActEndingBoss);
    }
}

internal static class PartyEvaluator
{
    public const double LocalDeathPenalty = -250.0;
    public const double TeammateImmediateDeathPenalty = -160.0;
    public const double EnemyHpWeight = 0.27;

    public static double EvaluateTerminalOrHorizon(
        SimStepState stepState,
        PartyRootBaseline baseline,
        bool includePendingIncomingThreat)
    {
        CombatPredictionSimulator sim = stepState.Simulator;
        SimulatedCombatState combat = stepState.Combat;
        bool localDead = sim.State.GetCreature(baseline.LocalPlayer.Creature).IsDead;

        int remainingEnemyHp = 0;
        int remainingEnemyBlock = 0;
        int aliveEnemies = 0;
        foreach (Creature enemy in combat.Enemies)
        {
            SimCreatureState es = sim.State.GetCreature(enemy);
            int effHp = combat.EffectiveEnemyHp(enemy, es);
            if (es.IsAlive || effHp > 0)
            {
                aliveEnemies++;
                remainingEnemyHp += Math.Max(0, effHp);
                remainingEnemyBlock += Math.Max(0, es.Block);
            }
        }
        bool allEnemiesDead = aliveEnemies == 0 && !combat.BattlewornDummyTimedOut;

        int potionCost = ComputeSpentPotionCost(combat, baseline);
        double maxHpLossCost = Math.Max(0, combat.BrightestFlameMaxHpSpent - baseline.InitialBrightestFlameMaxHpSpent) * 2.5;
        double growthBonus = ComputeGrowthBonus(combat);
        double teammateSupport = stepState.TeammateSupportValue;
        int turnsElapsed = Math.Max(0, combat.GetPlayerTurnNumber(baseline.LocalPlayer) - baseline.StartTurnNumber);

        if (localDead || combat.BattlewornDummyTimedOut)
        {
            int localLost = baseline.InitialPlayerHp.GetValueOrDefault(baseline.LocalPlayer.Creature.CombatId ?? 0, 0);
            return LocalDeathPenalty - localLost - remainingEnemyHp * 0.4 - potionCost + teammateSupport;
        }

        if (allEnemiesDead)
        {
            double utility = 0;
            foreach (Player p in combat.Players)
            {
                SimCreatureState cs = sim.State.GetCreature(p.Creature);
                uint id = p.Creature.CombatId ?? 0;
                if (cs.IsDead)
                {
                    int startHpDead = baseline.InitialPlayerHp.GetValueOrDefault(id, 0);
                    utility += TeammateDeathPenaltyAtTurn(stepState, id, baseline.StartTurnNumber, startHpDead);
                    if (stepState.GetTeammateDeathTurn(id) > baseline.StartTurnNumber
                        && stepState.TryGetTeammatePostRootSnapshot(id, out int postHp, out int postOsty, out double postPower))
                    {
                        utility -= Math.Max(0, startHpDead - postHp) * 0.75;
                        utility += postOsty * 0.55 + postPower;
                    }
                    continue;
                }
                int startHp = baseline.InitialPlayerHp.GetValueOrDefault(id, cs.CurrentHp);
                int postHeal = EstimatePostCombatHeal(combat, p, cs.CurrentHp, cs.MaxHp, baseline);
                int effectiveEndHp = Math.Min(cs.MaxHp, cs.CurrentHp + postHeal);
                int netLost = startHp - effectiveEndHp;
                utility -= netLost;
            }
            return utility
                + teammateSupport
                - potionCost
                - maxHpLossCost
                + growthBonus
                - turnsElapsed * 0.02;
        }

        double expectedTurnsLeft = Math.Clamp(remainingEnemyHp / 18.0, 1.0, 6.0);
        double score = 0;

        foreach (Player p in combat.Players)
        {
            Creature c = p.Creature;
            SimCreatureState cs = sim.State.GetCreature(c);
            uint id = c.CombatId ?? 0;
            if (cs.IsDead)
            {
                if (ReferenceEquals(p, baseline.LocalPlayer))
                {
                    score += LocalDeathPenalty;
                }
                else
                {
                    int startHpDead = baseline.InitialPlayerHp.GetValueOrDefault(id, 0);
                    score += TeammateDeathPenaltyAtTurn(stepState, id, baseline.StartTurnNumber, startHpDead);
                    if (stepState.GetTeammateDeathTurn(id) > baseline.StartTurnNumber
                        && stepState.TryGetTeammatePostRootSnapshot(id, out int postHp, out int postOsty, out double postPower))
                    {
                        score -= Math.Max(0, startHpDead - postHp) * 0.75;
                        score += postOsty * 0.55 + postPower;
                    }
                }
                continue;
            }

            int startHp = baseline.InitialPlayerHp.GetValueOrDefault(id, cs.CurrentHp);
            int hpLost = startHp - cs.CurrentHp;
            double hpScale = 1.0;
            score -= hpLost * hpScale;
            if (cs.MaxHp > 0 && cs.CurrentHp * 100 < cs.MaxHp * 28)
                score -= (cs.MaxHp * 0.28 - cs.CurrentHp) * 0.35;

            if (combat.GetOsty(p) is { } osty)
            {
                SimCreatureState os = sim.State.GetCreature(osty);
                if (os.IsAlive)
                    score += os.CurrentHp * 0.55 + 0.8;
            }

            if (includePendingIncomingThreat)
            {
                int incoming = EstimateIncomingDamage(sim, combat, c);
                int effectiveShield = cs.Block;
                if (combat.GetOsty(p) is { } shieldOsty)
                {
                    SimCreatureState os = sim.State.GetCreature(shieldOsty);
                    if (os.IsAlive)
                        effectiveShield += os.CurrentHp;
                }
                int unblocked = Math.Max(0, incoming - effectiveShield);
                if (unblocked >= cs.CurrentHp)
                {
                    score += ReferenceEquals(p, baseline.LocalPlayer)
                        ? LocalDeathPenalty
                        : TeammateImmediateDeathPenalty;
                }
                else
                {
                    score -= unblocked * hpScale;
                }
            }
            else if (cs.Block > 0 && combat.GetAmount<BarricadePower>(c) + combat.GetAmount<BlurPower>(c) > 0)
            {
                score += cs.Block * 0.35;
            }

            score += EvaluatePlayerPowers(combat, c, expectedTurnsLeft);

            if (ReferenceEquals(p, baseline.LocalPlayer))
            {
                SimPlayerCombatState ps = sim.State.GetPlayerCombatState(p);
                score += EvaluateLocalResources(sim, combat, p, ps, expectedTurnsLeft);
            }
        }

        int minAliveEnemyHp = int.MaxValue;
        foreach (Creature enemy in combat.Enemies)
        {
            SimCreatureState es = sim.State.GetCreature(enemy);
            int effHp = combat.EffectiveEnemyHp(enemy, es);
            if (effHp > 0 && effHp < minAliveEnemyHp)
                minAliveEnemyHp = effHp;
        }

        score -= remainingEnemyHp * EnemyHpWeight;
        if (aliveEnemies > 1 && minAliveEnemyHp < int.MaxValue)
            score -= minAliveEnemyHp * 0.06;
        score -= remainingEnemyBlock * 0.04;
        score -= aliveEnemies * 1.1;
        score += teammateSupport;
        score += EvaluateEnemyDebuffs(sim, combat, expectedTurnsLeft);
        score -= potionCost;
        score -= maxHpLossCost;
        score += growthBonus;

        return score;
    }

    public static double EvaluateExactUtilityForAggregation(
        SimStepState stepState,
        PartyRootBaseline baseline)
        => EvaluateTerminalOrHorizon(stepState, baseline, includePendingIncomingThreat: false);

    public static double EvaluatePartyScaling(
        CombatPredictionSimulator sim,
        SimulatedCombatState combat,
        Player localPlayer,
        int remainingEnemyHp)
    {
        double turnsLeft = Math.Clamp(remainingEnemyHp / 18.0, 1.0, 6.0);
        double score = 0;
        foreach (Player p in combat.Players)
        {
            SimCreatureState cs = sim.State.GetCreature(p.Creature);
            if (cs.IsDead)
                continue;
            score += EvaluatePlayerPowers(combat, p.Creature, turnsLeft);
            if (ReferenceEquals(p, localPlayer))
                score += EvaluateLocalResources(sim, combat, p, sim.State.GetPlayerCombatState(p), turnsLeft);
        }
        return score;
    }

    public static double EvaluateTeammateBuffPowers(SimulatedCombatState combat, Creature c)
        => EvaluatePlayerPowers(combat, c, 3.0);

    private static double TeammateDeathPenaltyAtTurn(SimStepState state, uint combatId, int rootTurn, int startHp)
    {
        int deathTurn = state.GetTeammateDeathTurn(combatId);
        if (deathTurn <= rootTurn)
            return TeammateImmediateDeathPenalty;
        return -startHp - 24.0 / (1.0 + 0.5 * (deathTurn - rootTurn));
    }

    public static int EstimateIncomingDamage(
        CombatPredictionSimulator sim,
        SimulatedCombatState combat,
        Creature target)
    {
        SimCreatureState targetState = sim.State.GetCreature(target);
        if (targetState.IsDead)
            return 0;

        bool covered = combat.GetAmount<CoveredPower>(target) > 0;
        bool intangible = combat.GetAmount<IntangiblePower>(target) > 0;
        int buffers = combat.GetAmount<BufferPower>(target);
        bool targetVuln = combat.GetAmount<VulnerablePower>(target) > 0;
        int guardedCount = combat.EffectivePowers()
            .OfType<GuardedPower>()
            .Count(p => p.Amount > 0 && ReferenceEquals(p.Owner, target));
        bool isTank = combat.GetAmount<TankPower>(target) > 0;
        int interceptCovered = combat.GetAmount<InterceptPower>(target) > 0
            ? combat.EffectivePowers().OfType<CoveredPower>().Count(p => p.Amount > 0 && ReferenceEquals(p.Applier, target))
            : 0;

        int total = 0;
        foreach (Creature enemy in combat.Enemies)
        {
            if (!combat.CanPerformMonsterMove(sim, enemy))
                continue;
            ForecastMove move = combat.CurrentMonsterMove(enemy);
            if (move.AttackHits.Count == 0)
                continue;
            int enemyStr = combat.GetAmount<StrengthPower>(enemy);
            bool enemyWeak = combat.GetAmount<WeakPower>(enemy) > 0;
            foreach (ForecastAttackHit hit in move.AttackHits)
            {
                if (covered)
                    continue;
                int baseDmg = combat.AdjustMonsterMoveDamage(enemy, move.Move.Id, hit.BaseDamage);
                double dmg = Math.Max(0, baseDmg + enemyStr);
                if (enemyWeak)
                    dmg *= 0.75;
                if (targetVuln)
                    dmg *= 1.5;
                if (isTank)
                    dmg *= 1.5;
                for (int g = 0; g < guardedCount; g++)
                    dmg *= 0.5;
                if (interceptCovered > 0)
                    dmg *= (interceptCovered + 1);
                int hitDmg = (int)Math.Floor(dmg);
                if (intangible && hitDmg > 1)
                    hitDmg = 1;
                if (hitDmg > 0 && buffers > 0)
                {
                    buffers--;
                    hitDmg = 0;
                }
                total += hitDmg;
            }
        }
        return total;
    }

    private static double EvaluatePlayerPowers(SimulatedCombatState combat, Creature c, double turnsLeft)
    {
        double v = 0;
        double scale = Math.Min(turnsLeft, 4.5);
        foreach (PowerModel power in combat.EffectivePowers())
        {
            if (power.Amount == 0 || !ReferenceEquals(power.Owner, c))
                continue;
            int amt = power.Amount;
            v += power switch
            {
                StrengthPower => amt * 0.42 * scale,
                DexterityPower => amt * 0.45 * scale,
                FocusPower => amt * 0.60 * scale,
                VigorPower => amt * 0.25,
                StretchPower => amt * 0.25,
                PlatingPower => amt * 0.30 * scale,
                RegenPower => amt * 0.45 * Math.Min(turnsLeft, 3.0),
                BufferPower => amt * 3.5,
                IntangiblePower => amt * 5.0,
                BarricadePower => 1.5 * scale,
                BlurPower => amt * 1.0,
                ThornsPower => amt * 0.25 * scale,
                FlameBarrierPower => amt * 0.20,
                DemonFormPower => amt * 0.70 * scale,
                EchoFormPower => amt * 2.5 * scale,
                WraithFormPower => amt * 1.0,
                AfterimagePower => amt * 1.1 * scale,
                NoxiousFumesPower => amt * 0.50 * scale,
                FeelNoPainPower => amt * 0.35 * scale,
                DarkEmbracePower => amt * 1.4 * scale,
                CorruptionPower => 1.8 * scale,
                CreativeAiPower => amt * 1.2 * scale,
                DrawCardsNextTurnPower => amt * 0.85,
                EnergyNextTurnPower => amt * 1.5,
                BlockNextTurnPower => amt * 0.32,
                StarNextTurnPower => amt * 0.45,
                OneForAllPower => amt * 0.35 * scale,
                BeaconOfHopePower => 1.4 * scale,
                CompostPower => amt * 0.9 * scale,
                HammerTimePower => 1.3 * scale,
                CoveringFirePower => amt * 0.2 * scale,
                SneakyPower => amt * 0.45 * scale,
                SoulboundPower => 1.1 * scale,
                ConcoctPower => amt * 0.35,
                GuardedPower => 1.8 * scale,
                TankPower => -0.5 * scale,
                CoveredPower => 2.2,
                InterceptPower => -0.9,
                UnderworldPower => 1.2,
                ImitationLearningPower => amt * 0.85 * Math.Min(turnsLeft, 3.5),
                WeakPower => -amt * 0.4,
                VulnerablePower => -amt * 0.9,
                FrailPower => -amt * 0.55,
                PoisonPower => -amt * 0.5,
                _ => 0,
            };
        }
        return v;
    }

    private static double EvaluateLocalResources(
        CombatPredictionSimulator sim,
        SimulatedCombatState combat,
        Player player,
        SimPlayerCombatState ps,
        double turnsLeft)
    {
        double v = 0;
        v += ps.Stars * 0.42;

        foreach (OrbModel orb in ps.OrbQueue.Orbs)
        {
            int passive = Math.Max(0, (int)OrbMirrors.GetPassiveValue(sim, orb));
            int evoke = Math.Max(0, (int)OrbMirrors.GetEvokeValue(sim, orb));
            v += orb switch
            {
                FrostOrb => passive * 0.52 + 0.8,
                LightningOrb => passive * 0.36 + 0.5,
                DarkOrb => evoke * 0.24 + 0.6,
                PlasmaOrb => 2.2 * Math.Min(turnsLeft, 4.0),
                _ => 0.7,
            };
        }

        int sovereignDamage = 0;
        int maxEnergy = Math.Max(1, PersistentPowerSupport.GetModifiedMaxEnergy(combat, player));
        foreach (PredictedCard card in ps.AllCards)
        {
            if (ps.ExhaustPile.Cards.Contains(card))
                continue;
            if (card.Preview is SovereignBlade sb)
            {
                int dmg = sb.DynamicVars.Damage.IntValue;
                if (dmg > sovereignDamage)
                    sovereignDamage = dmg;
            }
            if (card.Preview.BaseReplayCount > 0)
            {
                int cost = card.Preview.EnergyCost.CostsX ? 0 : card.GetEnergyCostWithModifiers(sim, ps);
                if (cost <= maxEnergy)
                {
                    double replayWeight = card.Preview.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Power
                        ? 1.2 * Math.Min(turnsLeft, 4.5)
                        : 0.7 * Math.Min(turnsLeft, 3.0);
                    v += card.Preview.BaseReplayCount * replayWeight;
                }
            }
        }
        if (sovereignDamage > 10)
            v += (sovereignDamage - 10) * 0.14;

        return v;
    }

    private static double EvaluateEnemyDebuffs(
        CombatPredictionSimulator sim,
        SimulatedCombatState combat,
        double turnsLeft)
    {
        double v = 0;
        foreach (Creature enemy in combat.Enemies)
        {
            SimCreatureState es = sim.State.GetCreature(enemy);
            if (!es.IsAlive)
                continue;
            int poison = combat.GetAmount<PoisonPower>(enemy);
            if (poison > 0)
            {
                double ticks = Math.Min(turnsLeft, Math.Max(1.0, poison * 0.6));
                double poisonDmg = Math.Min(es.CurrentHp, poison * ticks);
                v += poisonDmg * 0.25;
            }
            int doom = combat.GetAmount<DoomPower>(enemy);
            if (doom > 0)
            {
                if (doom >= es.CurrentHp)
                    v += es.CurrentHp * 0.26 + 2.0;
                else
                    v += doom * 0.18;
            }
            int vuln = combat.GetAmount<VulnerablePower>(enemy);
            if (vuln > 0)
                v += Math.Min(vuln, 3) * Math.Min(es.CurrentHp, 40) * 0.025;
            int weak = combat.GetAmount<WeakPower>(enemy);
            if (weak > 0)
                v += Math.Min(weak, 3) * 0.9;
            int flanking = combat.GetAmount<FlankingPower>(enemy);
            if (flanking > 0)
                v += Math.Min(es.CurrentHp, 60) * 0.04;
            int str = combat.GetAmount<StrengthPower>(enemy);
            if (str != 0)
                v -= str * 0.65 * Math.Min(turnsLeft, 4.0);
        }
        return v;
    }

    private static int ComputeSpentPotionCost(SimulatedCombatState combat, PartyRootBaseline baseline)
    {
        int spent = 0;
        foreach (var (slot, cost) in baseline.PotionStrategicCosts)
        {
            if (!combat.IsPotionAvailable(baseline.LocalPlayer, slot))
                spent += cost;
        }
        return spent;
    }

    private static int EstimatePostCombatHeal(
        SimulatedCombatState combat,
        Player player,
        int currentHp,
        int maxHp,
        PartyRootBaseline baseline)
    {
        if (ReferenceEquals(player, baseline.LocalPlayer))
        {
            int heal = baseline.LocalPostCombatHeal.UnconditionalHeal;
            if (baseline.LocalPostCombatHeal.WoundedHeal > 0
                && currentHp * 100 <= maxHp * baseline.LocalPostCombatHeal.WoundedHpPercent)
            {
                heal += baseline.LocalPostCombatHeal.WoundedHeal;
            }
            return heal;
        }
        int total = 0;
        foreach (RelicModel relic in combat.RelicsOf(player))
        {
            if (relic.IsMelted)
                continue;
            if (relic is BurningBlood or BlackBlood)
                total += relic.DynamicVars.Heal.IntValue;
            else if (relic is MeatOnTheBone && currentHp * 2 <= maxHp)
                total += relic.DynamicVars.Heal.IntValue;
        }
        return total;
    }

    private static double ComputeGrowthBonus(SimulatedCombatState combat)
    {
        GrowthValues g = combat.GrowthRewards;
        return g.Feed * 4.0
            + g.HandOfGreed * 2.5
            + g.TheHunt * 4.0
            + g.Alchemize * 3.5
            + g.GeneticAlgorithm * 1.5
            + g.TheScythe * 1.5
            + g.Royalties * 2.0
            + g.Goopy * 1.0
            + g.ForbiddenGrimoire * 2.0
            + g.MadScience * 2.0
            + g.ShowOff * 1.0;
    }
}
