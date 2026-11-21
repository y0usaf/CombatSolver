using System.Reflection;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertTurnCounterResetFork(CombatRootSnapshot root)
    {
        CombatPredictionSimulator parent = root.ForkSimulator();
        SimulatedCombatState parentState = (SimulatedCombatState)parent.State.CombatState;
        var player = parentState.Players[0];
        AssertRootColorlessGenerationPoolCache(parent, player);
        parentState.BeginSideTurn(player.Creature);
        FieldInfo counterField = typeof(SimulatedCombatState).GetField(
            "_creatureTurnCounters", BindingFlags.NonPublic | BindingFlags.Instance)!;
        MethodInfo setCounter = typeof(SimulatedCombatState).GetMethod(
            "SetCreatureTurnCounter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Type counterEnum = typeof(SimulatedCombatState).GetNestedType(
            "CreatureTurnCounter", BindingFlags.NonPublic)!;
        object cardsPlayedEnum = Enum.Parse(counterEnum, "CardsPlayed");
        setCounter.Invoke(parentState, [player.Creature, cardsPlayedEnum, 7]);
        CombatPredictionSimulator child = parent.Fork();
        SimulatedCombatState childState = (SimulatedCombatState)child.State.CombatState;
        childState.BeginSideTurn(player.Creature);
        if (parentState.GetCardsPlayedThisTurn(player.Creature) != 7
            || childState.GetCardsPlayedThisTurn(player.Creature) != 0)
            throw new InvalidOperationException("回合计数重置未保持父子隔离或显式零。");
        CombatPredictionSimulator grandchild = child.Fork();
        SimulatedCombatState grandchildState = (SimulatedCombatState)grandchild.State.CombatState;
        object childCounters = counterField.GetValue(childState)!;
        object grandchildCounters = counterField.GetValue(grandchildState)!;
        FieldInfo storage = childCounters.GetType().GetField(
            "_storage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        StateFingerprintBuilder before = new();
        grandchildState.AppendFingerprint(ref before, grandchild);
        grandchildState.BeginSideTurn(player.Creature);
        StateFingerprintBuilder after = new();
        grandchildState.AppendFingerprint(ref after, grandchild);
        if (before.Finish() != after.Finish()
            || !ReferenceEquals(storage.GetValue(childCounters), storage.GetValue(grandchildCounters)))
            throw new InvalidOperationException("已有零计数的回合重置改变状态或复制共享存储。");
        setCounter.Invoke(grandchildState, [player.Creature, cardsPlayedEnum, 3]);
        if (childState.GetCardsPlayedThisTurn(player.Creature) != 0
            || parentState.GetCardsPlayedThisTurn(player.Creature) != 7
            || grandchildState.GetCardsPlayedThisTurn(player.Creature) != 3)
            throw new InvalidOperationException("省略零写入后后续计数写入破坏了 Fork 隔离。");
    }
}
