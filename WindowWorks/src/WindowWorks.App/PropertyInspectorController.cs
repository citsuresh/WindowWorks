using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Threading;
using WindowWorks.App.UI;

namespace WindowWorks.App
{
    /// <summary>
    /// Drives the native UI Automation inspection and text-write proof of concept. Every live
    /// operation is identity-checked against the immutable picker-time selection snapshot.
    /// </summary>
    public sealed class PropertyInspectorController : IDisposable
    {
        private static readonly TimeSpan UiaOperationTimeout = TimeSpan.FromSeconds(3);

        // DevTools (CDP) property attempts involve HTTP + WebSocket round-trips, potentially
        // across several browser tabs while searching for the correlated one (see
        // CdpBridgeAttempt), so they need a more generous budget than pure in-process UIA calls.
        private static readonly TimeSpan DevToolsOperationTimeout = TimeSpan.FromSeconds(8);

        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private WindowPickerSession? _activeSession;
        private long _selectionGeneration;
        private long _viewGeneration;
        private int _disposed;
        private int _uiaOperationInProgress;

        // Serializes all DevTools (CDP) discover/connect/correlate/read/write round trips.
        // _uiaOperationInProgress is released before a DevTools append runs (see ReadAndShowAsync/
        // CompleteWrite), so without a separate gate, a read-triggered DevTools append could race
        // with a concurrent DevTools write (or another concurrent append) against the same
        // element, opening overlapping CDP connections. This semaphore ensures only one CDP round
        // trip is ever in flight at a time.
        private readonly SemaphoreSlim _devToolsOperationGate = new(1, 1);

        public void InvokePicker()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Interlocked.Increment(ref _selectionGeneration);
            _activeSession?.Cancel();

            var session = new WindowPickerSession(
                (uint)Environment.ProcessId,
                includePopOutPicks: true,
                includeCropEntry: false,
                WindowPickerMode.PropertyInspector);
            _activeSession = session;
            session.PropertyInspectorSelectionConfirmed += (_, selection) =>
            {
                if (ReferenceEquals(_activeSession, session))
                {
                    _activeSession = null;
                }

                BeginInspection(selection);
            };
            session.PropertyInspectorSelectionUnavailable += (_, _) =>
            {
                if (ReferenceEquals(_activeSession, session))
                {
                    _activeSession = null;
                }

                ShowUnavailableMessage();
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

        private void BeginInspection(PropertyInspectorSelection selection)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            long generation = Interlocked.Increment(ref _selectionGeneration);
            _ = ReadAndShowAsync(selection, generation);
        }

        private async Task ReadAndShowAsync(PropertyInspectorSelection selection, long generation)
        {
            if (Interlocked.CompareExchange(ref _uiaOperationInProgress, 1, 0) != 0)
            {
                PostIfCurrent(generation, ShowReadInProgressMessage);
                return;
            }

            Task<PropertyReadResult> readTask;
            try
            {
                readTask = Task.Factory.StartNew(
                    () =>
                    {
                        try
                        {
                            return TryReadProperties(selection);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _uiaOperationInProgress, 0);
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
            catch
            {
                Interlocked.Exchange(ref _uiaOperationInProgress, 0);
                PostIfCurrent(generation, ShowUnavailableMessage);
                return;
            }

            var completedTask = await Task.WhenAny(readTask, Task.Delay(UiaOperationTimeout)).ConfigureAwait(false);
            if (!ReferenceEquals(completedTask, readTask))
            {
                ObserveFault(readTask);
                PostIfCurrent(generation, ShowUnavailableMessage);
                return;
            }

            var result = await readTask.ConfigureAwait(false);
            if (result.Properties is null)
            {
                PostIfCurrent(generation, ShowUnavailableMessage);
                return;
            }

            var (mergedProperties, devToolsUnavailable) = await TryAppendDevToolsPropertiesAsync(selection, result.Properties).ConfigureAwait(false);
            var finalResult = new PropertyReadResult(mergedProperties, result.IdentitySnapshot, result.SelectedElementRuntimeId);

            PostIfCurrent(generation, () => ShowInspectorWindow(selection, generation, finalResult, devToolsUnavailable));
        }

        /// <summary>
        /// Attempts to fetch and append a "DevTools Properties" section (docs/
        /// PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E, sub-phase 3) for browser DOM selections.
        /// Entirely additive and best-effort: only runs for Chromium-family top-level windows, and
        /// any failure (no CDP endpoint, no correlation, timeout) simply returns the original UIA
        /// properties unchanged - the DevTools section is silently absent, never an error, per the
        /// plan's explicit requirement. The returned <c>DevToolsUnavailable</c> flag is <c>true</c>
        /// only when the selection IS a Chromium-family window but no live DevTools endpoint could
        /// be found (sub-phase 6: this is what triggers the "Relaunch with DevTools" bar) — it is
        /// <c>false</c> both when DevTools is present and when the browser isn't Chromium-family at
        /// all (nothing useful to relaunch in that case).
        /// </summary>
        private async Task<(IReadOnlyList<PropertyInspectorProperty> Properties, bool DevToolsUnavailable)> TryAppendDevToolsPropertiesAsync(
            PropertyInspectorSelection selection,
            IReadOnlyList<PropertyInspectorProperty> uiaProperties)
        {
            if (!BrowserClassifier.IsChromiumFamily(selection.RootWindowEntry.ClassName))
            {
                return (uiaProperties, false);
            }

            Cdp.CdpNodeProperties? devToolsProperties;
            await _devToolsOperationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var attemptTask = Cdp.CdpBridgeAttempt.TryReadAsync(selection.SelectedElement, selection.RootWindowEntry.Hwnd, selection.CdpCache);
                var completed = await Task.WhenAny(attemptTask, Task.Delay(DevToolsOperationTimeout)).ConfigureAwait(false);
                if (!ReferenceEquals(completed, attemptTask))
                {
                    ObserveFault(attemptTask);
                    return (uiaProperties, true);
                }

                devToolsProperties = await attemptTask.ConfigureAwait(false);
            }
            catch
            {
                return (uiaProperties, true);
            }
            finally
            {
                _devToolsOperationGate.Release();
            }

            if (devToolsProperties is null)
            {
                return (uiaProperties, true);
            }

            return (AppendDevToolsPropertyRows(uiaProperties, devToolsProperties), false);
        }

        private static List<PropertyInspectorProperty> AppendDevToolsPropertyRows(
            IReadOnlyList<PropertyInspectorProperty> uiaProperties,
            Cdp.CdpNodeProperties devToolsProperties)
        {
            var properties = new List<PropertyInspectorProperty>(uiaProperties)
            {
                new(
                    "style.display",
                    devToolsProperties.Display,
                    editorKind: PropertyInspectorEditorKind.Text,
                    canEdit: true,
                    source: PropertyInspectorPropertySource.DevTools),
                new(
                    "style.visibility",
                    devToolsProperties.Visibility,
                    editorKind: PropertyInspectorEditorKind.Text,
                    canEdit: true,
                    source: PropertyInspectorPropertySource.DevTools),
                new(
                    "innerText",
                    devToolsProperties.InnerText,
                    source: PropertyInspectorPropertySource.DevTools),
                new(
                    "BoundingBox (page coordinates)",
                    devToolsProperties.PageBoundingBox is { } box
                        ? $"{box.Left:F1}, {box.Top:F1}, {box.Width:F1}, {box.Height:F1}"
                        : null,
                    source: PropertyInspectorPropertySource.DevTools)
            };

            // Every HTML attribute is shown as its own editable row (docs/
            // PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E, sub-phase 5), named "attr.<name>" so
            // TryWriteProperty can route a commit back to the correct raw attribute name. This
            // includes class/id alongside every other attribute rather than special-casing them.
            foreach (var attribute in devToolsProperties.Attributes)
            {
                properties.Add(new PropertyInspectorProperty(
                    $"attr.{attribute.Name}",
                    attribute.Value,
                    editorKind: PropertyInspectorEditorKind.Text,
                    canEdit: true,
                    source: PropertyInspectorPropertySource.DevTools));
            }

            return properties;
        }

        private void ShowInspectorWindow(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            PropertyReadResult result,
            bool devToolsUnavailable)
        {
            long viewGeneration = Interlocked.Increment(ref _viewGeneration);
            var window = new PropertyInspectorWindow(
                result.IdentitySnapshot,
                result.Properties!,
                result.SelectedElementRuntimeId);
            window.SetDevToolsUnavailable(devToolsUnavailable);
            window.TextCommitRequested += (property, value) => BeginWrite(selection, selectionGeneration, viewGeneration, window, property, value);
            window.LegacyTextCommitRequested += (property, value) => BeginWrite(selection, selectionGeneration, viewGeneration, window, property, value);
            window.ToggleCommitRequested += (property, desired) => BeginWrite(selection, selectionGeneration, viewGeneration, window, property, desired);
            window.SelectionItemCommitRequested += (property, desired) => BeginWrite(selection, selectionGeneration, viewGeneration, window, property, desired);
            window.ComboCommitRequested += (property, value) => BeginWrite(selection, selectionGeneration, viewGeneration, window, property, value);
            window.Win32BoolCommitRequested += (property, desired) => BeginWrite(selection, selectionGeneration, viewGeneration, window, property, desired);
            window.InvokeCommitRequested += property => BeginWrite(selection, selectionGeneration, viewGeneration, window, property, null);
            window.TransformCommitRequested += (property, rect) => BeginWrite(selection, selectionGeneration, viewGeneration, window, property, rect);
            window.RefreshRequested += () => BeginRefresh(selection, selectionGeneration, viewGeneration, window);
            window.RelaunchDevToolsRequested += () => BeginRelaunchDevTools(selection, selectionGeneration, viewGeneration, window);
            window.Closed += (_, _) =>
            {
                if (Volatile.Read(ref _viewGeneration) == viewGeneration)
                {
                    Interlocked.Increment(ref _viewGeneration);
                }
            };
            window.Show();
        }

        private void BeginWrite(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            PropertyInspectorWindow window,
            PropertyInspectorProperty property,
            object? requestedValue)
        {
            if (!IsCurrent(selectionGeneration, viewGeneration))
            {
                window.ShowOperationFailure(TargetChangedMessage);
                return;
            }

            _ = WriteAndRefreshAsync(selection, selectionGeneration, viewGeneration, window, property, requestedValue);
        }

        private async Task WriteAndRefreshAsync(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            PropertyInspectorWindow window,
            PropertyInspectorProperty property,
            object? requestedValue)
        {
            if (Interlocked.CompareExchange(ref _uiaOperationInProgress, 1, 0) != 0)
            {
                PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(
                    "Another UI Automation operation is still waiting for a target to respond. Try again after it finishes."));
                return;
            }

            Task<PropertyWriteResult> writeTask;
            try
            {
                writeTask = Task.Factory.StartNew(
                    () =>
                    {
                        try
                        {
                            return TryWriteProperty(selection, selectionGeneration, viewGeneration, property, requestedValue);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _uiaOperationInProgress, 0);
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
            catch
            {
                Interlocked.Exchange(ref _uiaOperationInProgress, 0);
                PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(
                    "The property update could not be started."));
                return;
            }

            var completedTask = await Task.WhenAny(writeTask, Task.Delay(UiaOperationTimeout)).ConfigureAwait(false);
            if (!ReferenceEquals(completedTask, writeTask))
            {
                ObserveFault(writeTask);
                ObserveTimedOutWriteCompletion(writeTask, selectionGeneration, viewGeneration, window);
                PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(
                    "The selected UI element did not respond in time. The property update may still complete in the background."));
                return;
            }

            PropertyWriteResult result;
            try
            {
                result = await writeTask.ConfigureAwait(false);
            }
            catch
            {
                PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(
                    "The property update failed unexpectedly."));
                return;
            }

            if (!result.Succeeded)
            {
                PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(result.Message));
                return;
            }

            if (result.Properties is null)
            {
                PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(
                    $"{result.Message} The live properties could not be refreshed."));
                return;
            }

            PostIfCurrent(selectionGeneration, viewGeneration, () =>
            {
                window.UpdateProperties(result.Properties);
                window.SetDevToolsUnavailable(result.DevToolsUnavailable);
                window.ShowOperationSuccess(result.Message);
            });
        }

        /// <summary>
        /// Handles a manual Refresh button click (docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md §4
        /// Phase E follow-up): re-runs the full UIA+DevTools property read for the current
        /// selection on demand, independent of any write. Runs on the same dedicated
        /// LongRunning background thread / single-in-flight-operation gate as writes, so a
        /// refresh can't race with a concurrent write (or another refresh) against the same
        /// element.
        /// </summary>
        private void BeginRefresh(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            PropertyInspectorWindow window)
        {
            if (!IsCurrent(selectionGeneration, viewGeneration))
            {
                window.ShowOperationFailure(TargetChangedMessage);
                return;
            }

            _ = RefreshAsync(selection, selectionGeneration, viewGeneration, window);
        }

        private async Task RefreshAsync(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            PropertyInspectorWindow window)
        {
            if (Interlocked.CompareExchange(ref _uiaOperationInProgress, 1, 0) != 0)
            {
                PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(
                    "Another UI Automation operation is still waiting for a target to respond. Try again after it finishes."));
                return;
            }

            Task<PropertyWriteResult> refreshTask;
            try
            {
                refreshTask = Task.Factory.StartNew(
                    () =>
                    {
                        try
                        {
                            if (!IsCurrent(selectionGeneration, viewGeneration) || !IsLiveSelectionIdentity(selection))
                            {
                                return PropertyWriteResult.Failure(TargetChangedMessage);
                            }

                            var readResult = TryReadProperties(selection);
                            if (readResult.Properties is null)
                            {
                                return PropertyWriteResult.Failure(
                                    "The selected target is no longer available for refresh.");
                            }

                            // Same full re-fetch behavior as any write (docs/
                            // PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E sync rule) so a manual
                            // refresh also picks up a DevTools section that appears/disappears/
                            // changes for reasons outside this app's own writes (e.g. edited
                            // directly in browser DevTools, or by page script).
                            var (mergedProperties, devToolsUnavailable) = TryAppendDevToolsPropertiesAsync(selection, readResult.Properties)
                                .GetAwaiter()
                                .GetResult();

                            if (!IsCurrent(selectionGeneration, viewGeneration) || !IsLiveSelectionIdentity(selection))
                            {
                                return PropertyWriteResult.Failure(TargetChangedMessage);
                            }

                            return PropertyWriteResult.Success("Properties refreshed.", mergedProperties, devToolsUnavailable);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _uiaOperationInProgress, 0);
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
            catch
            {
                Interlocked.Exchange(ref _uiaOperationInProgress, 0);
                PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(
                    "The refresh could not be started."));
                return;
            }

            var completedTask = await Task.WhenAny(refreshTask, Task.Delay(UiaOperationTimeout)).ConfigureAwait(false);
            if (!ReferenceEquals(completedTask, refreshTask))
            {
                ObserveFault(refreshTask);
                PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(
                    "The selected UI element did not respond in time."));
                return;
            }

            PropertyWriteResult result;
            try
            {
                result = await refreshTask.ConfigureAwait(false);
            }
            catch
            {
                PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(
                    "The refresh failed unexpectedly."));
                return;
            }

            if (!result.Succeeded || result.Properties is null)
            {
                PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(result.Message));
                return;
            }

            PostIfCurrent(selectionGeneration, viewGeneration, () =>
            {
                window.UpdateProperties(result.Properties);
                window.SetDevToolsUnavailable(result.DevToolsUnavailable);
                window.ShowOperationSuccess(result.Message);
            });
        }

        /// <summary>
        /// Handles the "Open a DevTools-enabled copy of this page" button (docs/
        /// PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E, sub-phase 6). The window has already
        /// shown its own confirmation popup and only raises this event after the user confirms, so
        /// no further confirmation happens here. This launches a brand-new, independent browser
        /// instance (separate temporary profile) at the same URL rather than closing/relaunching
        /// the user's existing window. After a successful launch it closes the stale inspector,
        /// then strictly attempts to reacquire the matching DOM/UIA element in the new process;
        /// every unavailable or ambiguous match falls back to the normal picker. Runs on the
        /// single-in-flight-operation gate, including the bounded readiness poll.
        /// </summary>
        private void BeginRelaunchDevTools(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            PropertyInspectorWindow window)
        {
            if (!IsCurrent(selectionGeneration, viewGeneration))
            {
                window.ShowOperationFailure(TargetChangedMessage);
                return;
            }

            _ = RelaunchDevToolsAsync(selection, selectionGeneration, viewGeneration, window);
        }

        private async Task RelaunchDevToolsAsync(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            PropertyInspectorWindow window)
        {
            if (Interlocked.CompareExchange(ref _uiaOperationInProgress, 1, 0) != 0)
            {
                PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(
                    "Another UI Automation operation is still waiting for a target to respond. Try again after it finishes."));
                return;
            }

            // This accesses the current UIA element before launch. The fingerprint is only present
            // when a prior high-confidence CDP correlation captured one; backend node ids are not
            // reused because the fresh process has a new DOM. Run this off the UI thread with a
            // bounded timeout: it's a synchronous cross-process UIA call that could otherwise
            // freeze the WPF UI thread if the original element's provider is unresponsive.
            var relaunchIdentity = await Cdp.CdpBrowserRelaunchHandoff.TryCaptureIdentityAsync(selection).ConfigureAwait(false);
            IntPtr browserHwnd = selection.RootWindowEntry.Hwnd;
            Task<Cdp.CdpBrowserRelauncher.RelaunchResult> relaunchTask;
            try
            {
                relaunchTask = Cdp.CdpBrowserRelauncher.RelaunchWithDevToolsAsync(browserHwnd);
            }
            catch
            {
                Interlocked.Exchange(ref _uiaOperationInProgress, 0);
                PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(
                    "Opening a DevTools-enabled copy could not be started."));
                return;
            }

            bool releasedGateForHandoff = false;
            try
            {
                var result = await relaunchTask.ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(result.Message));
                    return;
                }

                bool closeSucceeded;
                try
                {
                    closeSucceeded = await _dispatcher.InvokeAsync(() =>
                    {
                        if (!IsCurrent(selectionGeneration, viewGeneration))
                        {
                            return false;
                        }

                        window.Close();
                        return true;
                    });
                }
                catch (InvalidOperationException)
                {
                    // The dispatcher is shutting down (app closing) while this relaunch was still
                    // in flight; nothing left to post to, and the finally block still releases the
                    // gate below.
                    return;
                }
                if (!closeSucceeded)
                {
                    return;
                }

                // Closing increments the old view generation. Establish a distinct generation for
                // the handoff so its result is never discarded merely because the stale view closed.
                long handoffGeneration = Interlocked.Increment(ref _selectionGeneration);
                PropertyInspectorSelection? relaunchedSelection = relaunchIdentity is not null && result.NewProcessId is int newProcessId
                    ? await Cdp.CdpBrowserRelaunchHandoff.TryFindSelectionAsync(newProcessId, relaunchIdentity).ConfigureAwait(false)
                    : null;

                try
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (Volatile.Read(ref _disposed) != 0
                            || Volatile.Read(ref _selectionGeneration) != handoffGeneration)
                        {
                            return;
                        }

                        // BeginInspection/InvokePicker own their subsequent work and need the request
                        // gate available before they start.
                        Interlocked.Exchange(ref _uiaOperationInProgress, 0);
                        releasedGateForHandoff = true;

                        if (relaunchedSelection is not null)
                        {
                            BeginInspection(relaunchedSelection);
                        }
                        else
                        {
                            InvokePicker();
                        }
                    });
                }
                catch (InvalidOperationException)
                {
                    // Dispatcher shut down before the handoff result could be posted; the finally
                    // block below still releases the gate exactly once.
                }
            }
            finally
            {
                if (!releasedGateForHandoff)
                {
                    Interlocked.Exchange(ref _uiaOperationInProgress, 0);
                }
            }
        }

        private static PropertyReadResult TryReadProperties(PropertyInspectorSelection selection)
        {
            try
            {
                if (!IsLiveRootIdentity(selection))
                {
                    return new PropertyReadResult(null, null, null);
                }
                var element = selection.SelectedElement;
                if (!string.Equals(FormatRuntimeId(element.GetRuntimeId()), selection.SelectedElementRuntimeId, StringComparison.Ordinal))
                {
                    return new PropertyReadResult(null, null, null);
                }

                var properties = ReadProperties(element, selection.RootWindowEntry.Hwnd);
                if (!string.Equals(FormatRuntimeId(element.GetRuntimeId()), selection.SelectedElementRuntimeId, StringComparison.Ordinal)
                    || !IsLiveRootIdentity(selection))
                {
                    return new PropertyReadResult(null, null, null);
                }

                return new PropertyReadResult(
                    properties,
                    ReparentEngine.CreateIdentitySnapshot(selection.RootWindowIdentity),
                    selection.SelectedElementRuntimeId);
            }
            catch (Exception)
            {
                return new PropertyReadResult(null, null, null);
            }
        }

        private static bool IsLiveRootIdentity(PropertyInspectorSelection selection)
        {
            var identity = selection.RootWindowIdentity;
            return ReparentEngine.VerifyWindowIdentity(
                identity.Hwnd,
                identity.ProcessId,
                identity.ProcessStartTimeUtc,
                identity.ClassName,
                identity.AutomationRuntimeId,
                identity);
        }

        private PropertyWriteResult TryWriteProperty(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            PropertyInspectorProperty property,
            object? requestedValue)
        {
            if (property.Source == PropertyInspectorPropertySource.DevTools)
            {
                if (property.Name.StartsWith("attr.", StringComparison.Ordinal))
                {
                    string attributeName = property.Name["attr.".Length..];
                    return TryWriteDevToolsAttribute(
                        selection, selectionGeneration, viewGeneration, attributeName, requestedValue as string ?? string.Empty);
                }

                return property.Name switch
                {
                    "style.display" => TryWriteDevToolsStyle(
                        selection, selectionGeneration, viewGeneration, "display", requestedValue as string ?? string.Empty),
                    "style.visibility" => TryWriteDevToolsStyle(
                        selection, selectionGeneration, viewGeneration, "visibility", requestedValue as string ?? string.Empty),
                    _ => PropertyWriteResult.Failure("That DevTools property is read-only.")
                };
            }

            return property.EditorKind switch
            {
                PropertyInspectorEditorKind.Text => TryWriteText(selection, selectionGeneration, viewGeneration, requestedValue as string ?? string.Empty),
                PropertyInspectorEditorKind.Range => TryWriteRangeValue(selection, selectionGeneration, viewGeneration, requestedValue as string ?? string.Empty),
                PropertyInspectorEditorKind.Toggle => TryWriteToggleState(selection, selectionGeneration, viewGeneration, requestedValue as bool?),
                PropertyInspectorEditorKind.SelectionItem => TryWriteSelectionItem(selection, selectionGeneration, viewGeneration, requestedValue is bool boolValue && boolValue),
                PropertyInspectorEditorKind.ExpandCollapse => TryWriteExpandCollapse(selection, selectionGeneration, viewGeneration, requestedValue as string ?? string.Empty),
                PropertyInspectorEditorKind.WindowVisualState => TryWriteWindowVisualState(selection, selectionGeneration, viewGeneration, requestedValue as string ?? string.Empty),
                PropertyInspectorEditorKind.Win32Bool => TryWriteWin32Bool(selection, selectionGeneration, viewGeneration, property, requestedValue is bool win32BoolValue && win32BoolValue),
                PropertyInspectorEditorKind.LegacyText => TryWriteLegacyText(selection, selectionGeneration, viewGeneration, requestedValue as string ?? string.Empty),
                PropertyInspectorEditorKind.Invoke => TryInvokeLegacyDefaultAction(selection, selectionGeneration, viewGeneration),
                PropertyInspectorEditorKind.Transform => TryWriteTransform(selection, selectionGeneration, viewGeneration, requestedValue as System.Windows.Rect? ?? default),
                _ => PropertyWriteResult.Failure("That property is read-only.")
            };
        }

        private PropertyWriteResult TryWriteText(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            string value)
        {
            try
            {
                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                var element = selection.SelectedElement;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObject)
                    && patternObject is ValuePattern valuePattern)
                {
                    if (valuePattern.Current.IsReadOnly)
                    {
                        return PropertyWriteResult.Failure(
                            "The selected UI element exposes ValuePattern, but its text value is read-only.");
                    }

                    if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                    {
                        return PropertyWriteResult.Failure(TargetChangedMessage);
                    }

                    valuePattern.SetValue(value);
                    return CompleteWrite(selection, selectionGeneration, viewGeneration,
                        "Text updated through UIA ValuePattern. Live properties were refreshed.");
                }

                return TrySetWindowTextFallback(selection, selectionGeneration, viewGeneration, value);
            }
            catch (Exception ex)
            {
                return PropertyWriteResult.Failure($"The UIA text update failed: {ex.Message}");
            }
        }

        private PropertyWriteResult TryWriteRangeValue(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            string value)
        {
            if (!double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double parsed))
            {
                return PropertyWriteResult.Failure("Enter a valid number using invariant format, for example 42 or 3.14.");
            }

            try
            {
                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                var element = selection.SelectedElement;
                if (!element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var patternObject)
                    || patternObject is not RangeValuePattern rangeValuePattern)
                {
                    return PropertyWriteResult.Failure("The selected UI element no longer exposes RangeValuePattern.");
                }

                if (rangeValuePattern.Current.IsReadOnly)
                {
                    return PropertyWriteResult.Failure("The selected UI element exposes RangeValuePattern, but its value is read-only.");
                }

                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                rangeValuePattern.SetValue(parsed);
                return CompleteWrite(selection, selectionGeneration, viewGeneration,
                    "Range value updated through UIA RangeValuePattern. Live properties were refreshed.");
            }
            catch (Exception ex)
            {
                return PropertyWriteResult.Failure($"The UIA range update failed: {ex.Message}");
            }
        }

        private PropertyWriteResult TryWriteToggleState(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            bool? desiredState)
        {
            try
            {
                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                var element = selection.SelectedElement;
                if (!element.TryGetCurrentPattern(TogglePattern.Pattern, out var patternObject)
                    || patternObject is not TogglePattern togglePattern)
                {
                    return PropertyWriteResult.Failure("The selected UI element no longer exposes TogglePattern.");
                }

                ToggleState currentState = togglePattern.Current.ToggleState;
                bool? currentBool = currentState switch
                {
                    ToggleState.On => true,
                    ToggleState.Off => false,
                    ToggleState.Indeterminate => null,
                    _ => null
                };

                if (currentBool == desiredState)
                {
                    return CompleteWrite(selection, selectionGeneration, viewGeneration,
                        "Toggle state was already at the requested value. Live properties were refreshed.");
                }

                ToggleState[] cycle = currentState == ToggleState.Indeterminate
                    ? new[] { ToggleState.Indeterminate, ToggleState.On, ToggleState.Off }
                    : new[] { ToggleState.Off, ToggleState.On, ToggleState.Indeterminate };
                int currentIndex = Array.IndexOf(cycle, currentState);
                int desiredIndex = Array.FindIndex(
                    cycle,
                    state => (state == ToggleState.On && desiredState == true)
                        || (state == ToggleState.Off && desiredState == false)
                        || (state == ToggleState.Indeterminate && desiredState is null));
                int steps = currentIndex >= 0 && desiredIndex >= 0
                    ? (desiredIndex - currentIndex + cycle.Length) % cycle.Length
                    : 1;
                if (steps == 0)
                {
                    steps = 1;
                }

                for (int i = 0; i < steps; i++)
                {
                    if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                    {
                        return PropertyWriteResult.Failure(TargetChangedMessage);
                    }

                    togglePattern.Toggle();
                }

                return CompleteWrite(selection, selectionGeneration, viewGeneration,
                    "Toggle state updated through UIA TogglePattern. Live properties were refreshed.");
            }
            catch (Exception ex)
            {
                return PropertyWriteResult.Failure($"The UIA toggle update failed: {ex.Message}");
            }
        }

        private PropertyWriteResult TryWriteSelectionItem(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            bool desiredSelected)
        {
            try
            {
                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                var element = selection.SelectedElement;
                if (!element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var patternObject)
                    || patternObject is not SelectionItemPattern selectionItemPattern)
                {
                    return PropertyWriteResult.Failure("The selected UI element no longer exposes SelectionItemPattern.");
                }

                if (!desiredSelected)
                {
                    try
                    {
                        selectionItemPattern.RemoveFromSelection();
                    }
                    catch (Exception ex)
                    {
                        return PropertyWriteResult.Failure($"This selection item cannot be cleared here: {ex.Message}");
                    }
                }
                else
                {
                    if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                    {
                        return PropertyWriteResult.Failure(TargetChangedMessage);
                    }

                    selectionItemPattern.Select();
                }

                return CompleteWrite(selection, selectionGeneration, viewGeneration,
                    "Selection state updated through UIA SelectionItemPattern. Live properties were refreshed.");
            }
            catch (Exception ex)
            {
                return PropertyWriteResult.Failure($"The UIA selection update failed: {ex.Message}");
            }
        }

        private PropertyWriteResult TryWriteExpandCollapse(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            string requestedState)
        {
            try
            {
                if (!Enum.TryParse(requestedState, out ExpandCollapseState desiredState))
                {
                    return PropertyWriteResult.Failure("Unknown ExpandCollapse state.");
                }

                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                var element = selection.SelectedElement;
                if (!element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var patternObject)
                    || patternObject is not ExpandCollapsePattern expandCollapsePattern)
                {
                    return PropertyWriteResult.Failure("The selected UI element no longer exposes ExpandCollapsePattern.");
                }

                if (desiredState == ExpandCollapseState.LeafNode)
                {
                    return PropertyWriteResult.Failure("LeafNode is a terminal state and cannot be changed.");
                }

                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                switch (desiredState)
                {
                    case ExpandCollapseState.Collapsed:
                        expandCollapsePattern.Collapse();
                        break;
                    case ExpandCollapseState.Expanded:
                    case ExpandCollapseState.PartiallyExpanded:
                        expandCollapsePattern.Expand();
                        break;
                }

                return CompleteWrite(selection, selectionGeneration, viewGeneration,
                    "Expand/collapse state updated through UIA ExpandCollapsePattern. Live properties were refreshed.");
            }
            catch (Exception ex)
            {
                return PropertyWriteResult.Failure($"The UIA expand/collapse update failed: {ex.Message}");
            }
        }

        private PropertyWriteResult TryWriteWindowVisualState(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            string requestedState)
        {
            try
            {
                if (!Enum.TryParse(requestedState, out WindowVisualState desiredState))
                {
                    return PropertyWriteResult.Failure("Unknown WindowVisualState value.");
                }

                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                var element = selection.SelectedElement;
                if (!element.TryGetCurrentPattern(WindowPattern.Pattern, out var patternObject)
                    || patternObject is not WindowPattern windowPattern)
                {
                    return PropertyWriteResult.Failure("The selected UI element no longer exposes WindowPattern.");
                }

                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                windowPattern.SetWindowVisualState(desiredState);
                return CompleteWrite(selection, selectionGeneration, viewGeneration,
                    "Window visual state updated through UIA WindowPattern. Live properties were refreshed.");
            }
            catch (Exception ex)
            {
                return PropertyWriteResult.Failure($"The UIA window-state update failed: {ex.Message}");
            }
        }

        private PropertyWriteResult TryWriteWin32Bool(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            PropertyInspectorProperty property,
            bool desiredValue)
        {
            try
            {
                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                IntPtr hwnd = new(selection.SelectedElement.Current.NativeWindowHandle);
                if (hwnd == IntPtr.Zero)
                {
                    return PropertyWriteResult.Failure(
                        "The selected UI element has no native window handle to apply this Win32 fallback to.");
                }

                var hwndElement = AutomationElement.FromHandle(hwnd);
                if (!string.Equals(FormatRuntimeId(hwndElement.GetRuntimeId()), selection.SelectedElementRuntimeId, StringComparison.Ordinal)
                    || !CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                if (property.Win32BoolWriter is null)
                {
                    return PropertyWriteResult.Failure("That property has no Win32 fallback write path.");
                }

                var (success, errorMessage) = property.Win32BoolWriter(hwnd, desiredValue);
                if (!success)
                {
                    return PropertyWriteResult.Failure(errorMessage ?? "The Win32 fallback write failed.");
                }

                return CompleteWrite(selection, selectionGeneration, viewGeneration,
                    $"{property.Name} updated through a Win32 fallback. Live properties were refreshed.");
            }
            catch (Exception ex)
            {
                return PropertyWriteResult.Failure($"The Win32 fallback write failed: {ex.Message}");
            }
        }

        private PropertyWriteResult TryWriteLegacyText(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            string value)
        {
            try
            {
                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                var runtimeId = selection.SelectedElement.GetRuntimeId();
                if (!NativeUiaLegacyIAccessible.TrySetValue(runtimeId, selection.RootWindowEntry.Hwnd, value))
                {
                    return PropertyWriteResult.Failure("The LegacyIAccessiblePattern write failed or the element no longer exposes this pattern.");
                }

                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                return CompleteWrite(selection, selectionGeneration, viewGeneration,
                    "Value updated through UIA LegacyIAccessiblePattern. Live properties were refreshed.");
            }
            catch (Exception ex)
            {
                return PropertyWriteResult.Failure($"The LegacyIAccessiblePattern write failed: {ex.Message}");
            }
        }

        private PropertyWriteResult TryInvokeLegacyDefaultAction(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration)
        {
            try
            {
                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                var runtimeId = selection.SelectedElement.GetRuntimeId();
                if (!NativeUiaLegacyIAccessible.TryDoDefaultAction(runtimeId, selection.RootWindowEntry.Hwnd))
                {
                    return PropertyWriteResult.Failure("The LegacyIAccessiblePattern DoDefaultAction call failed or the element no longer exposes this pattern.");
                }

                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                return CompleteWrite(selection, selectionGeneration, viewGeneration,
                    "DoDefaultAction invoked through UIA LegacyIAccessiblePattern. Live properties were refreshed.");
            }
            catch (Exception ex)
            {
                return PropertyWriteResult.Failure($"The LegacyIAccessiblePattern DoDefaultAction call failed: {ex.Message}");
            }
        }

        private PropertyWriteResult TryWriteTransform(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            System.Windows.Rect desiredRectangle)
        {
            try
            {
                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                var element = selection.SelectedElement;
                if (!element.TryGetCurrentPattern(TransformPattern.Pattern, out var patternObject)
                    || patternObject is not TransformPattern transformPattern)
                {
                    return PropertyWriteResult.Failure("The selected UI element no longer exposes TransformPattern.");
                }

                var transformCurrent = transformPattern.Current;
                var currentRectangle = element.Current.BoundingRectangle;
                bool sizeChanged = !currentRectangle.Size.Equals(desiredRectangle.Size);
                bool locationChanged = currentRectangle.Location != desiredRectangle.Location;

                if (sizeChanged && !transformCurrent.CanResize)
                {
                    return PropertyWriteResult.Failure("TransformPattern reports CanResize=false; the size cannot be changed.");
                }

                if (locationChanged && !transformCurrent.CanMove)
                {
                    return PropertyWriteResult.Failure("TransformPattern reports CanMove=false; the position cannot be changed.");
                }

                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                if (sizeChanged)
                {
                    transformPattern.Resize(desiredRectangle.Width, desiredRectangle.Height);
                }

                if (locationChanged)
                {
                    transformPattern.Move(desiredRectangle.X, desiredRectangle.Y);
                }

                return CompleteWrite(selection, selectionGeneration, viewGeneration,
                    "Bounding rectangle updated through UIA TransformPattern. Live properties were refreshed.");
            }
            catch (Exception ex)
            {
                return PropertyWriteResult.Failure($"The TransformPattern write failed: {ex.Message}");
            }
        }

        private PropertyWriteResult TrySetWindowTextFallback(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            string value)
        {
            try
            {
                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                IntPtr hwnd = new(selection.SelectedElement.Current.NativeWindowHandle);
                if (hwnd == IntPtr.Zero)
                {
                    return PropertyWriteResult.Failure(
                        "The selected UI element does not support ValuePattern and has no native window for SetWindowText.");
                }

                var hwndElement = AutomationElement.FromHandle(hwnd);
                if (!string.Equals(FormatRuntimeId(hwndElement.GetRuntimeId()), selection.SelectedElementRuntimeId, StringComparison.Ordinal)
                    || !CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                if (!NativeMethods.SetWindowText(hwnd, value))
                {
                    int error = Marshal.GetLastWin32Error();
                    return PropertyWriteResult.Failure(
                        $"SetWindowText fallback failed{(error == 0 ? "." : $" (Win32 error {error}).")}");
                }

                return CompleteWrite(selection, selectionGeneration, viewGeneration,
                    "Text updated through the Win32 SetWindowText fallback. Live properties were refreshed.");
            }
            catch (Exception ex)
            {
                return PropertyWriteResult.Failure($"The SetWindowText fallback failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes a DevTools-sourced style property (<c>style.display</c> / <c>style.visibility</c>)
        /// via CDP (docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E, sub-phase 4). Unlike the
        /// UIA writers above, this re-discovers the DevTools endpoint and re-correlates the
        /// element from scratch on every call, but falls back to the last known-good
        /// (WebSocket target, backendNodeId) cached on <paramref name="selection"/> when the UIA
        /// anchor is no longer available (e.g. after a prior write set style.display:none) — see
        /// <see cref="Cdp.CdpBridgeAttempt.TryWriteStyleAsync"/> and <see cref="Cdp.CdpCorrelationCache"/>.
        /// Follows the same identity re-verification and refresh-both-sections sync rule as every
        /// other writer.
        /// </summary>
        private PropertyWriteResult TryWriteDevToolsStyle(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            string cssProperty,
            string value)
        {
            return RunDevToolsWrite(
                selection,
                selectionGeneration,
                viewGeneration,
                () => Cdp.CdpBridgeAttempt.TryWriteStyleAsync(
                    selection.SelectedElement, selection.RootWindowEntry.Hwnd, selection.CdpCache, cssProperty, value),
                successDescription: $"style.{cssProperty}");
        }

        /// <summary>
        /// Writes an arbitrary HTML attribute value via CDP (docs/
        /// PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E, sub-phase 5), reusing the exact same
        /// re-correlation/cache-fallback/identity-re-verification/full-re-fetch machinery as
        /// <see cref="TryWriteDevToolsStyle"/> — only the underlying CDP command differs
        /// (<see cref="Cdp.CdpPropertyWriter.WriteAttributeAsync"/> via
        /// <see cref="Cdp.CdpBridgeAttempt.TryWriteAttributeAsync"/>). Only edits an attribute
        /// that already exists; adding a brand-new attribute name is out of scope for this pass.
        /// </summary>
        private PropertyWriteResult TryWriteDevToolsAttribute(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            string attributeName,
            string value)
        {
            return RunDevToolsWrite(
                selection,
                selectionGeneration,
                viewGeneration,
                () => Cdp.CdpBridgeAttempt.TryWriteAttributeAsync(
                    selection.SelectedElement, selection.RootWindowEntry.Hwnd, selection.CdpCache, attributeName, value),
                successDescription: $"attribute '{attributeName}'");
        }

        /// <summary>
        /// Shared orchestration for every DevTools (CDP) property writer: verifies the selection
        /// is still live, runs <paramref name="startWrite"/> under <see cref="_devToolsOperationGate"/>
        /// with a <see cref="DevToolsOperationTimeout"/> race, then applies the full
        /// UIA+DevTools re-fetch sync rule (including the rebind-and-retry recovery for a
        /// hide-then-show round trip) before reporting success/failure back to the caller.
        /// <paramref name="successDescription"/> is a short human-readable label (e.g.
        /// "style.display" or "attribute 'href'") used only in the resulting status message.
        /// </summary>
        private PropertyWriteResult RunDevToolsWrite(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            Func<Task<(Cdp.CdpNodeProperties? Properties, bool WriteSucceeded)>> startWrite,
            string successDescription)
        {
            try
            {
                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                if (!BrowserClassifier.IsChromiumFamily(selection.RootWindowEntry.ClassName))
                {
                    return PropertyWriteResult.Failure("DevTools writes are only supported for Chromium-family browsers.");
                }

                var writeTask = startWrite();
                Task<(Cdp.CdpNodeProperties? Properties, bool WriteSucceeded)> completedWriteTask;
                _devToolsOperationGate.Wait();
                try
                {
                    var completed = Task.WhenAny(writeTask, Task.Delay(DevToolsOperationTimeout)).GetAwaiter().GetResult();
                    if (!ReferenceEquals(completed, writeTask))
                    {
                        ObserveFault(writeTask);
                        return PropertyWriteResult.Failure("The DevTools connection did not respond in time.");
                    }

                    completedWriteTask = writeTask;
                }
                finally
                {
                    _devToolsOperationGate.Release();
                }

                var (properties, writeSucceeded) = completedWriteTask.GetAwaiter().GetResult();
                if (properties is null)
                {
                    return PropertyWriteResult.Failure(
                        "The DevTools connection is no longer available (browser closed the tab, or the element could not be re-correlated).");
                }

                if (!writeSucceeded)
                {
                    return PropertyWriteResult.Failure("The DevTools property update failed.");
                }

                if (!CanWriteCurrentSelection(selection, selectionGeneration, viewGeneration))
                {
                    return PropertyWriteResult.Failure(TargetChangedMessage);
                }

                var uiaResult = TryReadProperties(selection);
                if (uiaResult.Properties is null)
                {
                    // The original AutomationElement reference may simply be stale rather than
                    // genuinely still-hidden: e.g. a prior style.display:none write removed it
                    // from the accessibility tree, and this write just restored visibility, but
                    // Chromium creates a brand-new accessibility node when an element reappears —
                    // the old reference never becomes valid again even though the element is now
                    // visible. Try to re-acquire a fresh element at the cached screen point before
                    // concluding the element is genuinely still unavailable to UIA.
                    if (selection.CdpCache.TryGetLastKnownScreenRect(out var lastKnownScreenRect)
                        && selection.TryRebindSelectedElementAtRect(lastKnownScreenRect))
                    {
                        uiaResult = TryReadProperties(selection);
                    }
                }

                if (uiaResult.Properties is null)
                {
                    // Setting style.display:none (or style.visibility:hidden, in some browsers)
                    // legitimately removes the element from the accessibility tree, so the UIA
                    // re-read can no longer find it — this is expected browser behavior, not a
                    // failure of the write itself (which already succeeded above). Report success
                    // and show only the DevTools properties, since UIA properties are genuinely
                    // unavailable for a hidden element.
                    var devToolsOnlyProperties = AppendDevToolsPropertyRows(
                        Array.Empty<PropertyInspectorProperty>(), properties);
                    return PropertyWriteResult.Success(
                        $"{successDescription} updated through DevTools (CDP). The element is no longer exposed to UI " +
                        "Automation as a result (expected when hiding an element) — only DevTools properties are shown.",
                        devToolsOnlyProperties);
                }

                var mergedProperties = AppendDevToolsPropertyRows(uiaResult.Properties, properties);

                return PropertyWriteResult.Success(
                    $"{successDescription} updated through DevTools (CDP). Live properties were refreshed.", mergedProperties);
            }
            catch (Exception ex)
            {
                return PropertyWriteResult.Failure($"The DevTools property update failed: {ex.Message}");
            }
        }

        private PropertyWriteResult CompleteWrite(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration,
            string successMessage)
        {
            if (!IsCurrent(selectionGeneration, viewGeneration) || !IsLiveSelectionIdentity(selection))
            {
                return PropertyWriteResult.Failure(TargetChangedMessage);
            }

            var readResult = TryReadProperties(selection);
            if (readResult.Properties is null)
            {
                return PropertyWriteResult.Failure(
                    "The property update completed, but the selected target is no longer available for refresh.");
            }

            // Per the sync rule (docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E): after ANY
            // successful write, re-fetch BOTH the full UIA and DevTools property sets so the
            // "DevTools Properties" section (if previously shown) doesn't silently disappear.
            // This runs on a dedicated LongRunning background thread (see WriteAndRefreshAsync),
            // never the UI thread, so a blocking wait here is safe.
            var (mergedProperties, devToolsUnavailable) = TryAppendDevToolsPropertiesAsync(selection, readResult.Properties)
                .GetAwaiter()
                .GetResult();

            // TryAppendDevToolsPropertiesAsync can take up to DevToolsOperationTimeout (8s) to
            // complete. Re-verify identity afterward (mirroring TryWriteDevToolsStyle) so a
            // selection change or teardown that happened during that window doesn't get a stale
            // DevTools-merged property set reported as a successful refresh.
            if (!IsCurrent(selectionGeneration, viewGeneration) || !IsLiveSelectionIdentity(selection))
            {
                return PropertyWriteResult.Failure(TargetChangedMessage);
            }

            return PropertyWriteResult.Success(successMessage, mergedProperties, devToolsUnavailable);
        }

        private bool CanWriteCurrentSelection(
            PropertyInspectorSelection selection,
            long selectionGeneration,
            long viewGeneration)
        {
            return IsCurrent(selectionGeneration, viewGeneration) && IsLiveSelectionIdentity(selection);
        }

        private static bool IsLiveSelectionIdentity(PropertyInspectorSelection selection)
        {
            return IsLiveRootIdentity(selection)
                && string.Equals(
                    FormatRuntimeId(selection.SelectedElement.GetRuntimeId()),
                    selection.SelectedElementRuntimeId,
                    StringComparison.Ordinal);
        }

        private static string? FormatRuntimeId(int[]? runtimeIdParts)
        {
            return runtimeIdParts is null || runtimeIdParts.Length == 0
                ? null
                : string.Join(",", runtimeIdParts.Select(static value => value.ToString(CultureInfo.InvariantCulture)));
        }

        private static IReadOnlyList<PropertyInspectorProperty> ReadProperties(AutomationElement element, IntPtr containingWindowHandle)
        {
            var current = element.Current;
            bool supportsWritableValuePattern = element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject)
                && valuePatternObject is ValuePattern valuePattern
                && !valuePattern.Current.IsReadOnly;
            bool supportsNameFallback = !supportsWritableValuePattern && current.NativeWindowHandle != 0;
            bool targetIsElevatedRelativeToUs = current.NativeWindowHandle != 0
                && IsTargetProcessElevatedRelativeToUs(current.NativeWindowHandle);
            bool supportsHwndWin32Fallback = current.NativeWindowHandle != 0 && !targetIsElevatedRelativeToUs;
            string? elevationDisabledReason = targetIsElevatedRelativeToUs
                ? "Target process runs at a higher privilege level than WindowWorks; Win32 writes would be denied (UIPI)."
                : null;

            bool supportsTransform = element.TryGetCurrentPattern(TransformPattern.Pattern, out var transformPatternObject)
                && transformPatternObject is TransformPattern transformPatternForBounds
                && (transformPatternForBounds.Current.CanMove || transformPatternForBounds.Current.CanResize);
            int[]? runtimeId = element.GetRuntimeId();
            bool hasLegacyPattern = NativeUiaLegacyIAccessible.HasPattern(runtimeId, containingWindowHandle);
            bool supportsLegacyValue = !supportsWritableValuePattern && hasLegacyPattern;

            var properties = new List<PropertyInspectorProperty>
            {
                new(
                    "Name",
                    current.Name,
                    supportsWritableValuePattern
                        ? PropertyInspectorEditorKind.Text
                        : supportsLegacyValue
                            ? PropertyInspectorEditorKind.LegacyText
                            : supportsNameFallback
                                ? PropertyInspectorEditorKind.Text
                                : PropertyInspectorEditorKind.ReadOnly,
                    canEdit: supportsWritableValuePattern || supportsLegacyValue || supportsNameFallback,
                    disabledReason: supportsLegacyValue ? "Set through UIA LegacyIAccessiblePattern.SetValue." : null,
                    usesSetWindowTextFallback: !supportsWritableValuePattern && !supportsLegacyValue && supportsNameFallback),
                new("ControlType", current.ControlType?.ProgrammaticName ?? string.Empty),
                new("LocalizedControlType", current.LocalizedControlType),
                new("ClassName", current.ClassName),
                new("AutomationId", current.AutomationId),
                new(
                    "IsEnabled",
                    current.IsEnabled.ToString(),
                    supportsHwndWin32Fallback ? PropertyInspectorEditorKind.Win32Bool : PropertyInspectorEditorKind.ReadOnly,
                    canEdit: supportsHwndWin32Fallback,
                    disabledReason: supportsHwndWin32Fallback
                        ? "Enabled/disabled through the Win32 EnableWindow fallback."
                        : elevationDisabledReason,
                    win32BoolValue: current.IsEnabled,
                    win32BoolWriter: supportsHwndWin32Fallback ? WriteIsEnabled : null),
                new("IsOffscreen", current.IsOffscreen.ToString()),
                new("IsKeyboardFocusable", current.IsKeyboardFocusable.ToString()),
                new("HasKeyboardFocus", current.HasKeyboardFocus.ToString()),
                new(
                    "BoundingRectangle",
                    FormatRectangle(current.BoundingRectangle),
                    supportsTransform ? PropertyInspectorEditorKind.Transform : PropertyInspectorEditorKind.ReadOnly,
                    canEdit: supportsTransform,
                    disabledReason: supportsTransform
                        ? "Format: X, Y, Width, Height. Set through UIA TransformPattern (Move/Resize)."
                        : null),
                new("HelpText", current.HelpText),
                new("ItemStatus", current.ItemStatus),
                new("AcceleratorKey", current.AcceleratorKey),
                new("AccessKey", current.AccessKey),
                new("FrameworkId", current.FrameworkId),
                new("ProcessId", current.ProcessId.ToString(CultureInfo.InvariantCulture)),
                new("Orientation", current.Orientation.ToString())
            };

            if (valuePatternObject is ValuePattern currentValuePattern)
            {
                properties.Add(new PropertyInspectorProperty(
                    "ValuePattern.Value",
                    currentValuePattern.Current.Value,
                    currentValuePattern.Current.IsReadOnly ? PropertyInspectorEditorKind.ReadOnly : PropertyInspectorEditorKind.Text,
                    canEdit: !currentValuePattern.Current.IsReadOnly));
                properties.Add(new PropertyInspectorProperty("ValuePattern.IsReadOnly", currentValuePattern.Current.IsReadOnly.ToString()));
            }

            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePatternObject)
                && togglePatternObject is TogglePattern togglePattern)
            {
                properties.Add(new PropertyInspectorProperty(
                    "TogglePattern.ToggleState",
                    togglePattern.Current.ToggleState.ToString(),
                    PropertyInspectorEditorKind.Toggle,
                    canEdit: true,
                    toggleState: togglePattern.Current.ToggleState));
            }

            if (element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rangeValuePatternObject)
                && rangeValuePatternObject is RangeValuePattern rangeValuePattern)
            {
                var rangeCurrent = rangeValuePattern.Current;
                properties.Add(new PropertyInspectorProperty(
                    "RangeValuePattern.Value",
                    rangeCurrent.Value.ToString(CultureInfo.InvariantCulture),
                    rangeCurrent.IsReadOnly ? PropertyInspectorEditorKind.ReadOnly : PropertyInspectorEditorKind.Range,
                    canEdit: !rangeCurrent.IsReadOnly,
                    rangeMinimum: rangeCurrent.Minimum,
                    rangeMaximum: rangeCurrent.Maximum));
                properties.Add(new PropertyInspectorProperty("RangeValuePattern.Minimum", rangeCurrent.Minimum.ToString(CultureInfo.InvariantCulture)));
                properties.Add(new PropertyInspectorProperty("RangeValuePattern.Maximum", rangeCurrent.Maximum.ToString(CultureInfo.InvariantCulture)));
            }

            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionItemPatternObject)
                && selectionItemPatternObject is SelectionItemPattern selectionItemPattern)
            {
                properties.Add(new PropertyInspectorProperty(
                    "SelectionItemPattern.IsSelected",
                    selectionItemPattern.Current.IsSelected.ToString(),
                    PropertyInspectorEditorKind.SelectionItem,
                    canEdit: true,
                    disabledReason: "Checking selects this item. Clearing may fail if the provider does not support removal.",
                    selectionItemIsSelected: selectionItemPattern.Current.IsSelected));
            }

            if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandCollapsePatternObject)
                && expandCollapsePatternObject is ExpandCollapsePattern expandCollapsePattern)
            {
                var state = expandCollapsePattern.Current.ExpandCollapseState;
                properties.Add(new PropertyInspectorProperty(
                    "ExpandCollapsePattern.ExpandCollapseState",
                    state.ToString(),
                    PropertyInspectorEditorKind.ExpandCollapse,
                    canEdit: state != ExpandCollapseState.LeafNode,
                    disabledReason: state == ExpandCollapseState.LeafNode ? "LeafNode cannot be expanded or collapsed." : null,
                    options: Enum.GetNames(typeof(ExpandCollapseState)),
                    expandCollapseState: state));
            }

            if (element.TryGetCurrentPattern(WindowPattern.Pattern, out var windowPatternObject)
                && windowPatternObject is WindowPattern windowPattern)
            {
                var windowCurrent = windowPattern.Current;
                properties.Add(new PropertyInspectorProperty(
                    "WindowPattern.WindowVisualState",
                    windowCurrent.WindowVisualState.ToString(),
                    PropertyInspectorEditorKind.WindowVisualState,
                    canEdit: true,
                    options: Enum.GetNames(typeof(WindowVisualState)),
                    windowVisualState: windowCurrent.WindowVisualState));
                properties.Add(new PropertyInspectorProperty("WindowPattern.CanMaximize", windowCurrent.CanMaximize.ToString()));
                properties.Add(new PropertyInspectorProperty("WindowPattern.CanMinimize", windowCurrent.CanMinimize.ToString()));
                properties.Add(new PropertyInspectorProperty("WindowPattern.IsModal", windowCurrent.IsModal.ToString()));
                properties.Add(new PropertyInspectorProperty(
                    "WindowPattern.IsTopmost",
                    windowCurrent.IsTopmost.ToString(),
                    supportsHwndWin32Fallback ? PropertyInspectorEditorKind.Win32Bool : PropertyInspectorEditorKind.ReadOnly,
                    canEdit: supportsHwndWin32Fallback,
                    disabledReason: supportsHwndWin32Fallback
                        ? "Set through the Win32 SetWindowPos (HWND_TOPMOST/HWND_NOTOPMOST) fallback."
                        : elevationDisabledReason,
                    win32BoolValue: windowCurrent.IsTopmost,
                    win32BoolWriter: supportsHwndWin32Fallback ? WriteIsTopmost : null));
            }

            if (hasLegacyPattern && NativeUiaLegacyIAccessible.TryGetState(runtimeId, containingWindowHandle, out var legacyState))
            {
                properties.Add(new PropertyInspectorProperty(
                    "LegacyIAccessiblePattern.Value",
                    legacyState.Value,
                    supportsLegacyValue ? PropertyInspectorEditorKind.LegacyText : PropertyInspectorEditorKind.ReadOnly,
                    canEdit: supportsLegacyValue,
                    disabledReason: supportsLegacyValue ? null : "This element already exposes a writable ValuePattern; edit Name/ValuePattern.Value instead."));
                properties.Add(new PropertyInspectorProperty("LegacyIAccessiblePattern.Role", legacyState.Role));
                properties.Add(new PropertyInspectorProperty("LegacyIAccessiblePattern.DefaultAction", legacyState.DefaultAction));
                properties.Add(new PropertyInspectorProperty(
                    "LegacyIAccessiblePattern.DoDefaultAction",
                    legacyState.DefaultAction,
                    string.IsNullOrEmpty(legacyState.DefaultAction) ? PropertyInspectorEditorKind.ReadOnly : PropertyInspectorEditorKind.Invoke,
                    canEdit: !string.IsNullOrEmpty(legacyState.DefaultAction),
                    disabledReason: string.IsNullOrEmpty(legacyState.DefaultAction)
                        ? "No default action is reported for this element."
                        : $"Invokes '{legacyState.DefaultAction}' through UIA LegacyIAccessiblePattern.DoDefaultAction."));
            }

            if (transformPatternObject is TransformPattern transformPatternForDiagnostics)
            {
                var transformCurrent = transformPatternForDiagnostics.Current;
                properties.Add(new PropertyInspectorProperty("TransformPattern.CanMove", transformCurrent.CanMove.ToString()));
                properties.Add(new PropertyInspectorProperty("TransformPattern.CanResize", transformCurrent.CanResize.ToString()));
                properties.Add(new PropertyInspectorProperty("TransformPattern.CanRotate", transformCurrent.CanRotate.ToString()));
            }

            return properties;
        }

        /// <summary>
        /// UIPI (User Interface Privilege Isolation) blocks EnableWindow/SetWindowPos/SetWindowText
        /// against a target window owned by a higher-integrity-level process. This check surfaces
        /// that constraint at read time (so the property shows read-only with a clear reason)
        /// instead of letting the user attempt a write that is guaranteed to fail.
        /// </summary>
        private static bool IsTargetProcessElevatedRelativeToUs(int nativeWindowHandle)
        {
            try
            {
                IntPtr hwnd = new(nativeWindowHandle);
                if (hwnd == IntPtr.Zero)
                {
                    return false;
                }

                NativeMethods.GetWindowThreadProcessId(hwnd, out uint targetProcessId);
                if (targetProcessId == 0)
                {
                    return false;
                }

                int? targetLevel = TryGetIntegrityLevel((uint)Environment.ProcessId == targetProcessId
                    ? NativeMethods.GetCurrentProcess()
                    : NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, targetProcessId));
                int? ourLevel = TryGetIntegrityLevel(NativeMethods.GetCurrentProcess());

                return targetLevel.HasValue && ourLevel.HasValue && targetLevel.Value > ourLevel.Value;
            }
            catch
            {
                // Any failure here should never block the property from being read; fall back to
                // "not elevated" and let the write attempt itself surface the real Win32 error.
                return false;
            }
        }

        private static int? TryGetIntegrityLevel(IntPtr processHandle)
        {
            bool ownedHandle = false;
            try
            {
                if (processHandle == IntPtr.Zero)
                {
                    return null;
                }

                if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TOKEN_QUERY, out IntPtr tokenHandle))
                {
                    return null;
                }

                ownedHandle = true;
                try
                {
                    int length = 0;
                    NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenIntegrityLevel, IntPtr.Zero, 0, out length);
                    if (length == 0)
                    {
                        return null;
                    }

                    IntPtr buffer = Marshal.AllocHGlobal(length);
                    try
                    {
                        if (!NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenIntegrityLevel, buffer, length, out _))
                        {
                            return null;
                        }

                        IntPtr sidPointer = Marshal.ReadIntPtr(buffer);
                        IntPtr subAuthorityCountPointer = NativeMethods.GetSidSubAuthorityCount(sidPointer);
                        byte subAuthorityCount = Marshal.ReadByte(subAuthorityCountPointer);
                        IntPtr subAuthorityPointer = NativeMethods.GetSidSubAuthority(sidPointer, (uint)(subAuthorityCount - 1));
                        return Marshal.ReadInt32(subAuthorityPointer);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    if (ownedHandle)
                    {
                        NativeMethods.CloseHandle(tokenHandle);
                    }
                }
            }
            finally
            {
                if (processHandle != IntPtr.Zero && processHandle != NativeMethods.GetCurrentProcess())
                {
                    NativeMethods.CloseHandle(processHandle);
                }
            }
        }

        private static (bool Success, string? ErrorMessage) WriteIsEnabled(IntPtr hwnd, bool desiredValue)
        {
            NativeMethods.EnableWindow(hwnd, desiredValue);
            return (true, null);
        }

        private static (bool Success, string? ErrorMessage) WriteIsTopmost(IntPtr hwnd, bool desiredValue)
        {
            IntPtr insertAfter = desiredValue ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST;
            if (!NativeMethods.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE))
            {
                int error = Marshal.GetLastWin32Error();
                return (false, $"SetWindowPos fallback failed{(error == 0 ? "." : $" (Win32 error {error}).")}");
            }

            return (true, null);
        }

        private void PostIfCurrent(long generation, Action action)
        {
            try
            {
                _dispatcher.BeginInvoke(() =>
                {
                    if (Volatile.Read(ref _disposed) == 0
                        && Volatile.Read(ref _selectionGeneration) == generation)
                    {
                        action();
                    }
                });
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void PostIfCurrent(long selectionGeneration, long viewGeneration, Action action)
        {
            PostIfCurrent(selectionGeneration, () =>
            {
                if (Volatile.Read(ref _viewGeneration) == viewGeneration)
                {
                    action();
                }
            });
        }

        private bool IsCurrent(long selectionGeneration, long viewGeneration)
        {
            return Volatile.Read(ref _disposed) == 0
                && Volatile.Read(ref _selectionGeneration) == selectionGeneration
                && Volatile.Read(ref _viewGeneration) == viewGeneration;
        }

        private static void ObserveFault(Task task)
        {
            _ = task.ContinueWith(
                completedTask => _ = completedTask.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void ObserveTimedOutWriteCompletion(
            Task<PropertyWriteResult> writeTask,
            long selectionGeneration,
            long viewGeneration,
            PropertyInspectorWindow window)
        {
            _ = writeTask.ContinueWith(
                completedTask =>
                {
                    var result = completedTask.Result;
                    if (!result.Succeeded)
                    {
                        PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(
                            $"The earlier property update completed after the timeout: {result.Message}"));
                        return;
                    }

                    if (result.Properties is null)
                    {
                        PostIfCurrent(selectionGeneration, viewGeneration, () => window.ShowOperationFailure(
                            "The earlier property update completed after the timeout, but live properties could not be refreshed."));
                        return;
                    }

                    PostIfCurrent(selectionGeneration, viewGeneration, () =>
                    {
                        window.UpdateProperties(result.Properties);
                        window.ShowOperationSuccess($"The earlier property update completed after the timeout. {result.Message}");
                    });
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void ShowUnavailableMessage()
        {
            System.Windows.MessageBox.Show(
                "The selected UI element is unavailable or did not respond in time.",
                "Inspect UI Element",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        private static void ShowReadInProgressMessage()
        {
            System.Windows.MessageBox.Show(
                "A prior UI element inspection is still waiting for a target to respond. Try again after it finishes.",
                "Inspect UI Element",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        private const string TargetChangedMessage =
            "The selected target is no longer available or has changed. No property update was performed.";

        private static string FormatRectangle(System.Windows.Rect rectangle)
        {
            return $"X={rectangle.X:0.##}, Y={rectangle.Y:0.##}, Width={rectangle.Width:0.##}, Height={rectangle.Height:0.##}";
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Interlocked.Increment(ref _selectionGeneration);
            Interlocked.Increment(ref _viewGeneration);
            _activeSession?.Cancel();
            _activeSession = null;
            _devToolsOperationGate.Dispose();
        }

        private sealed class PropertyReadResult
        {
            public PropertyReadResult(
                IReadOnlyList<PropertyInspectorProperty>? properties,
                CapturedIdentitySnapshot? identitySnapshot,
                string? selectedElementRuntimeId)
            {
                Properties = properties;
                IdentitySnapshot = identitySnapshot;
                SelectedElementRuntimeId = selectedElementRuntimeId;
            }

            public IReadOnlyList<PropertyInspectorProperty>? Properties { get; }
            public CapturedIdentitySnapshot? IdentitySnapshot { get; }
            public string? SelectedElementRuntimeId { get; }
        }

        private sealed class PropertyWriteResult
        {
            private PropertyWriteResult(bool succeeded, string message, IReadOnlyList<PropertyInspectorProperty>? properties, bool devToolsUnavailable)
            {
                Succeeded = succeeded;
                Message = message;
                Properties = properties;
                DevToolsUnavailable = devToolsUnavailable;
            }

            public bool Succeeded { get; }
            public string Message { get; }
            public IReadOnlyList<PropertyInspectorProperty>? Properties { get; }

            /// <summary>
            /// True if the current selection is a Chromium-family browser but no live DevTools
            /// endpoint was found on this attempt (docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md §4
            /// Phase E, sub-phase 6) — drives whether the window's "Relaunch with DevTools" bar
            /// should be shown. Always <c>false</c> for plain UIA writes/refreshes that never
            /// attempted a DevTools append (the bar's visibility is simply left unchanged in that
            /// case rather than incorrectly toggled off).
            /// </summary>
            public bool DevToolsUnavailable { get; }

            public static PropertyWriteResult Success(string message, IReadOnlyList<PropertyInspectorProperty> properties, bool devToolsUnavailable = false) =>
                new(true, message, properties, devToolsUnavailable);

            public static PropertyWriteResult Failure(string message) => new(false, message, null, false);
        }

        private static class NativeMethods
        {
            internal static readonly IntPtr HWND_TOPMOST = new(-1);
            internal static readonly IntPtr HWND_NOTOPMOST = new(-2);
            internal const uint SWP_NOMOVE = 0x0002;
            internal const uint SWP_NOSIZE = 0x0001;
            internal const uint SWP_NOACTIVATE = 0x0010;
            internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            internal const uint TOKEN_QUERY = 0x0008;
            internal const int TokenIntegrityLevel = 25;

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetWindowText(IntPtr hWnd, string lpString);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bEnable);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

            [DllImport("kernel32.dll")]
            internal static extern IntPtr GetCurrentProcess();

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseHandle(IntPtr hObject);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

            [DllImport("advapi32.dll", SetLastError = true)]
            internal static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

            [DllImport("advapi32.dll", SetLastError = true)]
            internal static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthorityIndex);
        }
    }
}
