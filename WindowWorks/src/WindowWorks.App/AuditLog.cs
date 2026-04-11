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

        public Models.WindowStateSnapshot? UndoLast(WindowManager wm)
        {
            if (_stack.Count == 0) return null;
            var snap = _stack.Pop();
            snap.Restore(wm);
            return snap;
        }

        public void EmergencyReset(WindowManager wm)
        {
            // Restore all snapshots quickly. We pop in reverse order so last change gets restored first.
            while (_stack.Count > 0)
            {
                var snap = _stack.Pop();
                snap.Restore(wm);
            }
        }

        public void Dispose()
        {
            _stack.Clear();
        }
    }
}
