using System;
using System.Collections.Generic;
using BoltToolApp.Data;

namespace BoltToolApp.Geometry
{
    /// <summary>A part placed along the common axis (+X). X is the position of the part's local x = 0.</summary>
    public class PlacedPart
    {
        public PartKind Kind;
        public string Role;
        public double X;
        public double Length;     // bolts only (under-head length / rod length)
        public double Thickness;  // axial extent of the part (head + length for bolts)
    }

    public class Layout
    {
        public List<PlacedPart> Parts = new();
        public double BoltLength;
        public string Warning;
    }

    public static class PartBuilder
    {
        public const int Segments = 72;               // multiple of 6 -> hex corners are exact vertices
        const double Tan30 = 0.5773502691896258;

        public static double HeadHeight(PartKind k, SizeRow r) => k switch
        {
            PartKind.HexBolt or PartKind.HexBoltFullThread => r.K,
            PartKind.SocketCapScrew => r.SocketK,
            _ => 0
        };

        public static double Thickness(PartKind k, SizeRow r, double length = 0) => k switch
        {
            PartKind.HexNut => r.M,
            PartKind.ThinNut => r.MThin,
            PartKind.PlainWasher => r.WasherH,
            PartKind.LargeWasher => r.LargeH,
            PartKind.SpringWasher => r.SpringT,
            _ => HeadHeight(k, r) + length
        };

        /// <summary>Local axial position of a base point option.</summary>
        public static double BaseX(PartKind k, SizeRow r, double length, BasePoint bp)
        {
            double t = Thickness(k, r, length);
            if (PartKindInfo.IsBolt(k))
                return bp switch
                {
                    BasePoint.UnderHead => HeadHeight(k, r),
                    BasePoint.ThreadEnd => t,
                    BasePoint.Center => t / 2,
                    _ => 0
                };
            return bp switch
            {
                BasePoint.OuterFace => t,
                BasePoint.Center => t / 2,
                _ => 0
            };
        }

        // ------------------------------------------------------------------ layout

        public static Layout ComputeLayout(BoltJob job)
        {
            var row = job.Row;
            var lay = new Layout();
            if (!job.IsAssembly)
            {
                double len = PartKindInfo.HasLength(job.Kind) ? job.Length : 0;
                lay.Parts.Add(new PlacedPart { Kind = job.Kind, Role = "single", X = 0, Length = len, Thickness = Thickness(job.Kind, row, len) });
                lay.BoltLength = len;
                return lay;
            }

            var bolt = job.BoltKind;
            bool rod = bolt == PartKind.ThreadedRod;
            var headSide = new List<(PartKind k, string role)>();   // ordered outward from the clamped parts
            var nutSide = new List<(PartKind k, string role)>();

            PartKind? W(WasherChoice c) => c == WasherChoice.Plain ? PartKind.PlainWasher : c == WasherChoice.Large ? PartKind.LargeWasher : null;

            if (W(job.HeadWasher) is PartKind hw) headSide.Add((hw, "headwasher"));
            if (job.Spring == SpringPosition.UnderHead) headSide.Add((PartKind.SpringWasher, "spring"));
            if (W(job.NutWasher) is PartKind nw) nutSide.Add((nw, "nutwasher"));
            if (job.Spring == SpringPosition.UnderNut) nutSide.Add((PartKind.SpringWasher, "spring"));
            int nuts = Math.Clamp(job.NutCount, 0, 2);
            for (int i = 0; i < nuts; i++) nutSide.Add((i == 0 ? job.NutKind : PartKind.ThinNut, i == 0 ? "nut" : "jamnut"));
            if (rod) for (int i = 0; i < Math.Max(1, nuts); i++) headSide.Add((i == 0 ? job.NutKind : PartKind.ThinNut, i == 0 ? "headnut" : "headjamnut"));

            double pos = 0;
            foreach (var (k, role) in headSide)
            {
                double t = Thickness(k, row); pos -= t;
                lay.Parts.Add(new PlacedPart { Kind = k, Role = role, X = pos, Thickness = t });
            }
            double headEnd = pos;
            pos = job.ClampLength;
            foreach (var (k, role) in nutSide)
            {
                double t = Thickness(k, row);
                lay.Parts.Add(new PlacedPart { Kind = k, Role = role, X = pos, Thickness = t });
                pos += t;
            }
            // manual shifts from the preview (Ctrl+drag): a part can be moved along the bolt, but not into the head
            if (job.Offsets != null && job.Offsets.Count > 0)
                foreach (var p in lay.Parts)
                    if (job.Offsets.TryGetValue(p.Role, out double off) && Math.Abs(off) > 1e-9)
                        p.X = rod ? p.X + off : Math.Max(p.X + off, headEnd);
            double nutEnd = pos;
            if (nutSide.Count > 0)
            {
                nutEnd = double.MinValue;
                foreach (var p in lay.Parts) nutEnd = Math.Max(nutEnd, p.X + p.Thickness);
            }
            double prot = nuts > 0 ? job.MinProtrusionPitches * row.P : 0;

            double required = rod ? (nutEnd - headEnd) + 2 * prot : (nutEnd - headEnd) + prot;
            double length = job.AutoLength ? job.Table.NextStandardLength(required) : job.Length;
            if (length < required - 1e-6) lay.Warning = $"Length {length} is shorter than the required {required:0.#} mm";

            double hk = HeadHeight(bolt, row);
            double boltX = rod ? headEnd - (length - (nutEnd - headEnd)) / 2 : headEnd - hk;
            lay.Parts.Insert(0, new PlacedPart { Kind = bolt, Role = "bolt", X = boltX, Length = length, Thickness = hk + length });
            lay.BoltLength = length;

            // shift so that the bolt head top (rod start) is at x = 0
            foreach (var p in lay.Parts) p.X -= boltX;
            return lay;
        }

        // ------------------------------------------------------------------ meshes

        static double HexR(double acrossFlats, double th)
        {
            double a = acrossFlats / 2;
            double t = th % (Math.PI / 3); if (t < 0) t += Math.PI / 3;
            return a / Math.Cos(t - Math.PI / 6);
        }

        /// <summary>Builds the mesh of one part in its local frame (x from 0).</summary>
        public static Mesh BuildPart(PartKind kind, SizeRow r, double length, ThreadStyle thread)
        {
            var m = new Mesh();
            switch (kind)
            {
                case PartKind.HexBolt:
                case PartKind.HexBoltFullThread:
                case PartKind.SocketCapScrew:
                case PartKind.ThreadedRod:
                    BuildBolt(m, kind, r, length, thread); break;
                case PartKind.HexNut:
                case PartKind.ThinNut:
                    BuildNut(m, r, Thickness(kind, r), thread); break;
                case PartKind.SpringWasher:
                    BuildSpring(m, r); break;
                default:
                    BuildWasher(m, kind == PartKind.LargeWasher ? r.LargeD2 : r.WasherD2, r.WasherD1, Thickness(kind, r)); break;
            }
            return m;
        }

        public static Mesh BuildLayout(Layout lay, SizeRow r, ThreadStyle thread)
        {
            var all = new Mesh();
            for (int i = 0; i < lay.Parts.Count; i++)
            {
                var p = lay.Parts[i];
                all.Append(BuildPart(p.Kind, r, p.Length, thread), p.X);
                while (all.GroupOwner.Count < all.GroupCount) all.GroupOwner.Add(i);
            }
            return all;
        }

        static void AddThreadZigzag(List<(double x, double r)> pts, double from, double to, double rMaj, double rMin, double pitch)
        {
            // descending or ascending ring teeth between 'from' and 'to' (exclusive); same count for every angle
            double dir = Math.Sign(to - from);
            double span = Math.Abs(to - from);
            int n = (int)Math.Floor(span / (pitch / 2) - 0.5);
            for (int i = 1; i <= n; i++)
                pts.Add((from + dir * i * pitch / 2, (i % 2 == 1) ? rMin : rMaj));
        }

        static void BuildBolt(Mesh m, PartKind kind, SizeRow r, double L, ThreadStyle thread)
        {
            double k = HeadHeight(kind, r);
            double rs = r.D / 2, rMin = r.D3 / 2, c = rs - rMin;
            double xE = k + L;
            double b = StandardTable.ThreadLength(kind, r, L);
            bool rod = kind == PartKind.ThreadedRod;
            bool socket = kind == PartKind.SocketCapScrew;
            double xT0 = rod ? c : Math.Max(k, xE - b);
            double rc = 0.96 * r.S / 2;
            double dk = r.SocketDk / 2, ch = 0.06 * r.SocketDk, ks = r.SocketS / 2, st = r.SocketT;
            bool detailed = thread == ThreadStyle.Detailed;
            int head = m.NewGroup(), shank = rod ? head : m.NewGroup();
            int t0 = m.TriangleCount;

            void AddHead(List<(double x, double r)> p, double th)
            {
                if (socket)
                {
                    double rk = HexR(2 * ks, th);
                    p.Add((st + 0.3 * ks, 0)); p.Add((st, rk)); p.Add((0, rk));
                    p.Add((0, dk - ch)); p.Add((ch, dk)); p.Add((k, dk));
                }
                else
                {
                    double R = HexR(r.S, th);
                    double rc1 = Math.Min(rc, R);
                    p.Add((0, 0)); p.Add((0, rc1)); p.Add(((R - rc1) * Tan30, R)); p.Add((k, R));
                }
            }

            if (!detailed)
            {
                IList<(double, double)> Profile(double th)
                {
                    var p = new List<(double x, double r)>();
                    if (rod) { p.Add((0, 0)); p.Add((0, rMin)); p.Add((c, rs)); }
                    else
                    {
                        AddHead(p, th);
                        p.Add((k, rs));
                        if (xT0 > k + 1e-6) p.Add((xT0, rs));
                    }
                    p.Add((xE - c, rs)); p.Add((xE, rMin)); p.Add((xE, 0));
                    return p;
                }
                m.Loft(Profile, Segments, false, head);
            }
            else
            {
                // head + plain shank, closed at the start of the thread
                IList<(double, double)> Start(double th)
                {
                    var p = new List<(double x, double r)>();
                    if (rod) { p.Add((0, 0)); p.Add((0, rMin)); p.Add((c, rs)); p.Add((c, 0)); return p; }
                    AddHead(p, th);
                    if (xT0 > k + 1e-6) { p.Add((k, rs)); p.Add((xT0, rs)); p.Add((xT0, 0)); }
                    else p.Add((k, 0));
                    return p;
                }
                m.Loft(Start, Segments, false, head);
                // chamfered tip
                m.Loft(_ => new List<(double, double)> { (xE - c, 0), (xE - c, rs), (xE, rMin), (xE, 0) }, Segments, false, head);
                AddHelicalThread(m, xT0, xE - c, rs, rMin, r.P, head);
            }

            if (!rod)
                for (int t = t0; t < m.TriangleCount; t++)
                {
                    // a triangle belongs to the head only when it lies completely within the head (incl. the under-head face)
                    double mx = Math.Max(m.V[m.T[3 * t]].X, Math.Max(m.V[m.T[3 * t + 1]].X, m.V[m.T[3 * t + 2]].X));
                    m.TriGroup[t] = mx <= k + 1e-9 ? head : shank;
                }

            if (thread == ThreadStyle.Simplified)
                m.Threads.Add(new ThreadInfo
                {
                    X0 = xT0, X1 = xE - c, RMajor = rs, RMinor = rMin, Group = shank,
                    ShowTermination = !rod && xT0 > k + 1e-6
                });
        }

        /// <summary>
        /// Real helical V-thread surface between x0 and x1 (crest = rMaj, root = rMin).
        /// Successive turns share their crest vertices, so the surface is continuous; the ends are clamped flat.
        /// </summary>
        static void AddHelicalThread(Mesh m, double x0, double x1, double rMaj, double rMin, double pitch, int group)
        {
            if (x1 - x0 < pitch * 0.25) return;
            double dPhi = 2 * Math.PI / Segments;
            int jStart = -Segments;                                      // one turn before x0
            int jEnd = (int)Math.Ceiling((x1 - x0) / pitch * Segments);  // until the base passes x1
            double[] vs = { 0, pitch / 2, pitch };
            double[] rv = { rMaj, rMin, rMaj };
            int[] prev = null;
            for (int j = jStart; j <= jEnd; j++)
            {
                double phi = j * dPhi;
                double baseX = x0 + pitch * j / Segments;
                double cs = Math.Cos(phi), sn = Math.Sin(phi);
                var cur = new int[3];
                for (int i = 0; i < 3; i++)
                {
                    double x = Math.Clamp(baseX + vs[i], x0, x1);
                    double rr = rv[i];
                    cur[i] = m.AddVertex(new Vec3(x, rr * cs, rr * sn));
                }
                if (prev != null)
                    for (int i = 0; i < 2; i++) m.AddQuad(prev[i], prev[i + 1], cur[i + 1], cur[i], group);
                prev = cur;
            }
        }

        static void BuildNut(Mesh m, SizeRow r, double h, ThreadStyle thread)
        {
            double rh = r.D1Nut / 2, rc = 0.96 * r.S / 2;
            int g = m.NewGroup();
            IList<(double, double)> Profile(double th)
            {
                double R = HexR(r.S, th), rc1 = Math.Min(rc, R), drop = (R - rc1) * Tan30;
                var p = new List<(double x, double r)>
                {
                    (0, rh), (0, rc1), (drop, R), (h - drop, R), (h, rc1), (h, rh)
                };
                if (thread == ThreadStyle.Detailed) AddThreadZigzag(p, h, 0, rh, r.D / 2, r.P);
                return p;
            }
            m.Loft(Profile, Segments, true, g);
            if (thread == ThreadStyle.Simplified)
                m.Threads.Add(new ThreadInfo { X0 = 0, X1 = h, RMajor = r.D / 2, RMinor = rh, Internal = true, Group = g });
        }

        static void BuildWasher(Mesh m, double d2, double d1, double h)
        {
            int g = m.NewGroup();
            var prof = new List<(double, double)> { (0, d1 / 2), (0, d2 / 2), (h, d2 / 2), (h, d1 / 2) };
            m.Loft(_ => prof, Segments, true, g);
        }

        static void BuildSpring(Mesh m, SizeRow r)
        {
            int g = m.NewGroup();
            double ri = r.SpringD1 / 2, ro = r.SpringD2 / 2, t = r.SpringT;
            double rise = 0.35 * t;
            double gap = Math.Min(0.5, 0.6 * t / ((ri + ro) / 2));      // radians
            double start = Math.PI / 2 + gap / 2, sweep = 2 * Math.PI - gap;   // gap at the top of the side view
            IList<(double, double)> Profile(double th)
            {
                double f = (th - start) / sweep;
                double x0 = rise * (f - 0.5);
                return new List<(double, double)> { (x0, ri), (x0, ro), (x0 + t, ro), (x0 + t, ri) };
            }
            m.Loft(Profile, Segments, true, g, start, sweep);
        }
    }
}
