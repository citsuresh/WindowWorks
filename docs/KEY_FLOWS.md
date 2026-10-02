# Key Flows

- `TrayController` / `PropertyInspectorController` -> `WindowManager.SetTopmost` -> `WindowManager.UpdateTopmostOutlines` -> `TopmostOutlineWindow.Update`
- `AuditLog.UndoLast` / `AuditLog.EmergencyReset` -> `WindowStateSnapshot.Restore` -> `WindowManager.SetTopmost` -> `WindowManager.UpdateTopmostOutlines`
- `ReparentController` -> `WindowManager.RegisterReparentHost` -> `TrayController.HotkeyManager_ToggleTopmostRequested` -> `WindowManager.SetTopmost` -> `TopmostOutlineWindow.Update`

`Program.Main -> TrayApplicationContext -> TrayController -> SettingsWindow`

`Program.Main -> HotkeyManager -> HotkeyApplyService -> IHotkeyApplyService`

`PropertyInspectorController.RelaunchDevToolsAsync -> CdpBrowserRelauncher.RelaunchWithDevToolsAsync -> CdpBrowserRelaunchHandoff.TryCaptureIdentityAsync -> CdpBrowserRelaunchHandoff.TryFindSelectionAsync -> PropertyInspectorController.BeginInspection`

`WindowPickerSession.CropRequested -> ReparentController.StartDomCropReparent -> ReparentController.OnPicked -> ReparentHostWindow.ConfigureResizability -> ReparentHostWindow.AttachTarget`
