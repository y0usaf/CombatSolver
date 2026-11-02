using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

internal readonly record struct DrawPileKnowledge(int KnownTop, int KnownBottom);

internal static class FairDrawPileKnowledge
{
    private sealed class Knowledge
    {
        public readonly List<CardModel> Top = [];
        public readonly List<CardModel> Bottom = [];

        public void Forget()
        {
            Top.Clear();
            Bottom.Clear();
        }
    }

    private sealed class PendingAdd(CardPile pile, CardPilePosition position)
    {
        public CardPile Pile { get; } = pile;
        public CardPilePosition Position { get; } = position;
    }

    private static readonly ConditionalWeakTable<CardPile, Knowledge> Piles = new();
    private static readonly ConditionalWeakTable<CardModel, PendingAdd> Pending = new();
    private static int _shuffleDepth;

    internal static DrawPileKnowledge Capture(CardPile pile)
    {
        if (!Piles.TryGetValue(pile, out Knowledge? knowledge))
            return default;
        IReadOnlyList<CardModel> cards = pile.Cards;
        bool valid = knowledge.Top.Count + knowledge.Bottom.Count <= cards.Count;
        for (int index = 0; valid && index < knowledge.Top.Count; index++)
            valid = ReferenceEquals(knowledge.Top[index], cards[index]);
        int bottomStart = cards.Count - knowledge.Bottom.Count;
        for (int index = 0; valid && index < knowledge.Bottom.Count; index++)
            valid = ReferenceEquals(knowledge.Bottom[index], cards[bottomStart + index]);
        if (valid)
            return new DrawPileKnowledge(knowledge.Top.Count, knowledge.Bottom.Count);
        Entry.Logger.Warn(
            $"[CombatSolver/Fair] DRAW_KNOWLEDGE_RESET known_top={knowledge.Top.Count} " +
            $"known_bottom={knowledge.Bottom.Count} pile={cards.Count}");
        knowledge.Forget();
        return default;
    }

    private static bool Tracks(CardPile pile)
        => pile.Type == PileType.Draw && NGame.IsMainThread();

    internal static void BeginShuffle(Player player)
    {
        if (!NGame.IsMainThread())
            return;
        _shuffleDepth++;
        if (player.PlayerCombatState is { } state)
            Piles.GetOrCreateValue(state.DrawPile).Forget();
    }

    internal static void EndShuffle()
    {
        if (NGame.IsMainThread() && _shuffleDepth > 0)
            _shuffleDepth--;
    }

    internal static void RequestAdd(IEnumerable<CardModel> cards, CardPile pile, CardPilePosition position)
    {
        if (!Tracks(pile))
            return;
        CardPilePosition observed = _shuffleDepth > 0 ? CardPilePosition.Random : position;
        foreach (CardModel card in cards)
            Pending.AddOrUpdate(card, new PendingAdd(pile, observed));
    }

    internal static void Added(CardPile pile, CardModel card, int index)
    {
        if (!Tracks(pile))
            return;
        CardPilePosition? position = Pending.TryGetValue(card, out PendingAdd? pending)
            && ReferenceEquals(pending.Pile, pile)
                ? pending.Position
                : (CardPilePosition?)null;
        Pending.Remove(card);
        Knowledge knowledge = Piles.GetOrCreateValue(pile);
        if (position == CardPilePosition.Top && index == 0)
            knowledge.Top.Insert(0, card);
        else if (position == CardPilePosition.Bottom && index < 0)
            knowledge.Bottom.Add(card);
        else
            knowledge.Forget();
    }

    internal static void Removed(CardPile pile, CardModel card)
    {
        if (!Tracks(pile) || !Piles.TryGetValue(pile, out Knowledge? knowledge))
            return;
        knowledge.Top.Remove(card);
        knowledge.Bottom.Remove(card);
    }

    internal static void MovedToTop(CardPile pile, CardModel card)
    {
        if (!Tracks(pile))
            return;
        Knowledge knowledge = Piles.GetOrCreateValue(pile);
        knowledge.Top.Remove(card);
        knowledge.Bottom.Remove(card);
        knowledge.Top.Insert(0, card);
    }

    internal static void MovedToBottom(CardPile pile, CardModel card)
    {
        if (!Tracks(pile))
            return;
        Knowledge knowledge = Piles.GetOrCreateValue(pile);
        knowledge.Top.Remove(card);
        knowledge.Bottom.Remove(card);
        knowledge.Bottom.Add(card);
    }

    internal static void Reset(CardPile pile)
    {
        if (Tracks(pile) && Piles.TryGetValue(pile, out Knowledge? knowledge))
            knowledge.Forget();
    }
}

internal sealed class FairDrawPileAddRequestPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_fair_draw_pile_add_request";
    public static string Description => "Fair mode: remember the visible position of cards added to the draw pile";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardPileCmd), nameof(CardPileCmd.Add),
            [typeof(IEnumerable<CardModel>), typeof(CardPile), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool), typeof(bool)]),
    ];

    public static void Prefix(IEnumerable<CardModel> cards, CardPile newPile, CardPilePosition position)
        => FairDrawPileKnowledge.RequestAdd(cards, newPile, position);
}

internal sealed class FairDrawPileShufflePatch : IPatchMethod
{
    public static string PatchId => "combat_solver_fair_draw_pile_shuffle";
    public static string Description => "Fair mode: a reshuffle hides every draw pile position";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardPileCmd), nameof(CardPileCmd.Shuffle), [typeof(PlayerChoiceContext), typeof(Player)]),
    ];

    public static void Prefix(Player player) => FairDrawPileKnowledge.BeginShuffle(player);

    public static void Postfix() => FairDrawPileKnowledge.EndShuffle();
}

internal sealed class FairDrawPileInsertPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_fair_draw_pile_insert";
    public static string Description => "Fair mode: track draw pile insertions";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardPile), nameof(CardPile.AddInternal), [typeof(CardModel), typeof(int), typeof(bool)]),
    ];

    public static void Postfix(CardPile __instance, CardModel card, int index)
        => FairDrawPileKnowledge.Added(__instance, card, index);
}

internal sealed class FairDrawPileRemovePatch : IPatchMethod
{
    public static string PatchId => "combat_solver_fair_draw_pile_remove";
    public static string Description => "Fair mode: track draw pile removals";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardPile), nameof(CardPile.RemoveInternal), [typeof(CardModel), typeof(bool)]),
    ];

    public static void Postfix(CardPile __instance, CardModel card)
        => FairDrawPileKnowledge.Removed(__instance, card);
}

internal sealed class FairDrawPileMoveToTopPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_fair_draw_pile_move_top";
    public static string Description => "Fair mode: track cards moved to the top of the draw pile";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardPile), nameof(CardPile.MoveToTopInternal), [typeof(CardModel)]),
    ];

    public static void Postfix(CardPile __instance, CardModel card)
        => FairDrawPileKnowledge.MovedToTop(__instance, card);
}

internal sealed class FairDrawPileMoveToBottomPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_fair_draw_pile_move_bottom";
    public static string Description => "Fair mode: track cards moved to the bottom of the draw pile";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardPile), nameof(CardPile.MoveToBottomInternal), [typeof(CardModel)]),
    ];

    public static void Postfix(CardPile __instance, CardModel card)
        => FairDrawPileKnowledge.MovedToBottom(__instance, card);
}

internal sealed class FairDrawPileResetPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_fair_draw_pile_reset";
    public static string Description => "Fair mode: randomizing or clearing the draw pile hides every position";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardPile), nameof(CardPile.RandomizeOrderInternal),
            [typeof(Player), typeof(MegaCrit.Sts2.Core.Random.Rng), typeof(MegaCrit.Sts2.Core.Combat.CombatState)]),
        new(typeof(CardPile), nameof(CardPile.Clear), [typeof(bool)]),
    ];

    public static void Postfix(CardPile __instance) => FairDrawPileKnowledge.Reset(__instance);
}
