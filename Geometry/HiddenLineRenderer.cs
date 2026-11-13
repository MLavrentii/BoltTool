using System;
using System.Collections.Generic;
using System.Linq;

namespace BoltToolApp.Geometry
{
    public readonly struct P2
    {
        public readonly double X, Y;
        public P2(double x, double y) { X = x; Y = y; }
        public static P2 operator -(P2 a, P2 b) => new(a.X - b.X, a.Y - b.Y);
        public double Dist(P2 o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
        public override bool Equals(object o) => o is P2 p && p.X == X && p.Y == Y;
        public override int GetHashCode() => HashCode.Combine(X, Y);
    }

    public class Polyline2
    {
        public LineStyle Style;
        public List<P2> Points = new();
        public bool Closed;
    }

    /// <summary>Result of a hidden-line projection: what goes into a 2D block.</summary>
    public class Drawing2D
    {
        public List<Polyline2> Lines = new();
        public Dictionary<int, List<P2>> Wipeouts = new();   // group -> convex outline
        public Mat3 View;
        public double MinX, MinY, MaxX, MaxY;
        public double CenterX, CenterY, Radius;     // view-independent frame: projected 3D centre + bounding sphere
        public Vec3 Center3D;                       // centre of the bounding sphere in model space
        public P2 Project(Vec3 p) { var s = View.Apply(p); return new P2(s.X, s.Y); }
        public Vec3 ToViewer => View.Transposed().Apply(new Vec3(0, 0, 1));
    }

    /// <summary>
    /// Orthographic hidden-line removal for triangle meshes: crease + silhouette edges are
    /// sampled and depth-tested against all triangles (uniform screen grid for speed).
    /// </summary>
    public static class HiddenLineRenderer
    {
        public const double CreaseDeg = 15;

        public static Drawing2D Render(Mesh mesh, double azimuth, double elevation, double spin,
                                       bool hiddenLines, bool quality = true)
        {
            var R = Mat3.View(azimuth, elevation, spin);
            var d = new Drawing2D { View = R };
            int nv = mesh.V.Count, nt = mesh.TriangleCount;
            if (nt == 0) return d;

            var S = new Vec3[nv];
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i < nv; i++)
            {
                var s = R.Apply(mesh.V[i]); S[i] = s;
                minX = Math.Min(minX, s.X); maxX = Math.Max(maxX, s.X);
                minY = Math.Min(minY, s.Y); maxY = Math.Max(maxY, s.Y);
                minZ = Math.Min(minZ, s.Z); maxZ = Math.Max(maxZ, s.Z);
            }
            d.MinX = minX; d.MinY = minY; d.MaxX = maxX; d.MaxY = maxY;
            {
                // bounding sphere of the model: the preview scales by it, so the zoom does not change while orbiting
                double ax = double.MaxValue, ay = double.MaxValue, az = double.MaxValue, bx = double.MinValue, by = double.MinValue, bz = double.MinValue;
                foreach (var v in mesh.V)
                {
                    ax = Math.Min(ax, v.X); bx = Math.Max(bx, v.X);
                    ay = Math.Min(ay, v.Y); by = Math.Max(by, v.Y);
                    az = Math.Min(az, v.Z); bz = Math.Max(bz, v.Z);
                }
                var c3 = new Vec3((ax + bx) / 2, (ay + by) / 2, (az + bz) / 2);
                double rad = 0;
                foreach (var v in mesh.V) rad = Math.Max(rad, (v - c3).Length);
                var cs = R.Apply(c3);
                d.CenterX = cs.X; d.CenterY = cs.Y; d.Radius = rad;
                d.Center3D = c3;
            }
            double size = Math.Max(Math.Max(maxX - minX, maxY - minY), Math.Max(maxZ - minZ, 1e-6));
            double eps = 1e-4 * size;
            double step = size / (quality ? 400.0 : 120.0);

            // ---- triangle normals (screen space) ----
            var nrm = new Vec3[nt];
            var facing = new int[nt];
            for (int t = 0; t < nt; t++)
            {
                var a = S[mesh.T[3 * t]]; var b = S[mesh.T[3 * t + 1]]; var c = S[mesh.T[3 * t + 2]];
                var n = (b - a).Cross(c - a).Normalized();
                nrm[t] = n;
                facing[t] = n.Z > 1e-9 ? 1 : n.Z < -1e-9 ? -1 : 0;
            }

            // ---- edge adjacency ----
            var edges = new Dictionary<long, (int t1, int t2, int count)>(nt * 2);
            long Key(int a, int b) => a < b ? (long)a * nv + b : (long)b * nv + a;
            for (int t = 0; t < nt; t++)
                for (int e = 0; e < 3; e++)
                {
                    int a = mesh.T[3 * t + e], b = mesh.T[3 * t + (e + 1) % 3];
                    long k = Key(a, b);
                    if (edges.TryGetValue(k, out var v)) edges[k] = (v.t1, v.count == 1 ? t : v.t2, v.count + 1);
                    else edges[k] = (t, -1, 1);
                }

            var grid = new TriGrid(S, mesh, minX, minY, maxX, maxY, quality ? 96 : 48);
            double cosCrease = Math.Cos(CreaseDeg * Math.PI / 180);
            var raw = new List<(P2 a, P2 b, LineStyle st)>();

            foreach (var kv in edges)
            {
                var (t1, t2, cnt) = kv.Value;
                bool candidate;
                if (cnt != 2) candidate = true;
                else
                {
                    bool crease = nrm[t1].Dot(nrm[t2]) < cosCrease;
                    bool sil = facing[t1] * facing[t2] < 0 || ((facing[t1] == 0) != (facing[t2] == 0));
                    candidate = crease || sil;
                }
                if (!candidate) continue;
                int va = (int)(kv.Key / nv), vb = (int)(kv.Key % nv);
                SampleSegment(S[va], S[vb], grid, t1, t2, -1, eps, step, hiddenLines, LineStyle.Visible, raw);
            }

            // ---- conventional thread representation ----
            var toViewer = R.Transposed().Apply(new Vec3(0, 0, 1));
            var axis = new Vec3(1, 0, 0);
            foreach (var th in mesh.Threads)
                foreach (var ov in ThreadOverlays(th, axis, toViewer))
                    mesh.Overlays.Add(ov);
            foreach (var ov in mesh.Overlays)
            {
                if (ov.FacingNormal is Vec3 fn && fn.Dot(toViewer) < 0.05) continue;
                for (int i = 0; i + 1 < ov.Points.Count; i++)
                    SampleSegment(R.Apply(ov.Points[i]), R.Apply(ov.Points[i + 1]), grid, -1, -1, ov.IgnoreGroup,
                                  eps, step, false, ov.Style, raw);
            }
            // overlays generated from threads are view dependent -> remove them again
            mesh.Overlays.RemoveAll(o => o.IsThreadOverlay);

            // clean-up (like OVERKILL): merge collinear overlapping pieces, drop duplicates and crumbs
            raw = Overkill(raw, size);
            d.Lines = Chain(raw, size * 5e-6);

            // ---- wipeout outlines: convex hull per group ----
            var pts = new Dictionary<int, List<P2>>();
            for (int t = 0; t < nt; t++)
            {
                int g = mesh.TriGroup[t];
                if (!pts.TryGetValue(g, out var l)) pts[g] = l = new List<P2>();
                for (int e = 0; e < 3; e++) { var s = S[mesh.T[3 * t + e]]; l.Add(new P2(s.X, s.Y)); }
            }
            foreach (var kv in pts)
            {
                var hull = ConvexHull(kv.Value);
                if (hull.Count >= 3) d.Wipeouts[kv.Key] = hull;
            }
            return d;
        }

        static void SampleSegment(Vec3 p0, Vec3 p1, TriGrid grid, int ex1, int ex2, int ignoreGroup, double eps,
                                  double step, bool keepHidden, LineStyle style, List<(P2, P2, LineStyle)> outList)
        {
            double len = Math.Sqrt((p1.X - p0.X) * (p1.X - p0.X) + (p1.Y - p0.Y) * (p1.Y - p0.Y));
            if (len < 1e-9) return;
            int n = Math.Max(1, (int)Math.Ceiling(len / step));
            bool? cur = null; double tStart = 0;
            for (int k = 0; k <= n; k++)
            {
                bool vis = false;
                if (k < n)
                {
                    double t = (k + 0.5) / n;
                    var q = p0 + (p1 - p0) * t;
                    vis = !grid.Occluded(q, ex1, ex2, ignoreGroup, eps);
                }
                if (k == n || cur == null || vis != cur.Value)
                {
                    if (cur != null)
                    {
                        double tEnd = (double)k / n;
                        if (cur.Value || keepHidden)
                        {
                            var a = p0 + (p1 - p0) * tStart; var b = p0 + (p1 - p0) * tEnd;
                            outList.Add((new P2(a.X, a.Y), new P2(b.X, b.Y), cur.Value ? style : LineStyle.Hidden));
                        }
                    }
                    cur = vis; tStart = (double)k / n;
                }
            }
        }

        static IEnumerable<Overlay> ThreadOverlays(ThreadInfo th, Vec3 axis, Vec3 toViewer)
        {
            var cr = axis.Cross(toViewer);
            double along = axis.Dot(toViewer);
            if (!th.Internal)
            {
                if (cr.Length > 0.05)
                {
                    var u = cr.Normalized();
                    foreach (double sgn in new[] { 1.0, -1.0 })
                    {
                        var o = new Overlay { IgnoreGroup = th.Group, Style = LineStyle.Thin, IsThreadOverlay = true };
                        o.Points.Add(new Vec3(th.X0, 0, 0) + u * (sgn * th.RMinor));
                        o.Points.Add(new Vec3(th.X1, 0, 0) + u * (sgn * th.RMinor));
                        yield return o;
                    }
                    if (th.ShowTermination)
                    {
                        var vp = (toViewer - axis * along).Normalized();
                        var o = new Overlay { IgnoreGroup = th.Group, Style = LineStyle.Thin, IsThreadOverlay = true };
                        for (int i = 0; i <= 24; i++)
                        {
                            double phi = Math.PI * i / 24;
                            o.Points.Add(new Vec3(th.X0, 0, 0) + (u * Math.Cos(phi) + vp * Math.Sin(phi)) * th.RMajor);
                        }
                        yield return o;
                    }
                }
            }
            else if (Math.Abs(along) > 0.3)
            {
                // nut: 3/4 circle of the major diameter on the face toward the viewer
                double x = along > 0 ? th.X1 : th.X0;
                var o = new Overlay
                {
                    IgnoreGroup = th.Group, Style = LineStyle.Thin, IsThreadOverlay = true,
                    FacingNormal = new Vec3(along > 0 ? 1 : -1, 0, 0)
                };
                for (int i = 0; i <= 36; i++)
                {
                    double phi = 1.5 * Math.PI * i / 36 + Math.PI / 2;
                    o.Points.Add(new Vec3(x, th.RMajor * Math.Cos(phi), th.RMajor * Math.Sin(phi)));
                }
                yield return o;
            }
        }

        // ------------------------------------------------------------ overkill

        sealed class LineGroup
        {
            public double Ox, Oy, Dx, Dy;                       // reference line: origin + unit direction
            public readonly List<(double t0, double t1)>[] Iv =
                { new List<(double, double)>(), new List<(double, double)>(), new List<(double, double)>() };
            public double Dist(P2 p) => Math.Abs((p.X - Ox) * -Dy + (p.Y - Oy) * Dx);
            public double Param(P2 p) => (p.X - Ox) * Dx + (p.Y - Oy) * Dy;
            public P2 At(double t) => new(Ox + Dx * t, Oy + Dy * t);
        }

        static List<(double t0, double t1)> Union(List<(double t0, double t1)> iv, double tol)
        {
            var r = new List<(double t0, double t1)>();
            foreach (var i in iv.OrderBy(x => x.t0))
            {
                if (r.Count > 0 && i.t0 <= r[^1].t1 + tol) { if (i.t1 > r[^1].t1) r[^1] = (r[^1].t0, i.t1); }
                else r.Add(i);
            }
            return r;
        }

        /// <summary>a minus b (both sorted, non-overlapping).</summary>
        static List<(double t0, double t1)> Subtract(List<(double t0, double t1)> a, List<(double t0, double t1)> b, double tol)
        {
            if (b.Count == 0) return a;
            var r = new List<(double t0, double t1)>();
            foreach (var i in a)
            {
                double s = i.t0;
                foreach (var c in b)
                {
                    if (c.t1 <= s || c.t0 >= i.t1) continue;
                    if (c.t0 - s > tol) r.Add((s, c.t0));
                    s = Math.Max(s, c.t1);
                }
                if (i.t1 - s > tol) r.Add((s, i.t1));
            }
            return r;
        }

        /// <summary>
        /// Removes what a draftsman would not draw twice: segments lying on the same line are merged into one,
        /// duplicates disappear, and thin / hidden lines are cut away where a visible line already covers them.
        /// </summary>
        static List<(P2 a, P2 b, LineStyle st)> Overkill(List<(P2 a, P2 b, LineStyle st)> segs, double size)
        {
            double tol = size * 2e-5;
            const int Buckets = 720;                            // 0.25 degree per bucket
            var buckets = new Dictionary<int, List<LineGroup>>();
            var groups = new List<LineGroup>();

            foreach (var s in segs.OrderByDescending(x => x.a.Dist(x.b)))
            {
                double len = s.a.Dist(s.b);
                if (len < tol) continue;
                double dx = (s.b.X - s.a.X) / len, dy = (s.b.Y - s.a.Y) / len;
                if (dx < 0 || (Math.Abs(dx) < 1e-12 && dy < 0)) { dx = -dx; dy = -dy; }
                int bi = Math.Clamp((int)Math.Floor((Math.Atan2(dy, dx) + Math.PI / 2) / Math.PI * Buckets), 0, Buckets - 1);
                LineGroup g = null;
                for (int db = -1; db <= 1 && g == null; db++)
                {
                    if (!buckets.TryGetValue((bi + db + Buckets) % Buckets, out var list)) continue;
                    foreach (var c in list)
                        if (c.Dist(s.a) <= tol && c.Dist(s.b) <= tol) { g = c; break; }
                }
                if (g == null)
                {
                    g = new LineGroup { Ox = s.a.X, Oy = s.a.Y, Dx = dx, Dy = dy };
                    if (!buckets.TryGetValue(bi, out var list)) buckets[bi] = list = new List<LineGroup>();
                    list.Add(g); groups.Add(g);
                }
                double t0 = g.Param(s.a), t1 = g.Param(s.b);
                g.Iv[(int)s.st].Add(t0 <= t1 ? (t0, t1) : (t1, t0));
            }

            var result = new List<(P2, P2, LineStyle)>(segs.Count);
            foreach (var g in groups)
            {
                var vis = Union(g.Iv[(int)LineStyle.Visible], tol);
                var thin = Subtract(Union(g.Iv[(int)LineStyle.Thin], tol), vis, tol);
                var covered = Union(vis.Concat(thin).ToList(), tol);
                var hid = Subtract(Union(g.Iv[(int)LineStyle.Hidden], tol), covered, tol);
                foreach (var i in vis) result.Add((g.At(i.t0), g.At(i.t1), LineStyle.Visible));
                foreach (var i in thin) result.Add((g.At(i.t0), g.At(i.t1), LineStyle.Thin));
                foreach (var i in hid) result.Add((g.At(i.t0), g.At(i.t1), LineStyle.Hidden));
            }
            return result;
        }

        // ------------------------------------------------------------ chaining

        static List<Polyline2> Chain(List<(P2 a, P2 b, LineStyle st)> segs, double tol)
        {
            var result = new List<Polyline2>();
            foreach (var group in segs.GroupBy(s => s.st))
            {
                var list = group.ToList();
                double q = Math.Max(tol, 1e-12) * 20;   // snap grid for endpoint matching
                (long, long) K(P2 p) => ((long)Math.Round(p.X / q), (long)Math.Round(p.Y / q));
                var map = new Dictionary<(long, long), List<int>>();
                for (int i = 0; i < list.Count; i++)
                {
                    foreach (var p in new[] { list[i].a, list[i].b })
                    {
                        var k = K(p);
                        if (!map.TryGetValue(k, out var l)) map[k] = l = new List<int>();
                        l.Add(i);
                    }
                }
                var used = new bool[list.Count];
                int Next(P2 end, int exclude)
                {
                    if (!map.TryGetValue(K(end), out var l)) return -1;
                    if (l.Count != 2) return -1;                // only continue through simple joints
                    foreach (int j in l) if (j != exclude && !used[j]) return j;
                    return -1;
                }
                for (int i = 0; i < list.Count; i++)
                {
                    if (used[i]) continue;
                    used[i] = true;
                    var pts = new LinkedList<P2>();
                    pts.AddLast(list[i].a); pts.AddLast(list[i].b);
                    // forward
                    int last = i; P2 end = list[i].b;
                    for (int j = Next(end, last); j >= 0; j = Next(end, last))
                    {
                        used[j] = true;
                        end = K(list[j].a) == K(end) ? list[j].b : list[j].a;
                        pts.AddLast(end); last = j;
                    }
                    // backward
                    last = i; P2 start = list[i].a;
                    for (int j = Next(start, last); j >= 0; j = Next(start, last))
                    {
                        used[j] = true;
                        start = K(list[j].a) == K(start) ? list[j].b : list[j].a;
                        pts.AddFirst(start); last = j;
                    }
                    var pl = new Polyline2 { Style = group.Key, Points = Simplify(pts.ToList(), tol * 10) };
                    if (pl.Points.Count > 2 && pl.Points[0].Dist(pl.Points[^1]) < q)
                    {
                        pl.Points.RemoveAt(pl.Points.Count - 1); pl.Closed = true;
                    }
                    if (pl.Points.Count >= 2) result.Add(pl);
                }
            }
            return result;
        }

        /// <summary>Removes collinear interior points.</summary>
        static List<P2> Simplify(List<P2> p, double tol)
        {
            if (p.Count < 3) return p;
            var r = new List<P2> { p[0] };
            for (int i = 1; i < p.Count - 1; i++)
            {
                var a = r[^1]; var b = p[i]; var c = p[i + 1];
                double cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                double l = a.Dist(c);
                if (l > 0 && Math.Abs(cross) / l < tol) continue;
                r.Add(b);
            }
            r.Add(p[^1]);
            return r;
        }

        public static List<P2> ConvexHull(List<P2> pts)
        {
            var p = pts.Distinct().OrderBy(a => a.X).ThenBy(a => a.Y).ToList();
            if (p.Count < 3) return p;
            double Cross(P2 o, P2 a, P2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
            var h = new P2[2 * p.Count]; int k = 0;
            for (int i = 0; i < p.Count; i++) { while (k >= 2 && Cross(h[k - 2], h[k - 1], p[i]) <= 1e-12) k--; h[k++] = p[i]; }
            for (int i = p.Count - 2, t = k + 1; i >= 0; i--) { while (k >= t && Cross(h[k - 2], h[k - 1], p[i]) <= 1e-12) k--; h[k++] = p[i]; }
            return h.Take(k - 1).ToList();
        }

        // ------------------------------------------------------------ occlusion grid

        sealed class TriGrid
        {
            readonly Vec3[] _s; readonly Mesh _m;
            readonly double _x0, _y0, _cw, _ch; readonly int _n;
            readonly List<int>[] _cells;
            // precomputed 2D barycentric data
            readonly double[] _det;

            public TriGrid(Vec3[] s, Mesh m, double x0, double y0, double x1, double y1, int n)
            {
                _s = s; _m = m; _n = n; _x0 = x0; _y0 = y0;
                _cw = Math.Max((x1 - x0) / n, 1e-9); _ch = Math.Max((y1 - y0) / n, 1e-9);
                _cells = new List<int>[n * n];
                int nt = m.TriangleCount;
                _det = new double[nt];
                for (int t = 0; t < nt; t++)
                {
                    var a = s[m.T[3 * t]]; var b = s[m.T[3 * t + 1]]; var c = s[m.T[3 * t + 2]];
                    double det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                    _det[t] = det;
                    if (Math.Abs(det) < 1e-14) continue;     // edge-on triangle, never occludes
                    int cx0 = Cell(Math.Min(a.X, Math.Min(b.X, c.X)), true), cx1 = Cell(Math.Max(a.X, Math.Max(b.X, c.X)), true);
                    int cy0 = Cell(Math.Min(a.Y, Math.Min(b.Y, c.Y)), false), cy1 = Cell(Math.Max(a.Y, Math.Max(b.Y, c.Y)), false);
                    for (int iy = cy0; iy <= cy1; iy++)
                        for (int ix = cx0; ix <= cx1; ix++)
                            (_cells[iy * n + ix] ??= new List<int>()).Add(t);
                }
            }

            int Cell(double v, bool x)
            {
                int i = (int)Math.Floor(x ? (v - _x0) / _cw : (v - _y0) / _ch);
                return Math.Clamp(i, 0, _n - 1);
            }

            public bool Occluded(Vec3 q, int ex1, int ex2, int ignoreGroup, double eps)
            {
                var list = _cells[Cell(q.Y, false) * _n + Cell(q.X, true)];
                if (list == null) return false;
                foreach (int t in list)
                {
                    if (t == ex1 || t == ex2) continue;
                    if (ignoreGroup >= 0 && _m.TriGroup[t] == ignoreGroup) continue;
                    var a = _s[_m.T[3 * t]]; var b = _s[_m.T[3 * t + 1]]; var c = _s[_m.T[3 * t + 2]];
                    double det = _det[t];
                    double l1 = ((b.Y - c.Y) * (q.X - c.X) + (c.X - b.X) * (q.Y - c.Y)) / det;
                    if (l1 <= 1e-9 || l1 >= 1 - 1e-9) continue;
                    double l2 = ((c.Y - a.Y) * (q.X - c.X) + (a.X - c.X) * (q.Y - c.Y)) / det;
                    if (l2 <= 1e-9 || l2 >= 1 - 1e-9) continue;
                    double l3 = 1 - l1 - l2;
                    if (l3 <= 1e-9) continue;
                    double z = l1 * a.Z + l2 * b.Z + l3 * c.Z;
                    if (z > q.Z + eps) return true;
                }
                return false;
            }
        }
    }
}
