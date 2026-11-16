using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    /// <summary>
    /// Virtual two/three/four-seat co-op scenario. It proves the local process
    /// waits for every teammate before Auto solves, that Ctrl+F8/Execute Turn
    /// deploys only the local hand, and that a co-op board change drops the
    /// remaining plan instead of playing stale actions.
    /// </summary>
    private async Task AssertCoopAutoExecuteAsync(CombatState combat, Player player)
    {
        if (combat.Players.Count < 2)
            throw new InvalidOperationException("COOP-AUTO-EXECUTE requires --player-count 2..4.");
        Player teammate = combat.Players.First(candidate => !ReferenceEquals(candidate, player));
        NGame host = NGame.Instance
            ?? throw new InvalidOperationException("COOP-AUTO-EXECUTE requires the game host.");
        int turn = player.PlayerCombatState?.TurnNumber
            ?? throw new InvalidOperationException("Local player has no combat state.");

        await WaitForSolverIdleAsync("coop_initial_idle");
        if (CombatManager.Instance.IsPlayerReadyToEndTurn(teammate))
            throw new InvalidOperationException("The teammate unexpectedly ended the turn before the wait fixture.");

        // 1. Auto goes last: keep waiting while the teammate is still playing.
        SolverController.SetFullAutoEnabledForTesting(true);
        using CancellationTokenSource waitCancellation = new(TimeSpan.FromSeconds(10));
        Task<bool> wait = SolverController.WaitForCoopStableBoardAsync(
            host, combat, turn, static () => true, waitCancellation.Token);
        await PumpForAsync(TimeSpan.FromMilliseconds(300), "coop_wait_observed");
        if (wait.IsCompleted)
            throw new InvalidOperationException("Auto stopped waiting before the teammate ended their turn.");
        if (!SolverController.IsWaitingForCoopBoard)
            throw new InvalidOperationException("Auto did not report that it is waiting for teammates.");

        MarkTeammatesReadyForTest(combat, player);
        bool stable = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        SolverController.SetFullAutoEnabledForTesting(false);
        if (!stable
            || !CombatStability.AreAllTeammatesReadyToEndTurn(combat)
            || !CombatStability.IsCoopBoardStable(combat))
        {
            throw new InvalidOperationException(
                $"Auto did not resume on the stable post-teammate board: {CombatStability.Describe(combat)}");
        }
        _completedChecks.Add("CoopAuto:WaitForTeammates:StableBoard");
        SolverController.CancelCoopWaitForTesting();

        // 2. A board change after the first deployed action discards the remaining plan.
        await PrepareLocalStrikesAsync(combat, player, 2);
        int teammateHandBefore = teammate.PlayerCombatState!.Hand.Cards.Count;
        int deployedBefore = SolverController.DeployedActionCountForTesting;
        SolverController.ExecuteSingleTurn();
        await PumpUntilAsync(
            () => SolverController.IsSearching || SolverController.IsDeploying,
            TimeSpan.FromSeconds(20),
            "coop_single_turn_started");
        await PumpUntilAsync(
            () => SolverController.DeployedActionCountForTesting > deployedBefore,
            TimeSpan.FromSeconds(30),
            "coop_first_action");
        teammate.Creature.GainBlockInternal(7);
        await PumpUntilAsync(
            () => !SolverController.IsSearching && !SolverController.IsDeploying,
            TimeSpan.FromSeconds(20),
            "coop_plan_discarded");
        await PumpForAsync(TimeSpan.FromMilliseconds(250), "coop_discard_settled");
        int deployedAfterDiscard = SolverController.DeployedActionCountForTesting - deployedBefore;
        if (deployedAfterDiscard != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one local action before the board changed, got {deployedAfterDiscard}.");
        }
        if (!SolverController.AutoStatus.Events.Any(entry =>
                entry.Detail.Contains("coop_board_changed", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Co-op board change was not recorded by the solver status.");
        }
        if (teammate.PlayerCombatState.Hand.Cards.Count != teammateHandBefore)
            throw new InvalidOperationException("The solver changed the teammate's hand.");
        _completedChecks.Add("CoopAuto:BoardChangeAfterFirstAction:RemainingPlanDiscarded");

        // 3. Execute Turn now (Ctrl+F8 path) plays only the local side and stops.
        await PrepareLocalStrikesAsync(combat, player, 2);
        MarkTeammatesReadyForTest(combat, player);
        int handBefore = player.PlayerCombatState!.Hand.Cards.Count;
        int teammateCardPlaysBefore = CombatManager.Instance.History.Entries
            .OfType<CardPlayStartedEntry>()
            .Count(entry => ReferenceEquals(entry.CardPlay.Card.Owner, teammate));
        int actionsBefore = SolverController.DeployedActionCountForTesting;
        SolverController.ExecuteSingleTurn();
        await PumpUntilAsync(
            () => SolverController.IsSearching || SolverController.IsDeploying,
            TimeSpan.FromSeconds(20),
            "coop_ctrl_f8_started");
        await PumpUntilAsync(
            () => !SolverController.IsSearching && !SolverController.IsDeploying,
            TimeSpan.FromSeconds(40),
            "coop_ctrl_f8_finished");
        if (SolverController.FullAutoEnabled)
            throw new InvalidOperationException("Execute Turn left Auto Mode enabled.");
        int actions = SolverController.DeployedActionCountForTesting - actionsBefore;
        int teammateCardPlaysAfter = CombatManager.Instance.History.Entries
            .OfType<CardPlayStartedEntry>()
            .Count(entry => ReferenceEquals(entry.CardPlay.Card.Owner, teammate));
        if (teammateCardPlaysAfter != teammateCardPlaysBefore)
            throw new InvalidOperationException("Execute Turn played a teammate's card.");
        if (actions < 1 || player.PlayerCombatState!.Hand.Cards.Count >= handBefore)
            throw new InvalidOperationException("Execute Turn did not play the local hand.");
        _completedChecks.Add($"CoopAuto:SingleTurnExecute:LocalActions={actions}:AutoStopped");
    }

    /// <summary>
    /// A virtual run still uses the singleplayer net service, whose
    /// <c>AllPlayersReadyToEndTurn</c> returns true as soon as any player readies.
    /// Populate the ready set directly so the fixture observes the real multiplayer
    /// predicate (<see cref="CombatManager.IsPlayerReadyToEndTurn"/>) without ending
    /// the local turn on the way there.
    /// </summary>
    private static void MarkTeammatesReadyForTest(CombatState combat, Player localPlayer)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object turnState = typeof(CombatManager).GetField("_turnState", flags)?.GetValue(CombatManager.Instance)
            ?? throw new InvalidOperationException("CombatManager has no active turn state.");
        object ready = turnState.GetType().GetProperty("PlayersReadyToEndTurn")?.GetValue(turnState)
            ?? throw new InvalidOperationException("CombatTurnState has no ready set.");
        foreach (Player teammate in combat.Players.Where(candidate => !ReferenceEquals(candidate, localPlayer)))
        {
            ((HashSet<Player>)ready).Add(teammate);
            if (!CombatManager.Instance.IsPlayerReadyToEndTurn(teammate))
                throw new InvalidOperationException("The teammate ready marker did not reach the combat state.");
        }
    }

    private async Task PrepareLocalStrikesAsync(CombatState combat, Player player, int count)
    {
        await WaitForSolverIdleAsync("coop_prepare_cards_idle");
        await ClearPlayerPilesAsync(player);
        for (int index = 0; index < count; index++)
        {
            await InjectCardAsync(combat, player,
                new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        }
        SetEnergy(player, 3);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        await NextFrameAsync();
    }

    private async Task WaitForSolverIdleAsync(string stage)
    {
        SetStage(stage);
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (SolverController.IsSearching || SolverController.IsDeploying)
        {
            EnsureWithinDeadline();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The solver did not become idle for the co-op fixture.");
            await NextFrameAsync();
        }
    }

    private async Task PumpUntilAsync(Func<bool> condition, TimeSpan timeout, string stage)
    {
        SetStage(stage);
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            EnsureWithinDeadline();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Co-op fixture timed out at stage {stage}.");
            await NextFrameAsync();
        }
    }

    private async Task PumpForAsync(TimeSpan duration, string stage)
    {
        SetStage(stage);
        DateTime deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            EnsureWithinDeadline();
            await NextFrameAsync();
        }
    }
}
