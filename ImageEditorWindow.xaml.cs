using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;

namespace InspectionEditor
{
    public partial class ImageEditorWindow : Window
    {
        // Original image data (for undo)
        private byte[] _originalImageBytes;
        private byte[] _currentImageBytes;
        
        // Adjustment values
        private float _gamma = 1f;       // 0.5 (brighter) to 2.0 (darker), 1.0 = no change
        private float _sharpness = 0f;   // 0 to 4 range
        private float _rotation = 0f;    // degrees, -15 to +15
        
        // Gesture tracking
        private System.Windows.Point? _gestureStartPoint;
        private float _gestureStartGamma;
        private float _gestureStartSharpness;
        private bool _isGesturing = false;
        
        // Result
        public bool ImageWasEdited { get; private set; } = false;
        public byte[] EditedImageBytes { get; private set; }
        
        public ImageEditorWindow(byte[] imageBytes)
        {
            InitializeComponent();
            
            _originalImageBytes = imageBytes;
            _currentImageBytes = imageBytes;
            EditedImageBytes = imageBytes;
            
            LoadImageToDisplay(imageBytes);
            UpdateAdjustmentText();
        }
        
        private void LoadImageToDisplay(byte[] imageBytes)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(imageBytes);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            
            PhotoImage.Source = bitmap;
        }
        
        private void UpdateAdjustmentText()
        {
            // Gamma: 1.0 = neutral, <1 = brighter, >1 = darker
            // Display as "lift" percentage (how much shadows are lifted)
            int liftPercent = (int)((1f - _gamma) * 100);
            int sharpnessPercent = (int)(_sharpness * 50);
            
            BrightnessText.Text = $"Lift: {(liftPercent >= 0 ? "+" : "")}{liftPercent}%";
            SharpnessText.Text = $"Sharpness: +{sharpnessPercent}%";
            
            BrightnessText.Foreground = _gamma != 1f
                ? new SolidColorBrush(Colors.Gold)
                : new SolidColorBrush(Colors.Gray);
            SharpnessText.Foreground = _sharpness != 0
                ? new SolidColorBrush(Colors.DeepSkyBlue)
                : new SolidColorBrush(Colors.Gray);

            RotationText.Text = _rotation == 0f ? "Rotate: 0°" : $"Rotate: {_rotation:+0.#;-0.#}°";
            RotationText.Foreground = _rotation != 0f
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(144, 238, 144))
                : new SolidColorBrush(Colors.Gray);
        }
        
        private void ApplyAdjustmentsPreview()
        {
            if (_gamma == 1f && _sharpness == 0 && _rotation == 0f)
            {
                LoadImageToDisplay(_originalImageBytes);
                return;
            }

            try
            {
                using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(_originalImageBytes);

                image.Mutate(ctx =>
                {
                    if (_rotation != 0f)
                        ctx.Rotate(_rotation);

                    // Brightness with smaller range + contrast to preserve some depth
                    if (_gamma != 1f)
                    {
                        float lift = (1f - _gamma) * 1.0f;
                        ctx.Brightness(1f + lift);
                        if (lift > 0)
                            ctx.Contrast(1f + (lift * 0.2f));
                    }

                    if (_sharpness > 0)
                        ctx.GaussianSharpen(_sharpness);
                });
                
                using var ms = new MemoryStream();
                image.SaveAsJpeg(ms);
                _currentImageBytes = ms.ToArray();
                
                LoadImageToDisplay(_currentImageBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Image processing error: {ex.Message}");
            }
        }
        
        #region Gesture Handling
        
        private void ImageContainer_TouchDown(object sender, TouchEventArgs e)
        {
            if (PenToggle.IsChecked == true)
                return; // Let InkCanvas handle pen mode
                
            _gestureStartPoint = e.GetTouchPoint(ImageContainer).Position;
            _gestureStartGamma = _gamma;
            _gestureStartSharpness = _sharpness;
            _isGesturing = true;
            e.Handled = true;
        }
        
        private void ImageContainer_TouchMove(object sender, TouchEventArgs e)
        {
            if (!_isGesturing || !_gestureStartPoint.HasValue)
                return;
                
            var currentPoint = e.GetTouchPoint(ImageContainer).Position;
            ProcessGesture(_gestureStartPoint.Value, currentPoint);
            e.Handled = true;
        }
        
        private void ImageContainer_TouchUp(object sender, TouchEventArgs e)
        {
            _isGesturing = false;
            _gestureStartPoint = null;
        }
        
        // Mouse fallback for testing
        private void ImageContainer_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (PenToggle.IsChecked == true || e.LeftButton != MouseButtonState.Pressed)
                return;
                
            _gestureStartPoint = e.GetPosition(ImageContainer);
            _gestureStartGamma = _gamma;
            _gestureStartSharpness = _sharpness;
            _isGesturing = true;
            ImageContainer.CaptureMouse();
        }
        
        private void ImageContainer_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isGesturing || !_gestureStartPoint.HasValue)
                return;
                
            var currentPoint = e.GetPosition(ImageContainer);
            ProcessGesture(_gestureStartPoint.Value, currentPoint);
        }
        
        private void ImageContainer_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isGesturing = false;
            _gestureStartPoint = null;
            ImageContainer.ReleaseMouseCapture();
        }
        
        private void ProcessGesture(System.Windows.Point start, System.Windows.Point current)
        {
            // Vertical movement = gamma/lift (up = brighter/lower gamma)
            double deltaY = start.Y - current.Y;
            // Horizontal movement = sharpness (right = sharper)
            double deltaX = current.X - start.X;
            
            // Gamma: swiping up 200px goes from 1.0 to 0.5 (brighter)
            // Gamma range: 0.4 (very bright) to 1.5 (darker)
            float gammaChange = (float)(deltaY / 400.0);  // Slower adjustment
            float sharpnessChange = (float)(deltaX / 100.0);
            
            _gamma = Math.Clamp(_gestureStartGamma - gammaChange, 0.4f, 1.5f);
            _sharpness = Math.Clamp(_gestureStartSharpness + sharpnessChange, 0f, 4f);
            
            UpdateAdjustmentText();
            ApplyAdjustmentsPreview();
        }
        
        #endregion
        
        #region Pen Mode
        
        private void PenToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (PenToggle.IsChecked == true)
            {
                MarkupCanvas.EditingMode = InkCanvasEditingMode.Ink;
                PenToggle.Background = new SolidColorBrush(Colors.Red);
            }
            else
            {
                MarkupCanvas.EditingMode = InkCanvasEditingMode.None;
                PenToggle.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(85, 85, 85));
            }
        }
        
        #endregion
        
        #region Buttons
        
        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            // Reset everything to original
            _gamma = 1f;
            _sharpness = 0f;
            _rotation = 0f;
            RotationSlider.Value = 0;
            _currentImageBytes = _originalImageBytes;
            MarkupCanvas.Strokes.Clear();

            LoadImageToDisplay(_originalImageBytes);
            UpdateAdjustmentText();
        }
        
        private void RotationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _rotation = (float)RotationSlider.Value;
            UpdateAdjustmentText();
            ApplyAdjustmentsPreview();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            ImageWasEdited = false;
            DialogResult = false;
            Close();
        }
        
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Check if any edits were made
                bool hasAdjustments = _gamma != 1f || _sharpness != 0 || _rotation != 0f;
                bool hasMarkup = MarkupCanvas.Strokes.Count > 0;
                
                if (!hasAdjustments && !hasMarkup)
                {
                    // No changes
                    ImageWasEdited = false;
                    DialogResult = true;
                    Close();
                    return;
                }
                
                // Flatten markup onto image if needed
                if (hasMarkup)
                {
                    EditedImageBytes = FlattenWithMarkup();
                }
                else
                {
                    EditedImageBytes = _currentImageBytes;
                }
                
                ImageWasEdited = true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving image: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private byte[] FlattenWithMarkup()
        {
            // Render the image + InkCanvas to a single bitmap
            var container = ImageContainer;
            
            // Get the actual rendered size
            int width = (int)container.ActualWidth;
            int height = (int)container.ActualHeight;
            
            if (width == 0 || height == 0)
            {
                width = 1920;
                height = 1080;
            }
            
            // Create render target
            var renderTarget = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            
            // Render just the image and canvas
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                // Draw the image
                var imageBrush = new ImageBrush(PhotoImage.Source);
                context.DrawRectangle(imageBrush, null, new Rect(0, 0, width, height));
                
                // Draw the ink strokes
                foreach (var stroke in MarkupCanvas.Strokes)
                {
                    stroke.Draw(context);
                }
            }
            renderTarget.Render(visual);
            
            // Encode to JPEG
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(renderTarget));
            
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        
        #endregion
        
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CancelButton_Click(sender, e);
            }
        }
    }
}
