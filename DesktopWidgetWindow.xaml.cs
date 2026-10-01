using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;

namespace Warden
{
    public partial class DesktopWidgetWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MINIMIZE = 0xF020;
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_SHOWWINDOW = 0x0018;
        private const uint SWP_HIDEWINDOW = 0x0080;
        private const uint SWP_NOZORDER = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private readonly ThrottleStopService _tsService;
        private readonly AppConfig _config;
        private readonly Action _saveConfigAction;
        private readonly Action _openWardenAction;
        private bool _isLocked = false;
        private readonly Button[] _profileButtons;
        private readonly Style _normalStyle;
        private readonly Style _activeStyle;

        public DesktopWidgetWindow(ThrottleStopService tsService, AppConfig config, Action saveConfigAction, Action openWardenAction)
        {
            InitializeComponent();

            _tsService = tsService;
            _config = config;
            _saveConfigAction = saveConfigAction;
            _openWardenAction = openWardenAction;

            _profileButtons = new[] { btnProfile1, btnProfile2, btnProfile3, btnProfile4 };
            _normalStyle = (Style)FindResource("ProfileBtnStyle");
            _activeStyle = (Style)FindResource("ActiveProfileBtnStyle");

            // Load position & lock state
            _isLocked = _config.DesktopWidgetLocked;
            ApplyLanguage();
            if (mainCard.ContextMenu != null)
            {
                mainCard.ContextMenu.Opened += (s, e) => ApplyLanguage();
            }

            // Sanitize coordinates within screen bounds
            double left = _config.DesktopWidgetLeft;
            double top = _config.DesktopWidgetTop;

            double maxLeft = SystemParameters.VirtualScreenWidth - 200;
            double maxTop = SystemParameters.VirtualScreenHeight - 60;

            if (left < 0 || left > maxLeft) left = 100;
            if (top < 0 || top > maxTop) top = 100;

            this.Left = left;
            this.Top = top;

            // Load profile names & visibility
            UpdateProfileButtonLabels();
            ApplyVisibleProfiles();

            // Set current active profile visual
            UpdateActiveProfileVisual(_tsService.ActiveProfile);

            // Hook ThrottleStop changes
            _tsService.ActiveProfileChanged += OnActiveProfileChanged;

            // Auto restore if shell hides window
            this.IsVisibleChanged += (s, e) =>
            {
                if (!this.IsVisible && _config.DesktopWidgetEnabled)
                {
                    this.Show();
                }
            };
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState != WindowState.Normal)
            {
                WindowState = WindowState.Normal;
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var helper = new WindowInteropHelper(this);
            IntPtr hwnd = helper.Handle;

            // Prevent Alt+Tab visibility (WS_EX_TOOLWINDOW)
            try
            {
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE).ToInt32();
                SetWindowLong(hwnd, GWL_EXSTYLE, (IntPtr)(exStyle | WS_EX_TOOLWINDOW));
            }
            catch { }

            // Intercept Win+D / Show Desktop minimize, hide, and window position
            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 1. Intercept Win+D / Show Desktop hide command
            if (msg == WM_WINDOWPOSCHANGING)
            {
                WINDOWPOS wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                bool modified = false;

                // Stripping SWP_HIDEWINDOW prevents Windows from hiding the widget on Show Desktop
                if ((wp.flags & SWP_HIDEWINDOW) != 0)
                {
                    wp.flags &= ~SWP_HIDEWINDOW;
                    modified = true;
                }

                // Prevent Windows from pushing the widget behind the wallpaper (HWND_BOTTOM)
                if (wp.hwndInsertAfter == HWND_BOTTOM)
                {
                    wp.hwndInsertAfter = IntPtr.Zero;
                    wp.flags |= SWP_NOZORDER;
                    modified = true;
                }

                if (modified)
                {
                    Marshal.StructureToPtr(wp, lParam, false);
                }
            }

            // 2. Prevent Win+D / Show Desktop minimize command
            if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_MINIMIZE)
            {
                handled = true;
                return IntPtr.Zero;
            }

            // 3. Prevent OS from hiding window during Win+D (Show Desktop)
            if (msg == WM_SHOWWINDOW && wParam == IntPtr.Zero)
            {
                handled = true;
                return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        public void ApplyVisibleProfiles()
        {
            var visibleList = _config.DesktopWidgetVisibleProfiles;
            if (visibleList == null || visibleList.Count == 0)
            {
                visibleList = new List<int> { 0, 1, 2, 3 };
            }

            for (int i = 0; i < _profileButtons.Length; i++)
            {
                bool isVisible = visibleList.Contains(i);
                _profileButtons[i].Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            }

            // Adjust trailing margins so the last visible item has no right margin
            Button? lastVisibleBtn = null;
            for (int i = 0; i < _profileButtons.Length; i++)
            {
                if (_profileButtons[i].Visibility == Visibility.Visible)
                {
                    _profileButtons[i].Margin = new Thickness(0, 0, 6, 0);
                    lastVisibleBtn = _profileButtons[i];
                }
            }
            if (lastVisibleBtn != null)
            {
                lastVisibleBtn.Margin = new Thickness(0, 0, 0, 0);
            }
        }

        private void UpdateProfileButtonLabels()
        {
            var names = _tsService.ProfileNames;
            if (names.Length >= 4)
            {
                txtProfile1.Text = TruncateName(names[0], 5);
                txtProfile2.Text = TruncateName(names[1], 5);
                txtProfile3.Text = TruncateName(names[2], 5);
                txtProfile4.Text = TruncateName(names[3], 5);

                btnProfile1.ToolTip = names[0];
                btnProfile2.ToolTip = names[1];
                btnProfile3.ToolTip = names[2];
                btnProfile4.ToolTip = names[3];
            }
        }

        private static string TruncateName(string name, int maxLen)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Length <= maxLen ? name : name.Substring(0, maxLen);
        }

        private void OnActiveProfileChanged(object? sender, int profileIndex)
        {
            Dispatcher.BeginInvoke(() =>
            {
                UpdateActiveProfileVisual(profileIndex);
            });
        }

        private void UpdateActiveProfileVisual(int activeIndex)
        {
            for (int i = 0; i < _profileButtons.Length; i++)
            {
                _profileButtons[i].Style = (i == activeIndex) ? _activeStyle : _normalStyle;
            }
        }

        private void BtnProfile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int idx))
            {
                UpdateActiveProfileVisual(idx);
                _tsService.SetProfile(idx);
            }
        }

        private void MainCard_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && !_isLocked)
            {
                this.DragMove();
                SaveCurrentPosition();
            }
        }

        private void MainCard_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && !_isLocked)
            {
                SaveCurrentPosition();
            }
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            // Intentionally not saving to disk on every pixel move to prevent 3 FPS lag
        }

        private void SaveCurrentPosition()
        {
            _config.DesktopWidgetLeft = this.Left;
            _config.DesktopWidgetTop = this.Top;
            _saveConfigAction();
        }

        private void BtnLockToggle_Click(object sender, RoutedEventArgs e)
        {
            _isLocked = !_isLocked;
            _config.DesktopWidgetLocked = _isLocked;
            _saveConfigAction();
            UpdateLockVisuals();
        }

        private void MenuLock_Click(object sender, RoutedEventArgs e)
        {
            BtnLockToggle_Click(sender, e);
        }

        public void ApplyLanguage()
        {
            UpdateLockVisuals();
            menuStartTS.Header = Loc.Get("WidgetStartTS");
            menuOpenWarden.Header = Loc.Get("WidgetOpenWarden");
            menuHideWidget.Header = Loc.Get("WidgetHide");
        }

        private void UpdateLockVisuals()
        {
            txtLockIcon.Text = _isLocked ? "🔒" : "🔓";
            menuLock.Header = _isLocked ? Loc.Get("WidgetUnlock") : Loc.Get("WidgetLock");
            btnLockToggle.ToolTip = Loc.Get("WidgetLockTooltip");
            mainCard.Cursor = _isLocked ? Cursors.Arrow : Cursors.SizeAll;
        }

        private void MenuStartTS_Click(object sender, RoutedEventArgs e)
        {
            _tsService.StartThrottleStop();
        }

        private void MenuOpenWarden_Click(object sender, RoutedEventArgs e)
        {
            _openWardenAction();
        }

        private void MenuHideWidget_Click(object sender, RoutedEventArgs e)
        {
            _config.DesktopWidgetEnabled = false;
            _saveConfigAction();
            this.Hide();
        }

        protected override void OnClosed(EventArgs e)
        {
            _tsService.ActiveProfileChanged -= OnActiveProfileChanged;
            base.OnClosed(e);
        }
    }
}
