using System.Windows;

namespace InspectionEditor
{
    public partial class InputDialog : Window
    {
        public string ResponseText { get; private set; } = "";

        public InputDialog(string prompt, string title = "Input")
        {
            InitializeComponent();
            Title = title;
            PromptText.Text = prompt;
            InputTextBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            ResponseText = InputTextBox.Text;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
