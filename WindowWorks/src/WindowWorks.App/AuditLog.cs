using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowWorks.App
{
    /// <summary>
    /// In-memory audit/undo stack. Records WindowStateSnapshot before modifications.
    /// EmergencyReset restores all recorded snapshots.
    /// </summary>
    public class AuditLog : IDisposable
    {
        private readonly Stack<Models.WindowStateSnapshot> _stack = new();


        public void RecordSnapshot(Models.WindowStateSnapshot snapshot)
        {
            if (snapshot == null) return;
            _stack.Push(snapshot);
        }

        public Models.WindowStateSnapshot? UndoLast(WindowManager wm) => UndoLast(wm, out _);

        public Models.WindowStateSnapshot? UndoLast(WindowManager wm, out string? failure)
        {
            failure = null;
            if (_stack.Count == 0) return null;
            var snap = _stack.Peek();
            if (snap.TryRestore(wm, out failure, out bool stale))
            {
                _stack.Pop();
                return snap;
            }
            if (stale) _stack.Pop();
            else failure += " The snapshot remains available for another Undo attempt.";
            return null;
        }

        public int EmergencyReset(WindowManager wm)
        {
            int skipped = 0;
            var retry = new List<Models.WindowStateSnapshot>();
            var blocked = new HashSet<(IntPtr Hwnd, WindowManager.WindowIdentity? Identity)>();
            // Process each entry once; defer older changes to a window whose later restore failed.
            while (_stack.Count > 0)
            {
                var snap = _stack.Pop();
                if (blocked.Contains((snap.Hwnd, snap.Identity)))
                {
                    retry.Add(snap);
                    skipped++;
                    continue;
                }
                if (snap.TryRestore(wm, out _, out bool stale)) continue;
                skipped++;
                if (!stale)
                {
                    retry.Add(snap);
                    blocked.Add((snap.Hwnd, snap.Identity));
                }
            }
            for (int i = retry.Count - 1; i >= 0; i--) _stack.Push(retry[i]);
            return skipped;
        }

        public void Dispose()
        {
            _stack.Clear();
        }
    }
}
