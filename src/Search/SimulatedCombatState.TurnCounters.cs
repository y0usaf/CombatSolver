using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private enum CreatureTurnCounter : byte
    {
        CardsPlayed = 0,
        ManualCardsPlayed = 1,
        AttacksPlayed = 2,
        ShivsPlayed = 3,
        BlockCardsPlayed = 4,
        SkillCardsPlayed = 5,
        CardsExhausted = 6,
        CardsDiscarded = 7,
        CreatureAttacks = 8,
        CardPlaySeriesStarted = 9,
        ZeroCostAttackStarts = 10,
        AttackPlayStarts = 11,
        CardPlayStarts = 12,
        AttackSkillStarts = 13,
    }

    private enum PlayerTurnCounter : byte
    {
        EnergySpent = 0,
        StarsGained = 1,
        NonHandDraws = 2,
        StatusCardsDrawn = 3,
    }

    private const ushort AllCreatureTurnCountersMask = (1 << 14) - 1;
    private const byte AllPlayerTurnCountersMask = (1 << 4) - 1;

    [InlineArray(14)]
    private struct CreatureCounterValues
    {
        private ushort _element0;
    }

    [InlineArray(4)]
    private struct PlayerCounterValues
    {
        private int _element0;
    }

    private struct CreatureTurnCounterBag : IEquatable<CreatureTurnCounterBag>
    {
        public ushort PresentMask;
        public CreatureCounterValues Values;

        public readonly bool TryGet(CreatureTurnCounter counter, out int value)
        {
            int idx = (int)counter;
            if ((PresentMask & (1 << idx)) != 0)
            {
                value = Values[idx];
                return true;
            }
            value = 0;
            return false;
        }

        public void Set(CreatureTurnCounter counter, int value)
        {
            int idx = (int)counter;
            PresentMask |= (ushort)(1 << idx);
            Values[idx] = (ushort)Math.Clamp(value, 0, ushort.MaxValue);
        }

        public readonly bool Equals(CreatureTurnCounterBag other)
        {
            if (PresentMask != other.PresentMask)
                return false;
            ushort mask = PresentMask;
            for (int i = 0; mask != 0; i++, mask >>= 1)
            {
                if ((mask & 1) != 0 && Values[i] != other.Values[i])
                    return false;
            }
            return true;
        }

        public override readonly bool Equals(object? obj)
            => obj is CreatureTurnCounterBag other && Equals(other);

        public override readonly int GetHashCode()
            => HashCode.Combine(PresentMask, Values[0], Values[2], Values[12]);
    }

    private struct PlayerTurnCounterBag : IEquatable<PlayerTurnCounterBag>
    {
        public byte PresentMask;
        public PlayerCounterValues Values;

        public readonly bool TryGet(PlayerTurnCounter counter, out int value)
        {
            int idx = (int)counter;
            if ((PresentMask & (1 << idx)) != 0)
            {
                value = Values[idx];
                return true;
            }
            value = 0;
            return false;
        }

        public void Set(PlayerTurnCounter counter, int value)
        {
            int idx = (int)counter;
            PresentMask |= (byte)(1 << idx);
            Values[idx] = value;
        }

        public readonly bool Equals(PlayerTurnCounterBag other)
        {
            if (PresentMask != other.PresentMask)
                return false;
            for (int i = 0; i < 4; i++)
            {
                if ((PresentMask & (1 << i)) != 0 && Values[i] != other.Values[i])
                    return false;
            }
            return true;
        }

        public override readonly bool Equals(object? obj)
            => obj is PlayerTurnCounterBag other && Equals(other);

        public override readonly int GetHashCode()
            => HashCode.Combine(PresentMask, Values[0], Values[1], Values[2], Values[3]);
    }

    private static readonly CreatureTurnCounterBag AllZeroCreatureTurnCounters =
        new() { PresentMask = AllCreatureTurnCountersMask };

    private static readonly PlayerTurnCounterBag AllZeroPlayerTurnCounters =
        new() { PresentMask = AllPlayerTurnCountersMask };

    private ForkableDictionary<Creature, CreatureTurnCounterBag>? _creatureTurnCounters;
    private ForkableDictionary<Player, PlayerTurnCounterBag>? _playerTurnCounters;

    private bool TryGetCreatureTurnCounter(Creature owner, CreatureTurnCounter counter, out int value)
    {
        if (_creatureTurnCounters?.TryGetValue(owner, out CreatureTurnCounterBag bag) == true)
            return bag.TryGet(counter, out value);
        value = 0;
        return false;
    }

    private void SetCreatureTurnCounter(Creature owner, CreatureTurnCounter counter, int value)
    {
        ForkableDictionary<Creature, CreatureTurnCounterBag> map = _creatureTurnCounters ??= [];
        map.TryGetValue(owner, out CreatureTurnCounterBag bag);
        bag.Set(counter, value);
        map[owner] = bag;
    }

    private void ResetCreatureTurnCounter(Creature owner, CreatureTurnCounter counter)
    {
        if (TryGetCreatureTurnCounter(owner, counter, out int current) && current == 0)
            return;
        SetCreatureTurnCounter(owner, counter, 0);
    }

    private void ResetAllCreatureTurnCounters(Creature owner)
    {
        if (_creatureTurnCounters?.TryGetValue(owner, out CreatureTurnCounterBag current) == true
            && current.Equals(AllZeroCreatureTurnCounters))
        {
            return;
        }
        (_creatureTurnCounters ??= [])[owner] = AllZeroCreatureTurnCounters;
    }

    private bool TryGetPlayerTurnCounter(Player player, PlayerTurnCounter counter, out int value)
    {
        if (_playerTurnCounters?.TryGetValue(player, out PlayerTurnCounterBag bag) == true)
            return bag.TryGet(counter, out value);
        value = 0;
        return false;
    }

    private void SetPlayerTurnCounter(Player player, PlayerTurnCounter counter, int value)
    {
        ForkableDictionary<Player, PlayerTurnCounterBag> map = _playerTurnCounters ??= [];
        map.TryGetValue(player, out PlayerTurnCounterBag bag);
        bag.Set(counter, value);
        map[player] = bag;
    }

    private void ResetAllPlayerTurnCounters(Player player)
    {
        if (_playerTurnCounters?.TryGetValue(player, out PlayerTurnCounterBag current) == true
            && current.Equals(AllZeroPlayerTurnCounters))
        {
            return;
        }
        (_playerTurnCounters ??= [])[player] = AllZeroPlayerTurnCounters;
    }

    private void AddCreatureTurnCounterMap(
        ref StateFingerprintBuilder fingerprint,
        char marker,
        CreatureTurnCounter counter)
    {
        ulong first = 0;
        ulong second = 0;
        int count = 0;
        if (_creatureTurnCounters != null)
        {
            foreach ((Creature creature, CreatureTurnCounterBag bag) in _creatureTurnCounters)
            {
                if (!bag.TryGet(counter, out int value))
                    continue;
                StateFingerprintBuilder item = new();
                item.Add(creature.CombatId ?? uint.MaxValue);
                item.Add(value);
                AddUnorderedItem(item.Finish(), ref first, ref second);
                count++;
            }
        }
        AddUnordered(ref fingerprint, marker, count, first, second);
    }

    private void AddPlayerTurnCounterMap(
        ref StateFingerprintBuilder fingerprint,
        char marker,
        PlayerTurnCounter counter)
    {
        ulong first = 0;
        ulong second = 0;
        int count = 0;
        if (_playerTurnCounters != null)
        {
            foreach ((Player player, PlayerTurnCounterBag bag) in _playerTurnCounters)
            {
                if (!bag.TryGet(counter, out int value))
                    continue;
                StateFingerprintBuilder item = new();
                item.Add(player.NetId);
                item.Add(value);
                AddUnorderedItem(item.Finish(), ref first, ref second);
                count++;
            }
        }
        AddUnordered(ref fingerprint, marker, count, first, second);
    }
}
