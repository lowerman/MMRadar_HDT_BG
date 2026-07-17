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
                Height = 640,
                Background = new LinearGradientBrush(
                    Color.FromRgb(0x14, 0x12, 0x0F),
                    Color.FromRgb(0x24, 0x1E, 0x16),
                    90),
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
            var element = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap(
                (int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);

            // render the window background + content together
            var visual = new DrawingVisual();
            using (var ctx = visual.RenderOpen())
            {
                ctx.DrawRectangle(window.Background, null,
                    new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                ctx.DrawRectangle(new VisualBrush(element), null,
                    new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            }
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            using (var stream = File.Create(path))
                encoder.Save(stream);
        }
    }
}
