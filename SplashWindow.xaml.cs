using System.Windows;

namespace InspectionEditor
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
        }

        public void SetStatus(string message)
        {
            Dispatcher.Invoke(() => StatusText.Text = message);
        }
    }
}
