using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MessageBox  = System.Windows.MessageBox;
using Color       = System.Windows.Media.Color;
using Brush       = System.Windows.Media.Brush;
using Brushes     = System.Windows.Media.Brushes;
using FontFamily  = System.Windows.Media.FontFamily;
using Cursors     = System.Windows.Input.Cursors;
using Button         = System.Windows.Controls.Button;
using ComboBox       = System.Windows.Controls.ComboBox;
using Point          = System.Windows.Point;
using Orientation    = System.Windows.Controls.Orientation;
using ColorConverter = System.Windows.Media.ColorConverter;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Warden
{
    public partial class MainWindow : Window
    {
        // ── State ──────────────────────────────────────────────────────
        private readonly SteelSeriesClient _client;
        private readonly TrayApplicationContext _context;
        private readonly HardwareMonitorService _hardwareMonitor;
        private List<SonarConfig> _availablePresets = new();
        private string _currentPage = "profiles";
        private TelemetrySnapshot? _lastTelemetrySnapshot;
        private readonly Dictionary<string, (TextBlock txtVal, Button btnStar, Button btnGraph)> _categoryRowControls = new();
        private readonly Dictionary<string, TextBlock> _favoriteValControls = new();
        private string _lastCategoryStructureKey = "";
        private string _lastFavoritesKey = "";
        private bool _telemetryInitialized = false;
        private bool _isLoadingPresets = false;
        private bool _presetsLoaded = false;
        private bool _suppressPresetSave = false;
        private bool _isPopulatingControls = false;   // Kontroller config'den doldurulurken kaydetme olaylarını yok say

        // Telemetri sayfası her saniye yenilenir; fırçalar her seferinde yeniden oluşturulmak yerine önbelleğe alınır
        private static readonly Dictionary<string, SolidColorBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

        private static SolidColorBrush BrushFrom(string hex)
        {
            if (!BrushCache.TryGetValue(hex, out var brush))
            {
                brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                BrushCache[hex] = brush;
            }
            return brush;
        }

        // ── Init ───────────────────────────────────────────────────────
        public MainWindow(TrayApplicationContext context, SteelSeriesClient client)
        {
            InitializeComponent();
            _context = context;
            _client = client;

            // Load window size
            if (_context.Config.WindowWidth >= MinWidth) this.Width = _context.Config.WindowWidth;
            if (_context.Config.WindowHeight >= MinHeight) this.Height = _context.Config.WindowHeight;
            this.SizeChanged += MainWindow_SizeChanged;

            // Hardware Monitor Service: LibreHardwareMonitor (çekirdek sürücüsü yükler, ağırdır) artık
            // uygulama açılışında değil, telemetri sayfası ilk kez açıldığında başlatılır.
            _hardwareMonitor = new HardwareMonitorService();
            _hardwareMonitor.TelemetryUpdated += OnTelemetryUpdated;

            this.IsVisibleChanged += (s, e) =>
            {
                if (this.IsVisible && _currentPage == "telemetry")
                    StartTelemetry();
                else
                    StopTelemetry();

                // Gizli pencerede sonsuz animasyon boşuna çalışmasın
                if (_pulseAnimation != null)
                {
                    if (this.IsVisible) _pulseAnimation.Resume(ledStatus);
                    else _pulseAnimation.Pause(ledStatus);
                }

                // GG açılışta kapalıysa preset listesi boş kalıyordu; pencere her açıldığında tekrar dene
                if (this.IsVisible)
                {
                    RefreshPresetsIfNeeded();
                    if (_lastGpuData != null) UpdateGpuData(_lastGpuData);
                }
            };

            LoadInitialConfig();
            _ = LoadSonarPresetsAsync();

            // Status LED pulse animasyonunu başlat (kontrol edilebilir: pencere gizliyken duraklatılır)
            Loaded += (s, e) =>
            {
                _pulseAnimation = (System.Windows.Media.Animation.Storyboard)FindResource("PulseAnimation");
                _pulseAnimation.Begin(ledStatus, isControllable: true);
            };
        }

        private async void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (this.WindowState == WindowState.Normal)
            {
                _context.Config.WindowWidth = this.Width;
                _context.Config.WindowHeight = this.Height;
                
                // Debounce disk I/O slightly
                if (!_isSavingSize)
                {
                    _isSavingSize = true;
                    try
                    {
                        await Task.Delay(1000);
                        _context.SaveConfig();
                    }
                    finally
                    {
                        _isSavingSize = false;
                    }
                }
            }
        }
        private bool _isSavingSize = false;
        private System.Windows.Media.Animation.Storyboard? _pulseAnimation;
        private GpuData? _lastGpuData;

        // ══════════════════════════════════════════════════════════════
        //  Window chrome
        // ══════════════════════════════════════════════════════════════
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Windows 11: köşeleri DWM yuvarlasın (eskiden şeffaf pencere + CornerRadius ile yapılıyordu).
            // Windows 10'da çağrı hata döndürür ve köşeler düz kalır.
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int pref = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
            => Hide();

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                WindowState = WindowState.Normal;
                return;
            }

            // WindowChrome ile büyütülen pencere, yeniden boyutlandırma kenarı kadar ekran dışına taşar → telafi et
            rootBorder.Margin = WindowState == WindowState.Maximized
                ? SystemParameters.WindowResizeBorderThickness
                : new Thickness(0);
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void CloseButton_Click(object sender, RoutedEventArgs e)
            => Hide();

        public bool IsExitExplicit = false;

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            StopTelemetry();
            if (!IsExitExplicit)
            {
                e.Cancel = true;
                this.Hide();
            }
            else
            {
                _hardwareMonitor.Dispose();
                base.OnClosing(e);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Sidebar Navigation
        // ══════════════════════════════════════════════════════════════
        private void BtnNavHome_Click(object sender, RoutedEventArgs e)
            => ShowPage("profiles");

        private void BtnNavSettings_Click(object sender, RoutedEventArgs e)
            => ShowPage("settings");

        private void BtnNavTelemetry_Click(object sender, RoutedEventArgs e)
            => ShowPage("telemetry");

        private void BtnNavGpu_Click(object sender, RoutedEventArgs e)
            => ShowPage("gpu");
            
        private void BtnNavDevice_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("devices");
            LoadAudioDevices();
        }

        private void ShowPage(string page)
        {
            _currentPage = page;
            pageProfiles.Visibility      = page == "profiles"  ? Visibility.Visible : Visibility.Collapsed;
            pageDeviceManager.Visibility = page == "devices"   ? Visibility.Visible : Visibility.Collapsed;
            pageGpuMonitor.Visibility    = page == "gpu"       ? Visibility.Visible : Visibility.Collapsed;
            pageTelemetry.Visibility     = page == "telemetry" ? Visibility.Visible : Visibility.Collapsed;
            pageSettings.Visibility      = page == "settings"  ? Visibility.Visible : Visibility.Collapsed;

            rectHomeActive.Visibility      = page == "profiles"  ? Visibility.Visible : Visibility.Collapsed;
            rectDeviceActive.Visibility    = page == "devices"   ? Visibility.Visible : Visibility.Collapsed;
            rectGpuActive.Visibility       = page == "gpu"       ? Visibility.Visible : Visibility.Collapsed;
            rectTelemetryActive.Visibility = page == "telemetry" ? Visibility.Visible : Visibility.Collapsed;
            rectSettingsActive.Visibility  = page == "settings"  ? Visibility.Visible : Visibility.Collapsed;

            btnNavHome.Foreground      = page == "profiles"  ? (Brush)FindResource("Accent") : (Brush)FindResource("TxtSecond");
            btnNavDevice.Foreground    = page == "devices"   ? (Brush)FindResource("Accent") : (Brush)FindResource("TxtSecond");
            btnNavGpu.Foreground       = page == "gpu"       ? (Brush)FindResource("Accent") : (Brush)FindResource("TxtSecond");
            btnNavTelemetry.Foreground = page == "telemetry" ? (Brush)FindResource("Accent") : (Brush)FindResource("TxtSecond");
            btnNavSettings.Foreground  = page == "settings"  ? (Brush)FindResource("Accent") : (Brush)FindResource("TxtSecond");

            if (page == "telemetry")
            {
                StartTelemetry();
            }
            else
            {
                StopTelemetry();
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Data Loading
        // ══════════════════════════════════════════════════════════════
        private void LoadInitialConfig()
        {
            _isPopulatingControls = true;
            try
            {
                txtInterval.Text   = _context.Config.CheckIntervalMilliseconds.ToString();
                chkStartup.IsChecked = _context.Config.StartWithWindows;

                foreach (ComboBoxItem item in cbLanguage.Items)
                {
                    if (item.Tag?.ToString() == _context.Config.Language)
                    { cbLanguage.SelectedItem = item; break; }
                }

                // GPU Initial Values
                txtTargetMhz.Text = _context.Config.TargetMhz.ToString(System.Globalization.CultureInfo.CurrentCulture);
                foreach (ComboBoxItem item in cbTargetProfile.Items)
                {
                    if (item.Tag?.ToString() == _context.Config.TargetProfile.ToString())
                    { cbTargetProfile.SelectedItem = item; break; }
                }
                foreach (ComboBoxItem item in cbCooldown.Items)
                {
                    if (item.Tag?.ToString() == _context.Config.CooldownSeconds.ToString())
                    { cbCooldown.SelectedItem = item; break; }
                }

                // Device delay
                txtDeviceDelay.Text = _context.Config.DeviceDisableDelaySeconds.ToString();

                ApplyLanguage();
                RenderRulesList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Config load error: {ex.Message}");
            }
            finally
            {
                _isPopulatingControls = false;
            }
        }

        private async Task LoadSonarPresetsAsync()
        {
            if (_isLoadingPresets) return;
            _isLoadingPresets = true;

            txtConnectionStatus.Text = Loc.Get("Connecting");
            ledStatus.Fill = new SolidColorBrush(Color.FromRgb(120, 120, 160));

            try
            {
                string address = await _client.GetSonarAddressAsync();
                txtConnectionStatus.Text = $"● {address.Replace("http://", "").Replace("https://", "")}";
                ledStatus.Fill = new SolidColorBrush(Color.FromRgb(78, 201, 126));

                _availablePresets = await _client.GetConfigsAsync();

                // Listeyi yeniden doldururken SelectionChanged → SaveConfig/ReloadConfig zinciri tetiklenmesin
                _suppressPresetSave = true;
                try
                {
                    cbDefaultPreset.Items.Clear();

                    foreach (var preset in _availablePresets)
                    {
                        if (preset.virtualAudioDevice == "game")
                        {
                            cbDefaultPreset.Items.Add(new PresetComboBoxItem { Text = preset.name, Value = preset.id });
                        }
                    }

                    if (!string.IsNullOrEmpty(_context.Config.DefaultPresetId))
                        SelectComboBoxByValue(cbDefaultPreset, _context.Config.DefaultPresetId);
                }
                finally
                {
                    _suppressPresetSave = false;
                }

                _presetsLoaded = true;
                RenderRulesList();
            }
            catch (Exception ex)
            {
                txtConnectionStatus.Text = Loc.Get("ConnFailed");
                ledStatus.Fill = new SolidColorBrush(Color.FromRgb(224, 85, 85));
                txtActivePreset.Text = ex.Message;
            }
            finally
            {
                _isLoadingPresets = false;
            }
        }

        /// <summary>Preset listesi henüz yüklenemediyse (GG kapalıydı vb.) yeniden dener.</summary>
        public void RefreshPresetsIfNeeded()
        {
            if (_presetsLoaded || _isLoadingPresets) return;
            _client.ResetAddress();
            _ = LoadSonarPresetsAsync();
        }

        /// <summary>"Windows ile başlat" kutusunu, kaydetme olaylarını tetiklemeden günceller.</summary>
        public void SetStartupChecked(bool isChecked)
        {
            _isPopulatingControls = true;
            try { chkStartup.IsChecked = isChecked; }
            finally { _isPopulatingControls = false; }
        }

        /// <summary>Tray'den "Reload Config" sonrası arayüzü yeni config ile yeniden doldurur.</summary>
        public void ReloadFromConfig()
        {
            _telemetryInitialized = false;
            LoadInitialConfig();
            _suppressPresetSave = true;
            try
            {
                if (!string.IsNullOrEmpty(_context.Config.DefaultPresetId))
                    SelectComboBoxByValue(cbDefaultPreset, _context.Config.DefaultPresetId);
                else
                    cbDefaultPreset.SelectedIndex = -1;
            }
            finally
            {
                _suppressPresetSave = false;
            }
            if (_currentPage == "telemetry" && IsVisible) StartTelemetry();
        }

        // ══════════════════════════════════════════════════════════════
        //  Rules List Rendering (Inline Design)
        // ══════════════════════════════════════════════════════════════
        private void RenderRulesList()
        {
            spRules.Children.Clear();

            foreach (var rule in _context.Config.Rules)
            {
                spRules.Children.Add(BuildRuleItem(rule.Key, rule.Value));
            }

            // Empty row for adding a new rule
            spRules.Children.Add(BuildAddRow());
        }

        private List<string> GetKnownExeNames()
        {
            var list = new List<string>();
            foreach (var exe in _context.Config.DiscoveredGames)
            {
                if (_context.Config.DiscoveredGameNames.TryGetValue(exe, out string? name) && !string.IsNullOrEmpty(name))
                    list.Add($"{name} ({exe})");
                else
                    list.Add(exe);
            }
            return list.OrderBy(x => x).ToList();
        }

        private UIElement BuildRuleItem(string originalKey, string currentPresetId)
        {
            var rowBorder = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 12, 0, 12)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Exe
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });                   // Arrow
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Preset
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // Delete

            // Exe ComboBox
            string displayKey = originalKey;
            if (_context.Config.DiscoveredGameNames.TryGetValue(originalKey, out string? gameName) && !string.IsNullOrEmpty(gameName))
                displayKey = $"{gameName} ({originalKey})";

            var cbExe = new ComboBox { IsEditable = false };
            var knownExes = GetKnownExeNames();
            // Büyük/küçük harf farkı (ör. "Game.exe" / "game.exe") listede aynı oyunu iki kez göstermesin
            string? existing = knownExes.FirstOrDefault(k => k.Equals(displayKey, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
                knownExes.Insert(0, displayKey);
            else
                displayKey = existing;

            foreach (var exe in knownExes) cbExe.Items.Add(exe);
            cbExe.SelectedItem = displayKey;
            
            Grid.SetColumn(cbExe, 0);
            grid.Children.Add(cbExe);

            // Arrow
            var arrow = new TextBlock
            {
                Text = "→",
                Foreground = (Brush)FindResource("TxtSecond"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(arrow, 1);
            grid.Children.Add(arrow);

            // Preset ComboBox
            var cbPreset = new ComboBox { DisplayMemberPath = "Text", SelectedValuePath = "Value" };
            foreach (var p in _availablePresets.Where(x => x.virtualAudioDevice == "game"))
            {
                cbPreset.Items.Add(new PresetComboBoxItem { Text = p.name, Value = p.id });
            }
            if (!string.IsNullOrEmpty(currentPresetId))
                SelectComboBoxByValue(cbPreset, currentPresetId);
            Grid.SetColumn(cbPreset, 2);
            grid.Children.Add(cbPreset);

            // Delete Button
            var btnDel = new Button
            {
                Content = "🗑",
                Style = (Style)FindResource("DangerButton"),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(12, 0, 0, 0),
                Cursor = Cursors.Hand,
                ToolTip = Loc.Get("BtnDeleteRule")
            };
            btnDel.Click += (s, e) =>
            {
                _context.Config.Rules.Remove(originalKey);
                _context.SaveConfig();
                _context.ReloadConfig();
                RenderRulesList();
            };
            Grid.SetColumn(btnDel, 3);
            grid.Children.Add(btnDel);

            // Save logic
            Action saveRule = () =>
            {
                string newKey = ExeNameHelper.ExtractExeName(cbExe.Text);

                var selectedPreset = cbPreset.SelectedItem as PresetComboBoxItem;

                if (string.IsNullOrEmpty(newKey) || selectedPreset == null) return;

                // Hiçbir şey değişmediyse gereksiz kayıt/reload yapma.
                // Not: karşılaştırma config'deki GÜNCEL değerle yapılır; eskiden satır oluşturulurken yakalanan
                // ilk değerle yapılıyordu ve A→B→A geri dönüşü hiç kaydedilmiyordu.
                bool keyChanged = !newKey.Equals(originalKey, StringComparison.OrdinalIgnoreCase);
                _context.Config.Rules.TryGetValue(originalKey, out string? savedPresetId);
                bool presetChanged = selectedPreset.Value != savedPresetId;
                if (!keyChanged && !presetChanged) return;

                // Başka bir kuralın exe'sine çevrilirse o kural sessizce eziliyordu → engelle ve satırı eski haline getir
                if (keyChanged && _context.Config.Rules.ContainsKey(newKey))
                {
                    MessageBox.Show(Loc.Format("RuleExists", newKey), Loc.Get("Warning"),
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                    Dispatcher.BeginInvoke(new Action(RenderRulesList));
                    return;
                }

                if (keyChanged)
                {
                    _context.Config.Rules.Remove(originalKey);
                    if (!_context.Config.DiscoveredGames.Contains(newKey, StringComparer.OrdinalIgnoreCase))
                        _context.Config.DiscoveredGames.Add(newKey);
                }

                _context.Config.Rules[newKey] = selectedPreset.Value;
                _context.SaveConfig();
                _context.ReloadConfig();

                if (keyChanged)
                {
                    RenderRulesList();
                }
            };

            cbExe.SelectionChanged += (s, e) => saveRule();
            cbPreset.SelectionChanged += (s, e) => saveRule();

            rowBorder.Child = grid;
            return rowBorder;
        }

        private UIElement BuildAddRow()
        {
            var rowBorder = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(0, 0, 0, 0),
                Padding = new Thickness(0, 12, 0, 12)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cbExe = new ComboBox { IsEditable = false };
            foreach (var exe in GetKnownExeNames()) cbExe.Items.Add(exe);
            Grid.SetColumn(cbExe, 0);
            grid.Children.Add(cbExe);

            var arrow = new TextBlock
            {
                Text = "→",
                Foreground = (Brush)FindResource("TxtSecond"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(arrow, 1);
            grid.Children.Add(arrow);

            var cbPreset = new ComboBox { DisplayMemberPath = "Text", SelectedValuePath = "Value" };
            foreach (var p in _availablePresets.Where(x => x.virtualAudioDevice == "game"))
            {
                cbPreset.Items.Add(new PresetComboBoxItem { Text = p.name, Value = p.id });
            }
            Grid.SetColumn(cbPreset, 2);
            grid.Children.Add(cbPreset);

            var btnAdd = new Button
            {
                Content = "+ " + Loc.Get("BtnAddManual"),
                Style = (Style)FindResource("AccentButton"),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(12, 0, 0, 0),
                Cursor = Cursors.Hand,
                ToolTip = Loc.Get("BtnAddRule")
            };

            Action addRule = () =>
            {
                string newKey = ExeNameHelper.ExtractExeName(cbExe.Text);

                var selectedPreset = cbPreset.SelectedItem as PresetComboBoxItem;

                if (string.IsNullOrEmpty(newKey) || selectedPreset == null) return;

                _context.Config.Rules[newKey] = selectedPreset.Value;
                if (!_context.Config.DiscoveredGames.Contains(newKey, StringComparer.OrdinalIgnoreCase))
                    _context.Config.DiscoveredGames.Add(newKey);

                _context.SaveConfig();
                _context.ReloadConfig();
                RenderRulesList(); // Re-render everything to convert this row to a normal rule
            };

            btnAdd.Click += (s, e) => addRule();
            Grid.SetColumn(btnAdd, 3);
            grid.Children.Add(btnAdd);

            rowBorder.Child = grid;
            return rowBorder;
        }

        // ══════════════════════════════════════════════════════════════
        //  UI Event Handlers – Profiles Page
        // ══════════════════════════════════════════════════════════════

        private void CbDefaultPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPresetSave) return;
            if (cbDefaultPreset.SelectedItem is PresetComboBoxItem item &&
                item.Value != _context.Config.DefaultPresetId)
            {
                _context.Config.DefaultPresetId = item.Value;
                _context.SaveConfig();
                _context.ReloadConfig();
            }
        }

        private async void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            lblScanBtn.Text = Loc.Get("Scanning");
            btnScan.IsEnabled = false;

            try
            {
                var games = await Task.Run(GameScanner.ScanAllGames);

                int added = 0;
                foreach (var g in games)
                {
                    if (!_context.Config.DiscoveredGames.Contains(g.ExeName, StringComparer.OrdinalIgnoreCase))
                    {
                        _context.Config.DiscoveredGames.Add(g.ExeName);
                        added++;
                    }
                    if (!string.IsNullOrEmpty(g.GameName))
                    {
                        _context.Config.DiscoveredGameNames[g.ExeName] = g.GameName;
                    }
                }
                _context.SaveConfig();
                RenderRulesList();

                if (added > 0)
                {
                    txtActivePreset.Text = Loc.Format("ScanDone", added);
                    ledActive.Fill = new SolidColorBrush(Color.FromRgb(78, 201, 126));
                }
                else
                {
                    txtActivePreset.Text = Loc.Get("ScanNone");
                    ledActive.Fill = new SolidColorBrush(Color.FromRgb(120, 120, 160));
                }
            }
            catch (Exception ex)
            {
                txtActivePreset.Text = ex.Message;
            }
            finally
            {
                // Hata olsa bile buton tekrar kullanılabilir olmalı (eskiden kalıcı olarak devre dışı kalabiliyordu)
                lblScanBtn.Text = Loc.Get("BtnScan");
                btnScan.IsEnabled = true;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Events – Settings Page
        // ══════════════════════════════════════════════════════════════
        private void AutoSaveSettings()
        {
            if (!IsLoaded || _context == null || _isPopulatingControls) return;
            ApplySettingsFromUi();
        }

        private void ApplySettingsFromUi()
        {
            // 0, negatif veya çok küçük değer watcher Timer'ını bozuyordu (0 = tek sefer, negatif = exception)
            if (int.TryParse(txtInterval.Text, out int interval))
                _context.Config.CheckIntervalMilliseconds =
                    Math.Clamp(interval, AppConfig.MinCheckIntervalMs, AppConfig.MaxCheckIntervalMs);
            txtInterval.Text = _context.Config.CheckIntervalMilliseconds.ToString();

            _context.Config.StartWithWindows = chkStartup.IsChecked == true;

            if (cbLanguage.SelectedItem is ComboBoxItem langItem)
                _context.Config.Language = langItem.Tag?.ToString() ?? "TR";

            _context.SaveConfig();
            _context.ReloadConfig();
        }

        private void Setting_Changed(object sender, RoutedEventArgs e) 
        {
            AutoSaveSettings();
        }

        private void TxtInterval_LostFocus(object sender, RoutedEventArgs e)
        {
            AutoSaveSettings();
        }

        private void LoadAudioDevices()
        {
            var allDevices = AudioDeviceEnforcer.GetAllAudioDevices();
            var vmList = new List<AudioDeviceViewModel>();
            foreach (var (id, name, isRender) in allDevices)
            {
                string icon = isRender ? "🔊" : "🎤";
                bool isDisabled = _context.Config.DisabledDevices.Contains(id) ||
                                  AudioDeviceEnforcer.MatchesAnyName(name, _context.Config.DisabledDeviceNames);

                vmList.Add(new AudioDeviceViewModel
                {
                    Id           = id,
                    FriendlyName = $"{icon} {name}",
                    IsDisabled   = isDisabled,
                    WasDisabled  = isDisabled
                });
            }
            listAudioDevices.ItemsSource = vmList;
        }

        private async void BtnSaveDevices_Click(object sender, RoutedEventArgs e)
        {
            // Gecikme değerini de kaydet
            if (int.TryParse(txtDeviceDelay.Text, out int delayVal))
                _context.Config.DeviceDisableDelaySeconds = Math.Clamp(delayVal, 0, 300);

            if (listAudioDevices.ItemsSource is List<AudioDeviceViewModel> vmList)
            {
                // Yeni listeler oluşturulup tek seferde atanır: arka plandaki denetim zamanlayıcısı
                // eski listeyi gezerken Clear/Add yapılması "Collection was modified" hatasına yol açıyordu.
                var ids = new List<string>();
                var names = new List<string>();

                foreach (var vm in vmList)
                {
                    if (vm.IsDisabled)
                    {
                        ids.Add(vm.Id);
                        string cleanName = AudioDeviceEnforcer.CleanDeviceName(vm.FriendlyName);
                        if (!string.IsNullOrEmpty(cleanName) && !names.Contains(cleanName, StringComparer.OrdinalIgnoreCase))
                        {
                            names.Add(cleanName);
                        }
                    }
                }

                _context.Config.DisabledDevices = ids;
                _context.Config.DisabledDeviceNames = names;
                _context.SaveConfig();

                // Yalnızca durumu değişen cihazlara dokunulur. Eskiden işaretsiz TÜM cihazlar görünür yapılıyordu;
                // kullanıcının Windows'ta kendisinin devre dışı bıraktığı cihazlar da yeniden açılıyordu.
                // İşaretli olanlar her seferinde tekrar gizlenir (GG güncellemesiyle geri gelmiş olabilirler).
                var changes = vmList.Where(vm => vm.IsDisabled || vm.WasDisabled)
                                    .Select(vm => (vm.Id, vm.IsDisabled)).ToList();
                foreach (var vm in vmList) vm.WasDisabled = vm.IsDisabled;

                // COM çağrıları cihaz sayısına göre zaman alabilir → UI'yı dondurmamak için arka planda
                btnSaveDevices.IsEnabled = false;
                try
                {
                    await Task.Run(() =>
                    {
                        foreach (var (id, disabled) in changes)
                            AudioDeviceEnforcer.SetDeviceState(id, disabled);
                    });
                }
                finally
                {
                    btnSaveDevices.IsEnabled = true;
                }

                txtSavedDevice.Visibility = Visibility.Visible;
                await System.Threading.Tasks.Task.Delay(3000);
                txtSavedDevice.Visibility = Visibility.Collapsed;
            }
        }

        private void TxtDeviceDelay_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtDeviceDelay.Text, out int val))
            {
                val = Math.Clamp(val, 0, 300);
                txtDeviceDelay.Text = val.ToString();
                _context.Config.DeviceDisableDelaySeconds = val;
                _context.SaveConfig();
            }
            else
            {
                txtDeviceDelay.Text = _context.Config.DeviceDisableDelaySeconds.ToString();
            }
        }

        private void CbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_context == null || _isPopulatingControls) return;
            if (cbLanguage.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                string lang = item.Tag.ToString() ?? "TR";
                bool changed = lang != Loc.CurrentLang;
                Loc.CurrentLang = lang;
                _context.Config.Language = lang;
                ApplyLanguage();
                AutoSaveSettings();

                if (changed && IsLoaded)
                {
                    // Dinamik oluşturulan satırlar, sensör adları ve tepsi menüsü de yeni dile geçsin
                    RenderRulesList();
                    _lastCategoryStructureKey = "";
                    _lastFavoritesKey = "";
                    _context.UpdateTrayMenu();
                }
            }
        }

        private async void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            ApplySettingsFromUi();

            // Brief feedback on button
            lblSaveBtn.Text = Loc.Get("SavedSuccess");
            await Task.Delay(1500);
            lblSaveBtn.Text = Loc.Get("BtnSaveSettings");
        }

        private void BtnClearGames_Click(object sender, RoutedEventArgs e)
        {
            _context.Config.DiscoveredGames.Clear();
            _context.Config.DiscoveredGameNames.Clear();
            _context.SaveConfig();
            RenderRulesList();
            MessageBox.Show(Loc.Get("ClearedSuccess"), Loc.Get("ClearedSuccessTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnBrowseExe_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Executable Files|*.exe",
                Title = Loc.Get("ManualAdd") ?? "Select EXE File",
                CheckFileExists = false,
                CheckPathExists = false,
                ValidateNames = false
            };

            if (dialog.ShowDialog() == true)
            {
                txtManualExe.Text = System.IO.Path.GetFileName(dialog.FileName);
                if (string.IsNullOrEmpty(txtManualName.Text))
                {
                    txtManualName.Text = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
                }
            }
        }

        private void BtnAddManual_Click(object sender, RoutedEventArgs e)
        {
            string exe = txtManualExe.Text.Trim();
            string name = txtManualName.Text.Trim();

            if (string.IsNullOrEmpty(exe)) return;

            exe = System.IO.Path.GetFileName(exe); // Tam yol yapıştırılırsa yalnızca dosya adını al
            if (string.IsNullOrEmpty(exe)) return;
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                exe += ".exe";

            if (!_context.Config.DiscoveredGames.Contains(exe, StringComparer.OrdinalIgnoreCase))
                _context.Config.DiscoveredGames.Add(exe);

            if (!string.IsNullOrEmpty(name))
                _context.Config.DiscoveredGameNames[exe] = name;
            else if (!_context.Config.DiscoveredGameNames.ContainsKey(exe))
                _context.Config.DiscoveredGameNames[exe] = "";

            _context.SaveConfig();
            RenderRulesList();
            
            txtManualExe.Text = "";
            txtManualName.Text = "";
            
            MessageBox.Show(Loc.Get("ManualSuccess"), Loc.Get("Info"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ══════════════════════════════════════════════════════════════
        //  GPU UI Events
        // ══════════════════════════════════════════════════════════════
        private void GpuSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _context == null || _isPopulatingControls) return;

            if (double.TryParse(txtTargetMhz.Text, out double mhz) && mhz >= 0 && !double.IsInfinity(mhz))
                _context.Config.TargetMhz = mhz;
            else
                txtTargetMhz.Text = _context.Config.TargetMhz.ToString(System.Globalization.CultureInfo.CurrentCulture);

            if (cbTargetProfile.SelectedItem is ComboBoxItem profileItem && int.TryParse(profileItem.Tag?.ToString(), out int profile))
                _context.Config.TargetProfile = profile;

            if (cbCooldown.SelectedItem is ComboBoxItem cooldownItem && int.TryParse(cooldownItem.Tag?.ToString(), out int cd))
                _context.Config.CooldownSeconds = cd;

            _context.SaveConfig();
        }

        public void UpdateGpuData(GpuData data)
        {
            // Pencere tepsideyken (çoğu zaman) görünmeyen metinleri 2 sn'de bir güncelleme; gösterilince son veri basılır
            _lastGpuData = data;
            if (!IsVisible) return;

            txtGpuClock.Text = Math.Round(data.CoreClockMhz).ToString();
            txtGpuTemp.Text = data.TemperatureCelsius.ToString();
            txtGpuUsage.Text = data.UsagePercentage.ToString();

            if (_context.Config.TargetMhz > 0 && data.CoreClockMhz > _context.Config.TargetMhz)
                txtGpuClock.Foreground = (Brush)FindResource("Red");
            else
                txtGpuClock.Foreground = (Brush)FindResource("TxtPrimary");
        }

        // ══════════════════════════════════════════════════════════════
        //  Localization
        // ══════════════════════════════════════════════════════════════
        private void ApplyLanguage()
        {
            // Title Bar & Chrome
            txtOnlineBadge.Text         = Loc.Get("Online");
            btnMinimizeTitle.ToolTip    = Loc.Get("ToolTipMinimize");
            btnMaximizeTitle.ToolTip    = Loc.Get("ToolTipMaximize");
            btnCloseTitle.ToolTip       = Loc.Get("ToolTipClose");

            // Sidebar Navigation Tooltips
            btnNavHome.ToolTip          = Loc.Get("NavHome");
            btnNavDevice.ToolTip        = Loc.Get("NavDeviceManager");
            btnNavGpu.ToolTip           = Loc.Get("NavGpuMonitor");
            btnNavTelemetry.ToolTip     = Loc.Get("NavTelemetry");
            btnNavSettings.ToolTip      = Loc.Get("NavSettings");

            // Profiles Page
            lblPageHeader.Text          = Loc.Get("RulesHeader");
            lblPageDesc.Text            = Loc.Get("RulesDesc");
            lblDefaultConfig.Text       = Loc.Get("DefaultEQ");
            lblDefaultConfigDesc.Text   = Loc.Get("DefaultEQDesc");
            lblPerApp.Text              = Loc.Get("PerAppConfig");
            lblTargetPresetHeader.Text  = Loc.Get("TargetSonarConfig");
            lblScanBtn.Text             = Loc.Get("BtnScan");
            lblBtnClear.Text            = Loc.Get("BtnClear");
            lblManualAdd.Text           = Loc.Get("ManualAdd");
            lblBtnAddManual.Text        = Loc.Get("BtnAddManual");
            btnBrowseExe.ToolTip        = Loc.Get("ManualBrowse");
            
            // Device Manager
            lblDeviceManagerHeader.Text = Loc.Get("DeviceManagerHeader");
            lblDeviceManagerDesc.Text   = Loc.Get("DeviceManagerDesc");
            lblDeviceDelay.Text         = Loc.Get("DeviceDelayTitle");
            btnSaveDevices.Content      = Loc.Get("BtnSaveAndApply");
            txtSavedDevice.Text         = Loc.Get("SavedSuccess");

            // GPU Monitor
            lblGpuMonitorHeader.Text    = Loc.Get("GpuMonitorHeader");
            lblGpuMonitorDesc.Text      = Loc.Get("GpuMonitorDesc");
            lblGpuCoreClock.Text        = Loc.Get("GpuCoreClock");
            lblGpuTemperature.Text      = Loc.Get("GpuTemperature");
            lblGpuUsage.Text            = Loc.Get("GpuUsage");
            lblGpuSettingsHeader.Text   = Loc.Get("GpuSettingsHeader");
            lblGpuLimitTitle.Text       = Loc.Get("GpuLimitTitle");
            lblGpuLimitDesc.Text        = Loc.Get("GpuLimitDesc");
            lblGpuProfileTitle.Text     = Loc.Get("GpuProfileTitle");
            lblGpuProfileDesc.Text      = Loc.Get("GpuProfileDesc");
            lblGpuCooldownTitle.Text    = Loc.Get("GpuCooldownTitle");
            lblGpuCooldownDesc.Text     = Loc.Get("GpuCooldownDesc");
            
            // Settings Page
            lblSettingsHeader.Text      = Loc.Get("SettingsHeader");
            lblSettingsDesc.Text        = Loc.Get("SettingsDesc");
            lblGeneralSection.Text      = Loc.Get("GeneralSection");
            lblStartWithWin.Text        = Loc.Get("StartWithWin");
            lblStartWithWinDesc.Text    = Loc.Get("StartWithWinDesc");
            lblLanguage.Text            = Loc.Get("Language");
            lblScanInterval.Text        = Loc.Get("ScanInterval");
            lblScanIntervalDesc.Text    = Loc.Get("ScanIntervalDesc");
            lblSaveBtn.Text             = Loc.Get("BtnSaveSettings");

            // Telemetry
            lblTelemetryHeader.Text     = Loc.Get("TelemetryHeader");
            lblTelemetryDesc.Text       = Loc.Get("TelemetryDesc");
            lblFavoritesTitle.Text      = Loc.Get("FavoritesTitle");
            lblLiveGraphTitle.Text      = Loc.Get("LiveGraphTitle");
            txtNoGraphHint.Text         = Loc.Get("NoGraphSensorsHint");
            lblGraphNow.Text            = Loc.Get("GraphNow");
            runPawnIoHint.Text          = Loc.Get("PawnIoMissing") + " ";

            foreach (ComboBoxItem item in cbTargetProfile.Items)
                item.Content = Loc.Format("ProfileN", item.Tag);
            foreach (ComboBoxItem item in cbCooldown.Items)
                item.Content = Loc.Format("SecondsN", item.Tag);
        }

        // ══════════════════════════════════════════════════════════════
        //  Hardware Telemetry & Monitor Logic
        // ══════════════════════════════════════════════════════════════
        private void StartTelemetry()
        {
            if (_hardwareMonitor == null) return;

            if (!_telemetryInitialized)
            {
                _telemetryInitialized = true;
                pnlPawnIoHint.Visibility = HardwareMonitorService.IsPawnIoInstalled ? Visibility.Collapsed : Visibility.Visible;
                _hardwareMonitor.Initialize(_context.Config.TelemetryFavorites, _context.Config.TelemetryGraphSensors);
            }
            _hardwareMonitor.Start(1000);
        }

        private void LnkPawnIo_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            // Warden yönetici olarak çalışır; tarayıcıyı doğrudan başlatmak onu da yönetici yapar.
            // explorer.exe isteği mevcut (yükseltilmemiş) kabuğa devreder.
            try
            {
                using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{e.Uri.AbsoluteUri}\"",
                    UseShellExecute = false
                });
            }
            catch { }
            e.Handled = true;
        }

        private void StopTelemetry()
        {
            _hardwareMonitor?.Stop();
        }

        private void OnTelemetryUpdated(object? sender, TelemetrySnapshot snapshot)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<object?, TelemetrySnapshot>(OnTelemetryUpdated), sender, snapshot);
                return;
            }

            if (!IsVisible || _currentPage != "telemetry") return;

            _lastTelemetrySnapshot = snapshot;
            RenderFavorites(snapshot.Favorites);
            RenderGraph(snapshot.GraphSensors);
            RenderCategories(snapshot.Categories);
        }

        private void RenderFavorites(List<TelemetrySensorItem> favorites)
        {
            if (favorites.Count == 0)
            {
                pnlFavoritesContainer.Visibility = Visibility.Collapsed;
                _lastFavoritesKey = "";
                _favoriteValControls.Clear();
                return;
            }

            pnlFavoritesContainer.Visibility = Visibility.Visible;

            string currentKey = string.Join(",", favorites.Select(f => f.Id));
            if (currentKey == _lastFavoritesKey && _favoriteValControls.Count == favorites.Count)
            {
                foreach (var item in favorites)
                {
                    if (_favoriteValControls.TryGetValue(item.Id, out var txt))
                    {
                        txt.Text = item.FormattedValue;
                    }
                }
                return;
            }

            _lastFavoritesKey = currentKey;
            _favoriteValControls.Clear();
            wpFavorites.Children.Clear();

            foreach (var item in favorites)
            {
                var card = new Border
                {
                    Background = BrushFrom("#081517"),
                    BorderBrush = BrushFrom("#16363B"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 10, 10),
                    MinWidth = 150
                };

                var sp = new StackPanel();

                var topGrid = new Grid();
                topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var badgeBorder = new Border
                {
                    Background = BrushFrom("#103035"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                var badgeText = new TextBlock
                {
                    Text = item.Category,
                    Foreground = (Brush)FindResource("Accent"),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold
                };
                badgeBorder.Child = badgeText;
                Grid.SetColumn(badgeBorder, 0);
                topGrid.Children.Add(badgeBorder);

                var btnStar = new Button
                {
                    Content = "★",
                    Foreground = BrushFrom("#FACC15"),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    FontSize = 13,
                    ToolTip = Loc.Get("UnpinFromFavorites"),
                    Padding = new Thickness(0),
                    Tag = item.Id
                };
                btnStar.Click += (s, e) =>
                {
                    if (s is Button b && b.Tag is string id)
                    {
                        ToggleFavorite(id);
                    }
                };
                Grid.SetColumn(btnStar, 1);
                topGrid.Children.Add(btnStar);
                sp.Children.Add(topGrid);

                var txtName = new TextBlock
                {
                    Text = item.Name,
                    Foreground = (Brush)FindResource("TxtSecond"),
                    FontSize = 11,
                    Margin = new Thickness(0, 4, 0, 2),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                sp.Children.Add(txtName);

                var txtVal = new TextBlock
                {
                    Text = item.FormattedValue,
                    Foreground = (Brush)FindResource("TxtPrimary"),
                    FontSize = 16,
                    FontWeight = FontWeights.Bold
                };
                sp.Children.Add(txtVal);
                _favoriteValControls[item.Id] = txtVal;

                card.Child = sp;
                wpFavorites.Children.Add(card);
            }
        }

        // Grafik legend'ı ve çizgileri her saniye sıfırdan kurulmaz: sensör kümesi değişmedikçe mevcut
        // kontroller güncellenir. Eskiden ✕ butonu her tick'te yok edilip yeniden oluşturulduğu için
        // tıklama, yenilemeye denk gelince boşa gidiyordu.
        private readonly Dictionary<string, (TextBlock Text, System.Windows.Shapes.Rectangle Dot)> _legendControls = new();
        private readonly Dictionary<string, (System.Windows.Shapes.Polyline Line, System.Windows.Shapes.Ellipse Dot)> _chartShapes = new();
        private string _lastLegendKey = "";

        private void RenderGraph(List<TelemetrySensorItem> graphSensors)
        {
            if (graphSensors.Count == 0)
            {
                txtNoGraphHint.Visibility = Visibility.Visible;
                wpGraphLegend.Children.Clear();
                cvsChart.Children.Clear();
                _legendControls.Clear();
                _chartShapes.Clear();
                _lastLegendKey = "";
                return;
            }

            txtNoGraphHint.Visibility = Visibility.Collapsed;

            string key = string.Join(",", graphSensors.Select(s => s.Id));
            if (key != _lastLegendKey || _legendControls.Count != graphSensors.Count)
            {
                _lastLegendKey = key;
                RebuildLegend(graphSensors);
            }
            else
            {
                foreach (var s in graphSensors)
                {
                    if (!_legendControls.TryGetValue(s.Id, out var c)) continue;
                    c.Text.Text = $"{s.Name}: {s.FormattedValue}";
                    c.Dot.Fill = BrushFrom(s.GraphColor);
                }
            }

            DrawChartLines(graphSensors);
        }

        private void RebuildLegend(List<TelemetrySensorItem> graphSensors)
        {
            wpGraphLegend.Children.Clear();
            _legendControls.Clear();

            foreach (var s in graphSensors)
            {
                var badge = new Border
                {
                    Background = BrushFrom("#0A1618"),
                    BorderBrush = BrushFrom("#183338"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 0, 8, 4)
                };

                var sp = new StackPanel { Orientation = Orientation.Horizontal };

                var colorDot = new System.Windows.Shapes.Rectangle
                {
                    Width = 8,
                    Height = 8,
                    RadiusX = 2,
                    RadiusY = 2,
                    Margin = new Thickness(0, 0, 6, 0),
                    Fill = BrushFrom(s.GraphColor),
                    VerticalAlignment = VerticalAlignment.Center
                };
                sp.Children.Add(colorDot);

                var txt = new TextBlock
                {
                    Text = $"{s.Name}: {s.FormattedValue}",
                    Foreground = BrushFrom("#C5DCDE"),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                sp.Children.Add(txt);

                var btnRemove = new Button
                {
                    Content = "✕",
                    Foreground = BrushFrom("#607B80"),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(6, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    FontSize = 10,
                    Tag = s.Id,
                    VerticalAlignment = VerticalAlignment.Center
                };
                btnRemove.Click += (sender, e) =>
                {
                    if (sender is Button b && b.Tag is string id)
                    {
                        ToggleGraph(id);
                    }
                };
                sp.Children.Add(btnRemove);

                badge.Child = sp;
                wpGraphLegend.Children.Add(badge);
                _legendControls[s.Id] = (txt, colorDot);
            }
        }

        private void DrawChartLines(List<TelemetrySensorItem> graphSensors)
        {
            double width = cvsChart.ActualWidth;
            double height = cvsChart.ActualHeight;
            if (width <= 20 || height <= 20) return;

            // Grafikten çıkarılan sensörlerin şekillerini kaldır
            var activeIds = new HashSet<string>(graphSensors.Select(s => s.Id));
            foreach (var staleId in _chartShapes.Keys.Where(id => !activeIds.Contains(id)).ToList())
            {
                cvsChart.Children.Remove(_chartShapes[staleId].Line);
                cvsChart.Children.Remove(_chartShapes[staleId].Dot);
                _chartShapes.Remove(staleId);
            }

            // Her birim (°C, MHz, W, % ...) kendi ölçeğinde çizilir. Eskiden tek ortak eksen vardı:
            // grafiğe 4500 MHz eklenince 60 °C'lik sıcaklık çizgisi tabana yapışıyordu.
            var unitRange = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var s in graphSensors)
            {
                // Yüzde ve sıcaklık için en az 0-100 aralığı: küçük dalgalanmalar abartılı görünmesin
                float max = s.Unit is "%" or "°C" ? 100f : 1f;
                foreach (var v in s.History)
                {
                    if (v > max) max = v;
                }
                unitRange[s.Unit] = unitRange.TryGetValue(s.Unit, out float existing) ? Math.Max(existing, max) : max;
            }
            foreach (var unit in unitRange.Keys.ToList())
            {
                float max = unitRange[unit] * 1.1f;
                float step = max > 1000 ? 100f : max > 100 ? 10f : 1f;
                unitRange[unit] = Math.Max(1f, (float)Math.Ceiling(max / step) * step);
            }

            const float minVal = 0f;
            double stepX = width / 59.0;

            foreach (var s in graphSensors)
            {
                if (!_chartShapes.TryGetValue(s.Id, out var shapes))
                {
                    shapes = (
                        new System.Windows.Shapes.Polyline { StrokeThickness = 2.0, StrokeLineJoin = PenLineJoin.Round },
                        new System.Windows.Shapes.Ellipse { Width = 6, Height = 6 });
                    cvsChart.Children.Add(shapes.Line);
                    cvsChart.Children.Add(shapes.Dot);
                    _chartShapes[s.Id] = shapes;
                }

                int count = s.History.Count;
                if (count < 2)
                {
                    shapes.Line.Visibility = Visibility.Collapsed;
                    shapes.Dot.Visibility = Visibility.Collapsed;
                    continue;
                }

                float range = unitRange[s.Unit] - minVal;
                var strokeBrush = BrushFrom(s.GraphColor);
                var points = new PointCollection(count);
                Point lastPoint = new Point();
                for (int i = 0; i < count; i++)
                {
                    float val = s.History[i];
                    double x = width - (count - 1 - i) * stepX;
                    double y = height - ((val - minVal) / range) * (height - 16) - 8;
                    if (y < 4) y = 4;
                    if (y > height - 4) y = height - 4;

                    lastPoint = new Point(x, y);
                    points.Add(lastPoint);
                }
                points.Freeze();

                shapes.Line.Points = points;
                shapes.Line.Stroke = strokeBrush;
                shapes.Line.Visibility = Visibility.Visible;

                shapes.Dot.Fill = strokeBrush;
                shapes.Dot.Visibility = Visibility.Visible;
                Canvas.SetLeft(shapes.Dot, lastPoint.X - 3);
                Canvas.SetTop(shapes.Dot, lastPoint.Y - 3);
            }
        }

        private void RenderCategories(Dictionary<string, List<TelemetrySensorItem>> categories)
        {
            string currentKey = string.Join(";", categories.Select(kv => kv.Key + ":" + string.Join(",", kv.Value.Select(i => i.Id))));
            if (currentKey == _lastCategoryStructureKey && _categoryRowControls.Count > 0)
            {
                foreach (var kv in categories)
                {
                    foreach (var item in kv.Value)
                    {
                        if (_categoryRowControls.TryGetValue(item.Id, out var row))
                        {
                            row.txtVal.Text = item.FormattedValue;
                            row.btnStar.Content = item.IsFavorite ? "★" : "☆";
                            row.btnStar.Foreground = BrushFrom(item.IsFavorite ? "#FACC15" : "#3E565B");
                            row.btnStar.ToolTip = item.IsFavorite ? Loc.Get("UnpinFromFavorites") : Loc.Get("PinToFavorites");
                            row.btnGraph.Foreground = BrushFrom(item.IsOnGraph ? item.GraphColor : "#3E565B");
                            row.btnGraph.ToolTip = item.IsOnGraph ? Loc.Get("RemoveFromGraph") : Loc.Get("PlotOnGraph");
                        }
                    }
                }
                return;
            }

            _lastCategoryStructureKey = currentKey;
            _categoryRowControls.Clear();
            pnlCategoriesContainer.Children.Clear();

            string[] catOrder = { "CPU", "GPU", "Fans", "Motherboard", "Memory" };
            foreach (var catKey in catOrder)
            {
                if (!categories.TryGetValue(catKey, out var items) || items.Count == 0)
                    continue;

                string catTitle = catKey switch
                {
                    "CPU" => $"💻 {Loc.Get("CategoryCpu")} — {items.FirstOrDefault()?.HardwareName ?? ""}",
                    "GPU" => $"🎮 {Loc.Get("CategoryGpu")} — {items.FirstOrDefault()?.HardwareName ?? ""}",
                    "Fans" => $"🌀 {Loc.Get("CategoryFans")}",
                    "Motherboard" => $"⚡ {Loc.Get("CategoryMotherboard")}",
                    "Memory" => $"💾 {Loc.Get("CategoryMemory")}",
                    _ => catKey
                };

                var card = new Border
                {
                    Background = (Brush)FindResource("BgCard"),
                    BorderBrush = (Brush)FindResource("BorderLight"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(16, 12, 16, 12),
                    Margin = new Thickness(0, 0, 0, 14)
                };

                var sp = new StackPanel();

                // Category Header
                var headerText = new TextBlock
                {
                    Text = catTitle,
                    Foreground = (Brush)FindResource("TxtPrimary"),
                    FontWeight = FontWeights.Bold,
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                sp.Children.Add(headerText);

                // Sensor Rows
                foreach (var item in items)
                {
                    var rowGrid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    // Sensor Name
                    var txtName = new TextBlock
                    {
                        Text = item.Name,
                        Foreground = (Brush)FindResource("TxtSecond"),
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(txtName, 0);
                    rowGrid.Children.Add(txtName);

                    // Formatted Value
                    var txtVal = new TextBlock
                    {
                        Text = item.FormattedValue,
                        Foreground = (Brush)FindResource("TxtPrimary"),
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 13,
                        Margin = new Thickness(12, 0, 16, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(txtVal, 1);
                    rowGrid.Children.Add(txtVal);

                    // Actions Panel (Star + Graph)
                    var actionsSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                    // Star Button
                    var btnStar = new Button
                    {
                        Content = item.IsFavorite ? "★" : "☆",
                        Foreground = item.IsFavorite 
                            ? BrushFrom("#FACC15") 
                            : BrushFrom("#3E565B"),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        FontSize = 14,
                        Margin = new Thickness(0, 0, 8, 0),
                        ToolTip = item.IsFavorite ? Loc.Get("UnpinFromFavorites") : Loc.Get("PinToFavorites"),
                        Tag = item.Id
                    };
                    btnStar.Click += (s, e) =>
                    {
                        if (s is Button b && b.Tag is string id)
                        {
                            ToggleFavorite(id);
                        }
                    };
                    actionsSp.Children.Add(btnStar);

                    // Graph Button
                    var btnGraph = new Button
                    {
                        Content = "📈",
                        Foreground = item.IsOnGraph
                            ? BrushFrom(item.GraphColor)
                            : BrushFrom("#3E565B"),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        FontSize = 13,
                        ToolTip = item.IsOnGraph ? Loc.Get("RemoveFromGraph") : Loc.Get("PlotOnGraph"),
                        Tag = item.Id
                    };
                    btnGraph.Click += (s, e) =>
                    {
                        if (s is Button b && b.Tag is string id)
                        {
                            ToggleGraph(id);
                        }
                    };
                    actionsSp.Children.Add(btnGraph);

                    Grid.SetColumn(actionsSp, 2);
                    rowGrid.Children.Add(actionsSp);

                    sp.Children.Add(rowGrid);
                }

                card.Child = sp;
                pnlCategoriesContainer.Children.Add(card);
            }
        }

        private void ToggleFavorite(string sensorId)
        {
            if (_hardwareMonitor == null) return;
            _hardwareMonitor.ToggleFavorite(sensorId);

            _context.Config.TelemetryFavorites = _hardwareMonitor.GetFavoriteIds();
            _context.SaveConfig();
        }

        private void ToggleGraph(string sensorId)
        {
            if (_hardwareMonitor == null) return;
            _hardwareMonitor.ToggleGraph(sensorId);

            _context.Config.TelemetryGraphSensors = _hardwareMonitor.GetGraphSensorIds();
            _context.SaveConfig();
        }

        private void CvsChart_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawChartGrid();
            if (_lastTelemetrySnapshot != null && _currentPage == "telemetry")
            {
                DrawChartLines(_lastTelemetrySnapshot.GraphSensors);
            }
        }

        private void DrawChartGrid()
        {
            double width = cvsChartGrid.ActualWidth;
            double height = cvsChartGrid.ActualHeight;
            if (width <= 10 || height <= 10) return;

            cvsChartGrid.Children.Clear();

            for (int i = 1; i <= 3; i++)
            {
                double y = height * (i / 4.0);
                var line = new System.Windows.Shapes.Line
                {
                    X1 = 0,
                    Y1 = y,
                    X2 = width,
                    Y2 = y,
                    Stroke = BrushFrom("#0E1D20"),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 3 }
                };
                cvsChartGrid.Children.Add(line);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Thread-safe callbacks from SonarWatcher
        // ══════════════════════════════════════════════════════════════
        public void UpdateActivePreset(string ruleName, string presetName)
        {
            if (!Dispatcher.CheckAccess())
            { Dispatcher.BeginInvoke(new Action<string, string>(UpdateActivePreset), ruleName, presetName); return; }

            bool isGame = ruleName != "Desktop";
            string label = isGame
                ? $"{ruleName}  →  {presetName}"
                : $"{Loc.Get("Desktop")} ({presetName})";

            txtActivePreset.Text       = label;
            txtActivePreset.Foreground = isGame
                ? new SolidColorBrush(Color.FromRgb(78, 201, 126))
                : new SolidColorBrush(Color.FromRgb(120, 120, 160));
            ledActive.Fill = isGame
                ? new SolidColorBrush(Color.FromRgb(78, 201, 126))
                : new SolidColorBrush(Color.FromRgb(120, 120, 160));
        }

        // ══════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════
        private static void SelectComboBoxByValue(ComboBox cb, string value)
        {
            for (int i = 0; i < cb.Items.Count; i++)
            {
                if (cb.Items[i] is PresetComboBoxItem item && item.Value == value)
                { cb.SelectedIndex = i; return; }
            }
        }
    }

    public class PresetComboBoxItem
    {
        public string Text  { get; set; } = "";
        public string Value { get; set; } = "";
        public override string ToString() => Text;
    }

    public class AudioDeviceViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string FriendlyName { get; set; } = string.Empty;
        public bool IsDisabled { get; set; }
        public bool WasDisabled { get; set; }   // Sayfa açıldığındaki / son kayıttaki durum
    }
}
