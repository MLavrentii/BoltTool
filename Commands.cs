using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using BoltToolApp.Cad;
using BoltToolApp.Data;
using BoltToolApp.UI;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(BoltToolApp.Plugin))]
[assembly: CommandClass(typeof(BoltToolApp.Commands))]

namespace BoltToolApp
{
    public class Plugin : IExtensionApplication
    {
        public void Initialize()
        {
            try
            {
                LengthGripOverrule.Register();
                RibbonUi.Create();
                AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                    "\nBolt Tool loaded. Commands: BOLTTOOL (window), BOLTEDIT (edit a bolt block), BOLTTORQUE (torque calculator).");
            }
            catch (System.Exception ex)
            {
                AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nBolt Tool init error: " + ex.Message);
            }
        }

        public void Terminate() => LengthGripOverrule.Unregister();
    }

    public class Commands
    {
        static BoltToolWindow _window;

        /// <summary>Job + action handed over from the modeless window to a command context.</summary>
        internal static BoltJob PendingJob;
        internal static ObjectId PendingTarget = ObjectId.Null;

        internal static BoltToolWindow ShowWindow(int tab = -1)   // -1 = keep the tab from the last session
        {
            if (_window == null || !_window.IsLoaded)
            {
                _window = new BoltToolWindow();
                _window.Closed += (s, e) => _window = null;
                AcApp.ShowModelessWindow(_window);
            }
            else _window.Activate();
            _window.SelectTab(tab);
            return _window;
        }

        [CommandMethod("BTOOL", "BOLTTOOL", CommandFlags.Modal)]
        public void BoltTool() => ShowWindow();

        [CommandMethod("BTOOL", "BOLTTORQUE", CommandFlags.Modal)]
        public void BoltTorque() => ShowWindow(3);

        /// <summary>Select a Bolt Tool block and load its parameters into the window for editing.</summary>
        [CommandMethod("BTOOL", "BOLTEDIT", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void BoltEdit()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument; var ed = doc.Editor;
            ObjectId id = ObjectId.Null;
            var sel = ed.SelectImplied();
            if (sel.Status == PromptStatus.OK && sel.Value.Count == 1) id = sel.Value[0].ObjectId;
            else
            {
                var o = new PromptEntityOptions("\nSelect a Bolt Tool block: ");
                o.SetRejectMessage("\nOnly block references."); o.AddAllowedClass(typeof(BlockReference), false);
                var r = ed.GetEntity(o);
                if (r.Status != PromptStatus.OK) return;
                id = r.ObjectId;
            }
            BoltJob job;
            using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                job = XDataUtil.Read(tr.GetObject(id, OpenMode.ForRead));
                tr.Commit();
            }
            if (job == null) { ed.WriteMessage("\nThis block was not created by the Bolt Tool."); return; }
            ShowWindow().LoadForEdit(job, id);
        }

        // ---------- internal commands started from the window (command context = safe document access)

        [CommandMethod("BTOOL", "BT_BOLTINSERT", CommandFlags.Modal | CommandFlags.NoHistory)]
        public void InsertPending()
        {
            var job = PendingJob; PendingJob = null;
            if (job == null) return;
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            try { AssemblyEditor.Insert(doc, job); }
            catch (System.Exception ex) { doc.Editor.WriteMessage($"\nBoltTool error: {ex.Message}"); }
        }

        [CommandMethod("BTOOL", "BT_BOLTMAKE", CommandFlags.Modal | CommandFlags.NoHistory)]
        public void MakePending()
        {
            var job = PendingJob; PendingJob = null;
            if (job == null) return;
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            try
            {
                var names = AssemblyEditor.CreateDefinitions(doc, job);
                doc.Editor.WriteMessage($"\nBoltTool: block definition(s) ready: {string.Join(", ", names)}");
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage($"\nBoltTool error: {ex.Message}"); }
        }

        [CommandMethod("BTOOL", "BT_BOLTUPDATE", CommandFlags.Modal | CommandFlags.NoHistory)]
        public void UpdatePending()
        {
            var job = PendingJob; var id = PendingTarget;
            PendingJob = null; PendingTarget = ObjectId.Null;
            if (job == null || id.IsNull || id.IsErased) return;
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (id.Database != doc.Database) { doc.Editor.WriteMessage("\nBoltTool: the edited block is in another drawing."); return; }
            try
            {
                using var tr = doc.Database.TransactionManager.StartTransaction();
                var br = (BlockReference)tr.GetObject(id, OpenMode.ForWrite);
                AssemblyEditor.Apply(tr, br, job);
                tr.Commit();
                doc.Editor.WriteMessage("\nBoltTool: block updated.");
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage($"\nBoltTool error: {ex.Message}"); }
        }

        internal static void Run(string command, BoltJob job, ObjectId target = default)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            PendingJob = job.Clone();
            PendingTarget = target;
            doc.SendStringToExecute($"_{command} ", true, false, false);
        }
    }
}
