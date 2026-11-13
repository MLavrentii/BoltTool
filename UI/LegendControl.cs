using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BoltToolApp.Data;

namespace BoltToolApp.UI
{
    /// <summary>Small schematic of the selected part that shows what each dimension letter means.</summary>
    public class LegendControl : FrameworkElement
    {
        PartKind _kind = PartKind.HexBolt;

        static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)));
        static readonly Brush Blue = Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xEB)));
        static readonly Brush Fill = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF8)));
        static readonly Pen Outline = FrozenPen(new Pen(Ink, 1.3) { LineJoin = PenLineJoin.Round });
        static readonly Pen Thin = FrozenPen(new Pen(Ink, 0.6));
        static readonly Pen Dash = FrozenPen(new Pen(Ink, 0.7) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) });
        static readonly Pen DimPen = FrozenPen(new Pen(Blue, 0.9));

        static Brush Frozen(Brush b) { b.Freeze(); return b; }
        static Pen FrozenPen(Pen p) { p.Freeze(); return p; }

        public void SetKind(PartKind kind)
        {
            if (_kind == kind) return;
            _kind = kind;
            InvalidateVisual();
        }

        // model -> screen mapping for the current render
        double _s, _ox, _oy;
        DrawingContext _dc;
        Point T(double x, double y) => new(_ox + x * _s, _oy + y * _s);

        void Begin(DrawingContext dc, Rect model)
        {
            _dc = dc;
            double pad = 6;
            _s = Math.Min((ActualWidth - 2 * pad) / model.Width, (ActualHeight - 2 * pad) / model.Height);
            _ox = (ActualWidth - model.Width * _s) / 2 - model.X * _s;
            _oy = (ActualHeight - model.Height * _s) / 2 - model.Y * _s;
        }

        void Line(Pen p, double x1, double y1, double x2, double y2) => _dc.DrawLine(p, T(x1, y1), T(x2, y2));

        void Box(double x1, double y1, double x2, double y2) =>
            _dc.DrawRectangle(Fill, Outline, new Rect(T(x1, y1), T(x2, y2)));

        void Label(string s, Point c)
        {
            var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                12, Blue, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            _dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
        }

        void Arrow(Point tip, Vector dir)
        {
            dir.Normalize();
            var n = new Vector(-dir.Y, dir.X);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(tip, true, true);
                c.LineTo(tip - dir * 6 + n * 2.2, true, false);
                c.LineTo(tip - dir * 6 - n * 2.2, true, false);
            }
            g.Freeze();
            _dc.DrawGeometry(Blue, null, g);
        }

        /// <summary>Horizontal dimension between x1 and x2 on the line y; extension lines start at yFrom.</summary>
        void HDim(double x1, double x2, double y, double yFrom, string label)
        {
            Point a = T(x1, y), b = T(x2, y);
            double ext = y > yFrom ? 3 : -3;
            _dc.DrawLine(DimPen, T(x1, yFrom), a + new Vector(0, ext));
            _dc.DrawLine(DimPen, T(x2, yFrom), b + new Vector(0, ext));
            _dc.DrawLine(DimPen, a, b);
            if (Math.Abs(b.X - a.X) > 16) { Arrow(a, new Vector(-1, 0)); Arrow(b, new Vector(1, 0)); }
            Label(label, new Point((a.X + b.X) / 2, a.Y + (y > yFrom ? 9 : -9)));
        }

        /// <summary>Vertical dimension between y1 and y2 on the line x; extension lines start at xFrom.</summary>
        void VDim(double x, double y1, double y2, double xFrom, string label)
        {
            Point a = T(x, y1), b = T(x, y2);
            double ext = x > xFrom ? 3 : -3;
            _dc.DrawLine(DimPen, T(xFrom, y1), a + new Vector(ext, 0));
            _dc.DrawLine(DimPen, T(xFrom, y2), b + new Vector(ext, 0));
            _dc.DrawLine(DimPen, a, b);
            if (Math.Abs(b.Y - a.Y) > 16) { Arrow(a, new Vector(0, -1)); Arrow(b, new Vector(0, 1)); }
            Label(label, new Point(a.X + (x > xFrom ? 11 : -11), (a.Y + b.Y) / 2));
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth < 40 || ActualHeight < 40) return;
            switch (_kind)
            {
                case PartKind.HexBolt:
                case PartKind.HexBoltFullThread: DrawBolt(dc, false, _kind == PartKind.HexBoltFullThread); break;
                case PartKind.SocketCapScrew: DrawBolt(dc, true, false); break;
                case PartKind.ThreadedRod: DrawRod(dc); break;
                case PartKind.HexNut:
                case PartKind.ThinNut:
                case PartKind.FlangeNut:
                case PartKind.ChannelNut:
                case PartKind.ChannelNutHolder: DrawNut(dc); break;
                case PartKind.SpringWasher: DrawWasher(dc, "s"); break;
                default: DrawWasher(dc, "h"); break;
            }
        }

        void ThreadLines(double x0, double x1, double r, double rMin)
        {
            Line(Thin, x0, -rMin, x1, -rMin);
            Line(Thin, x0, rMin, x1, rMin);
        }

        void DrawBolt(DrawingContext dc, bool socket, bool fullThread)
        {
            // head 0..hk, shank hk..xe ; y is down on screen
            double hk = socket ? 30 : 24, hh = socket ? 24 : 30, r = 13, rm = 10.5, xe = 170, xt = fullThread ? hk : 100;
            Begin(dc, new Rect(-34, -50, 236, 112));

            Box(hk, -r, xe, r);                                  // shank
            Box(0, -hh, hk, hh);                                 // head
            if (socket)
            {
                Line(Dash, 0, -9, 16, -9); Line(Dash, 0, 9, 16, 9); Line(Dash, 16, -9, 16, 9);
                VDim(8, -9, 9, 8, "s");
            }
            else
            {
                Line(Thin, 0, -hh / 2, hk, -hh / 2); Line(Thin, 0, hh / 2, hk, hh / 2);   // hex corner lines
            }
            ThreadLines(xt, xe, r, rm);
            if (!fullThread) Line(Thin, xt, -r, xt, r);

            VDim(-16, -hh, hh, 0, socket ? "dk" : "s");
            VDim(xe + 14, -r, r, xe, "d");
            HDim(0, hk, 50, hh, "k");
            HDim(hk, xe, 50, r, "L");
            if (!fullThread) HDim(xt, xe, 30, r, "b");
            // pitch
            double px = xe - 44;
            Line(Thin, px, -r, px, -r - 6); Line(Thin, px + 9, -r, px + 9, -r - 6);
            HDim(px, px + 9, -r - 14, -r - 6, "P");
        }

        void DrawRod(DrawingContext dc)
        {
            double r = 13, rm = 10.5, xe = 180;
            Begin(dc, new Rect(-10, -44, 220, 96));
            Box(0, -r, xe, r);
            ThreadLines(0, xe, r, rm);
            VDim(xe + 14, -r, r, xe, "d");
            HDim(0, xe, 40, r, "L");
            double px = 80;
            Line(Thin, px, -r, px, -r - 6); Line(Thin, px + 9, -r, px + 9, -r - 6);
            HDim(px, px + 9, -r - 14, -r - 6, "P");
        }

        void DrawNut(DrawingContext dc)
        {
            double m = 34, hh = 34, r = 14;
            Begin(dc, new Rect(-40, -48, 120, 108));
            Box(0, -hh, m, hh);
            Line(Thin, 0, -hh / 2, m, -hh / 2); Line(Thin, 0, hh / 2, m, hh / 2);
            Line(Dash, 0, -r, m, -r); Line(Dash, 0, r, m, r);
            VDim(-16, -hh, hh, 0, "s");
            VDim(m + 16, -r, r, m, "d");
            HDim(0, m, 48, hh, "m");
        }

        void DrawWasher(DrawingContext dc, string thicknessLabel)
        {
            double h = 10, ro = 36, ri = 16;
            Begin(dc, new Rect(-44, -50, 104, 112));
            Box(0, -ro, h, ro);
            Line(Dash, 0, -ri, h, -ri); Line(Dash, 0, ri, h, ri);
            VDim(-16, -ro, ro, 0, "d2");
            VDim(h + 16, -ri, ri, h, "d1");
            HDim(0, h, 50, ro, thicknessLabel);
        }
    }
}
