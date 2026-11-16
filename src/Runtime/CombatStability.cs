using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

/// <summary>
/// Single source for "the live combat will not change under us right now" checks.
/// The bug-report checkpoint gate and the co-op Auto wait share the same player-turn test.
/// </summary>
internal static class CombatStability
{
    internal static bool IsPlayerTurnStable(CombatState state)
        => CombatManager.Instance.IsInProgress
           && LocalContext.GetMe(state)?.PlayerCombatState?.Phase == PlayerTurnPhase.Play
           && !RunManager.Instance.ActionExecutor.IsRunning
           && !CombatManager.Instance.EndingPlayerTurnPhaseOne
           && !CombatManager.Instance.EndingPlayerTurnPhaseTwo;

    internal static bool AreAllTeammatesReadyToEndTurn(CombatState state)
    {
        Player? me = LocalContext.GetMe(state);
        if (me == null)
            return false;
        foreach (Player player in state.Players)
        {
            if (!ReferenceEquals(player, me) && !CombatManager.Instance.IsPlayerReadyToEndTurn(player))
                return false;
        }
        return true;
    }

    /// <summary>The board is stable and every teammate has already finished this player turn.</summary>
    internal static bool IsCoopBoardStable(CombatState state)
        => state.Players.Count > 1
           && IsPlayerTurnStable(state)
           && !CombatManager.Instance.PlayerActionsDisabled
           && AreAllTeammatesReadyToEndTurn(state);

    internal static string Describe(CombatState state)
    {
        Player? me = LocalContext.GetMe(state);
        return $"in_progress={CombatManager.Instance.IsInProgress} " +
               $"over_or_ending={CombatManager.Instance.IsOverOrEnding} " +
               $"side={state.CurrentSide} local_phase={me?.PlayerCombatState?.Phase} " +
               $"local_turn={me?.PlayerCombatState?.TurnNumber} " +
               $"actions_running={RunManager.Instance.ActionExecutor.IsRunning} " +
               $"current_action={RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.Id} " +
               $"ending={CombatManager.Instance.EndingPlayerTurnPhaseOne}/" +
               $"{CombatManager.Instance.EndingPlayerTurnPhaseTwo} " +
               $"actions_disabled={CombatManager.Instance.PlayerActionsDisabled} " +
               $"players=[{string.Join(',', state.Players.Select(player =>
                   $"{player.NetId}:hp={player.Creature.CurrentHp}:phase={player.PlayerCombatState?.Phase}:" +
                   $"turn={player.PlayerCombatState?.TurnNumber}:" +
                   $"ready={CombatManager.Instance.IsPlayerReadyToEndTurn(player)}"))}]";
    }
}

/// <summary>
/// Public board text used to drop a co-op plan when another player changed the fight.
/// It contains every value <see cref="LiveCombatStamp"/> already tracks plus each player's
/// visible HP, block, turn and phase, which are not part of the single-player state text.
/// </summary>
internal readonly record struct CoopBoardStamp(string StateText)
{
    internal static CoopBoardStamp Capture(CombatState state)
    {
        StringBuilder text = new(LiveCombatStamp.Capture(state).StateText);
        foreach (Player player in state.Players.OrderBy(player => player.NetId))
        {
            text.Append(";coop_player=").Append(player.NetId)
                .Append(':').Append(player.Creature.CurrentHp)
                .Append('/').Append(player.Creature.MaxHp)
                .Append('/').Append(player.Creature.Block)
                .Append(":phase=").Append((int)(player.PlayerCombatState?.Phase ?? PlayerTurnPhase.None))
                .Append(":turn=").Append(player.PlayerCombatState?.TurnNumber ?? -1);
        }
        return new CoopBoardStamp(text.ToString());
    }
}
