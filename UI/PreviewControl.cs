using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BoltToolApp.Geometry;

namespace BoltToolApp.UI
{
    /// <summary>
    /// Live hidden-line preview.
    ///   Left-drag            orbit (the round joystick orbits continuously while held)
    ///   Wheel / right-drag   zoom / pan, double-click = fit
    ///   Ctrl+click / drag    select washers and nuts, move them along the bolt
    ///   Drag a blue ▶ grip   change bolt length / clamp length
    ///   Shift+click          measure between two snapped points (green dimensions)
    /// The view frame stays fixed until the user explicitly selects Fit view or double-clicks.
    /// </summary>
    public class PreviewControl : FrameworkElement
    {
        public Drawing2D Drawing { get; private set; }
        public List<(Point p, string label)> Markers = new();              // model-space 2D markers (base point)
        public List<(Point p, Vector dir, string kind)> Grips = new();     // stretch grips (model 2D position + axis direction)
        public bool ShowWipeouts = true;
        public string Caption = "";

        /// <summary>Raised with (dAzimuth, dElevation) in degrees; `final` = mouse released.</summary>
        public event Action<double, double, bool> RotateRequested;

        double _zoom = 1; Vector _pan;

        // ---- parts: selection / Ctrl+drag along the bolt ----
        public readonly HashSet<int> SelectableGroups = new();
        public readonly HashSet<int> Highlight = new();
        public readonly List<List<P2>> Ghosts = new();                     // outlines of where a dragged part would land
        public readonly List<(Point a, Point b, string text, int tag)> Dims = new();   // red distance labels (model 2D)
        public bool ShowPartHint;
        public event Action<int> PartDown;                 // group under the cursor (-1 = none)
        public event Action<double, bool> PartDrag;        // movement along the axis in mm, final
        public event Action<int, bool> PartUp;             // group, moved
        public event Action<int, Point> DistanceClicked;   // tag of the label, its top-left corner
        public event Action<string, double, bool> GripDrag; // grip kind, movement along the axis in mm, final

        // ---- measuring (Shift) ----
        readonly List<(P2 a, P2 b)> _measures = new();
        P2? _measureFirst, _snap;
        public bool HasMeasures => _measures.Count > 0 || _measureFirst != null;

        // ---- sticky view frame: a sphere in model space ----
        Vec3 _frC; double _frR = 1; bool _hasFrame;

        bool _partDrag, _partMoved; int _partGroup = -1; Point _downPos;
        int _gripDrag = -1; bool _gripMoved;
        readonly List<(Rect r, int tag)> _dimRects = new();
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
        static readonly Brush GripHot = Frozen(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
        static readonly Brush SelFill = Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0x2F, 0x6F, 0xEB)));
        static readonly Pen SelPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xEB)), 2));
        static readonly Brush GhostFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x16, 0xA3, 0x4A)));
        static readonly Pen GhostPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)), 1.6) { DashStyle = new DashStyle(new double[] { 5, 3 }, 0) });
        static readonly Pen DimPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x48)), 1.4));
        static readonly Brush Green = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)));
        static readonly Pen GreenPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)), 1.5));

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

        /// <summary>
        /// Shows a new drawing without changing the current frame, zoom or pan. Refit is
        /// intentional (Fit button / double-click / Reset) because even a small length or
        /// washer edit must not unexpectedly move the preview. keepFrame is retained for
        /// compatibility with the existing caller during drags.
        /// </summary>
        public void SetDrawing(Drawing2D d, bool keepFrame = false)
        {
            Drawing = d;
            if (!_hasFrame) Refit(false);
            InvalidateVisual();
        }

        /// <summary>Fits the view to the model; resetView also clears the user's zoom and pan.</summary>
        public void Refit(bool resetView)
        {
            if (Drawing != null) { _frC = Drawing.Center3D; _frR = Math.Max(1e-6, Drawing.Radius); _hasFrame = true; }
            if (resetView) { _zoom = 1; _pan = new Vector(); }
            InvalidateVisual();
        }

        public void ClearMeasures()
        {
            if (!HasMeasures && _snap == null) return;
            _measures.Clear(); _measureFirst = null; _snap = null;
            InvalidateVisual();
        }

        /// <summary>The preview as a bitmap (for the JPG snapshot).</summary>
        public BitmapSource Capture(double scale = 2)
        {
            int w = Math.Max(1, (int)Math.Ceiling(ActualWidth * scale)), h = Math.Max(1, (int)Math.Ceiling(ActualHeight * scale));
            var bmp = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            // paint through a brush so the position of the control inside its parent does not matter
            var dv = new DrawingVisual();
            using (var ctx = dv.RenderOpen())
                ctx.DrawRectangle(new VisualBrush(this), null, new Rect(0, 0, ActualWidth, ActualHeight));
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        Point JoyCenter => new(ActualWidth - JoyR - 16, ActualHeight - JoyR - 16);

        (double s, double ox, double oy) Fit()
        {
            // scale by the frame sphere, centre on its projection: same distance from every direction
            double margin = 40;
            double s = Math.Max(10, Math.Min(ActualWidth - 2 * margin, ActualHeight - 2 * margin - 20)) / (2 * Math.Max(1e-6, _frR)) * _zoom;
            var c = Drawing != null ? Drawing.View.Apply(_frC) : new Vec3(0, 0, 0);
            return (s, ActualWidth / 2 - c.X * s + _pan.X, ActualHeight / 2 + c.Y * s + _pan.Y);
        }

        StreamGeometry Poly(IList<Point> pts, bool closed)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(pts[0], closed, closed);
                c.PolyLineTo(pts.Skip(1).ToList(), true, true);
            }
            g.Freeze();
            return g;
        }

        void Arrow(DrawingContext dc, Brush b, Point tip, Vector dir)
        {
            if (dir.Length < 1e-9) return;
            dir.Normalize();
            var n = new Vector(-dir.Y, dir.X);
            dc.DrawGeometry(b, null, Poly(new[] { tip, tip - dir * 9 + n * 3.2, tip - dir * 9 - n * 3.2 }, true));
        }

        /// <summary>Label in a rounded box centred at c; returns the box.</summary>
        Rect Pill(DrawingContext dc, string text, Point c, Brush fg, Pen border)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                13, fg, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var r = new Rect(c.X - ft.Width / 2 - 8, c.Y - ft.Height / 2 - 4, ft.Width + 16, ft.Height + 8);
            dc.DrawRoundedRectangle(Brushes.White, border, r, 6, 6);
            dc.DrawText(ft, new Point(r.X + 8, r.Y + 4));
            return r;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawRectangle(Bg, null, bounds);
            for (double x = 0; x < ActualWidth; x += 24) dc.DrawRectangle(GridBrush, null, new Rect(x, 0, 1, ActualHeight));
            for (double y = 0; y < ActualHeight; y += 24) dc.DrawRectangle(GridBrush, null, new Rect(0, y, ActualWidth, 1));
            _dimRects.Clear();

            if (Drawing != null && ActualWidth > 10 && ActualHeight > 10)
            {
                var (s, ox, oy) = Fit();
                Point P(P2 p) => new(ox + p.X * s, oy - p.Y * s);
                Point Q(Point p) => new(ox + p.X * s, oy - p.Y * s);

                if (ShowWipeouts)
                    foreach (var w in Drawing.Wipeouts.Values)
                        if (w.Count >= 3) dc.DrawGeometry(WipeFill, WipePen, Poly(w.Select(P).ToList(), true));

                foreach (var pl in Drawing.Lines.OrderBy(l => l.Style == LineStyle.Hidden ? 0 : l.Style == LineStyle.Thin ? 1 : 2))
                    dc.DrawGeometry(null, pl.Style == LineStyle.Visible ? LinePen : pl.Style == LineStyle.Thin ? ThinPen : HiddenPen,
                                    Poly(pl.Points.Select(P).ToList(), pl.Closed));

                foreach (int hg in Highlight)
                    if (Drawing.Wipeouts.TryGetValue(hg, out var hw) && hw.Count >= 3)
                        dc.DrawGeometry(SelFill, SelPen, Poly(hw.Select(P).ToList(), true));

                foreach (var gh in Ghosts)
                    if (gh.Count >= 3) dc.DrawGeometry(GhostFill, GhostPen, Poly(gh.Select(P).ToList(), true));

                // red distance labels of the selected parts
                foreach (var dm in Dims)
                {
                    Point a = Q(dm.a), b = Q(dm.b);
                    var along = b - a;
                    var n = along.Length > 1e-6 ? new Vector(-along.Y, along.X) / along.Length : new Vector(0, -1);
                    if (n.Y > 0) n = -n;                                   // label on the upper side
                    dc.DrawLine(DimPen, a, b);
                    dc.DrawLine(DimPen, a - n * 8, a + n * 8);
                    dc.DrawLine(DimPen, b - n * 8, b + n * 8);
                    _dimRects.Add((Pill(dc, dm.text, a + along / 2 + n * 26, Red, DimPen), dm.tag));
                }

                // green measurements (Shift+click)
                foreach (var (ma, mb) in _measures)
                {
                    Point a = P(ma), b = P(mb);
                    var along = b - a;
                    dc.DrawLine(GreenPen, a, b);
                    Arrow(dc, Green, a, -along); Arrow(dc, Green, b, along);
                    var n = along.Length > 1e-6 ? new Vector(-along.Y, along.X) / along.Length : new Vector(0, -1);
                    if (n.Y > 0) n = -n;
                    Pill(dc, ma.Dist(mb).ToString("0.##", CultureInfo.InvariantCulture) + " mm (2D)", a + along / 2 + n * 16, Green, GreenPen);
                }
                if (_measureFirst is P2 mf)
                {
                    var a = P(mf);
                    dc.DrawEllipse(Green, null, a, 4, 4);
                    if (_snap is P2 sp2) dc.DrawLine(GreenPen, a, P(sp2));
                }
                if (_snap is P2 sp)
                {
                    var c = P(sp);
                    dc.DrawRectangle(null, GreenPen, new Rect(c.X - 6, c.Y - 6, 12, 12));
                }

                for (int i = 0; i < Grips.Count; i++)
                {
                    var (gp, dir, _) = Grips[i];
                    var c = Q(gp);
                    var a = new Vector(dir.X, -dir.Y);
                    if (a.Length < 1e-9) continue;
                    a.Normalize();
                    var n = new Vector(-a.Y, a.X);
                    dc.DrawGeometry(i == _gripDrag ? GripHot : GripBrush, null, Poly(new[] { c + a * 10, c - a * 6 + n * 8, c - a * 6 - n * 8 }, true));
                    Text(dc, Loc.T(Grips[i].kind == "clamp" ? "Clamp" : "L"), c + new Vector(12, -8), GripBrush, 10);
                }

                foreach (var (mp, label) in Markers)
                {
                    var c = Q(mp);
                    var pen = new Pen(Red, 1.5);
                    dc.DrawLine(pen, c + new Vector(-7, 0), c + new Vector(7, 0));
                    dc.DrawLine(pen, c + new Vector(0, -7), c + new Vector(0, 7));
                    dc.DrawEllipse(null, pen, c, 3.5, 3.5);
                    Text(dc, label, c + new Vector(8, -18), Red, 11);
                }
            }

            Text(dc, Caption, new Point(14, 10), Muted, 12);
            Text(dc, Loc.T("Drag = rotate · Arrow keys = 1° (Shift 15°, Ctrl 0.1°) · Wheel = zoom · Right-drag = pan · Double-click = fit · Ctrl+Z = undo"), new Point(14, 30), Muted, 11);
            Text(dc, Loc.T("Shift+click two points = measure · drag a blue ▶ = change length / clamp · Esc = clear"), new Point(14, 46), Muted, 11);
            if (ShowPartHint)
                Text(dc, Loc.T("Ctrl+click = select a part · Ctrl+drag = move it along the bolt (pull past a neighbour to swap places) · click a red distance to type a value"), new Point(14, 62), Muted, 11);

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

        // ------------------------------------------------------------------ hit testing

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

        int HitGrip(Point p)
        {
            if (Drawing == null) return -1;
            var (s, ox, oy) = Fit();
            for (int i = 0; i < Grips.Count; i++)
            {
                var c = new Point(ox + Grips[i].p.X * s, oy - Grips[i].p.Y * s);
                if ((p - c).Length <= 13) return i;
            }
            return -1;
        }

        /// <summary>Nearest drawn corner / curve point within 12 px (for measuring).</summary>
        P2? SnapPoint(Point p)
        {
            if (Drawing == null) return null;
            var (s, ox, oy) = Fit();
            double best = 12 * 12; P2? hit = null;
            foreach (var pl in Drawing.Lines)
                foreach (var v in pl.Points)
                {
                    double dx = ox + v.X * s - p.X, dy = oy - v.Y * s - p.Y, d2 = dx * dx + dy * dy;
                    if (d2 < best) { best = d2; hit = v; }
                }
            return hit;
        }

        // ------------------------------------------------------------------ mouse

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            Focus();
            var p = e.GetPosition(this);
            bool left = e.ChangedButton == MouseButton.Left;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool onJoy = (p - JoyCenter).Length <= JoyR + 4;

            if (left)
                foreach (var (r, tag) in _dimRects)
                    if (r.Contains(p)) { DistanceClicked?.Invoke(tag, r.TopLeft); e.Handled = true; return; }

            if (left && shift && !ctrl && !onJoy)
            {
                // measuring: first click = start point, second click = dimension
                var sp = SnapPoint(p);
                if (sp is P2 hit)
                {
                    if (_measureFirst is P2 first)
                    {
                        if (first.Dist(hit) > 1e-9) _measures.Add((first, hit));
                        _measureFirst = null;
                    }
                    else _measureFirst = hit;
                    InvalidateVisual();
                }
                e.Handled = true;
                return;
            }

            if (left && e.ClickCount == 2) { Refit(true); return; }

            if (left && ctrl && Drawing != null && !onJoy)
            {
                _partDrag = true; _partMoved = false; _partGroup = HitGroup(p); _downPos = p; _last = p;
                CaptureMouse();
                PartDown?.Invoke(_partGroup);
                return;
            }

            if (left && !onJoy)
            {
                int g = HitGrip(p);
                if (g >= 0)
                {
                    _gripDrag = g; _gripMoved = false; _last = p;
                    CaptureMouse();
                    InvalidateVisual();
                    return;
                }
            }

            if (left && onJoy)
            {
                _joy = true; _joyVec = Clamp(p - JoyCenter); _joyTimer.Start();
            }
            else if (left) _rotating = true;
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
            if (_gripDrag >= 0)
            {
                double mm = AxisMm(p - _last);
                _last = p;
                if (mm != 0 && _gripDrag < Grips.Count) { _gripMoved = true; GripDrag?.Invoke(Grips[_gripDrag].kind, mm, false); }
                return;
            }

            var d = p - _last;
            if (_joy) { _joyVec = Clamp(p - JoyCenter); InvalidateVisual(); }
            else if (_rotating && d.Length > 0) RotateRequested?.Invoke(d.X * 0.5, d.Y * 0.5, false);
            else if (_panning) { _pan += d; InvalidateVisual(); }
            else
            {
                // hover: snap marker while Shift is held, hand cursor over a grip
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                var snap = shift ? SnapPoint(p) : null;
                if (!Equals(snap, _snap)) { _snap = snap; InvalidateVisual(); }
                int grip = HitGrip(p);
                Cursor = shift ? Cursors.Cross : grip >= 0 ? Cursors.Hand : Cursors.SizeAll;
                ToolTip = grip >= 0
                    ? Loc.T(Grips[grip].kind == "clamp"
                        ? "Drag to change clamped thickness; nut-side parts and bolt length follow."
                        : "Drag to change the bolt length / protrusion.")
                    : null;
            }
            _last = p;
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
            if (_gripDrag >= 0)
            {
                string kind = _gripDrag < Grips.Count ? Grips[_gripDrag].kind : null;
                _gripDrag = -1;
                ReleaseMouseCapture();
                if (_gripMoved && kind != null) GripDrag?.Invoke(kind, 0, true);
                InvalidateVisual();
                return;
            }
            bool wasRot = _rotating || _joy;
            bool any = wasRot || _panning;
            _rotating = _panning = _joy = false;
            _joyTimer.Stop();
            if (any) ReleaseMouseCapture();
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
