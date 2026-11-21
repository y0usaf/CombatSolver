using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private struct HistoryCourseCardMap
    {
        private Player? _singlePlayer;
        private PredictedCard? _singleCard;
        private Dictionary<Player, PredictedCard>? _multi;

        public readonly bool IsEmpty => _multi is null ? _singlePlayer is null : _multi.Count == 0;

        public void Set(Player player, PredictedCard card)
        {
            if (_multi is not null)
            {
                _multi[player] = card;
                return;
            }
            if (_singlePlayer is null || ReferenceEquals(_singlePlayer, player))
            {
                _singlePlayer = player;
                _singleCard = card;
                return;
            }
            _multi = new Dictionary<Player, PredictedCard>(2)
            {
                [_singlePlayer] = _singleCard!,
                [player] = card,
            };
            _singlePlayer = null;
            _singleCard = null;
        }

        public bool Remove(Player player, out PredictedCard? card)
        {
            if (_multi is not null)
                return _multi.Remove(player, out card);
            if (ReferenceEquals(_singlePlayer, player))
            {
                card = _singleCard;
                _singlePlayer = null;
                _singleCard = null;
                return true;
            }
            card = null;
            return false;
        }

        public void Remove(Player player) => Remove(player, out _);

        public readonly bool TryGetValue(Player player, out PredictedCard? card)
        {
            if (_multi is not null)
                return _multi.TryGetValue(player, out card);
            if (ReferenceEquals(_singlePlayer, player))
            {
                card = _singleCard;
                return true;
            }
            card = null;
            return false;
        }

        public readonly PredictedCard? GetValueOrDefault(Player player)
            => TryGetValue(player, out PredictedCard? card) ? card : null;

        public readonly HistoryCourseCardMap Fork(PredictionForkContext context)
        {
            if (_multi is not null)
            {
                if (_multi.Count == 0)
                    return default;
                Dictionary<Player, PredictedCard> result = new(_multi.Count);
                foreach ((Player player, PredictedCard card) in _multi)
                    result.Add(player, ForkCard(card, context));
                return new HistoryCourseCardMap { _multi = result };
            }
            if (_singlePlayer is null)
                return default;
            return new HistoryCourseCardMap
            {
                _singlePlayer = _singlePlayer,
                _singleCard = ForkCard(_singleCard!, context),
            };
        }

        public readonly void AppendFingerprint(ref StateFingerprintBuilder fingerprint, char marker)
        {
            if (_multi is not null)
            {
                if (_multi.Count == 0)
                    return;
                if (_multi.Count == 1)
                {
                    foreach ((Player player, PredictedCard card) in _multi)
                        AppendTrackedAttackEntry(ref fingerprint, marker, player, card);
                    return;
                }
                foreach ((Player player, PredictedCard card) in _multi.OrderBy(entry => entry.Key.NetId))
                    AppendTrackedAttackEntry(ref fingerprint, marker, player, card);
                return;
            }
            if (_singlePlayer is not null)
                AppendTrackedAttackEntry(ref fingerprint, marker, _singlePlayer, _singleCard!);
        }
    }

    private HistoryCourseCardMap _lastAttackThisTurn;
    private HistoryCourseCardMap _lastAttackPreviousTurn;

    private void RecordHistoryCourseAttack(PredictedCard card)
    {
        if (card.Preview.Type != CardType.Attack || card.Preview.IsDupe)
            return;
        _lastAttackThisTurn.Set(card.Preview.Owner, card);
    }

    public void CommitHistoryCourseTurn(Player player)
    {
        if (_lastAttackThisTurn.Remove(player, out PredictedCard? card))
            _lastAttackPreviousTurn.Set(player, card!);
        else
            _lastAttackPreviousTurn.Remove(player);
    }

    public bool TriggerScheduledAutoPlays(
        CombatPredictionSimulator simulator,
        Player player,
        int turnNumber,
        TurnStartChoiceCursor choices,
        ISet<uint> processedEnemyDeaths)
    {
        int mayhem = GetAmount<MegaCrit.Sts2.Core.Models.Powers.MayhemPower>(player.Creature);
        IReadOnlyList<PredictedCard> mayhemCards = simulator.MoveCardsForAutoPlay(
            player,
            mayhem,
            CardPilePosition.Top);
        if (HasPendingChoice)
        {
            simulator.AppendExecutionContinuation(new ScheduledAutoPlayFrame(player, turnNumber,
                processedEnemyDeaths, mayhemCards, 0));
            return true;
        }
        return ContinueScheduledAutoPlays(simulator, player, turnNumber, processedEnemyDeaths, mayhemCards, 0);
    }

    public bool TriggerWhisperingEarring(
        CombatPredictionSimulator simulator,
        Player player,
        int turnNumber,
        ISet<uint> processedEnemyDeaths)
    {
        if (turnNumber > 1)
            return true;

        foreach (WhisperingEarring relic in RelicsOf(player)
                     .OfType<WhisperingEarring>()
                     .Where(static relic => !relic.IsMelted))
        {
            TurnStartChoiceCursor vakuuChoices = TurnStartChoiceCursor.ForAutomaticPolicy(request =>
                request.Spec == null ? null : CardChoiceSupport.BuildVakuuChoice(request.Spec));
            TurnStartChoiceCursor previous = OverrideActionChoices(vakuuChoices);
            try
            {
                for (int cardsPlayed = 0; cardsPlayed < WhisperingEarring.maxCardsToPlay; cardsPlayed++)
                {
                    if (simulator.IsOverOrEnding)
                        break;
                    SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
                    PredictedCard? card = playerState.Hand.Cards
                        .FirstOrDefault(candidate => CanPlayCard(simulator, candidate));
                    if (card == null)
                        break;
                    Creature? target = simulator.GetTargetType(card) switch
                    {
                        TargetType.AnyEnemy => HittableEnemies.FirstOrDefault(),
                        TargetType.AnyAlly => simulator.Rng.CombatTargets.NextItem(
                            Allies.Where(candidate => candidate.IsPlayer
                                && !ReferenceEquals(candidate, player.Creature)
                                && simulator.State.GetCreature(candidate).IsAlive)),
                        TargetType.AnyPlayer => player.Creature,
                        _ => null,
                    };
                    // The overridden cursor answers every request with the fixed Vakuu policy, so the
                    // card's own selection resolves inside the auto-play like any other nested choice.
                    bool played = CardExecutionSupport.AutoPlay(
                            simulator,
                            this,
                            card,
                            target,
                            processedEnemyDeaths,
                            payResources: true,
                            nestedChoiceSourceId: card.Preview.Id.Entry);
                    if (HasPendingChoice)
                        return false;
                    if (!played)
                    {
                        break;
                    }

                    if (!CorePowerSupport.ApplyEnemyDeathPowers(
                            simulator,
                            this,
                            KnownEnemies,
                            processedEnemyDeaths))
                    {
                        return false;
                    }
                }
            }
            finally
            {
                RestoreActionChoices(vakuuChoices, previous);
            }
        }
        return true;
    }

    public bool AutoPlayWithChoice(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        string sourceId,
        string contextId,
        TurnStartChoiceCursor choices,
        ISet<uint> processedEnemyDeaths)
    {
        _ = choices;
        _ = CardExecutionSupport.AutoPlay(
            simulator,
            this,
            card,
            target: null,
            processedEnemyDeaths,
            nestedChoiceSourceId: sourceId,
            nestedChoiceContextId: contextId);
        return !HasPendingChoice;
    }

    private PredictedCard? GetPreviousTurnAttack(CombatPredictionSimulator simulator, Player player)
    {
        if (_rootMaterialized)
            return _lastAttackPreviousTurn.GetValueOrDefault(player);
        if (_lastAttackPreviousTurn.TryGetValue(player, out PredictedCard? predicted))
            return predicted;
        CardPlayFinishedEntry? live = _rootHistory.CardPlaysFinished.LastOrDefault(entry =>
            entry.CardPlay.Player == player
            && entry.HappenedLastPlayerTurn(player)
            && entry.CardPlay.Card.Type == CardType.Attack
            && !entry.CardPlay.Card.IsDupe);
        if (live == null)
            return null;
        predicted = simulator.State.FindCard(live.CardPlay.Card)
            ?? PredictedCard.FromGenerated(PredictionUtils.CloneCardStateForSimulation(live.CardPlay.Card));
        _lastAttackPreviousTurn.Set(player, predicted);
        return predicted;
    }

    private void AppendAutoPlayFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        _lastAttackThisTurn.AppendFingerprint(ref fingerprint, 't');
        _lastAttackPreviousTurn.AppendFingerprint(ref fingerprint, 'p');
    }

    private static void AppendTrackedAttackEntry(
        ref StateFingerprintBuilder fingerprint,
        char marker,
        Player player,
        PredictedCard card)
    {
        fingerprint.Add(marker);
        fingerprint.Add((long)player.NetId);
        fingerprint.Add(card.Preview.Id.Entry);
        fingerprint.Add(card.Preview.CurrentUpgradeLevel);
    }
}
