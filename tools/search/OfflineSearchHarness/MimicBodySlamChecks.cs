using System.Reflection;
using System.Text.Json;
using CombatSolver;
using CombatSolver.NewSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace OfflineSearchHarness;

internal static class MimicBodySlamChecks
{
    internal static void Run(CombatState combat, Player local, MainLoopContext loop, Action<bool, string> check, bool open, string outDir)
    {
        check(combat.Players.Count == 2, $"coop-mimic needs 2 players, got {combat.Players.Count}");
        CoopEvalSuite.InstallTimeBypass();
        Player mate = combat.Players.First(p => !ReferenceEquals(p, local));
        Creature enemy = combat.Enemies.First(e => e.IsAlive);
        foreach (Creature extra in combat.Enemies.Where(e => e.IsAlive && !ReferenceEquals(e, enemy)).ToArray())
            loop.RunUntilCompleted(CreatureCmd.Kill(extra, force: true), TimeSpan.FromSeconds(15), "single enemy");
        loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(enemy, Math.Min(enemy.MaxHp, 80)), TimeSpan.FromSeconds(15), "enemy hp");
        loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(local.Creature, 60), TimeSpan.FromSeconds(15), "local hp");
        loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(mate.Creature, 40), TimeSpan.FromSeconds(15), "mate hp");
        ReplaceHand(combat, local, loop, ["DefendIronclad", "DefendIronclad", "StrikeIronclad", "StrikeIronclad"]);
        ReplaceHand(combat, mate, loop, ["Mimic", "BodySlam", "BodySlam"]);
        SetEnergy(local, 2);
        SetEnergy(mate, 3);
        CoopEvalSuite.WaitForQueueIdle(loop);

        CardModel mimic = mate.PlayerCombatState!.Hand.Cards.First(c => c.Id.Entry == "MIMIC");
        CardModel slam = mate.PlayerCombatState.Hand.Cards.First(c => c.Id.Entry == "BODY_SLAM");
        Console.WriteLine($"MIMIC_PROBE mimic_target={mimic.TargetType} mimic_cost={mimic.EnergyCost.GetWithModifiers(CostModifiers.All)} "
            + $"slam_target={slam.TargetType} slam_cost={slam.EnergyCost.GetWithModifiers(CostModifiers.All)} "
            + $"enemy={enemy.Monster?.Id.Entry}:{enemy.CurrentHp}@{enemy.Monster?.NextMove.Id}");
        check(mimic.CanPlayTargeting(local.Creature), "teammate Mimic must be able to target the local player");

        NewSolverConfig cfg = NewSolverConfig.For(SolverSpeed.Normal) with { MaxParallelism = 4 };
        SetReady(combat, mate, true);
        NewSolverDecision ready = NewFairSolver.DecideLive(combat, local, cfg, CancellationToken.None);
        SetReady(combat, mate, false);
        Console.WriteLine($"MIMIC_DECISION ready=[{Route(ready.CommittedActions)}]");

        IReadOnlyList<PlanAction> chosen;
        string? deferredFailure = null;
        if (open)
        {
            NewSolverDecision openDecision = NewFairSolver.DecideLive(combat, local, cfg, CancellationToken.None);
            Console.WriteLine($"MIMIC_DECISION open=[{Route(openDecision.CommittedActions)}]");
            if (Defends(openDecision.CommittedActions) <= Defends(ready.CommittedActions))
                deferredFailure = $"open teammate with Mimic+Body Slam must raise local Block: open=[{Route(openDecision.CommittedActions)}] ready=[{Route(ready.CommittedActions)}]";
            chosen = openDecision.CommittedActions;
        }
        else
        {
            ReplaceHand(combat, mate, loop, []);
            SetReady(combat, mate, true);
            NewSolverDecision empty = NewFairSolver.DecideLive(combat, local, cfg, CancellationToken.None);
            SetReady(combat, mate, false);
            Console.WriteLine($"MIMIC_DECISION ready_empty_hand=[{Route(empty.CommittedActions)}]");
            if (Route(empty.CommittedActions) != Route(ready.CommittedActions))
                deferredFailure = $"a ready teammate's Mimic must not change the plan: ready=[{Route(ready.CommittedActions)}] empty=[{Route(empty.CommittedActions)}]";
            ReplaceHand(combat, mate, loop, ["Mimic", "BodySlam", "BodySlam"]);
            SetEnergy(mate, 3);
            CoopEvalSuite.WaitForQueueIdle(loop);
            chosen = ready.CommittedActions;
        }

        int turn = local.PlayerCombatState!.TurnNumber;
        int enemyBefore = enemy.CurrentHp;
        Dictionary<Player, int> hpBefore = combat.Players.ToDictionary(p => p, p => p.Creature.CurrentHp);
        PlanAction? endTurn = null;
        foreach (PlanAction action in chosen)
        {
            if (action.Kind == PlanActionKind.EndTurn)
            {
                endTurn = action;
                break;
            }
            check(CoopEvalSuite.ExecuteLocalAction(combat, local, action, loop), $"live execution of {action.CardId} failed");
        }
        int localBlock = local.Creature.Block;
        PlayMate(combat, mate, local, loop, "MIMIC");
        int mateBlock = mate.Creature.Block;
        while (PlayMate(combat, mate, local, loop, "BODY_SLAM")) { }
        int enemyAfterPlay = enemy.IsAlive ? enemy.CurrentHp : 0;
        CoopEvalSuite.ExecuteLocalAction(combat, local, endTurn ?? new PlanAction(PlanActionKind.EndTurn, turn), loop);
        if (mate.Creature.IsAlive && !CombatManager.Instance.IsPlayerReadyToEndTurn(mate))
        {
            CoopEvalSuite.WaitForQueueIdle(loop);
            EndPlayerTurnAction end = new(mate, mate.PlayerCombatState!.TurnNumber);
            RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(end);
            loop.RunUntilCompleted(end.CompletionTask, TimeSpan.FromSeconds(30), "mate end turn");
        }
        CoopEvalSuite.WaitForNextTurnOrEnd(combat, local, turn, loop);
        int enemyDamage = enemyBefore - enemyAfterPlay;
        int partyHpLost = combat.Players.Sum(p => Math.Max(0, hpBefore[p] - Math.Max(0, p.Creature.CurrentHp)));
        var result = new
        {
            variant = open ? "open" : "ready",
            route = Route(chosen),
            readyRoute = Route(ready.CommittedActions),
            localBlock,
            mateBlock,
            enemyDamage,
            partyHpLost,
            net = enemyDamage - partyHpLost,
        };
        Console.WriteLine($"MIMIC_NATIVE {JsonSerializer.Serialize(result)}");
        File.WriteAllText(Path.Combine(outDir, $"mimic-{result.variant}.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        check(deferredFailure == null, deferredFailure ?? "");
    }

    private static bool PlayMate(CombatState combat, Player mate, Player local, MainLoopContext loop, string cardId)
    {
        CoopEvalSuite.WaitForQueueIdle(loop);
        if (CoopEvalSuite.CheckTerminal(combat, local) != null)
            return false;
        CardModel? card = mate.PlayerCombatState!.Hand.Cards.FirstOrDefault(c => c.Id.Entry == cardId);
        if (card == null)
            return false;
        Creature? target = cardId == "MIMIC"
            ? (local.Creature.Block > 0 ? local.Creature : null)
            : combat.Enemies.Where(e => e.IsAlive).OrderBy(e => e.CurrentHp).FirstOrDefault();
        if (target == null || !card.CanPlayTargeting(target) || (cardId == "BODY_SLAM" && mate.Creature.Block <= 0))
            return false;
        PlayCardAction play = new(card, target);
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(play);
        loop.RunUntilCompleted(play.CompletionTask, TimeSpan.FromSeconds(30), $"mate {cardId}");
        CoopEvalSuite.WaitForQueueIdle(loop);
        return true;
    }

    private static int Defends(IReadOnlyList<PlanAction> actions)
        => actions.Count(a => a.Kind == PlanActionKind.PlayCard && a.CardId == "DEFEND_IRONCLAD");

    private static string Route(IReadOnlyList<PlanAction> actions)
        => string.Join(" -> ", actions.Select(a => $"{a.Kind}:{a.CardId}{a.PotionId}>{a.TargetCombatId}"));

    private static void SetReady(CombatState combat, Player mate, bool ready)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        object turnState = typeof(CombatManager).GetField("_turnState", flags)?.GetValue(CombatManager.Instance)
            ?? throw new InvalidOperationException("CombatManager has no active turn state.");
        HashSet<Player> set = (HashSet<Player>)(turnState.GetType().GetProperty("PlayersReadyToEndTurn", flags)?.GetValue(turnState)
            ?? throw new InvalidOperationException("CombatTurnState has no ready set."));
        if (ready)
            set.Add(mate);
        else
            set.Remove(mate);
        if (CombatManager.Instance.IsPlayerReadyToEndTurn(mate) != ready)
            throw new InvalidOperationException("Teammate ready marker did not reach the combat state.");
    }

    private static void ReplaceHand(CombatState combat, Player player, MainLoopContext loop, IReadOnlyList<string> cardNames)
    {
        async Task DoReplace()
        {
            PlayerCombatState pcs = player.PlayerCombatState!;
            await CardPileCmd.RemoveFromCombat([.. pcs.Hand.Cards], true);
            foreach (string name in cardNames)
            {
                CardModel canonical = ModelDb.AllCards.Single(c => c.GetType().Name == name || c.Id.Entry == name);
                CardModel card = combat.CreateCard(canonical, player);
                await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, player);
            }
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        loop.RunUntilCompleted(DoReplace(), TimeSpan.FromSeconds(30), "replace hand");
    }

    private static void SetEnergy(Player player, int energy)
    {
        PlayerCombatState pcs = player.PlayerCombatState!;
        if (pcs.Energy > energy)
            pcs.LoseEnergy(pcs.Energy - energy);
        else if (pcs.Energy < energy)
            pcs.GainEnergy(energy - pcs.Energy);
    }
}
