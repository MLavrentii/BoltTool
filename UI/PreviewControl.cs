using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WS.BoltTool.Geometry;

namespace WS.BoltTool.UI
{
    /// <summary>
    /// Live hidden-line preview. Left-drag rotates the view directly, the round joystick in the corner
    /// rotates continuously while held (further from the centre = faster), wheel zooms, right/middle drag pans,
    /// double-click fits.
    /// </summary>
    public class PreviewControl : FrameworkElement
    {
        public Drawing2D Drawing { get; private set; }
        public List<(Point p, string label)> Markers = new();      // model-space 2D markers (base point)
        public List<(Point p, Vector dir)> Grips = new();          // length grips
        public bool ShowWipeouts = true;
        public string Caption = "";

        /// <summary>Raised with (dAzimuth, dElevation) in degrees; `final` = mouse released.</summary>
        public event Action<double, double, bool> RotateRequested;

        double _zoom = 1; Vector _pan;

        // ---- parts: selection / Ctrl+drag along the bolt ----
        public readonly HashSet<int> SelectableGroups = new();
        public readonly HashSet<int> Highlight = new();
        public (Point a, Point b, string text)? Dim;       // distance marker in model 2D coordinates
        public bool ShowPartHint;
        public event Action<int> PartDown;                 // group under the cursor (-1 = none)
        public event Action<double, bool> PartDrag;        // movement along the axis in mm, final
        public event Action<int, bool> PartUp;             // group, moved
        public event Action<Point> DistanceClicked;        // top-left of the distance label
        double _frR = 1, _frCx, _frCy; bool _hasFrame;
        bool _partDrag, _partMoved; int _partGroup = -1; Point _downPos; Rect _dimRect = Rect.Empty;
        static readonly Brush SelFill = Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0x2F, 0x6F, 0xEB)));
        static readonly Pen SelPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xEB)), 2));
        static readonly Pen DimPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x48)), 1.4));
        Point _last; bool _rotating, _panning;
        bool _joy; Vector _joyVec;
        readonly DispatcherTimer _joyTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
        const double JoyR = 34;

        static readonly Brush Bg = Frozen(new SolidColorBrush(Color.FromRgb(0xFB, 0xFC, 0xFE)));
        static readonly Brush GridBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF6)));
        static readonly Brush WipeFill = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xDC, 0xE6, 0xF5)));
        static readonly Pen LinePen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)), 1.25) { LineJoin = PenLineJoin.Round });
        static readonly Pen ThinPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), 0.8));
        static readonly Pen HiddenPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), 0.8) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) });
        static readonly Pen WipePen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0xB6, 0xC6, 0xE3)), 0.6) { DashStyle = DashStyles.Dot });
        static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xEB)));
        static readonly Brush Muted = Frozen(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
        static readonly Brush Red = Frozen(new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x48)));
        static readonly Brush GripBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x0E, 0xA5, 0xE9)));

        static Brush Frozen(Brush b) { b.Freeze(); return b; }
        static Pen FrozenPen(Pen p) { p.Freeze(); return p; }

        public PreviewControl()
        {
            Focusable = true;
            ClipToBounds = true;
            Cursor = Cursors.SizeAll;
            _joyTimer.Tick += (s, e) =>
            {
                if (!_joy) return;
                RotateRequested?.Invoke(_joyVec.X / JoyR * 4.0, _joyVec.Y / JoyR * 4.0, false);
            };
        }

        public void SetDrawing(Drawing2D d, bool fit, bool keepFrame = false)
        {
            Drawing = d;
            if (!keepFrame || !_hasFrame) { _frR = d.Radius; _frCx = d.CenterX; _frCy = d.CenterY; _hasFrame = true; }
            if (fit) { _zoom = 1; _pan = new Vector(); }
            InvalidateVisual();
        }

        Point JoyCenter => new(ActualWidth - JoyR - 16, ActualHeight - JoyR - 16);

        (double s, double ox, double oy) Fit()
        {
            // scale by the bounding sphere, centre on the projected 3D centre: same distance from every direction
            double margin = 40;
            double s = Math.Max(10, Math.Min(ActualWidth - 2 * margin, ActualHeight - 2 * margin - 20)) / (2 * Math.Max(1e-6, _frR)) * _zoom;
            return (s, ActualWidth / 2 - _frCx * s + _pan.X, ActualHeight / 2 + _frCy * s + _pan.Y);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var r = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawRectangle(Bg, null, r);
            for (double x = 0; x < ActualWidth; x += 24) dc.DrawRectangle(GridBrush, null, new Rect(x, 0, 1, ActualHeight));
            for (double y = 0; y < ActualHeight; y += 24) dc.DrawRectangle(GridBrush, null, new Rect(0, y, ActualWidth, 1));

            if (Drawing != null && ActualWidth > 10 && ActualHeight > 10)
            {
                var (s, ox, oy) = Fit();
                Point P(P2 p) => new(ox + p.X * s, oy - p.Y * s);

                if (ShowWipeouts)
                    foreach (var w in Drawing.Wipeouts.Values)
                    {
                        var g = new StreamGeometry();
                        using (var c = g.Open())
                        {
                            c.BeginFigure(P(w[0]), true, true);
                            c.PolyLineTo(w.Skip(1).Select(P).ToList(), true, true);
                        }
                        g.Freeze();
                        dc.DrawGeometry(WipeFill, WipePen, g);
                    }

                foreach (var pl in Drawing.Lines.OrderBy(l => l.Style == LineStyle.Hidden ? 0 : l.Style == LineStyle.Thin ? 1 : 2))
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(P(pl.Points[0]), false, pl.Closed);
                        c.PolyLineTo(pl.Points.Skip(1).Select(P).ToList(), true, true);
                    }
                    g.Freeze();
                    dc.DrawGeometry(null, pl.Style == LineStyle.Visible ? LinePen : pl.Style == LineStyle.Thin ? ThinPen : HiddenPen, g);
                }

                foreach (int hg in Highlight)
                {
                    if (!Drawing.Wipeouts.TryGetValue(hg, out var hw) || hw.Count < 3) continue;
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(P(hw[0]), true, true);
                        c.PolyLineTo(hw.Skip(1).Select(P).ToList(), true, true);
                    }
                    g.Freeze();
                    dc.DrawGeometry(SelFill, SelPen, g);
                }

                _dimRect = Rect.Empty;
                if (Dim is { } dm)
                {
                    var A = new Point(ox + dm.a.X * s, oy - dm.a.Y * s);
                    var B = new Point(ox + dm.b.X * s, oy - dm.b.Y * s);
                    var along = B - A;
                    var n = along.Length > 1e-6 ? new Vector(-along.Y, along.X) / along.Length : new Vector(0, -1);
                    if (n.Y > 0) n = -n;                                   // label on the upper side
                    dc.DrawLine(DimPen, A, B);
                    dc.DrawLine(DimPen, A - n * 8, A + n * 8);
                    dc.DrawLine(DimPen, B - n * 8, B + n * 8);
                    var ft = new FormattedText(dm.text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                        13, Red, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    var mid = A + along / 2 + n * 26;
                    _dimRect = new Rect(mid.X - ft.Width / 2 - 8, mid.Y - ft.Height / 2 - 4, ft.Width + 16, ft.Height + 8);
                    dc.DrawRoundedRectangle(Brushes.White, DimPen, _dimRect, 6, 6);
                    dc.DrawText(ft, new Point(_dimRect.X + 8, _dimRect.Y + 4));
                }

                foreach (var (gp, dir) in Grips)
                {
                    var c = new Point(ox + gp.X * s, oy - gp.Y * s);
                    var a = dir; a.Y = -a.Y; if (a.Length < 1e-9) continue; a.Normalize();
                    var n = new Vector(-a.Y, a.X);
                    var g = new StreamGeometry();
                    using (var cx = g.Open())
                    {
                        cx.BeginFigure(c + a * 9, true, true);
                        cx.PolyLineTo(new[] { c - a * 5 + n * 7, c - a * 5 - n * 7 }, true, true);
                    }
                    g.Freeze();
                    dc.DrawGeometry(GripBrush, null, g);
                }

                foreach (var (mp, label) in Markers)
                {
                    var c = new Point(ox + mp.X * s, oy - mp.Y * s);
                    var pen = new Pen(Red, 1.5);
                    dc.DrawLine(pen, c + new Vector(-7, 0), c + new Vector(7, 0));
                    dc.DrawLine(pen, c + new Vector(0, -7), c + new Vector(0, 7));
                    dc.DrawEllipse(null, pen, c, 3.5, 3.5);
                    Text(dc, label, c + new Vector(8, -18), Red, 11);
                }
            }

            Text(dc, Caption, new Point(14, 10), Muted, 12);
            if (ShowPartHint)
                Text(dc, Loc.T("Ctrl+click = select a part · Ctrl+drag = move it along the bolt · click the distance to type a value · Esc = unselect"), new Point(14, 30), Muted, 11);
            Text(dc, Loc.T("Drag = rotate · Arrow keys = 1° (Shift 15°, Ctrl 0.1°) · Wheel = zoom · Right-drag = pan · Double-click = fit · Ctrl+Z = undo"), new Point(14, ActualHeight - 24), Muted, 11);

            // joystick
            var jc = JoyCenter;
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x18, 0x2F, 0x6F, 0xEB)), new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x2F, 0x6F, 0xEB)), 1), jc, JoyR, JoyR);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x2F, 0x6F, 0xEB)), 1), jc + new Vector(-JoyR, 0), jc + new Vector(JoyR, 0));
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x2F, 0x6F, 0xEB)), 1), jc + new Vector(0, -JoyR), jc + new Vector(0, JoyR));
            dc.DrawEllipse(Accent, new Pen(Brushes.White, 2), jc + (_joy ? _joyVec : new Vector()), 11, 11);
            Text(dc, Loc.T("orbit"), jc + new Vector(-13, -JoyR - 16), Muted, 10);
        }

        void Text(DrawingContext dc, string s, Point p, Brush b, double size)
        {
            if (string.IsNullOrEmpty(s)) return;
            var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, p);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            Focus();
            var p = e.GetPosition(this);
            if (e.ChangedButton == MouseButton.Left && !_dimRect.IsEmpty && _dimRect.Contains(p))
            {
                DistanceClicked?.Invoke(_dimRect.TopLeft);
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2) { _zoom = 1; _pan = new Vector(); InvalidateVisual(); return; }
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (e.ChangedButton == MouseButton.Left && ctrl && Drawing != null && (p - JoyCenter).Length > JoyR + 4)
            {
                _partDrag = true; _partMoved = false; _partGroup = HitGroup(p); _downPos = p; _last = p;
                CaptureMouse();
                PartDown?.Invoke(_partGroup);
                return;
            }
            if (e.ChangedButton == MouseButton.Left && (p - JoyCenter).Length <= JoyR + 4)
            {
                _joy = true; _joyVec = Clamp(p - JoyCenter); _joyTimer.Start();
            }
            else if (e.ChangedButton == MouseButton.Left) _rotating = true;
            else _panning = true;
            _last = p;
            CaptureMouse();
            InvalidateVisual();
        }

        static Vector Clamp(Vector v) => v.Length > JoyR ? v / v.Length * JoyR : v;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (_partDrag)
            {
                if (!_partMoved && (p - _downPos).Length < 3) return;
                _partMoved = true;
                if (_partGroup >= 0)
                {
                    double mm = AxisMm(p - _last);
                    if (mm != 0) PartDrag?.Invoke(mm, false);
                }
                _last = p;
                return;
            }
            var d = p - _last;
            if (_joy) { _joyVec = Clamp(p - JoyCenter); InvalidateVisual(); }
            else if (_rotating && d.Length > 0) RotateRequested?.Invoke(d.X * 0.5, d.Y * 0.5, false);
            else if (_panning) { _pan += d; InvalidateVisual(); }
            _last = p;
        }

        /// <summary>Screen movement -> millimetres along the bolt axis (0 when the axis points at the viewer).</summary>
        double AxisMm(Vector d)
        {
            if (Drawing == null) return 0;
            var (s, _, _) = Fit();
            var o = Drawing.Project(new Vec3(0, 0, 0)); var x = Drawing.Project(new Vec3(1, 0, 0));
            double ax = x.X - o.X, ay = x.Y - o.Y, l2 = ax * ax + ay * ay;
            if (l2 < 0.02) return 0;
            return (d.X / s * ax + -d.Y / s * ay) / l2;
        }

        /// <summary>Selectable part under the cursor (smallest outline wins when several overlap), -1 = none.</summary>
        int HitGroup(Point p)
        {
            if (Drawing == null) return -1;
            var (s, ox, oy) = Fit();
            double mx = (p.X - ox) / s, my = (oy - p.Y) / s;
            int best = -1; double bestArea = double.MaxValue;
            foreach (var kv in Drawing.Wipeouts)
            {
                if (!SelectableGroups.Contains(kv.Key)) continue;
                var h = kv.Value; bool inside = true; double area = 0;
                for (int i = 0; i < h.Count; i++)
                {
                    var a = h[i]; var b = h[(i + 1) % h.Count];
                    if ((b.X - a.X) * (my - a.Y) - (b.Y - a.Y) * (mx - a.X) < 0) inside = false;
                    area += a.X * b.Y - b.X * a.Y;
                }
                area = Math.Abs(area);
                if (inside && area < bestArea) { best = kv.Key; bestArea = area; }
            }
            return best;
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            if (_partDrag)
            {
                _partDrag = false;
                ReleaseMouseCapture();
                if (_partMoved && _partGroup >= 0) PartDrag?.Invoke(0, true);
                PartUp?.Invoke(_partGroup, _partMoved);
                return;
            }
            bool wasRot = _rotating || _joy;
            _rotating = _panning = _joy = false;
            _joyTimer.Stop();
            ReleaseMouseCapture();
            if (wasRot) RotateRequested?.Invoke(0, 0, true);
            InvalidateVisual();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            _zoom *= e.Delta > 0 ? 1.15 : 1 / 1.15;
            _zoom = Math.Clamp(_zoom, 0.1, 20);
            InvalidateVisual();
        }
    }
}
