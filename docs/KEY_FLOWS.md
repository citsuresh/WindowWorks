# Key Flows

`Program.Main -> TrayApplicationContext -> TrayController -> SettingsWindow`

`Program.Main -> HotkeyManager -> HotkeyApplyService -> IHotkeyApplyService`

`PropertyInspectorController.RelaunchDevToolsAsync -> CdpBrowserRelauncher.RelaunchWithDevToolsAsync -> CdpBrowserRelaunchHandoff.TryCaptureIdentityAsync -> CdpBrowserRelaunchHandoff.TryFindSelectionAsync -> PropertyInspectorController.BeginInspection`
