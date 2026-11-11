using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using BoltToolApp.Data;
using BoltToolApp.Geometry;

namespace BoltToolApp.Cad
{
    public static class AssemblyEditor
    {
        /// <summary>Creates the block definition(s) and lets the user place them with a jig.</summary>
        public static void Insert(Document doc, BoltJob job)
        {
            var db = doc.Database; var ed = doc.Editor;
            job = job.Clone();
            if (job.IsAssembly && job.SeparateBlocks) job.AssemblyId = Guid.NewGuid().ToString("N").Substring(0, 12);

            using var tr = db.TransactionManager.StartTransaction();
            var roles = BlockFactory.Roles(job);
            var lay = PartBuilder.ComputeLayout(job);
            if (lay.Warning != null) ed.WriteMessage($"\nBoltTool: {lay.Warning}");
            string refRole = roles[0];
            var refBase = BlockFactory.Local(job, BlockFactory.LayoutBaseX(job, refRole, lay));

            var items = new List<(BlockReference br, Vector3d off, BoltJob job)>();
            foreach (var role in roles)
            {
                var id = BlockFactory.GetOrCreate(db, tr, job, role);
                var pj = job.Clone(); pj.Role = role;
                var off = BlockFactory.Local(job, BlockFactory.LayoutBaseX(job, role, lay)) - refBase;
                var br = new BlockReference(Point3d.Origin, id);
                br.SetDatabaseDefaults(db);
                items.Add((br, off, pj));
            }

            var jig = new MultiInsertJig(items.Select(i => (i.br, i.off)).ToList());
            if (ed.Drag(jig).Status != PromptStatus.OK) { foreach (var i in items) i.br.Dispose(); tr.Abort(); return; }
            jig.Phase = 1;
            var res = ed.Drag(jig);
            if (res.Status != PromptStatus.OK && res.Status != PromptStatus.None) jig.Angle = 0;

            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            foreach (var (br, off, pj) in items)
            {
                jig.Place(br, off);
                space.AppendEntity(br);
                tr.AddNewlyCreatedDBObject(br, true);
                XDataUtil.Write(br, pj, tr);
            }
            tr.Commit();
            Settings.SaveLastJob(job);
            ed.WriteMessage($"\nBoltTool: inserted {items.Count} block(s). Drag the ▶ grip to change length / clamp. BOLTEDIT to edit.");
        }

        /// <summary>Only creates the definitions (no insert). Returns their names.</summary>
        public static List<string> CreateDefinitions(Document doc, BoltJob job)
        {
            var db = doc.Database; var names = new List<string>();
            using var tr = db.TransactionManager.StartTransaction();
            foreach (var role in BlockFactory.Roles(job))
            {
                var id = BlockFactory.GetOrCreate(db, tr, job, role);
                names.Add(((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
            }
            tr.Commit();
            return names;
        }

        /// <summary>All references of the same linked set (or just the one).</summary>
        public static List<BlockReference> LinkedRefs(Transaction tr, BlockReference br, BoltJob job)
        {
            var list = new List<BlockReference> { br };
            if (string.IsNullOrEmpty(job?.AssemblyId)) return list;
            var owner = (BlockTableRecord)tr.GetObject(br.OwnerId, OpenMode.ForRead);
            var cls = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(BlockReference));
            foreach (ObjectId id in owner)
            {
                if (id == br.ObjectId || !id.ObjectClass.IsDerivedFrom(cls) || id.IsErased) continue;
                var other = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                var oj = XDataUtil.Read(other);
                if (oj != null && oj.AssemblyId == job.AssemblyId) list.Add(other);
            }
            return list;
        }

        /// <summary>
        /// Regenerates a reference (and its linked parts) with new parameters. The bolt head top stays fixed;
        /// every part moves by the change of its base point position. Parts that no longer exist are erased,
        /// new parts are added next to the bolt.
        /// </summary>
        public static void Apply(Transaction tr, BlockReference br, BoltJob newJob)
        {
            var db = br.Database;
            var oldJob = XDataUtil.Read(br);
            if (oldJob == null) return;
            var refs = LinkedRefs(tr, br, oldJob);
            newJob = newJob.Clone();
            newJob.AssemblyId = oldJob.AssemblyId;
            // structural switches are only allowed through BOLTEDIT on a new insert
            newJob.IsAssembly = oldJob.IsAssembly; newJob.SeparateBlocks = oldJob.SeparateBlocks;

            var newRoles = BlockFactory.Roles(newJob);
            var newLay = PartBuilder.ComputeLayout(newJob);
            var oldLay = PartBuilder.ComputeLayout(oldJob);
            BlockReference boltRef = null;
            var seen = new HashSet<string>();

            foreach (var r in refs)
            {
                var oj = XDataUtil.Read(r);
                if (oj == null) continue;
                string role = oj.Role ?? BlockFactory.RoleSingle;
                if (!r.IsWriteEnabled) r.UpgradeOpen();
                if (!newRoles.Contains(role) || !seen.Add(role)) { r.Erase(); continue; }
                var nj = newJob.Clone(); nj.Role = role;
                var oldVec = BlockFactory.Local(oj, BlockFactory.LayoutBaseX(oj, role, oldLay));
                var newVec = BlockFactory.Local(nj, BlockFactory.LayoutBaseX(nj, role, newLay));
                var btrId = BlockFactory.GetOrCreate(db, tr, nj, role);
                var xf = r.BlockTransform;
                r.BlockTableRecord = btrId;
                r.Position = r.Position + (newVec - oldVec).TransformBy(xf);
                XDataUtil.Write(r, nj, tr);
                if (role == BlockFactory.RoleBolt) boltRef = r;
            }

            // parts that were added (e.g. a lock nut) -> place relative to the bolt
            if (boltRef != null)
            {
                var space = (BlockTableRecord)tr.GetObject(boltRef.OwnerId, OpenMode.ForWrite);
                var boltVec = BlockFactory.Local(newJob, BlockFactory.LayoutBaseX(newJob, BlockFactory.RoleBolt, newLay));
                foreach (var role in newRoles.Where(x => !seen.Contains(x)))
                {
                    var nj = newJob.Clone(); nj.Role = role;
                    var id = BlockFactory.GetOrCreate(db, tr, nj, role);
                    var off = BlockFactory.Local(nj, BlockFactory.LayoutBaseX(nj, role, newLay)) - boltVec;
                    var nb = new BlockReference(boltRef.Position + off.TransformBy(boltRef.BlockTransform), id)
                    {
                        Rotation = boltRef.Rotation, ScaleFactors = boltRef.ScaleFactors, Normal = boltRef.Normal,
                        Layer = boltRef.Layer
                    };
                    space.AppendEntity(nb);
                    tr.AddNewlyCreatedDBObject(nb, true);
                    XDataUtil.Write(nb, nj, tr);
                }
            }
        }
    }

    /// <summary>Drags one or several block references (linked set) and then asks for the rotation.</summary>
    public class MultiInsertJig : DrawJig
    {
        readonly List<(BlockReference br, Vector3d off)> _items;
        public int Phase;
        public Point3d Pos;
        public double Angle;

        public MultiInsertJig(List<(BlockReference, Vector3d)> items) { _items = items; }

        public void Place(BlockReference br, Vector3d off)
        {
            var rot = Matrix3d.Rotation(Angle, Vector3d.ZAxis, Point3d.Origin);
            br.Position = Pos + off.TransformBy(rot);
            br.Rotation = Angle;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            if (Phase == 0)
            {
                var o = new JigPromptPointOptions("\nInsertion point (base point of the bolt): ")
                { UserInputControls = UserInputControls.Accept3dCoordinates };
                var r = prompts.AcquirePoint(o);
                if (r.Status != PromptStatus.OK) return SamplerStatus.Cancel;
                if (r.Value.DistanceTo(Pos) < 1e-9) return SamplerStatus.NoChange;
                Pos = r.Value; return SamplerStatus.OK;
            }
            else
            {
                var o = new JigPromptAngleOptions("\nRotation angle <0>: ")
                {
                    BasePoint = Pos, UseBasePoint = true,
                    UserInputControls = UserInputControls.NullResponseAccepted | UserInputControls.Accept3dCoordinates
                };
                var r = prompts.AcquireAngle(o);
                if (r.Status == PromptStatus.None) return SamplerStatus.OK;
                if (r.Status != PromptStatus.OK) return SamplerStatus.Cancel;
                if (Math.Abs(r.Value - Angle) < 1e-9) return SamplerStatus.NoChange;
                Angle = r.Value; return SamplerStatus.OK;
            }
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            foreach (var (br, off) in _items)
            {
                Place(br, off);
                draw.Geometry.Draw(br);
            }
            return true;
        }
    }
}
