using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WindowWorks.App.UI.ViewModels
{
    /// <summary>
    /// Backs the dedicated "Window Reparenting" Settings section (docs/REPARENT_FEATURE_PLAN.md
    /// §9, §14 Phase 1 item 11). Exposes the master enable/disable toggle, the "Pop Out and
    /// Reparent" sub-toggle, and the "Allow resizing reparented child elements" toggle (§6.5's
    /// three-way fixed-size finding, item 4). The "Crop and Reparent" sub-toggle described in §9
    /// is not added yet — crop mode itself is Phase 2 scope, not yet implemented.
    /// </summary>
    public class WindowReparentingSettingsViewModel : INotifyPropertyChanged, ISettingsSectionViewModel
    {
        private bool _enableWindowReparenting = true;
        private bool _enablePopOutAndReparent = true;
        private bool _allowResizingReparentedChildElements = false;

        /// <summary>
        /// Window Reparenting: master enable/disable toggle for the whole feature (§9). Default
        /// ON. When off, the reparent hotkey/picker must be fully inert; does not affect already-
        /// reparented content (Close/Restore and "Reset Reparenting" always remain available).
        ///
        /// Turning this off also forces <see cref="EnablePopOutAndReparent"/> to false (and back
        /// to true when re-enabled) so the sub-toggle's checked state in the UI never implies
        /// "on" while it's actually greyed out/inert underneath the disabled master — a disabled-
        /// but-still-checked checkbox reads as misleading, so the two stay in lockstep instead of
        /// the sub-toggle silently remembering a stale value.
        /// </summary>
        public bool EnableWindowReparenting
        {
            get => _enableWindowReparenting;
            set
            {
                if (value == _enableWindowReparenting) return;
                _enableWindowReparenting = value;
                OnPropertyChanged();
                EnablePopOutAndReparent = value;
            }
        }

        /// <summary>
        /// Window Reparenting: "Pop Out and Reparent" sub-toggle — the whole-window/ancestor-
        /// element reparent action (§6, §9). Default ON.
        /// </summary>
        public bool EnablePopOutAndReparent { get => _enablePopOutAndReparent; set { if (value == _enablePopOutAndReparent) return; _enablePopOutAndReparent = value; OnPropertyChanged(); } }

        /// <summary>
        /// Window Reparenting: "Allow resizing reparented child elements" (docs/REPARENT_FEATURE_PLAN.md
        /// §9, §6.5). Off by default; only affects NEW picks made after the toggle changes.
        /// </summary>
        public bool AllowResizingReparentedChildElements { get => _allowResizingReparentedChildElements; set { if (value == _allowResizingReparentedChildElements) return; _allowResizingReparentedChildElements = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void LoadFromDictionary(Dictionary<string, JsonElement>? d)
        {
            if (d == null) return;
            try
            {
                if (d.TryGetValue("EnableWindowReparenting", out var ev))
                {
                    if (ev.ValueKind == JsonValueKind.True) EnableWindowReparenting = true;
                    else if (ev.ValueKind == JsonValueKind.False) EnableWindowReparenting = false;
                }
                if (d.TryGetValue("EnablePopOutAndReparent", out var pv))
                {
                    if (pv.ValueKind == JsonValueKind.True) EnablePopOutAndReparent = true;
                    else if (pv.ValueKind == JsonValueKind.False) EnablePopOutAndReparent = false;
                }
                if (d.TryGetValue("AllowResizingReparentedChildElements", out var v) && v.ValueKind == JsonValueKind.True) AllowResizingReparentedChildElements = true;
                else if (d.TryGetValue("AllowResizingReparentedChildElements", out v) && v.ValueKind == JsonValueKind.False) AllowResizingReparentedChildElements = false;
            }
            catch { }
        }

        public Dictionary<string, object?> ToDictionary()
        {
            var d = new Dictionary<string, object?>();
            d["EnableWindowReparenting"] = EnableWindowReparenting;
            d["EnablePopOutAndReparent"] = EnablePopOutAndReparent;
            d["AllowResizingReparentedChildElements"] = AllowResizingReparentedChildElements;
            return d;
        }
    }
}
