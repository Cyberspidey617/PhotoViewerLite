// Photo Viewer Lite: a small single-window picture viewer modelled on Windows Photo Viewer.
//
// The decode pipeline mirrors what PhotoViewer.dll and ImagingEngine.dll do with WIC:
//   1. open the file as a read-only stream and create a decoder from that stream;
//   2. pick a frame (frame 0 by default; multi-page TIFF/GIF can be stepped through);
//   3. show the embedded thumbnail first as a fast preview while the full frame decodes;
//   4. apply the EXIF orientation tag;
//   5. convert an embedded colour profile to sRGB before display.
// Rotation is saved when you leave the picture: JPEG/TIFF get a lossless orientation-tag
// update when the file has room for it, PNG/BMP/GIF are rewritten losslessly.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace PhotoViewerLite
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            return app.Run(new ViewerWindow(args.Length > 0 ? args[0] : null));
        }
    }

    static class NativeMethods
    {
        // Explorer's "natural" ordering (img2 before img10), as Photo Viewer uses for folder navigation.
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int StrCmpLogicalW(string left, string right);
    }

    sealed class DecodedImage
    {
        public BitmapSource Bitmap;   // frozen, oriented, colour-converted, Pbgra32
        public int FrameIndex;
        public int FrameCount;
        public string ColorNote;
    }

    delegate void PreviewHandler(BitmapSource preview, int fullWidth, int fullHeight);

    static class Codec
    {
        public static readonly string[] Extensions =
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff",
            ".ico", ".wdp", ".jxr", ".hdp", ".heic", ".heif", ".avif", ".webp", ".jxl", ".dds",
            ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf"
        };

        public const string OpenFilter =
            "Pictures|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.dib;*.gif;*.tif;*.tiff;*.ico;*.wdp;*.jxr;*.hdp;" +
            "*.heic;*.heif;*.avif;*.webp;*.jxl;*.dds;*.dng;*.cr2;*.cr3;*.nef;*.arw;*.orf;*.rw2;*.raf|All files|*.*";

        const BitmapCreateOptions RawOptions =
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;

        public static bool IsSupported(string path)
        {
            return Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());
        }

        public static DecodedImage Decode(string path, int frameIndex, PreviewHandler preview)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
            {
                BitmapDecoder decoder = BitmapDecoder.Create(stream, RawOptions, BitmapCacheOption.None);
                int count = decoder.Frames.Count;
                if (count == 0)
                    throw new FileFormatException("The file contains no pictures.");

                int index = Math.Max(0, Math.Min(frameIndex, count - 1));
                BitmapFrame frame = decoder.Frames[index];
                ushort orientation = ExifOrientation.Read(frame);
                bool swapsAxes = orientation >= 5;
                int fullWidth = swapsAxes ? frame.PixelHeight : frame.PixelWidth;
                int fullHeight = swapsAxes ? frame.PixelWidth : frame.PixelHeight;

                if (preview != null)
                {
                    BitmapSource thumbnail = TryGetThumbnail(frame);
                    if (thumbnail != null)
                        preview(Finish(ExifOrientation.Apply(thumbnail, orientation)), fullWidth, fullHeight);
                }

                var result = new DecodedImage { FrameIndex = index, FrameCount = count };
                ColorContext profile = FirstColorContext(frame);
                if (profile != null)
                {
                    try
                    {
                        var converted = new ColorConvertedBitmap(frame, profile,
                            new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);
                        result.Bitmap = Finish(ExifOrientation.Apply(converted, orientation));
                        result.ColorNote = "colour profile applied";
                        return result;
                    }
                    catch (ArgumentException) { }
                    catch (NotSupportedException) { }
                    catch (COMException) { }
                    catch (FileFormatException) { }  // e.g. WIC rejects the embedded profile (0x800707DB)
                    // Like ImagingEngine, fall back to the raw pixels when a profile can't be used.
                    result.ColorNote = "colour profile ignored";
                }

                result.Bitmap = Finish(ExifOrientation.Apply(frame, orientation));
                return result;
            }
        }

        // Copies the pixels out so the result no longer depends on the stream and can cross threads.
        static BitmapSource Finish(BitmapSource source)
        {
            BitmapSource converted = source.Format == PixelFormats.Pbgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            var copy = new WriteableBitmap(converted);
            copy.Freeze();
            return copy;
        }

        static BitmapSource TryGetThumbnail(BitmapFrame frame)
        {
            try { return frame.Thumbnail; }
            catch (NotSupportedException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (COMException) { return null; }
            catch (FileFormatException) { return null; }
        }

        public static ColorContext FirstColorContext(BitmapFrame frame)
        {
            ReadOnlyCollection<ColorContext> contexts = ColorContexts(frame);
            return contexts != null && contexts.Count > 0 ? contexts[0] : null;
        }

        public static ReadOnlyCollection<ColorContext> ColorContexts(BitmapFrame frame)
        {
            try { return frame.ColorContexts; }
            catch (NotSupportedException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (COMException) { return null; }
        }
    }

    static class ExifOrientation
    {
        const string Query = "System.Photo.Orientation";

        // EXIF orientation as (mirror horizontally first?, then clockwise degrees).
        static readonly bool[] Mirror = { false, false, true, false, true, true, false, true, false };
        static readonly int[] Degrees = { 0, 0, 0, 180, 180, 270, 90, 90, 270 };

        public static ushort Read(BitmapFrame frame)
        {
            var metadata = frame.Metadata as BitmapMetadata;
            if (metadata == null)
                return 1;
            object value = TryGetQuery(metadata, Query);
            if (value is ushort)
            {
                ushort orientation = (ushort)value;
                if (orientation >= 1 && orientation <= 8)
                    return orientation;
            }
            return 1;
        }

        public static object TryGetQuery(BitmapMetadata metadata, string query)
        {
            try { return metadata.GetQuery(query); }
            catch (NotSupportedException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (ArgumentException) { return null; }
            catch (COMException) { return null; }
        }

        public static BitmapSource Apply(BitmapSource source, ushort orientation)
        {
            if (orientation <= 1 || orientation > 8)
                return source;
            var group = new TransformGroup();
            if (Mirror[orientation])
                group.Children.Add(new ScaleTransform(-1, 1));
            if (Degrees[orientation] != 0)
                group.Children.Add(new RotateTransform(Degrees[orientation]));
            return new TransformedBitmap(source, group);
        }

        // The orientation value that shows the stored pixels rotated a further `clockwise` degrees.
        public static ushort Rotate(ushort orientation, int clockwise)
        {
            if (orientation < 1 || orientation > 8)
                orientation = 1;
            bool mirror = Mirror[orientation];
            int degrees = (Degrees[orientation] + clockwise) % 360;
            for (ushort candidate = 1; candidate <= 8; candidate++)
            {
                if (Mirror[candidate] == mirror && Degrees[candidate] == degrees)
                    return candidate;
            }
            return 1;
        }
    }

    static class RotationSaver
    {
        const BitmapCreateOptions RawOptions =
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;

        // Returns a short description of what was written, or null if the user declined.
        public static string Save(string path, int clockwise, Func<string, bool> confirm)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool jpeg = ext == ".jpg" || ext == ".jpeg" || ext == ".jpe" || ext == ".jfif";
            bool tiff = ext == ".tif" || ext == ".tiff";

            if (jpeg || tiff)
            {
                if (TryUpdateOrientationInPlace(path, clockwise))
                    return "Rotation saved (lossless)";
                if (tiff)
                {
                    RewriteWithOrientation(path, clockwise, new TiffBitmapEncoder());
                    return "Rotation saved";
                }
                if (!confirm("This picture can't be rotated without re-saving it, which slightly " +
                             "reduces JPEG quality. Re-save it now?"))
                    return null;
                RewriteWithOrientation(path, clockwise, new JpegBitmapEncoder { QualityLevel = 95 });
                return "Rotation saved (re-encoded at quality 95)";
            }

            if (ext == ".png")
                return RewritePixels(path, clockwise, new PngBitmapEncoder());
            if (ext == ".bmp" || ext == ".dib")
                return RewritePixels(path, clockwise, new BmpBitmapEncoder());
            if (ext == ".gif")
                return RewritePixels(path, clockwise, new GifBitmapEncoder());

            throw new NotSupportedException("Rotation can't be saved for " + ext + " files.");
        }

        static bool TryUpdateOrientationInPlace(string path, int clockwise)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                BitmapDecoder decoder = BitmapDecoder.Create(stream, RawOptions, BitmapCacheOption.None);
                if (decoder.Frames.Count != 1)
                    throw new NotSupportedException("Rotation can't be saved for multi-page pictures.");
                BitmapFrame frame = decoder.Frames[0];
                ushort next = ExifOrientation.Rotate(ExifOrientation.Read(frame), clockwise);
                InPlaceBitmapMetadataWriter writer;
                try { writer = frame.CreateInPlaceBitmapMetadataWriter(); }
                catch (InvalidOperationException) { return false; }
                catch (NotSupportedException) { return false; }
                if (writer == null)
                    return false;
                try { writer.SetQuery("System.Photo.Orientation", next); }
                catch (InvalidOperationException) { return false; }
                catch (ArgumentException) { return false; }
                catch (COMException) { return false; }
                return writer.TrySave();
            }
        }

        static void RewriteWithOrientation(string path, int clockwise, BitmapEncoder encoder)
        {
            ReplaceFile(path, encoder, delegate(BitmapFrame frame)
            {
                ushort next = ExifOrientation.Rotate(ExifOrientation.Read(frame), clockwise);
                var metadata = frame.Metadata as BitmapMetadata;
                metadata = metadata != null ? metadata.Clone() : null;
                if (metadata == null)
                    throw new NotSupportedException("The picture's metadata can't be edited.");
                metadata.SetQuery("System.Photo.Orientation", next);
                if (encoder is JpegBitmapEncoder)
                    TryAddPadding(metadata);  // lets the next rotation be done in place
                return BitmapFrame.Create(frame, null, metadata, Codec.ColorContexts(frame));
            });
        }

        static string RewritePixels(string path, int clockwise, BitmapEncoder encoder)
        {
            ReplaceFile(path, encoder, delegate(BitmapFrame frame)
            {
                var rotated = new TransformedBitmap(frame, new RotateTransform(clockwise));
                return BitmapFrame.Create(rotated, null, null, Codec.ColorContexts(frame));
            });
            return "Rotation saved (lossless)";
        }

        static void TryAddPadding(BitmapMetadata metadata)
        {
            foreach (string query in new[] { "/app1/ifd/PaddingSchema:Padding", "/xmp/PaddingSchema:Padding" })
            {
                try { metadata.SetQuery(query, (uint)4096); }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
                catch (COMException) { }
            }
        }

        // Writes the new file beside the original, then swaps it in so a failure never truncates the picture.
        static void ReplaceFile(string path, BitmapEncoder encoder, Func<BitmapFrame, BitmapFrame> transform)
        {
            string temp = Path.Combine(Path.GetDirectoryName(path),
                                       "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    BitmapDecoder decoder = BitmapDecoder.Create(source, RawOptions, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count != 1)
                        throw new NotSupportedException("Rotation can't be saved for multi-page pictures.");
                    encoder.Frames.Add(transform(decoder.Frames[0]));
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                        encoder.Save(output);
                }
                DateTime created = File.GetCreationTimeUtc(path);
                File.Replace(temp, path, null);
                File.SetCreationTimeUtc(path, created);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }
    }

    // A few recently used decodes, so stepping back and forth or prefetching the next picture is instant.
    sealed class ImageCache
    {
        readonly int capacity;
        readonly LinkedList<KeyValuePair<string, Task<DecodedImage>>> entries =
            new LinkedList<KeyValuePair<string, Task<DecodedImage>>>();

        public ImageCache(int capacity) { this.capacity = capacity; }

        static string Key(string path, int frame) { return path.ToUpperInvariant() + "|" + frame; }

        public Task<DecodedImage> Get(string path, int frame, PreviewHandler preview)
        {
            string key = Key(path, frame);
            for (var node = entries.First; node != null; node = node.Next)
            {
                if (node.Value.Key == key)
                {
                    entries.Remove(node);
                    entries.AddFirst(node);
                    return node.Value.Value;
                }
            }
            Task<DecodedImage> task = Task.Run(() => Codec.Decode(path, frame, preview));
            entries.AddFirst(new KeyValuePair<string, Task<DecodedImage>>(key, task));
            while (entries.Count > capacity)
                entries.RemoveLast();
            return task;
        }

        public void Invalidate(string path)
        {
            string prefix = path.ToUpperInvariant() + "|";
            var node = entries.First;
            while (node != null)
            {
                var next = node.Next;
                if (node.Value.Key.StartsWith(prefix, StringComparison.Ordinal))
                    entries.Remove(node);
                node = next;
            }
        }
    }

    sealed class ViewerWindow : Window
    {
        const string AppName = "Photo Viewer Lite";
        const double MinZoom = 0.02;
        const double MaxZoom = 32.0;
        const double ZoomStep = 1.25;

        static readonly Brush ViewBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
        static readonly Brush BarBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)));
        static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)));
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        readonly Canvas canvas = new Canvas();
        readonly Image image = new Image();
        readonly TextBlock message = new TextBlock();
        readonly TextBlock status = new TextBlock();
        readonly Border toolbar = new Border();
        readonly DispatcherTimer slideTimer = new DispatcherTimer();
        readonly DispatcherTimer noticeTimer = new DispatcherTimer();
        readonly ImageCache cache = new ImageCache(3);
        readonly string initialPath;

        List<string> files = new List<string>();
        int index = -1;
        DecodedImage current;
        BitmapSource shown;
        int layoutWidth, layoutHeight;   // picture size in pixels (the preview is stretched to this)
        int pendingRotation;             // clockwise degrees not yet written to the file
        int loadGeneration;
        string notice;

        double zoom = 1;
        bool fitMode = true;
        double offsetX, offsetY;
        bool panning;
        Point panStart;
        double panOriginX, panOriginY;

        bool slideshow;
        WindowState savedState;
        WindowStyle savedStyle;
        ResizeMode savedResize;

        public ViewerWindow(string initialPath)
        {
            this.initialPath = initialPath;
            Title = AppName;
            Width = 1100;
            Height = 760;
            MinWidth = 480;
            MinHeight = 320;
            Background = ViewBrush;
            AllowDrop = true;

            image.Stretch = Stretch.Fill;
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            canvas.Children.Add(image);
            canvas.ClipToBounds = true;
            canvas.Background = ViewBrush;

            message.Foreground = TextBrush;
            message.FontSize = 15;
            message.TextWrapping = TextWrapping.Wrap;
            message.TextAlignment = TextAlignment.Center;
            message.HorizontalAlignment = HorizontalAlignment.Center;
            message.VerticalAlignment = VerticalAlignment.Center;
            message.MaxWidth = 560;
            message.IsHitTestVisible = false;

            var stage = new Grid();
            stage.Children.Add(canvas);
            stage.Children.Add(message);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            buttons.Children.Add(ToolButton("", "Open (Ctrl+O)", OpenDialog, false));
            buttons.Children.Add(Separator());
            buttons.Children.Add(ToolButton("", "Zoom out (-)", delegate { ZoomBy(1 / ZoomStep); }, false));
            buttons.Children.Add(ToolButton("", "Fit to window (Ctrl+0)", delegate { Fit(); }, false));
            buttons.Children.Add(TextButton("1:1", "Actual size (Ctrl+1)", delegate { ActualSize(); }));
            buttons.Children.Add(ToolButton("", "Zoom in (+)", delegate { ZoomBy(ZoomStep); }, false));
            buttons.Children.Add(Separator());
            buttons.Children.Add(ToolButton("", "Previous (Left)", delegate { Navigate(-1); }, false));
            buttons.Children.Add(ToolButton("", "Slide show (F11)", delegate { ToggleSlideshow(); }, false));
            buttons.Children.Add(ToolButton("", "Next (Right)", delegate { Navigate(1); }, false));
            buttons.Children.Add(Separator());
            buttons.Children.Add(ToolButton("", "Rotate counterclockwise (Ctrl+,)", delegate { Rotate(-90); }, true));
            buttons.Children.Add(ToolButton("", "Rotate clockwise (Ctrl+.)", delegate { Rotate(90); }, false));
            buttons.Children.Add(Separator());
            buttons.Children.Add(ToolButton("", "Copy (Ctrl+C)", delegate { CopyCurrent(); }, false));
            buttons.Children.Add(ToolButton("", "Print (Ctrl+P)", delegate { PrintCurrent(); }, false));
            buttons.Children.Add(ToolButton("", "Delete (Del)", delegate { DeleteCurrent(); }, false));

            status.Foreground = TextBrush;
            status.Opacity = 0.75;
            status.VerticalAlignment = VerticalAlignment.Center;
            status.Margin = new Thickness(12, 0, 12, 0);
            status.TextTrimming = TextTrimming.CharacterEllipsis;
            status.MaxWidth = 330;

            // Overlay rather than dock, so the buttons stay centred whatever the status text says.
            status.HorizontalAlignment = HorizontalAlignment.Left;
            var bar = new Grid();
            bar.Children.Add(status);
            bar.Children.Add(buttons);
            toolbar.Background = BarBrush;
            toolbar.Padding = new Thickness(0, 4, 0, 4);
            toolbar.Child = bar;

            var root = new DockPanel();
            DockPanel.SetDock(toolbar, Dock.Bottom);
            root.Children.Add(toolbar);
            root.Children.Add(stage);
            Content = root;

            slideTimer.Interval = TimeSpan.FromSeconds(3.5);
            slideTimer.Tick += delegate { Navigate(1); };
            noticeTimer.Interval = TimeSpan.FromSeconds(3);
            noticeTimer.Tick += delegate { noticeTimer.Stop(); notice = null; UpdateStatus(); };

            canvas.SizeChanged += delegate { if (fitMode) Fit(); else { ClampOffset(); Apply(); } };
            canvas.MouseWheel += OnWheel;
            canvas.MouseLeftButtonDown += OnMouseDown;
            canvas.MouseMove += OnMouseMove;
            canvas.MouseLeftButtonUp += OnMouseUp;
            canvas.LostMouseCapture += delegate { EndPan(); };
            PreviewKeyDown += OnKey;
            Drop += OnDrop;
            Loaded += delegate
            {
                if (!string.IsNullOrEmpty(this.initialPath))
                    OpenPath(this.initialPath);
                else
                    ShowMessage("Open a picture with Ctrl+O, or drop one here.");
            };
            Closing += OnClosing;
        }

        static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

        Button ToolButton(string glyph, string tip, RoutedEventHandler click, bool mirror)
        {
            var text = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 16 };
            if (mirror)
            {
                text.RenderTransformOrigin = new Point(0.5, 0.5);
                text.RenderTransform = new ScaleTransform(-1, 1);
            }
            return StyledButton(text, tip, click);
        }

        Button TextButton(string label, string tip, RoutedEventHandler click)
        {
            return StyledButton(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold }, tip, click);
        }

        Button StyledButton(object content, string tip, RoutedEventHandler click)
        {
            var button = new Button
            {
                Content = content,
                ToolTip = tip,
                Width = 40,
                Height = 34,
                Margin = new Thickness(2, 0, 2, 0),
                Foreground = TextBrush,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Focusable = false
            };
            button.Click += click;
            return button;
        }

        static UIElement Separator()
        {
            return new Border { Width = 1, Margin = new Thickness(8, 6, 8, 6), Background = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)) };
        }

        // ---- Opening and navigation -------------------------------------------------------------

        void OpenDialog(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Codec.OpenFilter, Title = "Open a picture" };
            if (index >= 0)
                dialog.InitialDirectory = Path.GetDirectoryName(files[index]);
            if (dialog.ShowDialog(this) == true)
                OpenPath(dialog.FileName);
        }

        void OpenPath(string path)
        {
            CommitRotation();
            string full;
            try { full = Path.GetFullPath(path); }
            catch (ArgumentException) { ShowMessage("That isn't a valid file path."); return; }
            catch (NotSupportedException) { ShowMessage("That isn't a valid file path."); return; }

            var list = new List<string>();
            try
            {
                list.AddRange(Directory.EnumerateFiles(Path.GetDirectoryName(full)).Where(Codec.IsSupported));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            list.Sort(NativeMethods.StrCmpLogicalW);

            int found = list.FindIndex(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase));
            if (found < 0)
            {
                list.Insert(0, full);  // an explicitly opened file is shown even if its extension isn't listed
                found = 0;
            }
            files = list;
            index = found;
            ShowCurrent(0);
        }

        void Navigate(int delta)
        {
            if (files.Count == 0)
                return;
            CommitRotation();
            index = ((index + delta) % files.Count + files.Count) % files.Count;
            if (slideshow)
            {
                slideTimer.Stop();
                slideTimer.Start();
            }
            ShowCurrent(0);
        }

        void JumpTo(int target)
        {
            if (files.Count == 0 || target == index)
                return;
            CommitRotation();
            index = target;
            ShowCurrent(0);
        }

        void StepFrame(int delta)
        {
            if (current == null || current.FrameCount <= 1)
                return;
            pendingRotation = 0;
            ShowCurrent(((current.FrameIndex + delta) % current.FrameCount + current.FrameCount) % current.FrameCount);
        }

        async void ShowCurrent(int frame)
        {
            if (index < 0 || index >= files.Count)
                return;
            string path = files[index];
            int generation = ++loadGeneration;
            pendingRotation = 0;
            Title = Path.GetFileName(path) + " - " + AppName;

            PreviewHandler preview = delegate(BitmapSource thumbnail, int width, int height)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (generation == loadGeneration)
                        ShowBitmap(thumbnail, width, height, true);
                }));
            };

            Task<DecodedImage> task = cache.Get(path, frame, preview);
            if (!task.IsCompleted && current != null)
                ShowMessage(null);

            DecodedImage decoded;
            try
            {
                decoded = await task;
            }
            catch (Exception error)
            {
                if (generation != loadGeneration)
                    return;
                cache.Invalidate(path);
                current = null;
                ClearPicture();
                ShowMessage(Path.GetFileName(path) + "\n\n" + Describe(error));
                return;
            }
            if (generation != loadGeneration)
                return;

            current = decoded;
            ShowMessage(null);
            ShowBitmap(decoded.Bitmap, decoded.Bitmap.PixelWidth, decoded.Bitmap.PixelHeight, false);
            Prefetch();
        }

        void Prefetch()
        {
            if (files.Count < 2)
                return;
            cache.Get(files[(index + 1) % files.Count], 0, null);
        }

        static string Describe(Exception error)
        {
            var aggregate = error as AggregateException;
            if (aggregate != null && aggregate.InnerExceptions.Count == 1)
                error = aggregate.InnerExceptions[0];
            if (error is FileNotFoundException || error is DirectoryNotFoundException)
                return "This picture can't be found. It may have been moved, renamed or deleted.";
            if (error is UnauthorizedAccessException)
                return "You don't have permission to open this picture.";
            if (error is NotSupportedException || error is FileFormatException)
                return "This picture can't be opened: the file is damaged, or this format isn't supported " +
                       "by the codecs installed on this PC.";
            if (error is OutOfMemoryException || error is InsufficientMemoryException)
                return "There isn't enough memory to open this picture.";
            return "This picture can't be opened.\n" + error.Message;
        }

        // ---- Display, zoom and pan ---------------------------------------------------------------

        void ShowBitmap(BitmapSource bitmap, int width, int height, bool isPreview)
        {
            BitmapSource source = bitmap;
            if (!isPreview && pendingRotation != 0)
            {
                var rotated = new TransformedBitmap(bitmap, new RotateTransform(pendingRotation));
                rotated.Freeze();
                source = rotated;
                if (pendingRotation % 180 != 0)
                {
                    int swap = width;
                    width = height;
                    height = swap;
                }
            }
            bool sameSize = shown != null && width == layoutWidth && height == layoutHeight;
            shown = source;
            image.Source = source;
            layoutWidth = width;
            layoutHeight = height;
            // Keep the view when the full picture replaces its own preview; otherwise start fitted.
            if (sameSize && !isPreview && !fitMode)
            {
                ClampOffset();
                Apply();
            }
            else
            {
                Fit();
            }
        }

        void ClearPicture()
        {
            shown = null;
            image.Source = null;
            layoutWidth = layoutHeight = 0;
            UpdateStatus();
        }

        double PictureWidthDip()
        {
            return layoutWidth / VisualTreeHelper.GetDpi(this).DpiScaleX;
        }

        double PictureHeightDip()
        {
            return layoutHeight / VisualTreeHelper.GetDpi(this).DpiScaleY;
        }

        void Fit()
        {
            fitMode = true;
            if (shown == null || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0)
            {
                UpdateStatus();
                return;
            }
            // Like Photo Viewer, small pictures are shown at actual size rather than enlarged.
            zoom = Math.Min(1.0, Math.Min(canvas.ActualWidth / PictureWidthDip(), canvas.ActualHeight / PictureHeightDip()));
            ClampOffset();
            Apply();
        }

        void ActualSize()
        {
            if (shown == null)
                return;
            ZoomTo(1.0, new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2));
        }

        void ZoomBy(double factor)
        {
            ZoomTo(zoom * factor, new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2));
        }

        void ZoomTo(double target, Point anchor)
        {
            if (shown == null)
                return;
            target = Math.Max(MinZoom, Math.Min(MaxZoom, target));
            double ratio = target / zoom;
            offsetX = anchor.X - (anchor.X - offsetX) * ratio;
            offsetY = anchor.Y - (anchor.Y - offsetY) * ratio;
            zoom = target;
            fitMode = false;
            ClampOffset();
            Apply();
        }

        void ClampOffset()
        {
            if (shown == null)
                return;
            offsetX = ClampAxis(offsetX, PictureWidthDip() * zoom, canvas.ActualWidth);
            offsetY = ClampAxis(offsetY, PictureHeightDip() * zoom, canvas.ActualHeight);
        }

        static double ClampAxis(double offset, double size, double viewport)
        {
            if (size <= viewport)
                return (viewport - size) / 2;
            return Math.Min(0, Math.Max(viewport - size, offset));
        }

        void Apply()
        {
            if (shown != null)
            {
                image.Width = PictureWidthDip() * zoom;
                image.Height = PictureHeightDip() * zoom;
                Canvas.SetLeft(image, Math.Round(offsetX));
                Canvas.SetTop(image, Math.Round(offsetY));
                bool canPan = image.Width > canvas.ActualWidth + 0.5 || image.Height > canvas.ActualHeight + 0.5;
                canvas.Cursor = slideshow ? Cursors.None : (canPan ? Cursors.SizeAll : null);
            }
            UpdateStatus();
        }

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (shown == null || slideshow)
                return;
            ZoomTo(zoom * (e.Delta > 0 ? ZoomStep : 1 / ZoomStep), e.GetPosition(canvas));
            e.Handled = true;
        }

        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (slideshow)
            {
                StopSlideshow();
                return;
            }
            if (shown == null)
                return;
            if (e.ClickCount == 2)
            {
                if (fitMode)
                    ZoomTo(1.0, e.GetPosition(canvas));
                else
                    Fit();
                return;
            }
            panning = true;
            panStart = e.GetPosition(canvas);
            panOriginX = offsetX;
            panOriginY = offsetY;
            canvas.CaptureMouse();
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!panning)
                return;
            Point now = e.GetPosition(canvas);
            offsetX = panOriginX + (now.X - panStart.X);
            offsetY = panOriginY + (now.Y - panStart.Y);
            ClampOffset();
            Apply();
        }

        void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            EndPan();
        }

        void EndPan()
        {
            if (!panning)
                return;
            panning = false;
            canvas.ReleaseMouseCapture();
        }

        // ---- Commands ------------------------------------------------------------------------------

        void Rotate(int degrees)
        {
            if (current == null)
                return;
            if (current.FrameCount > 1)
            {
                Notify("Multi-page pictures can't be rotated.");
                return;
            }
            pendingRotation = ((pendingRotation + degrees) % 360 + 360) % 360;
            ShowBitmap(current.Bitmap, current.Bitmap.PixelWidth, current.Bitmap.PixelHeight, false);
        }

        // Photo Viewer saves a rotation when you move on; this does the same.
        void CommitRotation()
        {
            if (pendingRotation == 0 || current == null || index < 0)
                return;
            string path = files[index];
            int degrees = pendingRotation;
            pendingRotation = 0;
            try
            {
                string result = RotationSaver.Save(path, degrees, delegate(string question)
                {
                    return MessageBox.Show(this, question, AppName, MessageBoxButton.YesNo,
                                           MessageBoxImage.Question) == MessageBoxResult.Yes;
                });
                cache.Invalidate(path);
                if (result != null)
                    Notify(result);
            }
            catch (Exception error)
            {
                if (error is IOException || error is UnauthorizedAccessException || error is NotSupportedException ||
                    error is InvalidOperationException || error is ArgumentException || error is COMException ||
                    error is FileFormatException)
                {
                    MessageBox.Show(this, "The rotation of " + Path.GetFileName(path) + " couldn't be saved.\n\n" +
                                    error.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                throw;
            }
        }

        void DeleteCurrent()
        {
            if (index < 0 || files.Count == 0)
                return;
            string path = files[index];
            try
            {
                FileSystem.DeleteFile(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin,
                                      UICancelOption.ThrowException);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException error)
            {
                MessageBox.Show(this, error.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            catch (UnauthorizedAccessException error)
            {
                MessageBox.Show(this, error.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            pendingRotation = 0;
            cache.Invalidate(path);
            files.RemoveAt(index);
            current = null;
            if (files.Count == 0)
            {
                index = -1;
                ClearPicture();
                Title = AppName;
                ShowMessage("There are no more pictures in this folder.");
                return;
            }
            if (index >= files.Count)
                index = 0;
            ShowCurrent(0);
        }

        void CopyCurrent()
        {
            if (shown == null || index < 0)
                return;
            var data = new DataObject();
            data.SetImage(shown);
            var list = new StringCollection();
            list.Add(files[index]);
            data.SetFileDropList(list);
            try
            {
                Clipboard.SetDataObject(data, true);
                Notify("Copied");
            }
            catch (COMException)
            {
                Notify("The clipboard is busy; try again.");
            }
        }

        void PrintCurrent()
        {
            if (shown == null || index < 0)
                return;
            var dialog = new PrintDialog();
            if (dialog.ShowDialog() != true)
                return;
            double width = dialog.PrintableAreaWidth;
            double height = dialog.PrintableAreaHeight;
            const double margin = 48;  // half an inch
            var picture = new Image
            {
                Source = shown,
                Stretch = Stretch.Uniform,
                Width = Math.Max(1, width - 2 * margin),
                Height = Math.Max(1, height - 2 * margin),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var page = new Grid { Width = width, Height = height, Background = Brushes.White };
            page.Children.Add(picture);
            page.Measure(new Size(width, height));
            page.Arrange(new Rect(0, 0, width, height));
            page.UpdateLayout();
            dialog.PrintVisual(page, Path.GetFileName(files[index]));
        }

        void ToggleSlideshow()
        {
            if (slideshow)
                StopSlideshow();
            else
                StartSlideshow();
        }

        void StartSlideshow()
        {
            if (files.Count == 0)
                return;
            CommitRotation();
            slideshow = true;
            savedState = WindowState;
            savedStyle = WindowStyle;
            savedResize = ResizeMode;
            toolbar.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;   // re-maximize so the borderless window covers the taskbar
            WindowState = WindowState.Maximized;
            Topmost = true;
            canvas.Background = Brushes.Black;
            canvas.Cursor = Cursors.None;
            Fit();
            slideTimer.Start();
        }

        void StopSlideshow()
        {
            if (!slideshow)
                return;
            slideshow = false;
            slideTimer.Stop();
            Topmost = false;
            WindowStyle = savedStyle;
            ResizeMode = savedResize;
            WindowState = savedState;
            toolbar.Visibility = Visibility.Visible;
            canvas.Background = ViewBrush;
            canvas.Cursor = null;
            Fit();
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;
            switch (key)
            {
                case Key.Left: Navigate(-1); break;
                case Key.Right: Navigate(1); break;
                case Key.Home: JumpTo(0); break;
                case Key.End: JumpTo(files.Count - 1); break;
                case Key.PageUp: StepFrame(-1); break;
                case Key.PageDown: StepFrame(1); break;
                case Key.Escape: StopSlideshow(); break;
                case Key.F11:
                case Key.F5: ToggleSlideshow(); break;
                case Key.Delete: DeleteCurrent(); break;
                case Key.Add:
                case Key.OemPlus: ZoomBy(ZoomStep); break;
                case Key.Subtract:
                case Key.OemMinus: ZoomBy(1 / ZoomStep); break;
                case Key.D0:
                case Key.NumPad0: if (ctrl) Fit(); else e.Handled = false; break;
                case Key.D1:
                case Key.NumPad1: if (ctrl) ActualSize(); else e.Handled = false; break;
                case Key.OemPeriod: if (ctrl) Rotate(90); else e.Handled = false; break;
                case Key.OemComma: if (ctrl) Rotate(-90); else e.Handled = false; break;
                case Key.O: if (ctrl) OpenDialog(null, null); else e.Handled = false; break;
                case Key.C: if (ctrl) CopyCurrent(); else e.Handled = false; break;
                case Key.P: if (ctrl) PrintCurrent(); else e.Handled = false; break;
                default: e.Handled = false; break;
            }
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            var dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (dropped != null && dropped.Length > 0)
                OpenPath(dropped[0]);
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            CommitRotation();
        }

        // ---- Status --------------------------------------------------------------------------------

        void ShowMessage(string text)
        {
            message.Text = text ?? string.Empty;
            message.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            UpdateStatus();
        }

        void Notify(string text)
        {
            notice = text;
            noticeTimer.Stop();
            noticeTimer.Start();
            UpdateStatus();
        }

        void UpdateStatus()
        {
            var parts = new List<string>();
            if (index >= 0 && files.Count > 0)
                parts.Add((index + 1) + " / " + files.Count);
            if (shown != null)
            {
                parts.Add(layoutWidth + " × " + layoutHeight);
                parts.Add(Math.Round(zoom * 100) + "%");
            }
            if (current != null && current.FrameCount > 1)
                parts.Add("page " + (current.FrameIndex + 1) + " of " + current.FrameCount);
            if (pendingRotation != 0)
                parts.Add("rotated, saves when you move on");
            if (notice != null)
                parts.Add(notice);
            status.Text = string.Join("   ·   ", parts);
            if (current != null && current.ColorNote != null)
                status.ToolTip = current.ColorNote;
            else
                status.ToolTip = null;
        }
    }
}
