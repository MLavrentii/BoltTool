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
        public double OuterR;     // largest radius (for grips, diagrams)
        public StackItem Item;    // null for the bolt / a single part
    }

    public class Layout
    {
        public List<PlacedPart> Parts = new();
        public double BoltLength;
        public double Required;     // shortest bolt length that fits everything incl. protrusion
        public double UnderHead;    // x of the underside of the head (rod: start of the element stack)
        public double ClampStart;   // x of the face that presses on the clamped parts from the head side
        public double ClampEnd;     // x of the face that presses on the clamped parts from the nut side
        public double StackEnd;     // x of the far face of the last element
        public double BoltEnd;      // x of the thread end
        public bool HasNutSide;     // at least one element behind the clamped parts
        public string Warning;
        public double Protrusion => BoltEnd - StackEnd;
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

        /// <summary>Standard thickness of a part (ignores any override).</summary>
        public static double StandardThickness(PartKind k, SizeRow r) => k switch
        {
            PartKind.HexNut => r.M,
            PartKind.ThinNut => r.MThin,
            PartKind.FlangeNut => r.FlangeM,
            PartKind.ChannelNut => ChannelDims(r).t,
            PartKind.ChannelNutHolder => HolderPlate + HolderLip + ChannelDims(r).t,
            PartKind.PlainWasher => r.WasherH,
            PartKind.LargeWasher => r.LargeH,
            PartKind.SpringWasher => r.SpringT,
            _ => HeadHeight(k, r)
        };

        public static double Thickness(PartKind k, SizeRow r, double length = 0, StackItem item = null)
        {
            if (PartKindInfo.IsBolt(k)) return HeadHeight(k, r) + length;
            return item != null && item.Thickness > 0 ? item.Thickness : StandardThickness(k, r);
        }

        /// <summary>Standard outer diameter of a washer-like part (0 for hex nuts and bolts).</summary>
        public static double StandardOuterD(PartKind k, SizeRow r) => k switch
        {
            PartKind.PlainWasher => r.WasherD2,
            PartKind.LargeWasher => r.LargeD2,
            PartKind.SpringWasher => r.SpringD2,
            PartKind.FlangeNut => r.FlangeD,
            _ => 0
        };

        /// <summary>
        /// Channel nut for 41 mm strut channel (slot 22 mm): length across the channel, width (fits through the
        /// slot), thickness. Approximation of common catalogue nuts (M10: 35 × 20 × 8) – verify with the maker.
        /// </summary>
        public static (double length, double width, double t) ChannelDims(SizeRow r) =>
            (r.D <= 16 ? 35.0 : 40.0, 20.0, Math.Max(6.0, Math.Round(0.8 * r.D)));

        public static double OuterD(PartKind k, SizeRow r, StackItem item = null)
        {
            double std = StandardOuterD(k, r);
            if (std <= 0) return 0;
            double min = r.D + 1;
            return item != null && item.OuterD > min ? item.OuterD : std;
        }

        /// <summary>Largest radius of a part.</summary>
        public static double OuterRadius(PartKind k, SizeRow r, StackItem item = null)
        {
            double d = OuterD(k, r, item);
            if (d > 0) return d / 2;
            if (k == PartKind.SocketCapScrew) return r.SocketDk / 2;
            if (k == PartKind.ChannelNut) { var c = ChannelDims(r); return Math.Sqrt(c.length * c.length + c.width * c.width) / 2; }
            if (k == PartKind.ChannelNutHolder) return Math.Sqrt(Math.Pow(HolderW / 2 + HolderTab, 2) + Math.Pow(HolderL / 2, 2));
            if (k == PartKind.ThreadedRod) return r.D / 2;
            return r.S / 2 / 0.8660254037844386;      // hexagon across corners
        }

        /// <summary>Local axial position of a base point option (single parts).</summary>
        public static double BaseX(PartKind k, SizeRow r, double length, BasePoint bp)
        {
            double t = Thickness(k, r, length);
            if (PartKindInfo.IsBolt(k))
                return bp switch
                {
                    BasePoint.UnderHead or BasePoint.ClampHead => HeadHeight(k, r),
                    BasePoint.ThreadEnd or BasePoint.ClampNut => t,
                    _ => 0
                };
            return bp == BasePoint.OuterFace ? t : 0;
        }

        // ------------------------------------------------------------------ layout

        public static Layout ComputeLayout(BoltJob job)
        {
            var row = job.Row;
            var lay = new Layout();
            if (!job.IsAssembly)
            {
                double len = PartKindInfo.HasLength(job.Kind) ? job.Length : 0;
                lay.Parts.Add(new PlacedPart
                {
                    Kind = job.Kind, Role = "single", X = 0, Length = len, Thickness = Thickness(job.Kind, row, len),
                    OuterR = OuterRadius(job.Kind, row)
                });
                lay.BoltLength = lay.Required = len;
                lay.UnderHead = lay.ClampStart = HeadHeight(job.Kind, row);
                lay.ClampEnd = lay.StackEnd = lay.BoltEnd = lay.Parts[0].Thickness;
                return lay;
            }

            job.EnsureStack();
            var bolt = job.BoltKind;
            bool rod = bolt == PartKind.ThreadedRod;

            // x = 0 is the underside of the bolt head; elements follow each other with their gaps
            double x = 0;
            bool headNut = false, endNut = false, hasNutSide = false;
            foreach (var it in job.Stack)
            {
                if (it.Side != StackSide.Head) continue;
                x += Math.Max(0, it.Gap);
                double t = Thickness(it.Kind, row, 0, it);
                lay.Parts.Add(new PlacedPart { Kind = it.Kind, Role = it.Id, X = x, Thickness = t, OuterR = OuterRadius(it.Kind, row, it), Item = it });
                x += t;
                headNut |= PartKindInfo.IsNut(it.Kind);
            }
            lay.ClampStart = x;
            x += Math.Max(0, job.ClampLength);
            lay.ClampEnd = x;
            foreach (var it in job.Stack)
            {
                if (it.Side != StackSide.Nut) continue;
                x += Math.Max(0, it.Gap); // first nut-side element can have free space after the plates
                hasNutSide = true;
                double t = Thickness(it.Kind, row, 0, it);
                lay.Parts.Add(new PlacedPart { Kind = it.Kind, Role = it.Id, X = x, Thickness = t, OuterR = OuterRadius(it.Kind, row, it), Item = it });
                x += t;
                endNut |= PartKindInfo.IsNut(it.Kind);
            }
            lay.HasNutSide = hasNutSide;
            lay.StackEnd = x;

            double prot = job.MinProtrusionPitches * row.P;
            double protEnd = endNut ? prot : 0;
            double protHead = rod && headNut ? prot : 0;      // a rod also sticks out of the nut on the head side
            double required = lay.StackEnd + protEnd + protHead;
            double length = job.AutoLength ? job.Table.NextStandardLength(required) : job.Length;
            if (length < required - 1e-6) lay.Warning = $"Length {length:0.##} is shorter than the required {required:0.#} mm";
            lay.Required = required;
            lay.BoltLength = length;

            // shift so that the bolt head top (rod start) is at x = 0
            double hk = HeadHeight(bolt, row);
            double shift = rod ? protHead : hk;
            foreach (var p in lay.Parts) p.X += shift;
            lay.UnderHead = shift;
            lay.ClampStart += shift; lay.ClampEnd += shift; lay.StackEnd += shift;
            lay.BoltEnd = hk + length;
            lay.Parts.Insert(0, new PlacedPart
            {
                Kind = bolt, Role = "bolt", X = 0, Length = length, Thickness = hk + length, OuterR = OuterRadius(bolt, row)
            });
            return lay;
        }

        // ------------------------------------------------------------------ meshes

        static double HexR(double acrossFlats, double th)
        {
            double a = acrossFlats / 2;
            double t = th % (Math.PI / 3); if (t < 0) t += Math.PI / 3;
            return a / Math.Cos(t - Math.PI / 6);
        }

        /// <summary>
        /// Builds the mesh of one part in its local frame (x from 0). <paramref name="item"/> carries thickness /
        /// diameter overrides; <paramref name="assembled"/> draws a spring washer flat (tightened).
        /// </summary>
        public static Mesh BuildPart(PartKind kind, SizeRow r, double length, ThreadStyle thread,
                                     StackItem item = null, bool assembled = false)
        {
            var m = new Mesh();
            double t = Thickness(kind, r, length, item);
            switch (kind)
            {
                case PartKind.HexBolt:
                case PartKind.HexBoltFullThread:
                case PartKind.SocketCapScrew:
                case PartKind.ThreadedRod:
                    BuildBolt(m, kind, r, length, thread); break;
                case PartKind.HexNut:
                case PartKind.ThinNut:
                    BuildNut(m, r, t, thread); break;
                case PartKind.FlangeNut:
                    // the flange faces the clamped parts: toward the head on the nut side, away from it on the head side
                    BuildFlangeNut(m, r, t, OuterD(kind, r, item), thread, item != null && item.Side == StackSide.Head); break;
                case PartKind.ChannelNut:
                    // the grooved face grips the channel lips = faces the clamped parts
                    BuildChannelNut(m, r, t, thread, item != null && item.Side == StackSide.Head); break;
                case PartKind.ChannelNutHolder:
                {
                    var hm = BuildChannelNutHolder(r, t, thread);
                    m.Append(item != null && item.Side == StackSide.Head ? hm.MirroredX(t) : hm, 0);
                    break;
                }
                case PartKind.SpringWasher:
                    BuildSpring(m, r, t, OuterD(kind, r, item), assembled); break;
                default:
                    BuildWasher(m, OuterD(kind, r, item), r.WasherD1, t); break;
            }
            return m;
        }

        public static Mesh BuildLayout(Layout lay, SizeRow r, ThreadStyle thread, bool assembled = true)
        {
            var all = new Mesh();
            for (int i = 0; i < lay.Parts.Count; i++)
            {
                var p = lay.Parts[i];
                all.Append(BuildPart(p.Kind, r, p.Length, thread, p.Item, assembled && lay.Parts.Count > 1), p.X);
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

        /// <summary>
        /// Rectangular channel nut: length along model Y, two opposite corners cut (so it can be turned in the
        /// channel), threaded hole, two V-grooves across the width on the face that grips the channel lips.
        /// Built as a loft with explicit angles so all corners and groove edges are exact.
        /// flip = grooved face at x = h instead of x = 0.
        /// </summary>
        // ---- holder plate of the 翼型ナット + 固定ブロック set (plastic, sits outside the channel on the lips) ----
        public const double HolderPlate = 2.0;   // plate thickness
        public const double HolderLip = 2.5;     // default channel lip thickness = height of the collar in the slot
        const double HolderL = 46.0, HolderW = 28.0, HolderTab = 3.0, HolderCollarR = 9.0;

        /// <summary>
        /// Channel nut with its holder ("wing nut + fixing block"):
        ///   x = 0 .. plate        holder plate (long along the channel = model Z), V-notches at both ends,
        ///                          wing tabs on two diagonal corners
        ///   plate .. plate+lip    round collar that sits in the channel slot (lip thickness)
        ///   plate+lip .. total    the metal channel nut inside the channel (long across the channel = model Y)
        /// total = whole height; a thickness override changes the lip / collar height.
        /// Holder dimensions are approximations from the installation drawing – verify with the maker.
        /// </summary>
        static Mesh BuildChannelNutHolder(SizeRow r, double total, ThreadStyle thread)
        {
            var m = new Mesh();
            double tNut = ChannelDims(r).t;
            double lip = Math.Max(0.5, total - HolderPlate - tNut);
            double hu = HolderW / 2, hv = HolderL / 2, n = 3.0, nd = 4.0, tw = 6.0, tab = HolderTab;
            var outline = new (double u, double v)[]
            {
                (-hu, -hv + tw), (-hu - tab, -hv + tw * 0.4), (-hu - tab, -hv + 1.0), (-hu, -hv),   // wing
                (-n, -hv), (0, -hv + nd), (n, -hv),                                                  // V notch
                (hu, -hv), (hu, hv - tw),
                (hu + tab, hv - tw * 0.4), (hu + tab, hv - 1.0), (hu, hv),                           // wing
                (n, hv), (0, hv - nd), (-n, hv), (-hu, hv)                                           // V notch
            };
            double rHole = r.D / 2 + 0.5, rc = Math.Max(HolderCollarR, rHole + 1.5);
            var angles = new List<double>();
            for (int k = 0; k < 96; k++) angles.Add(2 * Math.PI * k / 96);
            foreach (var (u, v) in outline) angles.Add(NormAngle(Math.Atan2(v, u)));
            int g = m.NewGroup();
            m.LoftAngles(th =>
            {
                double R = RayPolygon(outline, th);
                return new List<(double, double)>
                {
                    (0, rHole), (0, R), (HolderPlate, R), (HolderPlate, rc), (HolderPlate + lip, rc), (HolderPlate + lip, rHole)
                };
            }, UniqueAngles(angles), true, g);
            var nut = new Mesh();
            BuildChannelNut(nut, r, tNut, thread, false);
            m.Append(nut, HolderPlate + lip);
            return m;
        }

        static double NormAngle(double a) { a %= 2 * Math.PI; return a < 0 ? a + 2 * Math.PI : a; }

        static List<double> UniqueAngles(List<double> angles)
        {
            angles.Sort();
            var uniq = new List<double>();
            foreach (double a in angles) if (uniq.Count == 0 || a - uniq[^1] > 1e-7) uniq.Add(a);
            if (uniq.Count > 1 && 2 * Math.PI - uniq[^1] + uniq[0] < 1e-7) uniq.RemoveAt(uniq.Count - 1);
            return uniq;
        }

        /// <summary>Distance from the origin to a star-shaped polygon along the direction th.</summary>
        static double RayPolygon((double u, double v)[] poly, double th)
        {
            double dx = Math.Cos(th), dy = Math.Sin(th), best = double.MaxValue;
            for (int i = 0; i < poly.Length; i++)
            {
                var (x1, y1) = poly[i]; var (x2, y2) = poly[(i + 1) % poly.Length];
                double ex = x2 - x1, ey = y2 - y1, den = dx * ey - dy * ex;
                if (Math.Abs(den) < 1e-12) continue;
                double t = (x1 * ey - y1 * ex) / den, s = (x1 * dy - y1 * dx) / den;
                if (t > 0 && s >= -1e-9 && s <= 1 + 1e-9) best = Math.Min(best, t);
            }
            return best;
        }

        static void BuildChannelNut(Mesh m, SizeRow r, double h, ThreadStyle thread, bool flip)
        {
            var (len, wid, _) = ChannelDims(r);
            double hl = len / 2, hw = wid / 2, cut = 0.18 * wid;
            var poly = new (double u, double v)[]
            {
                (hl, -hw), (hl, hw - cut), (hl - cut, hw), (-hl, hw), (-hl, -hw + cut), (-hl + cut, -hw)
            };
            double rh = r.D1Nut / 2;
            double gPos = 12.0, gW = 2.4, gD = Math.Min(1.2, 0.2 * h);    // lips of a 41 channel: slot 22 mm
            int g = m.NewGroup();

            double Ray(double th)
            {
                double dx = Math.Cos(th), dy = Math.Sin(th), best = double.MaxValue;
                for (int i = 0; i < poly.Length; i++)
                {
                    var (x1, y1) = poly[i]; var (x2, y2) = poly[(i + 1) % poly.Length];
                    double ex = x2 - x1, ey = y2 - y1, den = dx * ey - dy * ex;
                    if (Math.Abs(den) < 1e-12) continue;
                    double t = (x1 * ey - y1 * ex) / den, s = (x1 * dy - y1 * dx) / den;
                    if (t > 0 && s >= -1e-9 && s <= 1 + 1e-9) best = Math.Min(best, t);
                }
                return best;
            }

            var angles = new List<double>();
            for (int k = 0; k < 96; k++) angles.Add(2 * Math.PI * k / 96);
            double Norm(double a) { a %= 2 * Math.PI; return a < 0 ? a + 2 * Math.PI : a; }
            foreach (var (u, v) in poly) angles.Add(Norm(Math.Atan2(v, u)));
            foreach (double sx in new[] { 1.0, -1.0 })
                foreach (double dl in new[] { -gW / 2, 0, gW / 2 })
                    foreach (double sy in new[] { 1.0, -1.0 })
                        angles.Add(Norm(Math.Atan2(sy * hw, sx * (gPos + dl))));
            angles.Sort();
            var uniq = new List<double>();
            foreach (double a in angles) if (uniq.Count == 0 || a - uniq[^1] > 1e-7) uniq.Add(a);
            if (uniq.Count > 1 && 2 * Math.PI - uniq[^1] + uniq[0] < 1e-7) uniq.RemoveAt(uniq.Count - 1);

            IList<(double, double)> Profile(double th)
            {
                double R = Ray(th), cs = Math.Abs(Math.Cos(th));
                var p = new List<(double x, double r)> { (0, rh) };
                foreach (var (dl, depth) in new[] { (-gW / 2, 0.0), (0.0, gD), (gW / 2, 0.0) })
                {
                    double rr = cs > 1e-9 ? (gPos + dl) / cs : double.MaxValue;
                    bool inside = rr > rh && rr < R - 1e-6;
                    p.Add((inside ? depth : 0, Math.Clamp(rr, rh, R)));
                }
                p.Add((0, R)); p.Add((h, R)); p.Add((h, rh));
                if (!flip) return p;
                var q = new List<(double x, double r)>(p.Count);
                for (int i = p.Count - 1; i >= 0; i--) q.Add((h - p[i].x, p[i].r));
                return q;
            }
            m.LoftAngles(Profile, uniq, true, g);
            if (thread != ThreadStyle.None)
                m.Threads.Add(new ThreadInfo { X0 = 0, X1 = h, RMajor = r.D / 2, RMinor = rh, Internal = true, Group = g });
        }

        /// <summary>Hex nut with a round washer face. flip = flange at the far end (x = h).</summary>
        static void BuildFlangeNut(Mesh m, SizeRow r, double h, double flangeD, ThreadStyle thread, bool flip)
        {
            double rh = r.D1Nut / 2, rc = 0.96 * r.S / 2;
            double rf = Math.Max(flangeD / 2, r.S / 2 / 0.8660254037844386 + 0.2);
            double tf = Math.Min(0.15 * r.D, h * 0.3);
            int g = m.NewGroup();
            IList<(double, double)> Profile(double th)
            {
                double R = HexR(r.S, th), rc1 = Math.Min(rc, R), drop = (R - rc1) * Tan30;
                double cone = tf + (rf - R) * 0.35;
                var p = new List<(double x, double r)>
                {
                    (0, rh), (0, rf), (tf, rf), (Math.Min(cone, h - drop), R), (h - drop, R), (h, rc1), (h, rh)
                };
                if (!flip) return p;
                // mirror along the axis and reverse the order so the surface keeps its orientation
                var q = new List<(double x, double r)>(p.Count);
                for (int i = p.Count - 1; i >= 0; i--) q.Add((h - p[i].x, p[i].r));
                return q;
            }
            m.Loft(Profile, Segments, true, g);
            if (thread != ThreadStyle.None)
                m.Threads.Add(new ThreadInfo { X0 = 0, X1 = h, RMajor = r.D / 2, RMinor = rh, Internal = true, Group = g });
        }

        /// <summary>Split spring washer. compressed = tightened (flat); otherwise with its free helical rise.</summary>
        static void BuildSpring(Mesh m, SizeRow r, double t, double outerD, bool compressed)
        {
            int g = m.NewGroup();
            double ri = r.SpringD1 / 2, ro = Math.Max(outerD / 2, ri + 0.5);
            double rise = compressed ? 0 : 0.35 * t;
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
