using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;

namespace InspectionEditor
{
    public partial class FactCheckWindow : Window
    {
        private static readonly Regex UrlRegex = new(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public FactCheckWindow(string message)
        {
            InitializeComponent();
            PopulateMessage(message ?? "");
        }

        private void PopulateMessage(string message)
        {
            MessageTextBlock.Inlines.Clear();

            int index = 0;
            foreach (Match match in UrlRegex.Matches(message))
            {
                if (match.Index > index)
                    AddPlainText(message.Substring(index, match.Index - index));

                string url = match.Value.TrimEnd('.', ',', ';', ')');
                string trailing = match.Value.Substring(url.Length);

                var link = new Hyperlink(new Run(url))
                {
                    NavigateUri = new Uri(url),
                    ToolTip = url
                };
                link.RequestNavigate += Link_RequestNavigate;
                MessageTextBlock.Inlines.Add(link);

                if (!string.IsNullOrEmpty(trailing))
                    AddPlainText(trailing);

                index = match.Index + match.Length;
            }

            if (index < message.Length)
                AddPlainText(message.Substring(index));
        }

        private void AddPlainText(string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    MessageTextBlock.Inlines.Add(new LineBreak());
                if (lines[i].Length > 0)
                    MessageTextBlock.Inlines.Add(new Run(lines[i]));
            }
        }

        private static void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                Clipboard.SetText(e.Uri.AbsoluteUri);
                MessageBox.Show("Could not open the link, so RED copied it to the clipboard.", "Open Link", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            e.Handled = true;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
