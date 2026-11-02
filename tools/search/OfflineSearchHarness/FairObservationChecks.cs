using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace OfflineSearchHarness;

internal static class FairObservationChecks
{
    private const ulong SamplerSeed = 0x5EEDF00DUL;

    private sealed record HiddenState(
        CardModel[] DrawOrder,
        SerializableRunRngSet RunRng,
        SerializablePlayerRngSet PlayerRng);

    private sealed record FairRoot(string Stamp, string Forecast, CardModel[] DrawPile);

    internal static void Run(CombatState combat, HarnessOptions options, MainLoopContext loop)
    {
        Player player = combat.Players.Single();
        CardPile drawPile = player.PlayerCombatState!.DrawPile;
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException($"FairObservation: {name}");
            checks++;
        }

        string[] fairPatches = [.. ModRuntime.PatchLog.Where(line => line.Contains(".FairDrawPile", StringComparison.Ordinal))];
        Check(fairPatches.Length == 7 && fairPatches.All(line => line.EndsWith("已装 1 个目标", StringComparison.Ordinal)
                || line.EndsWith("已装 2 个目标", StringComparison.Ordinal)),
            "all fair draw pile observation patches are installed: " + string.Join(" | ", fairPatches));
        Check(drawPile.Cards.Count >= 2, "fixture needs at least two unknown draw pile cards");
        HiddenState worldA = Save(combat, player);
        string liveA = ContinuationStamp.CaptureLive(combat).StateText;
        FairRoot rootA = Capture(combat, player, SamplerSeed);
        Check(ContinuationStamp.CaptureLive(combat).StateText == liveA, "fair capture leaves the live game untouched");

        MutateHiddenState(combat, player);
        HiddenState worldB = Save(combat, player);
        string liveB = ContinuationStamp.CaptureLive(combat).StateText;
        Check(liveA != liveB, "hidden mutation changes the clairvoyant live stamp");
        Check(Field(liveA, "R") != Field(liveB, "R"), "hidden mutation changes every RNG stream");
        Check(Field(liveA, "D") != Field(liveB, "D"), "hidden mutation changes the draw order");
        Check(Field(liveA, "H") == Field(liveB, "H") && Field(liveA, "C") == Field(liveB, "C"),
            "hidden mutation keeps hand and discard");
        Check(Multiset(drawPile.Cards) == Multiset(worldA.DrawOrder), "hidden mutation keeps the draw multiset");

        FairRoot rootB = Capture(combat, player, SamplerSeed);
        Check(rootA.Stamp == rootB.Stamp, "same sampler seed gives bit-identical sanitized roots across hidden states");
        Check(rootA.Forecast == rootB.Forecast, "same sampler seed gives identical intent forecasts across hidden states");
        FairRoot rootOtherSeed = Capture(combat, player, SamplerSeed + 1);
        Check(Field(rootOtherSeed.Stamp, "R") != Field(rootA.Stamp, "R"), "sampler seed drives the sampled RNG streams");
        Check(Field(rootA.Stamp, "R") != Field(liveA, "R") && Field(rootB.Stamp, "R") != Field(liveB, "R"),
            "sanitized RNG differs from both live RNG states");
        Check(ContinuationStamp.CaptureLive(combat).StateText == liveB, "fair captures leave hidden world B untouched");

        int searchSeeds = int.Parse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_SEARCH_SEEDS") ?? "12");
        Dictionary<string, int> firstActionsA = new(StringComparer.Ordinal);
        Dictionary<string, int> firstActionsB = new(StringComparer.Ordinal);
        int identicalRoutes = 0;
        HashSet<string> distinctRoutes = new(StringComparer.Ordinal);
        List<object> samples = [];
        for (int sample = 0; sample < searchSeeds; sample++)
        {
            ulong seed = SamplerSeed + 1000UL + (ulong)sample;
            Restore(combat, player, worldA);
            string[] routeA = Solve(combat, options, loop, seed);
            Restore(combat, player, worldB);
            string[] routeB = Solve(combat, options, loop, seed);
            Check(routeA.SequenceEqual(routeB), $"seed {seed}: identical chosen route across hidden states");
            identicalRoutes++;
            distinctRoutes.Add(string.Join('|', routeA));
            string firstA = routeA.FirstOrDefault() ?? "-";
            string firstB = routeB.FirstOrDefault() ?? "-";
            firstActionsA[firstA] = firstActionsA.GetValueOrDefault(firstA) + 1;
            firstActionsB[firstB] = firstActionsB.GetValueOrDefault(firstB) + 1;
            samples.Add(new { seed, routeA = routeA.Take(6).ToArray() });
        }
        Check(firstActionsA.OrderBy(item => item.Key).SequenceEqual(firstActionsB.OrderBy(item => item.Key)),
            "first action distribution is identical across hidden states");

        Restore(combat, player, worldA);
        object knownPositions = CheckKnownPositions(combat, player, loop, Check);

        File.WriteAllText(Path.Combine(options.OutputDirectory, "fair-observation-checks.json"), JsonSerializer.Serialize(new
        {
            status = "Passed",
            checks,
            samplerSeed = SamplerSeed,
            searchSeeds,
            identicalRoutes,
            distinctRoutes = distinctRoutes.Count,
            firstActionDistributionWorldA = firstActionsA,
            firstActionDistributionWorldB = firstActionsB,
            samples,
            knownPositions,
            liveDrawA = Field(liveA, "D"),
            liveDrawB = Field(liveB, "D"),
            sanitizedDraw = Field(rootA.Stamp, "D"),
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"FAIR_OBSERVATION_CHECKS Passed checks={checks} search_seeds={searchSeeds} identical_routes={identicalRoutes} distinct_routes={distinctRoutes.Count} distinct_first_actions={firstActionsA.Count}");
    }

    private static object CheckKnownPositions(
        CombatState combat,
        Player player,
        MainLoopContext loop,
        Action<bool, string> check)
    {
        PlayerCombatState state = player.PlayerCombatState!;
        CardPile drawPile = state.DrawPile;
        check(FairDrawPileKnowledge.Capture(drawPile) == default, "opening draw pile has no known positions");

        CardModel top = state.Hand.Cards[0];
        FairObservation.RevealCursor beforeTop = FairObservation.CaptureReveal(combat, player);
        Add(loop, top, CardPilePosition.Top);
        check(ReferenceEquals(drawPile.Cards[0], top), "native top insertion");
        check(FairDrawPileKnowledge.Capture(drawPile) == new DrawPileKnowledge(1, 0), "top insertion is a known position");
        check(FairObservation.DescribeReveal(beforeTop, combat, player) == null, "top insertion reveals no hidden information");

        CardModel bottom = state.Hand.Cards[0];
        Add(loop, bottom, CardPilePosition.Bottom);
        check(FairDrawPileKnowledge.Capture(drawPile) == new DrawPileKnowledge(1, 1), "bottom insertion is a known position");

        for (ulong seed = 1; seed <= 8; seed++)
        {
            FairRoot root = Capture(combat, player, seed);
            check(ReferenceEquals(root.DrawPile[0], top), $"seed {seed}: known top card stays on top");
            check(ReferenceEquals(root.DrawPile[^1], bottom), $"seed {seed}: known bottom card stays at the bottom");
        }
        HashSet<string> middleOrders = [.. Enumerable.Range(1, 16).Select(seed => string.Join(',',
            Capture(combat, player, (ulong)seed).DrawPile.Skip(1).SkipLast(1).Select(card => card.Id.Entry)))];

        CardModel random = state.Hand.Cards[0];
        FairObservation.RevealCursor beforeRandom = FairObservation.CaptureReveal(combat, player);
        Add(loop, random, CardPilePosition.Random);
        check(FairDrawPileKnowledge.Capture(drawPile) == default, "random insertion forgets positions it may have displaced");
        string? revealed = FairObservation.DescribeReveal(beforeRandom, combat, player);
        check(revealed != null && revealed.Contains("Shuffle", StringComparison.Ordinal),
            "random insertion is reported as revealed information");
        return new { middleOrdersObserved = middleOrders.Count, randomInsertReveal = revealed };
    }

    private static void Add(MainLoopContext loop, CardModel card, CardPilePosition position)
        => loop.RunUntilCompleted(
            CardPileCmd.Add(card, PileType.Draw, position),
            TimeSpan.FromSeconds(30),
            $"CardPileCmd.Add {position}");

    private static FairRoot Capture(CombatState combat, Player player, ulong seed)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat, fair: FairObservation.FromSeed(seed));
        CombatPredictionSimulatorAccess simulator = new(root);
        return new FairRoot(
            ContinuationStamp.CapturePredicted(player, simulator.Simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText,
            string.Join('|', root.Forecast.Rounds.Select(round => string.Join(',', round.Select(move =>
                $"{move.Owner.CombatId}:{move.Move.Id}:{string.Join('+', move.AttackHits.Select(hit => hit.Damage))}")))),
            [.. simulator.Simulator.State.GetPlayerCombatState(player).DrawPile.Cards.Select(card => card.Original)]);
    }

    private sealed class CombatPredictionSimulatorAccess(CombatRootSnapshot root)
    {
        public CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator Simulator { get; } = root.ForkSimulator();
    }

    private static string[] Solve(CombatState combat, HarnessOptions options, MainLoopContext loop, ulong seed)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat, fair: FairObservation.FromSeed(seed));
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            settings, combat, includeTurnSetup: false, theftPolicy: SolverController.ResolveTheftPolicy(combat));
        bool timeBoundary = false;
        SolverResult result = ModRuntime.SolveEvaluate(
            root, names, damage, policy, settings, options.BudgetMilliseconds, loop, out _, ref timeBoundary);
        if (timeBoundary)
            throw new InvalidOperationException("Fair search comparison hit the wall-clock budget; raise --budget-ms.");
        return [.. result.BestNode.Actions.Select(action =>
            $"{action.Turn}:{action.Kind}:{action.CardId}{action.PotionId}:{action.TargetCombatId}:{action.CardStateKey}")];
    }

    private static HiddenState Save(CombatState combat, Player player)
        => new(
            [.. player.PlayerCombatState!.DrawPile.Cards],
            combat.RunState.Rng.ToSerializable(),
            player.PlayerRng.ToSerializable());

    private static void Restore(CombatState combat, Player player, HiddenState hidden)
    {
        List<CardModel> cards = player.PlayerCombatState!.DrawPile._cards;
        if (Multiset(cards) != Multiset(hidden.DrawOrder) || cards.Count != hidden.DrawOrder.Length)
            throw new InvalidOperationException("Hidden state restore needs the same draw pile cards.");
        cards.Clear();
        cards.AddRange(hidden.DrawOrder);
        combat.RunState.Rng.LoadFromSerializable(hidden.RunRng);
        player.PlayerRng.LoadFromSerializable(hidden.PlayerRng);
    }

    private static void MutateHiddenState(CombatState combat, Player player)
    {
        List<CardModel> cards = player.PlayerCombatState!.DrawPile._cards;
        string original = string.Join(',', cards.Select(card => card.Id.Entry));
        for (int turn = 0; turn < cards.Count; turn++)
        {
            cards.Add(cards[0]);
            cards.RemoveAt(0);
            if (string.Join(',', cards.Select(card => card.Id.Entry)) != original)
                break;
        }
        ulong seed = 0xBADC0DEUL;
        foreach (RunRngType type in Enum.GetValues<RunRngType>())
            combat.RunState.Rng.GetRng(type).LoadFromSerializable(new Rng(seed++).ToSerializable());
        player.PlayerRng.Rewards.LoadFromSerializable(new Rng(seed).ToSerializable());
    }

    private static string Multiset(IEnumerable<CardModel> cards)
        => string.Join(',', cards.Select(card => card.Id.Entry).Order(StringComparer.Ordinal));

    private static string Field(string stamp, string name)
        => stamp.Split(';').Single(field => field.StartsWith(name + "=", StringComparison.Ordinal));
}
