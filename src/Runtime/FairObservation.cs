using System.Security.Cryptography;
using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed class FairObservation
{
    private readonly Rng _sampler;
    private readonly bool _keepDrawOrder;

    private FairObservation(SerializableRunRngSet liveRunRng)
    {
        _sampler = new Rng(0);
        RunRng = liveRunRng;
        _keepDrawOrder = true;
    }

    private FairObservation(ulong seed)
    {
        Seed = seed;
        _sampler = new Rng(seed);
        SerializableRunRngSet runRng = new() { Seed = "fair-" + _sampler.NextUnsignedLong().ToString("x16") };
        foreach (RunRngType type in Enum.GetValues<RunRngType>())
            runRng.Rngs[type] = new Rng(_sampler.NextUnsignedLong()).ToSerializable();
        RunRng = runRng;
    }

    internal static bool Enabled => true;

    public ulong Seed { get; }

    public SerializableRunRngSet RunRng { get; }

    public static FairObservation FromEntropy()
        => new(BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof(ulong))));

    public static FairObservation FromSeed(ulong seed) => new(seed);

    internal static FairObservation ClairvoyantForOfflineBenchmark(CombatState state)
        => new(state.RunState.Rng.ToSerializable());

    public Rng CreateMonsterAiRng() => new(RunRng.Rngs[RunRngType.MonsterAi]);

    public void ApplyToRoot(CombatPredictionSimulator simulator)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        combat.ResampleRootRandomness(simulator, RunRng);
        if (_keepDrawOrder)
            return;
        foreach (Player player in simulator.State.Players)
        {
            PlayerCombatState live = player.PlayerCombatState
                ?? throw new InvalidOperationException($"Player {player.NetId} has no combat state to sample.");
            SampleUnknownDrawOrder(
                simulator.State.GetPlayerCombatState(player).DrawPile,
                FairDrawPileKnowledge.Capture(live.DrawPile));
        }
    }

    private void SampleUnknownDrawOrder(SimCardPile pile, DrawPileKnowledge knowledge)
    {
        PredictedCard[] cards = [.. pile.Cards];
        int unknownCount = cards.Length - knowledge.KnownTop - knowledge.KnownBottom;
        if (unknownCount < 0)
            throw new InvalidOperationException("Known draw pile positions exceed the pile size.");
        List<PredictedCard> unknown = cards
            .Skip(knowledge.KnownTop)
            .Take(unknownCount)
            .OrderBy(ObservableKey, StringComparer.Ordinal)
            .ToList();
        _sampler.Shuffle(unknown);
        pile.Reorder([
            .. cards.Take(knowledge.KnownTop),
            .. unknown,
            .. cards.Skip(knowledge.KnownTop + unknownCount),
        ]);
    }

    private static string ObservableKey(PredictedCard card)
    {
        StringBuilder text = new();
        ContinuationStamp.AppendCard(text, card.Preview, discoverUnregisteredBaseLibModifiers: false);
        return text.ToString();
    }

    internal readonly record struct SetupPiles(CardModel[] DrawPile, HashSet<CardModel> OtherPiles);

    internal static SetupPiles CaptureSetupPiles(PlayerCombatState state)
        => new([.. state.DrawPile.Cards],
            new(state.DiscardPile.Cards.Concat(state.ExhaustPile.Cards).Concat(state.PlayPile.Cards),
                ReferenceEqualityComparer.Instance));

    internal static CardModel[]? RevealedSetupDraws(SetupPiles before, PlayerCombatState live)
    {
        HashSet<CardModel> remaining = new(live.DrawPile.Cards, ReferenceEqualityComparer.Instance);
        HashSet<CardModel> hand = new(live.Hand.Cards, ReferenceEqualityComparer.Instance);
        int drawn = before.DrawPile.TakeWhile(card => !remaining.Contains(card)).Count();
        CardModel[] reshuffled = [.. live.Hand.Cards.Where(before.OtherPiles.Contains)];
        if (before.DrawPile.Skip(drawn).Any(card => !remaining.Contains(card))
            || before.DrawPile.Take(drawn).Any(card => !hand.Contains(card))
            || reshuffled.Length > 0 && drawn < before.DrawPile.Length)
            return null;
        return [.. before.DrawPile.Take(drawn), .. reshuffled];
    }

    internal readonly record struct RevealCursor(PredictionRngState[] Streams, CardModel[] DrawPile);

    internal static RevealCursor CaptureReveal(CombatState state, Player player)
        => new(CaptureStreams(state.RunState.Rng), [.. DrawPileOf(player).Cards]);

    internal static string? DescribeReveal(RevealCursor before, CombatState state, Player player)
    {
        PredictionRngState[] streams = CaptureStreams(state.RunState.Rng);
        RunRngType[] types = Enum.GetValues<RunRngType>();
        List<string> rolled = [];
        for (int index = 0; index < types.Length; index++)
        {
            if (streams[index] != before.Streams[index])
                rolled.Add(types[index].ToString());
        }
        IReadOnlyList<CardModel> drawPile = DrawPileOf(player).Cards;
        int left = before.DrawPile.Count(card => !drawPile.Contains(card));
        if (rolled.Count == 0 && left == 0)
            return null;
        return $"rng=[{string.Join(',', rolled)}] draw_pile_left={left}";
    }

    private static CardPile DrawPileOf(Player player)
        => (player.PlayerCombatState
            ?? throw new InvalidOperationException($"Player {player.NetId} has no combat state.")).DrawPile;

    private static PredictionRngState[] CaptureStreams(RunRngSet rng)
        => [.. Enum.GetValues<RunRngType>().Select(type => rng.GetRng(type).CaptureState())];
}
