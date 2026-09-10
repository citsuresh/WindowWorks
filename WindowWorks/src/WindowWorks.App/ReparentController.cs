using System;
using System.Collections.Generic;
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
        private readonly ReparentEngine _engine = new();
        private readonly ReparentTrackingList _trackingList = new();
        private readonly Models.AppSettings _settings;
        private readonly ReparentWinEventWatcher _winEventWatcher = new();
        private readonly ReparentCrashRecoveryStore _crashRecoveryStore;
        private WindowPickerSession? _activeSession;

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
            _winEventWatcher.WindowDestroyed += OnWinEventWindowDestroyed;
            _winEventWatcher.Start();
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
            var entry = _trackingList.Find(destroyedHwnd);
            if (entry is null || entry.State == ReparentEntryState.Restoring)
            {
                // Not tracked, or already being restored via the normal Close/Restore path —
                // avoid double-handling the same entry (§14 Phase 1 item 8's race-with-normal-
                // close note).
                return;
            }

            entry.State = ReparentEntryState.Restoring;
            _trackingList.Remove(entry);
            try { _crashRecoveryStore.Remove(destroyedHwnd); } catch { }
            try
            {
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
            if (!_settings.EnableWindowReparenting || !_settings.EnablePopOutAndReparent)
            {
                return;
            }

            _activeSession?.Cancel();

            var session = new WindowPickerSession((uint)Environment.ProcessId);
            _activeSession = session;
            session.Confirmed += (_, entry) =>
            {
                if (ReferenceEquals(_activeSession, session))
                {
                    _activeSession = null;
                }
                OnPicked(entry.Hwnd, isChildHwndPick: !entry.IsTopLevel);
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
            _activeSession?.Cancel();
        }

        /// <summary>
        /// Continues the reparent flow once the picker has resolved a target (§14 Phase 1 items
        /// 1+3+4+5): validates the pick, saves its original state (passing through
        /// <paramref name="isChildHwndPick"/> so §8 step 8's restore-to-original-parent branch is
        /// used for ancestor-chain child-HWND picks), and reparents it into a new
        /// <see cref="ReparentHostWindow"/>. No-ops (silently) if the resolved window is already
        /// tracked (single-owner limitation, §4), no longer valid, or is this process's own UI.
        /// </summary>
        private void OnPicked(IntPtr target, bool isChildHwndPick)
        {
            if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
            {
                return;
            }

            if (IsOwnProcessWindow(target))
            {
                return;
            }

            if (_trackingList.IsTracked(target))
            {
                // Single-owner limitation (§4): this exact window is already reparented into a
                // host frame — don't start a second, overlapping reparent for the same target.
                return;
            }

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

            ReparentEngine.ReparentedWindowState state;
            try
            {
                state = _engine.SaveOriginalState(target, isChildHwndPick);
            }
            catch
            {
                // Target went away or state couldn't be read — nothing to reparent.
                return;
            }

            var host = new ReparentHostWindow();
            host.Title = "WindowWorks — Reparented Window";

            // Conditional resizability (§14 Phase 1 item 4, §6.5/§9): a whole top-level window
            // pick stays resizable (default); an ancestor-chain child-HWND pick defaults to
            // fixed-size unless the user has opted into "Allow resizing reparented child
            // elements". Decided once, here, at reparent time — not re-evaluated later if the
            // setting changes while this host frame is still open.
            bool resizable = !isChildHwndPick || _settings.AllowResizingReparentedChildElements;
            host.ConfigureResizability(resizable);

            bool reparented = false;
            ReparentedWindowEntry? entry = null;

            host.SocketReady += (_, _) =>
            {
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
                try { _crashRecoveryStore.AddOrUpdate(target, state); } catch { }

                reparented = _engine.Reparent(target, host.SocketHwnd);
                if (reparented)
                {
                    host.AttachTarget(target);
                    entry = new ReparentedWindowEntry(target, state, host);
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
                    System.Windows.MessageBox.Show(
                        "This window could not be reparented. It may not support this operation (some apps are incompatible with window reparenting).",
                        "Window Reparenting Failed",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    host.Close();
                }
            };

            host.RestoreRequested += (_, args) =>
            {
                // Only restore if Reparent() actually succeeded — SaveOriginalState alone doesn't
                // touch the target, so there's nothing to undo (and calling RestoreOriginalState
                // on a target that was never reparented risks needlessly re-applying its own
                // already-current style/placement). args.UnparentSucceeded is left at its default
                // (true) in this case — nothing was reparented, so the socket is still safe to
                // destroy.
                if (!reparented || entry is null)
                {
                    return;
                }

                args.UnparentSucceeded = RestoreEntry(entry);
            };

            host.Closed += (_, _) =>
            {
                if (entry is not null)
                {
                    // Safety net: the entry should already have been removed by RestoreEntry via
                    // RestoreRequested (which always fires before Closed, per
                    // ReparentHostWindow.OnClosing), but guard against it still being present
                    // (e.g. a future code path that closes the host without going through
                    // RestoreRequested) so the tracking list never retains a stale entry for a
                    // host frame that no longer exists.
                    _trackingList.Remove(entry);
                }
            };

            host.Show();
        }

        /// <summary>
        /// Re-entrancy-safe restore for a single tracking-list entry (§14 Phase 1 item 5): checks
        /// the entry is still present and not already <see cref="ReparentEntryState.Restoring"/>
        /// before acting, so overlapping triggers (a Close/Restore click racing a future WinEvent
        /// destroy callback or a "Reset Reparenting" pass) never run the restore sequence twice
        /// for the same entry. Removes the entry from the tracking list once the restore attempt
        /// completes, regardless of whether <see cref="ReparentEngine.RestoreOriginalState"/>
        /// itself reports success — a failed restore still means "nothing left to track" from a
        /// state-machine perspective (the target may simply be gone).
        /// </summary>
        /// <returns>
        /// True if the target was successfully unparented back to its original parent (or
        /// top-level), or if this call was a no-op because the entry was already handled
        /// elsewhere (already removed / already restoring — no destructive action is implied by
        /// returning true here, since the caller in that case takes no action regardless). False
        /// only when <see cref="ReparentEngine.RestoreOriginalState"/> itself reports the unparent
        /// step failed — callers (see <see cref="ReparentHostWindow.OnClosing"/> via
        /// <see cref="ReparentHostWindow.RestoreRequested"/>) must treat false as "the target may
        /// still be a WS_CHILD of the socket" and avoid destroying the socket window.
        /// </returns>
        public bool RestoreEntry(ReparentedWindowEntry entry)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (!ReferenceEquals(_trackingList.Find(entry.TargetHwnd), entry) || entry.State == ReparentEntryState.Restoring)
            {
                // Already removed, or a restore for this exact entry is already in progress —
                // no-op rather than double-restoring. Not a failure from the caller's
                // perspective — whichever call is actually performing the restore owns the
                // real success/failure result.
                return true;
            }

            entry.State = ReparentEntryState.Restoring;
            bool unparented = false;
            try
            {
                unparented = _engine.RestoreOriginalState(entry.SavedState);
            }
            catch { }
            finally
            {
                _trackingList.Remove(entry);
                // Crash-recovery write-ordering (§14 Phase 1 item 10): remove the state-file
                // entry only *after* the restore attempt completes (success or failure) — never
                // before — so a crash mid-restore still leaves a valid recovery record pointing
                // at the pre-restore saved state for the next launch's recovery pass to finish.
                try { _crashRecoveryStore.Remove(entry.TargetHwnd); } catch { }
            }

            return unparented;
        }

        /// <summary>
        /// "Reset Reparenting" (§14 Phase 1 item 6): all-or-nothing restore of every currently-
        /// tracked window. Snapshots the entry list first since <see cref="RestoreEntry"/> mutates
        /// <see cref="_trackingList"/> as it goes (removing each entry once restored), which would
        /// otherwise invalidate an in-progress enumeration of the live list.
        /// </summary>
        public void RestoreAll()
        {
            foreach (var entry in new List<ReparentedWindowEntry>(_trackingList.Entries))
            {
                bool unparented = RestoreEntry(entry);

                // BUG FIX (destroy-on-failed-restore report): tell the host the restore was
                // already performed (and its real outcome) before calling Close(), so OnClosing's
                // own RestoreRequested round-trip doesn't re-invoke RestoreEntry for this
                // already-removed entry and get back a misleading "no-op success" — see
                // ReparentHostWindow.NotifyRestoreAlreadyHandled for why that would otherwise mask
                // a real unparent failure and let the socket (and a still-embedded target) be
                // destroyed.
                try
                {
                    entry.Host.NotifyRestoreAlreadyHandled(unparented);
                    entry.Host.Close();
                }
                catch { }
            }
        }

        /// <summary>
        /// Crash-recovery pass (docs/REPARENT_FEATURE_PLAN.md §14 Phase 1 item 10): call once at
        /// app startup, before the picker/hotkey are wired up. Reads any entries left over from a
        /// previous run that crashed before its normal restore path executed, verifies each one's
        /// HWND-reuse-safe identity (target, and — for child-HWND picks — the original parent),
        /// and restores whichever entries pass verification using the exact same ordered restore
        /// sequence as the live-session path (<see cref="ReparentEngine.RestoreOriginalState"/>).
        /// Entries that fail verification (the target process also closed, or its HWND/parent's
        /// HWND was recycled) are dropped without being touched. Clears the state file once every
        /// entry has been processed (§14 Phase 1 item 10: "clear/archive the state file after a
        /// successful recovery pass").
        ///
        /// Idempotent by construction (per the plan): <c>SetParent</c>/style calls applied to an
        /// already-original-state window are harmless no-ops, so running this more than once (or
        /// after a crash mid-recovery) is always safe.
        /// </summary>
        public void RunCrashRecoveryPass()
        {
            List<ReparentCrashRecoveryStore.RecoveryEntry> entries;
            try
            {
                entries = _crashRecoveryStore.LoadAll();
            }
            catch
            {
                return;
            }

            if (entries.Count == 0)
            {
                return;
            }

            foreach (var recoveryEntry in entries)
            {
                try
                {
                    var state = ReparentCrashRecoveryStore.ToReparentedWindowState(recoveryEntry);

                    // HWND-reuse verification against the target itself (§14 Phase 1 item 10 —
                    // IsWindow alone is not sufficient; a recycled handle could belong to a
                    // completely unrelated window by the time WindowWorks relaunches).
                    bool targetValid = ReparentEngine.VerifyWindowIdentity(
                        state.TargetHwnd,
                        state.TargetProcessId,
                        state.TargetProcessStartTimeUtc,
                        state.TargetClassName);

                    if (!targetValid)
                    {
                        // Target process also closed (or its HWND was recycled) — nothing safe
                        // to restore; drop the entry without mutating anything.
                        continue;
                    }

                    if (_trackingList.IsTracked(state.TargetHwnd))
                    {
                        // Already tracked in this same session (shouldn't normally happen right
                        // at startup, but guards against double-processing if this is ever called
                        // more than once) — skip rather than restoring a window already active.
                        continue;
                    }

                    _engine.RestoreOriginalState(state);
                }
                catch { }
            }

            try { _crashRecoveryStore.Clear(); } catch { }
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
            _winEventWatcher.WindowDestroyed -= OnWinEventWindowDestroyed;
            _winEventWatcher.Dispose();
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll")]
            public static extern bool IsWindow(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        }
    }
}
