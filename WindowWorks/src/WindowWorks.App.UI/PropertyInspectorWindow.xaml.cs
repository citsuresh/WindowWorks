using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowWorks.App.UI
{
    /// <summary>
    /// Attempts a Win32 fallback write for a <see cref="PropertyInspectorEditorKind.Win32Bool"/>
    /// property against the given native window handle. Each property supplies its own writer at
    /// construction time (see <see cref="PropertyInspectorProperty"/>) so the write dispatcher
    /// never needs to hardcode property names/switch on them - adding a new Win32-writable
    /// property only requires attaching a new writer where that property is read.
    /// </summary>
    public delegate (bool Success, string? ErrorMessage) Win32BoolWriter(IntPtr hwnd, bool desiredValue);

    public enum PropertyInspectorEditorKind
    {
        ReadOnly,
        Text,
        Toggle,
        Range,
        SelectionItem,
        ExpandCollapse,
        WindowVisualState,
        Win32Bool,
        /// <summary>Text editing backed by UIA LegacyIAccessiblePattern.SetValue (MSAA bridge).</summary>
        LegacyText,
        /// <summary>A trigger action backed by UIA LegacyIAccessiblePattern.DoDefaultAction.</summary>
        Invoke,
        /// <summary>Bounding-rectangle editing (X,Y,Width,Height) backed by UIA TransformPattern.</summary>
        Transform
    }

    public sealed class PropertyInspectorProperty : INotifyPropertyChanged
    {
        private string _value;
        private string _editText;
        private bool _editBool;
        private bool? _editNullableBool;

        public PropertyInspectorProperty(
            string name,
            string? value,
            PropertyInspectorEditorKind editorKind = PropertyInspectorEditorKind.ReadOnly,
            bool canEdit = false,
            string? disabledReason = null,
            IReadOnlyList<string>? options = null,
            ToggleState? toggleState = null,
            ExpandCollapseState? expandCollapseState = null,
            WindowVisualState? windowVisualState = null,
            double? rangeMinimum = null,
            double? rangeMaximum = null,
            bool? selectionItemIsSelected = null,
            bool usesSetWindowTextFallback = false,
            bool? win32BoolValue = null,
            Win32BoolWriter? win32BoolWriter = null)
        {
            Name = name;
            EditorKind = editorKind;
            CanEdit = canEdit;
            DisabledReason = disabledReason;
            Options = options ?? Array.Empty<string>();
            ToggleState = toggleState;
            ExpandCollapseState = expandCollapseState;
            WindowVisualState = windowVisualState;
            RangeMinimum = rangeMinimum;
            RangeMaximum = rangeMaximum;
            SelectionItemIsSelected = selectionItemIsSelected;
            UsesSetWindowTextFallback = usesSetWindowTextFallback;
            Win32BoolWriter = win32BoolWriter;
            _value = value ?? string.Empty;
            _editText = _value;
            _editBool = win32BoolValue ?? selectionItemIsSelected ?? bool.TryParse(_value, out bool parsedBool) && parsedBool;
            _editNullableBool = toggleState switch
            {
                System.Windows.Automation.ToggleState.On => true,
                System.Windows.Automation.ToggleState.Off => false,
                System.Windows.Automation.ToggleState.Indeterminate => null,
                _ => null
            };
        }

        public string Name { get; }
        public PropertyInspectorEditorKind EditorKind { get; }
        public bool CanEdit { get; }
        public bool IsReadOnly => !CanEdit || EditorKind == PropertyInspectorEditorKind.ReadOnly;
        public string? DisabledReason { get; }
        public IReadOnlyList<string> Options { get; }
        public ToggleState? ToggleState { get; }
        public ExpandCollapseState? ExpandCollapseState { get; }
        public WindowVisualState? WindowVisualState { get; }
        public double? RangeMinimum { get; }
        public double? RangeMaximum { get; }
        public bool? SelectionItemIsSelected { get; }
        public bool UsesSetWindowTextFallback { get; }
        public Win32BoolWriter? Win32BoolWriter { get; }
        public string RangeTooltip => RangeMinimum.HasValue && RangeMaximum.HasValue
            ? $"Valid range: {RangeMinimum.Value.ToString(CultureInfo.InvariantCulture)} to {RangeMaximum.Value.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

        public string Value
        {
            get => _value;
            set
            {
                if (string.Equals(_value, value, StringComparison.Ordinal))
                {
                    return;
                }

                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public string EditText
        {
            get => _editText;
            set
            {
                if (string.Equals(_editText, value, StringComparison.Ordinal))
                {
                    return;
                }

                _editText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditText)));
            }
        }

        public bool EditBool
        {
            get => _editBool;
            set
            {
                if (_editBool == value)
                {
                    return;
                }

                _editBool = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditBool)));
            }
        }

        public bool? EditNullableBool
        {
            get => _editNullableBool;
            set
            {
                if (_editNullableBool == value)
                {
                    return;
                }

                _editNullableBool = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditNullableBool)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class PropertyInspectorEditorTemplateSelector : DataTemplateSelector
    {
        public required DataTemplate ReadOnlyTemplate { get; init; }
        public required DataTemplate TextTemplate { get; init; }
        public required DataTemplate ToggleTemplate { get; init; }
        public required DataTemplate RangeTemplate { get; init; }
        public required DataTemplate SelectionItemTemplate { get; init; }
        public required DataTemplate ExpandCollapseTemplate { get; init; }
        public required DataTemplate WindowVisualStateTemplate { get; init; }
        public required DataTemplate Win32BoolTemplate { get; init; }
        public required DataTemplate LegacyTextTemplate { get; init; }
        public required DataTemplate InvokeTemplate { get; init; }
        public required DataTemplate TransformTemplate { get; init; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is not PropertyInspectorProperty property)
            {
                return ReadOnlyTemplate;
            }

            return property.EditorKind switch
            {
                PropertyInspectorEditorKind.Text => TextTemplate,
                PropertyInspectorEditorKind.Toggle => ToggleTemplate,
                PropertyInspectorEditorKind.Range => RangeTemplate,
                PropertyInspectorEditorKind.SelectionItem => SelectionItemTemplate,
                PropertyInspectorEditorKind.ExpandCollapse => ExpandCollapseTemplate,
                PropertyInspectorEditorKind.WindowVisualState => WindowVisualStateTemplate,
                PropertyInspectorEditorKind.Win32Bool => Win32BoolTemplate,
                PropertyInspectorEditorKind.LegacyText => LegacyTextTemplate,
                PropertyInspectorEditorKind.Invoke => InvokeTemplate,
                PropertyInspectorEditorKind.Transform => TransformTemplate,
                _ => ReadOnlyTemplate
            };
        }
    }

    public partial class PropertyInspectorWindow : Window, INotifyPropertyChanged
    {
        private IReadOnlyList<PropertyInspectorProperty> _properties;

        public PropertyInspectorWindow(
            CapturedIdentitySnapshot? capturedIdentity,
            IReadOnlyList<PropertyInspectorProperty> properties,
            string? selectedElementRuntimeId)
        {
            CapturedIdentity = capturedIdentity;
            _properties = properties ?? throw new ArgumentNullException(nameof(properties));
            SelectedElementRuntimeId = selectedElementRuntimeId;
            DataContext = this;
            InitializeComponent();
        }

        public CapturedIdentitySnapshot? CapturedIdentity { get; }
        public IReadOnlyList<PropertyInspectorProperty> Properties
        {
            get => _properties;
            private set
            {
                _properties = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Properties)));
            }
        }

        public string? SelectedElementRuntimeId { get; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<PropertyInspectorProperty, string>? TextCommitRequested;
        public event Action<PropertyInspectorProperty, string>? LegacyTextCommitRequested;
        public event Action<PropertyInspectorProperty, bool?>? ToggleCommitRequested;
        public event Action<PropertyInspectorProperty, bool>? SelectionItemCommitRequested;
        public event Action<PropertyInspectorProperty, string>? ComboCommitRequested;
        public event Action<PropertyInspectorProperty, bool>? Win32BoolCommitRequested;
        public event Action<PropertyInspectorProperty>? InvokeCommitRequested;
        public event Action<PropertyInspectorProperty, System.Windows.Rect>? TransformCommitRequested;

        public void UpdateProperties(IReadOnlyList<PropertyInspectorProperty> properties)
        {
            Properties = properties ?? throw new ArgumentNullException(nameof(properties));
        }

        public void ShowOperationFailure(string message)
        {
            StatusTextBlock.Foreground = Brushes.OrangeRed;
            StatusTextBlock.Text = message;
        }

        public void ShowOperationSuccess(string message)
        {
            StatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(255, 183, 77));
            StatusTextBlock.Text = message;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void InlineTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox textBox)
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitTextEdit(textBox, textBox.Text ?? string.Empty);
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (textBox.Tag is PropertyInspectorProperty taggedProperty)
                {
                    textBox.Text = taggedProperty.Value;
                    taggedProperty.EditText = taggedProperty.Value;
                }
                e.Handled = true;
                return;
            }

            ApplyLayeredWindowTextInputWorkaround(textBox, e);
        }

        private void InlineTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                CommitTextEdit(textBox, textBox.Text ?? string.Empty);
            }
        }

        private void ApplyTextButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not PropertyInspectorProperty property)
            {
                return;
            }

            CommitTextEdit(null, property.EditText ?? string.Empty, property);
        }

        private void CommitTextEdit(TextBox? textBox, string value, PropertyInspectorProperty? propertyOverride = null)
        {
            PropertyInspectorProperty? property = propertyOverride ?? textBox?.Tag as PropertyInspectorProperty;
            if (property is null)
            {
                return;
            }

            if (!property.CanEdit)
            {
                if (textBox is not null)
                {
                    textBox.Text = property.Value;
                }

                property.EditText = property.Value;
                return;
            }

            if (string.Equals(value, property.Value, StringComparison.Ordinal))
            {
                property.EditText = property.Value;
                return;
            }

            if (property.EditorKind == PropertyInspectorEditorKind.Text && property.UsesSetWindowTextFallback)
            {
                var result = MessageBox.Show(
                    $"Apply the Name update?\n\nNew value:\n{value}\n\n" +
                    "WindowWorks will try UI Automation ValuePattern first. Only if that pattern " +
                    "is unsupported will it use the higher-risk Win32 SetWindowText fallback.",
                    "Inspect UI Element",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                {
                    if (textBox is not null)
                    {
                        textBox.Text = property.Value;
                    }

                    property.EditText = property.Value;
                    return;
                }
            }

            if (property.EditorKind == PropertyInspectorEditorKind.LegacyText)
            {
                LegacyTextCommitRequested?.Invoke(property, value);
                return;
            }

            TextCommitRequested?.Invoke(property, value);
        }

        private void ToggleCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggleButton && toggleButton.Tag is PropertyInspectorProperty property)
            {
                ToggleCommitRequested?.Invoke(property, toggleButton.IsChecked);
            }
        }

        private void SelectionItemCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggleButton && toggleButton.Tag is PropertyInspectorProperty property)
            {
                SelectionItemCommitRequested?.Invoke(property, toggleButton.IsChecked == true);
            }
        }

        private void Win32BoolCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggleButton && toggleButton.Tag is PropertyInspectorProperty property)
            {
                Win32BoolCommitRequested?.Invoke(property, toggleButton.IsChecked == true);
            }
        }

        private void InvokeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PropertyInspectorProperty property)
            {
                InvokeCommitRequested?.Invoke(property);
            }
        }

        private void TransformTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            CommitTransformEdit(sender is TextBox textBox ? textBox : null, (sender as TextBox)?.Text ?? string.Empty);
        }

        private void ApplyTransformButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not PropertyInspectorProperty property)
            {
                return;
            }

            CommitTransformEdit(null, property.EditText ?? string.Empty, property);
        }

        private void TransformTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (sender is TextBox textBox)
                {
                    CommitTransformEdit(textBox, textBox.Text ?? string.Empty);
                }

                return;
            }

            if (sender is TextBox escapeTextBox && e.Key == Key.Escape && escapeTextBox.Tag is PropertyInspectorProperty taggedProperty)
            {
                escapeTextBox.Text = taggedProperty.Value;
                taggedProperty.EditText = taggedProperty.Value;
                e.Handled = true;
                return;
            }

            if (sender is TextBox inputTextBox)
            {
                ApplyLayeredWindowTextInputWorkaround(inputTextBox, e);
            }
        }

        private void CommitTransformEdit(TextBox? textBox, string value, PropertyInspectorProperty? propertyOverride = null)
        {
            PropertyInspectorProperty? property = propertyOverride ?? textBox?.Tag as PropertyInspectorProperty;
            if (property is null)
            {
                return;
            }

            if (!property.CanEdit)
            {
                if (textBox is not null)
                {
                    textBox.Text = property.Value;
                }

                property.EditText = property.Value;
                return;
            }

            if (string.Equals(value, property.Value, StringComparison.Ordinal))
            {
                property.EditText = property.Value;
                return;
            }

            if (!TryParseRectangle(value, out System.Windows.Rect rectangle))
            {
                MessageBox.Show(
                    "Enter the bounding rectangle as: X, Y, Width, Height (invariant numbers).",
                    "Inspect UI Element",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                if (textBox is not null)
                {
                    textBox.Text = property.Value;
                }

                property.EditText = property.Value;
                return;
            }

            TransformCommitRequested?.Invoke(property, rectangle);
        }

        private static bool TryParseRectangle(string value, out System.Windows.Rect rectangle)
        {
            rectangle = default;
            string[] parts = value.Split(',');
            if (parts.Length != 4)
            {
                return false;
            }

            var numbers = new double[4];
            for (int i = 0; i < 4; i++)
            {
                string part = parts[i].Trim();

                // Accept either a plain number ("-33") or a labeled segment ("X=-33"), since the
                // displayed/round-tripped value is formatted with labels (see FormatRectangle).
                int equalsIndex = part.IndexOf('=');
                if (equalsIndex >= 0)
                {
                    part = part[(equalsIndex + 1)..].Trim();
                }

                if (!double.TryParse(part, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out numbers[i]))
                {
                    return false;
                }
            }

            rectangle = new System.Windows.Rect(numbers[0], numbers[1], numbers[2], numbers[3]);
            return true;
        }

        private void EditorComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox comboBox)
            {
                comboBox.Tag = new ComboBoxCommitState { HasLoadedOnce = true };
            }
        }

        private void EditorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox comboBox
                || !comboBox.IsLoaded
                || comboBox.SelectedItem is not string selectedValue
                || comboBox.DataContext is not PropertyInspectorProperty property)
            {
                return;
            }

            if (!property.CanEdit || string.Equals(selectedValue, property.Value, StringComparison.Ordinal))
            {
                return;
            }

            ComboCommitRequested?.Invoke(property, selectedValue);
        }

        /// <summary>
        /// Same layered-window text-entry workaround already needed elsewhere in this codebase:
        /// WPF TextBox receives PreviewKeyDown but no WM_CHAR/TextChanged under AllowsTransparency.
        /// </summary>
        private static void ApplyLayeredWindowTextInputWorkaround(TextBox textBox, KeyEventArgs e)
        {
            if (e.Key == Key.Back)
            {
                if (textBox.SelectionLength > 0)
                {
                    textBox.SelectedText = string.Empty;
                }
                else if (textBox.CaretIndex > 0)
                {
                    int index = textBox.CaretIndex - 1;
                    textBox.Text = textBox.Text.Remove(index, 1);
                    textBox.CaretIndex = index;
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Delete)
            {
                if (textBox.SelectionLength > 0)
                {
                    textBox.SelectedText = string.Empty;
                }
                else if (textBox.CaretIndex < textBox.Text.Length)
                {
                    textBox.Text = textBox.Text.Remove(textBox.CaretIndex, 1);
                }
                e.Handled = true;
                return;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End
                or Key.Tab or Key.Enter or Key.Escape or Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.CapsLock or Key.Insert or Key.PageUp or Key.PageDown
                or Key.F1 or Key.F2 or Key.F3 or Key.F4 or Key.F5 or Key.F6
                or Key.F7 or Key.F8 or Key.F9 or Key.F10 or Key.F11 or Key.F12)
            {
                return;
            }

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0)
            {
                return;
            }

            var keyboardState = new byte[256];
            if (!NativeMethods.GetKeyboardState(keyboardState))
            {
                return;
            }

            uint scanCode = NativeMethods.MapVirtualKey(vk, NativeMethods.MAPVK_VK_TO_VSC);
            var buffer = new StringBuilder(8);
            int result = NativeMethods.ToUnicode(vk, scanCode, keyboardState, buffer, buffer.Capacity, 0);
            if (result <= 0)
            {
                return;
            }

            string text = buffer.ToString(0, result);
            if (textBox.SelectionLength > 0)
            {
                textBox.SelectedText = string.Empty;
            }
            int caret = textBox.CaretIndex;
            textBox.Text = textBox.Text.Insert(caret, text);
            textBox.CaretIndex = caret + text.Length;
            e.Handled = true;
        }

        private void OnDragHandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindAncestorOrSelf<Button>(source) is not null)
            {
                return;
            }

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            const int WM_NCLBUTTONDOWN = 0x00A1;
            const int HTCAPTION = 2;

            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
            e.Handled = true;
        }

        private static T? FindAncestorOrSelf<T>(DependencyObject source) where T : DependencyObject
        {
            var current = source;
            while (current is not null)
            {
                if (current is T match)
                {
                    return match;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private sealed class ComboBoxCommitState
        {
            public bool HasLoadedOnce { get; set; }
        }

        private static class NativeMethods
        {
            public const uint MAPVK_VK_TO_VSC = 0;

            [DllImport("user32.dll")]
            public static extern bool ReleaseCapture();

            [DllImport("user32.dll")]
            public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern bool GetKeyboardState(byte[] lpKeyState);

            [DllImport("user32.dll")]
            public static extern uint MapVirtualKey(uint uCode, uint uMapType);

            [DllImport("user32.dll")]
            public static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
                StringBuilder pwszBuff, int cchBuff, uint wFlags);
        }
    }
}
