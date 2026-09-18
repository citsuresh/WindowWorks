using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowWorks.App.UI
{
    public sealed class PropertyInspectorProperty
    {
        public PropertyInspectorProperty(string name, string? value)
        {
            Name = name;
            Value = value ?? string.Empty;
        }

        public string Name { get; }
        public string Value { get; }
    }

    public partial class PropertyInspectorWindow : Window
    {
        public PropertyInspectorWindow(
            CapturedIdentitySnapshot? capturedIdentity,
            IReadOnlyList<PropertyInspectorProperty> properties,
            string? selectedElementRuntimeId)
        {
            CapturedIdentity = capturedIdentity;
            Properties = properties ?? throw new ArgumentNullException(nameof(properties));
            SelectedElementRuntimeId = selectedElementRuntimeId;
            DataContext = this;
            InitializeComponent();
        }

        public CapturedIdentitySnapshot? CapturedIdentity { get; }
        public IReadOnlyList<PropertyInspectorProperty> Properties { get; }
        public string? SelectedElementRuntimeId { get; }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Lets the user drag this borderless window by its title-bar-equivalent area, mirroring
        /// <see cref="PickerElementTreeWindow"/>'s drag handle. Guards against starting a drag
        /// when the click landed on the Close button so it remains clickable.
        /// </summary>
        private void OnDragHandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindAncestorOrSelf<System.Windows.Controls.Button>(source) is not null)
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

        private static class NativeMethods
        {
            [DllImport("user32.dll")]
            public static extern bool ReleaseCapture();

            [DllImport("user32.dll")]
            public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
        }
    }
}
