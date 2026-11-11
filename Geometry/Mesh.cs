using System;
using System.Collections.Generic;

namespace BoltToolApp.Geometry
{
    public readonly struct Vec3
    {
        public readonly double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);
        public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vec3 Cross(Vec3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public Vec3 Normalized() { double l = Length; return l < 1e-15 ? this : this * (1.0 / l); }
        public override string ToString() => $"({X:0.###},{Y:0.###},{Z:0.###})";
    }

    /// <summary>Row-major 3x3 rotation.</summary>
    public readonly struct Mat3
    {
        public readonly double M00, M01, M02, M10, M11, M12, M20, M21, M22;
        public Mat3(double a, double b, double c, double d, double e, double f, double g, double h, double i)
        { M00 = a; M01 = b; M02 = c; M10 = d; M11 = e; M12 = f; M20 = g; M21 = h; M22 = i; }
        public static Mat3 Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);
        public Vec3 Apply(Vec3 v) => new(M00 * v.X + M01 * v.Y + M02 * v.Z,
                                          M10 * v.X + M11 * v.Y + M12 * v.Z,
                                          M20 * v.X + M21 * v.Y + M22 * v.Z);
        public static Mat3 operator *(Mat3 a, Mat3 b) => new(
            a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);
        public Mat3 Transposed() => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);
        public static Mat3 RotX(double r) { double c = Math.Cos(r), s = Math.Sin(r); return new(1, 0, 0, 0, c, -s, 0, s, c); }
        public static Mat3 RotY(double r) { double c = Math.Cos(r), s = Math.Sin(r); return new(c, 0, s, 0, 1, 0, -s, 0, c); }

        /// <summary>
        /// Model frame: part axis = +X (head at x=0, shank toward +X). Screen frame: x right, y up, z toward viewer.
        /// Azimuth 0 / elevation 0 = side view (axis horizontal, head on the left).
        /// Azimuth +90 = looking at the head, -90 = looking at the thread end.
        /// </summary>
        public static Mat3 View(double azimuthDeg, double elevationDeg, double spinDeg)
        {
            const double d2r = Math.PI / 180.0;
            return RotX(elevationDeg * d2r) * RotY(azimuthDeg * d2r) * RotX(spinDeg * d2r);
        }
    }

    public enum LineStyle { Visible, Thin, Hidden }

    /// <summary>A conventional-representation curve (thread lines) that is not part of the solid surface.</summary>
    public class Overlay
    {
        public List<Vec3> Points = new();
        public int IgnoreGroup = -1;        // triangles of this group never hide it
        public Vec3? FacingNormal;          // drawn only when this normal points to the viewer
        public LineStyle Style = LineStyle.Thin;
        public bool IsThreadOverlay;        // generated per view, removed after rendering
    }

    /// <summary>Thread description; the renderer turns it into view-dependent overlays.</summary>
    public class ThreadInfo
    {
        public double X0, X1;               // threaded range along the axis
        public double RMajor, RMinor;
        public bool Internal;               // nut thread
        public bool ShowTermination;        // line where the thread ends (partial thread bolts)
        public int Group;
    }

    public class Mesh
    {
        public readonly List<Vec3> V = new();
        public readonly List<int> T = new();          // 3 indices per triangle
        public readonly List<int> TriGroup = new();   // group per triangle (wipeout & occlusion groups)
        public readonly List<ThreadInfo> Threads = new();
        public readonly List<Overlay> Overlays = new();
        public int GroupCount;
        public readonly List<int> GroupOwner = new();   // group -> index of the layout part (filled by BuildLayout)

        public int TriangleCount => TriGroup.Count;

        readonly Dictionary<(long, long, long), int> _weld = new();
        const double WeldTol = 1e-6;

        public int AddVertex(Vec3 p)
        {
            var key = ((long)Math.Round(p.X / WeldTol), (long)Math.Round(p.Y / WeldTol), (long)Math.Round(p.Z / WeldTol));
            if (_weld.TryGetValue(key, out int idx)) return idx;
            V.Add(p); _weld[key] = V.Count - 1; return V.Count - 1;
        }

        public void AddTri(int a, int b, int c, int group)
        {
            if (a == b || b == c || a == c) return;
            var n = (V[b] - V[a]).Cross(V[c] - V[a]);
            if (n.Length < 1e-12) return;
            T.Add(a); T.Add(b); T.Add(c); TriGroup.Add(group);
        }

        public void AddQuad(int a, int b, int c, int d, int group)
        {
            AddTri(a, b, c, group);
            AddTri(a, c, d, group);
        }

        public int NewGroup() => GroupCount++;

        /// <summary>Appends another mesh translated along the axis; returns the group offset used.</summary>
        public int Append(Mesh other, double dx)
        {
            int gOff = GroupCount;
            var map = new int[other.V.Count];
            for (int i = 0; i < other.V.Count; i++)
                map[i] = AddVertex(new Vec3(other.V[i].X + dx, other.V[i].Y, other.V[i].Z));
            for (int t = 0; t < other.TriangleCount; t++)
                AddTri(map[other.T[3 * t]], map[other.T[3 * t + 1]], map[other.T[3 * t + 2]], other.TriGroup[t] + gOff);
            foreach (var th in other.Threads)
                Threads.Add(new ThreadInfo
                {
                    X0 = th.X0 + dx, X1 = th.X1 + dx, RMajor = th.RMajor, RMinor = th.RMinor,
                    Internal = th.Internal, ShowTermination = th.ShowTermination, Group = th.Group + gOff
                });
            foreach (var o in other.Overlays)
            {
                var no = new Overlay
                {
                    IgnoreGroup = o.IgnoreGroup < 0 ? -1 : o.IgnoreGroup + gOff,
                    FacingNormal = o.FacingNormal, Style = o.Style
                };
                foreach (var p in o.Points) no.Points.Add(new Vec3(p.X + dx, p.Y, p.Z));
                Overlays.Add(no);
            }
            GroupCount += other.GroupCount;
            return gOff;
        }

        /// <summary>
        /// Generalised revolve ("polar loft"): for every angle θ the profile function returns the same number
        /// of (x, r) points. Vertices are (x, r·cosθ, r·sinθ). With a θ-dependent radius this builds hexagons,
        /// chamfered hex heads, sockets etc. as one closed, consistently oriented surface.
        /// </summary>
        public void Loft(Func<double, IList<(double x, double r)>> profile, int segments, bool profileClosed,
                         int group, double angleStart = 0, double angleSweep = 2 * Math.PI)
        {
            bool full = Math.Abs(angleSweep - 2 * Math.PI) < 1e-9;
            int nA = full ? segments : segments + 1;
            var rings = new List<int[]>(nA);
            int nP = -1;
            for (int j = 0; j < nA; j++)
            {
                double th = angleStart + angleSweep * j / segments;
                var prof = profile(th);
                if (nP < 0) nP = prof.Count;
                double c = Math.Cos(th), s = Math.Sin(th);
                var ring = new int[nP];
                for (int i = 0; i < nP; i++) ring[i] = AddVertex(new Vec3(prof[i].x, prof[i].r * c, prof[i].r * s));
                rings.Add(ring);
            }
            int segCount = full ? nA : nA - 1;
            int pSeg = profileClosed ? nP : nP - 1;
            for (int j = 0; j < segCount; j++)
            {
                var a = rings[j]; var b = rings[(j + 1) % nA];
                for (int i = 0; i < pSeg; i++)
                {
                    int i2 = (i + 1) % nP;
                    AddQuad(a[i], a[i2], b[i2], b[i], group);
                }
            }
            if (!full && profileClosed)
            {
                // end caps (profile must be convex, e.g. the rectangle of a spring washer)
                var first = rings[0]; var last = rings[nA - 1];
                for (int i = 1; i < nP - 1; i++)
                {
                    AddTri(first[0], first[i + 1], first[i], group);
                    AddTri(last[0], last[i], last[i + 1], group);
                }
            }
        }
    }
}
