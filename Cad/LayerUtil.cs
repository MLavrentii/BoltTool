using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using BoltToolApp.Data;

namespace BoltToolApp.Cad
{
    public static class LayerUtil
    {
        /// <summary>Creates the layer if missing (no-plot is NOT set: a wipeout must plot to mask).</summary>
        public static string EnsureLayer(Database db, Transaction tr, string name)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return name;
            try { SymbolUtilityServices.ValidateSymbolName(name, false); } catch { name = "01_WIPEOUT"; if (lt.Has(name)) return name; }
            var rec = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, 7) };
            lt.UpgradeOpen();
            lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            return name;
        }

        public static List<string> LayerNames(Database db)
        {
            var list = new List<string>();
            using var tr = db.TransactionManager.StartOpenCloseTransaction();
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!l.IsDependent) list.Add(l.Name);
            }
            tr.Commit();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        public static bool EnsureLinetype(Database db, Transaction tr, string name)
        {
            var lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (lt.Has(name)) return true;
            foreach (var f in new[] { "acadiso.lin", "acad.lin" })
            {
                try { db.LoadLineTypeFile(name, f); return true; } catch { }
            }
            return false;
        }

        static bool _wipeoutLoaded;
        /// <summary>Wipeout objects live in an object enabler that is demand-loaded by the WIPEOUT command.</summary>
        public static void EnsureWipeoutSupport()
        {
            if (_wipeoutLoaded) return;
            _wipeoutLoaded = true;
            try { SystemObjects.DynamicLinker.LoadModule("AcWipeoutObj.dbx", false, false); } catch { }
        }

        public static void EnsureRegApp(Database db, Transaction tr)
        {
            var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (rat.Has(XDataUtil.App)) return;
            rat.UpgradeOpen();
            var r = new RegAppTableRecord { Name = XDataUtil.App };
            rat.Add(r);
            tr.AddNewlyCreatedDBObject(r, true);
        }
    }

    /// <summary>Stores the job JSON on block references so they can be edited / stretched later.</summary>
    public static class XDataUtil
    {
        public const string App = "BT_BOLTTOOL";

        public static void Write(DBObject obj, BoltJob job, Transaction tr)
        {
            LayerUtil.EnsureRegApp(obj.Database, tr);
            string json = job.ToJson();
            var rb = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, App));
            for (int i = 0; i < json.Length; i += 240)
                rb.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, json.Substring(i, Math.Min(240, json.Length - i))));
            if (!obj.IsWriteEnabled) obj.UpgradeOpen();
            obj.XData = rb;
        }

        public static BoltJob Read(DBObject obj)
        {
            using var rb = obj.GetXDataForApplication(App);
            if (rb == null) return null;
            var sb = new StringBuilder();
            foreach (TypedValue tv in rb)
                if (tv.TypeCode == (int)DxfCode.ExtendedDataAsciiString) sb.Append((string)tv.Value);
            return BoltJob.FromJson(sb.ToString());
        }
    }
}
