using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    public void RecordPoweredCardBlockGained(Creature owner)
        => SetCreatureTurnCounter(owner, CreatureTurnCounter.BlockCardsPlayed, GetBlockCardsPlayedThisTurn(owner) + 1);

    public void RecordCardPlayed(PredictedCard card)
    {
        RecordHistoryCourseAttack(card);
        Creature owner = card.Preview.Owner.Creature;
        if (card.Preview.Type == CardType.Attack)
        {
            SetCreatureTurnCounter(owner, CreatureTurnCounter.AttacksPlayed, GetAttacksPlayedThisTurn(owner) + 1);
            if (card.Preview.Tags.Contains(CardTag.Shiv))
                SetCreatureTurnCounter(owner, CreatureTurnCounter.ShivsPlayed, GetShivsPlayedThisTurn(owner) + 1);
        }
        ApplySlowOnCardPlayed(_allies);
        ApplySlowOnCardPlayed(_enemies);
    }

    private void ApplySlowOnCardPlayed(ForkableList<Creature> roster)
    {
        for (int i = 0; i < roster.Count; i++)
        {
            Creature creature = roster[i];
            SlowPower? slow = GetPower<SlowPower>(creature);
            if (slow == null || slow.Amount <= 0)
                continue;
            SlowPower mutable = (SlowPower)GetOrCreatePower(
                creature,
                CanonicalModels.Power<SlowPower>(),
                slow.Applier);
            mutable.DynamicVars["SlowAmount"].BaseValue++;
            mutable.DynamicVars["DisplayAmount"].BaseValue =
                mutable.DynamicVars["SlowAmount"].BaseValue * 10;
        }
    }

    public void InitializeFeralAfterApplied(
        CombatPredictionSimulator simulator,
        Creature owner)
    {
        FeralPower power = GetPower<FeralPower>(owner)
            ?? throw new InvalidOperationException("野性状态施加后未找到对应 Power。");
        simulator.StateStore
            .Get(power, () => new FeralPredictionState(power))
            .ZeroCostAttacksPlayed = GetZeroCostAttackStartsThisTurn(owner);
    }

    public void InitializeJugglingAfterApplied(
        CombatPredictionSimulator simulator,
        Creature owner)
    {
        JugglingPower power = GetPower<JugglingPower>(owner)
            ?? throw new InvalidOperationException("杂耍状态施加后未找到对应 Power。");
        simulator.StateStore
            .Get(power, () => new JugglingPredictionState(power))
            .AttacksPlayedThisTurn = GetAttacksPlayedThisTurn(owner);
    }

    public int GetAttacksPlayedThisTurn(Creature owner)
    {
        if (TryGetCreatureTurnCounter(owner, CreatureTurnCounter.AttacksPlayed, out int value))
            return value;
        value = _rootHistory.CardPlaysStarted.Count(entry =>
            entry.HappenedThisTurn(this)
            && entry.CardPlay.Card.Type == CardType.Attack
            && entry.CardPlay.Player.Creature == owner);
        SetCreatureTurnCounter(owner, CreatureTurnCounter.AttacksPlayed, value);
        return value;
    }

    public int GetShivsPlayedThisTurn(Creature owner)
    {
        if (TryGetCreatureTurnCounter(owner, CreatureTurnCounter.ShivsPlayed, out int value))
            return value;
        value = _rootHistory.CardPlaysFinished.Count(entry =>
            entry.HappenedThisTurn(this)
            && entry.CardPlay.Card.Tags.Contains(CardTag.Shiv)
            && entry.CardPlay.Player.Creature == owner);
        SetCreatureTurnCounter(owner, CreatureTurnCounter.ShivsPlayed, value);
        return value;
    }

    public int GetBlockCardsPlayedThisTurn(Creature owner)
    {
        if (TryGetCreatureTurnCounter(owner, CreatureTurnCounter.BlockCardsPlayed, out int value))
            return value;
        value = _rootHistory.BlockGained.Count(entry =>
            entry.HappenedThisTurn(this)
            && entry.CardPlay?.Player.Creature == owner
            && entry.Props.IsCardOrMonsterMove());
        SetCreatureTurnCounter(owner, CreatureTurnCounter.BlockCardsPlayed, value);
        return value;
    }
}
