using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ObjectId = Autodesk.AutoCAD.DatabaseServices.ObjectId;
using BoltToolApp.Cad;
using BoltToolApp.Data;
using BoltToolApp.Geometry;
using BoltToolApp.Torque;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BoltToolApp.UI
{
    public partial class BoltToolWindow : Window
    {
        BoltJob _job;
        bool _loading;
        ObjectId _editId = ObjectId.Null;
        readonly DispatcherTimer _hq = new() { Interval = TimeSpan.FromMilliseconds(220) };
        Mesh _mesh; string _meshKey;
        string _snap; bool _restoring;
        double _rawAz, _rawEl; bool _rawValid;                  // unsnapped angles while orbiting
        readonly HashSet<string> _sel = new();                  // selected part roles (Ctrl+click in the preview)
        Layout _lay; bool _downAdded, _partDragging; double _dragAccum; Dictionary<string, double> _dragStart;
        double _stepMin, _stepMax;                              // how far the selection can move before it touches something
        readonly Stack<string> _undo = new(), _redo = new();

        public BoltToolWindow()
        {
            InitializeComponent();
            _job = Settings.LoadLastJob();
            _hq.Tick += (s, e) => { _hq.Stop(); Render(false, true); };
            Preview.RotateRequested += OnRotate;
            PreviewKeyDown += OnKeys;
            Preview.PartDown += OnPartDown;
            Preview.PartDrag += OnPartDrag;
            Preview.PartUp += OnPartUp;
            Preview.DistanceClicked += OnDistanceClicked;
            Loc.Jp = Settings.Language == "ja";
            _loading = true; LangBox.SelectedIndex = Loc.Jp ? 1 : 0; _loading = false;
            Loc.Apply(this); Loc.ApplyTips(this);
            FillStatic();
            LoadJob(_job);
            InitTorque();
            Activated += (s, e) => RefreshLayers();
            RestoreUi();
            Closing += (s, e) => SaveState();
        }

        // ================================================================ helpers

        static ComboBoxItem Item(string text, object tag) => new() { Content = Loc.T(text), Tag = tag };

        static void Select(ComboBox cb, object tag)
        {
            foreach (ComboBoxItem it in cb.Items)
                if (Equals(it.Tag, tag)) { cb.SelectedItem = it; return; }
            if (cb.Items.Count > 0 && cb.SelectedIndex < 0) cb.SelectedIndex = 0;
        }

        static T Tag<T>(ComboBox cb, T fallback) => cb.SelectedItem is ComboBoxItem it && it.Tag is T t ? t : fallback;

        static double Num(string s, double fallback)
        {
            s = (s ?? "").Trim().Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        public void SelectTab(int i) { if (i >= 0 && i < Tabs.Items.Count) Tabs.SelectedIndex = i; }

        void Status(string s, params object[] args) => StatusText.Text = Loc.F(s, args);

        // ================================================================ setup

        void FillStatic()
        {
            foreach (var box in new[] { KindBox, AsmBoltBox, HeadWasherBox, NutWasherBox, SpringBox, NutKindBox, NutCountBox }) box.Items.Clear();
            foreach (PartKind k in Enum.GetValues(typeof(PartKind))) KindBox.Items.Add(Item(PartKindInfo.DisplayName(k), k));
            foreach (PartKind k in new[] { PartKind.HexBolt, PartKind.HexBoltFullThread, PartKind.SocketCapScrew, PartKind.ThreadedRod })
                AsmBoltBox.Items.Add(Item(PartKindInfo.DisplayName(k), k));
            foreach (var cb in new[] { HeadWasherBox, NutWasherBox })
            {
                cb.Items.Add(Item("None", WasherChoice.None));
                cb.Items.Add(Item("Plain washer", WasherChoice.Plain));
                cb.Items.Add(Item("Large washer", WasherChoice.Large));
            }
            SpringBox.Items.Add(Item("No spring washer", SpringPosition.None));
            SpringBox.Items.Add(Item("Nut side (under the nut)", SpringPosition.UnderNut));
            SpringBox.Items.Add(Item("Bolt side (under the head)", SpringPosition.UnderHead));
            NutKindBox.Items.Add(Item("Hex nut", PartKind.HexNut));
            NutKindBox.Items.Add(Item("Thin nut", PartKind.ThinNut));
            NutCountBox.Items.Add(Item("No nut (tapped hole)", 0));
            NutCountBox.Items.Add(Item("1 nut", 1));
            NutCountBox.Items.Add(Item("Nut + lock nut", 2));
        }

        void FillSizes()
        {
            var t = _job.Table;
            SizeBox.Items.Clear(); TqSizeBox.Items.Clear();
            foreach (var r in t.Rows) { SizeBox.Items.Add(Item(r.Size, r.Size)); TqSizeBox.Items.Add(Item(r.Size, r.Size)); }
            Select(SizeBox, _job.Size);
            Select(TqSizeBox, _job.Size);
        }

        void FillLengths()
        {
            LengthBox.Items.Clear();
            foreach (var l in _job.Table.Lengths) LengthBox.Items.Add(F(l));
            LengthBox.Text = F(_job.Length);
        }

        void FillBase()
        {
            var cur = _job.BasePoint;
            BaseBox.Items.Clear();
            bool boltLike = _job.IsAssembly || PartKindInfo.IsBolt(_job.Kind);
            if (boltLike)
            {
                BaseBox.Items.Add(Item("Head top – middle of the head tip", BasePoint.HeadTop));
                BaseBox.Items.Add(Item("Under head – bearing face", BasePoint.UnderHead));
                BaseBox.Items.Add(Item("Thread end", BasePoint.ThreadEnd));
                BaseBox.Items.Add(Item("Middle of the length", BasePoint.Center));
                if (cur is BasePoint.BearingFace or BasePoint.OuterFace) cur = BasePoint.HeadTop;
            }
            else
            {
                BaseBox.Items.Add(Item("Bearing face (contact side)", BasePoint.BearingFace));
                BaseBox.Items.Add(Item("Outer face", BasePoint.OuterFace));
                BaseBox.Items.Add(Item("Middle of the thickness", BasePoint.Center));
                if (cur is BasePoint.HeadTop or BasePoint.UnderHead or BasePoint.ThreadEnd) cur = BasePoint.BearingFace;
            }
            Select(BaseBox, cur);
            _job.BasePoint = Tag(BaseBox, cur);
        }

        void RefreshLayers()
        {
            try
            {
                var db = AcApp.DocumentManager.MdiActiveDocument?.Database;
                if (db == null) return;
                var names = LayerUtil.LayerNames(db);
                string cur = string.IsNullOrWhiteSpace(WipeLayerBox.Text) ? _job.WipeoutLayer : WipeLayerBox.Text;
                _loading = true;
                WipeLayerBox.Items.Clear();
                if (!names.Contains(cur, StringComparer.OrdinalIgnoreCase)) WipeLayerBox.Items.Add(cur);
                foreach (var n in names) WipeLayerBox.Items.Add(n);
                WipeLayerBox.Text = cur;
                _loading = false;
            }
            catch { _loading = false; }
        }

        // ================================================================ job <-> controls

        void LoadJob(BoltJob job)
        {
            _loading = true;
            _job = job;
            job.Offsets ??= new();
            _sel.Clear();
            StdIso.IsChecked = job.Standard == FastenerStandard.ISO;
            StdJis.IsChecked = job.Standard == FastenerStandard.JIS;
            Select(KindBox, job.Kind);
            FillSizes();
            FillLengths();
            ThNone.IsChecked = job.Thread == ThreadStyle.None;
            ThSimple.IsChecked = job.Thread == ThreadStyle.Simplified;
            ThDetail.IsChecked = job.Thread == ThreadStyle.Detailed;

            AsmCheck.IsChecked = job.IsAssembly;
            Select(AsmBoltBox, job.BoltKind);
            ClampBox.Text = F(job.ClampLength);
            Select(HeadWasherBox, job.HeadWasher);
            Select(NutWasherBox, job.NutWasher);
            Select(SpringBox, job.Spring);
            Select(NutKindBox, job.NutKind);
            Select(NutCountBox, job.NutCount);
            AutoLenCheck.IsChecked = job.AutoLength;
            ProtrusionBox.Text = F(job.MinProtrusionPitches);
            SeparateCheck.IsChecked = job.SeparateBlocks;
            StickCheck.IsChecked = job.StickToBoltEnd;

            Out2D.IsChecked = job.Output == OutputMode.View2D;
            OutMesh.IsChecked = job.Output == OutputMode.Mesh3D;
            OutSolid.IsChecked = job.Output == OutputMode.Solid3D;
            SetAngles();
            HiddenCheck.IsChecked = job.HiddenLines;
            ViewSnapCheck.IsChecked = job.ViewSnap;
            ScaleBox.Text = F(job.Scale);
            WipeCheck.IsChecked = job.Wipeout;
            WipeLayerBox.Text = job.WipeoutLayer;
            SnapCheck.IsChecked = job.SnapLengthToStandard;
            FillBase();
            _loading = false;
            UpdateDependent();
            Render(true, true);
        }

        void SetAngles()
        {
            bool l = _loading; _loading = true;
            AzSlider.Value = _job.Azimuth; ElSlider.Value = _job.Elevation; SpinSlider.Value = _job.Spin;
            AzBox.Text = F(Math.Round(_job.Azimuth, 2)); ElBox.Text = F(Math.Round(_job.Elevation, 2)); SpinBox.Text = F(Math.Round(_job.Spin, 2));
            _loading = l;
        }

        void ReadControls()
        {
            var j = _job;
            var std = StdJis.IsChecked == true ? FastenerStandard.JIS : FastenerStandard.ISO;
            bool stdChanged = std != j.Standard;
            j.Standard = std;
            var kind = Tag(KindBox, j.Kind);
            j.Kind = kind;
            if (!stdChanged) j.Size = Tag(SizeBox, j.Size);
            j.Length = Math.Max(1, Num(LengthBox.Text, j.Length));
            j.Thread = ThDetail.IsChecked == true ? ThreadStyle.Detailed : ThNone.IsChecked == true ? ThreadStyle.None : ThreadStyle.Simplified;
            j.BasePoint = Tag(BaseBox, j.BasePoint);

            j.IsAssembly = AsmCheck.IsChecked == true;
            j.ClampLength = Math.Max(0, Num(ClampBox.Text, j.ClampLength));
            j.HeadWasher = Tag(HeadWasherBox, j.HeadWasher);
            j.NutWasher = Tag(NutWasherBox, j.NutWasher);
            j.Spring = Tag(SpringBox, j.Spring);
            j.NutKind = Tag(NutKindBox, j.NutKind);
            j.NutCount = Tag(NutCountBox, j.NutCount);
            j.AutoLength = AutoLenCheck.IsChecked == true;
            j.MinProtrusionPitches = Math.Max(0, Num(ProtrusionBox.Text, j.MinProtrusionPitches));
            j.SeparateBlocks = SeparateCheck.IsChecked == true;
            j.StickToBoltEnd = StickCheck.IsChecked == true;

            j.Output = OutMesh.IsChecked == true ? OutputMode.Mesh3D : OutSolid.IsChecked == true ? OutputMode.Solid3D : OutputMode.View2D;
            j.HiddenLines = HiddenCheck.IsChecked == true;
            j.ViewSnap = ViewSnapCheck.IsChecked == true;
            j.Scale = Math.Max(1e-4, Num(ScaleBox.Text, j.Scale));
            j.Wipeout = WipeCheck.IsChecked == true;
            if (!string.IsNullOrWhiteSpace(WipeLayerBox.Text)) j.WipeoutLayer = WipeLayerBox.Text.Trim();
            j.SnapLengthToStandard = SnapCheck.IsChecked == true;

            if (stdChanged)
            {
                _loading = true; FillSizes(); FillLengths(); _loading = false;
                j.Size = Tag(SizeBox, j.Size);
            }
        }

        void UpdateDependent()
        {
            var j = _job; var r = j.Row;
            bool asm = j.IsAssembly;
            AsmPanel.IsEnabled = asm;
            AsmPanel.Opacity = asm ? 1 : 0.5;
            bool hasLen = asm ? !j.AutoLength : PartKindInfo.HasLength(j.Kind);
            LengthPanel.Visibility = (asm || PartKindInfo.HasLength(j.Kind)) ? Visibility.Visible : Visibility.Hidden;
            LengthBox.IsEnabled = hasLen;
            DefaultPanel.Visibility = PartKindInfo.HasLength(asm ? j.BoltKind : j.Kind) ? Visibility.Visible : Visibility.Collapsed;
            DefaultText.Text = Loc.F("Default for {0}: {1} mm", r.Size, F(r.DefaultLength));
            var shownKind = asm ? j.BoltKind : j.Kind;
            DesignationText.Text = $"{PartKindInfo.Designation(shownKind, j.Standard)}" + (asm ? Loc.T("  ·  assembly uses the bolt selected on the Assembly tab") : "");
            SubTitle.Text = Loc.T(_editId.IsNull ? "Bolts · nuts · washers → blocks" : "Editing a placed block – press “Update selected block”");

            // dimension chips
            Legend.SetKind(shownKind);
            DimPanel.Children.Clear();
            void Dim(string n, double v) => DimPanel.Children.Add(new Border
            {
                Style = (Style)FindResource("Stat"),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = Loc.T(n), FontSize = 11, Foreground = (Brush)FindResource("Muted") },
                        new TextBlock { Text = F(v), FontSize = 15, FontWeight = FontWeights.SemiBold }
                    }
                }
            });
            Dim("d", r.D); Dim("pitch P", r.P);
            switch (shownKind)
            {
                case PartKind.SocketCapScrew: Dim("head Ø dk", r.SocketDk); Dim("head k", r.SocketK); Dim("key s", r.SocketS); break;
                case PartKind.ThreadedRod: break;
                case PartKind.HexNut: Dim("s", r.S); Dim("m", r.M); break;
                case PartKind.ThinNut: Dim("s", r.S); Dim("m", r.MThin); break;
                case PartKind.PlainWasher: Dim("d1", r.WasherD1); Dim("d2", r.WasherD2); Dim("h", r.WasherH); break;
                case PartKind.LargeWasher: Dim("d1", r.WasherD1); Dim("d2", r.LargeD2); Dim("h", r.LargeH); break;
                case PartKind.SpringWasher: Dim("d1", r.SpringD1); Dim("d2", r.SpringD2); Dim("s", r.SpringT); break;
                default: Dim("s", r.S); Dim("k", r.K); break;
            }
            if (PartKindInfo.HasLength(shownKind))
            {
                double L = asm ? PartBuilder.ComputeLayout(j).BoltLength : j.Length;
                Dim("thread b", Math.Round(StandardTable.ThreadLength(shownKind, r, L), 1));
            }

            if (asm)
            {
                var lay = PartBuilder.ComputeLayout(j);
                var parts = string.Join(" + ", lay.Parts.Select(p => p.Role switch
                {
                    "bolt" => $"{PartKindInfo.Code(p.Kind)} {j.Size}×{F(p.Length)}",
                    _ => PartKindInfo.Code(p.Kind)
                }));
                AsmResult.Text = Loc.F("Bolt length: {0} mm  ({1})", F(lay.BoltLength), Loc.T(j.AutoLength ? "auto" : "manual")) + "\n" + parts +
                                 (lay.Warning != null ? $"\n⚠ {lay.Warning}" : "");
                AsmResult.Foreground = lay.Warning != null ? Brushes.Firebrick : (Brush)FindResource("Ink");
            }
            UpdateBtn.Visibility = _editId.IsNull ? Visibility.Collapsed : Visibility.Visible;
        }

        // ================================================================ preview

        void Render(bool fit, bool quality)
        {
            try
            {
                var j = _job; var r = j.Row;
                var lay = PartBuilder.ComputeLayout(j);
                _lay = lay;
                string key = string.Join("|", j.Standard, j.Size, j.Thread,
                    string.Join(";", lay.Parts.Select(p => $"{p.Kind}{p.X:0.###}/{p.Length:0.###}")));
                if (key != _meshKey) { _mesh = PartBuilder.BuildLayout(lay, r, j.Thread); _meshKey = key; fit = !_partDragging; }
                var dwg = HiddenLineRenderer.Render(_mesh, j.Azimuth, j.Elevation, j.Spin, j.HiddenLines, quality);

                Preview.ShowWipeouts = j.Wipeout && j.Output == OutputMode.View2D && ShowWipeToggle.IsChecked == true;
                Preview.Markers.Clear(); Preview.Grips.Clear();
                string role = j.IsAssembly ? BlockFactory.RoleAssembly : BlockFactory.RoleSingle;
                double bx = BlockFactory.LayoutBaseX(j, role, lay);
                var bp = dwg.Project(new Vec3(bx, 0, 0));
                Preview.Markers.Add((new Point(bp.X, bp.Y), "0,0,0"));
                foreach (var (kind, x) in LengthGripOverrule.GripDefs(j.Clone().WithRole(role)))
                {
                    var a = dwg.Project(new Vec3(x, 0, 0)); var b = dwg.Project(new Vec3(x + 1, 0, 0));
                    var dir = new Vector(b.X - a.X, b.Y - a.Y);
                    if (dir.Length > 0.05) Preview.Grips.Add((new Point(a.X, a.Y), dir));
                }
                Preview.Caption = Loc.T(j.Output == OutputMode.View2D ? "2D block" : j.Output == OutputMode.Mesh3D ? "3D mesh block (preview)" : "3D solid block (preview)")
                                  + $"  ·  {j.ViewCode()}";
                // selectable parts, selection highlight and the head-to-part distance
                Preview.SelectableGroups.Clear(); Preview.Highlight.Clear(); Preview.Dim = null;
                _sel.RemoveWhere(sr => !lay.Parts.Any(p => p.Role == sr));
                Preview.ShowPartHint = j.IsAssembly;
                if (j.IsAssembly)
                    for (int g = 0; g < _mesh.GroupOwner.Count; g++)
                    {
                        var part = lay.Parts[_mesh.GroupOwner[g]];
                        if (part.Role == "bolt") continue;
                        Preview.SelectableGroups.Add(g);
                        if (_sel.Contains(part.Role)) Preview.Highlight.Add(g);
                    }
                if (_sel.Count > 0)
                {
                    double refX = RefX(lay), near = NearX(lay);
                    var da = dwg.Project(new Vec3(refX, 0, 0)); var db = dwg.Project(new Vec3(near, 0, 0));
                    Preview.Dim = (new Point(da.X, da.Y), new Point(db.X, db.Y), F(Math.Round(near - refX, 2)) + " mm");
                }
                Preview.SetDrawing(dwg, fit, _partDragging);
                int lines = dwg.Lines.Count;
                InfoText.Text = $"{Loc.T(j.IsAssembly ? "Assembly" : PartKindInfo.DisplayName(j.Kind))}  {j.Size}" +
                                (lay.BoltLength > 0 ? $"×{F(lay.BoltLength)}" : "") +
                                $"   ·   az {j.Azimuth:0.#}°  el {j.Elevation:0.#}°   ·   " + Loc.F("{0} triangles, {1} polylines", _mesh.TriangleCount, lines);
                if (quality) Track();
            }
            catch (Exception ex) { Status("Preview error: {0}", ex.Message); }
        }

        void OnRotate(double dAz, double dEl, bool final) => Rotate(dAz, dEl, final, true);

        static double Wrap(double a) => ((a + 180) % 360 + 360) % 360 - 180;

        /// <summary>Catches on multiples of 30° and 45° (side, ends, diagonals).</summary>
        static double SnapAz(double az)
        {
            double c1 = Math.Round(az / 30) * 30, c2 = Math.Round(az / 45) * 45;
            double c = Math.Abs(c1 - az) <= Math.Abs(c2 - az) ? c1 : c2;
            return Math.Abs(c - az) <= 4 ? Wrap(c) : az;
        }

        static readonly double[] ElSnaps = { 0, 30, 35.264, 45, 60, 90 };

        /// <summary>Catches on flat, 30°, isometric 35.26°, 45°, 60° and top / bottom views.</summary>
        static double SnapEl(double el)
        {
            double a = Math.Abs(el), best = ElSnaps.OrderBy(v => Math.Abs(v - a)).First();
            return Math.Abs(best - a) <= 2.5 ? Math.Sign(el) * best : el;
        }

        /// <summary>Rotates the view. Mouse / joystick orbiting keeps the raw angle and shows the snapped one,
        /// so the view catches on standard angles but never gets stuck there.</summary>
        void Rotate(double dAz, double dEl, bool final, bool snap)
        {
            if (!_rawValid) { _rawAz = _job.Azimuth; _rawEl = _job.Elevation; _rawValid = true; }
            _rawAz = Wrap(_rawAz + dAz);
            _rawEl = Math.Clamp(_rawEl + dEl, -90, 90);
            double az = _rawAz, el = _rawEl;
            if (snap && _job.ViewSnap) { az = SnapAz(az); el = SnapEl(el); }
            _job.Azimuth = Math.Round(az, 2);
            _job.Elevation = Math.Round(el, 2);
            if (final) _rawValid = false;
            SetAngles();
            Render(false, final);
            if (!final) { _hq.Stop(); _hq.Start(); }
        }

        // ================================================================ events

        void OnChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || !IsInitialized) return;
            // editable combo boxes update their Text only after SelectionChanged
            if (e is SelectionChangedEventArgs sce && sender is ComboBox cb && cb.IsEditable && sce.AddedItems.Count > 0)
            {
                _loading = true; cb.Text = sce.AddedItems[0]?.ToString(); _loading = false;
            }
            var oldKind = _job.Kind; bool oldAsm = _job.IsAssembly;
            ReadControls();
            if (oldKind != _job.Kind || oldAsm != _job.IsAssembly) { _loading = true; FillBase(); _loading = false; }
            UpdateDependent();
            Render(false, true);
        }

        void OnSizeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            ReadControls();
            // take the most used length for the new size
            _job.Length = _job.Row.DefaultLength;
            _loading = true; LengthBox.Text = F(_job.Length); Select(TqSizeBox, _job.Size); _loading = false;
            UpdateDependent();
            Render(true, true);
            OnTorque(null, null);
        }

        void OnAsmBoltChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var k = Tag(AsmBoltBox, PartKind.HexBolt);
            _loading = true; Select(KindBox, k); _loading = false;
            OnChanged(sender, e);
        }

        void OnEnterKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (sender == AzBox || sender == ElBox || sender == SpinBox) OnAngleBox(sender, e);
            else if (sender == TqMuG || sender == TqMuK || sender == TqUtil) OnTorque(sender, e);
            else OnChanged(sender, e);
        }

        void OnSlider(object sender, RoutedEventArgs e)
        {
            if (_loading || !IsInitialized) return;
            _job.Azimuth = Math.Round(AzSlider.Value, 1);
            _job.Elevation = Math.Round(ElSlider.Value, 1);
            _job.Spin = Math.Round(SpinSlider.Value, 1);
            SetAngles();
            Render(false, false);
            _hq.Stop(); _hq.Start();
        }

        void OnAngleBox(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _job.Azimuth = Num(AzBox.Text, _job.Azimuth);
            _job.Elevation = Math.Clamp(Num(ElBox.Text, _job.Elevation), -90, 90);
            _job.Spin = Num(SpinBox.Text, _job.Spin);
            SetAngles();
            Render(false, true);
        }

        void OnPreset(object sender, RoutedEventArgs e)
        {
            var p = ((string)((Button)sender).Tag).Split(',');
            _job.Azimuth = Num(p[0], 0); _job.Elevation = Num(p[1], 0);
            SetAngles();
            Render(true, true);
        }

        void OnScaleChip(object sender, RoutedEventArgs e)
        {
            ScaleBox.Text = (string)((Button)sender).Tag;
            OnChanged(sender, e);
        }

        void OnShowWipe(object sender, RoutedEventArgs e) => Render(false, true);

        void OnSetDefault(object sender, RoutedEventArgs e)
        {
            ReadControls();
            double len = _job.IsAssembly ? PartBuilder.ComputeLayout(_job).BoltLength : _job.Length;
            Settings.SetDefaultLength(_job.Standard, _job.Size, len);
            UpdateDependent();
            Status("Default length for {0} ({1}) set to {2} mm.", _job.Size, _job.Standard, F(len));
        }

        void OnReset(object sender, RoutedEventArgs e)
        {
            _editId = ObjectId.Null;
            LoadJob(new BoltJob());
            Status("Settings reset to defaults.");
        }

        bool Validate()
        {
            ReadControls();
            if (_job.Output == OutputMode.View2D && _job.Wipeout && string.IsNullOrWhiteSpace(_job.WipeoutLayer))
            { Status("Choose a layer for the wipeouts."); return false; }
            if (AcApp.DocumentManager.MdiActiveDocument == null) { Status("Open a drawing first."); return false; }
            Settings.SaveLastJob(_job);
            return true;
        }

        void OnInsert(object sender, RoutedEventArgs e)
        {
            if (!Validate()) return;
            Status("Pick the insertion point in the drawing…");
            Commands.Run("BT_BOLTINSERT", _job);
        }

        void OnMake(object sender, RoutedEventArgs e)
        {
            if (!Validate()) return;
            Commands.Run("BT_BOLTMAKE", _job);
            Status("Block definition created – see the command line for its name.");
        }

        void OnUpdate(object sender, RoutedEventArgs e)
        {
            if (_editId.IsNull || !Validate()) return;
            Commands.Run("BT_BOLTUPDATE", _job, _editId);
            Status("Block updated.");
        }

        public void LoadForEdit(BoltJob job, ObjectId id)
        {
            _editId = id;
            LoadJob(job.Clone());
            Status("Loaded from the selected block. Change anything and press “Update selected block”.");
        }

        // ================================================================ remember the last session

        /// <summary>Saves the current settings, view and window state so the next start continues from here.</summary>
        void SaveState()
        {
            try
            {
                ReadControls();
                Settings.SaveLastJob(_job);
                var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                Settings.SaveUi(new UiState
                {
                    Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height,
                    Tab = Tabs.SelectedIndex,
                    TqClass = TqClassBox.SelectedIndex, TqFinish = TqFinishBox.SelectedIndex,
                    TqLube = TqLubeBox.SelectedIndex, TqMaterial = TqMatBox.SelectedIndex,
                    TqMuG = TqMuG.Text, TqMuK = TqMuK.Text, TqUtil = TqUtil.Text,
                    TqWasher = TqWasher.IsChecked == true,
                    ShowWipeouts = ShowWipeToggle.IsChecked == true
                });
            }
            catch { /* never block closing */ }
        }

        void RestoreUi()
        {
            var u = Settings.LoadUi();
            if (u == null) return;
            try
            {
                if (u.Width >= MinWidth && u.Height >= MinHeight) { Width = u.Width; Height = u.Height; }
                // only reuse the position when it is still on a connected screen
                if (!double.IsNaN(u.Left) && !double.IsNaN(u.Top) &&
                    u.Left > SystemParameters.VirtualScreenLeft - 50 &&
                    u.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 150 &&
                    u.Top > SystemParameters.VirtualScreenTop - 10 &&
                    u.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = u.Left; Top = u.Top;
                }
                SelectTab(u.Tab);
                ShowWipeToggle.IsChecked = u.ShowWipeouts;

                _loading = true;
                void Pick(ComboBox cb, int i) { if (i >= 0 && i < cb.Items.Count) cb.SelectedIndex = i; }
                Pick(TqClassBox, u.TqClass); Pick(TqFinishBox, u.TqFinish); Pick(TqLubeBox, u.TqLube); Pick(TqMatBox, u.TqMaterial);
                if (!string.IsNullOrWhiteSpace(u.TqUtil)) TqUtil.Text = u.TqUtil;
                TqWasher.IsChecked = u.TqWasher;
                _loading = false;
                OnTorqueMu(null, null);                      // friction from the lists …
                if (!string.IsNullOrWhiteSpace(u.TqMuG) && !string.IsNullOrWhiteSpace(u.TqMuK))
                {
                    TqMuG.Text = u.TqMuG; TqMuK.Text = u.TqMuK;   // … then the values the user had typed
                    OnTorque(null, null);
                }
                Render(false, true);
            }
            catch { _loading = false; }
        }

        // ================================================================ moving parts in the preview

        /// <summary>Reference for the distance: under-head face of the bolt (start of a threaded rod).</summary>
        double RefX(Layout lay)
        {
            var bolt = lay.Parts[0];
            return bolt.X + PartBuilder.HeadHeight(bolt.Kind, _job.Row);
        }

        /// <summary>Near face of the closest selected part.</summary>
        double NearX(Layout lay) => lay.Parts.Where(p => _sel.Contains(p.Role)).Min(p => p.X);

        string RoleOfGroup(int g) =>
            g >= 0 && _lay != null && _mesh != null && g < _mesh.GroupOwner.Count ? _lay.Parts[_mesh.GroupOwner[g]].Role : null;

        double Off(string role) => _job.Offsets != null && _job.Offsets.TryGetValue(role, out var v) ? v : 0;

        void SetOffsets(Dictionary<string, double> values)
        {
            _job.Offsets ??= new();
            foreach (var kv in values)
            {
                if (Math.Abs(kv.Value) < 1e-9) _job.Offsets.Remove(kv.Key);
                else _job.Offsets[kv.Key] = kv.Value;
            }
        }

        /// <summary>Stores the shift the layout really used (a part cannot pass the bolt head).</summary>
        void NormalizeOffsets()
        {
            if (_job.Offsets == null || _job.Offsets.Count == 0 || _job.BoltKind == PartKind.ThreadedRod) return;
            var shifted = PartBuilder.ComputeLayout(_job);
            var plain = _job.Clone(); plain.Offsets = new();
            var without = PartBuilder.ComputeLayout(plain);
            foreach (var p in shifted.Parts)
            {
                if (p.Role == "bolt") continue;
                var q = without.Parts.FirstOrDefault(x => x.Role == p.Role);
                if (q == null) continue;
                double off = Math.Round(p.X - q.X, 3);
                if (Math.Abs(off) < 1e-9) _job.Offsets.Remove(p.Role); else _job.Offsets[p.Role] = off;
            }
        }

        /// <summary>
        /// How far the selected parts can move together (mm, negative = toward the head) before they touch
        /// a part that is not selected or the underside of the bolt head.
        /// </summary>
        (double min, double max) MoveLimits()
        {
            double min = double.NegativeInfinity, max = double.PositiveInfinity;
            if (_lay == null) return (min, max);
            const double eps = 1e-6;
            bool rod = _lay.Parts[0].Kind == PartKind.ThreadedRod;
            double head = RefX(_lay);
            foreach (var s in _lay.Parts)
            {
                if (!_sel.Contains(s.Role)) continue;
                double a = s.X, b = s.X + s.Thickness;
                if (!rod) min = Math.Max(min, head - a);
                foreach (var n in _lay.Parts)
                {
                    if (n.Role == "bolt" || _sel.Contains(n.Role)) continue;
                    double c = n.X, d = n.X + n.Thickness;
                    if (c >= b - eps) max = Math.Min(max, c - b);          // neighbour on the thread side
                    else if (d <= a + eps) min = Math.Max(min, d - a);     // neighbour on the head side
                }
            }
            return (Math.Min(min, 0), Math.Max(max, 0));
        }

        void OnPartDown(int group)
        {
            HideDistBox();
            _dragStart = null;
            var role = RoleOfGroup(group);
            if (role == null || role == "bolt")
            {
                _downAdded = false;
                if (_sel.Count > 0) { _sel.Clear(); Render(false, true); }
                return;
            }
            _downAdded = _sel.Add(role);
            _dragAccum = 0;
            _dragStart = _sel.ToDictionary(r => r, r => Off(r));
            (_stepMin, _stepMax) = MoveLimits();
            Render(false, true);
        }

        void OnPartDrag(double mm, bool final)
        {
            if (_dragStart == null) return;
            _dragAccum += mm;
            double step = Math.Round(_dragAccum * 2) / 2;            // 0.5 mm steps
            step = Math.Clamp(step, _stepMin, _stepMax);             // stop at contact with the next part / the head
            SetOffsets(_dragStart.ToDictionary(k => k.Key, k => k.Value + step));
            _partDragging = !final;
            if (final) NormalizeOffsets();
            UpdateDependent();
            Render(false, final);
        }

        void OnPartUp(int group, bool moved)
        {
            _partDragging = false;
            var role = RoleOfGroup(group);
            // Ctrl+click on an already selected part takes it out of the selection
            if (!moved && !_downAdded && role != null && _sel.Remove(role)) Render(false, true);
        }

        void OnDistanceClicked(Point topLeft)
        {
            if (_sel.Count == 0 || _lay == null) return;
            DistBox.Margin = new Thickness(Math.Max(0, topLeft.X), Math.Max(0, topLeft.Y), 0, 0);
            DistBox.Text = F(Math.Round(NearX(_lay) - RefX(_lay), 2));
            DistBox.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(() => { DistBox.Focus(); DistBox.SelectAll(); }), DispatcherPriority.Input);
        }

        void HideDistBox() => DistBox.Visibility = Visibility.Collapsed;

        /// <summary>Unselects all parts (Esc, also from inside the distance field).</summary>
        void ClearSelection()
        {
            HideDistBox();
            _dragStart = null;
            if (_sel.Count == 0) return;
            _sel.Clear();
            Preview.Focus();
            Render(false, true);
        }

        void OnDistLost(object sender, RoutedEventArgs e) => HideDistBox();

        void OnDistKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { ClearSelection(); e.Handled = true; return; }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (_sel.Count > 0 && _lay != null)
            {
                double cur = NearX(_lay) - RefX(_lay);
                double target = Math.Max(0, Num(DistBox.Text, cur));
                var (lo, hi) = MoveLimits();
                double delta = Math.Clamp(target - cur, lo, hi);     // a typed value also stops at contact
                SetOffsets(_sel.ToDictionary(r => r, r => Off(r) + delta));
                NormalizeOffsets();
                UpdateDependent();
                Render(false, true);
            }
            HideDistBox();
        }

        void OnResetOffsets(object sender, RoutedEventArgs e)
        {
            _job.Offsets = new();
            _sel.Clear();
            UpdateDependent();
            Render(true, true);
        }

        // ================================================================ undo / keyboard / language

        /// <summary>Remembers the settings after every finished change so Ctrl+Z can step back.</summary>
        void Track()
        {
            string now = _job.ToJson();
            if (_snap == null) { _snap = now; return; }
            if (now == _snap) return;
            if (!_restoring)
            {
                _undo.Push(_snap);
                _redo.Clear();
            }
            _snap = now;
        }

        void Restore(string json)
        {
            var j = BoltJob.FromJson(json);
            if (j == null) return;
            _restoring = true;
            try { LoadJob(j); } finally { _restoring = false; }
        }

        void Undo()
        {
            if (_undo.Count == 0) { Status("Nothing to undo."); return; }
            _redo.Push(_snap);
            Restore(_undo.Pop());
            Status("Undone.");
        }

        void Redo()
        {
            if (_redo.Count == 0) { Status("Nothing to redo."); return; }
            _undo.Push(_snap);
            Restore(_redo.Pop());
            Status("Redone.");
        }

        void OnKeys(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (ctrl && e.Key == Key.Z) { if (shift) Redo(); else Undo(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; return; }

            // Esc: close the distance field and drop the part selection (an open drop-down list keeps its own Esc)
            if (e.Key == Key.Escape && !(Keyboard.FocusedElement is ComboBoxItem) &&
                (_sel.Count > 0 || DistBox.Visibility == Visibility.Visible))
            {
                ClearSelection();
                e.Handled = true;
                return;
            }

            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                // leave the arrows to controls that need them
                var f = Keyboard.FocusedElement;
                if (f is TextBox || f is ComboBox || f is ComboBoxItem || f is Slider || f is TabItem || f is DataGridCell) return;
                double step = ctrl ? 0.1 : shift ? 15 : 1;
                double dAz = e.Key == Key.Right ? step : e.Key == Key.Left ? -step : 0;
                double dEl = e.Key == Key.Down ? step : e.Key == Key.Up ? -step : 0;
                Rotate(dAz, dEl, true, false);       // keyboard steps are exact, never snapped
                e.Handled = true;
            }
        }

        void OnLanguage(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !IsInitialized) return;
            Loc.Jp = LangBox.SelectedIndex == 1;
            Settings.Language = Loc.Jp ? "ja" : "en";
            ReadControls();
            Loc.Apply(this);
            Loc.ApplyTips(this);
            _loading = true; FillStatic(); _loading = false;
            bool r = _restoring; _restoring = true;      // a language change is not an undo step
            try { LoadJob(_job); } finally { _restoring = r; }
            InitTorque();
            Preview.InvalidateVisual();
        }

        // ================================================================ torque

        void InitTorque()
        {
            _loading = true;
            bool first = TqClassBox.Items.Count == 0;
            int iC = TqClassBox.SelectedIndex, iF = TqFinishBox.SelectedIndex, iL = TqLubeBox.SelectedIndex, iM = TqMatBox.SelectedIndex;
            foreach (var box in new[] { TqClassBox, TqFinishBox, TqLubeBox, TqMatBox }) box.Items.Clear();
            foreach (var c in TorqueCalculator.Classes) TqClassBox.Items.Add(Item(c.Name, c));
            foreach (var f in TorqueCalculator.Finishes) TqFinishBox.Items.Add(Item(f.Name, f));
            foreach (var l in TorqueCalculator.Lubrications) TqLubeBox.Items.Add(Item(l.Name, l));
            foreach (var m in TorqueCalculator.Materials) TqMatBox.Items.Add(Item(m.Name, m));
            if (first)
            {
                TqClassBox.SelectedIndex = 2; TqFinishBox.SelectedIndex = 2; TqLubeBox.SelectedIndex = 1; TqMatBox.SelectedIndex = 0;
                TqUtil.Text = "90"; TqWasher.IsChecked = true;
                _loading = false;
                OnTorqueMu(null, null);
            }
            else
            {
                // language switch: keep the selection and the friction values the user may have typed
                TqClassBox.SelectedIndex = iC; TqFinishBox.SelectedIndex = iF; TqLubeBox.SelectedIndex = iL; TqMatBox.SelectedIndex = iM;
                _loading = false;
                OnTorque(null, null);
            }
        }

        void OnTorqueMu(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var f = Tag<Finish>(TqFinishBox, null); var l = Tag<Lubrication>(TqLubeBox, null); var m = Tag<ClampMaterial>(TqMatBox, null);
            if (f == null || l == null || m == null) return;
            double mu = TorqueCalculator.Mu(f, l);
            _loading = true;
            TqMuG.Text = F(mu);
            TqMuK.Text = F(Math.Round(mu * m.HeadFrictionFactor, 3));
            _loading = false;
            OnTorque(sender, e);
        }

        void OnTorque(object sender, RoutedEventArgs e)
        {
            if (_loading || !IsInitialized) return;
            var sc = Tag<StrengthClass>(TqClassBox, null); var m = Tag<ClampMaterial>(TqMatBox, null);
            if (sc == null || m == null) return;
            double muG = Math.Clamp(Num(TqMuG.Text, 0.12), 0.02, 0.6), muK = Math.Clamp(Num(TqMuK.Text, 0.12), 0.02, 0.6);
            double util = Math.Clamp(Num(TqUtil.Text, 90), 10, 100) / 100.0;
            bool washer = TqWasher.IsChecked == true;
            var table = _job.Table;
            var kind = _job.IsAssembly ? _job.BoltKind : (PartKindInfo.IsBolt(_job.Kind) ? _job.Kind : PartKind.HexBolt);

            TorqueResult Calc(SizeRow r)
            {
                var (dW, dh, pd) = TorqueCalculator.Bearing(r, kind, washer);
                return TorqueCalculator.Calculate(r, sc, muG, muK, util, dW, dh, m.PG, pd);
            }

            var row = table.Get(Tag(TqSizeBox, _job.Size));
            var res = Calc(row);
            TqTorque.Text = $"{res.TorqueNm:0.#} N·m";
            TqRange.Text = Loc.F("range {0} – {1} N·m", res.TorqueMinNm.ToString("0.#", CultureInfo.InvariantCulture), res.TorqueMaxNm.ToString("0.#", CultureInfo.InvariantCulture));
            TqPreload.Text = $"{res.PreloadKN:0.0} kN";
            TqAs.Text = $"A_s {res.As:0.#} mm²  ·  Rp0.2 {sc.Rp(row.D):0} MPa";
            TqPressure.Text = res.PressureOk
                ? Loc.F("✓ Surface pressure under head {0} MPa ≤ {1} MPa allowed for {2}.", res.SurfacePressure.ToString("0"), res.PG.ToString("0"), Loc.T(m.Name))
                : Loc.F("⚠ Surface pressure {0} MPa exceeds {1} MPa for {2} – use a (large / hardened) washer or lower class.", res.SurfacePressure.ToString("0"), res.PG.ToString("0"), Loc.T(m.Name));
            TqPressureBorder.Background = new SolidColorBrush(res.PressureOk ? Color.FromRgb(0xEC, 0xFD, 0xF3) : Color.FromRgb(0xFE, 0xF2, 0xF2));
            TqGrid.ItemsSource = table.Rows.Select(Calc).ToList();
        }
    }

    static class JobExt
    {
        public static BoltJob WithRole(this BoltJob j, string role) { j.Role = role; return j; }
    }
}
