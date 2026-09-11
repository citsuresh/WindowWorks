using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowWorks.App
{
    /// <summary>
    /// Crash-recovery persistence for the Window Reparenting feature (docs/REPARENT_FEATURE_PLAN.md
    /// §14 Phase 1 item 10 / §12's detailed design). Maintains a small on-disk JSON state file at
    /// <c>%APPDATA%\WindowWorks\reparented-windows.json</c> (same folder convention as
    /// <see cref="Persistence"/>'s <c>settings.json</c>/<c>presets.json</c>, but a separate,
    /// stricter, synchronous-atomic-write helper — deliberately not reusing
    /// <see cref="Persistence"/>'s plain <c>File.WriteAllText</c>, per the plan's explicit note
    /// that this feature's durability requirement is new, not something to copy as-is) so that any
    /// currently-reparented windows can be detected and restored on the next WindowWorks launch if
    /// the app crashed before its normal restore path ran.
    ///
    /// Write-ordering contract (must be honored by callers, not enforced here): write an entry
    /// *before* calling <c>SetParent</c>/changing styles, and remove it only *after*
    /// <see cref="ReparentEngine.RestoreOriginalState"/> completes — this keeps the file always a
    /// superset of "what's actually reparented right now", never a subset that could miss an
    /// orphaned window (§14 Phase 1 item 10 write-ordering note).
    /// </summary>
    public sealed class ReparentCrashRecoveryStore
    {
        /// <summary>
        /// Plain data-transfer record persisted per tracked entry. Deliberately separate from
        /// <see cref="ReparentEngine.ReparentedWindowState"/> (which holds live Win32 structs like
        /// <c>WINDOWPLACEMENT</c>/<c>RECT</c> that need their own flat, JSON-serializable shape)
        /// rather than serializing that class directly.
        /// </summary>
        public sealed class RecoveryEntry
        {
            public long TargetHwnd { get; set; }
            public int ExStyle { get; set; }
            public int Style { get; set; }
            public bool IsChildHwndPick { get; set; }

            public int PlacementFlags { get; set; }
            public int PlacementShowCmd { get; set; }
            public int MinPositionX { get; set; }
            public int MinPositionY { get; set; }
            public int MaxPositionX { get; set; }
            public int MaxPositionY { get; set; }
            public int NormalPositionLeft { get; set; }
            public int NormalPositionTop { get; set; }
            public int NormalPositionRight { get; set; }
            public int NormalPositionBottom { get; set; }

            public int OriginalRectLeft { get; set; }
            public int OriginalRectTop { get; set; }
            public int OriginalRectRight { get; set; }
            public int OriginalRectBottom { get; set; }

            public long OriginalParentHwnd { get; set; }
            public uint OriginalParentProcessId { get; set; }
            public DateTime OriginalParentProcessStartTimeUtc { get; set; }
            public string? OriginalParentClassName { get; set; }
            public string? OriginalParentAutomationRuntimeId { get; set; }

            public uint TargetProcessId { get; set; }
            public DateTime TargetProcessStartTimeUtc { get; set; }
            public string? TargetClassName { get; set; }
            public string? TargetAutomationRuntimeId { get; set; }
        }

        private readonly string _filePath;
        private readonly Mutex _mutationMutex = new(false, @"Local\WindowWorks.ReparentCrashRecoveryStore");

        public ReparentCrashRecoveryStore()
            : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowWorks", "reparented-windows.json"))
        {
        }

        /// <summary>Test/DI seam — production code should use the parameterless constructor.</summary>
        public ReparentCrashRecoveryStore(string filePath)
        {
            _filePath = filePath;
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch { }
        }

        public static RecoveryEntry ToRecoveryEntry(IntPtr targetHwnd, ReparentEngine.ReparentedWindowState state)
        {
            var placement = state.Placement;
            var rect = state.OriginalRect;
            return new RecoveryEntry
            {
                TargetHwnd = targetHwnd.ToInt64(),
                ExStyle = state.ExStyle,
                Style = state.Style,
                IsChildHwndPick = state.IsChildHwndPick,
                PlacementFlags = placement.flags,
                PlacementShowCmd = placement.showCmd,
                MinPositionX = placement.ptMinPosition.X,
                MinPositionY = placement.ptMinPosition.Y,
                MaxPositionX = placement.ptMaxPosition.X,
                MaxPositionY = placement.ptMaxPosition.Y,
                NormalPositionLeft = placement.rcNormalPosition.Left,
                NormalPositionTop = placement.rcNormalPosition.Top,
                NormalPositionRight = placement.rcNormalPosition.Right,
                NormalPositionBottom = placement.rcNormalPosition.Bottom,
                OriginalRectLeft = rect.Left,
                OriginalRectTop = rect.Top,
                OriginalRectRight = rect.Right,
                OriginalRectBottom = rect.Bottom,
                OriginalParentHwnd = state.OriginalParentHwnd.ToInt64(),
                OriginalParentProcessId = state.OriginalParentProcessId,
                OriginalParentProcessStartTimeUtc = state.OriginalParentProcessStartTimeUtc,
                OriginalParentClassName = state.OriginalParentClassName,
                OriginalParentAutomationRuntimeId = state.OriginalParentAutomationRuntimeId,
                TargetProcessId = state.TargetProcessId,
                TargetProcessStartTimeUtc = state.TargetProcessStartTimeUtc,
                TargetClassName = state.TargetClassName,
                TargetAutomationRuntimeId = state.TargetAutomationRuntimeId
            };
        }

        public static ReparentEngine.ReparentedWindowState ToReparentedWindowState(RecoveryEntry entry)
        {
            return new ReparentEngine.ReparentedWindowState
            {
                TargetHwnd = new IntPtr(entry.TargetHwnd),
                ExStyle = entry.ExStyle,
                Style = entry.Style,
                IsChildHwndPick = entry.IsChildHwndPick,
                Placement = new ReparentEngine.NativeMethods.WINDOWPLACEMENT
                {
                    length = System.Runtime.InteropServices.Marshal.SizeOf<ReparentEngine.NativeMethods.WINDOWPLACEMENT>(),
                    flags = entry.PlacementFlags,
                    showCmd = entry.PlacementShowCmd,
                    ptMinPosition = new ReparentEngine.NativeMethods.POINT { X = entry.MinPositionX, Y = entry.MinPositionY },
                    ptMaxPosition = new ReparentEngine.NativeMethods.POINT { X = entry.MaxPositionX, Y = entry.MaxPositionY },
                    rcNormalPosition = new ReparentEngine.NativeMethods.RECT { Left = entry.NormalPositionLeft, Top = entry.NormalPositionTop, Right = entry.NormalPositionRight, Bottom = entry.NormalPositionBottom }
                },
                OriginalRect = new ReparentEngine.NativeMethods.RECT { Left = entry.OriginalRectLeft, Top = entry.OriginalRectTop, Right = entry.OriginalRectRight, Bottom = entry.OriginalRectBottom },
                OriginalParentHwnd = new IntPtr(entry.OriginalParentHwnd),
                OriginalParentProcessId = entry.OriginalParentProcessId,
                OriginalParentProcessStartTimeUtc = entry.OriginalParentProcessStartTimeUtc,
                OriginalParentClassName = entry.OriginalParentClassName,
                OriginalParentAutomationRuntimeId = entry.OriginalParentAutomationRuntimeId,
                TargetProcessId = entry.TargetProcessId,
                TargetProcessStartTimeUtc = entry.TargetProcessStartTimeUtc,
                TargetClassName = entry.TargetClassName,
                TargetAutomationRuntimeId = entry.TargetAutomationRuntimeId
            };
        }

        /// <summary>
        /// Adds (or replaces, by <c>TargetHwnd</c>) an entry and writes the file synchronously and
        /// atomically (temp file + <see cref="File.Replace(string, string, string)"/>/move) —
        /// never batched or deferred, per the plan's write-ordering requirement. Must be called
        /// *before* <c>SetParent</c>/style changes are applied to the target (§14 Phase 1 item 10).
        /// </summary>
        public bool AddOrUpdate(IntPtr targetHwnd, ReparentEngine.ReparentedWindowState state)
        {
            return UpdateAtomically(entries =>
            {
                entries.RemoveAll(e => e.TargetHwnd == targetHwnd.ToInt64());
                entries.Add(ToRecoveryEntry(targetHwnd, state));
            });
        }

        /// <summary>
        /// Removes an entry (by <c>TargetHwnd</c>) and writes the file synchronously and
        /// atomically. Must be called only *after* <see cref="ReparentEngine.RestoreOriginalState"/>
        /// completes for that entry (§14 Phase 1 item 10 write-ordering requirement) — removing it
        /// earlier would reopen the crash window the ordering rule exists to close.
        /// </summary>
        public bool Remove(IntPtr targetHwnd)
        {
            return UpdateAtomically(entries =>
            {
                entries.RemoveAll(e => e.TargetHwnd == targetHwnd.ToInt64());
            });
        }

        /// <summary>
        /// Reads all currently-persisted entries. Returns an empty list (not an error) if the file
        /// doesn't exist yet or fails to parse — a corrupt/missing state file should never block
        /// startup.
        /// </summary>
        public List<RecoveryEntry> LoadAll() => LoadRaw() ?? new List<RecoveryEntry>();

        /// <summary>
        /// Serializes a complete recovery pass with every live mutation across WindowWorks
        /// processes, then atomically writes the callback's retained entries. Holding the mutex
        /// across the callback prevents a live <see cref="AddOrUpdate"/> from being lost between
        /// the recovery snapshot and its final rewrite.
        /// </summary>
        public void ProcessRecoveryEntries(Func<IReadOnlyList<RecoveryEntry>, IReadOnlyList<RecoveryEntry>> process)
        {
            if (process is null)
            {
                throw new ArgumentNullException(nameof(process));
            }

            ExecuteUnderMutationLock(() =>
            {
                var entries = LoadRaw();
                if (entries is null)
                {
                    System.Diagnostics.Debug.WriteLine("[ReparentCrashRecoveryStore] Recovery file could not be read; retaining it without rewrite.");
                    return;
                }

                var retainedEntries = process(entries);
                if (retainedEntries is null)
                {
                    throw new InvalidOperationException("Recovery processing must return the entries to retain.");
                }

                WriteAtomic(new List<RecoveryEntry>(retainedEntries));
            });
        }

        private bool UpdateAtomically(Action<List<RecoveryEntry>> update)
        {
            bool persisted = false;
            ExecuteUnderMutationLock(() =>
            {
                var entries = LoadRaw();
                if (entries is null)
                {
                    System.Diagnostics.Debug.WriteLine("[ReparentCrashRecoveryStore] Recovery file could not be read; mutation skipped to preserve unresolved state.");
                    return;
                }

                update(entries);
                persisted = WriteAtomic(entries);
            });
            return persisted;
        }

        private void ExecuteUnderMutationLock(Action action)
        {
            bool lockHeld = false;
            try
            {
                try
                {
                    lockHeld = _mutationMutex.WaitOne();
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner exited while mutating. We now own the mutex and the
                    // existing atomic write leaves either the old or complete new file intact.
                    lockHeld = true;
                }

                action();
            }
            finally
            {
                if (lockHeld)
                {
                    _mutationMutex.ReleaseMutex();
                }
            }
        }

        private List<RecoveryEntry>? LoadRaw()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return new List<RecoveryEntry>();
                }

                var json = File.ReadAllText(_filePath);
                var entries = JsonSerializer.Deserialize<List<RecoveryEntry>>(json);
                return entries ?? new List<RecoveryEntry>();
            }
            catch
            {
                // An unreadable file might contain state for a still-attached target. Preserve it
                // rather than rewriting it as empty and losing the only recovery record.
                return null;
            }
        }

        private bool WriteAtomic(List<RecoveryEntry> entries)
        {
            try
            {
                var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
                var tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, json);

                if (File.Exists(_filePath))
                {
                    File.Replace(tempPath, _filePath, null);
                }
                else
                {
                    File.Move(tempPath, _filePath);
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ReparentCrashRecoveryStore] WriteAtomic failed: {ex}");
                return false;
            }
        }
    }
}
