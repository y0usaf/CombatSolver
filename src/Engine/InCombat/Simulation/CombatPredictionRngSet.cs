using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Random;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Simulation;

// Branch-local copy of the combat RNG streams that simulation mutates. Each stream keeps an
// immutable state handle across forks until a branch first writes to it, avoiding eager
// Rng allocations for untouched streams.
internal sealed class CombatPredictionRngSet
{
    private sealed class FrozenStream(PredictionRngState state)
    {
        public readonly PredictionRngState State = state;
    }

    private struct Stream
    {
        private readonly FrozenStream? _frozen;
        private Rng? _mutable;

        private Stream(FrozenStream frozen) => _frozen = frozen;

        public readonly bool IsMutable => _mutable is not null;
        public Rng Mutable => _mutable ??= State.ToRng();
        public readonly PredictionRngState State => _mutable is { } rng
            ? rng.CaptureState() : _frozen!.State;

        // A caller can still hold a previously returned Rng and advance it after Fork.
        // Copy its complete state now; never share that mutable instance with a child.
        public readonly Stream Fork() => new(_mutable is null
            ? _frozen! : new FrozenStream(_mutable.CaptureState()));

        public static Stream Capture(Rng rng) => new(new FrozenStream(rng.CaptureState()));
        public static Stream Capture(PredictionRngState state) => new(new FrozenStream(state));
    }

    private readonly struct PlayerStream(Player player, Stream stream)
    {
        public readonly Player Player = player;
        public readonly Stream Stream = stream;
    }

    private PlayerStream[] _nonCombatGeneration;
    private volatile bool _nonCombatShared;
    private bool _nonCombatHasMutable;

    public Rng NonCombatGeneration(Player player)
    {
        if (_nonCombatShared)
        {
            PlayerStream[] copy = new PlayerStream[_nonCombatGeneration.Length];
            for (int i = 0; i < _nonCombatGeneration.Length; i++)
                copy[i] = new PlayerStream(_nonCombatGeneration[i].Player, _nonCombatGeneration[i].Stream.Fork());
            _nonCombatGeneration = copy;
            _nonCombatShared = false;
            _nonCombatHasMutable = false;
        }
        for (int i = 0; i < _nonCombatGeneration.Length; i++)
        {
            if (ReferenceEquals(_nonCombatGeneration[i].Player, player))
            {
                Stream stream = _nonCombatGeneration[i].Stream;
                Rng rng = stream.Mutable;
                _nonCombatGeneration[i] = new PlayerStream(player, stream);
                _nonCombatHasMutable = true;
                return rng;
            }
        }
        throw new KeyNotFoundException($"Player {player} was not present in the combat RNG set.");
    }

    public PredictionRngState NonCombatGenerationState(Player player)
    {
        PlayerStream[] arr = _nonCombatGeneration;
        for (int i = 0; i < arr.Length; i++)
        {
            if (ReferenceEquals(arr[i].Player, player))
                return arr[i].Stream.State;
        }
        throw new KeyNotFoundException($"Player {player} was not present in the combat RNG set.");
    }

    private Stream _shuffle;
    public Rng Shuffle => _shuffle.Mutable;
    public PredictionRngState ShuffleState => _shuffle.State;

    private Stream _combatCardGeneration;
    public Rng CombatCardGeneration => _combatCardGeneration.Mutable;
    public PredictionRngState CombatCardGenerationState => _combatCardGeneration.State;

    private Stream _combatPotionGeneration;
    public Rng CombatPotionGeneration => _combatPotionGeneration.Mutable;
    public PredictionRngState CombatPotionGenerationState => _combatPotionGeneration.State;

    private Stream _combatCardSelection;
    public Rng CombatCardSelection => _combatCardSelection.Mutable;
    public PredictionRngState CombatCardSelectionState => _combatCardSelection.State;

    private Stream _combatEnergyCosts;
    public Rng CombatEnergyCosts => _combatEnergyCosts.Mutable;
    public PredictionRngState CombatEnergyCostsState => _combatEnergyCosts.State;

    private Stream _combatTargets;
    public Rng CombatTargets => _combatTargets.Mutable;
    public PredictionRngState CombatTargetsState => _combatTargets.State;

    private Stream _combatOrbGeneration;
    public Rng CombatOrbGeneration => _combatOrbGeneration.Mutable;
    public PredictionRngState CombatOrbGenerationState => _combatOrbGeneration.State;

    private Stream _monsterAi;
    public Rng MonsterAi => _monsterAi.Mutable;
    public PredictionRngState MonsterAiState => _monsterAi.State;

    private Stream _niche;
    public Rng Niche => _niche.Mutable;
    public PredictionRngState NicheState => _niche.State;

    private CombatPredictionRngSet(RunRngSet rng, IReadOnlyDictionary<Player, PredictionRngState> playerRngs)
    {
        PlayerStream[] arr = new PlayerStream[playerRngs.Count];
        int index = 0;
        foreach ((Player player, PredictionRngState state) in playerRngs)
            arr[index++] = new PlayerStream(player, Stream.Capture(state));
        _nonCombatGeneration = arr;
        _shuffle = Stream.Capture(rng.Shuffle);
        _combatCardGeneration = Stream.Capture(rng.CombatCardGeneration);
        _combatPotionGeneration = Stream.Capture(rng.CombatPotionGeneration);
        _combatCardSelection = Stream.Capture(rng.CombatCardSelection);
        _combatEnergyCosts = Stream.Capture(rng.CombatEnergyCosts);
        _combatTargets = Stream.Capture(rng.CombatTargets);
        _combatOrbGeneration = Stream.Capture(rng.CombatOrbGeneration);
        _monsterAi = Stream.Capture(rng.MonsterAi);
        _niche = Stream.Capture(rng.Niche);
    }

    private CombatPredictionRngSet(CombatPredictionRngSet source)
    {
        if (!source._nonCombatHasMutable)
        {
            source._nonCombatShared = true;
            _nonCombatGeneration = source._nonCombatGeneration;
            _nonCombatShared = true;
        }
        else
        {
            PlayerStream[] src = source._nonCombatGeneration;
            PlayerStream[] copy = new PlayerStream[src.Length];
            for (int i = 0; i < src.Length; i++)
                copy[i] = new PlayerStream(src[i].Player, src[i].Stream.Fork());
            _nonCombatGeneration = copy;
        }
        _shuffle = source._shuffle.Fork();
        _combatCardGeneration = source._combatCardGeneration.Fork();
        _combatPotionGeneration = source._combatPotionGeneration.Fork();
        _combatCardSelection = source._combatCardSelection.Fork();
        _combatEnergyCosts = source._combatEnergyCosts.Fork();
        _combatTargets = source._combatTargets.Fork();
        _combatOrbGeneration = source._combatOrbGeneration.Fork();
        _monsterAi = source._monsterAi.Fork();
        _niche = source._niche.Fork();
    }

    public static CombatPredictionRngSet From(RunRngSet rng, IReadOnlyDictionary<Player, PredictionRngState> playerRngs)
        => new(rng, playerRngs);

    public static CombatPredictionRngSet From(RunRngSet rng, IEnumerable<Player> players)
        => new(rng, players.ToDictionary(player => player, player => player.PlayerRng.NonCombatGeneration.CaptureState()));

    internal CombatPredictionRngSet Fork() => new(this);
}
