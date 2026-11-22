using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.NewSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace OfflineSearchHarness;

internal static class TeammateWinChecks
{
    private const int LocalHp = 2;
    private const int EnemyHp = 20;
    private const int MateBlock = 30;
    private const int LocalRegen = 5;

    internal static void Run(CombatState combat, Player local, MainLoopContext loop, Action<bool, string> check, string variant, string outDir)
    {
        bool stock = variant == "stock";
        check(combat.Players.Count == 2, $"coop-mate-win needs 2 players, got {combat.Players.Count}");
        CoopEvalSuite.InstallTimeBypass();
        Player mate = combat.Players.First(p => !ReferenceEquals(p, local));
        Creature enemy = combat.Enemies.First(e => e.IsAlive);
        foreach (Creature extra in combat.Enemies.Where(e => e.IsAlive && !ReferenceEquals(e, enemy)).ToArray())
            loop.RunUntilCompleted(CreatureCmd.Kill(extra, force: true), TimeSpan.FromSeconds(15), "single enemy");
        loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(enemy, Math.Min(enemy.MaxHp, EnemyHp)), TimeSpan.FromSeconds(15), "enemy hp");
        if (enemy.Block > 0)
            loop.RunUntilCompleted(CreatureCmd.LoseBlock(new BlockingPlayerChoiceContext(), enemy, enemy.Block, null), TimeSpan.FromSeconds(15), "enemy block");
        if (stock && enemy.GetPower<StockPower>() is not { Amount: > 0 })
        {
            PowerModel power = ModelDb.AllPowers.Single(p => p is StockPower).ToMutable();
            power.Target = enemy;
            loop.RunUntilCompleted(PowerCmd.Apply(new BlockingPlayerChoiceContext(), power, enemy, 1, enemy, null), TimeSpan.FromSeconds(15), "enemy stock");
        }
        loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(local.Creature, LocalHp), TimeSpan.FromSeconds(15), "local hp");
        loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(mate.Creature, 40), TimeSpan.FromSeconds(15), "mate hp");
        ReplaceHand(combat, local, loop, variant == "orb" ? [] : ["Burn"]);
        ReplaceHand(combat, mate, loop, ["Mimic", "BodySlam"]);
        loop.RunUntilCompleted(CreatureCmd.GainBlock(local.Creature, 1, ValueProp.Move, null), TimeSpan.FromSeconds(15), "local block");
        loop.RunUntilCompleted(CreatureCmd.GainBlock(mate.Creature, MateBlock, ValueProp.Move, null), TimeSpan.FromSeconds(15), "mate block");
        if (variant == "orb")
            loop.RunUntilCompleted(OrbCmd.Channel<FrostOrb>(new BlockingPlayerChoiceContext(), local), TimeSpan.FromSeconds(15), "local frost");
        if (!stock)
        {
            PowerModel regen = ModelDb.AllPowers.Single(p => p is RegenPower).ToMutable();
            regen.Target = local.Creature;
            loop.RunUntilCompleted(PowerCmd.Apply(new BlockingPlayerChoiceContext(), regen, local.Creature, LocalRegen, local.Creature, null), TimeSpan.FromSeconds(15), "local regen");
        }
        SetEnergy(mate, 2);
        CoopEvalSuite.WaitForQueueIdle(loop);
        check(!CombatManager.Instance.IsPlayerReadyToEndTurn(mate), "teammate must still be acting");

        int orbs = local.PlayerCombatState!.OrbQueue.Orbs.Count;
        if (variant == "orb")
            check(local.PlayerCombatState.OrbQueue.Orbs.Any(o => o is FrostOrb), "orb variant needs a channelled Frost orb");
        int stockAmount = enemy.GetPower<StockPower>()?.Amount ?? 0;
        if (stock)
            check(stockAmount > 0, "stock variant needs an enemy that respawns on death");
        Console.WriteLine($"MATE_WIN_PROBE enemy={enemy.Monster?.Id.Entry}:{enemy.CurrentHp} stock={stockAmount} "
            + $"local={local.Creature.CurrentHp}+{local.Creature.Block} regen={local.Creature.GetPower<RegenPower>()?.Amount ?? 0} orbs={orbs} mate={mate.Creature.CurrentHp}+{mate.Creature.Block}");

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat, fair: FairObservation.FromSeed(0x5EEDF00DUL));
        check(mate.Creature.CombatId is uint mateId && root.ActingTeammateIds.Contains(mateId), "root must list the open teammate");
        SimStepState state = CombatStepEngine.CreateRoot(root, 0);
        CombatPredictionSimulator sim = state.Simulator;
        int turn = state.Combat.GetPlayerTurnNumber(local);
        bool ok = CombatStepEngine.TryReplayAction(state, new PlanAction(PlanActionKind.EndTurn, turn), local, out _, out _);
        check(ok, "simulated end turn failed");

        bool originalDead = sim.State.GetCreature(enemy).IsDead;
        CombatPredictionSimulator before = root.ForkSimulator();
        int localBlockBefore = before.State.GetCreature(local.Creature).Block;
        PlayerTurnPhase phaseBefore = before.State.GetPlayerCombatState(local).Phase;
        PlayerTurnPhase phaseAfter = sim.State.GetPlayerCombatState(local).Phase;
        int localSimBlock = sim.State.GetCreature(local.Creature).Block;
        int localSimHp = sim.State.GetCreature(local.Creature).CurrentHp;
        bool localAlive = sim.State.GetCreature(local.Creature).IsAlive;
        int aliveEnemies = sim.State.Enemies.Count(e => sim.State.GetCreature(e).IsAlive);
        CombatTerminalStamp? stamp = sim.TerminalStamp;
        var result = new
        {
            variant,
            outcome = stamp?.Outcome.ToString() ?? "InProgress",
            stampTurn = stamp?.PlayerTurn,
            originalDead,
            aliveEnemies,
            localSimHp,
            localAlive,
            localBlockBefore,
            localSimBlock,
            phaseBefore = phaseBefore.ToString(),
            phaseAfter = phaseAfter.ToString(),
        };
        Console.WriteLine($"MATE_WIN {JsonSerializer.Serialize(result)}");
        File.WriteAllText(Path.Combine(outDir, $"mate-win-{result.variant}.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

        check(originalDead, "teammate Body Slam must kill the enemy in the simulation");
        if (stock)
        {
            check(stamp?.Outcome != CombatTerminalOutcome.Victory, "a respawning enemy's death must not count as victory");
            check(aliveEnemies > 0, "the respawned enemy must be on the board");
        }
        else
        {
            check(stamp is { Outcome: CombatTerminalOutcome.Victory } && stamp.Value.PlayerTurn == turn,
                $"teammate kill must end combat as victory on turn {turn}, got {result.outcome}");
            check(localAlive && localSimHp == LocalHp, $"turn-end Regen and Burn must not run after the teammate wins (local hp {LocalHp}->{localSimHp})");
            check(phaseBefore == PlayerTurnPhase.Play && phaseAfter == phaseBefore,
                $"a teammate win must end combat at the play, before the local turn-end phases ({phaseBefore}->{phaseAfter})");
            check(localSimBlock == localBlockBefore, $"turn-end orb passives and block changes must not run after the teammate wins (local block {localBlockBefore}->{localSimBlock})");
        }
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
