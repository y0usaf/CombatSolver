using System.Reflection;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace OfflineSearchHarness;

internal static class FairObservationChecks
{
    private const ulong SamplerSeed = 0x5EEDF00DUL;

    private sealed record HiddenState(
        CardModel[] DrawOrder,
        RunRngSet RunRng,
        SerializableRunRngSet RunRngState,
        RunRngSet[] MonsterRunRngs,
        Rng[] MonsterRngs,
        SerializableRng[] MonsterRngStates,
        Rng EncounterRng,
        SerializableRng EncounterRngState,
        PlayerRngSet PlayerRng,
        SerializablePlayerRngSet PlayerRngState,
        PlayerOddsSet PlayerOdds,
        string PlayerOddsState);

    private static readonly FieldInfo RunRngField = typeof(RunState)
        .GetField("<Rng>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(RunState).FullName, "<Rng>k__BackingField");
    private static readonly PropertyInfo OddsCurrentValue = typeof(AbstractOdds)
        .GetProperty(nameof(AbstractOdds.CurrentValue))
        ?? throw new MissingMemberException(typeof(AbstractOdds).FullName, nameof(AbstractOdds.CurrentValue));

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
        string[] extraCards = (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FAIR_EXTRA_CARDS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (string id in extraCards)
        {
            CardModel canonical = ModelDb.All.OfType<CardModel>().Single(card => card.Id.Entry == id);
            CardModel card = combat.CreateCard(canonical, player);
            loop.RunUntilCompleted(
                CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, player),
                TimeSpan.FromSeconds(30),
                $"add {id}");
        }
        HiddenState worldA = Save(combat, player);
        string liveA = ContinuationStamp.CaptureLive(combat).StateText;
        FairRoot rootA = Capture(combat, player, SamplerSeed);
        Check(ContinuationStamp.CaptureLive(combat).StateText == liveA, "fair capture leaves the live game untouched");

        MutateHiddenState(combat, player);
        HiddenState worldB = Save(combat, player);
        string liveB = ContinuationStamp.CaptureLive(combat).StateText;
        Check(liveA != liveB, "hidden mutation changes the clairvoyant live stamp");
        Check(Field(liveA, "R") != Field(liveB, "R"), "hidden mutation changes every RNG stream");
        Check(combat.RunState.Rng.StringSeed != worldA.RunRng.StringSeed, "hidden mutation changes the run seed string");
        Check(Fingerprint(worldA) != Fingerprint(worldB)
                && worldA.PlayerRng.Seed != worldB.PlayerRng.Seed
                && Enum.GetValues<PlayerRngType>().All(type => !SameRng(
                    worldA.PlayerRngState.Rngs[type], worldB.PlayerRngState.Rngs[type]))
                && worldA.PlayerOddsState != worldB.PlayerOddsState
                && worldA.MonsterRngStates.Zip(worldB.MonsterRngStates).All(pair => !SameRng(pair.First, pair.Second))
                && !SameRng(worldA.EncounterRngState, worldB.EncounterRngState),
            "hidden mutation changes every player RNG stream, player odds, per-monster and encounter RNGs");
        Check(Field(liveA, "MS0") == Field(liveB, "MS0") && Field(liveA, "AI0") == Field(liveB, "AI0"),
            "hidden mutation keeps displayed enemy move history and history-derived AI state");
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
            Check(Fingerprint(Save(combat, player)) == Fingerprint(worldA) && ContinuationStamp.CaptureLive(combat).StateText == liveA,
                $"seed {seed}: search consumes no live hidden RNG in world A");
            Restore(combat, player, worldB);
            string[] routeB = Solve(combat, options, loop, seed);
            Check(Fingerprint(Save(combat, player)) == Fingerprint(worldB) && ContinuationStamp.CaptureLive(combat).StateText == liveB,
                $"seed {seed}: search consumes no live hidden RNG in world B");
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

        List<string> multiSampleDecisions = [];
        for (int decision = 0; decision < 3; decision++)
        {
            ulong seed = SamplerSeed + 5000UL + (ulong)(decision * 16);
            Restore(combat, player, worldA);
            string decisionA = Decide(combat, loop, seed);
            Restore(combat, player, worldB);
            string decisionB = Decide(combat, loop, seed);
            Check(decisionA == decisionB, $"seed {seed}: identical multi-sample decision and candidate scores across hidden states");
            multiSampleDecisions.Add(decisionA);
        }

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
            multiSampleDecisions,
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

        FairObservation.RevealCursor beforeDraw = FairObservation.CaptureReveal(combat, player);
        loop.RunUntilCompleted(
            CardPileCmd.Draw(new BlockingPlayerChoiceContext(), player),
            TimeSpan.FromSeconds(30),
            "CardPileCmd.Draw");
        check(state.Hand.Cards.Contains(top), "native draw takes the known top card");
        check(FairDrawPileKnowledge.Capture(drawPile) == new DrawPileKnowledge(0, 1), "drawing consumes the known top position");
        string? drawReveal = FairObservation.DescribeReveal(beforeDraw, combat, player);
        check(drawReveal != null && drawReveal.EndsWith("draw_pile_left=1", StringComparison.Ordinal),
            "a draw is reported as revealed information");

        Add(loop, state.Hand.Cards[0], CardPilePosition.Top);
        check(FairDrawPileKnowledge.Capture(drawPile) == new DrawPileKnowledge(1, 1), "second top insertion is known");
        loop.RunUntilCompleted(
            CardPileCmd.Shuffle(new BlockingPlayerChoiceContext(), player),
            TimeSpan.FromSeconds(60),
            "CardPileCmd.Shuffle");
        check(FairDrawPileKnowledge.Capture(drawPile) == default, "a reshuffle forgets every known position");

        Add(loop, state.Hand.Cards[0], CardPilePosition.Top);
        check(FairDrawPileKnowledge.Capture(drawPile) == new DrawPileKnowledge(1, 0), "top insertion after reshuffle is known");
        CardModel random = state.Hand.Cards[0];
        FairObservation.RevealCursor beforeRandom = FairObservation.CaptureReveal(combat, player);
        Add(loop, random, CardPilePosition.Random);
        check(FairDrawPileKnowledge.Capture(drawPile) == default, "random insertion forgets positions it may have displaced");
        string? revealed = FairObservation.DescribeReveal(beforeRandom, combat, player);
        check(revealed != null && revealed.Contains("Shuffle", StringComparison.Ordinal),
            "random insertion is reported as revealed information");
        return new { middleOrdersObserved = middleOrders.Count, drawReveal, randomInsertReveal = revealed };
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

    private static string Decide(CombatState combat, MainLoopContext loop, ulong seed)
    {
        CombatRootSnapshot[] roots = [.. Enumerable.Range(0, 4).Select(index =>
            CombatRootSnapshot.Capture(combat, fair: FairObservation.FromSeed(seed + (ulong)index)))];
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: SolverController.ResolveTheftPolicy(combat));
        SearchPolicySnapshot isolated = FairDecisionPlanner.IsolatedPolicy(policy, policy.Profile);
        FairPlannerSettings settings = new(roots.Length, 3, 200, 0.25);
        Task<FairDecision?> task = Task.Run(() => FairDecisionPlanner.Decide(
            roots,
            (root, prefix) => FairDecisionPlanner.Solve(root, names, damage, isolated, prefix, CancellationToken.None),
            (root, action) => FairDecisionPlanner.RevealedVariants(root, names, damage, isolated, action, CancellationToken.None),
            settings,
            CancellationToken.None));
        loop.RunUntilCompleted(task, TimeSpan.FromMinutes(5), "multi-sample decision");
        FairDecision? result = task.GetAwaiter().GetResult();
        return result == null ? "-" : FairDecisionPlanner.Key(result.Action) + " " + string.Join(';', result.Candidates.Select(score =>
            $"{score.Key}:{score.MeanUtility:R}:{score.DeathRate:R}:{score.StandardDeviation:R}:{score.ValidSamples}"));
    }

    private static HiddenState Save(CombatState combat, Player player)
    {
        MonsterModel[] monsters = Monsters(combat);
        Rng encounterRng = combat.Encounter!._rng ?? throw new InvalidOperationException("Encounter RNG is missing.");
        return new(
            [.. player.PlayerCombatState!.DrawPile.Cards],
            combat.RunState.Rng,
            combat.RunState.Rng.ToSerializable(),
            [.. monsters.Select(monster => monster._runRng!)],
            [.. monsters.Select(monster => monster._rng!)],
            [.. monsters.Select(monster => monster._rng!.ToSerializable())],
            encounterRng,
            encounterRng.ToSerializable(),
            player.PlayerRng,
            player.PlayerRng.ToSerializable(),
            player.PlayerOdds,
            JsonSerializer.Serialize(player.PlayerOdds.ToSerializable(), FieldJson));
    }

    private static void Restore(CombatState combat, Player player, HiddenState hidden)
    {
        List<CardModel> cards = player.PlayerCombatState!.DrawPile._cards;
        if (Multiset(cards) != Multiset(hidden.DrawOrder) || cards.Count != hidden.DrawOrder.Length)
            throw new InvalidOperationException("Hidden state restore needs the same draw pile cards.");
        cards.Clear();
        cards.AddRange(hidden.DrawOrder);
        RunRngField.SetValue(combat.RunState, hidden.RunRng);
        hidden.RunRng.LoadFromSerializable(hidden.RunRngState);
        MonsterModel[] monsters = Monsters(combat);
        for (int index = 0; index < monsters.Length; index++)
        {
            monsters[index]._runRng = hidden.MonsterRunRngs[index];
            monsters[index]._rng = hidden.MonsterRngs[index];
            hidden.MonsterRngs[index].LoadFromSerializable(hidden.MonsterRngStates[index]);
        }
        combat.Encounter!._rng = hidden.EncounterRng;
        hidden.EncounterRng.LoadFromSerializable(hidden.EncounterRngState);
        player.PlayerRng = hidden.PlayerRng;
        hidden.PlayerRng.LoadFromSerializable(hidden.PlayerRngState);
        player.PlayerOdds = hidden.PlayerOdds;
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
        RunRngSet runRng = new("FAIRWORLDB" + combat.RunState.Rng.StringSeed);
        foreach (RunRngType type in Enum.GetValues<RunRngType>())
            runRng.GetRng(type).LoadFromSerializable(new Rng(seed++).ToSerializable());
        RunRngField.SetValue(combat.RunState, runRng);
        foreach (MonsterModel monster in Monsters(combat))
        {
            monster._runRng = runRng;
            monster._rng = new Rng(seed++);
        }
        combat.Encounter!._rng = new Rng(seed++);
        PlayerRngSet playerRng = new(player.PlayerRng.Seed ^ 0x5A5A5A5AUL);
        foreach (PlayerRngType type in Enum.GetValues<PlayerRngType>())
            playerRng.GetRng(type).LoadFromSerializable(new Rng(seed++).ToSerializable());
        PlayerOddsSet odds = new(playerRng);
        OddsCurrentValue.SetValue(odds.PotionReward, 0.9f);
        OddsCurrentValue.SetValue(odds.CardRarity, odds.CardRarity.CurrentValue + 0.07f);
        player.PlayerRng = playerRng;
        player.PlayerOdds = odds;
    }

    private static MonsterModel[] Monsters(CombatState combat)
        => [.. combat.Enemies.Select(enemy => enemy.Monster).OfType<MonsterModel>()];

    private static string Fingerprint(HiddenState hidden)
        => JsonSerializer.Serialize(new
        {
            run = hidden.RunRng.ToSerializable(),
            monsters = hidden.MonsterRngs.Select(rng => rng.ToSerializable()).ToArray(),
            monsterRunRngs = hidden.MonsterRunRngs.Select(rng => rng.ToSerializable()).ToArray(),
            encounter = hidden.EncounterRng.ToSerializable(),
            player = hidden.PlayerRng.ToSerializable(),
            odds = JsonSerializer.Serialize(hidden.PlayerOdds.ToSerializable(), FieldJson),
        }, FieldJson);

    private static bool SameRng(SerializableRng first, SerializableRng second)
        => JsonSerializer.Serialize(first, FieldJson) == JsonSerializer.Serialize(second, FieldJson);

    private static readonly JsonSerializerOptions FieldJson = new() { IncludeFields = true };

    private static string Multiset(IEnumerable<CardModel> cards)
        => string.Join(',', cards.Select(card => card.Id.Entry).Order(StringComparer.Ordinal));

    private static string Field(string stamp, string name)
        => stamp.Split(';').Single(field => field.StartsWith(name + "=", StringComparison.Ordinal));
}
