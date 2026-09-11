using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WindowWorks.App.UI.ViewModels
{
    /// <summary>
    /// Backs the dedicated "Window Reparenting" Settings section (docs/REPARENT_FEATURE_PLAN.md
    /// §9, §14 Phase 1 item 11). Exposes the master enable/disable toggle, the "Pop Out and
    /// Reparent" and "Crop and Reparent" sub-toggles, and the "Allow resizing reparented child
    /// elements" toggle (§6.5's three-way fixed-size finding, item 4).
    /// </summary>
    public class WindowReparentingSettingsViewModel : INotifyPropertyChanged, ISettingsSectionViewModel
    {
        private bool _enableWindowReparenting = true;
        private bool _enablePopOutAndReparent = true;
        private bool _enableCropAndReparent = true;
        private bool _allowResizingReparentedChildElements = false;

        /// <summary>
        /// Window Reparenting: master enable/disable toggle for the whole feature (§9). Default
        /// ON. When off, the reparent hotkey/picker must be fully inert; does not affect already-
        /// reparented content (Close/Restore and "Reset Reparenting" always remain available).
        ///
        /// Turning this off also forces <see cref="EnablePopOutAndReparent"/> and
        /// <see cref="EnableCropAndReparent"/> to false (and back to true when re-enabled) so the
        /// sub-toggles' checked state in the UI never implies "on" while actually greyed
        /// out/inert underneath the disabled master — a disabled-but-still-checked checkbox reads
        /// as misleading, so the toggles stay in lockstep instead of silently remembering stale
        /// values.
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
                EnableCropAndReparent = value;
            }
        }

        /// <summary>
        /// Window Reparenting: "Pop Out and Reparent" sub-toggle — the whole-window/ancestor-
        /// element reparent action (§6, §9). Default ON.
        /// </summary>
        public bool EnablePopOutAndReparent { get => _enablePopOutAndReparent; set { if (value == _enablePopOutAndReparent) return; _enablePopOutAndReparent = value; OnPropertyChanged(); } }

        /// <summary>
        /// Window Reparenting: "Crop and Reparent" sub-toggle — the crop-region reparent action
        /// (§9). Default ON.
        /// </summary>
        public bool EnableCropAndReparent { get => _enableCropAndReparent; set { if (value == _enableCropAndReparent) return; _enableCropAndReparent = value; OnPropertyChanged(); } }

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
                if (d.TryGetValue("EnableCropAndReparent", out var cv))
                {
                    if (cv.ValueKind == JsonValueKind.True) EnableCropAndReparent = true;
                    else if (cv.ValueKind == JsonValueKind.False) EnableCropAndReparent = false;
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
            d["EnableCropAndReparent"] = EnableCropAndReparent;
            d["AllowResizingReparentedChildElements"] = AllowResizingReparentedChildElements;
            return d;
        }
    }
}
