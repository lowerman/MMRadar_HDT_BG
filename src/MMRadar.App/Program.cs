using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using MMRadar.Hs;
using MMRadar.Overlay;
using MMRadar.Util;
using Forms = System.Windows.Forms;

namespace MMRadar.Standalone
{
    /// <summary>
    /// MMRadar without Hearthstone Deck Tracker: a tray app that reads the game
    /// state itself and draws the same overlay on top of Hearthstone.
    /// </summary>
    public static class Program
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "MMRadar";

        private static Mutex _singleInstance;
        private static Application _app;
        private static AppCore _core;
        private static HearthstoneReader _reader;
        private static GameTracker _tracker;
        private static OverlayHost _overlay;
        private static Forms.NotifyIcon _tray;
        private static Forms.ToolStripMenuItem _enabledItem;
        private static bool _lastConnected;
        private static readonly object LogLock = new object();
        private static string _logPath;

        public static string Version =>
            Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        [STAThread]
        public static int Main()
        {
            _singleInstance = new Mutex(true, "MMRadar.Standalone.SingleInstance", out var first);
            if (!first)
            {
                Forms.MessageBox.Show("MMRadar is already running — look for its icon in the system tray.",
                    "MMRadar", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
                return 0;
            }

            SetUpLogging();
            Logger.Info($"MMRadar {Version} starting");

            _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            _app.DispatcherUnhandledException += (s, e) =>
            {
                Logger.Error("Unhandled UI exception", e.Exception);
                e.Handled = true; // an overlay glitch must never take the app down
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Logger.Error("Unhandled exception", e.ExceptionObject as Exception);

            _app.Startup += (s, e) => Start();
            return _app.Run();
        }

        private static void Start()
        {
            _reader = new HearthstoneReader();
            _reader.Start();
            _tracker = new GameTracker(_reader);

            _overlay = new OverlayHost();
            _overlay.Start();

            _core = new AppCore(_tracker, _overlay);
            _core.Load();

            var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            tick.Tick += (s, e) =>
            {
                _core.Tick();
                UpdateTrayStatus();
            };
            tick.Start();

            BuildTray();
        }

        private static void BuildTray()
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Show / hide panel", null, (s, e) => _core.ToggleOverlay());
            menu.Items.Add("Settings…", null, (s, e) => _core.OpenSettings());
            _enabledItem = new Forms.ToolStripMenuItem("Overlay enabled")
            {
                Checked = _core.OverlayEnabled,
                CheckOnClick = true,
            };
            _enabledItem.CheckedChanged += (s, e) => _core.OverlayEnabled = _enabledItem.Checked;
            menu.Items.Add(_enabledItem);
            var autostart = new Forms.ToolStripMenuItem("Start with Windows")
            {
                Checked = IsAutostartEnabled(),
                CheckOnClick = true,
            };
            autostart.CheckedChanged += (s, e) => SetAutostart(autostart.Checked);
            menu.Items.Add(autostart);
            menu.Items.Add("Open data folder", null, (s, e) => OpenDataFolder());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => Exit());

            _tray = new Forms.NotifyIcon
            {
                Icon = DrawIcon(),
                Text = "MMRadar — waiting for Hearthstone",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.DoubleClick += (s, e) => _core.OpenSettings();
            _tray.ShowBalloonTip(4000, "MMRadar is running",
                "The panel appears automatically in Battlegrounds. Right-click this icon for options.",
                Forms.ToolTipIcon.Info);
        }

        private static void UpdateTrayStatus()
        {
            var connected = _tracker.HearthstoneRunning;
            if (_tray == null || connected == _lastConnected)
                return;
            _lastConnected = connected;
            _tray.Text = connected ? $"MMRadar {Version} — connected to Hearthstone" : "MMRadar — waiting for Hearthstone";
        }

        private static void Exit()
        {
            try
            {
                _core?.Unload();
                _reader?.Dispose();
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
                _overlay?.Close();
            }
            catch (Exception ex)
            {
                Logger.Error("Shutdown failed", ex);
            }
            Logger.Info("MMRadar exited");
            _app.Shutdown();
        }

        // --- autostart (per-user, no elevation) ---

        private static bool IsAutostartEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key?.GetValue(RunValue) != null;
            }
            catch
            {
                return false;
            }
        }

        private static void SetAutostart(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled)
                        key.SetValue(RunValue, $"\"{Assembly.GetExecutingAssembly().Location}\"");
                    else
                        key.DeleteValue(RunValue, false);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Autostart change failed", ex);
            }
        }

        private static void OpenDataFolder()
        {
            try
            {
                Directory.CreateDirectory(AppCore.DataDirectory);
                Process.Start("explorer.exe", $"\"{AppCore.DataDirectory}\"");
            }
            catch (Exception ex)
            {
                Logger.Error("Open data folder failed", ex);
            }
        }

        // --- logging ---

        private static void SetUpLogging()
        {
            try
            {
                var dir = Path.Combine(AppCore.DataDirectory, "logs");
                Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, "mmradar.log");
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 2 * 1024 * 1024)
                {
                    var old = Path.Combine(dir, "mmradar.old.log");
                    if (File.Exists(old))
                        File.Delete(old);
                    File.Move(_logPath, old);
                }
            }
            catch
            {
                _logPath = null;
            }
            Logger.InfoSink = m => Write("INFO ", m);
            Logger.DebugSink = m => Write("DEBUG", m);
            Logger.ErrorSink = m => Write("ERROR", m);
        }

        private static void Write(string level, string message)
        {
            if (_logPath == null)
                return;
            lock (LogLock)
                File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
        }

        // --- tray icon: a small radar sweep, drawn at runtime ---

        private static Icon DrawIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var bg = new SolidBrush(Color.FromArgb(255, 22, 27, 34)))
                        g.FillEllipse(bg, 1, 1, 30, 30);
                    using (var ring = new Pen(Color.FromArgb(255, 52, 194, 134), 2f))
                    {
                        g.DrawEllipse(ring, 2, 2, 27, 27);
                        g.DrawEllipse(ring, 9, 9, 13, 13);
                    }
                    using (var sweep = new SolidBrush(Color.FromArgb(150, 52, 194, 134)))
                        g.FillPie(sweep, 3, 3, 26, 26, -90, 70);
                    using (var dot = new SolidBrush(Color.FromArgb(255, 245, 197, 66)))
                        g.FillEllipse(dot, 19, 8, 5, 5);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
