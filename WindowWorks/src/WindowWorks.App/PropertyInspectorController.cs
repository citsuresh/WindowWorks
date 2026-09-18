using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Threading;
using WindowWorks.App.UI;

namespace WindowWorks.App
{
    /// <summary>
    /// Drives the native UI Automation inspection flow. Property mutation is intentionally not
    /// part of this controller; a captured identity accompanies the read-only view for a later
    /// Apply-time staleness check.
    /// </summary>
    public sealed class PropertyInspectorController : IDisposable
    {
        private static readonly TimeSpan UiaReadTimeout = TimeSpan.FromSeconds(3);

        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private WindowPickerSession? _activeSession;
        private long _selectionGeneration;
        private int _disposed;
        private int _uiaReadInProgress;

        public void InvokePicker()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            // Starting another inspection invalidates any outstanding UIA result before it can
            // display a stale element's properties.
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

        private async Task ReadAndShowAsync(
            PropertyInspectorSelection selection,
            long generation)
        {
            // UIA calls cannot be cancelled safely once a provider is blocked. Limit the
            // controller to one background call so repeated picks cannot accumulate stuck threads.
            if (Interlocked.CompareExchange(ref _uiaReadInProgress, 1, 0) != 0)
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
                            Interlocked.Exchange(ref _uiaReadInProgress, 0);
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
            catch
            {
                Interlocked.Exchange(ref _uiaReadInProgress, 0);
                PostIfCurrent(generation, ShowUnavailableMessage);
                return;
            }

            var completedTask = await Task.WhenAny(readTask, Task.Delay(UiaReadTimeout)).ConfigureAwait(false);
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

            PostIfCurrent(
                generation,
                () => new PropertyInspectorWindow(
                    result.IdentitySnapshot,
                    result.Properties,
                    result.SelectedElementRuntimeId).Show());
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
                if (!string.Equals(
                    FormatRuntimeId(element.GetRuntimeId()),
                    selection.SelectedElementRuntimeId,
                    StringComparison.Ordinal))
                {
                    return new PropertyReadResult(null, null, null);
                }

                var properties = ReadProperties(element);
                if (!string.Equals(
                    FormatRuntimeId(element.GetRuntimeId()),
                    selection.SelectedElementRuntimeId,
                    StringComparison.Ordinal)
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

        private static string? FormatRuntimeId(int[]? runtimeIdParts)
        {
            return runtimeIdParts is null || runtimeIdParts.Length == 0
                ? null
                : string.Join(
                    ",",
                    runtimeIdParts.Select(static value => value.ToString(CultureInfo.InvariantCulture)));
        }

        private static IReadOnlyList<PropertyInspectorProperty> ReadProperties(AutomationElement element)
        {
            var current = element.Current;
            var properties = new List<PropertyInspectorProperty>
            {
                new("Name", current.Name),
                new("IsEnabled", current.IsEnabled.ToString()),
                new("BoundingRectangle", FormatRectangle(current.BoundingRectangle))
            };

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject)
                && valuePatternObject is ValuePattern valuePattern)
            {
                properties.Add(new PropertyInspectorProperty("ValuePattern.Value", valuePattern.Current.Value));
            }

            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePatternObject)
                && togglePatternObject is TogglePattern togglePattern)
            {
                properties.Add(new PropertyInspectorProperty("TogglePattern.ToggleState", togglePattern.Current.ToggleState.ToString()));
            }

            return properties;
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
                // The app's UI dispatcher has already shut down.
            }
        }

        private static void ObserveFault(Task task)
        {
            _ = task.ContinueWith(
                completedTask => _ = completedTask.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
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
            _activeSession?.Cancel();
            _activeSession = null;
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
    }
}
