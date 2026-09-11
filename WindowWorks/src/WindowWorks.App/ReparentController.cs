using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowWorks.App.UI;

namespace WindowWorks.App
{
    /// <summary>
    /// Orchestrates the Window Reparenting feature's "pop out and reparent" flow
    /// (docs/REPARENT_FEATURE_PLAN.md §14 Phase 1, items 1+3+4+5): drives an interactive
    /// ancestor-chain picker session (§6.1-§6.4, <see cref="WindowPickerSession"/>) when the
    /// reparent hotkey is pressed, then drives <see cref="ReparentEngine"/> +
    /// <see cref="ReparentHostWindow"/> to save state, reparent, and (on host close) restore it.
    /// Tracks all currently-reparented windows via <see cref="ReparentTrackingList"/> (§14 Phase 1
    /// item 5) so multiple different windows can be reparented concurrently, each into its own
    /// host frame, subject to the single-owner rule (the *same* target window cannot be
    /// reparented twice at once).
    ///
    /// Passes through whether the pick was an ancestor-chain child HWND (vs. a whole top-level
    /// window) to <see cref="ReparentEngine.SaveOriginalState"/>, so §8 step 8's
    /// restore-to-original-parent branch is exercised for child-HWND picks.
    /// </summary>
    public sealed class ReparentController : IDisposable
    {
        private const string CropOverlapWarningMessage = "This overlaps a region shown in another reparented crop; that view may now show empty space.";
        private const string CropOverlapWarningTitle = "Window Reparenting Notice";

        private static readonly object s_ownershipLeaseGate = new();
        private static bool s_processOwnershipLeaseHeld;

        private readonly ReparentEngine _engine = new();
        private readonly ReparentTrackingList _trackingList = new();
        private readonly Models.AppSettings _settings;
        private readonly ReparentWinEventWatcher _winEventWatcher = new();
        private readonly ReparentCrashRecoveryStore _crashRecoveryStore;
        private readonly Mutex _ownershipMutex;
        private readonly bool _ownsReparenting;
        private readonly bool _ownsProcessOwnershipLease;
        private WindowPickerSession? _activeSession;
        private CropRectSelectionWindow? _activeCropSelection;
        private bool _disposed;

        /// <summary>
        /// <paramref name="settings"/> supplies the "Allow resizing reparented child elements"
        /// toggle (docs/REPARENT_FEATURE_PLAN.md §9) consulted at reparent time (§14 Phase 1 item
        /// 4). Defaults to a fresh <see cref="Models.AppSettings"/> (i.e. the setting's default,
        /// off) if not supplied, so existing callers/tests that construct this with no arguments
        /// keep working unchanged.
        ///
        /// Also starts a <see cref="ReparentWinEventWatcher"/> (§14 Phase 1 item 8) that detects
        /// a currently-reparented target being destroyed externally (e.g. the source app crashes
        /// or is force-closed while embedded), replacing the earlier polling-based approach.
        ///
        /// <paramref name="crashRecoveryStore"/> supplies the on-disk crash-recovery state file
        /// (§14 Phase 1 item 10). Defaults to a fresh <see cref="ReparentCrashRecoveryStore"/>
        /// (its own default constructor resolves the real %APPDATA%\WindowWorks\ path) if not
        /// supplied, so existing callers/tests keep working unchanged.
        /// </summary>
        public ReparentController(Models.AppSettings? settings = null, ReparentCrashRecoveryStore? crashRecoveryStore = null)
        {
            _settings = settings ?? new Models.AppSettings();
            _crashRecoveryStore = crashRecoveryStore ?? new ReparentCrashRecoveryStore();
            _ownershipMutex = new Mutex(false, @"Local\WindowWorks.ReparentController");
            (_ownsReparenting, _ownsProcessOwnershipLease) = TryAcquireOwnership(_ownershipMutex);
            if (_ownsReparenting)
            {
                _winEventWatcher.WindowDestroyed += OnWinEventWindowDestroyed;
                _winEventWatcher.Start();
            }
        }

        /// <summary>
        /// Handles an <c>EVENT_OBJECT_DESTROY</c> notification for any top-level window in the
        /// system (§14 Phase 1 item 8): no-ops unless the destroyed HWND matches a currently-
        /// tracked reparented target, in which case the host frame is cleaned up gracefully
        /// (closed) instead of being left showing a blank/dead frame. Since the target HWND is
        /// already gone by the time this fires, this does not attempt
        /// <see cref="ReparentEngine.RestoreOriginalState"/> (there's nothing left to restore —
        /// the window itself no longer exists); it only removes the tracking-list entry, the
        /// crash-recovery state-file entry (§14 Phase 1 item 10 — nothing left to recover for a
        /// window that's actually gone), and closes the now-orphaned host frame.
        /// </summary>
        private void OnWinEventWindowDestroyed(IntPtr destroyedHwnd)
        {
            ReparentEngine.InvalidateCapturedIdentities(destroyedHwnd);
            if (!_ownsReparenting || _disposed)
            {
                return;
            }

            var entry = _trackingList.Find(destroyedHwnd);
            if (entry is null || entry.State == ReparentEntryState.Restoring)
            {
                // Not tracked, or already being restored via the normal Close/Restore path —
                // avoid double-handling the same entry (§14 Phase 1 item 8's race-with-normal-
                // close note).
                return;
            }

            entry.State = ReparentEntryState.Restoring;
            bool recoveryRemoved = _crashRecoveryStore.Remove(destroyedHwnd);
            if (recoveryRemoved)
            {
                _trackingList.Remove(entry);
            }
            else
            {
                entry.State = ReparentEntryState.CleanupPending;
            }
            try
            {
                entry.Host.NotifyRestoreAlreadyHandled(RestoreOutcome.TargetGone);
                entry.Host.Close();
            }
            catch { }
        }

        /// <summary>
        /// Exposes the tracking list read-only so callers (e.g. a future "Reset Reparenting" tray
        /// menu item, §14 Phase 1 item 6) can inspect currently-reparented windows.
        /// </summary>
        public ReparentTrackingList TrackingList => _trackingList;

        /// <summary>
        /// Invoked when the reparent hotkey is pressed. Starts an interactive ancestor-chain
        /// picker session (§6.1-§6.4) at the current cursor position; re-invoking the hotkey while
        /// a session is already active cancels that session first (§6.1), rather than starting a
        /// second, overlapping one.
        ///
        /// Settings-gated (§9, §14 Phase 1 item 11): a no-op if either the master "Enable Window
        /// Reparenting" toggle or the "Pop Out and Reparent" sub-toggle is off (Phase 1 has no
        /// other picker action besides whole-window/ancestor-chain pop-out, so both must be on).
        /// This makes the hotkey fully inert rather than merely hiding UI — acceptance-check item
        /// 5's "disabled path" requires no picker to be invocable at all, not just no stray UI.
        /// </summary>
        public void InvokePicker()
        {
            if (!_ownsReparenting || _disposed || !_settings.EnableWindowReparenting || !_settings.EnablePopOutAndReparent)
            {
                return;
            }

            _activeSession?.Cancel();
            _activeCropSelection?.Close();

            var session = new WindowPickerSession((uint)Environment.ProcessId, _settings.EnableCropAndReparent);
            _activeSession = session;
            session.Confirmed += (_, entry) =>
            {
                if (ReferenceEquals(_activeSession, session))
                {
                    _activeSession = null;
                }
                OnPicked(entry.Hwnd, isChildHwndPick: !entry.IsTopLevel, expectedIdentity: entry);
            };
            session.CropRequested += (_, entry) =>
            {
                if (ReferenceEquals(_activeSession, session))
                {
                    _activeSession = null;
                }
                StartCropSelection(entry);
            };
            session.Cancelled += (_, _) =>
            {
                if (ReferenceEquals(_activeSession, session))
                {
                    _activeSession = null;
                }
            };
            session.Start();
        }

        /// <summary>
        /// Cancels any currently-active picker session (§14 Phase 1 item 11's "toggle changed
        /// while a picker session is open" rule, §9): call this when the master toggle or the
        /// "Pop Out and Reparent" sub-toggle is switched off while a picker overlay might be
        /// showing, so the session doesn't continue offering an action Settings just disabled.
        /// No-op if no session is active. Never affects already-reparented content (Close/Restore
        /// and "Reset Reparenting" are untouched by this or any settings-toggle state).
        /// </summary>
        public void CancelActivePicker()
        {
            if (!_ownsReparenting || _disposed)
            {
                return;
            }

            _activeSession?.Cancel();
            _activeCropSelection?.Close();
        }

        private void StartCropSelection(AncestorChainEntry entry)
        {
            if (!_ownsReparenting || _disposed)
            {
                return;
            }

            if (entry.Hwnd == IntPtr.Zero || !NativeMethods.IsWindow(entry.Hwnd))
            {
                ShowCropFailure();
                return;
            }

            if (!ReparentEngine.VerifyWindowIdentity(
                entry.Hwnd,
                entry.ProcessId,
                entry.ProcessStartTimeUtc,
                entry.ClassName,
                entry.AutomationRuntimeId,
                entry.CapturedIdentity))
            {
                ShowCropFailure();
                return;
            }

            var selection = new CropRectSelectionWindow(entry.Hwnd);
            _activeCropSelection = selection;
            selection.RectConfirmed += (_, rect) =>
            {
                if (ReferenceEquals(_activeCropSelection, selection))
                {
                    _activeCropSelection = null;
                }

                var cropRect = new CropRectGeometry.NativeMethods.RECT
                {
                    Left = rect.Left,
                    Top = rect.Top,
                    Right = rect.Right,
                    Bottom = rect.Bottom
                };
                if (!ReparentEngine.VerifyWindowIdentity(
                    entry.Hwnd,
                    entry.ProcessId,
                    entry.ProcessStartTimeUtc,
                    entry.ClassName,
                    entry.AutomationRuntimeId,
                    entry.CapturedIdentity))
                {
                    ShowCropFailure();
                    return;
                }
                if (!CropRectGeometry.TryCompute(entry.Hwnd, cropRect, out var geometry))
                {
                    System.Windows.MessageBox.Show(
                        "WindowWorks couldn't determine the selected crop region. The window was not reparented.",
                        "Crop and Reparent Failed",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }

                OnPicked(
                    entry.Hwnd,
                    isChildHwndPick: !entry.IsTopLevel,
                    geometry,
                    expectedIdentity: entry,
                    cropRectScreen: cropRect);
            };
            selection.Cancelled += (_, _) =>
            {
                if (ReferenceEquals(_activeCropSelection, selection))
                {
                    _activeCropSelection = null;
                }
            };
            selection.Show();
        }

        /// <summary>
        /// Continues the reparent flow once the picker has resolved a target (§14 Phase 1 items
        /// 1+3+4+5): validates the pick, saves its original state (passing through
        /// <paramref name="isChildHwndPick"/> so §8 step 8's restore-to-original-parent branch is
        /// used for ancestor-chain child-HWND picks), and reparents it into a new
        /// <see cref="ReparentHostWindow"/>. No-ops (silently) if the resolved window is already
        /// tracked (single-owner limitation, §4), no longer valid, or is this process's own UI.
        /// </summary>
        private void OnPicked(
            IntPtr target,
            bool isChildHwndPick,
            CropRectGeometry.Result? cropGeometry = null,
            ReparentEngine.ReparentedWindowState? savedState = null,
            AncestorChainEntry? expectedIdentity = null,
            CropRectGeometry.NativeMethods.RECT? cropRectScreen = null)
        {
            if (!_ownsReparenting || _disposed)
            {
                return;
            }

            if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
            {
                return;
            }

            if (IsOwnProcessWindow(target))
            {
                return;
            }

            if (expectedIdentity is not null &&
                !ReparentEngine.VerifyWindowIdentity(
                    target,
                    expectedIdentity.ProcessId,
                    expectedIdentity.ProcessStartTimeUtc,
                    expectedIdentity.ClassName,
                    expectedIdentity.AutomationRuntimeId,
                    expectedIdentity.CapturedIdentity))
            {
                if (cropGeometry is not null)
                {
                    ShowCropFailure();
                }
                return;
            }

            if (_trackingList.IsTracked(target))
            {
                // Single-owner limitation (§4): this exact window is already reparented into a
                // host frame — don't start a second, overlapping reparent for the same target.
                return;
            }

            ShowCropOverlapWarningIfNeeded(target, expectedIdentity);

            if (ReparentEngine.IsElevationMismatch(target))
            {
                // Elevation mismatch (§14 Phase 1 item 7): SetParent would be silently blocked
                // by UIPI since the target runs elevated and WindowWorks does not. Check this
                // before creating/showing the host window at all, so the user never sees a
                // blank reparent frame appear behind the message box.
                System.Windows.MessageBox.Show(
                    "This window is running as administrator, but WindowWorks is not. Windows blocks reparenting across elevation levels for security reasons. Try running WindowWorks as administrator, or reparent a non-elevated window instead.",
                    "Window Reparenting Blocked",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            ReparentEngine.ReparentedWindowState state = savedState!;
            if (state is null)
            {
                try
                {
                    state = _engine.SaveOriginalState(target, isChildHwndPick);
                }
                catch
                {
                    // Target went away or state couldn't be read — nothing to reparent.
                    return;
                }
            }
            if (expectedIdentity is not null &&
                (state.TargetProcessId != expectedIdentity.ProcessId ||
                 state.TargetProcessStartTimeUtc != expectedIdentity.ProcessStartTimeUtc ||
                 !string.Equals(state.TargetClassName, expectedIdentity.ClassName, StringComparison.Ordinal) ||
                 !string.Equals(
                     state.TargetAutomationRuntimeId,
                     expectedIdentity.AutomationRuntimeId,
                     StringComparison.Ordinal)))
            {
                ShowCropFailure();
                return;
            }

            var host = new ReparentHostWindow();
            host.Title = "WindowWorks — Reparented Window";

            // Conditional resizability (§14 Phase 1 item 4 + Phase 2 crop-mode item,
            // §6.5/§6.7/§9): crop-mode picks are always fixed-size regardless of settings;
            // otherwise, a whole top-level window pick stays resizable (default) while an
            // ancestor-chain child-HWND pick defaults to fixed-size unless the user has opted
            // into "Allow resizing reparented child elements". Decided once, here, at reparent
            // time — not re-evaluated later if the setting changes while this host frame is
            // still open.
            bool resizable = cropGeometry is null &&
                (!isChildHwndPick || _settings.AllowResizingReparentedChildElements);
            host.ConfigureResizability(resizable);

            bool reparented = false;
            ReparentedWindowEntry? entry = null;

            host.SocketReady += (_, _) =>
            {
                if (!_ownsReparenting || _disposed)
                {
                    // The host has no attached target yet, so this close is a safe no-mutation
                    // teardown after the controller relinquished feature ownership.
                    host.Close();
                    return;
                }

                if (host.SocketHwnd == IntPtr.Zero)
                {
                    // Socket window creation failed (e.g. RegisterClass/CreateWindowEx error) —
                    // nothing to reparent into. Surface this to the user instead of leaving a
                    // silently blank host frame open, and close the (empty) host.
                    System.Windows.MessageBox.Show(
                        "WindowWorks couldn't create the reparent host frame's internal window. The reparent was not performed.",
                        "Window Reparenting Failed",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    host.Close();
                    return;
                }

                // Crash-recovery write-before-mutate ordering (§14 Phase 1 item 10): persist the
                // saved state *before* calling SetParent, closing the crash window between
                // SetParent succeeding and a state-file record existing for it.
                bool recoveryPersisted;
                try
                {
                    recoveryPersisted = _crashRecoveryStore.AddOrUpdate(target, state);
                }
                catch
                {
                    recoveryPersisted = false;
                }
                if (!recoveryPersisted)
                {
                    System.Windows.MessageBox.Show(
                        "WindowWorks couldn't safely save recovery state, so the window was not reparented.",
                        "Window Reparenting Failed",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    host.Close();
                    return;
                }

                var reparentOutcome = _engine.Reparent(
                    target,
                    host.SocketHwnd,
                    state,
                    cropGeometry?.TargetOffsetX ?? 0,
                    cropGeometry?.TargetOffsetY ?? 0);
                reparented = reparentOutcome is ReparentEngine.ReparentOutcome.AttachedToHost
                    or ReparentEngine.ReparentOutcome.RollbackFailedStillAttached;
                if (reparented)
                {
                    int? hostContentWidth = null;
                    int? hostContentHeight = null;
                    if (cropGeometry is null)
                    {
                        var liveRect = state.LiveRectAtReparent;
                        int liveWidth = liveRect.Right - liveRect.Left;
                        int liveHeight = liveRect.Bottom - liveRect.Top;
                        if (liveWidth > 0 && liveHeight > 0)
                        {
                            hostContentWidth = liveWidth;
                            hostContentHeight = liveHeight;
                        }
                    }

                    if (cropGeometry is null)
                    {
                        host.AttachTarget(
                            target,
                            hostContentWidth,
                            hostContentHeight,
                            targetProcessId: state.TargetProcessId,
                            targetProcessStartTimeUtc: state.TargetProcessStartTimeUtc,
                            targetClassName: state.TargetClassName,
                            targetAutomationRuntimeId: state.TargetAutomationRuntimeId,
                            capturedTargetIdentity: ReparentEngine.CreateIdentitySnapshot(state.CapturedIdentity));
                    }
                    else
                    {
                        host.AttachTarget(
                            target,
                            cropGeometry.CropWidth,
                            cropGeometry.CropHeight,
                            cropGeometry.TargetOffsetX,
                            cropGeometry.TargetOffsetY,
                            resizeTargetToSocket: false,
                            targetProcessId: state.TargetProcessId,
                            targetProcessStartTimeUtc: state.TargetProcessStartTimeUtc,
                            targetClassName: state.TargetClassName,
                            targetAutomationRuntimeId: state.TargetAutomationRuntimeId,
                            capturedTargetIdentity: ReparentEngine.CreateIdentitySnapshot(state.CapturedIdentity));
                    }
                    entry = new ReparentedWindowEntry(target, state, host, cropRectScreen);
                    _trackingList.Add(entry);
                }
                else
                {
                    // Reparent failed (target may not tolerate WS_CHILD/SetParent, §8 step 6) —
                    // nothing was actually mutated, so remove the crash-recovery entry written
                    // just above rather than leaving a stale record for a reparent that never
                    // happened.
                    try { _crashRecoveryStore.Remove(target); } catch { }

                    // surface this instead of leaving an empty, non-functional frame open, and
                    // close the host rather than showing nothing with no explanation.
                    if (cropGeometry is not null)
                    {
                        ShowCropFailure();
                    }
                    else
                    {
                        System.Windows.MessageBox.Show(
                            "This window could not be reparented. It may not support this operation (some apps are incompatible with window reparenting).",
                            "Window Reparenting Failed",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                    }
                    host.Close();
                }
            };

            host.RestoreRequested += (_, args) =>
            {
                if (!reparented)
                {
                    // ReparentOutcome.NotMutated and RollbackSucceeded both prove this host never
                    // owns the target, so its socket is safe to destroy without touching the live
                    // target. This is deliberately distinct from TargetGone.
                    args.Outcome = RestoreOutcome.NotAttached;
                    return;
                }

                if (entry is null)
                {
                    args.Outcome = RestoreOutcome.FailedStillAttached;
                    return;
                }

                var outcome = RestoreEntry(entry);
                args.Outcome = ToHostRestoreOutcome(outcome);
                if (outcome == ReparentEngine.RestoreOutcome.FailedStillAttached)
                {
                    System.Windows.MessageBox.Show(
                        "WindowWorks could not safely restore this window. It remains embedded so it can be restored or recovered later.",
                        "Window Reparenting Restore Failed",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
            };

            host.Closed += (_, _) =>
            {
                // RestoreEntry removes a tracking entry only after it proves the target detached
                // or gone. Do not remove it here: a failed restore cancels host closing and must
                // retain the entry for retry and recovery.
            };

            host.Show();
        }

        private static void ShowCropFailure()
        {
            System.Windows.MessageBox.Show(
                "WindowWorks couldn't determine the selected crop region. The window was not reparented.",
                "Crop and Reparent Failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }

        private void ShowCropOverlapWarningIfNeeded(IntPtr target, AncestorChainEntry? expectedIdentity)
        {
            Debug.WriteLine($"[OverlapDiag] ShowCropOverlapWarningIfNeeded called target=0x{target.ToInt64():X} expectedIdentity={(expectedIdentity is null ? "null" : $"0x{expectedIdentity.Hwnd.ToInt64():X}")} trackedEntries={_trackingList.Entries.Count}");

            if (expectedIdentity is null)
            {
                Debug.WriteLine("[OverlapDiag] bail: expectedIdentity is null (only happens for non-crop whole-window pick without an entry AncestorChainEntry, e.g. Reset Reparenting path)");
                return;
            }

            if (!TryGetWindowRect(target, out var targetRect))
            {
                Debug.WriteLine("[OverlapDiag] bail: TryGetWindowRect failed for target");
                return;
            }
            Debug.WriteLine($"[OverlapDiag] target rect: {targetRect.Left},{targetRect.Top} - {targetRect.Right},{targetRect.Bottom}");

            foreach (var entry in _trackingList.Entries)
            {
                Debug.WriteLine($"[OverlapDiag]   entry TargetHwnd=0x{entry.TargetHwnd.ToInt64():X} State={entry.State} CropRectScreen={(entry.CropRectScreen is null ? "null" : entry.CropRectScreen.Value.Left + "," + entry.CropRectScreen.Value.Top + "-" + entry.CropRectScreen.Value.Right + "," + entry.CropRectScreen.Value.Bottom)}");

                if (entry.State != ReparentEntryState.Active || entry.CropRectScreen is null)
                {
                    Debug.WriteLine("[OverlapDiag]   -> skip: not Active or no CropRectScreen (this is a whole-window reparent entry, not a crop)");
                    continue;
                }

                bool sameWindow = IsSameOriginalWindow(entry.SavedState, expectedIdentity);
                Debug.WriteLine($"[OverlapDiag]   -> IsSameOriginalWindow={sameWindow}");
                if (!sameWindow)
                {
                    continue;
                }

                bool intersects = RectanglesIntersect(targetRect, entry.CropRectScreen.Value);
                Debug.WriteLine($"[OverlapDiag]   -> RectanglesIntersect={intersects}");
                if (!intersects)
                {
                    continue;
                }

                Debug.WriteLine("[OverlapDiag]   -> WARNING SHOULD FIRE NOW");
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    System.Windows.MessageBox.Show(
                        CropOverlapWarningMessage,
                        CropOverlapWarningTitle,
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }));
                break;
            }
        }

        private static bool TryGetWindowRect(IntPtr hwnd, out CropRectGeometry.NativeMethods.RECT rect)
        {
            rect = default;
            if (!NativeMethods.GetWindowRect(hwnd, out var nativeRect))
            {
                return false;
            }

            rect = new CropRectGeometry.NativeMethods.RECT
            {
                Left = nativeRect.Left,
                Top = nativeRect.Top,
                Right = nativeRect.Right,
                Bottom = nativeRect.Bottom
            };
            return true;
        }

        private static bool IsSameOriginalWindow(
            ReparentEngine.ReparentedWindowState activeEntryState,
            AncestorChainEntry newPick)
        {
            var activeEntryRoot = GetTopLevelAncestor(activeEntryState.TargetHwnd);
            var newPickRoot = GetTopLevelAncestor(newPick.Hwnd);
            if (activeEntryRoot == IntPtr.Zero || newPickRoot == IntPtr.Zero)
            {
                return false;
            }

            return activeEntryRoot == newPickRoot;
        }

        private static IntPtr GetTopLevelAncestor(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                return IntPtr.Zero;
            }

            var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
            return root == IntPtr.Zero ? hwnd : root;
        }

        private static bool RectanglesIntersect(CropRectGeometry.NativeMethods.RECT a, CropRectGeometry.NativeMethods.RECT b)
        {
            return a.Left < b.Right &&
                a.Right > b.Left &&
                a.Top < b.Bottom &&
                a.Bottom > b.Top;
        }

        /// <summary>
        /// Re-entrancy-safe restore for a single tracking-list entry (§14 Phase 1 item 5): checks
        /// the entry is still present and not already <see cref="ReparentEntryState.Restoring"/>
        /// before acting, so overlapping triggers (a Close/Restore click racing a future WinEvent
        /// destroy callback or a "Reset Reparenting" pass) never run the restore sequence twice
        /// for the same entry. Removes the entry from the tracking list once the restore attempt
        /// completes only when the target is proven detached or gone. A failed/uncertain restore
        /// keeps the entry active so the host and crash-recovery state remain available for retry.
        /// </summary>
        /// <returns>
        /// An explicit result describing whether the target is proven detached/gone or may still
        /// be attached to its host socket.
        /// </returns>
        public ReparentEngine.RestoreOutcome RestoreEntry(ReparentedWindowEntry entry)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }
            if (!_ownsReparenting || _disposed)
            {
                return ReparentEngine.RestoreOutcome.FailedStillAttached;
            }

            if (entry.State == ReparentEntryState.CleanupPending)
            {
                if (_crashRecoveryStore.Remove(entry.TargetHwnd))
                {
                    _trackingList.Remove(entry);
                    return ReparentEngine.RestoreOutcome.Restored;
                }

                return ReparentEngine.RestoreOutcome.FailedStillAttached;
            }

            if (!ReferenceEquals(_trackingList.Find(entry.TargetHwnd), entry) || entry.State == ReparentEntryState.Restoring)
            {
                // A caller cannot use a missing/in-flight entry as proof that the target detached.
                // Fail safe so the host never destroys a socket based on an indeterminate result.
                return ReparentEngine.RestoreOutcome.FailedStillAttached;
            }

            entry.State = ReparentEntryState.Restoring;
            ReparentEngine.RestoreOutcome outcome = ReparentEngine.RestoreOutcome.FailedStillAttached;
            try
            {
                outcome = _engine.RestoreOriginalState(entry.SavedState, entry.Host.SocketHwnd);
            }
            catch { }

            if (outcome is ReparentEngine.RestoreOutcome.Restored
                or ReparentEngine.RestoreOutcome.TargetGone
                or ReparentEngine.RestoreOutcome.NotAttached)
            {
                if (_crashRecoveryStore.Remove(entry.TargetHwnd))
                {
                    _trackingList.Remove(entry);
                }
                else
                {
                    entry.State = ReparentEntryState.CleanupPending;
                }
            }
            else
            {
                entry.State = ReparentEntryState.Active;
            }

            return outcome;
        }

        /// <summary>
        /// "Reset Reparenting" (§14 Phase 1 item 6): all-or-nothing restore of every currently-
        /// tracked window. Snapshots the entry list first since <see cref="RestoreEntry"/> mutates
        /// <see cref="_trackingList"/> as it goes (removing each entry once restored), which would
        /// otherwise invalidate an in-progress enumeration of the live list.
        /// </summary>
        public bool RestoreAll()
        {
            if (!_ownsReparenting || _disposed)
            {
                return _trackingList.Entries.Count == 0;
            }

            foreach (var entry in new List<ReparentedWindowEntry>(_trackingList.Entries))
            {
                var outcome = RestoreEntry(entry);

                try
                {
                    if (outcome is ReparentEngine.RestoreOutcome.Restored
                        or ReparentEngine.RestoreOutcome.TargetGone
                        or ReparentEngine.RestoreOutcome.NotAttached)
                    {
                        entry.Host.NotifyRestoreAlreadyHandled(ToHostRestoreOutcome(outcome));
                        entry.Host.Close();
                    }
                }
                catch { }
            }

            return _trackingList.Entries.Count == 0;
        }

        private static RestoreOutcome ToHostRestoreOutcome(ReparentEngine.RestoreOutcome outcome)
        {
            return outcome switch
            {
                ReparentEngine.RestoreOutcome.TargetGone => RestoreOutcome.TargetGone,
                ReparentEngine.RestoreOutcome.Restored => RestoreOutcome.Restored,
                ReparentEngine.RestoreOutcome.NotAttached => RestoreOutcome.NotAttached,
                _ => RestoreOutcome.FailedStillAttached
            };
        }

        /// <summary>
        /// Crash-recovery pass (docs/REPARENT_FEATURE_PLAN.md §14 Phase 1 item 10): call once at
        /// app startup, before the picker/hotkey are wired up. Reads any entries left over from a
        /// previous run that crashed before its normal restore path executed, verifies each one's
        /// HWND-reuse-safe identity (target, and — for child-HWND picks — the original parent),
        /// and restores whichever entries pass verification using the exact same ordered restore
        /// sequence as the live-session path (<see cref="ReparentEngine.RestoreOriginalState"/>).
        /// Entries are removed only after their target is proven gone or successfully restored.
        /// Identity-unverifiable or failed/uncertain entries are retained without being touched,
        /// so a later launch can safely retry recovery.
        ///
        /// Idempotent by construction (per the plan): <c>SetParent</c>/style calls applied to an
        /// already-original-state window are harmless no-ops, so running this more than once (or
        /// after a crash mid-recovery) is always safe.
        /// </summary>
        public void RunCrashRecoveryPass()
        {
            if (!_ownsReparenting || _disposed)
            {
                return;
            }

            try
            {
                _crashRecoveryStore.ProcessRecoveryEntries(entries =>
                {
                    var unresolvedEntries = new List<ReparentCrashRecoveryStore.RecoveryEntry>();
                    foreach (var recoveryEntry in entries)
                    {
                        try
                        {
                            var state = ReparentCrashRecoveryStore.ToReparentedWindowState(recoveryEntry);

                            if (!NativeMethods.IsWindow(state.TargetHwnd))
                            {
                                // A nonexistent HWND cannot still be attached to a host socket.
                                continue;
                            }

                            // IsWindow alone is not sufficient; a recycled handle could belong to
                            // an unrelated window. Retain anything whose identity cannot be proven.
                            bool targetValid = ReparentEngine.VerifyWindowIdentity(
                                state.TargetHwnd,
                                state.TargetProcessId,
                                state.TargetProcessStartTimeUtc,
                                state.TargetClassName,
                                state.TargetAutomationRuntimeId);
                            bool originalParentValid = !state.IsChildHwndPick ||
                                ReparentEngine.VerifyWindowIdentity(
                                    state.OriginalParentHwnd,
                                    state.OriginalParentProcessId,
                                    state.OriginalParentProcessStartTimeUtc,
                                    state.OriginalParentClassName,
                                    state.OriginalParentAutomationRuntimeId);
                            if (!targetValid || !originalParentValid || _trackingList.IsTracked(state.TargetHwnd))
                            {
                                unresolvedEntries.Add(recoveryEntry);
                                continue;
                            }

                            if (_engine.IsOriginalState(state))
                            {
                                continue;
                            }

                            var outcome = _engine.RestoreOriginalState(state);
                            if (outcome == ReparentEngine.RestoreOutcome.FailedStillAttached)
                            {
                                unresolvedEntries.Add(recoveryEntry);
                            }
                        }
                        catch
                        {
                            // An exception cannot prove detachment; retain for a later safe retry.
                            unresolvedEntries.Add(recoveryEntry);
                        }
                    }

                    return unresolvedEntries;
                });
            }
            catch
            {
                return;
            }
        }

        /// <summary>
        /// Naive placeholder window picking (§14 Phase 1 Part 4): resolves the topmost window at
        /// the current cursor position via <c>WindowFromPoint</c>, then walks up to its root
        /// ancestor via <c>GetAncestor(hwnd, GA_ROOT)</c>. No ancestor-chain discovery, no
        /// filtering of invisible/helper windows beyond what <c>WindowFromPoint</c> itself
        /// already skips — deliberately minimal, per the plan's placeholder scope.
        /// </summary>
        private static bool IsOwnProcessWindow(IntPtr hwnd)
        {
            try
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                return pid == (uint)Environment.ProcessId;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Stops the WinEvent hook (§14 Phase 1 item 8). Does not restore any currently-tracked
        /// entries — callers that need an all-or-nothing restore on shutdown should call
        /// <see cref="RestoreAll"/> explicitly first (a future Phase 1 slice, item 9).
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            // An unresolved target may still be a child of one of this process's sockets. Keep
            // this controller alive so its host can retry restoration instead of making the host
            // permanently unable to close safely.
            if (_ownsReparenting && !RestoreAll())
            {
                return;
            }

            _disposed = true;

            if (_ownsReparenting)
            {
                _activeSession?.Cancel();
                _activeCropSelection?.Close();
                _winEventWatcher.WindowDestroyed -= OnWinEventWindowDestroyed;
                try
                {
                    _ownershipMutex.ReleaseMutex();
                }
                finally
                {
                    if (_ownsProcessOwnershipLease)
                    {
                        lock (s_ownershipLeaseGate)
                        {
                            s_processOwnershipLeaseHeld = false;
                        }
                    }
                }
            }
            _winEventWatcher.Dispose();
            _ownershipMutex.Dispose();
        }

        private static (bool OwnsReparenting, bool OwnsProcessOwnershipLease) TryAcquireOwnership(Mutex mutex)
        {
            lock (s_ownershipLeaseGate)
            {
                if (s_processOwnershipLeaseHeld)
                {
                    return (false, false);
                }

                try
                {
                    if (!mutex.WaitOne(0))
                    {
                        return (false, false);
                    }
                }
                catch (AbandonedMutexException)
                {
                    // The former owner exited without releasing the feature mutex. Ownership now
                    // belongs to this controller, which may safely run the normal recovery pass.
                }

                s_processOwnershipLeaseHeld = true;
                return (true, true);
            }
        }

        private static class NativeMethods
        {
            public const uint GA_ROOT = 2;

            [DllImport("user32.dll")]
            public static extern bool IsWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetWindowRect(IntPtr hWnd, out CropRectGeometry.NativeMethods.RECT lpRect);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        }
    }
}
