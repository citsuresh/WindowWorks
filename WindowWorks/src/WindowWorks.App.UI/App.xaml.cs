using System.Windows;

namespace WindowWorks.App.UI
{
    public partial class WpfApp : Application
    {
        // Intentionally minimal. WPF Application will be constructed/started from the WinForms Program bootstrap.
        // Service provider injected at app startup for DI support in UI views and controls.
        public System.IServiceProvider? Services { get; set; }
    }
}

