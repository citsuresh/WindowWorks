using System.Windows;

namespace WindowWorks.App.UI
{
    public partial class OnboardingOverlay : Window
    {
        public OnboardingOverlay()
        {
            InitializeComponent();
            // Non-modal by design: Show without blocking
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        public void ShowOverlay()
        {
            if (!IsVisible) Show();
            Activate();
        }
    }
}
