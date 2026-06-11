using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using InspectionEditor.Services;

namespace InspectionEditor
{
    public partial class ActivationWindow : Window
    {
        public bool IsActivated { get; private set; }
        private readonly string _machineId;

        public ActivationWindow()
        {
            InitializeComponent();
            _machineId = LicenseService.GetMachineId();
            MachineIdText.Text = _machineId;
        }

        public ActivationWindow(string message) : this()
        {
            StatusMessage.Text = message;
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_machineId);
                CopyButton.Content = "✅ Copied!";
                
                // Reset button text after 2 seconds
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                timer.Tick += (s, args) =>
                {
                    CopyButton.Content = "📋 Copy Machine Code";
                    timer.Stop();
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select License File",
                Filter = "License files (*.lic)|*.lic|All files (*.*)|*.*",
                DefaultExt = ".lic"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    // Copy the license file to the app directory
                    var destPath = LicenseService.GetLicenseFilePath();
                    File.Copy(dialog.FileName, destPath, overwrite: true);

                    // Validate the new license
                    var result = LicenseService.ValidateLicense();
                    
                    if (result.IsValid)
                    {
                        IsActivated = true;
                        MessageBox.Show(
                            $"✅ License activated successfully!\n\n" +
                            $"Licensed to: {result.IssuedTo}\n" +
                            $"{result.Message}",
                            "Activation Successful",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        DialogResult = true;
                        Close();
                    }
                    else
                    {
                        // Remove invalid license file
                        if (File.Exists(destPath))
                        {
                            File.Delete(destPath);
                        }
                        
                        MessageBox.Show(
                            $"❌ {result.Message}\n\n" +
                            $"Your machine code: {_machineId}",
                            "Invalid License",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Error installing license: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
