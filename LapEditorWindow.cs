using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Orientation = System.Windows.Controls.Orientation;
using Cursors = System.Windows.Input.Cursors;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Warden
{
    /// <summary>
    /// Bir benchmark kaydını video düzenler gibi turlara bölme penceresi. GPU yükü grafiğine tıklamak keser, kesim
    /// çizgisi sürüklenir, sağ tık siler; üst şeride tıklamak bölgeyi dahil eder / dışarıda bırakır.
    /// Kaydet: dahil edilen bölgeler run.Segments olur (tek bölge kaydın tamamıysa Segments = null).
    /// </summary>
    public sealed class LapEditorWindow : Window
    {
        private sealed class Region
        {
            public double Start, End;
            public bool Included = true;
            public string Label = "";
            public bool Edited;
        }

        private const double PadLeft = 40, PadRight = 16, StripHeight = 24, HitPx = 7, MinRegion = 1;

        private readonly BenchmarkRun _run;
        private readonly double _warmup;
        private readonly double _duration;
        private List<Region> _regions = new();

        private readonly Canvas _load = new() { Background = Brushes.Transparent, ClipToBounds = true, Height = 220 };
        private readonly Canvas _fps = new() { Background = Brushes.Transparent, ClipToBounds = true, Height = 80 };
        private readonly StackPanel _list = new();
        private int _dragCut = -1;   // sürüklenen kesim: bölge i ile i+1 arası

        public LapEditorWindow(Window owner, BenchmarkRun run, double warmupSeconds)
        {
            Owner = owner;
            Resources.MergedDictionaries.Add(owner.Resources);
            _run = run;
            _warmup = warmupSeconds;
            _duration = Math.Max(1, run.Samples.Count > 0 ? run.Samples.Max(s => s.T) : run.DurationSeconds);

            Title = Loc.Format("LapEditorTitle", run.Label);
            Width = 980;
            Height = 660;
            MinWidth = 700;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Res("BgMain");
            Foreground = Res("TxtPrimary");

            if (run.Segments is { Count: > 0 }) LoadFromSegments(run.Segments);
            else AutoSplit();

            Content = BuildLayout();
            _load.SizeChanged += (s, e) => Redraw();
            _fps.SizeChanged += (s, e) => Redraw();
            _load.MouseLeftButtonDown += OnLoadDown;
            _load.MouseMove += OnLoadMove;
            _load.MouseLeftButtonUp += (s, e) => { _dragCut = -1; _load.ReleaseMouseCapture(); RebuildList(); };
            _load.MouseRightButtonUp += OnLoadRightUp;
            RebuildList();
        }

        private Brush Res(string key) => (Brush)FindResource(key);

        private UIElement BuildLayout()
        {
            var root = new DockPanel { Margin = new Thickness(20, 16, 20, 16) };

            var hint = new TextBlock { Text = Loc.Get("LapEditorHint"), Foreground = Res("TxtSecond"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(hint, Dock.Top);
            root.Children.Add(hint);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            Button Btn(string key, string style, RoutedEventHandler click)
            {
                var b = new Button { Content = Loc.Get(key), Style = (Style)FindResource(style), Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(8, 0, 0, 0) };
                b.Click += click;
                return b;
            }
            buttons.Children.Add(Btn("LapAuto", "FlatButton", (s, e) => { AutoSplit(); Redraw(); RebuildList(); }));
            buttons.Children.Add(Btn("LapClear", "FlatButton", (s, e) => { _regions = new() { NewRegion(0, _duration) }; Redraw(); RebuildList(); }));
            buttons.Children.Add(Btn("LapCancel", "FlatButton", (s, e) => { DialogResult = false; }));
            buttons.Children.Add(Btn("LapSave", "AccentButton", (s, e) => { Save(); DialogResult = true; }));
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var charts = new StackPanel();
            charts.Children.Add(new TextBlock { Text = Loc.Get("LapEditorLoad"), Foreground = Res("TxtSecond"), FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            charts.Children.Add(new Border { Background = Res("BgCard"), BorderBrush = Res("Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = _load, Cursor = Cursors.Cross });
            charts.Children.Add(new TextBlock { Text = "FPS", Foreground = Res("TxtSecond"), FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4) });
            charts.Children.Add(new Border { Background = Res("BgCard"), BorderBrush = Res("Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = _fps });
            DockPanel.SetDock(charts, Dock.Top);
            root.Children.Add(charts);

            root.Children.Add(new ScrollViewer
            {
                Margin = new Thickness(0, 12, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _list
            });
            return root;
        }

        // ── Bölge işlemleri ─────────────────────────────────────────────

        private Region NewRegion(double start, double end)
        {
            var r = new Region { Start = start, End = end };
            r.Label = Suggest(r);
            return r;
        }

        private string Suggest(Region r)
            => LapLabels.Suggest(_run, new BenchmarkSegment { Start = r.Start, End = r.End }, _warmup);

        private void Resuggest(Region r)
        {
            if (!r.Edited) r.Label = Suggest(r);
        }

        /// <summary>Otomatik turlar dahil, aradaki boşluklar (menü, yükleme) dışarıda.</summary>
        private void AutoSplit()
        {
            var laps = BenchmarkLaps.AutoSplit(_run);
            if (laps.Count == 0) { _regions = new() { NewRegion(0, _duration) }; return; }
            LoadFromSegments(laps, includeEdited: false);
        }

        private void LoadFromSegments(IEnumerable<BenchmarkSegment> segments, bool includeEdited = true)
        {
            _regions = new();
            double cursor = 0;
            foreach (var s in segments.OrderBy(s => s.Start))
            {
                if (s.Start - cursor >= MinRegion) _regions.Add(new Region { Start = cursor, End = s.Start, Included = false, Label = Loc.Get("LapGap") });
                var r = new Region { Start = s.Start, End = s.End, Label = s.Label, Edited = includeEdited && s.LabelEdited };
                if (string.IsNullOrWhiteSpace(r.Label) || !r.Edited) r.Label = Suggest(r);
                _regions.Add(r);
                cursor = s.End;
            }
            if (_duration - cursor >= MinRegion) _regions.Add(new Region { Start = cursor, End = _duration, Included = false, Label = Loc.Get("LapGap") });
        }

        private void AddCut(double t)
        {
            int i = _regions.FindIndex(r => t > r.Start + MinRegion && t < r.End - MinRegion);
            if (i < 0) return;
            var a = _regions[i];
            var b = new Region { Start = t, End = a.End, Included = a.Included };
            a.End = t;
            Resuggest(a);
            b.Label = Suggest(b);
            _regions.Insert(i + 1, b);
        }

        private void RemoveCut(int i)
        {
            var a = _regions[i];
            var b = _regions[i + 1];
            a.End = b.End;
            a.Included |= b.Included;
            Resuggest(a);
            _regions.RemoveAt(i + 1);
        }

        private void Save()
        {
            var included = _regions.Where(r => r.Included && r.End - r.Start >= MinRegion).ToList();
            bool whole = included.Count == 1 && included[0].Start <= 0.5 && included[0].End >= _duration - 0.5 && _regions.Count == 1;
            _run.Segments = whole || included.Count == 0
                ? null
                : included.Select(r => new BenchmarkSegment { Start = r.Start, End = r.End, Label = r.Label.Trim(), LabelEdited = r.Edited }).ToList();
        }

        // ── Çizim ───────────────────────────────────────────────────────

        private double PlotWidth(Canvas c) => Math.Max(10, c.ActualWidth - PadLeft - PadRight);
        private double X(Canvas c, double t) => PadLeft + t / _duration * PlotWidth(c);
        private double T(Canvas c, double x) => Math.Clamp(Math.Round((x - PadLeft) / PlotWidth(c) * _duration), 0, _duration);

        private void Redraw()
        {
            DrawSeries(_load, s => s.GpuLoad, 0, 100, "%", top: StripHeight);
            var fps = _run.Samples.Where(s => s.Fps is > 0).Select(s => s.Fps!.Value).ToList();
            DrawSeries(_fps, s => s.Fps, 0, fps.Count > 0 ? fps.Max() * 1.1 : 60, "", top: 6);
            DrawRegions();
        }

        private void DrawSeries(Canvas c, Func<BenchmarkSample, double?> value, double min, double max, string unit, double top)
        {
            c.Children.Clear();
            double h = c.ActualHeight - top - 18;
            if (c.ActualWidth < 50 || h < 20) return;
            double Y(double v) => top + (1 - (v - min) / (max - min)) * h;

            foreach (double v in new[] { min, (min + max) / 2, max })
            {
                c.Children.Add(new Line { X1 = PadLeft, X2 = PadLeft + PlotWidth(c), Y1 = Y(v), Y2 = Y(v), Stroke = Res("Border"), StrokeThickness = 1 });
                var l = new TextBlock { Text = $"{v:0}{unit}", Foreground = Res("TxtMuted"), FontSize = 10 };
                Canvas.SetLeft(l, 2);
                Canvas.SetTop(l, Y(v) - 7);
                c.Children.Add(l);
            }
            foreach (double t in new[] { 0, _duration / 2, _duration })
            {
                var l = new TextBlock { Text = $"{(int)t / 60}:{(int)t % 60:00}", Foreground = Res("TxtMuted"), FontSize = 10 };
                Canvas.SetLeft(l, X(c, t) - 12);
                Canvas.SetTop(l, top + h + 3);
                c.Children.Add(l);
            }

            var line = new Polyline { Stroke = Res("Accent"), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
            foreach (var s in _run.Samples.OrderBy(s => s.T))
                if (value(s) is double v) line.Points.Add(new Point(X(c, s.T), Y(v)));
            c.Children.Add(line);
        }

        private void DrawRegions()
        {
            double h = _load.ActualHeight;
            if (_load.ActualWidth < 50) return;
            for (int i = 0; i < _regions.Count; i++)
            {
                var r = _regions[i];
                double x1 = X(_load, r.Start), x2 = X(_load, r.End);

                // Dışarıda bırakılan bölge koyulaştırılır (menü, yükleme)
                if (!r.Included)
                {
                    var shade = new Rectangle { Width = Math.Max(0, x2 - x1), Height = h, Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 8, 8, 11)), IsHitTestVisible = false };
                    Canvas.SetLeft(shade, x1);
                    _load.Children.Add(shade);
                }

                // Üst şerit: tıklanınca dahil / dışarıda; etiketi gösterir
                var strip = new Border
                {
                    Width = Math.Max(0, x2 - x1 - 2), Height = StripHeight - 4,
                    Background = r.Included ? Res("AccentDim") : Brushes.Transparent,
                    BorderBrush = Res("Border"), BorderThickness = new Thickness(r.Included ? 0 : 1), CornerRadius = new CornerRadius(4),
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = r.Included ? $"{i + 1}. {r.Label}" : Loc.Get("LapExcluded"),
                        Foreground = Res(r.Included ? "TxtPrimary" : "TxtMuted"), FontSize = 10.5,
                        TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 2, 6, 0)
                    }
                };
                Canvas.SetLeft(strip, x1 + 1);
                Canvas.SetTop(strip, 2);
                _load.Children.Add(strip);

                if (i < _regions.Count - 1)
                {
                    foreach (var c in new[] { _load, _fps })
                    {
                        double x = X(c, r.End);
                        c.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = c.ActualHeight, Stroke = Res("Accent"), StrokeThickness = 2, IsHitTestVisible = false });
                    }
                }
            }
        }

        // ── Fare ────────────────────────────────────────────────────────

        private int CutNear(double x)
        {
            for (int i = 0; i < _regions.Count - 1; i++)
                if (Math.Abs(X(_load, _regions[i].End) - x) <= HitPx) return i;
            return -1;
        }

        private void OnLoadDown(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(_load);
            if (p.X < PadLeft || p.X > PadLeft + PlotWidth(_load)) return;

            int cut = CutNear(p.X);
            if (cut >= 0)
            {
                _dragCut = cut;
                _load.CaptureMouse();
                return;
            }

            double t = T(_load, p.X);
            if (p.Y <= StripHeight)
            {
                var r = _regions.FirstOrDefault(r => t >= r.Start && t <= r.End);
                if (r != null) r.Included = !r.Included;
            }
            else
            {
                AddCut(t);
            }
            Redraw();
            RebuildList();
        }

        private void OnLoadMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var p = e.GetPosition(_load);
            _load.Cursor = _dragCut >= 0 || CutNear(p.X) >= 0 ? Cursors.SizeWE : p.Y <= StripHeight ? Cursors.Hand : Cursors.Cross;
            if (_dragCut < 0 || e.LeftButton != MouseButtonState.Pressed) return;

            var a = _regions[_dragCut];
            var b = _regions[_dragCut + 1];
            double t = Math.Clamp(T(_load, p.X), a.Start + MinRegion, b.End - MinRegion);
            if (t == a.End) return;
            a.End = b.Start = t;
            Redraw();
        }

        private void OnLoadRightUp(object sender, MouseButtonEventArgs e)
        {
            int cut = CutNear(e.GetPosition(_load).X);
            if (cut < 0) return;
            RemoveCut(cut);
            Redraw();
            RebuildList();
        }

        // ── Bölge listesi ───────────────────────────────────────────────

        private void RebuildList()
        {
            foreach (var r in _regions) Resuggest(r);   // sürükleme sonrası öneriler güncellensin
            _list.Children.Clear();
            for (int i = 0; i < _regions.Count; i++)
            {
                var r = _regions[i];
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var include = new System.Windows.Controls.CheckBox { IsChecked = r.Included, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), ToolTip = Loc.Get("LapIncludeTip") };
                include.Checked += (s, e) => { r.Included = true; Redraw(); };
                include.Unchecked += (s, e) => { r.Included = false; Redraw(); };
                row.Children.Add(include);

                var label = new System.Windows.Controls.TextBox { Text = r.Label, VerticalAlignment = VerticalAlignment.Center };
                label.TextChanged += (s, e) =>
                {
                    if (label.Text == r.Label) return;
                    r.Label = label.Text;
                    r.Edited = true;
                };
                label.LostFocus += (s, e) => Redraw();
                Grid.SetColumn(label, 1);
                row.Children.Add(label);

                var range = new TextBlock
                {
                    Text = $"  {(int)r.Start / 60}:{(int)r.Start % 60:00} – {(int)r.End / 60}:{(int)r.End % 60:00} · {Loc.Duration(TimeSpan.FromSeconds(r.End - r.Start))}",
                    Foreground = Res("TxtSecond"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0)
                };
                Grid.SetColumn(range, 2);
                row.Children.Add(range);

                var reset = new Button { Content = "↺", Style = (Style)FindResource("FlatButton"), Padding = new Thickness(10, 4, 10, 4), ToolTip = Loc.Get("LapResetLabel") };
                reset.Click += (s, e) => { r.Edited = false; r.Label = Suggest(r); RebuildList(); Redraw(); };
                Grid.SetColumn(reset, 3);
                row.Children.Add(reset);

                _list.Children.Add(row);
            }
        }
    }
}
