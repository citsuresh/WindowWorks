using System;
using System.Collections.Generic;
using WindowWorks.App.UI;

namespace WindowWorks.App
{
    /// <summary>
    /// State machine for a single tracked reparented window (docs/REPARENT_FEATURE_PLAN.md §14
    /// Phase 1, "In-memory reparented-windows tracking list"). There is no separate "Pending"
    /// state — an entry only exists once <see cref="ReparentEngine.Reparent"/> has actually
    /// succeeded, and a crash before that point leaves the target in a state indistinguishable
    /// from "nothing to do" once identity-verified (crash recovery, a later Phase 1 slice, will
    /// persist entries to disk at the same point they're added here).
    /// </summary>
    public enum ReparentEntryState
    {
        /// <summary>Currently reparented and embedded in its host frame.</summary>
        Active,

        /// <summary>
        /// A restore/cleanup operation has started for this entry. Entries in this state are
        /// removed from the tracking list once the restore attempt completes (success or
        /// failure) — this state exists only to guard against re-entrancy (see
        /// <see cref="ReparentTrackingList"/>), not as a durable/long-lived state.
        /// </summary>
        Restoring,
        /// <summary>
        /// The target reached a terminal state, but its crash-recovery record could not be
        /// durably removed. Retain this entry solely to retry that cleanup without restoring again.
        /// </summary>
        CleanupPending
    }

    /// <summary>
    /// A single tracked reparented window: the target HWND, its saved pre-reparent state, the
    /// host frame it's embedded in, and its current state-machine state.
    /// </summary>
    public sealed class ReparentedWindowEntry
    {
        public ReparentedWindowEntry(
            IntPtr targetHwnd,
            ReparentEngine.ReparentedWindowState savedState,
            ReparentHostWindow host,
            CropRectGeometry.NativeMethods.RECT? cropRectScreen = null)
        {
            TargetHwnd = targetHwnd;
            SavedState = savedState ?? throw new ArgumentNullException(nameof(savedState));
            Host = host ?? throw new ArgumentNullException(nameof(host));
            CropRectScreen = cropRectScreen;
        }

        public IntPtr TargetHwnd { get; }
        public ReparentEngine.ReparentedWindowState SavedState { get; }
        public ReparentHostWindow Host { get; }
        public CropRectGeometry.NativeMethods.RECT? CropRectScreen { get; }
        public bool IsOriginalTemporarilyVisible { get; set; }
        public ReparentEntryState State { get; set; } = ReparentEntryState.Active;
    }

    /// <summary>
    /// In-memory list of currently-reparented windows (docs/REPARENT_FEATURE_PLAN.md §14 Phase 1)
    /// — the shared data structure "Reset Reparenting", the settings toggle logic, and crash
    /// recovery all depend on. A plain in-memory collection, not the Windows Registry.
    ///
    /// Concurrency/session-state protocol: all mutations to this list, and all Win32 calls that
    /// affect entries in it, happen only on the UI thread (the same thread that owns the picker,
    /// host frames, and — once implemented — the WinEvent hook's WINEVENT_OUTOFCONTEXT callback,
    /// which already runs via the normal message loop). This single-threaded-owner rule removes
    /// the need for locks; this class is not itself thread-safe and must not be called from any
    /// other thread.
    /// </summary>
    public sealed class ReparentTrackingList
    {
        private readonly List<ReparentedWindowEntry> _entries = new();

        public IReadOnlyList<ReparentedWindowEntry> Entries => _entries;

        /// <summary>
        /// Single-owner enforcement (§4 "Single-owner limitation"): a window can only be
        /// reparented into one host at a time. Callers must check this before reparenting a
        /// newly-picked target.
        /// </summary>
        public bool IsTracked(IntPtr targetHwnd)
        {
            foreach (var entry in _entries)
            {
                if (entry.TargetHwnd == targetHwnd)
                {
                    return true;
                }
            }

            return false;
        }

        public ReparentedWindowEntry? Find(IntPtr targetHwnd)
        {
            foreach (var entry in _entries)
            {
                if (entry.TargetHwnd == targetHwnd)
                {
                    return entry;
                }
            }

            return null;
        }

        public void Add(ReparentedWindowEntry entry)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            _entries.Add(entry);
        }

        /// <summary>
        /// Counts currently-tracked child-HWND picks whose saved original parent HWND matches
        /// <paramref name="parentHwnd"/>. This supports the phased/multi-child reparenting
        /// model in docs/REPARENT_FEATURE_PLAN.md section 6.8, where multiple distinct children can be
        /// pulled from the same original parent over time and later UI can query how many are
        /// currently out.
        /// </summary>
        public int CountTrackedChildrenOfParent(IntPtr parentHwnd)
        {
            if (parentHwnd == IntPtr.Zero)
            {
                return 0;
            }

            int count = 0;
            foreach (var entry in _entries)
            {
                if (entry.State == ReparentEntryState.Active &&
                    entry.SavedState.IsChildHwndPick &&
                    entry.SavedState.OriginalParentHwnd == parentHwnd)
                {
                    count++;
                }
            }

            return count;
        }

        public void Remove(ReparentedWindowEntry entry)
        {
            _entries.Remove(entry);
        }
    }
}
