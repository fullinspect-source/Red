using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InspectionEditor.Services;
using Newtonsoft.Json.Linq;

namespace InspectionEditor
{
    // F-011 inspector signature in the End section (F-007 Footer). Every stroke re-renders the whole 770x105 pad into
    // Signature and stamps DateInspected with this device's clock (owner Q-09); Clear sets Signature null and keeps the
    // date (owner Q-08). Only Signature and DateInspected are written (C1).
    public partial class MainWindow
    {
        private SignatureSurface? _signaturePad;
        private readonly InspectorSignatureStore _signatureStore = new();

        partial void OnFooterBuilt(StackPanel slot)
        {
            slot.Children.Add(new TextBlock
            {
                Text = "Inspector's Signature: *",
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                Margin = new Thickness(0, 4, 0, 4)
            });
            var pad = new SignatureSurface(InspectorSignatureStore.PadWidth, InspectorSignatureStore.PadHeight)
            {
                IsEnabledForInk = !_readOnlyMode // P6: read-only files show the signature with the pad disabled
            };
            var existing = DecodeHeaderSignature();
            if (existing != null)
                pad.AddImage(existing);
            pad.StrokeFinished += (_, _) => StoreSignatureFromPad();
            _signaturePad = pad;
            slot.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new ScrollViewer { Content = pad, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxWidth = 790 }
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var clear = new Button { Content = "Clear", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), IsEnabled = !_readOnlyMode };
            clear.Click += (_, _) => ClearInspectorSignature();
            var useSaved = new Button { Content = "Use Saved Signature", Padding = new Thickness(12, 4, 12, 4), IsEnabled = !_readOnlyMode };
            useSaved.Click += (_, _) => UseSavedSignature();
            buttons.Children.Add(clear);
            buttons.Children.Add(useSaved);
            slot.Children.Add(buttons);
            var marker = new TextBlock
            {
                Text = "Signature required.",
                Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Visibility = HasInspectorSignature() ? Visibility.Collapsed : Visibility.Visible
            };
            _headerMarkerBlocks["Signature"] = marker;
            slot.Children.Add(marker);
        }

        private bool HasInspectorSignature()
        {
            var token = Header("Signature");
            return token != null && token.Type != JTokenType.Null; // INSPECT tests only != null (F-012 I20, Q-36)
        }

        private BitmapSource? DecodeHeaderSignature()
        {
            string? text = HeaderRules.Text(Header("Signature"));
            if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("None", StringComparison.OrdinalIgnoreCase))
                return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(Convert.FromBase64String(text));
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is FormatException || ex is NotSupportedException || ex is IOException)
            {
                return null; // an unreadable stored signature is shown as an empty pad; the bytes stay until re-signed
            }
        }

        // P2: the whole pad as a 770x105 baseline JPEG, and DateInspected = now (this device, local with offset).
        private void StoreSignatureFromPad()
        {
            if (_signaturePad == null || _readOnlyMode)
                return;
            string jpeg;
            try { jpeg = _signaturePad.RenderJpegBase64(); }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Could not render the inspector signature", ex);
                MessageBox.Show(this, "RED could not store the signature. Try again.", "Signature", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            WriteHeader("Signature", new JValue(jpeg), refresh: false);
            WriteHeader("DateInspected", new JValue(HeaderRules.FormatDate(DateTime.Now)));
            AfterSignatureChanged();
        }

        // P4 (owner Q-08): Clear empties the pad and sets Signature null; DateInspected is unchanged.
        private void ClearInspectorSignature()
        {
            if (_readOnlyMode)
                return;
            _signaturePad?.Clear();
            WriteHeader("Signature", JValue.CreateNull());
            AfterSignatureChanged();
        }

        // P3 / E2: the saved image at the pad's top-left at natural size over what is there, then re-rendered.
        private void UseSavedSignature()
        {
            if (_readOnlyMode || _signaturePad == null)
                return;
            if (!_signatureStore.HasSaved)
            {
                if (MessageBox.Show(this, InspectorSignatureStore.NoSavedSignatureQuestion, "Signature", MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                var capture = new SignatureCaptureWindow(_signatureStore) { Owner = this };
                capture.ShowDialog();
                if (!capture.Saved)
                    return;
            }
            BitmapSource? saved;
            try { saved = _signatureStore.LoadImage(); }
            catch (InvalidDataException)
            {
                MessageBox.Show(this, InspectorSignatureStore.UnreadableSavedSignature, "Signature", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (saved == null)
                return;
            _signaturePad.AddImage(saved);
            StoreSignatureFromPad();
        }

        private void AfterSignatureChanged()
        {
            if (_headerMarkerBlocks.TryGetValue("Signature", out var marker))
                marker.Visibility = HasInspectorSignature() ? Visibility.Collapsed : Visibility.Visible;
            RefreshHeaderMarkers();
            RefreshTopDateDisplay();
            UpdateSignButton();
        }

        // Owner tablet test 2026-10-20 ("could not find how to sign"): the Sign button beside the result button shows
        // whether the report is signed and opens the End section at the signature pad. It writes nothing itself.
        private void UpdateSignButton()
        {
            if (ResultButton.Visibility != Visibility.Visible)
            {
                SignButton.Visibility = Visibility.Collapsed;
                return;
            }
            var look = SignButtonLook.For(HasInspectorSignature(), _readOnlyMode); // Services\SubmitAvailability.cs
            SignButton.Content = look.Text;
            SignButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(look.Background));
            SignButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(look.Foreground));
            SignButton.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(look.Border));
            SignButton.BorderThickness = new Thickness(1);
            SignButton.ToolTip = look.ToolTip;
            System.Windows.Automation.AutomationProperties.SetName(SignButton, look.Text);
            SignButton.Visibility = Visibility.Visible;
        }

        private void SignButton_Click(object sender, RoutedEventArgs e)
        {
            if (!HeaderSectionsAvailable || _isLoadingFile)
                return;
            var report = _currentInspection;
            AutoApplyCurrentItem();
            // As the result button: a save that stopped on a changed-on-disk file queues its own choices first.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!HeaderSectionsAvailable || _isLoadingFile || !ReferenceEquals(_currentInspection, report))
                    return;
                JumpToProblem(new CompletionProblem("Sign", "", "Signature", null, false));
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // T5: the Top date shows the new DateInspected at once.
        private void RefreshTopDateDisplay()
        {
            if (_topDateBlock != null)
                _topDateBlock.Text = HeaderRules.DisplayDate(Header("DateInspected")) is { Length: > 0 } shown ? shown : "(blank)";
        }
    }
}
