using System.Text;
using CombatSolver.NewSolver;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal enum UnEndTurnPollOutcome
{
    Idle,
    NotReady,
    CannotUnEnd,
    BaselineRecorded,
    Unchanged,
    GuardBlocked,
    ForcedEndTurn,
    NoBeneficialAction,
    Triggered,
}

internal static partial class SolverController
{
    internal const int MaxUnEndTurnsPerTurn = 4;
    private static bool _unEndTurnEventsHooked;

    internal static void EnsureUnEndTurnEventsHooked()
    {
        if (_unEndTurnEventsHooked)
            return;
        _unEndTurnEventsHooked = true;
        CombatManager.Instance.PlayerEndedTurn += OnPlayerEndedTurnForUnEnd;
        CombatManager.Instance.PlayerUnendedTurn += OnPlayerUnendedTurnForUnEnd;
        CombatManager.Instance.TurnStarted += OnTurnStartedForUnEnd;
    }

    private static void OnPlayerEndedTurnForUnEnd(Player player, bool canBackOut)
    {
        if (canBackOut)
            _combat.CannotBackOutPlayers.Remove(player.NetId);
        else
            _combat.CannotBackOutPlayers.Add(player.NetId);
    }

    private static void OnPlayerUnendedTurnForUnEnd(Player player)
        => _combat.CannotBackOutPlayers.Remove(player.NetId);

    private static void OnTurnStartedForUnEnd(CombatState _)
        => _combat.CannotBackOutPlayers.Clear();

    internal static bool CanLocalPlayerUnEndTurn(Player player)
        => CombatManager.Instance.IsInProgress
            && !CombatManager.Instance.IsOverOrEnding
            && !CombatManager.Instance.EndingPlayerTurnPhaseOne
            && !CombatManager.Instance.EndingPlayerTurnPhaseTwo
            && CombatManager.Instance.IsPlayerReadyToEndTurn(player)
            && !CombatManager.Instance.AllPlayersReadyToEndTurn()
            && !_combat.CannotBackOutPlayers.Contains(player.NetId);

    internal static Creature? ResolveLiveActionTarget(CombatState state, PlanAction action)
        => state.GetCreature(action.TargetCombatId);

    internal static bool IsValidLivePotionTarget(Player player, PotionModel potion, Creature? target)
    {
        if (target == null)
            return potion.IsValidTarget(null) || potion.IsValidTarget(player.Creature);
        return target.IsAlive && potion.IsValidTarget(target);
    }

    internal static string CaptureReadyToEndTurnFingerprint(CombatState state, Player player)
    {
        PlayerCombatState pcs = player.PlayerCombatState
            ?? throw new InvalidOperationException("Missing player combat state.");
        StringBuilder sb = new(256);
        sb.Append("turn=").Append(pcs.TurnNumber)
            .Append(";e=").Append(pcs.Energy)
            .Append(";s=").Append(pcs.Stars)
            .Append(";hp=").Append(player.Creature.CurrentHp).Append('/').Append(player.Creature.MaxHp)
            .Append(";blk=").Append(player.Creature.Block)
            .Append(";ocap=").Append(pcs.OrbQueue.Capacity)
            .Append(";orbs=");
        foreach (OrbModel orb in pcs.OrbQueue.Orbs)
            sb.Append(orb.Id.Entry).Append(',');
        sb.Append(";hand=");
        foreach (CardModel card in pcs.Hand.Cards)
        {
            sb.Append(card.Id.Entry)
                .Append('+').Append(card.CurrentUpgradeLevel)
                .Append(':').Append(card.EnergyCost.GetWithModifiers(CostModifiers.All))
                .Append(':').Append(card.CanPlay() ? '1' : '0')
                .Append(',');
        }
        sb.Append(";potions=");
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            PotionModel? potion = player.GetPotionAtSlotIndex(slot);
            if (potion != null)
                sb.Append(slot).Append(':').Append(potion.Id.Entry).Append(',');
        }
        sb.Append(";powers=");
        foreach (PowerModel power in player.Creature.Powers.OrderBy(p => p.Id.Entry, StringComparer.Ordinal))
            sb.Append(power.Id.Entry).Append(':').Append(power.Amount).Append(',');
        sb.Append(";players=");
        foreach (Player p in state.Players.OrderBy(p => p.NetId))
        {
            sb.Append(p.NetId)
                .Append(':').Append(p.Creature.CombatId)
                .Append(':').Append(p.Creature.CurrentHp)
                .Append(':').Append(p.Creature.Block)
                .Append(':').Append(p.Creature.IsAlive ? '1' : '0')
                .Append(',');
        }
        sb.Append(";enemies=");
        foreach (Creature enemy in state.Enemies.OrderBy(e => e.CombatId ?? 0))
        {
            sb.Append(enemy.CombatId)
                .Append(':').Append(enemy.CurrentHp)
                .Append(':').Append(enemy.Block)
                .Append(':').Append(enemy.IsAlive ? '1' : '0')
                .Append(',');
        }
        return sb.ToString();
    }

    internal static void RecordLocalReadyToEndTurnBaseline(CombatState state, Player player, int turn)
    {
        if (_combat.ReadyToEndTurnTurn != turn)
        {
            _combat.ReadyToEndTurnTurn = turn;
            _combat.LastUnEndTriggerFingerprint = null;
            _combat.UnEndTurnCountThisTurn = 0;
            _combat.EvaluatedUnEndFingerprintsThisTurn.Clear();
        }
        _combat.ReadyToEndTurnBaselineFingerprint = CaptureReadyToEndTurnFingerprint(state, player);
        _combat.SolverEndedTurn = turn;
        _combat.SolverEndedTurnSeenReady = false;
    }

    private static bool IsUnEndArmed(int turn)
        => _combat.FullAutoEnabled || _combat.SolverEndedTurn == turn;

    private static UnEndTurnPollOutcome InspectReadyToEndTurnCandidate(
        CombatState state,
        out Player? me,
        out int turn,
        out string fingerprint)
    {
        me = null;
        turn = -1;
        fingerprint = string.Empty;

        if (_solverDisabled
            || !_combat.FullAutoEnabled && _combat.SolverEndedTurn == null
            || _search != null
            || _deployment != null
            || _combat.UnEndTurnInFlight)
        {
            return UnEndTurnPollOutcome.Idle;
        }
        if (!CombatManager.Instance.IsInProgress
            || CombatManager.Instance.IsOverOrEnding
            || CombatManager.Instance.EndingPlayerTurnPhaseOne
            || CombatManager.Instance.EndingPlayerTurnPhaseTwo
            || state.CurrentSide != CombatSide.Player
            || RunManager.Instance.ActionExecutor.IsRunning)
        {
            return UnEndTurnPollOutcome.NotReady;
        }
        me = LocalContext.GetMe(state);
        // Auto is off and the turn the solver ended was later seen un-ended without the solver doing it: the
        // player took the turn back, so a later end turn is theirs and must not be un-ended. Requiring a prior
        // ready observation avoids disarming while the solver's own EndPlayerTurnAction is still in flight; the
        // solver's own re-deploy re-arms through RecordLocalReadyToEndTurnBaseline.
        if (!_combat.FullAutoEnabled
            && me?.PlayerCombatState is { Phase: PlayerTurnPhase.Play } ownState
            && _combat.SolverEndedTurn == ownState.TurnNumber)
        {
            if (CombatManager.Instance.IsPlayerReadyToEndTurn(me))
            {
                _combat.SolverEndedTurnSeenReady = true;
            }
            else if (_combat.SolverEndedTurnSeenReady)
            {
                _combat.SolverEndedTurn = null;
                _combat.SolverEndedTurnSeenReady = false;
                return UnEndTurnPollOutcome.Idle;
            }
        }
        if (me == null
            || !me.Creature.IsAlive
            || me.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } pcs
            || !CombatManager.Instance.IsPlayerReadyToEndTurn(me))
        {
            return UnEndTurnPollOutcome.NotReady;
        }

        turn = pcs.TurnNumber;
        if (!IsUnEndArmed(turn))
            return UnEndTurnPollOutcome.Idle;
        fingerprint = CaptureReadyToEndTurnFingerprint(state, me);

        if (_combat.ReadyToEndTurnTurn != turn)
        {
            _combat.ReadyToEndTurnTurn = turn;
            _combat.ReadyToEndTurnBaselineFingerprint = fingerprint;
            _combat.LastUnEndTriggerFingerprint = null;
            _combat.UnEndTurnCountThisTurn = 0;
            _combat.EvaluatedUnEndFingerprintsThisTurn.Clear();
            return UnEndTurnPollOutcome.BaselineRecorded;
        }

        if (_combat.ReadyToEndTurnBaselineFingerprint == null)
        {
            _combat.ReadyToEndTurnBaselineFingerprint = fingerprint;
            return UnEndTurnPollOutcome.BaselineRecorded;
        }

        if (string.Equals(_combat.ReadyToEndTurnBaselineFingerprint, fingerprint, StringComparison.Ordinal))
            return UnEndTurnPollOutcome.Unchanged;

        if (!CanLocalPlayerUnEndTurn(me))
        {
            _combat.ReadyToEndTurnBaselineFingerprint = fingerprint;
            return UnEndTurnPollOutcome.CannotUnEnd;
        }

        if (_combat.UnEndTurnCountThisTurn >= MaxUnEndTurnsPerTurn
            || _combat.EvaluatedUnEndFingerprintsThisTurn.Contains(fingerprint)
            || string.Equals(_combat.LastUnEndTriggerFingerprint, fingerprint, StringComparison.Ordinal))
        {
            _combat.ReadyToEndTurnBaselineFingerprint = fingerprint;
            return UnEndTurnPollOutcome.GuardBlocked;
        }

        if (NewFairSolver.IsForcedEndTurnLive(me))
        {
            _combat.EvaluatedUnEndFingerprintsThisTurn.Add(fingerprint);
            _combat.ReadyToEndTurnBaselineFingerprint = fingerprint;
            return UnEndTurnPollOutcome.ForcedEndTurn;
        }

        return UnEndTurnPollOutcome.Triggered;
    }

    private static void PollReadyToEndTurnWatcher(CombatState state)
    {
        if (NGame.Instance is not { } host)
            return;
        UnEndTurnPollOutcome outcome = InspectReadyToEndTurnCandidate(
            state,
            out Player? me,
            out int turn,
            out string fingerprint);
        if (outcome != UnEndTurnPollOutcome.Triggered || me == null)
            return;

        _combat.EvaluatedUnEndFingerprintsThisTurn.Add(fingerprint);
        _combat.UnEndTurnInFlight = true;

        SolverDisplayNames names;
        CombatRootSnapshot[] worlds;
        int knownTop;
        try
        {
            names = SolverDisplayNames.Capture(state);
            ulong seed = NewFairSolver.VisibleSeed(state, me);
            knownTop = FairDrawPileKnowledge.Capture(me.PlayerCombatState!.DrawPile).KnownTop;
            int sampleCount = NewFairSolver.QuickUnEndCheckConfig.Samples;
            worlds = new CombatRootSnapshot[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                worlds[i] = CombatRootSnapshot.Capture(state, fair: FairObservation.FromSeed(seed + (ulong)i));
        }
        catch (Exception ex)
        {
            _combat.UnEndTurnInFlight = false;
            _combat.ReadyToEndTurnBaselineFingerprint = fingerprint;
            Entry.Logger.Warn($"[CombatSolver/Fair] UN_END_TURN_CAPTURE_FAILED turn={turn} error={ex.Message}");
            return;
        }

        Task task = StartCombatDeferredOperation(async token =>
        {
            try
            {
                bool beneficial = await Task.Run(
                    () => NewFairSolver.HasBeneficialAction(worlds, names, knownTop, token),
                    token);
                if (!beneficial)
                {
                    _combat.ReadyToEndTurnBaselineFingerprint = fingerprint;
                    Entry.Logger.Info(
                        $"[CombatSolver/Fair] UN_END_TURN_SKIPPED turn={turn} reason=end_turn_still_best");
                    return;
                }
                if (!CanExecuteUnEndTurnNow(state, me, turn))
                    return;

                _combat.UnEndTurnCountThisTurn++;
                _combat.LastUnEndTriggerFingerprint = fingerprint;
                SolverAutoStatus.Record(
                    "un_end_turn",
                    $"turn={turn} count={_combat.UnEndTurnCountThisTurn}/{MaxUnEndTurnsPerTurn} reason=teammate_gift");
                Entry.Logger.Info(
                    $"[CombatSolver/Fair] UN_END_TURN_TRIGGERED turn={turn} " +
                    $"count={_combat.UnEndTurnCountThisTurn}/{MaxUnEndTurnsPerTurn}");

                GameAction undoAction = await EnqueueAndCaptureActionAsync(
                    candidate => candidate is UndoEndPlayerTurnAction && candidate.OwnerId == me.NetId,
                    () => RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                        new UndoEndPlayerTurnAction(me, turn)),
                    token);
                await undoAction.CompletionTask.WaitAsync(token);

                long deadline = System.Environment.TickCount64 + 5_000;
                while (CombatManager.Instance.IsInProgress
                       && !CombatManager.Instance.IsOverOrEnding
                       && ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                       && IsUnEndArmed(turn)
                       && me.PlayerCombatState?.TurnNumber == turn
                       && (CombatManager.Instance.IsPlayerReadyToEndTurn(me)
                           || CombatManager.Instance.PlayerActionsDisabled
                           || RunManager.Instance.ActionExecutor.IsRunning))
                {
                    if (System.Environment.TickCount64 >= deadline)
                    {
                        Entry.Logger.Warn($"[CombatSolver/Fair] UN_END_TURN_TIMEOUT turn={turn}");
                        return;
                    }
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                    token.ThrowIfCancellationRequested();
                }

                if (!IsUnEndArmed(turn)
                    || !CombatManager.Instance.IsInProgress
                    || CombatManager.Instance.IsOverOrEnding
                    || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                    || CombatManager.Instance.IsPlayerReadyToEndTurn(me)
                    || !CanSolve(state, out _))
                {
                    return;
                }

                _combat.LatestResult = null;
                _combat.LatestStamp = null;
                _combat.ContinuationSource = null;
                _combat.ReadyToEndTurnBaselineFingerprint = null;
                _combat.UnEndTurnInFlight = false;
                if (_combat.FullAutoEnabled)
                    RequestSearch(host, state, SearchReason.FullAuto);
                else
                    RequestSearch(host, state, SearchReason.SingleTurn, deployWhenReady: true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn($"[CombatSolver/Fair] UN_END_TURN_FAILED turn={turn} error={ex.Message}");
            }
            finally
            {
                _combat.UnEndTurnInFlight = false;
            }
        });
        if (UnattendedAsyncActivityTracker.IsRequestActive)
            task = UnattendedAsyncActivityTracker.Track(task);
        _ = task;
    }

    private static bool CanExecuteUnEndTurnNow(CombatState state, Player me, int turn)
        => !_solverDisabled
            && IsUnEndArmed(turn)
            && CombatManager.Instance.IsInProgress
            && !CombatManager.Instance.IsOverOrEnding
            && !CombatManager.Instance.EndingPlayerTurnPhaseOne
            && !CombatManager.Instance.EndingPlayerTurnPhaseTwo
            && ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
            && state.CurrentSide == CombatSide.Player
            && me.Creature.IsAlive
            && me.PlayerCombatState is { Phase: PlayerTurnPhase.Play } pcs
            && pcs.TurnNumber == turn
            && CombatManager.Instance.IsPlayerReadyToEndTurn(me)
            && CanLocalPlayerUnEndTurn(me)
            && !RunManager.Instance.ActionExecutor.IsRunning;

    internal static async Task<UnEndTurnPollOutcome> TryPollAndUnEndTurnForTestingAsync(
        CombatState state,
        Func<CombatState, Player, bool>? beneficialCheck = null,
        CancellationToken token = default)
    {
        UnEndTurnPollOutcome outcome = InspectReadyToEndTurnCandidate(
            state,
            out Player? me,
            out int turn,
            out string fingerprint);
        if (outcome != UnEndTurnPollOutcome.Triggered || me == null)
            return outcome;

        _combat.EvaluatedUnEndFingerprintsThisTurn.Add(fingerprint);
        _combat.UnEndTurnInFlight = true;
        try
        {
            bool beneficial = beneficialCheck != null
                ? beneficialCheck(state, me)
                : NewFairSolver.DecideLive(state, me, NewFairSolver.QuickUnEndCheckConfig, token)
                    .CommittedActions.Any(action => action.Kind != PlanActionKind.EndTurn);
            if (!beneficial)
            {
                _combat.ReadyToEndTurnBaselineFingerprint = fingerprint;
                return UnEndTurnPollOutcome.NoBeneficialAction;
            }
            if (!CanExecuteUnEndTurnNow(state, me, turn))
                return UnEndTurnPollOutcome.NotReady;

            _combat.UnEndTurnCountThisTurn++;
            _combat.LastUnEndTriggerFingerprint = fingerprint;

            GameAction undoAction = await EnqueueAndCaptureActionAsync(
                candidate => candidate is UndoEndPlayerTurnAction && candidate.OwnerId == me.NetId,
                () => RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                    new UndoEndPlayerTurnAction(me, turn)),
                token);
            await undoAction.CompletionTask.WaitAsync(token);
            _combat.ReadyToEndTurnBaselineFingerprint = null;
            return UnEndTurnPollOutcome.Triggered;
        }
        finally
        {
            _combat.UnEndTurnInFlight = false;
        }
    }
}
