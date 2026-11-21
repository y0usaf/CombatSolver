using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed class SimPlayerCombatState
{
    private readonly PlayerCombatState _liveState;
    private SimOrbQueue? _orbQueue;
    private SimCardPile? _hand;
    private SimCardPile? _drawPile;
    private SimCardPile? _discardPile;
    private SimCardPile? _exhaustPile;
    private SimCardPile? _playPile;
    private SimCardPile[]? _allPiles;
    private bool _materialized;

    public SimOrbQueue OrbQueue => _orbQueue ??= new SimOrbQueue(_liveState.OrbQueue);

    public SimCardPile Hand => _hand ??= _materialized ? new SimCardPile(PileType.Hand) : new SimCardPile(_liveState.Hand);

    public SimCardPile DrawPile => _drawPile ??= _materialized ? new SimCardPile(PileType.Draw) : new SimCardPile(_liveState.DrawPile);

    public SimCardPile DiscardPile => _discardPile ??= _materialized ? new SimCardPile(PileType.Discard) : new SimCardPile(_liveState.DiscardPile);

    public SimCardPile ExhaustPile => _exhaustPile ??= _materialized ? new SimCardPile(PileType.Exhaust) : new SimCardPile(_liveState.ExhaustPile);

    public SimCardPile PlayPile => _playPile ??= _materialized ? new SimCardPile(PileType.Play) : new SimCardPile(_liveState.PlayPile);

    public IReadOnlyList<SimCardPile> AllPiles
        => _allPiles ??= [Hand, DrawPile, DiscardPile, ExhaustPile, PlayPile];

    public AllCardsEnumerable AllCards => new(this);

    public readonly struct AllCardsEnumerable(SimPlayerCombatState state) : IEnumerable<PredictedCard>
    {
        private readonly SimPlayerCombatState _state = state;

        public Enumerator GetEnumerator() => new(_state);

        IEnumerator<PredictedCard> IEnumerable<PredictedCard>.GetEnumerator() => GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public struct Enumerator : IEnumerator<PredictedCard>
    {
        private readonly SimPlayerCombatState _state;
        private int _pileIndex;
        private List<PredictedCard>.Enumerator _cards;

        internal Enumerator(SimPlayerCombatState state)
        {
            _state = state;
            _pileIndex = -1;
            _cards = default;
        }

        public readonly PredictedCard Current => _cards.Current;

        readonly object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_pileIndex >= 0 && _cards.MoveNext())
                return true;
            while (++_pileIndex < 5)
            {
                SimCardPile? pile = _state.TryGetNonEmptyPileByEnumerationIndex(_pileIndex);
                if (pile is null)
                    continue;
                _cards = pile.GetEnumerator();
                if (_cards.MoveNext())
                    return true;
            }
            return false;
        }

        public readonly void Dispose()
        {
        }

        void System.Collections.IEnumerator.Reset() => throw new NotSupportedException();
    }

    public int Energy { get; private set; }

    public int Stars { get; private set; }
    public PlayerTurnPhase Phase { get; set; }
    public bool HooksActive { get; set; }

    public SimPlayerCombatState(PlayerCombatState liveState)
    {
        _liveState = liveState;
        Energy = liveState.Energy;
        Stars = liveState.Stars;
        Phase = liveState.Phase;
        HooksActive = liveState._player.IsActiveForHooks;
    }

    private SimPlayerCombatState(PlayerCombatState liveState, int energy, int stars)
    {
        _liveState = liveState;
        Energy = energy;
        Stars = stars;
    }

    public PredictedCard? FindCard(CardModel card)
    {
        foreach (PredictedCard predicted in AllCards)
        {
            if (predicted.References(card))
                return predicted;
        }
        return null;
    }

    internal SimCardPile? TryGetNonEmptyPileByEnumerationIndex(int index)
    {
        switch (index)
        {
            case 0:
                if (_hand is not null) return _hand.IsEmpty ? null : _hand;
                return _materialized || _liveState.Hand.Cards.Count == 0 ? null : Hand;
            case 1:
                if (_drawPile is not null) return _drawPile.IsEmpty ? null : _drawPile;
                return _materialized || _liveState.DrawPile.Cards.Count == 0 ? null : DrawPile;
            case 2:
                if (_discardPile is not null) return _discardPile.IsEmpty ? null : _discardPile;
                return _materialized || _liveState.DiscardPile.Cards.Count == 0 ? null : DiscardPile;
            case 3:
                if (_exhaustPile is not null) return _exhaustPile.IsEmpty ? null : _exhaustPile;
                return _materialized || _liveState.ExhaustPile.Cards.Count == 0 ? null : ExhaustPile;
            case 4:
                if (_playPile is not null) return _playPile.IsEmpty ? null : _playPile;
                return _materialized || _liveState.PlayPile.Cards.Count == 0 ? null : PlayPile;
            default:
                throw new ArgumentOutOfRangeException(nameof(index), index, "Unknown combat-pile index.");
        }
    }

    internal SimCardPile GetPileByEnumerationIndex(int index)
        => index switch
        {
            0 => Hand,
            1 => DrawPile,
            2 => DiscardPile,
            3 => ExhaustPile,
            4 => PlayPile,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "Unknown combat-pile index."),
        };

    public SimCardPile? GetCardPile(PileType type)
    {
        return type switch
        {
            PileType.None => null,
            PileType.Draw => DrawPile,
            PileType.Hand => Hand,
            PileType.Discard => DiscardPile,
            PileType.Exhaust => ExhaustPile,
            PileType.Play => PlayPile,
            PileType.Deck => throw new ArgumentOutOfRangeException(nameof(type), type, "Deck is not a combat pile."),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, $"Unknown pile type: {type}.")
        };
    }

    internal bool ContainsPile(SimCardPile pile)
        => ReferenceEquals(_hand, pile)
            || ReferenceEquals(_drawPile, pile)
            || ReferenceEquals(_discardPile, pile)
            || ReferenceEquals(_exhaustPile, pile)
            || ReferenceEquals(_playPile, pile);

    // Mirrors PlayerCombatState.GainEnergy.
    public void GainEnergy(decimal amount)
    {
        Energy = (int)Math.Clamp(Energy + amount, 0m, 999999999m);
    }

    // Mirrors PlayerCombatState.LoseEnergy.
    public void LoseEnergy(decimal amount)
    {
        Energy = (int)Math.Clamp(Energy - amount, 0m, 999999999m);
    }

    // Mirrors PlayerCombatState.GainStars.
    public void GainStars(decimal amount)
    {
        if (amount < 0m)
            throw new ArgumentException("Must not be negative.", nameof(amount));
        Stars = (int)Math.Clamp(Stars + amount, 0m, 999999999m);
    }

    // Mirrors PlayerCombatState.LoseStars.
    public void LoseStars(decimal amount)
    {
        Stars = (int)Math.Clamp(Stars - amount, 0m, 999999999m);
    }

    internal void MaterializeRoot()
    {
        _ = OrbQueue;
        foreach (PredictedCard card in AllCards)
            card.MaterializePreview();
        _materialized = true;
    }

    private SimCardPile? ForkPile(SimCardPile? pile, CardPile livePile, PredictionForkContext context)
    {
        if (pile is null)
            return null;
        if (pile.IsEmpty && (_materialized || livePile.Cards.Count == 0))
            return null;
        return pile.Fork(context);
    }

    internal SimPlayerCombatState Fork(PredictionForkContext context)
    {
        SimPlayerCombatState fork = new(_liveState, Energy, Stars)
        {
            Phase = Phase,
            HooksActive = HooksActive,
            _materialized = _materialized,
            _orbQueue = _orbQueue?.Fork(context),
            _hand = ForkPile(_hand, _liveState.Hand, context),
            _drawPile = ForkPile(_drawPile, _liveState.DrawPile, context),
            _discardPile = ForkPile(_discardPile, _liveState.DiscardPile, context),
            _exhaustPile = ForkPile(_exhaustPile, _liveState.ExhaustPile, context),
            _playPile = ForkPile(_playPile, _liveState.PlayPile, context)
        };
        context.Register(this, fork);
        return fork;
    }
}
