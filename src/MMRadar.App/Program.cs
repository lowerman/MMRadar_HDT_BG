using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
        private const string MutexName = "MMRadar.Standalone.SingleInstance";
        private const string ShowEventName = "MMRadar.Standalone.ShowWindow";

        private static Mutex _singleInstance;
        private static EventWaitHandle _showEvent;
        private static Application _app;
        private static AppCore _core;
        private static HearthstoneReader _reader;
        private static GameTracker _tracker;
        private static OverlayHost _overlay;
        private static AppWindow _window;
        private static Forms.NotifyIcon _tray;
        private static Forms.ToolStripMenuItem _enabledItem;
        private static bool _lastConnected;
        private static readonly object LogLock = new object();
        private static string _logPath;

        private static UpdateInfo _pendingUpdate;
        private static bool _updating;
        private static bool _justUpdated;
        private static bool _autostart;
        private const string AutostartArg = "--autostart";
        private static string _installZip;

        public static string Version => UpdateService.CurrentVersion.ToString(3);

        [STAThread]
        public static int Main(string[] args)
        {
            args = args ?? new string[0];
            _justUpdated = HasArg(args, "--updated");
            _autostart = HasArg(args, AutostartArg);

            // One instance per user. A second launch brings the running one's
            // window up instead of complaining; right after an update the new
            // build waits for the old one to finish exiting.
            _singleInstance = new Mutex(false, MutexName);
            bool owned;
            try
            {
                owned = _singleInstance.WaitOne(_justUpdated ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                owned = true; // the previous instance died without cleaning up
            }
            if (!owned)
            {
                try
                {
                    using (var show = EventWaitHandle.OpenExisting(ShowEventName))
                        show.Set();
                }
                catch
                {
                    Forms.MessageBox.Show("MMRadar is already running — look for its icon in the system tray.",
                        "MMRadar", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
                }
                return 0;
            }

            SetUpLogging();
            Logger.Info($"MMRadar {Version} starting" + (_justUpdated ? " (just updated)" : ""));
            UpdateService.CleanupOldFiles();

            AppCore.SimulateWalliiDown = HasArg(args, "--simulate-wallii-down");
            if (AppCore.SimulateWalliiDown)
                Logger.Info("TEST MODE: wallii outage simulation is ON (--simulate-wallii-down)");
            // Developer aid: install a local release zip through the real update path.
            var zipIndex = Array.FindIndex(args, a => string.Equals(a, "--install-zip", StringComparison.OrdinalIgnoreCase));
            if (zipIndex >= 0 && zipIndex + 1 < args.Length)
                _installZip = args[zipIndex + 1];

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

        private static bool HasArg(string[] args, string name) =>
            Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

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
                if (_window != null && _window.IsVisible)
                    RefreshWindowStatus();
            };
            tick.Start();

            BuildTray();
            ListenForShowRequests();
            // Refresh an existing autostart entry: older builds wrote it without the
            // quiet-start flag, and the exe may have moved since.
            if (IsAutostartEnabled())
                SetAutostart(true);

            if (_justUpdated)
            {
                _tray.ShowBalloonTip(4000, $"MMRadar updated to {Version}",
                    "Everything is ready — your settings were kept.", Forms.ToolTipIcon.Info);
            }
            else if (_autostart && _core.AppSettings.WelcomeShown)
            {
                // Started with Windows: settle quietly into the tray.
                _tray.ShowBalloonTip(4000,
                    AppCore.SimulateWalliiDown ? "MMRadar is running — wallii outage simulation" : "MMRadar is running",
                    "The panel appears automatically in Battlegrounds. Click here for options.",
                    Forms.ToolTipIcon.Info);
            }
            else
            {
                // Launched by hand: show the window — a tray icon alone is easy to
                // miss and reads as "nothing happened". The welcome card only once.
                ShowWindow();
                if (!_core.AppSettings.WelcomeShown)
                {
                    _window.ShowWelcome();
                    _core.AppSettings.WelcomeShown = true;
                    _core.AppSettings.Save();
                }
            }

            if (_installZip != null)
                _app.Dispatcher.BeginInvoke(new Action(InstallLocalZip), DispatcherPriority.ApplicationIdle);

            ScheduleUpdateChecks();
        }

        // --- window ---

        private static void ShowWindow()
        {
            if (_window == null)
            {
                _window = new AppWindow(_core, IsAutostartEnabled(), SetAutostart, OpenDataFolder);
                try
                {
                    using (var icon = DrawIcon())
                        _window.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle,
                            Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                }
                catch (Exception ex)
                {
                    Logger.Debug("Window icon: " + ex.Message);
                }
                _window.UpdateRequested += () => _ = ApplyUpdateAsync();
                _window.CheckRequested += () => _ = CheckForUpdatesAsync(manual: true);
                _window.WhatsNewRequested += () =>
                    UpdateService.OpenInBrowser(_pendingUpdate?.PageUrl ?? UpdateService.ReleasesPage);
            }
            if (_pendingUpdate != null && !_updating)
                _window.ShowUpdate(_pendingUpdate, UpdateService.CanUpdateInPlace());
            RefreshWindowStatus();
            _window.Show();
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        private static void RefreshWindowStatus()
        {
            var snap = _reader.Latest;
            string title, detail;
            if (!snap.Connected)
            {
                title = "Waiting for Hearthstone";
                detail = "start the game";
            }
            else
            {
                title = _tracker.InBattlegroundsMatch ? "In a Battlegrounds match" : "Connected";
                detail = snap.Region ?? "";
                if (!_core.OverlayEnabled)
                    detail = (detail.Length > 0 ? detail + " · " : "") + "overlay off";
            }
            if (AppCore.SimulateWalliiDown)
                detail = (detail.Length > 0 ? detail + " · " : "") + "wallii sim";
            _window.SetStatus(title, detail, snap.Connected);
        }

        /// <summary>A second launch signals this event instead of starting twice.</summary>
        private static void ListenForShowRequests()
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var listener = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _showEvent.WaitOne();
                        _app.Dispatcher.BeginInvoke(new Action(ShowWindow));
                    }
                    catch
                    {
                        return; // disposed on exit
                    }
                }
            })
            { IsBackground = true, Name = "MMRadar.ShowListener" };
            listener.Start();
        }

        // --- tray ---

        private static void BuildTray()
        {
            var menu = new Forms.ContextMenuStrip();
            var open = new Forms.ToolStripMenuItem("Open MMRadar", null, (s, e) => ShowWindow())
            {
                Font = new Font(Forms.SystemInformation.MenuFont, System.Drawing.FontStyle.Bold),
            };
            menu.Items.Add(open);
            menu.Items.Add("Show / hide panel", null, (s, e) => _core.ToggleOverlay());
            _enabledItem = new Forms.ToolStripMenuItem("Overlay enabled")
            {
                Checked = _core.OverlayEnabled,
                CheckOnClick = true,
            };
            _enabledItem.CheckedChanged += (s, e) => _core.OverlayEnabled = _enabledItem.Checked;
            menu.Items.Add(_enabledItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => Exit());

            _tray = new Forms.NotifyIcon
            {
                Icon = DrawIcon(),
                Text = TrayText(false),
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.MouseClick += (s, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                    ShowWindow();
            };
            _tray.BalloonTipClicked += (s, e) => ShowWindow();
        }

        private static void UpdateTrayStatus()
        {
            var connected = _tracker.HearthstoneRunning;
            if (_tray == null || connected == _lastConnected)
                return;
            _lastConnected = connected;
            _tray.Text = TrayText(connected);
        }

        /// <summary>Tray hover text (NotifyIcon caps it at 63 characters).</summary>
        private static string TrayText(bool connected) =>
            (connected ? $"MMRadar {Version} — connected to Hearthstone" : "MMRadar — waiting for Hearthstone") +
            (AppCore.SimulateWalliiDown ? " [wallii down sim]" : "");

        // --- updates ---

        private static void ScheduleUpdateChecks()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            timer.Tick += (s, e) =>
            {
                timer.Interval = TimeSpan.FromHours(6);
                if (_core.AppSettings.AutoCheckUpdates)
                    _ = CheckForUpdatesAsync(manual: false);
            };
            timer.Start();
        }

        private static async Task CheckForUpdatesAsync(bool manual)
        {
            UpdateInfo found;
            try
            {
                found = await UpdateService.CheckAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Logger.Debug("Update check failed: " + ex.Message);
                if (manual)
                    _window?.ShowCheckResult("Couldn't reach GitHub — check your connection and try again.");
                return;
            }

            if (found == null)
            {
                if (manual)
                    _window?.ShowCheckResult($"You're on the latest version ({Version}).");
                return;
            }

            Logger.Info($"Update available: {found.Version.ToString(3)}");
            _pendingUpdate = found;
            if (manual)
                _window?.ShowCheckResult("");
            if (_window != null && !_updating)
                _window.ShowUpdate(found, UpdateService.CanUpdateInPlace());

            // Announce each new version once; the window keeps showing it.
            var v = found.Version.ToString(3);
            if (!manual && _core.AppSettings.LastAnnouncedVersion != v)
            {
                _core.AppSettings.LastAnnouncedVersion = v;
                _core.AppSettings.Save();
                _tray.ShowBalloonTip(6000, $"MMRadar {v} is available",
                    "Click here to update — it takes a few seconds.", Forms.ToolTipIcon.Info);
            }
        }

        private static async Task ApplyUpdateAsync()
        {
            var update = _pendingUpdate;
            if (update == null || _updating)
                return;
            if (!UpdateService.CanUpdateInPlace())
            {
                UpdateService.OpenInBrowser(update.PageUrl);
                return;
            }

            _updating = true;
            var progress = new Progress<string>(text => _window?.SetUpdateBusy(text));
            try
            {
                var exe = await UpdateService.DownloadAndInstallAsync(update, progress).ConfigureAwait(true);
                Restart(exe);
            }
            catch (Exception ex)
            {
                Logger.Error("Update failed", ex);
                _updating = false;
                _window?.ShowUpdateError(
                    "The update didn't go through — nothing was changed. " +
                    "Try again, or download it from the release page (What's new).");
            }
        }

        private static void InstallLocalZip()
        {
            try
            {
                Logger.Info("Installing local zip: " + _installZip);
                var exe = UpdateService.InstallFromZip(_installZip);
                Restart(exe);
            }
            catch (Exception ex)
            {
                Logger.Error("Local zip install failed", ex);
            }
        }

        /// <summary>Hands over to the freshly installed build.</summary>
        private static void Restart(string exe)
        {
            Logger.Info("Restarting into the updated build");
            Shutdown();
            try
            {
                Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                Logger.Error("Restart failed — start MMRadar manually", ex);
            }
            _app.Shutdown();
        }

        // --- shutdown ---

        private static void Exit()
        {
            Shutdown();
            Logger.Info("MMRadar exited");
            _app.Shutdown();
        }

        private static void Shutdown()
        {
            try
            {
                _core?.Unload();
                _reader?.Dispose();
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                    _tray = null;
                }
                _window?.CloseForExit();
                _overlay?.Close();
            }
            catch (Exception ex)
            {
                Logger.Error("Shutdown failed", ex);
            }
            try
            {
                // Release before a restart so the new build can take over at once.
                _singleInstance?.ReleaseMutex();
            }
            catch
            {
                // not owned (already released)
            }
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
                        key.SetValue(RunValue, $"\"{Assembly.GetExecutingAssembly().Location}\" {AutostartArg}");
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
