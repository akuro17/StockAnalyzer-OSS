using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace StockAnalyzer.Tests.Visual
{
    public static class VisualTestHelper
    {
        public static void InitializeTestEnvironment()
        {
            var culture = new CultureInfo("en-US");
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            EnsureWpfLanguageOverride();
        }

        private static void EnsureWpfLanguageOverride()
        {
            try
            {
                FrameworkElement.LanguageProperty.OverrideMetadata(
                    typeof(FrameworkElement),
                    new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("en-US")));
            }
            catch (ArgumentException) 
            {
            }
        }

        public static void EnsureFontAvailability(string fontName, bool strictMode = true)
        {
            if (!Fonts.SystemFontFamilies.Any(f => f.Source.Equals(fontName, StringComparison.OrdinalIgnoreCase)))
            {
                if (strictMode)
                {
                    throw new InvalidOperationException($"Required font '{fontName}' is not installed.");
                }
            }
        }

        public static void ApplyDeterministicRenderingOptions(DependencyObject root)
        {
            RenderOptions.SetEdgeMode(root, EdgeMode.Aliased);
            RenderOptions.SetBitmapScalingMode(root, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetClearTypeHint(root, ClearTypeHint.Enabled);
            
            TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(root, TextRenderingMode.Aliased);
            TextOptions.SetTextHintingMode(root, TextHintingMode.Fixed);
        }

        public static void ForceWpfLayout(UIElement element)
        {
            var fe = element as FrameworkElement;
            double width = (fe != null && !double.IsNaN(fe.Width)) ? fe.Width : 800;
            double height = (fe != null && !double.IsNaN(fe.Height)) ? fe.Height : 600;

            var size = new Size(width, height);
            element.Measure(size);
            element.Arrange(new Rect(new System.Windows.Point(0, 0), size));
            
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
            
            element.UpdateLayout();
        }

        public static byte[] CaptureAndValidate(FrameworkElement element)
        {
            if (element.ActualWidth == 0 || element.ActualHeight == 0)
                throw new InvalidOperationException("Element has zero size. Layout failed.");

            const double dpi = 96.0;
            var renderBitmap = new RenderTargetBitmap(
                (int)element.ActualWidth, (int)element.ActualHeight,
                dpi, dpi, PixelFormats.Pbgra32);

            renderBitmap.Render(element);

            ValidateContentDensity(renderBitmap, (int)element.ActualWidth, (int)element.ActualHeight);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        private static void ValidateContentDensity(RenderTargetBitmap bitmap, int width, int height)
        {
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            bitmap.CopyPixels(pixels, stride, 0);

            long nonBackgroundPixels = 0;
            
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];

                if (a != 0 && !(r == 255 && g == 255 && b == 255))
                {
                    nonBackgroundPixels++;
                }
            }
            
            double density = (double)nonBackgroundPixels / (width * height);
            double minDensity = Math.Max(0.0001, 100.0 / (width * height)); 
            long minAbsolutePixels = 50;

            if (density < minDensity && nonBackgroundPixels < minAbsolutePixels)
            {
                throw new InvalidOperationException($"Rendered content validation failed. Density: {density:P6}, Pixels: {nonBackgroundPixels}.");
            }
        }
    }
}
