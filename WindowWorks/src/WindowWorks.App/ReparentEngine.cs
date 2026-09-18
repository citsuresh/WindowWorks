using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WindowWorks.App
{
    /// <summary>
    /// Core Win32 SetParent/WS_CHILD save-restore mechanics for the Window Reparenting feature.
    /// Implements the exact sequencing verified against the real PowerToys Crop-and-Lock source
    /// (ReparentCropAndLockWindow.cpp), per docs/REPARENT_FEATURE_PLAN.md §8.
    ///
    /// Phase 1 scope: whole-window reparent/save/restore mechanics (Phase 0), plus §8 step 8's
    /// ancestor-chain child-HWND correction — restoring a child-HWND pick to its real original
    /// parent (identity-verified) rather than making it top-level. No picker UI, no crash
    /// recovery yet — those build on top of this class later.
    /// </summary>
    public sealed class ReparentEngine
    {
        private static readonly object s_capturedIdentityGate = new();
        private static readonly List<WeakReference<CapturedWindowIdentity>> s_capturedIdentities = new();

        public sealed class CapturedWindowIdentity
        {
            internal CapturedWindowIdentity(
                IntPtr hwnd,
                uint processId,
                DateTime processStartTimeUtc,
                string className,
                string? automationRuntimeId)
            {
                Hwnd = hwnd;
                ProcessId = processId;
                ProcessStartTimeUtc = processStartTimeUtc;
                ClassName = className;
                AutomationRuntimeId = automationRuntimeId;
            }

            public IntPtr Hwnd { get; }
            public uint ProcessId { get; }
            public DateTime ProcessStartTimeUtc { get; }
            public string ClassName { get; }
            public string? AutomationRuntimeId { get; }
            internal bool IsInvalidated { get; private set; }

            internal void Invalidate() => IsInvalidated = true;
        }

        /// <summary>
        /// The target's ownership state after a reparent attempt. Callers must base host-socket
        /// destruction, tracking, and crash-recovery cleanup on this outcome rather than assuming
        /// a positioning failure means no mutation occurred.
        /// </summary>
        public enum ReparentOutcome
        {
            /// <summary>No mutation occurred because the target was blocked or SetParent failed.</summary>
            NotMutated,

            /// <summary>The target is attached to the host socket and must be tracked/restored.</summary>
            AttachedToHost,

            /// <summary>
            /// Final positioning failed, but the target was detached from the host and restored to
            /// its original parent/style before returning.
            /// </summary>
            RollbackSucceeded,

            /// <summary>
            /// Final positioning failed and the target could not be detached from the host socket.
            /// It must remain tracked and its recovery record must be retained.
            /// </summary>
            RollbackFailedStillAttached,

            /// <summary>
            /// The target disappeared or was no longer attached to the host socket before rollback.
            /// The host has no child to retain, and the target must not be changed further.
            /// </summary>
            NotAttached
        }

        /// <summary>
        /// The target's structural state after a restore attempt. Only <see cref="Restored"/> and
        /// <see cref="TargetGone"/> permit destruction of the host socket or removal of recovery
        /// state.
        /// </summary>
        public enum RestoreOutcome
        {
            /// <summary>The target no longer exists, so it cannot be attached to the socket.</summary>
            TargetGone,

            /// <summary>The target was proven detached from the host before style restoration.</summary>
            Restored,

            /// <summary>
            /// The target could not be proven detached from the host socket. Its host, tracking,
            /// and recovery state must be retained.
            /// </summary>
            FailedStillAttached,

            /// <summary>
            /// The live target exists but is no longer parented to the expected host socket. It
            /// must not be modified as it could be an HWND-reused or externally-detached target.
            /// </summary>
            NotAttached
        }

        /// <summary>
        /// Saved pre-reparent state for a single target window, produced by
        /// <see cref="SaveOriginalState"/> and consumed by <see cref="RestoreOriginalState"/>.
        /// </summary>
        public sealed class ReparentedWindowState
        {
            public IntPtr TargetHwnd { get; init; }
            public int ExStyle { get; init; }
            public int Style { get; init; }
            public NativeMethods.WINDOWPLACEMENT Placement { get; init; }
            public NativeMethods.RECT OriginalRect { get; init; }
            public NativeMethods.RECT LiveRectAtReparent { get; set; }

            /// <summary>
            /// True for an ancestor-chain child-HWND pick (§6.5) — the target's original parent
            /// was some other window, not the desktop. False for a whole-top-level-window pick,
            /// whose original parent is implicitly the desktop.
            /// </summary>
            public bool IsChildHwndPick { get; init; }

            /// <summary>
            /// The target's original parent HWND at pick time (<c>GetParent(target)</c>), only
            /// meaningful when <see cref="IsChildHwndPick"/> is true.
            /// </summary>
            public IntPtr OriginalParentHwnd { get; init; }

            /// <summary>
            /// The original parent's process ID at pick time, used for identity verification
            /// before restoring into it (the HWND value alone can be recycled by the OS).
            /// </summary>
            public uint OriginalParentProcessId { get; init; }

            /// <summary>
            /// The original parent's process start time at pick time (paired with
            /// <see cref="OriginalParentProcessId"/> for identity verification — PID alone can
            /// also be recycled).
            /// </summary>
            public DateTime OriginalParentProcessStartTimeUtc { get; init; }

            /// <summary>
            /// The original parent's window class name at pick time, used as a secondary,
            /// defense-in-depth identity check alongside PID + process start time.
            /// </summary>
            public string? OriginalParentClassName { get; init; }

            /// <summary>
            /// The original parent's UI Automation runtime ID at pick time. This is durable across
            /// WindowWorks restarts and is required before restoring a child into that parent.
            /// </summary>
            public string? OriginalParentAutomationRuntimeId { get; init; }

            /// <summary>
            /// The target window's own process ID at pick time (docs/REPARENT_FEATURE_PLAN.md §14
            /// Phase 1 crash recovery) — used, together with
            /// <see cref="TargetProcessStartTimeUtc"/> and <see cref="TargetClassName"/>, to verify
            /// on next launch that a saved <see cref="TargetHwnd"/> value still refers to the same
            /// window rather than a recycled HWND. Not needed for the live-session Close/Restore
            /// path (the target is known-live there), only for crash recovery after a relaunch.
            /// </summary>
            public uint TargetProcessId { get; init; }

            /// <summary>See <see cref="TargetProcessId"/>.</summary>
            public DateTime TargetProcessStartTimeUtc { get; init; }

            /// <summary>See <see cref="TargetProcessId"/>.</summary>
            public string? TargetClassName { get; init; }

            /// <summary>See <see cref="TargetProcessId"/>.</summary>
            public string? TargetAutomationRuntimeId { get; init; }

            internal CapturedWindowIdentity? CapturedIdentity { get; init; }
            internal CapturedWindowIdentity? OriginalParentCapturedIdentity { get; init; }
        }

        /// <summary>
        /// Captures the target window's current extended style, style, placement, and screen
        /// rect so it can later be restored via <see cref="RestoreOriginalState"/>. Must be
        /// called before <see cref="Reparent"/> (§8 step 2).
        /// </summary>
        /// <param name="targetHwnd">The window being reparented.</param>
        /// <param name="isChildHwndPick">
        /// True for an ancestor-chain child-HWND pick (§6.5) — captures the target's current
        /// parent (<c>GetParent</c>) plus its identity (PID, process start time, class name) so
        /// <see cref="RestoreOriginalState"/> can restore into that real original parent instead
        /// of making the target top-level. False (default) for a whole-top-level-window pick.
        /// </param>
        public ReparentedWindowState SaveOriginalState(IntPtr targetHwnd, bool isChildHwndPick = false)
        {
            if (targetHwnd == IntPtr.Zero)
            {
                throw new ArgumentException("targetHwnd must not be IntPtr.Zero.", nameof(targetHwnd));
            }

            int exStyle = NativeMethods.GetWindowLong(targetHwnd, NativeMethods.GWL_EXSTYLE);
            int style = NativeMethods.GetWindowLong(targetHwnd, NativeMethods.GWL_STYLE);

            var placement = new NativeMethods.WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>()
            };
            if (!NativeMethods.GetWindowPlacement(targetHwnd, ref placement))
            {
                throw new InvalidOperationException("GetWindowPlacement failed for the target window.");
            }

            if (!NativeMethods.GetWindowRect(targetHwnd, out var rect))
            {
                throw new InvalidOperationException("GetWindowRect failed for the target window.");
            }

            IntPtr originalParentHwnd = IntPtr.Zero;
            uint originalParentPid = 0;
            DateTime originalParentStartTimeUtc = default;
            string? originalParentClassName = null;
            string? originalParentAutomationRuntimeId = null;
            CapturedWindowIdentity? originalParentCapturedIdentity = null;

            if (isChildHwndPick)
            {
                // Use GetAncestor(GA_PARENT), not GetParent, to match how AncestorChainWalker
                // itself walks parents when classifying a pick as child-HWND vs top-level (see
                // NativeMethods.GetAncestor's remarks above) — using GetParent here previously
                // captured parent=0 for some real child picks (e.g. Explorer's navigation tree),
                // permanently losing the ability to restore into the real original parent.
                originalParentHwnd = NativeMethods.GetAncestor(targetHwnd, NativeMethods.GA_PARENT);
                if (originalParentHwnd == targetHwnd)
                {
                    // GetAncestor returns the window itself if it has no parent/owner at all —
                    // treat that the same as "no parent" rather than a self-referencing loop.
                    originalParentHwnd = IntPtr.Zero;
                }

                if (originalParentHwnd != IntPtr.Zero)
                {
                    if (TryGetWindowIdentity(
                        originalParentHwnd,
                        out originalParentPid,
                        out originalParentStartTimeUtc,
                        out originalParentClassName,
                        out originalParentAutomationRuntimeId))
                    {
                        originalParentCapturedIdentity = CaptureIdentity(
                            originalParentHwnd,
                            originalParentPid,
                            originalParentStartTimeUtc,
                            originalParentClassName,
                            originalParentAutomationRuntimeId);
                    }
                }
                if (originalParentHwnd == IntPtr.Zero || originalParentCapturedIdentity is null)
                {
                    throw new InvalidOperationException("Could not capture the original parent window's identity.");
                }
            }

            // Target's own identity (docs/REPARENT_FEATURE_PLAN.md §14 crash recovery) — persisted
            // so a crash-recovery pass on next launch can verify a saved TargetHwnd value hasn't
            // been recycled to an unrelated window before touching it (not needed for the live-
            // session Close/Restore path, where the target is already known-live).
            if (!TryGetWindowIdentity(
                targetHwnd,
                out uint targetPid,
                out DateTime targetStartTimeUtc,
                out string? targetClassName,
                out string? targetAutomationRuntimeId))
            {
                throw new InvalidOperationException("Could not capture the target window's identity.");
            }

            return new ReparentedWindowState
            {
                TargetHwnd = targetHwnd,
                ExStyle = exStyle,
                Style = style,
                Placement = placement,
                OriginalRect = rect,
                IsChildHwndPick = isChildHwndPick,
                OriginalParentHwnd = originalParentHwnd,
                OriginalParentProcessId = originalParentPid,
                OriginalParentProcessStartTimeUtc = originalParentStartTimeUtc,
                OriginalParentClassName = originalParentClassName,
                OriginalParentAutomationRuntimeId = originalParentAutomationRuntimeId,
                TargetProcessId = targetPid,
                TargetProcessStartTimeUtc = targetStartTimeUtc,
                TargetClassName = targetClassName,
                TargetAutomationRuntimeId = targetAutomationRuntimeId,
                CapturedIdentity = CaptureIdentity(
                    targetHwnd,
                    targetPid,
                    targetStartTimeUtc,
                    targetClassName,
                    targetAutomationRuntimeId),
                OriginalParentCapturedIdentity = originalParentCapturedIdentity
            };
        }

        internal static CapturedWindowIdentity CaptureIdentity(
            IntPtr hwnd,
            uint processId,
            DateTime processStartTimeUtc,
            string? className,
            string? automationRuntimeId)
        {
            if (hwnd == IntPtr.Zero ||
                processId == 0 ||
                processStartTimeUtc == default ||
                string.IsNullOrWhiteSpace(className) ||
                string.IsNullOrWhiteSpace(automationRuntimeId))
            {
                throw new ArgumentException("A complete window identity is required.", nameof(className));
            }

            var identity = new CapturedWindowIdentity(hwnd, processId, processStartTimeUtc, className, automationRuntimeId);
            lock (s_capturedIdentityGate)
            {
                for (int index = s_capturedIdentities.Count - 1; index >= 0; index--)
                {
                    if (!s_capturedIdentities[index].TryGetTarget(out _))
                    {
                        s_capturedIdentities.RemoveAt(index);
                    }
                }
                s_capturedIdentities.Add(new WeakReference<CapturedWindowIdentity>(identity));
            }
            return identity;
        }

        internal static WindowWorks.App.UI.CapturedIdentitySnapshot? CreateIdentitySnapshot(CapturedWindowIdentity? identity)
        {
            if (identity is null)
            {
                return null;
            }

            return new WindowWorks.App.UI.CapturedIdentitySnapshot(
                identity.Hwnd,
                identity.ProcessId,
                identity.ProcessStartTimeUtc,
                identity.ClassName,
                identity.AutomationRuntimeId,
                () => identity.IsInvalidated);
        }

        internal static void InvalidateCapturedIdentities(IntPtr hwnd)
        {
            lock (s_capturedIdentityGate)
            {
                for (int index = s_capturedIdentities.Count - 1; index >= 0; index--)
                {
                    if (!s_capturedIdentities[index].TryGetTarget(out var identity))
                    {
                        s_capturedIdentities.RemoveAt(index);
                    }
                    else if (identity.Hwnd == hwnd)
                    {
                        identity.Invalidate();
                    }
                }
            }
        }

        /// <summary>
        /// Reads a window's identity fields (PID, process start time, class name, UI Automation
        /// runtime ID) used for the
        /// HWND-reuse identity-verification protocol (§8 step 8 / §14 crash recovery). Returns
        /// false (leaving out-params at their default) if any of the underlying Win32/process
        /// calls fail — callers should treat that as "identity unknown", not "identity matches".
        /// </summary>
        internal static bool TryGetWindowIdentity(
            IntPtr hwnd,
            out uint pid,
            out DateTime processStartTimeUtc,
            out string? className,
            out string? automationRuntimeId,
            bool includeAutomationRuntimeId = true)
        {
            pid = 0;
            processStartTimeUtc = default;
            className = null;
            automationRuntimeId = null;

            NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0)
            {
                return false;
            }

            try
            {
                using var process = Process.GetProcessById((int)pid);
                processStartTimeUtc = process.StartTime.ToUniversalTime();
            }
            catch (Exception ex)
            {
                // Any failure here (process exited, access-denied querying an elevated
                // process's StartTime, etc.) is treated as "identity unknown" — logged so it's
                // distinguishable from a genuine identity mismatch during troubleshooting, but
                // still handled the same way by callers (fail closed / fall back to top-level).
                DebugLog($"TryGetWindowIdentity: failed to query process for hwnd={hwnd}, pid={pid}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            var classNameBuffer = new System.Text.StringBuilder(256);
            int len = NativeMethods.GetClassName(hwnd, classNameBuffer, classNameBuffer.Capacity);
            if (len <= 0)
            {
                return false;
            }

            className = classNameBuffer.ToString();
            if (string.IsNullOrWhiteSpace(className))
            {
                return false;
            }

            return !includeAutomationRuntimeId || TryGetAutomationRuntimeId(hwnd, out automationRuntimeId);
        }

        internal static bool TryGetAutomationRuntimeId(IntPtr hwnd, out string? runtimeId)
        {
            runtimeId = null;
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                return false;
            }

            try
            {
                AutomationElement? element = AutomationElement.FromHandle(hwnd);
                int[]? runtimeIdParts = element?.GetRuntimeId();
                if (runtimeIdParts is null || runtimeIdParts.Length == 0)
                {
                    return false;
                }

                runtimeId = string.Join(
                    ",",
                    runtimeIdParts.Select(static value => value.ToString(CultureInfo.InvariantCulture)));
                return !string.IsNullOrWhiteSpace(runtimeId);
            }
            catch (Exception ex)
            {
                DebugLog($"TryGetAutomationRuntimeId: failed for hwnd={hwnd}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// HWND-reuse identity-verification protocol (§8 step 8 / §14 crash recovery): a bare
        /// <c>IsWindow</c> check is not sufficient because Win32 HWND values are recycled after a
        /// window is destroyed. Verifies the live window at <paramref name="hwnd"/> is still the
        /// same window originally saved by comparing PID + process start time, window class name,
        /// and UI Automation runtime ID. Internal (not private) so the
        /// crash-recovery pass (a separate class, per §14 Phase 1 item 10) can reuse the exact
        /// same verification logic against a saved <c>TargetHwnd</c>/<c>OriginalParentHwnd</c>
        /// loaded back from disk, instead of duplicating it.
        /// </summary>
        internal static bool VerifyWindowIdentity(
            IntPtr hwnd,
            uint savedPid,
            DateTime savedStartTimeUtc,
            string? savedClassName,
            string? savedAutomationRuntimeId,
            CapturedWindowIdentity? capturedIdentity = null)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(savedAutomationRuntimeId))
            {
                return false;
            }
            if (capturedIdentity is not null &&
                (capturedIdentity.IsInvalidated ||
                 capturedIdentity.Hwnd != hwnd ||
                 capturedIdentity.ProcessId != savedPid ||
                 capturedIdentity.ProcessStartTimeUtc != savedStartTimeUtc ||
                 !string.Equals(capturedIdentity.ClassName, savedClassName, StringComparison.Ordinal) ||
                 !string.Equals(
                     capturedIdentity.AutomationRuntimeId,
                     savedAutomationRuntimeId,
                     StringComparison.Ordinal)))
            {
                return false;
            }

            if (!TryGetWindowIdentity(
                hwnd,
                out uint livePid,
                out DateTime liveStartTimeUtc,
                out string? liveClassName,
                out string? liveAutomationRuntimeId))
            {
                return false;
            }

            if (livePid != savedPid ||
                liveStartTimeUtc != savedStartTimeUtc ||
                string.IsNullOrWhiteSpace(savedClassName) ||
                string.IsNullOrWhiteSpace(liveClassName) ||
                string.IsNullOrWhiteSpace(liveAutomationRuntimeId))
            {
                return false;
            }

            return string.Equals(savedClassName, liveClassName, StringComparison.Ordinal) &&
                string.Equals(savedAutomationRuntimeId, liveAutomationRuntimeId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Reparents the target window into <paramref name="hostChildHwnd"/>, following the
        /// exact order verified against the real, shipped PowerToys
        /// <c>ReparentCropAndLockWindow.cpp</c> / <c>CropAndLock()</c> implementation (§8 steps
        /// 4-5): <c>SetParent</c> first, then OR in <c>WS_CHILD</c> (existing style bits,
        /// including any pre-existing <c>WS_POPUP</c>, are left as-is — PowerToys does not clear
        /// it), then <c>SetWindowPos</c> with <c>SWP_FRAMECHANGED</c> to force Windows to
        /// re-evaluate the non-client area after the style change.
        /// </summary>
        /// <param name="targetHwnd">The window being reparented.</param>
        /// <param name="hostChildHwnd">The host frame's child "socket" window.</param>
        /// <param name="originalState">
        /// State captured before reparenting. Used to roll back if the final positioning step
        /// fails after <c>SetParent</c> has already changed the target.
        /// </param>
        /// <param name="x">
        /// X offset within the host's client area. 0 for whole-window mode (Phase 0/1); the
        /// negative crop-rect origin in crop mode (Phase 2 — not used here).
        /// </param>
        /// <param name="y">Y offset within the host's client area. See <paramref name="x"/>.</param>
        /// <returns>The target's explicit final attachment outcome.</returns>
        public ReparentOutcome Reparent(
            IntPtr targetHwnd,
            IntPtr hostChildHwnd,
            ReparentedWindowState originalState,
            int x = 0,
            int y = 0)
        {
            if (targetHwnd == IntPtr.Zero)
            {
                throw new ArgumentException("targetHwnd must not be IntPtr.Zero.", nameof(targetHwnd));
            }
            if (hostChildHwnd == IntPtr.Zero)
            {
                throw new ArgumentException("hostChildHwnd must not be IntPtr.Zero.", nameof(hostChildHwnd));
            }
            if (originalState is null)
            {
                throw new ArgumentNullException(nameof(originalState));
            }
            if (originalState.TargetHwnd != targetHwnd)
            {
                throw new ArgumentException("originalState must belong to targetHwnd.", nameof(originalState));
            }
            if (!VerifyWindowIdentity(
                targetHwnd,
                originalState.TargetProcessId,
                originalState.TargetProcessStartTimeUtc,
                originalState.TargetClassName,
                originalState.TargetAutomationRuntimeId,
                originalState.CapturedIdentity))
            {
                DebugLog($"Reparent: target identity changed before mutation, target={targetHwnd}.");
                return ReparentOutcome.NotMutated;
            }

            if (IsElevationMismatch(targetHwnd))
            {
                DebugLog($"Reparent: blocked, target={targetHwnd} is elevated and WindowWorks is not.");
                return ReparentOutcome.NotMutated;
            }

            // BUG FIX (maximized-source crop report): a target that was maximized when picked
            // (e.g. a maximized browser window cropped via a DOM pick) needs its actual current
            // size captured and explicitly re-asserted after reparenting, rather than relying on
            // SWP_NOSIZE to preserve it. Windows tracks "is this window zoomed" internally,
            // separate from the WS_MAXIMIZE style bit; once SetParent detaches the target from
            // being a top-level maximized window, that internal zoom-state resolution snaps the
            // window down to its pre-maximize "restored" size — even under SWP_NOSIZE — leaving a
            // too-small video element and a blank unpainted strip filling the leftover socket
            // area. The capture must happen immediately before SetParent: capturing it any later
            // (even a moment after SetParent runs) already observes the snapped-down size, and
            // capturing it earlier (e.g. at SaveOriginalState time) risks staleness across the
            // unbounded-latency gap before Reparent actually runs (host-window construction plus
            // a WPF Dispatcher "Loaded" round-trip).
            bool haveUsableRect = NativeMethods.GetWindowRect(targetHwnd, out var preStripRect) &&
                !NativeMethods.IsIconic(targetHwnd);
            int preStripWidth = haveUsableRect ? preStripRect.Right - preStripRect.Left : 0;
            int preStripHeight = haveUsableRect ? preStripRect.Bottom - preStripRect.Top : 0;

            if (!TrySetParent(targetHwnd, hostChildHwnd, out int setParentError))
            {
                DebugLog($"Reparent: SetParent(target={targetHwnd}, host={hostChildHwnd}) failed, GetLastError={setParentError}");
                return ReparentOutcome.NotMutated;
            }

            // DESIGN CHANGE (per explicit user request, superseding the earlier mouse-hook-based
            // drag/resize-ghosting fix below): rather than reparenting the target's whole window
            // (title bar + resize border still present, just made inert), strip its own
            // WS_CAPTION/WS_THICKFRAME/WS_MINIMIZEBOX/WS_MAXIMIZEBOX/WS_SYSMENU chrome bits
            // entirely, so only its client-area *content* is reparented — filling the host frame's
            // socket with no native title bar/border at all. Only the host frame's own chrome
            // controls move/resize going forward. This mirrors the plan's own §6.5/§8-cited
            // PowerToys crop-mode precedent of stripping WS_THICKFRAME/WS_MAXIMIZEBOX, just applied
            // unconditionally to whole-window reparenting too (not only crop mode), and makes the
            // previous mouse-hook workaround for drag/resize ghosting unnecessary — removed below.
            // WS_MAXIMIZE is stripped alongside the other chrome bits for the same reason: a
            // maximized child window is nonsensical. This does not affect restore: the saved
            // Placement/Style in ReparentedWindowState is untouched and still carries WS_MAXIMIZE,
            // so RestoreOriginalState correctly restores the window to maximized.
            int style = NativeMethods.GetWindowLong(targetHwnd, NativeMethods.GWL_STYLE);
            int strippedStyle = (style | NativeMethods.WS_CHILD) &
                ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_MINIMIZEBOX |
                  NativeMethods.WS_MAXIMIZEBOX | NativeMethods.WS_SYSMENU | NativeMethods.WS_MAXIMIZE);
            NativeMethods.SetWindowLong(targetHwnd, NativeMethods.GWL_STYLE, strippedStyle);

            bool posOk = NativeMethods.SetWindowPos(
                targetHwnd,
                IntPtr.Zero,
                x,
                y,
                preStripWidth,
                preStripHeight,
                (haveUsableRect ? 0 : NativeMethods.SWP_NOSIZE) | NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOZORDER);
            if (!posOk)
            {
                DebugLog($"Reparent: SetWindowPos(target={targetHwnd}, x={x}, y={y}) failed, GetLastError={Marshal.GetLastWin32Error()}");

                // SetParent and the style mutation have already happened. The rollback proves
                // detachment from this socket through SetParent's own result before restoring
                // styles, because WS_CHILD changes make later GetParent inference unreliable.
                if (TryRollbackFailedReparent(originalState, hostChildHwnd, out bool targetStillAttached))
                {
                    return ReparentOutcome.RollbackSucceeded;
                }

                if (!targetStillAttached)
                {
                    return ReparentOutcome.NotAttached;
                }

                DebugLog($"Reparent: rollback after positioning failure left target={targetHwnd} parented to socket={hostChildHwnd}; retaining host ownership for safe recovery.");
                return ReparentOutcome.RollbackFailedStillAttached;
            }

            if (NativeMethods.GetWindowRect(targetHwnd, out var liveRect))
            {
                originalState.LiveRectAtReparent = liveRect;
            }

            return ReparentOutcome.AttachedToHost;
        }

        private static bool TryRollbackFailedReparent(ReparentedWindowState state, IntPtr expectedHostHwnd, out bool targetStillAttached)
        {
            targetStillAttached = NativeMethods.IsWindow(state.TargetHwnd) &&
                NativeMethods.GetParent(state.TargetHwnd) == expectedHostHwnd;
            if (!targetStillAttached)
            {
                return false;
            }

            IntPtr restoreParent = IntPtr.Zero;
            bool restoreAsOriginalChild = false;
            if (state.IsChildHwndPick &&
                VerifyWindowIdentity(
                    state.OriginalParentHwnd,
                    state.OriginalParentProcessId,
                    state.OriginalParentProcessStartTimeUtc,
                    state.OriginalParentClassName,
                    state.OriginalParentAutomationRuntimeId,
                    state.OriginalParentCapturedIdentity))
            {
                restoreParent = state.OriginalParentHwnd;
                restoreAsOriginalChild = true;
            }

            if (!TrySetParent(state.TargetHwnd, restoreParent, out int setParentError))
            {
                DebugLog($"Reparent: rollback SetParent(target={state.TargetHwnd}, parent={restoreParent}) failed, GetLastError={setParentError}");
                targetStillAttached = NativeMethods.IsWindow(state.TargetHwnd) &&
                    NativeMethods.GetParent(state.TargetHwnd) == expectedHostHwnd;
                return !targetStillAttached;
            }

            int width = state.OriginalRect.Right - state.OriginalRect.Left;
            int height = state.OriginalRect.Bottom - state.OriginalRect.Top;
            NativeMethods.SetWindowPos(
                state.TargetHwnd,
                IntPtr.Zero,
                state.OriginalRect.Left,
                state.OriginalRect.Top,
                width,
                height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);

            var placement = state.Placement;
            NativeMethods.SetWindowPlacement(state.TargetHwnd, ref placement);
            NativeMethods.SetWindowLong(state.TargetHwnd, NativeMethods.GWL_EXSTYLE, state.ExStyle);
            NativeMethods.SetWindowLong(
                state.TargetHwnd,
                NativeMethods.GWL_STYLE,
                restoreAsOriginalChild ? state.Style : state.Style & ~NativeMethods.WS_CHILD);
            return true;
        }

        private static bool TrySetParent(IntPtr targetHwnd, IntPtr newParentHwnd, out int lastError)
        {
            Marshal.SetLastPInvokeError(0);
            IntPtr previousParent = NativeMethods.SetParent(targetHwnd, newParentHwnd);
            lastError = Marshal.GetLastWin32Error();
            return previousParent != IntPtr.Zero || lastError == 0;
        }

        /// <summary>
        /// Elevation mismatch check (§14 Phase 1 item 7): <c>SetParent</c> silently fails (or
        /// produces a broken embed) when the target process is elevated (running as
        /// administrator) and WindowWorks itself is not — Windows' UIPI (User Interface
        /// Privilege Isolation) blocks a lower-integrity process from reparenting/subclassing a
        /// higher-integrity one. Callers must check this before calling <see cref="Reparent"/>
        /// and surface a clear inline message instead of attempting (and silently failing) the
        /// SetParent call.
        /// </summary>
        /// <returns>
        /// True if the target process is elevated while WindowWorks' own process is not (i.e.
        /// reparenting would be blocked/unsafe). False if elevation matches (both elevated or
        /// both not) or elevation status couldn't be determined for either side (fails open —
        /// let the actual SetParent call surface any real failure rather than block on an
        /// inconclusive check).
        /// </returns>
        public static bool IsElevationMismatch(IntPtr targetHwnd)
        {
            if (targetHwnd == IntPtr.Zero || !NativeMethods.IsWindow(targetHwnd))
            {
                return false;
            }

            bool? targetElevated = TryIsProcessElevated(targetHwnd);
            bool? selfElevated = TryIsCurrentProcessElevated();
            if (targetElevated is null || selfElevated is null)
            {
                return false;
            }

            return targetElevated.Value && !selfElevated.Value;
        }

        private static bool? TryIsProcessElevated(IntPtr hwnd)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
            {
                return null;
            }

            IntPtr processHandle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                // Commonly ACCESS_DENIED — itself a strong signal the target is elevated and we
                // are not (a non-elevated process can't even open a handle to an elevated one's
                // token). Treat as "elevated" rather than "unknown" so the mismatch is still
                // caught even when the token can't be inspected directly.
                return Marshal.GetLastWin32Error() == NativeMethods.ERROR_ACCESS_DENIED ? true : null;
            }

            try
            {
                return TryIsElevatedFromProcessHandle(processHandle);
            }
            finally
            {
                NativeMethods.CloseHandle(processHandle);
            }
        }

        private static bool? TryIsCurrentProcessElevated()
        {
            return TryIsElevatedFromProcessHandle(NativeMethods.GetCurrentProcess());
        }

        private static bool? TryIsElevatedFromProcessHandle(IntPtr processHandle)
        {
            if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TOKEN_QUERY, out IntPtr tokenHandle))
            {
                return null;
            }

            try
            {
                int size = Marshal.SizeOf<int>();
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenElevation, buffer, size, out _))
                    {
                        return null;
                    }

                    int elevation = Marshal.ReadInt32(buffer);
                    return elevation != 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(tokenHandle);
            }
        }


        // Diagnostic logging for failure paths only (appended to a temp file, mirroring
        // HotkeyManager.DebugLog's pattern) — kept permanently, not just for the original
        // blank-host-frame investigation, since these are exactly the kind of interop calls that
        // can fail silently against an incompatible target app in the field.
        private static void DebugLog(string message)
        {
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "windowworks_reparent_log.txt");
                System.IO.File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " [ReparentEngine] " + message + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>
        /// Restores the target window to its original state, following the exact order verified
        /// against the real PowerToys source (§8 step 8): rect (<c>SetWindowPos</c>) -> unparent
        /// (<c>SetParent</c>) -> placement (<c>SetWindowPlacement</c>) -> styles-last. For a
        /// whole-top-level-window pick (<see cref="ReparentedWindowState.IsChildHwndPick"/>
        /// false), unparenting to <c>IntPtr.Zero</c> is always correct since the original parent
        /// is implicitly the desktop. For an ancestor-chain child-HWND pick, restores into the
        /// real original parent HWND instead (after identity verification), falling back to
        /// top-level if the original parent is no longer valid/recycled.
        /// </summary>
        public RestoreOutcome TemporarilyRestoreToOriginalState(
            ReparentedWindowState state,
            IntPtr expectedHostHwnd = default)
        {
            return RestoreOriginalState(state, expectedHostHwnd);
        }

        public RestoreOutcome RestoreOriginalState(
            ReparentedWindowState state,
            IntPtr expectedHostHwnd = default)
        {
            if (state is null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            IntPtr hwnd = state.TargetHwnd;
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                return RestoreOutcome.TargetGone;
            }
            if (!VerifyWindowIdentity(
                hwnd,
                state.TargetProcessId,
                state.TargetProcessStartTimeUtc,
                state.TargetClassName,
                state.TargetAutomationRuntimeId,
                state.CapturedIdentity))
            {
                DebugLog($"RestoreOriginalState: target identity changed before mutation, target={hwnd}.");
                return RestoreOutcome.FailedStillAttached;
            }
            if (expectedHostHwnd != IntPtr.Zero && NativeMethods.GetParent(hwnd) != expectedHostHwnd)
            {
                return RestoreOutcome.NotAttached;
            }

            int width = state.OriginalRect.Right - state.OriginalRect.Left;
            int height = state.OriginalRect.Bottom - state.OriginalRect.Top;
            bool posOk = NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                state.OriginalRect.Left,
                state.OriginalRect.Top,
                width,
                height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
            if (!posOk)
            {
                DebugLog($"RestoreOriginalState: SetWindowPos(target={hwnd}) failed, GetLastError={Marshal.GetLastWin32Error()}");
            }

            bool restoreAsOriginalChild = false;
            IntPtr restoreParent = IntPtr.Zero;
            if (state.IsChildHwndPick)
            {
                // §8 step 8 correction: a child-HWND pick's original parent was some other
                // window, not the desktop — restore into that real original parent instead of
                // making the target top-level, after identity-verifying it's still the same
                // parent window (not a recycled HWND). Fall back to top-level if verification
                // fails, per the plan's explicit guidance (a recovered top-level window the user
                // can see/deal with manually is strictly better than silently failing or risking
                // a recycled-HWND hazard).
                //
                // Note (Phase 3 design correction, see docs/DESIGN_DECISIONS.md): restoring into a
                // still-identity-valid original parent is always preferred, even if that parent is
                // itself currently reparented/tracked elsewhere in WindowWorks — nesting works
                // correctly (the child rides along with the parent's current fate) and is not a
                // hazard to defend against. Standalone/top-level restore is therefore reserved
                // solely for the case below: the original parent itself fails identity
                // verification (gone, recycled, or otherwise invalid).
                bool parentValid = VerifyWindowIdentity(
                    state.OriginalParentHwnd,
                    state.OriginalParentProcessId,
                    state.OriginalParentProcessStartTimeUtc,
                    state.OriginalParentClassName,
                    state.OriginalParentAutomationRuntimeId,
                    state.OriginalParentCapturedIdentity);

                restoreParent = parentValid ? state.OriginalParentHwnd : IntPtr.Zero;
                restoreAsOriginalChild = parentValid;
                if (!parentValid)
                {
                    DebugLog($"RestoreOriginalState: original parent={state.OriginalParentHwnd} for target={hwnd} failed identity verification (or is gone); falling back to top-level.");
                }
            }

            // Prove detachment through the SetParent result before restoring style bits: clearing
            // WS_CHILD first would make GetParent-based structural checks unreliable.
            if (!TrySetParent(hwnd, restoreParent, out int setParentError))
            {
                DebugLog($"RestoreOriginalState: SetParent(target={hwnd}, parent={restoreParent}) failed, GetLastError={setParentError}");
                if (!NativeMethods.IsWindow(hwnd))
                {
                    return RestoreOutcome.TargetGone;
                }
                if (expectedHostHwnd != IntPtr.Zero && NativeMethods.GetParent(hwnd) != expectedHostHwnd)
                {
                    return RestoreOutcome.NotAttached;
                }
                return RestoreOutcome.FailedStillAttached;
            }

            var placement = state.Placement;
            NativeMethods.SetWindowPlacement(hwnd, ref placement);

            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, state.ExStyle);
            NativeMethods.SetWindowLong(
                hwnd,
                NativeMethods.GWL_STYLE,
                restoreAsOriginalChild ? state.Style : state.Style & ~NativeMethods.WS_CHILD);

            int finalExStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            int finalStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
            bool isEnabled = NativeMethods.IsWindowEnabled(hwnd);
            bool isVisible = NativeMethods.IsWindowVisible(hwnd);
            IntPtr finalParent = NativeMethods.GetParent(hwnd);
            DebugLog($"RestoreOriginalState: target={hwnd} restored. savedExStyle=0x{state.ExStyle:X8}, finalExStyle=0x{finalExStyle:X8}, savedStyle=0x{state.Style:X8}, finalStyle=0x{finalStyle:X8}, isEnabled={isEnabled}, isVisible={isVisible}, finalParent={finalParent}, posOk={posOk}.");

            return RestoreOutcome.Restored;
        }

        public bool IsOriginalState(ReparentedWindowState state)
        {
            if (state is null ||
                !VerifyWindowIdentity(
                    state.TargetHwnd,
                    state.TargetProcessId,
                    state.TargetProcessStartTimeUtc,
                    state.TargetClassName,
                    state.TargetAutomationRuntimeId,
                    state.CapturedIdentity))
            {
                return false;
            }

            bool restoreAsOriginalChild = state.IsChildHwndPick &&
                VerifyWindowIdentity(
                    state.OriginalParentHwnd,
                    state.OriginalParentProcessId,
                    state.OriginalParentProcessStartTimeUtc,
                    state.OriginalParentClassName,
                    state.OriginalParentAutomationRuntimeId,
                    state.OriginalParentCapturedIdentity);
            IntPtr expectedParent = restoreAsOriginalChild ? state.OriginalParentHwnd : IntPtr.Zero;
            int expectedStyle = restoreAsOriginalChild ? state.Style : state.Style & ~NativeMethods.WS_CHILD;

            return NativeMethods.GetParent(state.TargetHwnd) == expectedParent &&
                NativeMethods.GetWindowLong(state.TargetHwnd, NativeMethods.GWL_EXSTYLE) == state.ExStyle &&
                NativeMethods.GetWindowLong(state.TargetHwnd, NativeMethods.GWL_STYLE) == expectedStyle;
        }

        public static class NativeMethods
        {
            public const int GWL_EXSTYLE = -20;
            public const int GWL_STYLE = -16;
            public const int WS_CHILD = 0x40000000;

            // Chrome bits stripped from the target after reparenting (per explicit user request):
            // reparent only the target's client-area content, not its own window frame — only the
            // host frame's chrome should be visible/interactive going forward.
            public const int WS_CAPTION = 0x00C00000;
            public const int WS_THICKFRAME = 0x00040000;
            public const int WS_MINIMIZEBOX = 0x00020000;
            public const int WS_MAXIMIZEBOX = 0x00010000;
            public const int WS_SYSMENU = 0x00080000;
            public const int WS_MAXIMIZE = 0x01000000;

            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_NOMOVE = 0x0002;
            public const uint SWP_NOZORDER = 0x0004;
            public const uint SWP_NOACTIVATE = 0x0010;
            public const uint SWP_FRAMECHANGED = 0x0020;

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int X;
                public int Y;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct WINDOWPLACEMENT
            {
                public int length;
                public int flags;
                public int showCmd;
                public POINT ptMinPosition;
                public POINT ptMaxPosition;
                public RECT rcNormalPosition;
            }

            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
            private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

            [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
            public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

            public static int GetWindowLong(IntPtr hWnd, int nIndex) => (int)GetWindowLongPtr(hWnd, nIndex).ToInt64();

            public static int SetWindowLong(IntPtr hWnd, int nIndex, int newValue) =>
                (int)SetWindowLongPtr(hWnd, nIndex, new IntPtr(newValue)).ToInt64();

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr GetParent(IntPtr hWnd);

            public const uint GA_PARENT = 1;

            // Used (instead of GetParent) to capture the original parent for a child-HWND pick,
            // so SaveOriginalState agrees with AncestorChainWalker's own parent-walking API
            // (which uses GetAncestor(hwnd, GA_PARENT), not GetParent). GetParent can return NULL
            // for some windows (e.g. certain shell/DirectUI child windows such as Explorer's
            // navigation tree) that GetAncestor(GA_PARENT) still resolves correctly — using
            // GetParent here was causing SaveOriginalState to record parent=0 for genuine child
            // picks, which then made RestoreOriginalState always fall back to top-level instead
            // of restoring into the real original parent.
            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll")]
            public static extern bool IsIconic(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool IsWindow(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

            // Elevation-mismatch check support (§14 Phase 1 item 7).
            public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            public const uint TOKEN_QUERY = 0x0008;
            public const int TokenElevation = 20; // TOKEN_INFORMATION_CLASS.TokenElevation
            public const int ERROR_ACCESS_DENIED = 5;

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

            [DllImport("kernel32.dll")]
            public static extern IntPtr GetCurrentProcess();

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr hObject);

            [DllImport("user32.dll")]
            public static extern bool IsWindowEnabled(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool IsWindowVisible(IntPtr hWnd);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool GetTokenInformation(
                IntPtr tokenHandle,
                int tokenInformationClass,
                IntPtr tokenInformation,
                int tokenInformationLength,
                out int returnLength);
        }
    }
}
