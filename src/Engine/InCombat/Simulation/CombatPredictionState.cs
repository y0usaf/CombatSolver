using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed class CombatPredictionState
{
    public ICombatState CombatState { get; }

    private const int SmallCreatureLimit = 8;

    private SimCreatureState[]? _smallCreatures;
    private int _smallCreatureCount;
    private Dictionary<Creature, SimCreatureState>? _creaturesDict;

    // 绝大多数分叉里没有任何生物被移出预测名册。惰性建表让 Fork 少一次空 HashSet 分配。
    private HashSet<Creature>? _removedCreatures;

    private Player? _singlePlayer;
    private SimPlayerCombatState? _singlePlayerState;
    private Dictionary<Player, SimPlayerCombatState>? _playerCombatStates;
    private HittableEnemyView? _hittableEnemies;

    private sealed class HittableEnemyView(CombatPredictionState owner) : IReadOnlyList<Creature>
    {
        public int Count
        {
            get
            {
                int count = 0;
                IReadOnlyList<Creature> enemies = owner.CombatState.Enemies;
                for (int i = 0; i < enemies.Count; i++)
                {
                    if (owner.IsHittable(enemies[i]))
                        count++;
                }
                return count;
            }
        }

        public Creature this[int index]
        {
            get
            {
                IReadOnlyList<Creature> enemies = owner.CombatState.Enemies;
                for (int i = 0; i < enemies.Count; i++)
                {
                    Creature enemy = enemies[i];
                    if (!owner.IsHittable(enemy))
                        continue;
                    if (index-- == 0)
                        return enemy;
                }
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public IEnumerator<Creature> GetEnumerator()
        {
            IReadOnlyList<Creature> enemies = owner.CombatState.Enemies;
            for (int i = 0; i < enemies.Count; i++)
            {
                Creature enemy = enemies[i];
                if (owner.IsHittable(enemy))
                    yield return enemy;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public CombatPredictionState(ICombatState combatState)
    {
        CombatState = combatState;
        if (combatState is ICombatPredictionStateOwner owner)
            owner.AttachPredictionState(this);
    }

    private CombatPredictionState(
        ICombatState combatState,
        SimCreatureState[]? smallCreatures,
        int smallCreatureCount,
        Dictionary<Creature, SimCreatureState>? creaturesDict,
        HashSet<Creature>? removedCreatures,
        Player? singlePlayer,
        SimPlayerCombatState? singlePlayerState,
        Dictionary<Player, SimPlayerCombatState>? playerCombatStates)
    {
        CombatState = combatState;
        _smallCreatures = smallCreatures;
        _smallCreatureCount = smallCreatureCount;
        _creaturesDict = creaturesDict;
        _removedCreatures = removedCreatures;
        _singlePlayer = singlePlayer;
        _singlePlayerState = singlePlayerState;
        _playerCombatStates = playerCombatStates;
        if (combatState is ICombatPredictionStateOwner owner)
            owner.AttachPredictionState(this);
    }

    public IReadOnlyList<Creature> Allies => !RequiresRemovedCreatureFiltering
        ? CombatState.Allies
        : [.. ExcludeRemoved(CombatState.Allies)];

    public IReadOnlyList<Creature> Enemies => !RequiresRemovedCreatureFiltering
        ? CombatState.Enemies
        : [.. ExcludeRemoved(CombatState.Enemies)];

    public IReadOnlyList<Creature> Creatures => !RequiresRemovedCreatureFiltering
        ? CombatState.Creatures
        : [.. ExcludeRemoved(CombatState.Creatures)];

    // SimulatedCombatState updates its allies/enemies rosters, but PlayerCreatures is an
    // immutable root projection and can still contain a removed player-side summon.
    public IReadOnlyList<Creature> PlayerCreatures => _removedCreatures is not { Count: > 0 }
        ? CombatState.PlayerCreatures
        : [.. ExcludeRemoved(CombatState.PlayerCreatures)];

    public IReadOnlyList<Player> Players => CombatState.Players;

    public IReadOnlyList<Creature> HittableEnemies => _hittableEnemies ??= new HittableEnemyView(this);

    public SimCreatureState GetCreature(Creature creature)
    {
        if (_creaturesDict is { } dict)
        {
            if (!dict.TryGetValue(creature, out var dictState))
            {
                if (CombatState is ICombatPredictionRootCaptureBoundary boundary)
                    boundary.AssertCanCaptureCreature(creature);
                dictState = new SimCreatureState(creature);
                dict.Add(creature, dictState);
            }
            return dictState;
        }
        SimCreatureState[]? small = _smallCreatures;
        int count = _smallCreatureCount;
        if (small is not null)
        {
            for (int i = 0; i < count; i++)
            {
                if (ReferenceEquals(small[i].Creature, creature))
                    return small[i];
            }
        }
        if (CombatState is ICombatPredictionRootCaptureBoundary rootBoundary)
            rootBoundary.AssertCanCaptureCreature(creature);
        var state = new SimCreatureState(creature);
        if (count < SmallCreatureLimit)
        {
            if (small is null)
            {
                small = new SimCreatureState[4];
                _smallCreatures = small;
            }
            else if (count >= small.Length)
            {
                Array.Resize(ref small, SmallCreatureLimit);
                _smallCreatures = small;
            }
            small[count] = state;
            _smallCreatureCount = count + 1;
        }
        else
        {
            var newDict = new Dictionary<Creature, SimCreatureState>(count + 1);
            for (int i = 0; i < count; i++)
                newDict.Add(small![i].Creature, small[i]);
            newDict.Add(creature, state);
            _creaturesDict = newDict;
            _smallCreatures = null;
            _smallCreatureCount = 0;
        }
        return state;
    }

    public Creature? GetOsty(Player player)
        => CombatState is ICombatPredictionPetState pets
            ? pets.GetOsty(player)
            : player.Osty;

    public bool IsHittable(Creature creature)
    {
        if (_removedCreatures?.Contains(creature) == true || !GetCreature(creature).IsAlive)
            return false;
        return CombatState is ICombatPredictionCreatureSemantics semantics
            ? semantics.IsHittable(creature)
            : Hook.ShouldAllowHitting(CombatState, creature);
    }

    /// <summary>
    /// 实机 <c>Creature.CanReceivePowers</c> 里的「个体仍在战斗里」那一半。
    /// </summary>
    /// <remarks>
    /// 实机 <c>CreatureCmd.Kill</c> 在击杀当时就 <c>combatState.RemoveCreature</c>
    /// （<c>CombatState = null</c>，只放过正在执行自己行动的怪物），于是同一动作里后续对它施加
    /// Power 是空操作。预测侧同一处也走 <see cref="RemoveCreature"/>，所以「有没有被移除」
    /// 就是这条判据的等价物；它换来的是「死亡效果被推迟到 ApplyEnemyDeathPowers」那个窗口里的
    /// 正确行为，见 <c>SimulatedCombatState.CanReceivePredictedPowers</c>。
    /// </remarks>
    public bool IsAttachedToCombat(Creature creature)
        => _removedCreatures?.Contains(creature) != true;

    public IReadOnlyList<Creature> GetOpponentsOf(Creature creature)
        => !RequiresRemovedCreatureFiltering
            ? CombatState.GetOpponentsOf(creature)
            : [.. ExcludeRemoved(CombatState.GetOpponentsOf(creature))];

    public IReadOnlyList<Creature> GetTeammatesOf(Creature creature)
        => !RequiresRemovedCreatureFiltering
            ? CombatState.GetTeammatesOf(creature)
            : [.. ExcludeRemoved(CombatState.GetTeammatesOf(creature))];

    public IReadOnlyList<Creature> GetCreaturesOnSide(CombatSide side)
        => !RequiresRemovedCreatureFiltering
            ? CombatState.GetCreaturesOnSide(side)
            : [.. ExcludeRemoved(CombatState.GetCreaturesOnSide(side))];

    // A prediction roster sink has already removed the creature from its mutable side rosters.
    // Filtering those views again used to rebuild LINQ iterators and arrays on every read after
    // the first death, which dominates long multi-enemy searches. Keep the fallback for combat
    // state implementations that cannot update their own roster.
    private bool RequiresRemovedCreatureFiltering
        => _removedCreatures is { Count: > 0 } && CombatState is not ICombatPredictionRosterSink;

    public IReadOnlyList<AbstractModel> IterateHookListeners()
    {
        if (CombatState is ICombatPredictionHookListenerSource source)
            return source.HookListeners;
        return CombatState.IterateHookListeners().ToArray();
    }

    public SimPlayerCombatState GetPlayerCombatState(Player player)
    {
        if (_playerCombatStates is { } dict)
        {
            if (!dict.TryGetValue(player, out var dictState))
            {
                if (CombatState is ICombatPredictionRootCaptureBoundary boundary)
                    boundary.AssertCanCapturePlayer(player);
                var liveState = player.PlayerCombatState
                    ?? throw new InvalidOperationException($"Player {player.Creature.Name} has no combat state to simulate.");
                dictState = new SimPlayerCombatState(liveState);
                dict.Add(player, dictState);
            }
            return dictState;
        }
        if (ReferenceEquals(_singlePlayer, player))
            return _singlePlayerState!;
        if (CombatState is ICombatPredictionRootCaptureBoundary rootBoundary)
            rootBoundary.AssertCanCapturePlayer(player);
        var live = player.PlayerCombatState
            ?? throw new InvalidOperationException($"Player {player.Creature.Name} has no combat state to simulate.");
        var state = new SimPlayerCombatState(live);
        if (_singlePlayer is null)
        {
            _singlePlayer = player;
            _singlePlayerState = state;
        }
        else
        {
            _playerCombatStates = new Dictionary<Player, SimPlayerCombatState>(2)
            {
                [_singlePlayer] = _singlePlayerState!,
                [player] = state,
            };
            _singlePlayer = null;
            _singlePlayerState = null;
        }
        return state;
    }

    public void RemoveCreature(Creature creature)
    {
        if (Creatures.Contains(creature))
        {
            (_removedCreatures ??= []).Add(creature);
            if (CombatState is ICombatPredictionRosterSink roster)
                roster.RemoveCreatureFromPrediction(creature);
        }
    }

    public PredictedCard? FindCard(CardModel card)
    {
        return GetPlayerCombatState(card.Owner).FindCard(card);
    }

    internal void MaterializeRoot()
    {
        foreach (Creature creature in CombatState.Creatures)
            _ = GetCreature(creature);
        foreach (Player player in CombatState.Players)
        {
            if (player.Osty is { } osty)
                _ = GetCreature(osty);
            GetPlayerCombatState(player).MaterializeRoot();
        }
    }

    private IEnumerable<Creature> ExcludeRemoved(IEnumerable<Creature> creatures)
    {
        return creatures.Where(creature => _removedCreatures?.Contains(creature) != true);
    }

    internal CombatPredictionState Fork(PredictionForkContext context)
    {
        SimCreatureState[]? smallCreatures = null;
        int smallCreatureCount = _smallCreatureCount;
        Dictionary<Creature, SimCreatureState>? creaturesDict = null;
        if (_creaturesDict is not null)
        {
            creaturesDict = new Dictionary<Creature, SimCreatureState>(_creaturesDict.Count);
            foreach ((Creature creature, SimCreatureState state) in _creaturesDict)
                creaturesDict.Add(creature, state.Fork(context));
        }
        else if (_smallCreatures is not null && smallCreatureCount > 0)
        {
            smallCreatures = new SimCreatureState[smallCreatureCount];
            for (int i = 0; i < smallCreatureCount; i++)
                smallCreatures[i] = _smallCreatures[i].Fork(context);
        }

        Player? singlePlayer = null;
        SimPlayerCombatState? singlePlayerState = null;
        Dictionary<Player, SimPlayerCombatState>? players = null;
        if (_playerCombatStates is not null)
        {
            players = new Dictionary<Player, SimPlayerCombatState>(_playerCombatStates.Count);
            foreach ((Player player, SimPlayerCombatState state) in _playerCombatStates)
                players.Add(player, state.Fork(context));
        }
        else if (_singlePlayer is not null)
        {
            singlePlayer = _singlePlayer;
            singlePlayerState = _singlePlayerState!.Fork(context);
        }

        if (CombatState is not ICombatPredictionForkableState forkableCombatState)
        {
            throw new InvalidOperationException(
                $"Combat state {CombatState.GetType().FullName} does not implement " +
                $"{nameof(ICombatPredictionForkableState)}.");
        }
        ICombatState combatState = forkableCombatState.Fork(context);
        CombatPredictionState fork = new(
            combatState,
            smallCreatures,
            smallCreatureCount,
            creaturesDict,
            _removedCreatures is { Count: > 0 } ? new HashSet<Creature>(_removedCreatures) : null,
            singlePlayer,
            singlePlayerState,
            players);
        context.Register(this, fork);
        return fork;
    }
}
