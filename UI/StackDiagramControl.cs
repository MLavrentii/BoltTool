using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using BoltToolApp.Data;
using BoltToolApp.Geometry;

namespace BoltToolApp.UI
{
    /// <summary>
    /// Small side-view diagram of the connection: the order of the elements on the bolt, their names,
    /// and the distances that are not zero (gaps, clamp length, protrusion beyond the last element, bolt length).
    /// </summary>
    public class StackDiagramControl : FrameworkElement
    {
        Layout _lay; SizeRow _row; BoltJob _job; HashSet<string> _sel = new();

        static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)));
        static readonly Brush Muted = Frozen(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
        static readonly Brush Blue = Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xEB)));
        static readonly Brush BoltFill = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)));
        static readonly Brush PartFill = Frozen(new SolidColorBrush(Color.FromRgb(0xDB, 0xE7, 0xFB)));
        static readonly Brush SelFill = Frozen(new SolidColorBrush(Color.FromRgb(0x9C, 0xBC, 0xF5)));
        static readonly Brush ClampFill = Frozen(new SolidColorBrush(Color.FromRgb(0xF3, 0xEC, 0xD9)));
        static readonly Pen Outline = FrozenPen(new Pen(Ink, 1));
        static readonly Pen DimPen = FrozenPen(new Pen(Blue, 0.9));
        static readonly Pen ClampPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0xC9, 0xB3, 0x7A)), 0.8) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) });

        static Brush Frozen(Brush b) { b.Freeze(); return b; }
        static Pen FrozenPen(Pen p) { p.Freeze(); return p; }
        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        public void Set(Layout lay, SizeRow row, BoltJob job, IEnumerable<string> selected = null)
        {
            _lay = lay; _row = row; _job = job;
            _sel = selected != null ? new HashSet<string>(selected) : new HashSet<string>();
            InvalidateVisual();
        }

        FormattedText Ft(string s, Brush b, double size = 11, bool bold = false) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override void OnRender(DrawingContext dc)
        {
            if (_lay == null || _lay.Parts.Count == 0 || ActualWidth < 60 || ActualHeight < 60) return;
            var bolt = _lay.Parts[0];
            bool assembly = _lay.Parts.Count > 1 || bolt.Role == "bolt";
            double x0 = 0, x1 = Math.Max(_lay.BoltEnd, _lay.Parts.Max(p => p.X + p.Thickness));
            double rMax = _lay.Parts.Max(p => p.OuterR);
            if (x1 - x0 < 1e-6 || rMax < 1e-6) return;

            const double padX = 10, top = 40, bottom = 46;      // room for the dimension rows
            double availH = Math.Max(20, ActualHeight - top - bottom);
            double s = Math.Min((ActualWidth - 2 * padX) / (x1 - x0), availH / (2 * rMax));
            double ox = (ActualWidth - (x1 - x0) * s) / 2, cy = top + availH / 2;
            double X(double x) => ox + (x - x0) * s;
            Rect Box(double a, double b, double r) => new(X(a), cy - r * s, Math.Max(1, (b - a) * s), 2 * r * s);

            // clamped parts (the material between the head side and the nut side)
            if (assembly && _lay.ClampEnd - _lay.ClampStart > 1e-6)
                dc.DrawRectangle(ClampFill, ClampPen, Box(_lay.ClampStart, _lay.ClampEnd, rMax * 1.05));

            // bolt: shank, then head
            double hk = bolt.Thickness - bolt.Length;
            if (PartKindInfo.IsBolt(bolt.Kind))
            {
                dc.DrawRectangle(BoltFill, Outline, Box(bolt.X + hk, bolt.X + bolt.Thickness, _row.D / 2));
                if (hk > 1e-6) dc.DrawRectangle(BoltFill, Outline, Box(bolt.X, bolt.X + hk, bolt.OuterR));
            }

            // elements, with their short names in two staggered rows below
            int n = 0;
            foreach (var p in _lay.Parts)
            {
                if (PartKindInfo.IsBolt(p.Kind)) continue;
                dc.DrawRectangle(_sel.Contains(p.Role) ? SelFill : PartFill, Outline, Box(p.X, p.X + p.Thickness, p.OuterR));
                var ft = Ft(PartKindInfo.Code(p.Kind), Ink, 10);
                double tx = X(p.X + p.Thickness / 2) - ft.Width / 2;
                dc.DrawText(ft, new Point(Math.Clamp(tx, 0, ActualWidth - ft.Width), cy + rMax * s + 3 + (n % 2) * 11));
                n++;
            }

            if (!assembly) return;

            // Explicitly separate solid plate thickness from the free gaps on either side.
            var dims = new List<(double a, double b, string text)>();
            if (_lay.ClampEnd - _lay.ClampStart > 1e-6)
                dims.Add((_lay.ClampStart, _lay.ClampEnd, Loc.T("clamp") + " " + F(_lay.ClampEnd - _lay.ClampStart)));
            double prev = _lay.UnderHead;
            foreach (var p in _lay.Parts.Where(q => q.Item != null).OrderBy(q => q.X))
            {
                if (p.Item.Side == StackSide.Nut && prev < _lay.ClampEnd) prev = _lay.ClampEnd;
                if (p.X - prev > 1e-6) dims.Add((prev, p.X, F(p.X - prev)));
                prev = p.X + p.Thickness;
            }
            if (_lay.Protrusion > 1e-6) dims.Add((_lay.StackEnd, _lay.BoltEnd, Loc.T("out") + " " + F(_lay.Protrusion)));

            double yDim = cy - rMax * s * 1.05 - 8;
            int k = 0;
            foreach (var (a, b, text) in dims)
            {
                double xa = X(a), xb = X(b), y = yDim;
                dc.DrawLine(DimPen, new Point(xa, y), new Point(xb, y));
                dc.DrawLine(DimPen, new Point(xa, y - 4), new Point(xa, y + 6));
                dc.DrawLine(DimPen, new Point(xb, y - 4), new Point(xb, y + 6));
                var ft = Ft(text, Blue, 10.5, true);
                double tx = Math.Clamp((xa + xb) / 2 - ft.Width / 2, 0, ActualWidth - ft.Width);
                dc.DrawText(ft, new Point(tx, y - 15 - (k % 2) * 12));       // stagger so neighbours do not overlap
                k++;
            }

            // bolt length below
            if (PartKindInfo.IsBolt(bolt.Kind))
            {
                double xa = X(bolt.X + hk), xb = X(bolt.X + bolt.Thickness), y = ActualHeight - 8;
                dc.DrawLine(DimPen, new Point(xa, y), new Point(xb, y));
                dc.DrawLine(DimPen, new Point(xa, y - 5), new Point(xa, y + 4));
                dc.DrawLine(DimPen, new Point(xb, y - 5), new Point(xb, y + 4));
                var ft = Ft("L " + F(bolt.Length), Blue, 10.5, true);
                dc.DrawRectangle(Brushes.White, null, new Rect((xa + xb) / 2 - ft.Width / 2 - 3, y - 7, ft.Width + 6, 13));
                dc.DrawText(ft, new Point((xa + xb) / 2 - ft.Width / 2, y - 7));
            }
        }
    }
}
