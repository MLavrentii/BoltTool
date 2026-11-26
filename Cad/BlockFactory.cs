using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BoltToolApp.Data;
using BoltToolApp.Geometry;
using LineStyle = BoltToolApp.Geometry.LineStyle;
using Mesh = BoltToolApp.Geometry.Mesh;
using Layout = BoltToolApp.Geometry.Layout;

namespace BoltToolApp.Cad
{
    /// <summary>Creates (or reuses) block definitions for a job and computes the positions of linked parts.</summary>
    public static class BlockFactory
    {
        public const string RoleSingle = "single";
        public const string RoleAssembly = "assembly";
        public const string RoleBolt = "bolt";

        /// <summary>Block roles that a job produces (one per block reference).</summary>
        public static List<string> Roles(BoltJob job)
        {
            if (!job.IsAssembly) return new List<string> { RoleSingle };
            if (!job.SeparateBlocks) return new List<string> { RoleAssembly };
            return PartBuilder.ComputeLayout(job).Parts.Select(p => p.Role).ToList();
        }

        /// <summary>Axial position (assembly coordinates, bolt head top = 0) of the base point of a role.</summary>
        public static double LayoutBaseX(BoltJob job, string role, Layout lay = null)
        {
            var row = job.Row;
            if (role == RoleSingle) return PartBuilder.BaseX(job.Kind, row, job.Length, job.BasePoint);
            lay ??= PartBuilder.ComputeLayout(job);
            if (role == RoleAssembly || role == RoleBolt)
                return job.BasePoint switch
                {
                    BasePoint.UnderHead => lay.UnderHead,                              // underside of the head
                    BasePoint.ClampHead => lay.ClampStart,                             // face that presses on the clamped parts (head side)
                    BasePoint.ClampNut => lay.HasNutSide ? lay.ClampEnd : lay.BoltEnd, // face of the nut-side elements toward the head
                    BasePoint.ThreadEnd => lay.BoltEnd,
                    _ => 0                                                             // middle of the head top
                };
            var p = lay.Parts.FirstOrDefault(x => x.Role == role);
            return p?.X ?? 0;
        }

        /// <summary>Block-local vector of the axial position x (relative to x = 0) incl. view projection and scale.</summary>
        public static Vector3d Local(BoltJob job, double x)
        {
            if (job.Output != OutputMode.View2D) return new Vector3d(x * job.Scale, 0, 0);
            var R = Mat3.View(job.Azimuth, job.Elevation, job.Spin);
            var s = R.Apply(new Vec3(x, 0, 0));
            return new Vector3d(s.X * job.Scale, s.Y * job.Scale, 0);
        }

        /// <summary>Point in the block of the axial position x (block origin = base point of the role).</summary>
        public static Point3d LocalPoint(BoltJob job, string role, double x) =>
            Point3d.Origin + (Local(job, x) - Local(job, LayoutBaseX(job, role)));

        static string Hash(string s)
        {
            var h = SHA1.HashData(Encoding.UTF8.GetBytes(s));
            return Convert.ToHexString(h, 0, 4);
        }

        static string Fmt(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        // Full-precision values for the geometry cache key: two dimensions such as
        // 2.000 and 2.004 mm must NOT reuse the same existing AutoCAD block.
        static string Exact(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Returns the id of the block definition for the role, creating it when needed.</summary>
        public static ObjectId GetOrCreate(Database db, Transaction tr, BoltJob job, string role)
        {
            var row = job.Row;
            var lay = PartBuilder.ComputeLayout(job);
            List<PlacedPart> parts;
            if (role == RoleSingle || role == RoleAssembly) parts = lay.Parts;
            else parts = lay.Parts.Where(p => p.Role == role).Take(1).ToList();
            if (parts.Count == 0) throw new InvalidOperationException("Unknown part role " + role);
            double baseX = LayoutBaseX(job, role, lay);

            // ---- unique, readable name ----
            var main = parts[0];
            string std = job.Standard.ToString();
            string sizeTxt = PartKindInfo.HasLength(main.Kind) ? $"{job.Size}x{Fmt(main.Length)}" : job.Size;
            string prefix = role == RoleAssembly
                ? $"BT_{std}_ASM_{job.Size}x{Fmt(lay.BoltLength)}_C{Fmt(job.ClampLength)}"
                : $"BT_{std}_{PartKindInfo.Code(main.Kind)}_{sizeTxt}";
            // Include the complete size table row: users can edit ISO/JIS dimensions in JSON.
            // Hash from full precision rather than display rounding to avoid accidental reuse
            // of a block whose stored geometry differs from the newly requested dimensions.
            string geomKey = string.Join("|", "v5", std, job.Size, role,
                string.Join(";", parts.Select(p => $"{p.Kind}:{Exact(p.X - baseX)}:{Exact(p.Length)}:{Exact(p.Thickness)}:{Exact(p.OuterR)}:{p.Item?.Side}")),
                job.Thread, job.ThreadStartLine, Exact(job.Azimuth), Exact(job.Elevation), Exact(job.Spin), job.HiddenLines, job.Output,
                Exact(job.Scale), job.Wipeout, job.WipeoutLayer, JsonSerializer.Serialize(row));
            string hash = Hash(geomKey);
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            string name;
            if (job.NameStyle != BlockNameStyle.Full)
            {
                // 12x40_BWSN: size, then the parts from the head side to the bolt end.
                // Same name but different geometry -> _2, _3 … (the key is kept in the block comment).
                string baseName = SimpleName(job, parts, lay, role);
                if (job.NameStyle == BlockNameStyle.SizeOnly) baseName = SizeOnlyName(baseName);
                if (job.NameStyle == BlockNameStyle.Custom && !string.IsNullOrWhiteSpace(job.CustomName))
                {
                    // exactly the typed name (no view suffix); separate blocks: <name>_<simple name of the part>
                    string custom = CustomBlockName(job.CustomName);
                    baseName = role == RoleAssembly || role == RoleSingle ? custom : $"{custom}_{baseName}";
                }
                if (job.NameStyle == BlockNameStyle.SetName && SetNameValid(job, lay))
                {
                    string set = Clean(job.SetName.Trim());
                    string view = job.ViewCode();
                    if (view != "SIDE") set += "_" + view;
                    baseName = role == RoleAssembly || role == RoleSingle ? set : $"{set}_{baseName}";
                }
                string tag = $"[BT:{hash}]";
                name = null;
                for (int n = 1; n < 1000 && name == null; n++)
                {
                    string cand = n == 1 ? baseName : $"{baseName}_{n}";
                    if (!bt.Has(cand)) { name = cand; break; }
                    var old = (BlockTableRecord)tr.GetObject(bt[cand], OpenMode.ForRead);
                    if (old.Comments != null && old.Comments.Contains(tag)) return old.ObjectId;
                }
                name ??= $"{baseName}_{hash}";
            }
            else
            {
                name = $"{prefix}_{job.ViewCode()}_{hash}".Replace('.', '_');
                if (bt.Has(name)) return bt[name];
            }

            var btr = new BlockTableRecord { Name = name, Origin = Point3d.Origin };
            string note = string.IsNullOrWhiteSpace(job.SetComment) || !SetNameValid(job, lay) ? "" : $" – {job.SetComment.Trim()}";
            btr.Comments = $"{PartKindInfo.Designation(main.Kind, job.Standard)} {sizeTxt}{note} – Bolt Tool [BT:{hash}]";
            bt.UpgradeOpen();
            var id = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);

            // geometry in assembly coordinates
            var mesh = new Mesh();
            foreach (var p in parts) mesh.Append(PartBuilder.BuildPart(p.Kind, row, p.Length, job.Thread, p.Item, job.IsAssembly), p.X);

            if (job.Output == OutputMode.View2D) Write2D(db, tr, btr, job, mesh, baseX);
            else Write3D(db, tr, btr, job, parts, row, baseX);
            return id;
        }

        /// <summary>Simple block name: "12x40_BWSN", "12_N", "12x40_BWlS_ChNH_ISO".
        /// Size without the M, "x length" when there is a bolt, then the letters of all parts
        /// from the head side to the bolt end, then the view when it is not the side view.</summary>
        public static string SimpleName(BoltJob job, List<PlacedPart> parts, Layout lay, string role)
        {
            string d = (job.Size ?? "").TrimStart('M', 'm');
            var bolt = parts.FirstOrDefault(p => p.Item == null && PartKindInfo.HasLength(p.Kind));
            double len = role == RoleAssembly ? lay.BoltLength : bolt?.Length ?? 0;
            string size = bolt != null && len > 0 ? $"{d}x{Fmt(len)}" : d;

            var ordered = parts.Where(p => p.Item == null).Concat(parts.Where(p => p.Item != null).OrderBy(p => p.X));
            string letters = string.Concat(ordered.Select(p => PartKindInfo.ShortCode(p.Kind)));

            string view = job.ViewCode();
            return Clean(view == "SIDE" ? $"{size}_{letters}" : $"{size}_{letters}_{view}");
        }

        /// <summary>Typed name made safe for AutoCAD (forbidden characters -> _, max. 200 characters).</summary>
        public static string CustomBlockName(string typed)
        {
            string n = Clean((typed ?? "").Trim());
            return n.Length > 200 ? n.Substring(0, 200) : n;
        }

        /// <summary>"12x40_BWSN" -> "12x40", "12x40_BWSN_ISO" -> "12x40_ISO". Names without a length ("12_N") stay,
        /// otherwise a nut and a washer of the same size would both be called "12".</summary>
        public static string SizeOnlyName(string simple)
        {
            var parts = simple.Split('_');
            if (parts.Length < 2 || !parts[0].Contains('x')) return simple;
            var keep = new System.Collections.Generic.List<string> { parts[0] };
            keep.AddRange(parts.Skip(2));          // view suffix
            return string.Join("_", keep);
        }

        /// <summary>Simple code of the whole job without view, e.g. "12x40_BWSN" (used as default set name).</summary>
        public static string SimpleCode(BoltJob job)
        {
            var lay = PartBuilder.ComputeLayout(job);
            var j = job.Clone(); j.Azimuth = 0; j.Elevation = 0; j.Output = OutputMode.View2D;
            return SimpleName(j, lay.Parts, lay, job.IsAssembly ? RoleAssembly : RoleSingle);
        }

        /// <summary>The set name is only used while the parts still match the saved set.</summary>
        static bool SetNameValid(BoltJob job, Layout lay) =>
            !string.IsNullOrWhiteSpace(job.SetName) && (string.IsNullOrEmpty(job.SetCode) || job.SetCode == SimpleCode(job));

        /// <summary>Removes characters AutoCAD does not allow in block names.</summary>
        public static string Clean(string name)
        {
            foreach (char c in "<>/\\\":;?*|,=`") name = name.Replace(c, '_');
            return name.Trim();
        }

        static void Style(Entity e, LineStyle st, Database db, Transaction tr)
        {
            e.Layer = "0";
            e.Color = Color.FromColorIndex(ColorMethod.ByBlock, 0);
            e.LineWeight = LineWeight.ByBlock;
            e.Linetype = st == LineStyle.Hidden && LayerUtil.EnsureLinetype(db, tr, "HIDDEN") ? "HIDDEN" : "ByBlock";
        }

        static void Write2D(Database db, Transaction tr, BlockTableRecord btr, BoltJob job, Mesh mesh, double baseX)
        {
            var dwg = HiddenLineRenderer.Render(mesh, job.Azimuth, job.Elevation, job.Spin, job.HiddenLines, true, job.ThreadStartLine);
            var o = dwg.Project(new Vec3(baseX, 0, 0));
            double sc = job.Scale;
            Point2d P(P2 p) => new((p.X - o.X) * sc, (p.Y - o.Y) * sc);

            // wipeouts first, so they sit below the lines of this block
            if (job.Wipeout && dwg.Wipeouts.Count > 0)
            {
                string layer = LayerUtil.EnsureLayer(db, tr, string.IsNullOrWhiteSpace(job.WipeoutLayer) ? "01_WIPEOUT" : job.WipeoutLayer);
                LayerUtil.EnsureWipeoutSupport();
                foreach (var kv in dwg.Wipeouts.OrderBy(k => k.Key))
                {
                    var pc = new Point2dCollection();
                    foreach (var p in kv.Value) pc.Add(P(p));
                    pc.Add(P(kv.Value[0]));
                    try
                    {
                        var w = new Wipeout();
                        w.SetDatabaseDefaults(db);
                        w.SetFrom(pc, Vector3d.ZAxis);
                        w.Layer = layer;
                        w.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                        btr.AppendEntity(w);
                        tr.AddNewlyCreatedDBObject(w, true);
                    }
                    catch (Exception ex)
                    {
                        Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?
                            .Editor.WriteMessage($"\nBoltTool: wipeout skipped ({ex.Message}).");
                        break;
                    }
                }
            }

            // visible lines last (thin thread lines after the outlines)
            foreach (var pl in dwg.Lines.OrderBy(l => l.Style == LineStyle.Hidden ? 0 : l.Style == LineStyle.Visible ? 1 : 2))
            {
                var poly = new Polyline();
                poly.SetDatabaseDefaults(db);
                for (int i = 0; i < pl.Points.Count; i++) poly.AddVertexAt(i, P(pl.Points[i]), 0, 0, 0);
                poly.Closed = pl.Closed;
                Style(poly, pl.Style, db, tr);
                btr.AppendEntity(poly);
                tr.AddNewlyCreatedDBObject(poly, true);
            }
        }

        static void Write3D(Database db, Transaction tr, BlockTableRecord btr, BoltJob job, List<PlacedPart> parts, SizeRow row, double baseX)
        {
            double sc = job.Scale;
            foreach (var p in parts)
            {
                var m = PartBuilder.BuildPart(p.Kind, row, p.Length, job.Thread == ThreadStyle.Detailed ? ThreadStyle.Detailed : ThreadStyle.None, p.Item, job.IsAssembly);
                var verts = new Point3dCollection();
                foreach (var v in m.V) verts.Add(new Point3d((v.X + p.X - baseX) * sc, v.Y * sc, v.Z * sc));
                var faces = new Int32Collection();
                for (int t = 0; t < m.TriangleCount; t++)
                {
                    faces.Add(3); faces.Add(m.T[3 * t]); faces.Add(m.T[3 * t + 1]); faces.Add(m.T[3 * t + 2]);
                }
                var sub = new SubDMesh();
                sub.SetDatabaseDefaults(db);
                sub.SetSubDMesh(verts, faces, 0);
                Entity ent = sub;
                if (job.Output == OutputMode.Solid3D)
                {
                    try
                    {
                        var sol = sub.ConvertToSolid(false, true);
                        if (sol != null) { ent = sol; sub.Dispose(); }
                    }
                    catch { ent = sub; }   // e.g. helical thread is not watertight -> keep the mesh
                }
                Style(ent, LineStyle.Visible, db, tr);
                btr.AppendEntity(ent);
                tr.AddNewlyCreatedDBObject(ent, true);
            }
        }
    }
}
