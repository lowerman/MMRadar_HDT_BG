using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MMRadar.Engine;
using MMRadar.UI;
using MMRadar.Wallii;

namespace MMRadar.Harness
{
    /// <summary>
    /// Standalone visual test bed for the plugin UI — no HDT or Hearthstone required.
    ///
    ///   Harness.exe                              sample lobby, interactive
    ///   Harness.exe --live name1,name2,...       real wallii data for the given names
    ///   Harness.exe --region EU --mode 0         region/mode for --live
    ///   Harness.exe --shot out.png               render, save a screenshot, exit
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string shotPath = null;
            string liveNames = null;
            var top = false;
            var collapsed = false;
            var region = "EU";
            var mode = "0";
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--shot": shotPath = args[++i]; break;
                    case "--live": liveNames = args[++i]; break;
                    case "--top": top = true; break;
                    case "--collapsed": collapsed = true; break;
                    case "--theme": MMRadar.UI.ThemeManager.Apply(args[++i]); break;
                    case "--region": region = args[++i]; break;
                    case "--mode": mode = args[++i]; break;
                }
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };

            var canvas = new Canvas();
            var window = new Window
            {
                Title = "MMRadar Harness",
                Width = 760,
                Height = 680,
                // GitHub dark-theme page color, so README screenshots blend in.
                Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x17)),
                Content = canvas,
            };

            var panel = new LobbyPanel();
            var popup = new PlayerDetailsPopup();
            Canvas.SetLeft(panel, 24);
            Canvas.SetTop(panel, 24);
            Canvas.SetLeft(popup, 396);
            Canvas.SetTop(popup, 24);
            canvas.Children.Add(panel);
            canvas.Children.Add(popup);

            WalliiService live = null;
            if (liveNames != null || top)
                live = new WalliiService(new WalliiApi());

            panel.PlayerClicked += async summary =>
            {
                popup.ShowLoading(summary);
                try
                {
                    if (live != null)
                    {
                        var details = await live.GetPlayerDetailsAsync(summary, mode);
                        if (details != null)
                            popup.SetData(details);
                        else
                            popup.ShowError(summary, "No wallii data.");
                    }
                    else
                    {
                        popup.SetData(SampleData.Details(summary));
                    }
                }
                catch (Exception ex)
                {
                    popup.ShowError(summary, ex.Message);
                }
            };

            window.Loaded += async (s, e) =>
            {
                try
                {
                    if (top)
                    {
                        panel.SetStatus("Loading top players…");
                        var stats = await live.GetTopLobbyAsync(mode);
                        panel.SetStats(stats);
                        panel.SetStatus("PREVIEW — current top 8 · wallii.gg");
                        var first = stats.FirstOrDefault(x => x.OnLeaderboard);
                        if (first != null)
                        {
                            popup.ShowLoading(first);
                            var details = await live.GetPlayerDetailsAsync(first, mode);
                            if (details != null)
                                popup.SetData(details);
                        }
                    }
                    else if (live != null)
                    {
                        panel.SetStatus("Loading wallii stats…");
                        var names = liveNames.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
                        var stats = await live.GetLobbyStatsAsync(names, region, mode);
                        panel.SetStats(stats);
                        var first = stats.FirstOrDefault(x => x.OnLeaderboard);
                        if (first != null)
                        {
                            popup.ShowLoading(first);
                            var details = await live.GetPlayerDetailsAsync(first, mode);
                            if (details != null)
                                popup.SetData(details);
                        }
                    }
                    else
                    {
                        var lobby = SampleData.Lobby();
                        panel.SetStats(lobby);
                        popup.SetData(SampleData.Details(lobby[0]));
                    }

                    if (collapsed)
                    {
                        panel.IsCollapsed = true;
                        popup.Visibility = Visibility.Collapsed;
                    }

                    if (shotPath != null)
                    {
                        // give the layout a moment to settle
                        await Task.Delay(600);
                        SaveScreenshot(window, shotPath);
                        app.Shutdown(0);
                    }
                }
                catch (Exception ex)
                {
                    if (shotPath != null)
                    {
                        Console.Error.WriteLine(ex);
                        app.Shutdown(1);
                    }
                    else
                    {
                        MessageBox.Show(ex.ToString());
                    }
                }
            };

            return app.Run(window);
        }

        private static void SaveScreenshot(Window window, string path)
        {
            var canvas = (Canvas)window.Content;

            // Crop to the union of the visible panels so screenshots have no dead space.
            double minX = double.MaxValue, minY = double.MaxValue, maxX = 0, maxY = 0;
            foreach (FrameworkElement child in canvas.Children)
            {
                if (child.Visibility != Visibility.Visible || child.ActualWidth < 1)
                    continue;
                var left = Canvas.GetLeft(child);
                var top = Canvas.GetTop(child);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;
                var scale = (child.RenderTransform as ScaleTransform)?.ScaleX ?? 1.0;
                minX = Math.Min(minX, left);
                minY = Math.Min(minY, top);
                maxX = Math.Max(maxX, left + child.ActualWidth * scale);
                maxY = Math.Max(maxY, top + child.ActualHeight * scale);
            }
            if (maxX <= minX)
            {
                minX = 0; minY = 0; maxX = canvas.ActualWidth; maxY = canvas.ActualHeight;
            }
            const double pad = 20;
            var x = Math.Max(0, minX - pad);
            var y = Math.Max(0, minY - pad);
            var w = Math.Min(canvas.ActualWidth, maxX + pad) - x;
            var h = Math.Min(canvas.ActualHeight, maxY + pad) - y;

            var debug = Environment.GetEnvironmentVariable("MMRADAR_SHOT_DEBUG");
            if (!string.IsNullOrEmpty(debug))
            {
                var lines = new System.Text.StringBuilder();
                lines.AppendLine($"canvas {canvas.ActualWidth}x{canvas.ActualHeight}");
                foreach (FrameworkElement child in canvas.Children)
                    lines.AppendLine($"child {child.GetType().Name} left={Canvas.GetLeft(child)} top={Canvas.GetTop(child)} aw={child.ActualWidth} ah={child.ActualHeight} ds={child.DesiredSize} rs={child.RenderSize}");
                lines.AppendLine($"crop x={x} y={y} w={w} h={h}");
                File.WriteAllText(debug, lines.ToString());
            }

            var bitmap = new RenderTargetBitmap(
                (int)canvas.ActualWidth, (int)canvas.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            // Two Render passes compose 1:1 — a VisualBrush would stretch the content
            // bounds to fill the bitmap and distort the proportions.
            var background = new DrawingVisual();
            using (var ctx = background.RenderOpen())
            {
                ctx.DrawRectangle(window.Background, null,
                    new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight));
            }
            bitmap.Render(background);
            bitmap.Render(canvas);
            var cropped = new CroppedBitmap(bitmap, new Int32Rect((int)x, (int)y, (int)w, (int)h));

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            using (var stream = File.Create(path))
                encoder.Save(stream);
        }
    }
}
