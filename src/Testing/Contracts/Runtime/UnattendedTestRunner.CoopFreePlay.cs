using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    /// <summary>
    /// Virtual two/three/four-seat co-op scenario for the Free play pace. It proves Auto plays the
    /// local hand while a teammate is still thinking, that a teammate playing a real card mid-turn
    /// drops the stale plan and replans without waiting, that the replanned turn continues with the
    /// remaining local cards, that no card is played twice, that the teammate's side is never
    /// played for them, and that Ctrl+F8 still plays exactly one local turn.
    /// </summary>
    private async Task AssertCoopFreePlayExecuteAsync(CombatState combat, Player player)
    {
        if (combat.Players.Count < 2)
            throw new InvalidOperationException("COOP-FREE-PLAY-EXECUTE requires --player-count 2..4.");
        Player teammate = combat.Players.First(candidate => !ReferenceEquals(candidate, player));
        NGame host = NGame.Instance
            ?? throw new InvalidOperationException("COOP-FREE-PLAY-EXECUTE requires the game host.");
        int turn = player.PlayerCombatState?.TurnNumber
            ?? throw new InvalidOperationException("Local player has no combat state.");
        SolverSettingsData settingsBefore = SolverSettings.Current;
        SolverController.SetCoopPlayModeForTesting(SolverCoopPlayMode.FreePlay);
        try
        {
            // 0. Free play is the default, and a chosen pace survives a settings reload.
            SolverSettingsData defaults = SolverSettings.DeserializeForTesting("{}");
            if (defaults.CoopPlayMode != SolverCoopPlayMode.FreePlay)
            {
                throw new InvalidOperationException(
                    $"Free play is not the default pace for a settings file without the property: {defaults.CoopPlayMode}.");
            }
            SolverSettingsData reloaded = SolverSettings.RoundTripForTesting(
                settingsBefore with { CoopPlayMode = SolverCoopPlayMode.Wait });
            if (reloaded.CoopPlayMode != SolverCoopPlayMode.Wait)
            {
                throw new InvalidOperationException(
                    $"The chosen pace does not survive a settings reload: {reloaded.CoopPlayMode}.");
            }
            _completedChecks.Add("CoopFreePlay:Settings:DefaultFreePlayAndPersistedChoice");

            await WaitForSolverIdleAsync("free_play_initial_idle");
            if (CombatManager.Instance.IsPlayerReadyToEndTurn(teammate))
            {
                throw new InvalidOperationException(
                    "The teammate unexpectedly ended the turn before the free-play fixture.");
            }

            // 1. Free play answers the board-stability question without looking at the teammates.
            using (CancellationTokenSource probe = new(TimeSpan.FromSeconds(5)))
            {
                Task<bool> wait = SolverController.WaitForCoopStableBoardAsync(
                    host, combat, turn, static () => true, probe.Token);
                await PumpForAsync(TimeSpan.FromMilliseconds(150), "free_play_probe");
                if (!wait.IsCompleted)
                    throw new InvalidOperationException("Free play still waits for the teammates.");
                if (!await wait)
                {
                    throw new InvalidOperationException(
                        "Free play rejected a board that the teammates have not settled.");
                }
            }
            if (SolverController.IsWaitingForCoopBoard)
                throw new InvalidOperationException("Free play reported a teammate wait.");
            _completedChecks.Add("CoopFreePlay:DoesNotWaitForTeammates");

            // 2. Auto plays the local hand while the teammate is still thinking.
            await PrepareLocalStrikesAsync(combat, player, 1);
            int teammatePlaysBefore = CountCardPlays(teammate);
            int localPlaysBefore = CountCardPlays(player);
            int deployedBefore = SolverController.DeployedActionCountForTesting;
            SolverController.SetFullAuto(host, combat, true);
            await PumpUntilAsync(
                () => SolverController.DeployedActionCountForTesting > deployedBefore,
                TimeSpan.FromSeconds(60),
                "free_play_first_local_action");
            if (CombatManager.Instance.IsPlayerReadyToEndTurn(teammate))
            {
                throw new InvalidOperationException(
                    "The teammate became ready before the first free-play action.");
            }
            if (SolverController.IsWaitingForCoopBoard)
                throw new InvalidOperationException("Auto waited for the teammate in Free play.");
            if (CountCardPlays(teammate) != teammatePlaysBefore)
                throw new InvalidOperationException("Free play Auto played a teammate's card.");
            int localActions = CountCardPlays(player) - localPlaysBefore;
            if (localActions < 1 || !SolverController.FullAutoEnabled)
            {
                throw new InvalidOperationException(
                    $"Free play did not play the local hand: local_actions={localActions}.");
            }
            _completedChecks.Add($"CoopFreePlay:ActsWhileTeammateStillPlaying:LocalActions={localActions}");

            // 3. A teammate's move mid-turn drops the stale plan and replans without waiting for
            //    them to end the turn. The board change is injected on the teammate's creature:
            //    driving another player's card through this single-process fixture crashes the
            //    native action path, so only a real network teammate can exercise that half.
            int playsAtChange = CountCardPlays(player);
            int searchesAtChange = SolverController.SearchesStartedForTesting;
            int discardsAtChange = CountCoopBoardChanges();
            teammate.Creature.GainBlockInternal(7);
            await PumpUntilAsync(
                () => CountCoopBoardChanges() > discardsAtChange,
                TimeSpan.FromSeconds(20),
                "free_play_board_change_observed");
            if (CountCardPlays(player) != playsAtChange)
            {
                throw new InvalidOperationException(
                    "Free play kept playing the stale plan after the teammate played a card.");
            }
            if (SolverController.IsWaitingForCoopBoard)
                throw new InvalidOperationException("Free play waited for teammates after dropping the plan.");
            await PumpUntilAsync(
                () => SolverController.SearchesStartedForTesting > searchesAtChange,
                TimeSpan.FromSeconds(20),
                "free_play_replanned");
            if (!SolverController.FullAutoEnabled)
                throw new InvalidOperationException("Free play stopped Auto Mode after the board changed.");
            _completedChecks.Add("CoopFreePlay:BoardChangeAfterFirstAction:StalePlanDroppedAndReplanned");

            // Keep the player turn alive for the single-turn fixture below.
            SolverController.SetFullAutoEnabledForTesting(false);
            SolverController.CancelCoopWaitForTesting();
            await PumpUntilAsync(
                () => !SolverController.IsSearching && !SolverController.IsDeploying,
                TimeSpan.FromSeconds(60),
                "free_play_replan_settled");
            if (player.PlayerCombatState!.Phase != PlayerTurnPhase.Play)
                throw new InvalidOperationException("The discarded free-play turn ended unexpectedly.");

            // 4. Ctrl+F8 still plays one local turn in Free play and stops.
            // The virtual run keeps the single-player net service, so ending the local turn also
            // advances the round and legitimately redraws the teammate's hand: only the local
            // card plays and the teammate's own card plays are evidence here.
            await PrepareLocalStrikesAsync(combat, player, 2);
            int localPlaysBeforeExecute = CountCardPlays(player);
            int teammatePlaysBeforeExecute = CountCardPlays(teammate);
            int actionsBefore = SolverController.DeployedActionCountForTesting;
            SolverController.ExecuteSingleTurn();
            await PumpUntilAsync(
                () => SolverController.IsSearching || SolverController.IsDeploying,
                TimeSpan.FromSeconds(20),
                "free_play_ctrl_f8_started");
            await PumpUntilAsync(
                () => !SolverController.IsSearching && !SolverController.IsDeploying,
                TimeSpan.FromSeconds(60),
                "free_play_ctrl_f8_finished");
            if (SolverController.FullAutoEnabled)
                throw new InvalidOperationException("Execute Turn left Auto Mode enabled in Free play.");
            int actions = SolverController.DeployedActionCountForTesting - actionsBefore;
            int localPlays = CountCardPlays(player) - localPlaysBeforeExecute;
            if (actions < 1 || localPlays < 1)
            {
                throw new InvalidOperationException(
                    $"Execute Turn did not play the local hand in Free play: actions={actions} local_plays={localPlays}.");
            }
            if (CountCardPlays(teammate) != teammatePlaysBeforeExecute)
                throw new InvalidOperationException("Execute Turn played a teammate's card in Free play.");
            AssertNoCardPlayedTwice(player);
            _completedChecks.Add($"CoopFreePlay:SingleTurnExecute:LocalActions={actions}:AutoStopped");

            // 5. The next player turn replans after a native teammate card and then plays the rest
            //    of the local hand itself: the discarded turn must continue, not stall.
            await PumpUntilAsync(
                () => CombatManager.Instance.IsInProgress
                      && LocalContext.GetMe(combat)?.PlayerCombatState is { Phase: PlayerTurnPhase.Play } live
                      && live.TurnNumber > turn,
                TimeSpan.FromSeconds(90),
                "free_play_next_turn");
            int nextTurn = player.PlayerCombatState!.TurnNumber;
            await PrepareLocalStrikesAsync(combat, player, 3);
            int replanPlaysBefore = CountCardPlays(player);
            int replanTeammatePlaysBefore = CountCardPlays(teammate);
            int replanDeployedBefore = SolverController.DeployedActionCountForTesting;
            int replanSearchesBefore = SolverController.SearchesStartedForTesting;
            int replanDiscardsBefore = CountCoopBoardChanges();
            SolverController.SetFullAuto(host, combat, true);
            await PumpUntilAsync(
                () => SolverController.DeployedActionCountForTesting > replanDeployedBefore,
                TimeSpan.FromSeconds(60),
                "free_play_replan_first_action");
            teammate.Creature.GainBlockInternal(7);
            await PumpUntilAsync(
                () => CountCoopBoardChanges() > replanDiscardsBefore,
                TimeSpan.FromSeconds(20),
                "free_play_replan_discard");
            await PumpUntilAsync(
                () => SolverController.SearchesStartedForTesting > replanSearchesBefore,
                TimeSpan.FromSeconds(20),
                "free_play_replan_search");
            await PumpUntilAsync(
                () => !SolverController.IsSearching && !SolverController.IsDeploying,
                TimeSpan.FromSeconds(90),
                "free_play_replan_finished");
            int replanPlays = CountCardPlays(player) - replanPlaysBefore;
            if (replanPlays < 2)
            {
                throw new InvalidOperationException(
                    $"The replanned free-play turn did not continue the local hand: plays={replanPlays} turn={nextTurn}.");
            }
            if (CountCardPlays(teammate) != replanTeammatePlaysBefore)
                throw new InvalidOperationException("The replanned turn played a teammate's card.");
            AssertNoCardPlayedTwice(player);
            _completedChecks.Add($"CoopFreePlay:TeammateChangeMidTurn:ReplannedLocalActions={replanPlays}");
        }
        finally
        {
            SolverController.SetFullAutoEnabledForTesting(false);
            SolverController.CancelCoopWaitForTesting();
            SolverController.SetCoopPlayModeForTesting(settingsBefore.CoopPlayMode);
        }
    }

    private static int CountCoopBoardChanges()
        => SolverController.AutoStatus.Events.Count(entry =>
            entry.Detail.Contains("coop_board_changed", StringComparison.Ordinal));

    /// <summary>A stale plan must never play the same card instance twice.</summary>
    private static void AssertNoCardPlayedTwice(Player player)
    {
        HashSet<CardModel> played = new(ReferenceEqualityComparer.Instance);
        foreach (CardPlayStartedEntry entry in CombatManager.Instance.History.Entries.OfType<CardPlayStartedEntry>())
        {
            if (!ReferenceEquals(entry.CardPlay.Card.Owner, player))
                continue;
            if (!played.Add(entry.CardPlay.Card))
            {
                throw new InvalidOperationException(
                    $"The solver played {entry.CardPlay.Card.Id.Entry} twice in this co-op turn.");
            }
        }
    }
}
