using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MMRadar.Overlay
{
    /// <summary>
    /// A transparent, always-on-top window glued to Hearthstone's client area —
    /// the standalone replacement for HDT's overlay canvas.
    ///
    /// Clicks fall through every fully transparent pixel (layered-window hit
    /// testing), so only the panel and its cards are interactive. The window
    /// never takes focus (WS_EX_NOACTIVATE): activating it would minimize a
    /// fullscreen game. It shows only while the game (or MMRadar itself) is in
    /// the foreground, and spans the primary screen when the game is not running
    /// so the preview still has somewhere to live.
    /// </summary>
    public sealed class OverlayHost : Window
    {
        private readonly DispatcherTimer _follow;
        private IntPtr _hwnd;
        private IntPtr _gameHwnd;
        private DateTime _nextGameLookupUtc = DateTime.MinValue;
        private Rect _lastBounds = Rect.Empty;
        private bool _gameWasForeground;
        private DateTime _nextTopmostCheckUtc = DateTime.MinValue;

        public Canvas Surface { get; }

        /// <summary>Set while the panel is wanted on screen at all.</summary>
        public bool Wanted { get; set; } = true;

        /// <summary>
        /// Without the game the overlay exists only for the test panel. Otherwise
        /// it stays hidden: an invisible topmost screen-sized window would sit over
        /// every other app (and other fullscreen games) for no reason.
        /// </summary>
        public bool ShowWithoutGame
        {
            get => _showWithoutGame;
            set
            {
                if (_showWithoutGame == value)
                    return;
                _showWithoutGame = value;
                Follow(); // apply now, not on the next 250 ms tick
            }
        }

        private bool _showWithoutGame;

        public OverlayHost()
        {
            Title = "MMRadar Overlay";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            // Null background: empty canvas areas are not hit-test targets.
            Surface = new Canvas { Background = null, ClipToBounds = false };
            Content = Surface;

            var wa = SystemParameters.WorkArea;
            Left = wa.Left;
            Top = wa.Top;
            Width = wa.Width;
            Height = wa.Height;

            SourceInitialized += (s, e) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                var ex = GetWindowLong(_hwnd, GwlExStyle);
                SetWindowLong(_hwnd, GwlExStyle, ex | WsExNoActivate | WsExToolWindow);
            };

            _follow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _follow.Tick += (s, e) => Follow();
        }

        public void Start()
        {
            Show();
            _follow.Start();
            Follow();
        }

        private void Follow()
        {
            try
            {
                var game = FindGameWindow();
                if (game == IntPtr.Zero)
                {
                    // Game not running: park over the primary work area so the
                    // positioning preview works from the tray.
                    var wa = SystemParameters.WorkArea;
                    Place(new Rect(wa.Left, wa.Top, wa.Width, wa.Height));
                    SetShown(Wanted && ShowWithoutGame);
                    return;
                }

                if (IsIconic(game))
                {
                    SetShown(false);
                    return;
                }

                var fg = GetForegroundWindow();
                GetWindowThreadProcessId(fg, out var fgPid);
                var ours = fgPid == (uint)Process.GetCurrentProcess().Id;
                if (fg != game && !ours)
                {
                    // Alt-tabbed away: don't float the panel over other apps.
                    SetShown(false);
                    return;
                }

                if (GetClientRect(game, out var rc))
                {
                    var origin = new NativePoint { X = 0, Y = 0 };
                    ClientToScreen(game, ref origin);
                    var device = new Rect(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
                    if (device.Width > 100 && device.Height > 100)
                        Place(ToDips(device));
                }
                var becameVisible = SetShown(Wanted);
                var gameFg = fg == game;
                // Fullscreen games can claim the top of the z-order when they
                // (re)gain focus — re-assert then. NOT on every tick: tooltips
                // are separate topmost popups, and a constant re-assert pushed
                // the overlay above its own hover text. The slow periodic
                // safety net only runs while the cursor is off the panel, i.e.
                // while no tooltip can be open.
                var regained = gameFg && !_gameWasForeground;
                var periodic = gameFg && !IsMouseOver && DateTime.UtcNow >= _nextTopmostCheckUtc;
                if (_hwnd != IntPtr.Zero && Wanted && (regained || becameVisible || periodic))
                {
                    SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                    _nextTopmostCheckUtc = DateTime.UtcNow.AddSeconds(5);
                }
                _gameWasForeground = gameFg;
            }
            catch (Exception ex)
            {
                Util.Logger.Debug("Overlay follow: " + ex.Message);
            }
        }

        private IntPtr FindGameWindow()
        {
            if (_gameHwnd != IntPtr.Zero && IsWindow(_gameHwnd))
                return _gameHwnd;
            _gameHwnd = IntPtr.Zero;
            if (DateTime.UtcNow < _nextGameLookupUtc)
                return IntPtr.Zero;
            _nextGameLookupUtc = DateTime.UtcNow.AddSeconds(2);
            var proc = Process.GetProcessesByName("Hearthstone").FirstOrDefault();
            if (proc == null)
                return IntPtr.Zero;
            using (proc)
            {
                var h = proc.MainWindowHandle;
                if (h != IntPtr.Zero)
                    _gameHwnd = h;
            }
            return _gameHwnd;
        }

        private void Place(Rect bounds)
        {
            if (bounds == _lastBounds)
                return;
            _lastBounds = bounds;
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
        }

        /// <summary>Returns true when the window just went from hidden to shown.</summary>
        private bool SetShown(bool shown)
        {
            var v = shown ? Visibility.Visible : Visibility.Hidden;
            if (Visibility == v)
                return false;
            Visibility = v;
            if (!shown)
                _gameWasForeground = false;
            return shown;
        }

        private Rect ToDips(Rect device)
        {
            var source = PresentationSource.FromVisual(this);
            var m = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var tl = m.Transform(new Point(device.Left, device.Top));
            var br = m.Transform(new Point(device.Right, device.Bottom));
            return new Rect(tl, br);
        }

        // --- Win32 ---

        private const int GwlExStyle = -20;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X, Y; }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
