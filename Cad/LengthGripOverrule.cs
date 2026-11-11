using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;
using WS.BoltTool.Data;
using WS.BoltTool.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace WS.BoltTool.Cad
{
    /// <summary>
    /// "Dynamic block" behaviour without authoring dynamic blocks: every Bolt Tool block reference gets
    /// an extra grip at the thread end (length) and/or at the nut (clamp length). Dragging it regenerates
    /// the block with the new length – snapped to standard lengths – and moves linked nuts/washers.
    /// </summary>
    public class LengthGripOverrule : GripOverrule
    {
        public static LengthGripOverrule Instance { get; private set; }

        class BoltGrip : GripData
        {
            public string Kind;          // "len" or "clamp"
            public Vector3d AxisLocal;   // block-local vector of +1 mm along the axis
            public override bool ViewportDraw(ViewportDraw worldDraw, ObjectId entityId, GripData.DrawType type,
                                              Point3d? imageGripPoint, int gripSizeInPixels)
            {
                // draw a small triangle pointing along the axis so it looks like a stretch grip
                var p = imageGripPoint ?? GripPoint;
                var vd = worldDraw.Viewport;
                var pix = vd.GetNumPixelsInUnitSquare(p);
                double s = gripSizeInPixels / Math.Max(pix.X, 1e-9) * 1.3;
                var a = AxisLocal.Length > 1e-12 ? AxisLocal.GetNormal() : Vector3d.XAxis;
                var n = a.GetPerpendicularVector();
                var pts = new Point3dCollection { p + a * s, p - a * s * 0.6 + n * s * 0.8, p - a * s * 0.6 - n * s * 0.8 };
                worldDraw.SubEntityTraits.Color = (short)(type == GripData.DrawType.HotGrip ? 1 : type == GripData.DrawType.HoverGrip ? 2 : 150);
                worldDraw.SubEntityTraits.FillType = Autodesk.AutoCAD.GraphicsInterface.FillType.FillAlways;
                worldDraw.Geometry.Polygon(pts);
                return true;
            }
        }

        static readonly Dictionary<ObjectId, (string kind, double delta)> Pending = new();

        public static void Register()
        {
            if (Instance != null) return;
            Instance = new LengthGripOverrule();
            Instance.SetXDataFilter(XDataUtil.App);
            AddOverrule(RXObject.GetClass(typeof(BlockReference)), Instance, false);
            Overruling = true;
            AcApp.DocumentManager.DocumentCreated += (s, e) => Hook(e.Document);
            foreach (Document d in AcApp.DocumentManager) Hook(d);
        }

        public static void Unregister()
        {
            if (Instance == null) return;
            RemoveOverrule(RXObject.GetClass(typeof(BlockReference)), Instance);
            Instance = null;
        }

        static void Hook(Document d)
        {
            if (d == null) return;
            d.CommandEnded += (s, e) => Process(d);
            d.CommandCancelled += (s, e) => Pending.Clear();
            d.CommandFailed += (s, e) => Pending.Clear();
        }

        /// <summary>Grip definitions: (kind, axial position in assembly coordinates).</summary>
        internal static IEnumerable<(string kind, double x)> GripDefs(BoltJob job)
        {
            string role = job.Role ?? BlockFactory.RoleSingle;
            var lay = PartBuilder.ComputeLayout(job);
            var bolt = lay.Parts[0];
            if (role == BlockFactory.RoleSingle)
            {
                if (PartKindInfo.HasLength(job.Kind)) yield return ("len", bolt.Thickness);
                yield break;
            }
            if (role == BlockFactory.RoleBolt || role == BlockFactory.RoleAssembly)
                yield return ("len", bolt.X + bolt.Thickness);
            var nutSide = lay.Parts.Where(p => p.Role is "nutwasher" or "nut" or "jamnut" || (p.Role == "spring" && job.Spring == SpringPosition.UnderNut)).ToList();
            if (nutSide.Count == 0) yield break;
            if (role == BlockFactory.RoleAssembly)
                yield return ("clamp", nutSide.Max(p => p.X + p.Thickness));
            else if (role == "nut" || (role == "nutwasher" && !nutSide.Any(p => p.Role == "nut")))
            {
                var p = nutSide.First(x => x.Role == role);
                yield return ("clamp", p.X + p.Thickness);
            }
        }

        public override void GetGripPoints(Entity entity, GripDataCollection grips, double curViewUnitSize, int gripSize,
                                           Vector3d curViewDir, GetGripPointsFlags bitFlags)
        {
            base.GetGripPoints(entity, grips, curViewUnitSize, gripSize, curViewDir, bitFlags);
            try
            {
                if (entity is not BlockReference br) return;
                var job = XDataUtil.Read(br);
                if (job == null) return;
                string role = job.Role ?? BlockFactory.RoleSingle;
                var xf = br.BlockTransform;
                foreach (var (kind, x) in GripDefs(job))
                {
                    var axis = BlockFactory.Local(job, x + 1) - BlockFactory.Local(job, x);
                    if (axis.Length < 0.05 * job.Scale) continue;   // axis points at the viewer (end view)
                    var g = new BoltGrip
                    {
                        Kind = kind,
                        AxisLocal = axis.TransformBy(xf),
                        GripPoint = BlockFactory.LocalPoint(job, role, x).TransformBy(xf)
                    };
                    grips.Add(g);
                }
            }
            catch { /* never break AutoCAD's grip display */ }
        }

        public override void MoveGripPointsAt(Entity entity, GripDataCollection grips, Vector3d offset, MoveGripPointsFlags bitFlags)
        {
            var others = new GripDataCollection();
            foreach (GripData g in grips)
            {
                if (g is BoltGrip bg)
                {
                    try
                    {
                        if (entity is BlockReference br && !br.ObjectId.IsNull && br.Database != null)
                        {
                            double a2 = bg.AxisLocal.DotProduct(bg.AxisLocal);
                            if (a2 > 1e-12) Pending[br.ObjectId] = (bg.Kind, offset.DotProduct(bg.AxisLocal) / a2);
                        }
                    }
                    catch { }
                }
                else others.Add(g);
            }
            if (others.Count > 0) base.MoveGripPointsAt(entity, others, offset, bitFlags);
        }

        static double SnapLength(BoltJob job, double target)
        {
            var row = job.Row;
            double min = Math.Max(row.D * 0.5, 4);
            target = Math.Max(min, target);
            if (!job.SnapLengthToStandard) return Math.Round(target);
            return job.Table.Lengths.OrderBy(l => Math.Abs(l - target)).First();
        }

        static void Process(Document doc)
        {
            if (Pending.Count == 0) return;
            var work = Pending.ToList();
            Pending.Clear();
            try
            {
                using var lk = doc.LockDocument();
                using var tr = doc.Database.TransactionManager.StartTransaction();
                foreach (var (id, (kind, delta)) in work)
                {
                    if (id.IsErased || id.Database != doc.Database) continue;
                    var br = (BlockReference)tr.GetObject(id, OpenMode.ForWrite);
                    var job = XDataUtil.Read(br);
                    if (job == null) continue;
                    var nj = job.Clone();
                    var lay = PartBuilder.ComputeLayout(job);
                    if (kind == "len")
                    {
                        double oldLen = job.IsAssembly ? lay.BoltLength : job.Length;
                        nj.Length = SnapLength(job, oldLen + delta);
                        if (Math.Abs(nj.Length - oldLen) < 1e-9) continue;
                        nj.AutoLength = false;
                        if (job.IsAssembly && job.StickToBoltEnd)
                            nj.ClampLength = Math.Max(0, job.ClampLength + (nj.Length - oldLen));
                    }
                    else
                    {
                        nj.ClampLength = Math.Max(0, Math.Round((job.ClampLength + delta) * 2) / 2);
                        if (Math.Abs(nj.ClampLength - job.ClampLength) < 1e-9) continue;
                        if (!job.AutoLength) nj.Length = lay.BoltLength;
                    }
                    AssemblyEditor.Apply(tr, br, nj);
                    var l2 = PartBuilder.ComputeLayout(nj);
                    doc.Editor.WriteMessage($"\nBoltTool: {job.Size} length {l2.BoltLength:0.#} mm" +
                        (nj.IsAssembly ? $", clamp {nj.ClampLength:0.#} mm" : "") + (l2.Warning != null ? $"  ⚠ {l2.Warning}" : ""));
                }
                tr.Commit();
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\nBoltTool: could not update ({ex.Message}).");
            }
        }
    }
}
