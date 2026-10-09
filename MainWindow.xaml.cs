using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SensorType = LibreHardwareMonitor.Hardware.SensorType;
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
        private HardwareMonitorService Telemetry => _context.Telemetry;
        private List<SonarConfig> _availablePresets = new();
        private string _currentPage = "overview";
        private TelemetrySnapshot? _lastTelemetrySnapshot;
        private readonly Dictionary<string, (TextBlock txtVal, Button btnStar, Button btnGraph)> _categoryRowControls = new();
        private readonly Dictionary<string, TextBlock> _favoriteValControls = new();
        private string _lastCategoryStructureKey = "";
        private string _lastFavoritesKey = "";
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

            // Donanım telemetrisi tepsiye aittir (pencere kapalıyken de alarm / GPU profili / kayıt çalışır).
            // Pencere yalnızca görünen sayfanın ihtiyacını bildirir; tepsi hız ve kapsamı buna göre ayarlar.
            Telemetry.TelemetryUpdated += OnTelemetryUpdated;

            this.IsVisibleChanged += (s, e) =>
            {
                ApplyPageTelemetryDemand();

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
                    if (_context.LastSnapshot != null) UpdateGpuData(_context.LastSnapshot);
                }
            };

            LoadInitialConfig();
            _ = LoadSonarPresetsAsync();
            ShowPage("overview");

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
            if (!IsExitExplicit)
            {
                e.Cancel = true;
                this.Hide();   // IsVisibleChanged telemetri ihtiyacını kaldırır
            }
            else
            {
                Telemetry.TelemetryUpdated -= OnTelemetryUpdated;
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

        private void BtnNavHistory_Click(object sender, RoutedEventArgs e) => ShowPage("history");
        private void BtnNavBenchmark_Click(object sender, RoutedEventArgs e) => ShowPage("benchmark");

        private void BtnNavStatus_Click(object sender, RoutedEventArgs e)
            => ShowPage("status");

        private void BtnNavOverview_Click(object sender, RoutedEventArgs e)
            => ShowPage("overview");

        // ── Sol alttaki Sonar göstergesi ──
        private void SetSonarIndicator(Color color)
        {
            ledStatus.Fill = new SolidColorBrush(color);
            txtSonarLabel.Foreground = new SolidColorBrush(color);
        }

        private void SonarWidget_ToolTipOpening(object sender, System.Windows.Controls.ToolTipEventArgs e)
        {
            string preset = string.IsNullOrWhiteSpace(txtActivePreset.Text) ? "" : "\n" + txtActivePreset.Text;
            sonarWidget.ToolTip = $"{txtConnectionStatus.Text}{preset}\n{Loc.Get("SonarWidgetHint")}";
        }

        private void SonarWidget_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ShowPage("status");

        /// <summary>İlk açılışta tepsi çağırır: durum sayfasını göster.</summary>
        public void ShowStatusPage() => ShowPage("status");

        // ══════════════════════════════════════════════════════════════
        //  Modules
        // ══════════════════════════════════════════════════════════════

        // Sayfa → onu kontrol eden modül (null: her zaman açık sayfa)
        private static string? ModuleOfPage(string page) => page switch
        {
            "profiles"  => "sonar",
            "devices"   => "devices",
            "gpu"       => "gpu",
            "telemetry" => "hardware",
            "benchmark" => "benchmark",
            _           => null
        };

        private bool IsModuleOn(string module) => module switch
        {
            "sonar"    => _context.Config.ModuleSonar,
            "devices"  => _context.Config.ModuleAudioDevices,
            "hardware" => _context.Config.ModuleHardware,
            "gpu"      => _context.Config.GpuProfileActive,
            "benchmark" => _context.Config.ModuleBenchmark,
            _          => true
        };

        /// <summary>Kapalı modüllerin menü düğmesini ve sayfasını soluklaştırır; açık sayfa kapalıysa kartı gösterir.</summary>
        private void ApplyModuleState()
        {
            var modulePages = new (Button Nav, FrameworkElement Page, string Module)[]
            {
                (btnNavHome,      pageProfiles,      "sonar"),
                (btnNavDevice,    pageDeviceManager, "devices"),
                (btnNavGpu,       pageGpuMonitor,    "gpu"),
                (btnNavTelemetry, pageTelemetry,     "hardware"),
                (btnNavBenchmark, pageBenchmark,     "benchmark"),
            };
            foreach (var (nav, page, module) in modulePages)
            {
                bool on = IsModuleOn(module);
                nav.Opacity = on ? 1 : 0.45;
                page.IsEnabled = on;
                page.Opacity = on ? 1 : 0.35;
            }

            string? current = ModuleOfPage(_currentPage);
            if (current != null && !IsModuleOn(current))
            {
                txtModuleOffDesc.Text = current == "gpu" && !_context.Config.ModuleHardware
                    ? Loc.Get("ModuleGpuNeedsHardware")
                    : Loc.Get("ModuleOffDesc");
                moduleOffOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                moduleOffOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnModuleEnable_Click(object sender, RoutedEventArgs e)
        {
            var cfg = _context.Config;
            switch (ModuleOfPage(_currentPage))
            {
                case "sonar":    cfg.ModuleSonar = true; break;
                case "devices":  cfg.ModuleAudioDevices = true; break;
                case "hardware": cfg.ModuleHardware = true; break;
                case "gpu":      cfg.ModuleGpuProfile = true; cfg.ModuleHardware = true; break;
                case "benchmark": cfg.ModuleBenchmark = true; break;
                default: return;
            }
            _context.SaveConfig();
            _context.ReloadConfig();
            LoadInitialConfig();   // ayarlar sayfasındaki modül kutularını güncelle
            if (cfg.ModuleSonar) RefreshPresetsIfNeeded();
            ShowPage(_currentPage);
        }

        /// <summary>Sonar modülü kapalıyken bağlantı göstergesi; açılınca preset listesi yeniden yüklensin.</summary>
        private void ShowSonarModuleOff()
        {
            txtConnectionStatus.Text = Loc.Get("SonarModuleOff");
            SetSonarIndicator(Color.FromRgb(120, 120, 160));
            _sonarConnected = false;
            _presetsLoaded = false;
        }

        /// <summary>Oyun taraması bildirimine tıklanınca tepsi çağırır.</summary>
        public void ShowProfilesPage() => ShowPage("profiles");

        // ══════════════════════════════════════════════════════════════
        //  Game history page
        // ══════════════════════════════════════════════════════════════

        private const int HistoryRecentCount = 25;

        /// <summary>Bir oyun oturumu geçmişe yazılınca tepsi çağırır; sayfa açıksa yeniden çizilir.</summary>
        public void RefreshHistory()
        {
            if (_currentPage == "history") RenderHistory();
        }

        private void RenderHistory()
        {
            var sessions = _context.History.Sessions;
            _ = RenderDiskUsageAsync(sessions);   // oturum olmasa da kurulu oyunlar gösterilir

            bool empty = sessions.Count == 0;
            pnlHistoryEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            pnlHistoryContent.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            if (empty) return;

            // Oyun başına: toplam süre, oturum, son oynama, sıcaklık (ortalama / en yüksek)
            double[] gameCols = { 90, 60, 100, 100, 100 };
            spHistoryGames.Children.Clear();
            spHistoryGames.Children.Add(BuildHistoryRow(new[]
            {
                Loc.Get("HistoryColGame"), Loc.Get("HistoryColTotal"), Loc.Get("HistoryColSessions"),
                Loc.Get("HistoryColLastPlayed"), Loc.Get("HistoryColCpu"), Loc.Get("HistoryColGpu")
            }, gameCols, header: true, first: true));
            foreach (var g in GameHistory.Aggregate(sessions))
            {
                spHistoryGames.Children.Add(BuildHistoryRow(new[]
                {
                    g.Name, Loc.Duration(g.TotalTime), g.Sessions.ToString(),
                    g.LastPlayedUtc.ToLocalTime().ToString("d"),
                    FormatTempPair(g.AvgCpuTemp, g.MaxCpuTemp), FormatTempPair(g.AvgGpuTemp, g.MaxGpuTemp)
                }, gameCols, header: false, first: false));
            }

            // Son oturumlar, en yeni önce
            double[] sessionCols = { 130, 80, 90, 90 };
            spHistorySessions.Children.Clear();
            spHistorySessions.Children.Add(BuildHistoryRow(new[]
            {
                Loc.Get("HistoryColGame"), Loc.Get("HistoryColDate"), Loc.Get("HistoryColDuration"),
                Loc.Get("HistoryColCpuMax"), Loc.Get("HistoryColGpuMax")
            }, sessionCols, header: true, first: true));
            foreach (var s in sessions.OrderByDescending(x => x.StartUtc).Take(HistoryRecentCount))
            {
                spHistorySessions.Children.Add(BuildHistoryRow(new[]
                {
                    s.DisplayName, s.StartUtc.ToLocalTime().ToString("g"), Loc.Duration(s.Duration),
                    FormatTemp(s.MaxCpuTemp), FormatTemp(s.MaxGpuTemp)
                }, sessionCols, header: false, first: false));
            }
        }

        private int _diskRenderId;

        /// <summary>
        /// Kurulu oyunlar: boyut, son oynama, sürücü. Steam boyutları hemen; diğerleri klasör sayılınca gelir.
        /// </summary>
        private async Task RenderDiskUsageAsync(IReadOnlyList<GameSession> sessions)
        {
            int renderId = ++_diskRenderId;
            var games = await _context.GetInstalledGamesAsync();
            if (renderId != _diskRenderId) return;

            var known = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            FillDiskUsage(games, sessions, known, sizesPending: games.Any(g => g.SizeBytes == null));

            var sizes = await _context.GetFolderSizesAsync(games);
            if (renderId != _diskRenderId) return;
            FillDiskUsage(games, sessions, sizes, sizesPending: false);
        }

        private void FillDiskUsage(IReadOnlyList<DiscoveredGame> games, IReadOnlyList<GameSession> sessions,
                                   IReadOnlyDictionary<string, long> folderSizes, bool sizesPending)
        {
            var now = DateTime.UtcNow;
            var rows = DiskUsage.Build(games, sessions, folderSizes, now);

            double[] cols = { 90, 150, 60 };
            spHistoryDisk.Children.Clear();
            spHistoryDisk.Children.Add(BuildHistoryRow(new[]
            {
                Loc.Get("HistoryColGame"), Loc.Get("HistoryColSize"), Loc.Get("HistoryColLastPlayed"), Loc.Get("HistoryColDrive")
            }, cols, header: true, first: true));

            foreach (var r in rows)
            {
                string size = r.SizeBytes is long b ? TrayApplicationContext.FormatBytes(b) : (sizesPending ? "…" : "–");
                string last = r.LastPlayedKind switch
                {
                    LastPlayedKind.Never => Loc.Get("NeverPlayed"),
                    LastPlayedKind.Unknown => Loc.Get("LastPlayedUnknown"),
                    _ => FormatDaysAgo(now - r.LastPlayedUtc!.Value)
                };
                var row = (Border)BuildHistoryRow(new[] { r.Name, size, last, r.Drive.TrimEnd('\\') }, cols, header: false, first: false);
                if (r.Unused && row.Child is Grid g && g.Children[2] is TextBlock lastText)
                    lastText.Foreground = (Brush)FindResource("Accent");   // uzun süredir oynanmıyor
                spHistoryDisk.Children.Add(row);
            }

            // Sürücülerin boş alanı; dolmak üzere olan kırmızı
            spHistoryDrives.Children.Clear();
            foreach (var d in DiskUsage.Drives(games.Select(x => x.InstallPath)))
            {
                spHistoryDrives.Children.Add(new TextBlock
                {
                    Text = Loc.Format(d.IsLow ? "DriveSpaceLow" : "DriveSpace", d.Drive.TrimEnd('\\'),
                                      TrayApplicationContext.FormatBytes(d.FreeBytes),
                                      TrayApplicationContext.FormatBytes(d.TotalBytes), Math.Round(d.FreeRatio * 100)),
                    Foreground = (Brush)FindResource(d.IsLow ? "AccentRed" : "TxtSecond"),
                    FontSize = 11.5,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
        }

        // "bugün" / "12 gün önce"
        private static string FormatDaysAgo(TimeSpan ago)
            => ago.TotalDays < 1 ? Loc.Get("Today") : Loc.Format("DaysAgo", (int)ago.TotalDays);

        private static string FormatTemp(float? t) => t is float v ? $"{Math.Round(v)} °C" : "–";

        // "72 / 88 °C" (ortalama / en yüksek); sıcaklık okunamadıysa "–"
        private static string FormatTempPair(float? avg, float? max)
            => avg is float a && max is float m ? $"{Math.Round(a)} / {Math.Round(m)} °C" : "–";

        /// <summary>Tablo satırı: ilk sütun esnek (oyun adı), diğerleri sabit genişlikte; satırlar arasında ince çizgi.</summary>
        private UIElement BuildHistoryRow(string[] cells, double[] fixedWidths, bool header, bool first)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foreach (double w in fixedWidths)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });

            for (int i = 0; i < cells.Length; i++)
            {
                var text = new TextBlock
                {
                    Text = cells[i],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    FontSize = header ? 10.5 : 12,
                    FontWeight = header || i == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = (Brush)FindResource(header ? "TxtMuted" : i == 0 ? "TxtPrimary" : "TxtSecond")
                };
                Grid.SetColumn(text, i);
                grid.Children.Add(text);
            }

            return new Border
            {
                BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
                Padding = new Thickness(0, 9, 0, 9),
                Child = grid
            };
        }

        // ══════════════════════════════════════════════════════════════
        //  Benchmark page
        // ══════════════════════════════════════════════════════════════

        // Sayfa açıkken saniyede bir: canlı durum (geçen süre, FPS)
        private readonly System.Windows.Threading.DispatcherTimer _benchmarkTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private bool _benchmarkTimerHooked;
        private static readonly int[] WarmupChoices = { 0, 5, 10, 15, 30, 60 };

        private void PopulateBenchmarkSettings()
        {
            var cfg = _context.Config;
            cbBenchmarkHotkey.Items.Clear();
            foreach (var key in GlobalHotkey.Choices)
            {
                var item = new ComboBoxItem { Content = "Ctrl+Shift+" + key, Tag = key.ToString() };
                cbBenchmarkHotkey.Items.Add(item);
                if (key.ToString() == cfg.BenchmarkHotkey) cbBenchmarkHotkey.SelectedItem = item;
            }

            cbBenchmarkWarmup.Items.Clear();
            foreach (int sec in WarmupChoices.Union(new[] { cfg.BenchmarkWarmupSeconds }).OrderBy(x => x))
            {
                var item = new ComboBoxItem { Content = Loc.Format("Seconds", sec), Tag = sec };
                cbBenchmarkWarmup.Items.Add(item);
                if (sec == cfg.BenchmarkWarmupSeconds) cbBenchmarkWarmup.SelectedItem = item;
            }

            cbBenchmarkEfficiency.Items.Clear();
            foreach (var (basis, key) in new[] { (EfficiencyBasis.Gpu, "EffGpu"), (EfficiencyBasis.Cpu, "EffCpu"), (EfficiencyBasis.Total, "EffTotal") })
            {
                var item = new ComboBoxItem { Content = Loc.Get(key), Tag = basis };
                cbBenchmarkEfficiency.Items.Add(item);
                if (basis == cfg.BenchmarkEfficiency) cbBenchmarkEfficiency.SelectedItem = item;
            }
            UpdateBenchmarkHotkeyStatus();
        }

        /// <summary>Kısayol başka bir uygulamada kayıtlıysa ayarlarda kırmızı uyarı.</summary>
        private void UpdateBenchmarkHotkeyStatus()
        {
            bool taken = _context.HotkeyRegistered == false;
            lblBenchmarkHotkeyDesc.Text = taken
                ? Loc.Format("BenchmarkHotkeyTaken", "Ctrl+Shift+" + _context.Config.BenchmarkHotkey)
                : Loc.Get("BenchmarkHotkeyDesc");
            lblBenchmarkHotkeyDesc.Foreground = (Brush)FindResource(taken ? "AccentRed" : "TxtSecond");
        }

        /// <summary>Kayıt başlayınca / bitince tepsi çağırır.</summary>
        public void RefreshBenchmarks()
        {
            if (_currentPage == "benchmark") RenderBenchmarks();
        }

        private void RenderBenchmarks()
        {
            if (!_benchmarkTimerHooked)
            {
                _benchmarkTimer.Tick += (s, e) => UpdateBenchmarkStatus();
                _benchmarkTimerHooked = true;
            }
            if (txtBenchmarkLabel.Text != _context.NextBenchmarkLabel) txtBenchmarkLabel.Text = _context.NextBenchmarkLabel;
            UpdateBenchmarkStatus();

            var basis = _context.Config.BenchmarkEfficiency;
            int warmup = _context.Config.BenchmarkWarmupSeconds;
            spBenchmarkRuns.Children.Clear();
            var runs = _context.Benchmarks.LoadAll();
            _benchmarkRuns = runs;
            _benchmarkSelected.IntersectWith(runs.Select(r => r.Id));   // silinen kayıtlar seçimden düşer
            RenderBenchmarkCompare();
            if (runs.Count == 0)
            {
                spBenchmarkRuns.Children.Add(new TextBlock
                {
                    Text = Loc.Get("BenchmarkNoRuns"),
                    Foreground = (Brush)FindResource("TxtSecond"),
                    Margin = new Thickness(0, 12, 0, 12)
                });
                return;
            }
            for (int i = 0; i < runs.Count; i++)
                spBenchmarkRuns.Children.Add(BuildBenchmarkRow(runs[i], BenchmarkStats.Compute(runs[i], warmup), basis, first: i == 0));
        }

        private void UpdateBenchmarkStatus()
        {
            string hotkey = "Ctrl+Shift+" + _context.Config.BenchmarkHotkey;
            var live = _context.Benchmark.Live;
            lblBenchmarkToggle.Text = Loc.Get(live != null ? "BenchmarkStop" : "BenchmarkStart");
            // Kısayol başka bir uygulamada kayıtlıysa çalışmayan bir tuşu önermesin
            lblBenchmarkToggleHotkey.Text = _context.HotkeyRegistered == true ? hotkey : "";
            txtBenchmarkHint.Text = Loc.Format("BenchmarkHint", _context.Config.BenchmarkWarmupSeconds);

            if (live is (string app, var fps, TimeSpan elapsed))
            {
                txtBenchmarkStatus.Text = Loc.Format("BenchmarkRecording", elapsed.ToString(@"mm\:ss"),
                    string.IsNullOrEmpty(app) ? "–" : app, fps is double f ? Math.Round(f).ToString() : "–");
                txtBenchmarkStatus.Foreground = (Brush)FindResource("Accent");
            }
            else if (RtssReader.ReadApps().Count == 0)
            {
                txtBenchmarkStatus.Text = Loc.Get("BenchmarkNoRtss");
                txtBenchmarkStatus.Foreground = (Brush)FindResource("AccentRed");
            }
            else
            {
                txtBenchmarkStatus.Text = Loc.Format("BenchmarkReady", hotkey);
                txtBenchmarkStatus.Foreground = (Brush)FindResource("TxtPrimary");
            }
        }

        private void TxtBenchmarkLabel_TextChanged(object sender, TextChangedEventArgs e)
            => _context.NextBenchmarkLabel = txtBenchmarkLabel.Text;

        private void BtnBenchmarkToggle_Click(object sender, RoutedEventArgs e)
        {
            _context.ToggleBenchmark();
            UpdateBenchmarkStatus();
        }

        private void CbBenchmarkEfficiency_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isPopulatingControls || cbBenchmarkEfficiency.SelectedItem is not ComboBoxItem { Tag: EfficiencyBasis basis }) return;
            if (basis == _context.Config.BenchmarkEfficiency) return;
            _context.Config.BenchmarkEfficiency = basis;
            _context.SaveConfig();
            RenderBenchmarks();   // listedeki FPS/W satırları ve karşılaştırma tablosu
        }

        private static string Num(double? v, string format = "0") => v is double d ? d.ToString(format) : "–";

        private static string BasisName(EfficiencyBasis basis)
            => Loc.Get(basis switch { EfficiencyBasis.Gpu => "EffGpu", EfficiencyBasis.Cpu => "EffCpu", _ => "EffTotal" });

        // ── Karşılaştırma ───────────────────────────────────────────────

        private readonly HashSet<string> _benchmarkSelected = new();
        private List<BenchmarkRun> _benchmarkRuns = new();
        private List<CompareGroup> _compareGroups = new();

        // Kategorik palet (koyu mod, kart zemini #13131A üzerinde doğrulandı); sırası sabit, gruba göre atanır
        private static readonly string[] SeriesColors = { "#3987e5", "#d95926", "#199e70", "#c98500" };
        private const string BetterColor = "#7EE787";

        private string MetricName(string key)
            => key == "FpsPerWatt" ? Loc.Format("CmpFpsPerWatt", BasisName(_context.Config.BenchmarkEfficiency)) : Loc.Get("Cmp" + key);

        private void RenderBenchmarkCompare()
        {
            int warmup = _context.Config.BenchmarkWarmupSeconds;
            var groups = BenchmarkCompare.Group(_benchmarkRuns.Where(r => _benchmarkSelected.Contains(r.Id)), warmup);
            bool trimmed = groups.Count > BenchmarkCompare.MaxGroups;
            _compareGroups = groups.Take(BenchmarkCompare.MaxGroups).ToList();

            bool ready = _compareGroups.Count >= 2;
            pnlBenchmarkCompare.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
            btnBenchmarkExport.IsEnabled = ready;
            if (!ready)
            {
                txtBenchmarkCompareHint.Text = Loc.Get("BenchmarkCompareHint");
                return;
            }

            txtBenchmarkCompareHint.Text = Loc.Format("BenchmarkCompareBaseline", _compareGroups[0].Label) +
                                           (trimmed ? " " + Loc.Format("BenchmarkCompareMax", BenchmarkCompare.MaxGroups) : "");
            BuildCompareTable();

            if (cbBenchmarkChartMetric.Items.Count == 0)
            {
                foreach (var m in BenchmarkCompare.ChartMetrics)
                    cbBenchmarkChartMetric.Items.Add(new ComboBoxItem { Content = Loc.Get("Chart" + m.Key), Tag = m });
                cbBenchmarkChartMetric.SelectedIndex = 0;
            }
            RenderBenchmarkChart();
        }

        /// <summary>Ölçüler satırda, gruplar sütunda; temel dışındaki grupların değerinin altında yüzde fark.</summary>
        private void BuildCompareTable()
        {
            var basis = _context.Config.BenchmarkEfficiency;
            var g = grdBenchmarkCompare;
            g.Children.Clear();
            g.RowDefinitions.Clear();
            g.ColumnDefinitions.Clear();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            foreach (var _ in _compareGroups) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            void Add(UIElement e, int row, int col)
            {
                Grid.SetRow(e, row);
                Grid.SetColumn(e, col);
                g.Children.Add(e);
            }

            // Başlık: renk işareti (grafikteki çizgi) + etiket + kayıt sayısı
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < _compareGroups.Count; i++)
            {
                var head = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
                var title = new StackPanel { Orientation = Orientation.Horizontal };
                title.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 12, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = BrushFrom(SeriesColors[i]),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });
                title.Children.Add(new TextBlock
                {
                    Text = _compareGroups[i].Label, FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TxtPrimary"), TextTrimming = TextTrimming.CharacterEllipsis
                });
                head.Children.Add(title);
                head.Children.Add(new TextBlock
                {
                    Text = Loc.Format("BenchmarkRunsCount", _compareGroups[i].Runs.Count) + (i == 0 ? " · " + Loc.Get("BenchmarkBaseline") : ""),
                    Foreground = (Brush)FindResource("TxtMuted"), FontSize = 10.5
                });
                Add(head, 0, i + 1);
            }

            int r = 1;
            foreach (var m in BenchmarkCompare.Metrics)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var line = new Border
                {
                    BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(0, 1, 0, 0)
                };
                Grid.SetColumnSpan(line, _compareGroups.Count + 1);
                Add(line, r, 0);
                Add(new TextBlock
                {
                    Text = MetricName(m.Key), Foreground = (Brush)FindResource("TxtSecond"), FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 7, 8, 7), TextWrapping = TextWrapping.Wrap
                }, r, 0);

                double? baseline = _compareGroups[0].Value(m, basis);
                for (int i = 0; i < _compareGroups.Count; i++)
                {
                    double? v = _compareGroups[i].Value(m, basis);
                    var cell = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 7, 8, 7) };
                    cell.Children.Add(new TextBlock { Text = Num(v, m.Format), Foreground = (Brush)FindResource("TxtPrimary"), FontWeight = FontWeights.SemiBold });
                    if (i > 0 && BenchmarkCompare.PercentDiff(baseline, v) is double pct)
                    {
                        int verdict = BenchmarkCompare.Verdict(m, pct);
                        // İşaret metinde: renk tek başına anlam taşımaz
                        cell.Children.Add(new TextBlock
                        {
                            Text = $"  {(pct >= 0 ? "+" : "")}{pct:0.0}%",
                            FontSize = 11,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = verdict > 0 ? BrushFrom(BetterColor)
                                       : verdict < 0 ? (Brush)FindResource("AccentRed")
                                       : (Brush)FindResource("TxtMuted")
                        });
                    }
                    Add(cell, r, i + 1);
                }
                r++;
            }
        }

        // Grafik çerçevesi: hover hesabı için son çizimden saklanır
        private List<(CompareGroup Group, List<(double T, double Value)> Points, string Color)> _chartSeries = new();
        private double _chartMinT, _chartMaxT, _chartMinV, _chartMaxV, _chartLeft, _chartTop, _chartWidth, _chartHeight;
        private string _chartUnit = "";

        private const double ChartPadLeft = 46, ChartPadRight = 96, ChartPadTop = 8, ChartPadBottom = 22;

        private static string FormatT(double t) => $"{(int)t / 60}:{(int)t % 60:00}";

        private void RenderBenchmarkChart()
        {
            var cvs = cvsBenchmarkChart;
            cvs.Children.Clear();
            cvsBenchmarkHover.Children.Clear();
            spBenchmarkLegend.Children.Clear();
            if (_compareGroups.Count < 2 || cbBenchmarkChartMetric.SelectedItem is not ComboBoxItem { Tag: ChartMetric metric }) return;

            int warmup = _context.Config.BenchmarkWarmupSeconds;
            _chartUnit = metric.Unit;
            _chartSeries = _compareGroups.Select((g, i) => (g, BenchmarkCompare.Series(g, metric, warmup), SeriesColors[i])).ToList();

            // Lejant her zaman (2+ seri): renk işareti + etiket metin renginde
            foreach (var (group, _, color) in _chartSeries)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
                item.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 14, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = BrushFrom(color),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });
                item.Children.Add(new TextBlock { Text = group.Label, Foreground = (Brush)FindResource("TxtSecond"), FontSize = 11.5 });
                spBenchmarkLegend.Children.Add(item);
            }

            var all = _chartSeries.SelectMany(x => x.Points).ToList();
            double w = cvs.ActualWidth, h = cvs.ActualHeight;
            if (all.Count == 0)
            {
                cvs.Children.Add(new TextBlock { Text = Loc.Get("BenchmarkChartNoData"), Foreground = (Brush)FindResource("TxtSecond") });
                return;
            }
            if (w < 120 || h < 60) return;

            _chartMinT = 0;
            _chartMaxT = Math.Max(1, all.Max(p => p.T));
            double lo = all.Min(p => p.Value), hi = all.Max(p => p.Value);
            double pad = Math.Max((hi - lo) * 0.08, Math.Max(Math.Abs(hi) * 0.02, 1));
            _chartMinV = Math.Max(0, lo - pad);
            _chartMaxV = hi + pad;
            _chartLeft = ChartPadLeft;
            _chartTop = ChartPadTop;
            _chartWidth = w - ChartPadLeft - ChartPadRight;
            _chartHeight = h - ChartPadTop - ChartPadBottom;

            var muted = (Brush)FindResource("TxtMuted");
            var gridBrush = (Brush)FindResource("Border");

            // Silik ızgara ve eksen etiketleri
            for (int i = 0; i <= 3; i++)
            {
                double v = _chartMinV + (_chartMaxV - _chartMinV) * i / 3;
                double y = Y(v);
                cvs.Children.Add(new System.Windows.Shapes.Line { X1 = _chartLeft, X2 = _chartLeft + _chartWidth, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
                var label = new TextBlock { Text = v.ToString(v >= 100 ? "0" : "0.#"), Foreground = muted, FontSize = 10 };
                Canvas.SetLeft(label, 4);
                Canvas.SetTop(label, y - 7);
                cvs.Children.Add(label);
            }
            foreach (double t in new[] { 0, _chartMaxT / 2, _chartMaxT })
            {
                var label = new TextBlock { Text = FormatT(t), Foreground = muted, FontSize = 10 };
                Canvas.SetLeft(label, X(t) - 12);
                Canvas.SetTop(label, _chartTop + _chartHeight + 4);
                cvs.Children.Add(label);
            }

            // Çizgiler (2 px) ve çizgi sonunda doğrudan etiket; etiketler çakışmasın diye dikeyde aralanır
            var ends = new List<(double Y, string Text)>();
            foreach (var (group, points, color) in _chartSeries)
            {
                if (points.Count == 0) continue;
                var line = new System.Windows.Shapes.Polyline
                {
                    Stroke = BrushFrom(color), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round
                };
                foreach (var (t, v) in points) line.Points.Add(new Point(X(t), Y(v)));
                cvs.Children.Add(line);
                ends.Add((Y(points[^1].Value), group.Label));
            }
            double lastY = double.MinValue;
            foreach (var (y, text) in ends.OrderBy(e => e.Y))
            {
                double top = Math.Max(y - 7, lastY + 14);
                lastY = top;
                var label = new TextBlock
                {
                    Text = text, Foreground = (Brush)FindResource("TxtSecond"), FontSize = 10.5,
                    MaxWidth = ChartPadRight - 10, TextTrimming = TextTrimming.CharacterEllipsis
                };
                Canvas.SetLeft(label, _chartLeft + _chartWidth + 8);
                Canvas.SetTop(label, top);
                cvs.Children.Add(label);
            }
        }

        private double X(double t) => _chartLeft + (t - _chartMinT) / (_chartMaxT - _chartMinT) * _chartWidth;
        private double Y(double v) => _chartTop + (1 - (v - _chartMinV) / (_chartMaxV - _chartMinV)) * _chartHeight;

        private void CbBenchmarkChartMetric_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderBenchmarkChart();
        private void CvsBenchmarkChart_SizeChanged(object sender, SizeChangedEventArgs e) => RenderBenchmarkChart();
        private void CvsBenchmarkChart_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => cvsBenchmarkHover.Children.Clear();

        /// <summary>Dikey çizgi + o saniyedeki değerler (en yakın örnek).</summary>
        private void CvsBenchmarkChart_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var hover = cvsBenchmarkHover;
            hover.Children.Clear();
            if (_chartSeries.Count == 0 || _chartWidth <= 0) return;

            var pos = e.GetPosition(cvsBenchmarkChart);
            if (pos.X < _chartLeft || pos.X > _chartLeft + _chartWidth) return;
            double t = Math.Round(_chartMinT + (pos.X - _chartLeft) / _chartWidth * (_chartMaxT - _chartMinT));
            double x = X(t);

            hover.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = x, X2 = x, Y1 = _chartTop, Y2 = _chartTop + _chartHeight,
                Stroke = (Brush)FindResource("TxtMuted"), StrokeThickness = 1
            });

            var tip = new StackPanel();
            tip.Children.Add(new TextBlock { Text = FormatT(t), Foreground = (Brush)FindResource("TxtSecond"), FontSize = 10.5, Margin = new Thickness(0, 0, 0, 3) });
            foreach (var (group, points, color) in _chartSeries)
            {
                if (points.Count == 0) continue;
                var nearest = points.OrderBy(p => Math.Abs(p.T - t)).First();
                if (Math.Abs(nearest.T - t) > 2) continue;

                // 8 px işaret, zemin renginde 2 px halka
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 8, Height = 8, Fill = BrushFrom(color), Stroke = (Brush)FindResource("BgCard"), StrokeThickness = 2
                };
                Canvas.SetLeft(dot, X(nearest.T) - 4);
                Canvas.SetTop(dot, Y(nearest.Value) - 4);
                hover.Children.Add(dot);

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 8, Height = 8, RadiusX = 2, RadiusY = 2, Fill = BrushFrom(color),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });
                row.Children.Add(new TextBlock
                {
                    Text = $"{group.Label}: {nearest.Value.ToString(nearest.Value >= 100 ? "0" : "0.#")} {_chartUnit}",
                    Foreground = (Brush)FindResource("TxtPrimary"), FontSize = 11.5
                });
                tip.Children.Add(row);
            }

            var box = new Border
            {
                Background = (Brush)FindResource("BgInput"), BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 8, 6), Child = tip
            };
            box.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double left = x + 12;
            if (left + box.DesiredSize.Width > cvsBenchmarkChart.ActualWidth) left = x - 12 - box.DesiredSize.Width;
            Canvas.SetLeft(box, Math.Max(0, left));
            Canvas.SetTop(box, _chartTop);
            hover.Children.Add(box);
        }

        /// <summary>Karşılaştırma tablosu + saniyelik veriler; bölge ayarına göre ayırıcı (Excel'de doğrudan açılır).</summary>
        private async void BtnBenchmarkExport_Click(object sender, RoutedEventArgs e)
        {
            if (_compareGroups.Count < 2) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv",
                FileName = $"benchmark-{DateTime.Now:yyyyMMdd-HHmm}.csv"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var culture = System.Globalization.CultureInfo.CurrentCulture;
                var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                string summary = BenchmarkCompare.SummaryCsv(_compareGroups, _context.Config.BenchmarkEfficiency,
                                                             key => key == "Metric" ? Loc.Get("CmpMetric") : MetricName(key), culture);
                System.IO.File.WriteAllText(dialog.FileName, summary, utf8);

                string samplesPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dialog.FileName) ?? "",
                    System.IO.Path.GetFileNameWithoutExtension(dialog.FileName) + Loc.Get("CsvSamplesSuffix") + ".csv");
                System.IO.File.WriteAllText(samplesPath, BenchmarkCompare.SamplesCsv(_compareGroups, culture), utf8);

                lblBenchmarkExport.Text = Loc.Get("BenchmarkExported");
                await Task.Delay(2000);
                lblBenchmarkExport.Text = Loc.Get("BenchmarkExport");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Warden", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private UIElement BuildBenchmarkRow(BenchmarkRun run, BenchmarkSummary sum, EfficiencyBasis basis, bool first)
        {
            var secondary = (Brush)FindResource("TxtSecond");
            var panel = new StackPanel();

            // Etiket: yerinde düzenlenir (Enter veya odak kaybı kaydeder)
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            var label = new System.Windows.Controls.TextBox
            {
                Text = run.Label,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = (Brush)FindResource("TxtPrimary"),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                MinWidth = 60,
                ToolTip = Loc.Get("BenchmarkRenameTip")
            };
            void Commit()
            {
                string text = label.Text.Trim();
                if (text.Length == 0) { label.Text = run.Label; return; }
                if (text == run.Label) return;
                _context.Benchmarks.Rename(run, text);
            }
            label.LostFocus += (s, e) => Commit();
            label.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); } };
            header.Children.Add(label);

            string meta = string.Join(" · ", new[]
            {
                run.Game, run.StartUtc.ToLocalTime().ToString("g"), Loc.Duration(TimeSpan.FromSeconds(run.DurationSeconds))
            }.Where(x => !string.IsNullOrEmpty(x)));
            header.Children.Add(new TextBlock { Text = "  " + meta, Foreground = secondary, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(header);

            TextBlock Line(string text, Brush? brush = null, double size = 12) => new()
            {
                Text = text, Foreground = brush ?? secondary, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0)
            };

            if (sum.MeasuredSeconds <= 0)
            {
                panel.Children.Add(Line(Loc.Get("BenchmarkRunShort")));
            }
            else
            {
                string low = (sum.Low1Approximate ? "≈ " : "") + Num(sum.Low1Fps);
                panel.Children.Add(Line(Loc.Format("BenchmarkRunFps", Num(sum.AvgFps), low), (Brush)FindResource("TxtPrimary"), 13));
                panel.Children.Add(Line(Loc.Format("BenchmarkRunDevice", "GPU", Num(sum.AvgGpuClock), Num(sum.AvgGpuTemp), Num(sum.P95GpuTemp),
                                                   Num(sum.AvgGpuPower, "0.0"), Num(sum.AvgGpuLoad))));
                panel.Children.Add(Line(Loc.Format("BenchmarkRunDevice", "CPU", Num(sum.AvgCpuClock), Num(sum.AvgCpuTemp), Num(sum.P95CpuTemp),
                                                   Num(sum.AvgCpuPower, "0.0"), Num(sum.AvgCpuLoad))));
                panel.Children.Add(Line(Loc.Format("BenchmarkRunEfficiency", Num(sum.FpsPerWatt(basis), "0.00"), BasisName(basis)),
                                        (Brush)FindResource("Accent")));
            }

            var delete = new Button
            {
                Content = "🗑",
                Style = (Style)FindResource("DangerButton"),
                Padding = new Thickness(10, 5, 10, 5),
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                ToolTip = Loc.Get("BenchmarkDelete")
            };
            delete.Click += (s, e) =>
            {
                if (MessageBox.Show(Loc.Format("BenchmarkDeleteConfirm", run.Label), "Warden",
                                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _context.Benchmarks.Delete(run.Id);
                RenderBenchmarks();
            };

            // Karşılaştırmaya ekle / çıkar
            var select = new System.Windows.Controls.CheckBox
            {
                IsChecked = _benchmarkSelected.Contains(run.Id),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 14, 0),
                ToolTip = Loc.Get("BenchmarkSelectTip")
            };
            select.Checked += (s, e) => { _benchmarkSelected.Add(run.Id); RenderBenchmarkCompare(); };
            select.Unchecked += (s, e) => { _benchmarkSelected.Remove(run.Id); RenderBenchmarkCompare(); };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(select);
            Grid.SetColumn(panel, 1);
            grid.Children.Add(panel);
            Grid.SetColumn(delete, 2);
            grid.Children.Add(delete);

            return new Border
            {
                BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
                Padding = new Thickness(0, 12, 0, 12),
                Child = grid
            };
        }

        /// <summary>Arka plan taraması oyun listesini/kuralları değiştirince tepsi çağırır.</summary>
        public void RefreshGameList() => RenderRulesList();

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

            var pages = new (string Key, UIElement Page, UIElement Indicator, Button Nav)[]
            {
                ("overview",  pageOverview,      rectOverviewActive,  btnNavOverview),
                ("profiles",  pageProfiles,      rectHomeActive,      btnNavHome),
                ("devices",   pageDeviceManager, rectDeviceActive,    btnNavDevice),
                ("gpu",       pageGpuMonitor,    rectGpuActive,       btnNavGpu),
                ("telemetry", pageTelemetry,     rectTelemetryActive, btnNavTelemetry),
                ("history",   pageHistory,       rectHistoryActive,   btnNavHistory),
                ("benchmark", pageBenchmark,     rectBenchmarkActive, btnNavBenchmark),
                ("status",    pageStatus,        rectStatusActive,    btnNavStatus),
                ("settings",  pageSettings,      rectSettingsActive,  btnNavSettings),
            };
            foreach (var (key, pageElement, indicator, nav) in pages)
            {
                bool active = key == page;
                pageElement.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                indicator.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                nav.Foreground = (Brush)FindResource(active ? "Accent" : "TxtSecond");
                nav.Background = active ? (Brush)FindResource("NavActiveBg") : Brushes.Transparent;
            }
            ApplyModuleState();

            if (page == "overview")
            {
                RefreshOverviewStatus();
                if (_context.LastSnapshot != null) RenderOverview(_context.LastSnapshot);
            }
            if (page == "status") _ = RunStatusChecksAsync();
            if (page == "history") RenderHistory();
            if (page == "benchmark") RenderBenchmarks();
            if (page == "benchmark") _benchmarkTimer.Start(); else _benchmarkTimer.Stop();

            ApplyPageTelemetryDemand();
        }

        // ══════════════════════════════════════════════════════════════
        //  Data Loading
        // ══════════════════════════════════════════════════════════════
        private void LoadInitialConfig()
        {
            _isPopulatingControls = true;
            try
            {
                PopulateIntervalChoices(_context.Config.CheckIntervalMilliseconds);
                chkStartup.IsChecked = _context.Config.StartWithWindows;
                chkAutoUpdate.IsChecked = _context.Config.AutoCheckUpdates;
                chkAutoScan.IsChecked = _context.Config.AutoScanGames;
                chkAutoAssign.IsChecked = _context.Config.AutoAssignGgPresets;
                chkModuleSonar.IsChecked = _context.Config.ModuleSonar;
                chkModuleAudioDevices.IsChecked = _context.Config.ModuleAudioDevices;
                chkModuleHardware.IsChecked = _context.Config.ModuleHardware;
                chkModuleGpuProfile.IsChecked = _context.Config.ModuleGpuProfile;
                chkModuleBenchmark.IsChecked = _context.Config.ModuleBenchmark;
                PopulateBenchmarkSettings();
                chkTempAlarm.IsChecked = _context.Config.TempAlarmEnabled;
                chkSessionSummary.IsChecked = _context.Config.SessionSummaryEnabled;
                txtCpuTempLimit.Text = _context.Config.CpuTempLimit.ToString();
                txtGpuTempLimit.Text = _context.Config.GpuTempLimit.ToString();
                chkAutoRecord.IsChecked = _context.Config.AutoRecordGameSessions;

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
            if (!_context.Config.ModuleSonar)
            {
                ShowSonarModuleOff();
                return;
            }
            _isLoadingPresets = true;

            txtConnectionStatus.Text = Loc.Get("Connecting");
            SetSonarIndicator(Color.FromRgb(120, 120, 160));

            try
            {
                string address = await _client.GetSonarAddressAsync();
                txtConnectionStatus.Text = Loc.Format("SonarConnectedAt", address.Replace("http://", "").Replace("https://", ""));
                SetSonarIndicator(Color.FromRgb(78, 201, 126));
                _sonarConnected = true;

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
                SetSonarIndicator(Color.FromRgb(224, 85, 85));
                _sonarConnected = false;
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
            ApplyPageTelemetryDemand();
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
            string scanResult = "";

            try
            {
                // Tarama, kaldırılan oyunların silinmesi ve GG profili ataması açılış taramasıyla ortak
                var result = await _context.SyncGamesAsync();
                if (result == null)
                {
                    scanResult = Loc.Get("ScanBusy");
                }
                else
                {
                    // Sonuç, kimsenin görmediği sol alttaki küçük kutu yerine butonun üzerinde gösterilir
                    scanResult = result.Added.Count > 0 ? Loc.Format("ScanDone", result.Added.Count) : Loc.Get("ScanNone");
                    if (result.Assigned.Count > 0) scanResult += " · " + Loc.Format("ScanMatched", result.Assigned.Count);
                    if (result.Removed.Count > 0) scanResult += " · " + Loc.Format("ScanRemoved", result.Removed.Count);
                }
            }
            catch (Exception ex)
            {
                scanResult = ex.Message;
                MessageBox.Show(ex.Message, "Warden", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                // Hata olsa bile buton tekrar kullanılabilir olmalı (eskiden kalıcı olarak devre dışı kalabiliyordu)
                btnScan.IsEnabled = true;
            }

            lblScanBtn.Text = "✔ " + scanResult;
            await Task.Delay(3000);
            lblScanBtn.Text = Loc.Get("BtnScan");
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
            if (cbInterval.SelectedValue is int interval)
                _context.Config.CheckIntervalMilliseconds =
                    Math.Clamp(interval, AppConfig.MinCheckIntervalMs, AppConfig.MaxCheckIntervalMs);

            _context.Config.StartWithWindows = chkStartup.IsChecked == true;
            _context.Config.AutoCheckUpdates = chkAutoUpdate.IsChecked == true;
            _context.Config.AutoScanGames = chkAutoScan.IsChecked == true;
            _context.Config.AutoAssignGgPresets = chkAutoAssign.IsChecked == true;
            _context.Config.ModuleSonar = chkModuleSonar.IsChecked == true;
            _context.Config.ModuleAudioDevices = chkModuleAudioDevices.IsChecked == true;
            _context.Config.ModuleHardware = chkModuleHardware.IsChecked == true;
            _context.Config.ModuleGpuProfile = chkModuleGpuProfile.IsChecked == true;
            _context.Config.ModuleBenchmark = chkModuleBenchmark.IsChecked == true;
            if (cbBenchmarkHotkey.SelectedItem is ComboBoxItem hk) _context.Config.BenchmarkHotkey = (string)hk.Tag;
            if (cbBenchmarkWarmup.SelectedItem is ComboBoxItem wu) _context.Config.BenchmarkWarmupSeconds = (int)wu.Tag;
            _context.Config.TempAlarmEnabled = chkTempAlarm.IsChecked == true;
            _context.Config.SessionSummaryEnabled = chkSessionSummary.IsChecked == true;
            ApplyTempLimitsFromUi();

            if (cbLanguage.SelectedItem is ComboBoxItem langItem)
                _context.Config.Language = langItem.Tag?.ToString() ?? "TR";

            _context.SaveConfig();
            _context.ReloadConfig();

            if (_context.Config.ModuleSonar) RefreshPresetsIfNeeded();
            else ShowSonarModuleOff();
            ApplyModuleState();
            UpdateBenchmarkHotkeyStatus();
        }

        private void Setting_Changed(object sender, RoutedEventArgs e) 
        {
            AutoSaveSettings();
        }

        private void CbInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AutoSaveSettings();
        }

        // Oyun kontrol sıklığı seçenekleri (ms). Elle config'e yazılmış başka bir değer varsa listeye eklenir.
        private static readonly int[] IntervalChoices = { 500, 1000, 2000, 5000 };

        private void PopulateIntervalChoices(int currentMs)
        {
            var values = IntervalChoices.ToList();
            if (!values.Contains(currentMs)) values.Add(currentMs);
            values.Sort();

            cbInterval.Items.Clear();
            foreach (int ms in values)
            {
                string seconds = (ms / 1000.0).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
                cbInterval.Items.Add(new IntervalChoice { Text = Loc.Format("IntervalSeconds", seconds), Value = ms });
            }
            cbInterval.SelectedValue = currentMs;
        }

        // WPF bağlaması (DisplayMemberPath/SelectedValuePath) yalnızca public türlerin özelliklerini okuyabilir
        public sealed class IntervalChoice
        {
            public string Text { get; set; } = "";
            public int Value { get; set; }
            public override string ToString() => Text;
        }

        private void BtnGoGpu_Click(object sender, RoutedEventArgs e) => ShowPage("gpu");
        private void BtnGoDevices_Click(object sender, RoutedEventArgs e) => ShowPage("devices");

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
        //  Overview (Panel) page
        // ══════════════════════════════════════════════════════════════
        private string _lastPresetName = "";
        private bool _sonarConnected;
        private TelemetrySnapshot? _lastOverviewSnapshot;
        private bool? _afterburnerFound;
        private bool? _pawnIoInstalled;

        private static readonly Color OverviewGpuLineColor = (Color)ColorConverter.ConvertFromString("#38BDF8");

        /// <summary>Telemetriden bağımsız durumları (bağlantılar, alarm, kayıt, güncelleme) yeniler.</summary>
        private async void RefreshOverviewStatus()
        {
            RenderOverviewGame();
            RenderOverviewChips();

            // Kayıt defteri taraması UI thread'inde yapılmasın
            if (_afterburnerFound == null || _pawnIoInstalled == null)
            {
                var (ab, pawn) = await Task.Run(() => (_context.AfterburnerPath != null, HardwareMonitorService.IsPawnIoInstalled));
                _afterburnerFound = ab;
                _pawnIoInstalled = pawn;
                RenderOverviewChips();
            }
        }

        private void RenderOverview(TelemetrySnapshot snapshot)
        {
            _lastOverviewSnapshot = snapshot;
            var all = snapshot.AllSensors;

            // CPU
            var cpuTemp = snapshot.CpuTemperature;
            SetGauge(ringCpu, txtOvCpuTemp, cpuTemp, _context.Config.CpuTempLimit);
            txtOvCpuName.Text = all.FirstOrDefault(x => x.Category == "CPU")?.HardwareName ?? "–";
            txtOvCpuLoad.Text = Loc.Format("OvLoad", FormatSensor(all, "CPU", SensorType.Load));
            txtOvCpuClock.Text = Loc.Format("OvClock", FormatSensor(all, "CPU", SensorType.Clock));
            txtOvCpuPower.Text = Loc.Format("OvPower", FormatSensor(all, "CPU", SensorType.Power));

            // GPU
            var gpu = snapshot.PrimaryGpu;
            SetGauge(ringGpu, txtOvGpuTemp, gpu is { TemperatureCelsius: > 0 } ? gpu.TemperatureCelsius : null, _context.Config.GpuTempLimit);
            txtOvGpuName.Text = gpu?.Name ?? (snapshot.HardwareReady ? Loc.Get("GpuNotFound") : Loc.Get("GpuDetecting"));
            txtOvGpuLoad.Text = Loc.Format("OvLoad", gpu != null ? $"%{gpu.UsagePercentage}" : "–");
            txtOvGpuClock.Text = Loc.Format("OvClock", gpu != null ? $"{Math.Round(gpu.CoreClockMhz)} MHz" : "–");
            txtOvGpuPower.Text = Loc.Format("OvPower", FormatSensor(all, "GPU", SensorType.Power));

            // Mini göstergeler
            txtOvTileRam.Text = FormatSensor(all, "Memory", SensorType.Load);
            var hotSpot = all.FirstOrDefault(x => x.Category == "GPU" && x.SensorType == SensorType.Temperature &&
                                                  (x.RawName.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                                                   x.RawName.Contains("Hotspot", StringComparison.OrdinalIgnoreCase)));
            txtOvTileHotSpot.Text = hotSpot?.FormattedValue ?? "–";
            var fan = all.Where(x => x.SensorType == SensorType.Fan).OrderByDescending(x => x.Value).FirstOrDefault();
            txtOvTileFan.Text = fan?.FormattedValue ?? "–";
            txtOvTileCpuPower.Text = FormatSensor(all, "CPU", SensorType.Power);

            DrawOverviewChart(snapshot);
            RenderOverviewGame();
        }

        private static string FormatSensor(List<TelemetrySensorItem> all, string category, SensorType type)
            => all.FirstOrDefault(x => x.Category == category && x.SensorType == type)?.FormattedValue ?? "–";

        /// <summary>Halka göstergesi: 0–100 °C ölçeği, sınıra ulaşınca kırmızı.</summary>
        private void SetGauge(System.Windows.Shapes.Ellipse ring, TextBlock label, float? temperature, int limit)
        {
            if (temperature is not float t || t <= 0)
            {
                ring.Visibility = Visibility.Hidden;
                label.Text = "–";
                return;
            }

            double fraction = Math.Clamp(t / 100.0, 0.01, 1.0);
            double thickness = ring.StrokeThickness;
            double diameter = Math.Max(1, (double.IsNaN(ring.ActualWidth) || ring.ActualWidth <= 0 ? 124 : ring.ActualWidth) - thickness);
            double circumferenceInThickness = Math.PI * diameter / thickness;   // StrokeDashArray kalınlık birimindedir
            ring.StrokeDashArray = new DoubleCollection { fraction * circumferenceInThickness, circumferenceInThickness * 2 };
            ring.Stroke = (Brush)FindResource(t >= limit ? "Red" : "Accent");
            ring.Visibility = Visibility.Visible;
            label.Text = $"{Math.Round(t)}°";
        }

        /// <summary>Aktif oyun kartı: tepsinin takip ettiği oyun oturumu + uygulanan preset.</summary>
        private void RenderOverviewGame()
        {
            string? game = _context.ActiveGame;
            if (game != null)
            {
                string display = _context.Config.DiscoveredGameNames.TryGetValue(game, out var name) && !string.IsNullOrEmpty(name)
                    ? name : game;
                txtOvGameState.Text = Loc.Get("OvInGame");
                txtOvGameState.Foreground = (Brush)FindResource("Accent");
                dotOvGame.Fill = (Brush)FindResource("Accent");
                runOvGame.Text = display;
                runOvPreset.Text = string.IsNullOrEmpty(_lastPresetName) ? "" : "  ·  " + _lastPresetName;

                var elapsed = DateTime.Now - (_context.ActiveGameSince ?? DateTime.Now);
                txtOvSession.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
                lblOvSession.Text = Loc.Get("OvSession");
            }
            else
            {
                txtOvGameState.Text = Loc.Get("OvDesktop");
                txtOvGameState.Foreground = (Brush)FindResource("TxtSecond");
                dotOvGame.Fill = (Brush)FindResource("TxtMuted");
                runOvGame.Text = Loc.Get("OvDesktopHint");
                runOvPreset.Text = "";
                txtOvSession.Text = "";
                lblOvSession.Text = "";
            }
        }

        private void RenderOverviewChips()
        {
            void Chip(TextBlock value, string text, string brushKey)
            {
                value.Text = text;
                value.Foreground = brushKey.StartsWith('#') ? BrushFrom(brushKey) : (Brush)FindResource(brushKey);
            }
            const string Good = "#7EE787", Warn = "#FFD166";

            Chip(txtOvChipSonar, Loc.Get(_sonarConnected ? "OvConnected" : "OvDisconnected"), _sonarConnected ? Good : Warn);
            Chip(txtOvChipAfterburner,
                 _afterburnerFound == null ? "…" : Loc.Get(_afterburnerFound.Value ? "OvReady" : "OvMissing"),
                 _afterburnerFound == true ? Good : Warn);
            Chip(txtOvChipAlarm,
                 _context.Config.TempAlarmEnabled ? $"{_context.Config.CpuTempLimit}° / {_context.Config.GpuTempLimit}°" : Loc.Get("OvOff"),
                 "TxtPrimary");
            Chip(txtOvChipPawnIo,
                 _pawnIoInstalled == null ? "…" : Loc.Get(_pawnIoInstalled.Value ? "OvReady" : "OvMissing"),
                 _pawnIoInstalled == true ? Good : Warn);
            Chip(txtOvChipRecord, Loc.Get(_context.Recorder.IsRecording ? "OvRecording" : "OvIdle"),
                 _context.Recorder.IsRecording ? "Red" : "TxtPrimary");

            var update = _context.LastUpdateResult;
            if (update is { Status: UpdateCheckStatus.UpdateAvailable, Update: { } pending })
                Chip(txtOvChipUpdate, Loc.Format("OvUpdateAvailable", pending.Tag), "Accent");
            else
                Chip(txtOvChipUpdate, "v" + UpdateService.CurrentVersion.ToString(3), "TxtPrimary");
        }

        private void DrawOverviewChart(TelemetrySnapshot snapshot)
        {
            double width = cvsOverviewChart.ActualWidth, height = cvsOverviewChart.ActualHeight;
            if (width <= 20 || height <= 20) return;
            cvsOverviewChart.Children.Clear();

            for (int i = 1; i <= 3; i++)
            {
                double y = height * i / 4.0;
                cvsOverviewChart.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = 0, X2 = width, Y1 = y, Y2 = y,
                    Stroke = BrushFrom("#1E1E27"), StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 4 }
                });
            }

            var all = snapshot.AllSensors;
            var cpu = all.FirstOrDefault(x => x.Category == "CPU" && x.SensorType == SensorType.Temperature);
            var gpu = all.FirstOrDefault(x => x.Category == "GPU" && x.SensorType == SensorType.Temperature &&
                                              x.RawName.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                      ?? all.FirstOrDefault(x => x.Category == "GPU" && x.SensorType == SensorType.Temperature);

            float max = 100f;
            foreach (var s in new[] { cpu, gpu })
                if (s != null && s.History.Count > 0) max = Math.Max(max, s.History.Max() + 5);

            void DrawLine(TelemetrySensorItem? sensor, Brush brush)
            {
                if (sensor == null || sensor.History.Count < 2) return;
                var points = new PointCollection(sensor.History.Count);
                double step = width / 59.0;
                int count = sensor.History.Count;
                for (int i = 0; i < count; i++)
                {
                    double x = width - (count - 1 - i) * step;
                    double y = height - sensor.History[i] / max * (height - 8) - 4;
                    points.Add(new Point(x, Math.Clamp(y, 2, height - 2)));
                }
                points.Freeze();
                cvsOverviewChart.Children.Add(new System.Windows.Shapes.Polyline
                {
                    Points = points, Stroke = brush, StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round
                });
            }

            DrawLine(gpu, new SolidColorBrush(OverviewGpuLineColor));
            DrawLine(cpu, (Brush)FindResource("Accent"));
        }

        /// <summary>Tepsi oyun oturumu değiştiğinde çağırır (UI thread).</summary>
        public void OnGameSessionChanged()
        {
            if (_currentPage == "overview" && IsVisible) RenderOverviewGame();
        }

        private void CvsOverviewChart_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_lastOverviewSnapshot != null && _currentPage == "overview") DrawOverviewChart(_lastOverviewSnapshot);
        }

        private void BtnOvScan_Click(object sender, RoutedEventArgs e)
        {
            // Tarama sonucu kuralların yanında anlam kazanır: Sonar sayfasına geç ve orada tara
            ShowPage("profiles");
            BtnScan_Click(btnScan, e);
        }

        // ══════════════════════════════════════════════════════════════
        //  Temperature alarm settings
        // ══════════════════════════════════════════════════════════════
        private void ApplyTempLimitsFromUi()
        {
            static int Parse(string text, int fallback)
                => int.TryParse(text, out int v) ? Math.Clamp(v, AppConfig.MinTempLimit, AppConfig.MaxTempLimit) : fallback;

            _context.Config.CpuTempLimit = Parse(txtCpuTempLimit.Text, _context.Config.CpuTempLimit);
            _context.Config.GpuTempLimit = Parse(txtGpuTempLimit.Text, _context.Config.GpuTempLimit);
            txtCpuTempLimit.Text = _context.Config.CpuTempLimit.ToString();
            txtGpuTempLimit.Text = _context.Config.GpuTempLimit.ToString();
        }

        private void TxtTempLimit_LostFocus(object sender, RoutedEventArgs e) => AutoSaveSettings();

        // ══════════════════════════════════════════════════════════════
        //  Session recording (CSV)
        // ══════════════════════════════════════════════════════════════
        /// <summary>Kayıt kartını tepsideki kaydedicinin durumuna göre günceller.</summary>
        public void UpdateRecordingState()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateRecordingState));
                return;
            }

            var rec = _context.Recorder;
            if (_currentPage == "overview") RenderOverviewChips();
            if (rec.IsRecording)
            {
                string kind = rec.IsAutomatic ? Loc.Get("RecordKindAuto") : Loc.Get("RecordKindManual");
                txtRecordingStatus.Text = Loc.Format("RecordActive", rec.Label ?? "", kind,
                                                     System.IO.Path.GetFileName(rec.CurrentFile ?? ""));
                dotRecording.Fill = (Brush)FindResource("Red");
                lblRecordBtn.Text = Loc.Get("RecordStop");
                btnRecord.Style = (Style)FindResource("DangerButton");
                lblOvRecord.Text = Loc.Get("OvRecordStop");
                btnOvRecord.Style = (Style)FindResource("DangerButton");
            }
            else
            {
                txtRecordingStatus.Text = Loc.Get("RecordIdle");
                dotRecording.Fill = (Brush)FindResource("TxtMuted");
                lblRecordBtn.Text = Loc.Get("RecordStart");
                btnRecord.Style = (Style)FindResource("AccentButton");
                lblOvRecord.Text = Loc.Get("OvRecordStart");
                btnOvRecord.Style = (Style)FindResource("AccentButton");
            }
        }

        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_context.Recorder.IsRecording) _context.Recorder.Stop();
            else _context.StartManualRecording();
            UpdateRecordingState();
        }

        private void BtnOpenSessions_Click(object sender, RoutedEventArgs e) => _context.OpenSessionsFolder();

        private void ChkAutoRecord_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _isPopulatingControls) return;
            _context.Config.AutoRecordGameSessions = chkAutoRecord.IsChecked == true;
            // Kapatılırsa süren otomatik kayıt da biter (elle başlatılana dokunulmaz)
            if (!_context.Config.AutoRecordGameSessions && _context.Recorder.IsRecording && _context.Recorder.IsAutomatic)
                _context.Recorder.Stop();
            _context.SaveConfig();
        }

        // ══════════════════════════════════════════════════════════════
        //  System status page
        // ══════════════════════════════════════════════════════════════
        private enum CheckState { Ok, Warning, Error, Pending }

        private sealed record StatusCheck(CheckState State, string Title, string Detail, string? LinkText = null, string? Url = null);

        private int _statusRunId;

        private async Task RunStatusChecksAsync()
        {
            int runId = ++_statusRunId;
            btnRecheckStatus.IsEnabled = false;
            spStatusChecks.Children.Clear();
            spStatusChecks.Children.Add(BuildStatusRow(new StatusCheck(CheckState.Pending, Loc.Get("StatusChecking"), "")));

            try
            {
                var checks = new List<StatusCheck>();

                // Yönetici yetkisi + kurulum konumu (ACL taraması dosya sayısına göre birkaç yüz ms sürebilir)
                bool isAdmin = false, insecure = true;
                string insecureReason = "";
                await Task.Run(() =>
                {
                    using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                    isAdmin = new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                    insecure = InstallLocationGuard.IsWritableByNonAdmins(Environment.ProcessPath ?? "", out insecureReason);
                });

                checks.Add(isAdmin
                    ? new StatusCheck(CheckState.Ok, Loc.Get("StatusAdmin"), Loc.Get("StatusAdminOk"))
                    : new StatusCheck(CheckState.Error, Loc.Get("StatusAdmin"), Loc.Get("StatusAdminMissing")));

                checks.Add(insecure
                    ? new StatusCheck(CheckState.Warning, Loc.Get("StatusInstall"), Loc.Format("StatusInstallInsecure", insecureReason))
                    : new StatusCheck(CheckState.Ok, Loc.Get("StatusInstall"), Loc.Format("StatusInstallOk",
                        System.IO.Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "")));

                // Kapalı modüllerin bağımlılıkları "eksik" diye uyarılmaz (ve GG'ye bağlanılmaz)
                var cfg = _context.Config;
                string moduleOff = Loc.Get("StatusModuleOff");

                // SteelSeries GG + Sonar
                bool ggInstalled = false;
                if (cfg.ModuleSonar)
                {
                    try { _client.GetCorePropsPath(); ggInstalled = true; } catch { ggInstalled = false; }
                }

                if (!cfg.ModuleSonar)
                {
                    checks.Add(new StatusCheck(CheckState.Ok, "SteelSeries GG", moduleOff));
                }
                else if (!ggInstalled)
                {
                    checks.Add(new StatusCheck(CheckState.Warning, "SteelSeries GG", Loc.Get("StatusGgMissing"),
                                               Loc.Get("StatusDownload"), "https://steelseries.com/gg"));
                }
                else
                {
                    checks.Add(new StatusCheck(CheckState.Ok, "SteelSeries GG", Loc.Get("StatusGgOk")));
                    try
                    {
                        var addressTask = _client.GetSonarAddressAsync();
                        if (await Task.WhenAny(addressTask, Task.Delay(8000)) != addressTask)
                            throw new TimeoutException();
                        string address = await addressTask;
                        checks.Add(new StatusCheck(CheckState.Ok, "Sonar", Loc.Format("StatusSonarOk", address)));
                    }
                    catch
                    {
                        checks.Add(new StatusCheck(CheckState.Warning, "Sonar", Loc.Get("StatusSonarMissing")));
                    }
                }

                // MSI Afterburner
                string? afterburner = cfg.GpuProfileActive ? await Task.Run(() => _context.AfterburnerPath) : null;
                checks.Add(!cfg.GpuProfileActive
                    ? new StatusCheck(CheckState.Ok, "MSI Afterburner", moduleOff)
                    : afterburner != null
                    ? new StatusCheck(CheckState.Ok, "MSI Afterburner", afterburner)
                    : new StatusCheck(CheckState.Warning, "MSI Afterburner", Loc.Get("StatusAfterburnerMissing"),
                                      Loc.Get("StatusDownload"), "https://www.msi.com/Landing/afterburner/graphics-cards"));

                if (!cfg.ModuleHardware)
                {
                    // Sensörler çalışmıyor: PawnIO / GPU kontrolü (ve 10 sn bekleme) gereksiz
                    checks.Add(new StatusCheck(CheckState.Ok, Loc.Get("StatusGpu"), moduleOff));
                }
                else
                {
                    // PawnIO
                    checks.Add(HardwareMonitorService.IsPawnIoInstalled
                        ? new StatusCheck(CheckState.Ok, "PawnIO", Loc.Get("StatusPawnIoOk"))
                        : new StatusCheck(CheckState.Warning, "PawnIO", Loc.Get("StatusPawnIoMissing"),
                                          Loc.Get("StatusDownload"), "https://pawnio.eu/"));

                    // GPU (telemetri bu sayfa açıkken çalışır; ilk tarama birkaç saniye sürebilir)
                    for (int i = 0; i < 40 && _context.LastSnapshot?.HardwareReady != true; i++)
                        await Task.Delay(250);
                    var gpu = _context.LastSnapshot?.PrimaryGpu;
                    checks.Add(gpu != null
                        ? new StatusCheck(CheckState.Ok, Loc.Get("StatusGpu"), $"{gpu.Vendor} · {gpu.Name}")
                        : new StatusCheck(CheckState.Warning, Loc.Get("StatusGpu"), Loc.Get("StatusGpuMissing")));
                }

                // Güncelleme
                var update = _context.LastUpdateResult;
                string installed = Loc.Format("UpdateCurrentVersion", UpdateService.CurrentVersion.ToString(3));
                if (update is { Status: UpdateCheckStatus.UpdateAvailable, Update: { } pending })
                    checks.Add(new StatusCheck(CheckState.Warning, Loc.Get("StatusUpdate"), Loc.Format("UpdateAvailable", pending.Tag)));
                else if (update is { Status: UpdateCheckStatus.UpToDate })
                    checks.Add(new StatusCheck(CheckState.Ok, Loc.Get("StatusUpdate"), installed));
                else
                    checks.Add(new StatusCheck(CheckState.Pending, Loc.Get("StatusUpdate"), installed));

                if (runId != _statusRunId) return;   // bu arada yeniden başlatıldı
                spStatusChecks.Children.Clear();
                foreach (var c in checks) spStatusChecks.Children.Add(BuildStatusRow(c));
            }
            finally
            {
                if (runId == _statusRunId) btnRecheckStatus.IsEnabled = true;
            }
        }

        private void BtnRecheckStatus_Click(object sender, RoutedEventArgs e) => _ = RunStatusChecksAsync();

        private UIElement BuildStatusRow(StatusCheck check)
        {
            (string icon, string color) = check.State switch
            {
                CheckState.Ok => ("✔", "#7EE787"),
                CheckState.Warning => ("!", "#FACC15"),
                CheckState.Error => ("✖", "#FF3B5C"),
                _ => ("…", "#A3A3B2")
            };

            var row = new Border
            {
                Background = (Brush)FindResource("BgInput"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconText = new TextBlock
            {
                Text = icon,
                Foreground = BrushFrom(color),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(iconText);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock
            {
                Text = check.Title,
                Foreground = (Brush)FindResource("TxtPrimary"),
                FontWeight = FontWeights.SemiBold
            });
            if (!string.IsNullOrEmpty(check.Detail))
            {
                texts.Children.Add(new TextBlock
                {
                    Text = check.Detail,
                    Foreground = (Brush)FindResource("TxtSecond"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 12, 0)
                });
            }
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            if (check.Url != null)
            {
                var btn = new Button
                {
                    Content = check.LinkText,
                    Style = (Style)FindResource("FlatButton"),
                    Padding = new Thickness(12, 6, 12, 6),
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = check.Url
                };
                btn.Click += (s, e) => { if (s is Button b && b.Tag is string url) OpenUrl(url); };
                Grid.SetColumn(btn, 2);
                grid.Children.Add(btn);
            }

            row.Child = grid;
            return row;
        }

        /// <summary>Bağlantıyı yükseltilmemiş kabuk üzerinden açar (tarayıcı yönetici yetkisi almasın).</summary>
        private static void OpenUrl(string url)
        {
            try
            {
                using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{url}\"",
                    UseShellExecute = false
                });
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════
        //  Updates
        // ══════════════════════════════════════════════════════════════
        private bool _updateBusy;

        /// <summary>Kontrol sonucunu ayarlar sayfasında gösterir. null = kontrol sürüyor.</summary>
        public void ShowUpdateStatus(UpdateCheckResult? result, bool fromLanguageChange = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowUpdateStatus(result, fromLanguageChange)));
                return;
            }
            if (_updateBusy && fromLanguageChange) return;   // indirme mesajını dil değişimi ezmesin

            var pending = _context.PendingUpdate;
            btnInstallUpdate.Visibility = pending != null ? Visibility.Visible : Visibility.Collapsed;
            if (pending != null) lblInstallUpdate.Text = Loc.Format("BtnInstallUpdate", pending.Tag);

            if (result == null)
            {
                txtUpdateStatus.Text = fromLanguageChange ? "" : Loc.Get("UpdateChecking");
                txtUpdateStatus.Foreground = (Brush)FindResource("TxtSecond");
                return;
            }

            (string text, string brushKey) = result.Status switch
            {
                UpdateCheckStatus.UpdateAvailable when result.Update != null =>
                    (Loc.Format("UpdateAvailable", result.Update.Tag), "Accent"),
                UpdateCheckStatus.UpToDate => (Loc.Get("UpdateUpToDate"), "TxtSecond"),
                UpdateCheckStatus.Unavailable => (Loc.Get("UpdateUnavailable"), "TxtSecond"),
                _ => (Loc.Format("UpdateError", result.Message), "Red")
            };
            txtUpdateStatus.Text = text;
            txtUpdateStatus.Foreground = (Brush)FindResource(brushKey);
        }

        /// <summary>İndirme/kurulum ilerlemesini gösterir; busy iken butonlar kilitlenir.</summary>
        public void ShowUpdateMessage(string message, bool busy)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowUpdateMessage(message, busy)));
                return;
            }
            _updateBusy = busy;
            txtUpdateStatus.Text = message;
            txtUpdateStatus.Foreground = (Brush)FindResource(busy ? "Accent" : "TxtSecond");
            btnCheckUpdates.IsEnabled = !busy;
            btnInstallUpdate.IsEnabled = !busy;
        }

        private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            btnCheckUpdates.IsEnabled = false;
            try
            {
                await _context.CheckForUpdatesAsync(manual: true);
            }
            finally
            {
                if (!_updateBusy) btnCheckUpdates.IsEnabled = true;
            }
        }

        private async void BtnInstallUpdate_Click(object sender, RoutedEventArgs e)
        {
            await _context.ConfirmAndInstallUpdateAsync();
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

        /// <summary>GPU sayfasını birincil GPU ile günceller (NVIDIA, AMD veya Intel). Her thread'den çağrılabilir.</summary>
        public void UpdateGpuData(TelemetrySnapshot snapshot)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateGpuData(snapshot)));
                return;
            }
            // Pencere tepsideyken görünmeyen metinleri güncelleme; gösterilince son veri basılır
            if (!IsVisible) return;

            var data = snapshot.PrimaryGpu;
            if (data == null)
            {
                txtGpuName.Text = snapshot.HardwareReady ? Loc.Get("GpuNotFound") : Loc.Get("GpuDetecting");
                txtGpuClock.Text = txtGpuTemp.Text = txtGpuUsage.Text = "–";
                txtGpuClock.Foreground = (Brush)FindResource("TxtPrimary");
                return;
            }

            txtGpuName.Text = data.Name;
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
            txtVersionBadge.Text        = "v" + UpdateService.CurrentVersion.ToString(3);
            btnMinimizeTitle.ToolTip    = Loc.Get("ToolTipMinimize");
            btnMaximizeTitle.ToolTip    = Loc.Get("ToolTipMaximize");
            btnCloseTitle.ToolTip       = Loc.Get("ToolTipClose");

            // Sidebar Navigation Tooltips
            btnNavOverview.ToolTip      = Loc.Get("NavOverview");
            btnNavHome.ToolTip          = Loc.Get("NavHome");
            lblNavOverview.Text         = Loc.Get("NavShortOverview");
            lblNavHome.Text             = Loc.Get("NavShortSonar");
            lblNavDevice.Text           = Loc.Get("NavShortAudio");
            lblNavGpu.Text              = Loc.Get("NavShortGpu");
            lblNavTelemetry.Text        = Loc.Get("NavShortSensors");
            lblNavStatus.Text           = Loc.Get("NavShortStatus");
            lblNavHistory.Text          = Loc.Get("NavShortHistory");
            lblNavBenchmark.Text        = Loc.Get("NavShortBenchmark");
            lblBenchmarkHeader.Text     = Loc.Get("BenchmarkHeader");
            lblBenchmarkDesc.Text       = Loc.Get("BenchmarkDesc");
            lblBenchmarkLabel.Text      = Loc.Get("BenchmarkLabelCaption");
            lblBenchmarkRunsSection.Text = Loc.Get("BenchmarkRunsSection");
            lblBenchmarkEfficiency.Text = Loc.Get("BenchmarkEfficiency");
            lblBenchmarkCompareSection.Text = Loc.Get("BenchmarkCompareSection");
            lblBenchmarkExport.Text     = Loc.Get("BenchmarkExport");
            lblModuleBenchmark.Text     = Loc.Get("ModuleBenchmark");
            lblModuleBenchmarkDesc.Text = Loc.Get("ModuleBenchmarkDesc");
            lblBenchmarkHotkey.Text     = Loc.Get("BenchmarkHotkey");
            lblBenchmarkWarmup.Text     = Loc.Get("BenchmarkWarmup");
            lblBenchmarkWarmupDesc.Text = Loc.Get("BenchmarkWarmupDesc");
            lblHistoryHeader.Text       = Loc.Get("HistoryHeader");
            lblHistoryDesc.Text         = Loc.Get("HistoryDesc");
            txtHistoryEmpty.Text        = Loc.Get("HistoryEmpty");
            lblHistoryGamesSection.Text    = Loc.Get("HistoryGamesSection");
            lblHistorySessionsSection.Text = Loc.Get("HistorySessionsSection");
            lblHistoryDiskSection.Text  = Loc.Get("HistoryDiskSection");
            lblHistoryDiskDesc.Text     = Loc.Get("HistoryDiskDesc");
            lblNavSettings.Text         = Loc.Get("NavShortSettings");
            lblOverviewHeader.Text      = Loc.Get("OvTitle");
            lblOverviewDesc.Text        = Loc.Get("OvDesc");
            lblOvScan.Text              = Loc.Get("OvScan");
            lblOvGraph.Text             = Loc.Get("OvGraph");
            lblOvTileRam.Text           = Loc.Get("OvTileRam");
            lblOvTileHotSpot.Text       = Loc.Get("OvTileHotSpot");
            lblOvTileFan.Text           = Loc.Get("OvTileFan");
            lblOvTileCpuPower.Text      = Loc.Get("OvTileCpuPower");
            lblOvChipRecord.Text        = Loc.Get("OvChipRecord");
            lblOvChipUpdate.Text        = Loc.Get("OvChipUpdate");
            btnNavDevice.ToolTip        = Loc.Get("NavDeviceManager");
            btnNavGpu.ToolTip           = Loc.Get("NavGpuMonitor");
            btnNavTelemetry.ToolTip     = Loc.Get("NavTelemetry");
            btnNavSettings.ToolTip      = Loc.Get("NavSettings");
            btnNavStatus.ToolTip        = Loc.Get("NavStatus");

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
            lblAutoSaveHint.Text        = Loc.Get("AutoSaveHint");
            lblTempAlarm.Text           = Loc.Get("TempAlarmTitle");
            lblTempAlarmDesc.Text       = Loc.Get("TempAlarmDesc");
            lblSessionSummary.Text      = Loc.Get("SessionSummary");
            lblSessionSummaryDesc.Text  = Loc.Get("SessionSummaryDesc");
            lblCpuTempLimit.Text        = Loc.Get("CpuTempLimit");
            lblGpuTempLimit.Text        = Loc.Get("GpuTempLimit");
            lblAutoRecord.Text          = Loc.Get("RecordAuto");
            lblOpenSessions.Text        = Loc.Get("RecordOpenFolder");
            lblStatusHeader.Text        = Loc.Get("StatusHeader");
            lblStatusDesc.Text          = Loc.Get("StatusDesc");
            lblRecheckStatus.Text       = Loc.Get("StatusRecheck");
            UpdateRecordingState();
            lblUpdatesSection.Text      = Loc.Get("UpdatesSection");
            lblUpdateAutoCheck.Text     = Loc.Get("UpdateAutoCheck");
            lblUpdateAutoCheckDesc.Text = Loc.Get("UpdateAutoCheckDesc");
            lblGpuSettingsHint.Text     = Loc.Get("GpuSettingsHint");
            lblGoGpu.Text               = Loc.Get("GoToGpuPage");
            lblDeviceSettingsHint.Text  = Loc.Get("DeviceSettingsHint");
            lblGoDevices.Text           = Loc.Get("GoToDevicesPage");
            lblAutoScan.Text            = Loc.Get("AutoScan");
            lblAutoScanDesc.Text        = Loc.Get("AutoScanDesc");
            lblAutoAssign.Text          = Loc.Get("AutoAssign");
            lblAutoAssignDesc.Text      = Loc.Get("AutoAssignDesc");
            lblModulesSection.Text      = Loc.Get("ModulesSection");
            lblModuleSonar.Text         = Loc.Get("ModuleSonar");
            lblModuleSonarDesc.Text     = Loc.Get("ModuleSonarDesc");
            lblModuleAudioDevices.Text     = Loc.Get("ModuleAudioDevices");
            lblModuleAudioDevicesDesc.Text = Loc.Get("ModuleAudioDevicesDesc");
            lblModuleHardware.Text      = Loc.Get("ModuleHardware");
            lblModuleHardwareDesc.Text  = Loc.Get("ModuleHardwareDesc");
            lblModuleGpuProfile.Text     = Loc.Get("ModuleGpuProfile");
            lblModuleGpuProfileDesc.Text = Loc.Get("ModuleGpuProfileDesc");
            txtModuleOffTitle.Text      = Loc.Get("ModuleOffTitle");
            lblModuleEnable.Text        = Loc.Get("ModuleEnable");
            lblCheckUpdates.Text        = Loc.Get("BtnCheckUpdates");
            txtCurrentVersion.Text      = Loc.Format("UpdateCurrentVersion", UpdateService.CurrentVersion.ToString(3));
            ShowUpdateStatus(_context.LastUpdateResult, fromLanguageChange: true);

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
        /// <summary>Görünen sayfaya göre tepsiye telemetri ihtiyacını bildirir.</summary>
        private void ApplyPageTelemetryDemand()
        {
            bool visible = IsVisible;
            _context.SetUiTelemetryDemand("ui-telemetry", visible && _currentPage == "telemetry");
            _context.SetUiTelemetryDemand("ui-overview", visible && _currentPage == "overview");
            _context.SetUiTelemetryDemand("ui-gpu", visible && _currentPage == "gpu");
            _context.SetUiTelemetryDemand("ui-status", visible && _currentPage == "status");

            if (visible && _currentPage == "telemetry")
            {
                pnlPawnIoHint.Visibility = HardwareMonitorService.IsPawnIoInstalled ? Visibility.Collapsed : Visibility.Visible;
                UpdateRecordingState();
            }
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

        private void OnTelemetryUpdated(object? sender, TelemetrySnapshot snapshot)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<object?, TelemetrySnapshot>(OnTelemetryUpdated), sender, snapshot);
                return;
            }

            if (!IsVisible) return;
            if (_currentPage == "overview")
            {
                RenderOverview(snapshot);
                return;
            }
            if (_currentPage != "telemetry") return;

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
                    Background = BrushFrom("#101016"),
                    BorderBrush = BrushFrom("#2A2A35"),
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
                    Background = BrushFrom("#2A1C10"),
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
                    Background = BrushFrom("#101016"),
                    BorderBrush = BrushFrom("#2A2A35"),
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
                    Foreground = BrushFrom("#D6D6E0"),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                sp.Children.Add(txt);

                var btnRemove = new Button
                {
                    Content = "✕",
                    Foreground = BrushFrom("#7A7A88"),
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
                            row.btnStar.Foreground = BrushFrom(item.IsFavorite ? "#FACC15" : "#4A4A58");
                            row.btnStar.ToolTip = item.IsFavorite ? Loc.Get("UnpinFromFavorites") : Loc.Get("PinToFavorites");
                            row.btnGraph.Foreground = BrushFrom(item.IsOnGraph ? item.GraphColor : "#4A4A58");
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
                            : BrushFrom("#4A4A58"),
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
                            : BrushFrom("#4A4A58"),
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
            Telemetry.ToggleFavorite(sensorId);

            _context.Config.TelemetryFavorites = Telemetry.GetFavoriteIds();
            _context.SaveConfig();
        }

        private void ToggleGraph(string sensorId)
        {
            Telemetry.ToggleGraph(sensorId);

            _context.Config.TelemetryGraphSensors = Telemetry.GetGraphSensorIds();
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
                    Stroke = BrushFrom("#1C1C24"),
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
            _lastPresetName = presetName;
            if (_currentPage == "overview" && IsVisible) RenderOverviewGame();
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
