using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static partial class CardOnPlaySupport
{
    private static void ApplyCoop(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard playedCard,
        Creature? target)
    {
        CardModel card = playedCard.Preview;
        Creature owner = card.Owner.Creature;
        switch (card)
        {
            case LegionOfBone:
            {
                int amount = card.DynamicVars.Summon.IntValue;
                foreach (Creature ally in combat.PlayerCreatures)
                {
                    if (ally != null && ally.Player != null && simulator.State.GetCreature(ally).IsAlive)
                        combat.SummonOsty(simulator, ally.Player, amount);
                }
                break;
            }
            case BelieveInYou when target?.Player is { } targetPlayer:
                simulator.GainEnergy(targetPlayer, card.DynamicVars.Energy.IntValue);
                break;
            case EnergySurge:
            {
                int amount = card.DynamicVars.Energy.IntValue;
                foreach (Creature ally in AlivePlayerTeammates(simulator, combat, owner))
                    simulator.GainEnergy(ally.Player!, amount);
                break;
            }
            case Coordinate when target != null:
                combat.ApplyTemporaryStrengthGain<CoordinatePower>(target, card.DynamicVars.Strength.IntValue, owner);
                break;
            case Fade when target != null:
                combat.ApplyTemporaryDexterity<FadePower>(target, card.DynamicVars.Dexterity.IntValue, owner);
                break;
            case Blaze when target != null:
                combat.Apply<StrengthPower>(target, card.DynamicVars.Strength.IntValue, owner);
                break;
            case Plot:
            {
                int count = card.DynamicVars.Cards.IntValue;
                foreach (Creature ally in AlivePlayerTeammates(simulator, combat, owner))
                    combat.Apply<DrawCardsNextTurnPower>(ally, count, owner);
                break;
            }
            case OneForAll:
            {
                int amount = card.DynamicVars["OneForAllPower"].IntValue;
                foreach (Player player in combat.Players)
                {
                    if (simulator.State.GetCreature(player.Creature).IsAlive)
                        combat.Apply<OneForAllPower>(player.Creature, amount, owner);
                }
                break;
            }
            case GlimpseBeyond:
            {
                int count = card.DynamicVars.Cards.IntValue;
                foreach (Creature ally in AlivePlayerTeammates(simulator, combat, owner))
                {
                    List<PredictedCard> souls = new(count);
                    for (int i = 0; i < count; i++)
                        souls.Add(PredictedCard.Create(CanonicalModels.Card<Soul>(), ally.Player!));
                    simulator.AddGeneratedCardsToCombat(
                        souls,
                        PileType.Draw,
                        ally.Player!,
                        CardPilePosition.Random,
                        CardGenerationResultKind.Fixed);
                    if (simulator.HasPendingChoice)
                        return;
                }
                break;
            }
            case BladeSymphony:
            {
                int count = card.DynamicVars.Cards.IntValue;
                foreach (Creature ally in AlivePlayerTeammates(simulator, combat, owner))
                {
                    for (int i = 0; i < count; i++)
                    {
                        PredictedCard shiv = PredictedCard.Create(CanonicalModels.Card<Shiv>(), ally.Player!);
                        simulator.AddGeneratedCardToCombat(
                            shiv,
                            PileType.Hand,
                            ally.Player!,
                            CardPilePosition.Bottom,
                            CardGenerationResultKind.Fixed);
                        if (simulator.HasPendingChoice)
                            return;
                    }
                }
                break;
            }
            case BeaconOfHope:
                combat.Apply<BeaconOfHopePower>(owner, 1, owner);
                break;
            case Flanking when target != null:
                combat.Apply<FlankingPower>(target, 2, owner);
                break;
            case Sneaky:
                combat.Apply<SneakyPower>(owner, card.DynamicVars["SneakyPower"].IntValue, owner);
                break;
            case Soulbound when target != null:
                combat.Apply<SoulboundPower>(target, 1, owner);
                break;
            case Underworld:
                combat.Apply<UnderworldPower>(owner, 1, owner);
                break;
            case Concoct when target != null:
                combat.Apply<ConcoctPower>(target, card.DynamicVars["ConcoctPower"].IntValue, owner);
                break;
            case Intercept when target != null:
                combat.AddPowerInstance<CoveredPower>(target, 1, owner);
                if (combat.GetAmount<InterceptPower>(owner) <= 0)
                    combat.Apply<InterceptPower>(owner, 1, target);
                break;
            case Tank:
                combat.Apply<TankPower>(owner, 1, owner);
                foreach (Creature ally in AlivePlayerTeammates(simulator, combat, owner))
                {
                    if (!ReferenceEquals(ally, owner))
                        combat.AddPowerInstance<GuardedPower>(ally, 1, owner);
                }
                break;
            case HammerTime:
                combat.Apply<HammerTimePower>(owner, 1, owner);
                break;
            case Hibernate:
                combat.Apply<HibernatePower>(owner, 1, owner);
                simulator.OrbChannel<FrostOrb>(card.Owner, card.DynamicVars.Repeat.IntValue);
                break;
            case Outrage:
            {
                foreach (Creature ally in AlivePlayerTeammates(simulator, combat, owner))
                {
                    PredictedCard clone = PredictedCard.Create(CanonicalModels.Card<Outrage>(), ally.Player!);
                    if (card.IsUpgraded)
                        clone.Upgrade();
                    simulator.AddGeneratedCardToCombat(
                        clone,
                        PileType.Discard,
                        ally.Player!,
                        CardPilePosition.Bottom,
                        CardGenerationResultKind.Fixed);
                    if (simulator.HasPendingChoice)
                        return;
                }
                break;
            }
            case ImitationLearning when target?.Player is { } targetPlayer:
            {
                int amount = card.DynamicVars["ImitationLearningPower"].IntValue;
                ImitationLearningPower? existing = combat.EffectivePowers()
                    .OfType<ImitationLearningPower>()
                    .FirstOrDefault(p => ReferenceEquals(p.Owner, owner) && ReferenceEquals(p._playerTarget, targetPlayer));
                if (existing != null)
                {
                    combat.SetPowerAmount(existing, existing.Amount + amount);
                }
                else
                {
                    ImitationLearningPower power = combat.AddPowerInstance<ImitationLearningPower>(owner, amount, owner);
                    power._playerTarget = targetPlayer;
                }
                break;
            }
        }
    }

    private static List<Creature> AlivePlayerTeammates(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature owner)
    {
        List<Creature> result = [];
        foreach (Creature candidate in combat.GetTeammatesOf(owner))
        {
            if (candidate != null && candidate.IsPlayer && candidate.Player != null
                && simulator.State.GetCreature(candidate).IsAlive)
            {
                result.Add(candidate);
            }
        }
        return result;
    }
}
