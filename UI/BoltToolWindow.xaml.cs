using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        Mesh _mesh; string _meshKey, _viewKey;
        Layout _lay;
        string _snap; bool _restoring;
        readonly Stack<string> _undo = new(), _redo = new();
        double _rawAz, _rawEl; bool _rawValid;                  // unsnapped angles while orbiting

        // ---- elements on the bolt: selection, dragging, grips ----
        readonly HashSet<string> _sel = new();                  // ids of the selected elements (Ctrl+click in the preview)
        bool _downAdded, _dragging;                             // _dragging: keep the view frame while something is dragged
        double _dragAccum; BoltJob _dragBase, _dragTarget;      // state at drag start / state after a swap (green outline)
        BoltJob _gripBase; Layout _gripLay; double _gripAccum;
        int _distTag = -1;
        TextBlock _clampMarker;

        static readonly PartKind[] ElementKinds =
        {
            PartKind.PlainWasher, PartKind.LargeWasher, PartKind.SpringWasher,
            PartKind.HexNut, PartKind.ThinNut, PartKind.FlangeNut, PartKind.ChannelNut, PartKind.ChannelNutHolder
        };

        public BoltToolWindow()
        {
            InitializeComponent();
            _job = Settings.LoadLastJob();
            _hq.Tick += (s, e) => { _hq.Stop(); Render(true); };
            Preview.RotateRequested += OnRotate;
            PreviewKeyDown += OnKeys;
            Preview.PartDown += OnPartDown;
            Preview.PartDrag += OnPartDrag;
            Preview.PartUp += OnPartUp;
            Preview.DistanceClicked += OnDistanceClicked;
            Preview.GripDrag += OnGripDrag;
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
            foreach (var o in cb.Items)
                if (o is ComboBoxItem it && Equals(it.Tag, tag)) { cb.SelectedItem = it; return; }
            if (cb.Items.Count > 0 && cb.SelectedIndex < 0) cb.SelectedIndex = 0;
        }

        static T TagOf<T>(ComboBox cb, T fallback) => cb.SelectedItem is ComboBoxItem it && it.Tag is T t ? t : fallback;

        static double Num(string s, double fallback)
        {
            s = (s ?? "").Trim().Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
                ? v : fallback;
        }

        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        public void SelectTab(int i) { if (i >= 0 && i < Tabs.Items.Count) Tabs.SelectedIndex = i; }

        void Status(string s, params object[] args) => StatusText.Text = Loc.F(s, args);

        /// <summary>Sets a control's text without triggering the change handlers; leaves a box the user is typing in alone.</summary>
        void SetText(TextBox tb, string text)
        {
            if (tb.Text == text || tb.IsKeyboardFocusWithin) return;
            bool l = _loading; _loading = true; tb.Text = text; _loading = l;
        }

        // ================================================================ setup

        void FillStatic()
        {
            foreach (var box in new[] { KindBox, AsmBoltBox, AddHeadBox, AddNutBox }) box.Items.Clear();
            foreach (PartKind k in Enum.GetValues(typeof(PartKind))) KindBox.Items.Add(Item(PartKindInfo.DisplayName(k), k));
            foreach (PartKind k in new[] { PartKind.HexBolt, PartKind.HexBoltFullThread, PartKind.SocketCapScrew, PartKind.ThreadedRod })
                AsmBoltBox.Items.Add(Item(PartKindInfo.DisplayName(k), k));
            AddHeadBox.Items.Add(Item("＋ Add on the head side…", null));
            AddNutBox.Items.Add(Item("＋ Add on the nut side…", null));
            foreach (var k in ElementKinds)
            {
                AddHeadBox.Items.Add(Item(PartKindInfo.DisplayName(k), k));
                AddNutBox.Items.Add(Item(PartKindInfo.DisplayName(k), k));
            }
            AddHeadBox.SelectedIndex = 0; AddNutBox.SelectedIndex = 0;
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

        /// <summary>Choices for the block origin 0,0,0 – always a point on the axis.</summary>
        void FillBase()
        {
            var cur = _job.BasePoint;
            BaseBox.Items.Clear();
            if (_job.IsAssembly)
            {
                BaseBox.Items.Add(Item("Head top – middle of the head, outside", BasePoint.HeadTop));
                BaseBox.Items.Add(Item("Head-side contact face – where the head / its washers press on the part", BasePoint.ClampHead));
                BaseBox.Items.Add(Item("Nut-side contact face – where the nut-side washers / nut press on the part", BasePoint.ClampNut));
                BaseBox.Items.Add(Item("Bolt tip (thread end)", BasePoint.ThreadEnd));
                if (cur == BasePoint.UnderHead) cur = BasePoint.ClampHead;
                else if (cur is not (BasePoint.HeadTop or BasePoint.ClampHead or BasePoint.ClampNut or BasePoint.ThreadEnd)) cur = BasePoint.HeadTop;
            }
            else if (PartKindInfo.IsBolt(_job.Kind))
            {
                BaseBox.Items.Add(Item("Head top – middle of the head, outside", BasePoint.HeadTop));
                BaseBox.Items.Add(Item("Under head – bearing face", BasePoint.UnderHead));
                BaseBox.Items.Add(Item("Bolt tip (thread end)", BasePoint.ThreadEnd));
                if (cur == BasePoint.ClampHead) cur = BasePoint.UnderHead;
                else if (cur == BasePoint.ClampNut) cur = BasePoint.ThreadEnd;
                else if (cur is not (BasePoint.HeadTop or BasePoint.UnderHead or BasePoint.ThreadEnd)) cur = BasePoint.HeadTop;
            }
            else
            {
                BaseBox.Items.Add(Item("Bearing face (contact side)", BasePoint.BearingFace));
                BaseBox.Items.Add(Item("Outer face", BasePoint.OuterFace));
                if (cur != BasePoint.OuterFace) cur = BasePoint.BearingFace;
            }
            Select(BaseBox, cur);
            _job.BasePoint = TagOf(BaseBox, cur);
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
            job.EnsureStack();
            _sel.Clear();
            _dragBase = _dragTarget = _gripBase = null;
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
            RebuildStackUi();
            _loading = false;
            UpdateDependent();
            Render(true);
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
            j.Kind = TagOf(KindBox, j.Kind);
            if (!stdChanged) j.Size = TagOf(SizeBox, j.Size);
            j.Length = Math.Max(1, Num(LengthBox.Text, j.Length));
            j.Thread = ThDetail.IsChecked == true ? ThreadStyle.Detailed : ThNone.IsChecked == true ? ThreadStyle.None : ThreadStyle.Simplified;
            j.BasePoint = TagOf(BaseBox, j.BasePoint);

            j.IsAssembly = AsmCheck.IsChecked == true;
            j.ClampLength = Math.Max(0, Num(ClampBox.Text, j.ClampLength));
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
                j.Size = TagOf(SizeBox, j.Size);
            }
        }

        void UpdateDependent()
        {
            var j = _job; var r = j.Row;
            bool asm = j.IsAssembly;
            var lay = PartBuilder.ComputeLayout(j);
            AsmPanel.IsEnabled = asm;
            AsmPanel.Opacity = asm ? 1 : 0.5;
            LengthPanel.Visibility = (asm || PartKindInfo.HasLength(j.Kind)) ? Visibility.Visible : Visibility.Hidden;
            LengthBox.IsEnabled = true;
            LengthModeText.Text = asm
                ? Loc.T(j.AutoLength ? "Automatic length: edit clamped parts or washers; choose a value to switch to manual." :
                    "Manual length: choose another value, or enable automatic length on the Assembly tab.")
                : Loc.T("Choose a standard length or enter an exact length in mm.");

            // The length box always shows the current bolt length. With automatic length it follows what the
            // connection needs (it grows when elements are moved away from the head); choosing a value = manual.
            string lenText = F(asm && j.AutoLength ? lay.BoltLength : j.Length);
            if (LengthBox.Text != lenText && !LengthBox.IsKeyboardFocusWithin)
            {
                bool l = _loading; _loading = true; LengthBox.Text = lenText; _loading = l;
            }
            SetText(ClampBox, F(j.ClampLength));
            SetText(ProtMmBox, F(Math.Round(j.MinProtrusionPitches * r.P, 2)));
            SetText(ProtrusionBox, F(Math.Round(j.MinProtrusionPitches, 3)));
            if (AutoLenCheck.IsChecked != j.AutoLength) { bool l = _loading; _loading = true; AutoLenCheck.IsChecked = j.AutoLength; _loading = l; }
            if (_clampMarker != null) _clampMarker.Text = ClampText();

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
                case PartKind.FlangeNut: Dim("s", r.S); Dim("m", r.FlangeM); Dim("flange Ø", r.FlangeD); break;
                case PartKind.ChannelNutHolder:
                {
                    var cd = PartBuilder.ChannelDims(r);
                    Dim("nut L", cd.length); Dim("nut W", cd.width); Dim("nut t", cd.t);
                    Dim("plate", PartBuilder.HolderPlate); Dim("lip", PartBuilder.HolderLip); break;
                }
                case PartKind.ChannelNut:
                {
                    var cd = PartBuilder.ChannelDims(r);
                    Dim("length", cd.length); Dim("width", cd.width); Dim("t", cd.t); break;
                }
                case PartKind.PlainWasher: Dim("d1", r.WasherD1); Dim("d2", r.WasherD2); Dim("h", r.WasherH); break;
                case PartKind.LargeWasher: Dim("d1", r.WasherD1); Dim("d2", r.LargeD2); Dim("h", r.LargeH); break;
                case PartKind.SpringWasher: Dim("d1", r.SpringD1); Dim("d2", r.SpringD2); Dim("s", r.SpringT); break;
                default: Dim("s", r.S); Dim("k", r.K); break;
            }
            if (PartKindInfo.HasLength(shownKind))
                Dim("thread b", Math.Round(StandardTable.ThreadLength(shownKind, r, asm ? lay.BoltLength : j.Length), 1));

            // assembly summary + the small order / distance diagrams
            var diagVis = asm ? Visibility.Visible : Visibility.Collapsed;
            DiagramTitle2.Visibility = DiagramBorder2.Visibility = SizesText.Visibility = diagVis;
            if (asm)
            {
                var parts = string.Join(" + ", lay.Parts.Select(p => p.Role == "bolt"
                    ? $"{PartKindInfo.Code(p.Kind)} {j.Size}×{F(p.Length)}"
                    : PartKindInfo.Code(p.Kind)));
                string len = Loc.F("Bolt length: {0} mm  ({1})", F(lay.BoltLength), Loc.T(j.AutoLength ? "auto" : "manual"));
                string outTxt = Loc.F("sticks out {0} mm beyond the last element", F(Math.Round(lay.Protrusion, 2)));
                AsmResult.Text = len + "  ·  " + outTxt + "\n" + parts + (lay.Warning != null ? $"\n⚠ {lay.Warning}" : "");
                AsmResult.Foreground = lay.Warning != null ? Brushes.Firebrick : (Brush)FindResource("Ink");
                StackDiagram1.Set(lay, r, j, _sel);
                StackDiagram2.Set(lay, r, j, _sel);
                var sizes = lay.Parts.Where(p => p.Item != null)
                    .Select(p => $"{PartKindInfo.Code(p.Kind)} {F(p.Thickness)}" + (PartBuilder.StandardOuterD(p.Kind, r) > 0 ? $" × Ø{F(p.OuterR * 2)}" : ""));
                SizesText.Text = len + "  ·  " + Loc.F("clamp {0} mm", F(j.ClampLength)) + "  ·  " + outTxt +
                                 (lay.Parts.Count > 1 ? "\n" + Loc.T("Elements (thickness × outer Ø):") + " " + string.Join(", ", sizes) : "");
            }
            UpdateBtn.Visibility = _editId.IsNull ? Visibility.Collapsed : Visibility.Visible;
        }

        // ================================================================ element list (Assembly tab)

        string ClampText() => Loc.F("▦ Clamped parts – {0} mm", F(_job.ClampLength));

        /// <summary>Rebuilds the list of washers / nuts. The order of the rows is the order on the bolt.</summary>
        void RebuildStackUi()
        {
            StackList.Children.Clear();
            _clampMarker = null;
            _job.EnsureStack();
            var st = _job.Stack; int hc = _job.HeadCount;

            var head = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            SetColumns(head);
            void H(string text, int col, string tip)
            {
                var t = new TextBlock
                {
                    Text = Loc.T(text), FontSize = 11, Foreground = (Brush)FindResource("Muted"),
                    HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center, ToolTip = Loc.T(tip)
                };
                Grid.SetColumn(t, col); head.Children.Add(t);
            }
            H("Element", 0, "Kind of washer or nut. Change it with the list in each row.");
            H("T", 1, "Thickness in mm. Clear the box to use the standard thickness.");
            H("Ø", 2, "Outer diameter in mm (washers, spring washer, flange). Clear the box to use the standard.");
            H("gap", 3, "Free space before this element in mm. The first nut-side gap starts after the solid clamped parts; plate thickness is set separately.");
            StackList.Children.Add(head);

            StackList.Children.Add(Marker(Loc.T("● Bolt head"), false));
            for (int i = 0; i < st.Count; i++)
            {
                if (i == hc) StackList.Children.Add(Marker(ClampText(), true));
                StackList.Children.Add(StackRow(i));
            }
            if (hc == st.Count) StackList.Children.Add(Marker(ClampText(), true));
            StackList.Children.Add(Marker(Loc.T("● Bolt end"), false));
        }

        static void SetColumns(Grid g)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foreach (double w in new[] { 46.0, 46, 46, 24, 24, 24 })
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        }

        FrameworkElement Marker(string text, bool clamp)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = 11.5, FontWeight = clamp ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = clamp ? new SolidColorBrush(Color.FromRgb(0x8A, 0x6D, 0x1F)) : (Brush)FindResource("Muted")
            };
            if (clamp) _clampMarker = tb;
            return new Border
            {
                Child = tb, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 2, 0, 2), CornerRadius = new CornerRadius(5),
                Background = clamp ? new SolidColorBrush(Color.FromRgb(0xF7, 0xF0, 0xDC)) : Brushes.Transparent
            };
        }

        /// <summary>Small number box that commits on Enter or when the focus leaves it.</summary>
        TextBox NumberBox(string text, Action<string> commit, int column, bool enabled = true)
        {
            var tb = new TextBox
            {
                Text = text, Height = 26, FontSize = 12, Margin = new Thickness(3, 0, 0, 0), Padding = new Thickness(3, 0, 3, 0),
                IsEnabled = enabled, HorizontalContentAlignment = HorizontalAlignment.Right,
                ToolTip = Loc.T(column switch
                {
                    1 => "Thickness in mm. Clear the box to use the standard thickness.",
                    2 => "Outer diameter in mm (washers, spring washer, flange). Clear the box to use the standard.",
                    3 => "Free space before this element (mm). For the first nut-side element this is empty space AFTER the clamped plates; edit Clamp length separately.",
                    _ => ""
                })
            };
            string last = text;
            void Do()
            {
                if (_loading || tb.Text == last) return;
                commit(tb.Text);
                last = tb.Text;
            }
            tb.LostFocus += (s, e) => Do();
            tb.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Do(); e.Handled = true; } };
            Grid.SetColumn(tb, column);
            return tb;
        }

        Button RowButton(string text, string tip, Action click, int column, bool enabled = true)
        {
            var b = new Button
            {
                Content = text, Height = 24, Width = 22, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), FontSize = 11,
                IsEnabled = enabled, ToolTip = Loc.T(tip), Cursor = Cursors.Hand, Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD3, 0xDF))
            };
            b.Click += (s, e) => click();
            Grid.SetColumn(b, column);
            return b;
        }

        FrameworkElement StackRow(int index)
        {
            var it = _job.Stack[index];
            var row = _job.Row;
            int count = _job.Stack.Count;
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            SetColumns(g);
            if (_sel.Contains(it.Id)) g.Background = new SolidColorBrush(Color.FromRgb(0xE2, 0xEC, 0xFD));

            var kind = new ComboBox { Height = 26, FontSize = 12, Padding = new Thickness(6, 2, 2, 2) };
            foreach (var k in ElementKinds) kind.Items.Add(Item(PartKindInfo.DisplayName(k), k));
            Select(kind, it.Kind);
            kind.SelectionChanged += (s, e) =>
            {
                if (_loading) return;
                var k = TagOf(kind, it.Kind);
                if (k == it.Kind) return;
                it.Kind = k; it.Thickness = 0; it.OuterD = 0;       // another kind -> back to its standard size
                StackChanged(true);
            };
            g.Children.Add(kind);

            TextBox tT = null, tD = null, tG = null;
            tT = NumberBox(F(PartBuilder.Thickness(it.Kind, row, 0, it)), txt =>
            {
                double std = PartBuilder.StandardThickness(it.Kind, row), v = Num(txt, double.NaN);
                it.Thickness = double.IsNaN(v) || v <= 0 || Math.Abs(v - std) < 1e-9 ? 0 : v;
                tT.Text = F(PartBuilder.Thickness(it.Kind, row, 0, it));
                StackChanged(false);
            }, 1);
            g.Children.Add(tT);

            bool hasD = PartBuilder.StandardOuterD(it.Kind, row) > 0;
            tD = NumberBox(hasD ? F(PartBuilder.OuterD(it.Kind, row, it)) : "–", txt =>
            {
                double std = PartBuilder.StandardOuterD(it.Kind, row), v = Num(txt, double.NaN);
                it.OuterD = double.IsNaN(v) || v <= row.D + 1 || Math.Abs(v - std) < 1e-9 ? 0 : v;
                tD.Text = F(PartBuilder.OuterD(it.Kind, row, it));
                StackChanged(false);
            }, 2, hasD);
            g.Children.Add(tD);

            tG = NumberBox(F(_job.GapBefore(index)), txt =>
            {
                int i = _job.Stack.IndexOf(it);
                if (i < 0) return;
                _job.SetGapBefore(i, Math.Max(0, Num(txt, _job.GapBefore(i))));
                tG.Text = F(_job.GapBefore(i));
                StackChanged(false);
            }, 3);
            g.Children.Add(tG);

            g.Children.Add(RowButton("▲", "Move toward the bolt head", () => MoveItem(it, -1), 4, index > 0 || it.Side == StackSide.Nut));
            g.Children.Add(RowButton("▼", "Move toward the bolt end", () => MoveItem(it, +1), 5, index < count - 1 || it.Side == StackSide.Head));
            g.Children.Add(RowButton("✕", "Remove this element", () => RemoveItem(it), 6));
            return g;
        }

        /// <summary>Something in the element list changed: refresh texts, diagrams and the preview.</summary>
        void StackChanged(bool rebuild)
        {
            _job.EnsureStack();
            if (rebuild) RebuildStackUi();
            UpdateDependent();
            Render(true);
        }

        void MoveItem(StackItem it, int dir)
        {
            var st = _job.Stack; int i = st.IndexOf(it); if (i < 0) return;
            int hc = _job.HeadCount;
            if (dir < 0)
            {
                if (i == hc && it.Side == StackSide.Nut) it.Side = StackSide.Head;          // crosses the clamped parts
                else if (i > 0) { st[i] = st[i - 1]; st[i - 1] = it; }
            }
            else
            {
                if (i == hc - 1 && it.Side == StackSide.Head) it.Side = StackSide.Nut;      // crosses the clamped parts
                else if (i < st.Count - 1) { st[i] = st[i + 1]; st[i + 1] = it; }
            }
            StackChanged(true);
        }

        void RemoveItem(StackItem it)
        {
            _job.Stack.Remove(it);
            _sel.Remove(it.Id);
            StackChanged(true);
        }

        static bool IsUsualOrder(BoltJob job)
        {
            var sorted = job.Clone();
            StackMover.SortUsual(sorted);
            return sorted.Stack.Select(i => i.Id).SequenceEqual(job.Stack.Select(i => i.Id));
        }

        void OnAddElement(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || sender is not ComboBox cb || cb.SelectedIndex <= 0) return;
            var kind = TagOf(cb, PartKind.PlainWasher);
            _loading = true; cb.SelectedIndex = 0; _loading = false;
            _job.EnsureStack();
            bool usual = IsUsualOrder(_job);
            var item = new StackItem { Kind = kind, Side = ReferenceEquals(cb, AddHeadBox) ? StackSide.Head : StackSide.Nut };
            if (item.Side == StackSide.Head) _job.Stack.Insert(_job.HeadCount, item); else _job.Stack.Add(item);
            _job.EnsureStack();
            if (usual) StackMover.SortUsual(_job);      // keep the usual order; a custom order is left as it is
            StackChanged(true);
        }

        void OnStackPreset(object sender, RoutedEventArgs e)
        {
            string tag = (string)((Button)sender).Tag;
            bool rod = _job.BoltKind == PartKind.ThreadedRod;
            switch (tag)
            {
                case "std": _job.Stack = BoltJob.BuildStack(WasherChoice.Plain, WasherChoice.Plain, SpringPosition.UnderNut, PartKind.HexNut, 1, rod); break;
                case "dbl": _job.Stack = BoltJob.BuildStack(WasherChoice.Plain, WasherChoice.Plain, SpringPosition.None, PartKind.HexNut, 2, rod); break;
                case "tap": _job.Stack = BoltJob.BuildStack(WasherChoice.Plain, WasherChoice.None, SpringPosition.UnderHead, PartKind.HexNut, 0, false); break;
                case "chan":
                    // bolt + spring washer + large washer | bracket + channel lip | channel nut
                    _job.Stack = new List<StackItem>
                    {
                        new StackItem { Kind = PartKind.SpringWasher, Side = StackSide.Head },
                        new StackItem { Kind = PartKind.LargeWasher, Side = StackSide.Head },
                        new StackItem { Kind = PartKind.ChannelNutHolder, Side = StackSide.Nut },
                    };
                    _job.EnsureStack();
                    break;
                case "both":
                    _job.Stack = BoltJob.BuildStack(WasherChoice.Plain, WasherChoice.Plain, SpringPosition.UnderNut, PartKind.HexNut, 1, rod);
                    _job.Stack.Insert(0, new StackItem { Kind = PartKind.SpringWasher, Side = StackSide.Head });
                    _job.EnsureStack();
                    StackMover.SortUsual(_job);
                    break;
                default: StackMover.SortUsual(_job); break;
            }
            _sel.Clear();
            StackChanged(true);
        }

        /// <summary>"Close all gaps": every element touches the one before it again (the clamp length stays).</summary>
        void OnResetOffsets(object sender, RoutedEventArgs e)
        {
            _job.EnsureStack();
            foreach (var it in _job.Stack) it.Gap = 0;
            StackChanged(true);
        }

        // ================================================================ preview

        void Render(bool quality)
        {
            try
            {
                var j = _job; var r = j.Row;
                var lay = PartBuilder.ComputeLayout(j);
                _lay = lay;
                string key = string.Join("|", j.Standard, j.Size, j.Thread,
                    string.Join(";", lay.Parts.Select(p => $"{p.Kind}{p.X:0.###}/{p.Length:0.###}/{p.Thickness:0.###}/{p.OuterR:0.###}/{p.Item?.Side}")));
                string viewKey = $"{j.Azimuth:0.##}|{j.Elevation:0.##}|{j.Spin:0.##}";
                if (key != _meshKey) { _mesh = PartBuilder.BuildLayout(lay, r, j.Thread); _meshKey = key; Preview.ClearMeasures(); }
                if (viewKey != _viewKey) { _viewKey = viewKey; Preview.ClearMeasures(); }      // measured points belong to one view
                var dwg = HiddenLineRenderer.Render(_mesh, j.Azimuth, j.Elevation, j.Spin, j.HiddenLines, quality);
                Point Px(double x) { var p = dwg.Project(new Vec3(x, 0, 0)); return new Point(p.X, p.Y); }
                var ax = dwg.Project(new Vec3(1, 0, 0)); var o0 = dwg.Project(new Vec3(0, 0, 0));
                var axis = new Vector(ax.X - o0.X, ax.Y - o0.Y);

                Preview.ShowWipeouts = j.Wipeout && j.Output == OutputMode.View2D && ShowWipeToggle.IsChecked == true;
                Preview.Markers.Clear(); Preview.Grips.Clear();
                string role = j.IsAssembly ? BlockFactory.RoleAssembly : BlockFactory.RoleSingle;
                Preview.Markers.Add((Px(BlockFactory.LayoutBaseX(j, role, lay)), "0,0,0"));
                if (axis.Length > 0.05)
                {
                    var perp = new Vector(-axis.Y, axis.X) / axis.Length;
                    foreach (var (kind, x, radial) in LengthGripOverrule.GripDefs(j.Clone().WithRole(role)))
                    {
                        var gp = Px(x) + perp * radial;
                        Preview.Grips.Add((gp, axis, kind));
                    }
                }

                // selectable elements, selection highlight, distances and the landing outline of a swap
                Preview.SelectableGroups.Clear(); Preview.Highlight.Clear(); Preview.Dims.Clear(); Preview.Ghosts.Clear();
                _sel.RemoveWhere(id => !lay.Parts.Any(p => p.Role == id));
                Preview.ShowPartHint = j.IsAssembly;
                if (j.IsAssembly)
                    for (int g = 0; g < _mesh.GroupOwner.Count; g++)
                    {
                        var part = lay.Parts[_mesh.GroupOwner[g]];
                        if (part.Item == null) continue;
                        Preview.SelectableGroups.Add(g);
                        if (_sel.Contains(part.Role)) Preview.Highlight.Add(g);
                    }
                var chain = SelectedChain(lay);
                for (int k = 0; k < chain.Count; k++)
                    if (k == 0 || chain[k].gap > 1e-6)
                        Preview.Dims.Add((Px(chain[k].start), Px(chain[k].part.X), F(Math.Round(chain[k].gap, 2)) + " mm", k));

                if (_dragTarget != null && axis.Length > 0.05)
                {
                    var tl = PartBuilder.ComputeLayout(_dragTarget);
                    for (int g = 0; g < _mesh.GroupOwner.Count; g++)
                    {
                        var part = lay.Parts[_mesh.GroupOwner[g]];
                        if (part.Item == null || !_sel.Contains(part.Role)) continue;
                        var tp = tl.Parts.FirstOrDefault(p => p.Role == part.Role);
                        if (tp == null || !dwg.Wipeouts.TryGetValue(g, out var hull)) continue;
                        double dx = tp.X - part.X;
                        Preview.Ghosts.Add(hull.Select(p => new P2(p.X + axis.X * dx, p.Y + axis.Y * dx)).ToList());
                    }
                }

                Preview.Caption = Loc.T(j.Output == OutputMode.View2D ? "2D block" : j.Output == OutputMode.Mesh3D ? "3D mesh block (preview)" : "3D solid block (preview)")
                                  + $"  ·  {j.ViewCode()}";
                Preview.SetDrawing(dwg, _dragging);
                InfoText.Text = $"{Loc.T(j.IsAssembly ? "Assembly" : PartKindInfo.DisplayName(j.Kind))}  {j.Size}" +
                                (lay.BoltLength > 0 ? $"×{F(lay.BoltLength)}" : "") +
                                $"   ·   az {j.Azimuth:0.#}°  el {j.Elevation:0.#}°   ·   " + Loc.F("{0} triangles, {1} polylines", _mesh.TriangleCount, dwg.Lines.Count);
                if (quality) Track();
            }
            catch (Exception ex) { Status("Preview error: {0}", ex.Message); }
        }

        /// <summary>
        /// The selected elements from the head outward, each with the free distance in front of it:
        /// the first one measured from the underside of the head, the others from the previous selected element.
        /// </summary>
        List<(PlacedPart part, double start, double gap)> SelectedChain(Layout lay)
        {
            // Each label = free distance in front of a selected element, measured from the element just before it
            // (or from the underside of the head). For the first nut-side element this includes the clamped parts.
            var list = new List<(PlacedPart, double, double)>();
            if (lay == null) return list;
            double prev = lay.UnderHead;
            foreach (var p in lay.Parts.Where(p => p.Item != null).OrderBy(p => p.X))
            {
                if (_sel.Contains(p.Role)) list.Add((p, prev, p.X - prev));
                prev = p.X + p.Thickness;
            }
            return list;
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
            Render(final);
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
            // An explicit dropdown choice locks the chosen length, even if the choice equals
            // the current automatic value. Typing another value also switches to manual.
            // Merely tabbing out of an untouched text box leaves auto mode enabled.
            if (ReferenceEquals(sender, LengthBox) && _job.IsAssembly && _job.AutoLength)
            {
                double current = PartBuilder.ComputeLayout(_job).BoltLength;
                double requested = Num(LengthBox.Text, current);
                bool chosenFromList = e is SelectionChangedEventArgs selection && selection.AddedItems.Count > 0;
                if (chosenFromList || Math.Abs(requested - current) > 1e-6)
                {
                    _loading = true; AutoLenCheck.IsChecked = false; _loading = false;
                    Status("Bolt length is now manual. Tick “Choose standard length automatically” on the Assembly tab to go back.");
                }
            }
            var oldKind = _job.Kind; bool oldAsm = _job.IsAssembly;
            ReadControls();
            if (oldKind != _job.Kind || oldAsm != _job.IsAssembly) { _loading = true; FillBase(); _loading = false; }
            UpdateDependent();
            Render(true);
        }

        void OnSizeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            ReadControls();
            // take the most used length for the new size; element sizes follow the new size
            _job.Length = _job.Row.DefaultLength;
            _loading = true; LengthBox.Text = F(_job.Length); Select(TqSizeBox, _job.Size); _loading = false;
            RebuildStackUi();
            UpdateDependent();
            Render(true);
            OnTorque(null, null);
        }

        void OnAsmBoltChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var k = TagOf(AsmBoltBox, PartKind.HexBolt);
            _loading = true; Select(KindBox, k); _loading = false;
            OnChanged(sender, e);
        }

        void OnEnterKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (sender == AzBox || sender == ElBox || sender == SpinBox) OnAngleBox(sender, e);
            else if (sender == TqMuG || sender == TqMuK || sender == TqUtil) OnTorque(sender, e);
            else if (sender == ProtMmBox) OnProtMm(sender, e);
            else OnChanged(sender, e);
        }

        /// <summary>Protrusion typed in millimetres -> converted to thread pitches (that is what is stored).</summary>
        void OnProtMm(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            double p = _job.Row.P;
            double mm = Math.Max(0, Num(ProtMmBox.Text, _job.MinProtrusionPitches * p));
            _loading = true; ProtrusionBox.Text = F(Math.Round(mm / p, 3)); _loading = false;
            OnChanged(ProtrusionBox, e);
        }

        void OnSlider(object sender, RoutedEventArgs e)
        {
            if (_loading || !IsInitialized) return;
            _job.Azimuth = Math.Round(AzSlider.Value, 1);
            _job.Elevation = Math.Round(ElSlider.Value, 1);
            _job.Spin = Math.Round(SpinSlider.Value, 1);
            SetAngles();
            Render(false);
            _hq.Stop(); _hq.Start();
        }

        void OnAngleBox(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _job.Azimuth = Num(AzBox.Text, _job.Azimuth);
            _job.Elevation = Math.Clamp(Num(ElBox.Text, _job.Elevation), -90, 90);
            _job.Spin = Num(SpinBox.Text, _job.Spin);
            SetAngles();
            Render(true);
        }

        void OnPreset(object sender, RoutedEventArgs e)
        {
            var p = ((string)((Button)sender).Tag).Split(',');
            _job.Azimuth = Num(p[0], 0); _job.Elevation = Num(p[1], 0);
            SetAngles();
            Render(true);
        }

        void OnScaleChip(object sender, RoutedEventArgs e)
        {
            ScaleBox.Text = (string)((Button)sender).Tag;
            OnChanged(sender, e);
        }

        void OnShowWipe(object sender, RoutedEventArgs e) => Render(true);

        // Preview edits never change the user's camera. This is the explicit way to fit
        // newly added parts into the current screen.
        void OnFitView(object sender, RoutedEventArgs e) => Preview.Refit(true);

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
            Preview.Refit(true);
            Status("Settings reset to defaults.");
        }

        /// <summary>Saves the preview exactly as shown (with dimensions) as a JPG file.</summary>
        void OnSnapshot(object sender, RoutedEventArgs e)
        {
            try
            {
                string name = $"BoltTool_{_job.Size}" + (_lay != null && _lay.BoltLength > 0 ? "x" + F(_lay.BoltLength) : "") + "_" + _job.ViewCode() + ".jpg";
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "JPEG image (*.jpg)|*.jpg", FileName = name, DefaultExt = ".jpg", AddExtension = true,
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                };
                if (dlg.ShowDialog(this) != true) return;
                var enc = new JpegBitmapEncoder { QualityLevel = 95 };
                enc.Frames.Add(BitmapFrame.Create(Preview.Capture()));
                using (var fs = File.Create(dlg.FileName)) enc.Save(fs);
                Status("Saved {0}", dlg.FileName);
            }
            catch (Exception ex) { Status("Could not save the picture: {0}", ex.Message); }
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
                    TqMethod = TqMethodBox.SelectedIndex,
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
                Pick(TqMethodBox, u.TqMethod);
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
                Render(true);
            }
            catch { _loading = false; }
        }

        // ================================================================ moving elements in the preview

        string RoleOfGroup(int g) =>
            g >= 0 && _lay != null && _mesh != null && g < _mesh.GroupOwner.Count ? _lay.Parts[_mesh.GroupOwner[g]].Role : null;

        void ApplyStack(BoltJob src)
        {
            _job.Stack = src.Stack;
            _job.ClampLength = src.ClampLength;
        }

        void OnPartDown(int group)
        {
            HideDistBox();
            _dragBase = _dragTarget = null;
            var role = RoleOfGroup(group);
            bool element = role != null && _job.Stack != null && _job.Stack.Any(i => i.Id == role);
            if (!element)
            {
                _downAdded = false;
                if (_sel.Count > 0) { _sel.Clear(); RebuildStackUi(); UpdateDependent(); Render(true); }
                return;
            }
            _downAdded = _sel.Add(role);
            _dragAccum = 0;
            _dragBase = _job.Clone();
            RebuildStackUi();
            UpdateDependent();
            Render(true);
        }

        /// <summary>
        /// Ctrl+drag: the selected elements slide along the bolt and stop at contact. Pulled further than half of
        /// the neighbour they touch, a green outline shows where they would land; releasing swaps the places.
        /// </summary>
        void OnPartDrag(double mm, bool final)
        {
            if (_dragBase == null) return;
            _dragAccum += mm;
            double raw = Math.Round(_dragAccum * 2) / 2;            // 0.5 mm steps
            StackMover.Move(_dragBase, _sel, raw, out var live, out var target);
            if (final && target != null) { ApplyStack(target); _dragTarget = null; }
            else { ApplyStack(live); _dragTarget = final ? null : target; }
            _dragging = !final;
            if (final) { _dragBase = null; RebuildStackUi(); }
            UpdateDependent();
            Render(final);
        }

        void OnPartUp(int group, bool moved)
        {
            _dragging = false;
            _dragBase = null;
            bool hadGhost = _dragTarget != null;
            _dragTarget = null;
            var role = RoleOfGroup(group);
            // Ctrl+click on an already selected element takes it out of the selection
            bool removed = !moved && !_downAdded && role != null && _sel.Remove(role);
            if (removed || hadGhost) { RebuildStackUi(); UpdateDependent(); Render(true); }
        }

        /// <summary>Dragging a blue ▶ in the preview: "len" = bolt length only, "clamp" = the nut side with the bolt following.</summary>
        void OnGripDrag(string kind, double mm, bool final)
        {
            if (_gripBase == null)
            {
                _gripBase = _job.Clone();
                _gripLay = PartBuilder.ComputeLayout(_gripBase);
                _gripAccum = 0;
            }
            _gripAccum += mm;
            if (kind == "len")
            {
                double baseLen = _job.IsAssembly ? _gripLay.BoltLength : _gripBase.Length;
                double len = LengthGripOverrule.SnapLength(_gripBase, baseLen + _gripAccum);
                _job.Length = len;
                if (_job.IsAssembly)
                {
                    _job.AutoLength = false;                               // the user chose a length
                    if (_job.StickToBoltEnd) _job.ClampLength = Math.Max(0, _gripBase.ClampLength + (len - baseLen));
                }
            }
            else
            {
                _job.ClampLength = Math.Max(0, Math.Round((_gripBase.ClampLength + _gripAccum) * 2) / 2);
                if (!_job.AutoLength)
                {
                    // manual length: the bolt follows so the protrusion stays about the same
                    double need = PartBuilder.ComputeLayout(_job).Required + Math.Max(0, _gripLay.BoltLength - _gripLay.Required);
                    _job.Length = _job.SnapLengthToStandard ? _job.Table.NextStandardLength(need) : Math.Ceiling(need);
                }
            }
            _dragging = !final;
            if (final) { _gripBase = null; RebuildStackUi(); }
            UpdateDependent();
            Render(final);
        }

        void OnDistanceClicked(int tag, Point topLeft)
        {
            var chain = SelectedChain(_lay);
            if (tag < 0 || tag >= chain.Count) return;
            _distTag = tag;
            DistBox.Margin = new Thickness(Math.Max(0, topLeft.X), Math.Max(0, topLeft.Y), 0, 0);
            DistBox.Text = F(Math.Round(chain[tag].gap, 2));
            DistBox.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(() => { DistBox.Focus(); DistBox.SelectAll(); }), DispatcherPriority.Input);
        }

        void HideDistBox() { DistBox.Visibility = Visibility.Collapsed; _distTag = -1; }

        /// <summary>Unselects all elements and removes measurements (Esc, also from inside the distance field).</summary>
        void ClearSelection()
        {
            HideDistBox();
            _dragBase = _dragTarget = null;
            Preview.ClearMeasures();
            if (_sel.Count == 0) return;
            _sel.Clear();
            Preview.Focus();
            RebuildStackUi();
            UpdateDependent();
            Render(true);
        }

        void OnDistLost(object sender, RoutedEventArgs e) => HideDistBox();

        /// <summary>A typed distance moves that element and every selected element behind it (stops at contact).</summary>
        void OnDistKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { ClearSelection(); e.Handled = true; return; }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var chain = SelectedChain(_lay);
            int tag = _distTag;
            if (tag >= 0 && tag < chain.Count)
            {
                double cur = chain[tag].gap;
                double target = Math.Max(0, Num(DistBox.Text, cur));
                // Move the chosen part and selected followers on the same side only.
                // A red distance may span the solid plates; selecting parts on both sides
                // must not turn an editable label into a silent no-op.
                var side = chain[tag].part.Item.Side;
                var ids = new HashSet<string>(chain.Skip(tag)
                    .Where(c => c.part.Item.Side == side).Select(c => c.part.Role));
                StackMover.Move(_job, ids, target - cur, out var live, out _);
                ApplyStack(live);
                RebuildStackUi();
                UpdateDependent();
                Render(true);
            }
            HideDistBox();
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

            // Esc: close the distance field, drop the selection and the measurements (an open drop-down keeps its own Esc)
            if (e.Key == Key.Escape && !(Keyboard.FocusedElement is ComboBoxItem) &&
                (_sel.Count > 0 || DistBox.Visibility == Visibility.Visible || Preview.HasMeasures))
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
            int iC = TqClassBox.SelectedIndex, iF = TqFinishBox.SelectedIndex, iL = TqLubeBox.SelectedIndex, iM = TqMatBox.SelectedIndex, iMe = TqMethodBox.SelectedIndex;
            foreach (var box in new[] { TqMethodBox, TqClassBox, TqFinishBox, TqLubeBox, TqMatBox }) box.Items.Clear();
            TqMethodBox.Items.Add(Item("VDI 2230 – Europe / ISO practice", TorqueMethod.Vdi2230));
            TqMethodBox.Items.Add(Item("JIS B 1083 – simplified coefficient estimate (Japan)", TorqueMethod.JisTorqueCoefficient));
            foreach (var c in TorqueCalculator.Classes) TqClassBox.Items.Add(Item(c.Name, c));
            foreach (var f in TorqueCalculator.Finishes) TqFinishBox.Items.Add(Item(f.Name, f));
            foreach (var l in TorqueCalculator.Lubrications) TqLubeBox.Items.Add(Item(l.Name, l));
            foreach (var m in TorqueCalculator.Materials) TqMatBox.Items.Add(Item(m.Name, m));
            if (first)
            {
                // a JIS job starts with the Japanese method, an ISO job with VDI 2230
                TqMethodBox.SelectedIndex = _job.Standard == FastenerStandard.JIS ? 1 : 0;
                TqClassBox.SelectedIndex = TorqueCalculator.DefaultClassIndex;
                TqFinishBox.SelectedIndex = 2; TqLubeBox.SelectedIndex = 1; TqMatBox.SelectedIndex = 0;
                TqUtil.Text = F(TorqueCalculator.DefaultUtilisation(TagOf(TqMethodBox, TorqueMethod.Vdi2230)));
                TqWasher.IsChecked = true;
                _loading = false;
                OnTorqueMu(null, null);
            }
            else
            {
                // language switch: keep the selection and the friction values the user may have typed
                TqMethodBox.SelectedIndex = iMe; TqClassBox.SelectedIndex = iC; TqFinishBox.SelectedIndex = iF;
                TqLubeBox.SelectedIndex = iL; TqMatBox.SelectedIndex = iM;
                _loading = false;
                OnTorque(null, null);
            }
        }

        /// <summary>Another method -> its usual yield utilisation (90 % for VDI 2230, 70 % for the JIS method).</summary>
        void OnTorqueMethod(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _loading = true;
            TqUtil.Text = F(TorqueCalculator.DefaultUtilisation(TagOf(TqMethodBox, TorqueMethod.Vdi2230)));
            _loading = false;
            OnTorque(sender, e);
        }

        void OnTorqueMu(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var f = TagOf<Finish>(TqFinishBox, null); var l = TagOf<Lubrication>(TqLubeBox, null); var m = TagOf<ClampMaterial>(TqMatBox, null);
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
            var sc = TagOf<StrengthClass>(TqClassBox, null); var m = TagOf<ClampMaterial>(TqMatBox, null);
            if (sc == null || m == null) return;
            var method = TagOf(TqMethodBox, TorqueMethod.Vdi2230);
            double muG = Math.Clamp(Num(TqMuG.Text, 0.12), 0.02, 0.6), muK = Math.Clamp(Num(TqMuK.Text, 0.12), 0.02, 0.6);
            double util = Math.Clamp(Num(TqUtil.Text, 90), 10, 100) / 100.0;
            bool washer = TqWasher.IsChecked == true;
            var table = _job.Table;
            var kind = _job.IsAssembly ? _job.BoltKind : (PartKindInfo.IsBolt(_job.Kind) ? _job.Kind : PartKind.HexBolt);

            TorqueResult Calc(SizeRow r)
            {
                var (dW, dh, pd) = TorqueCalculator.Bearing(r, kind, washer);
                return TorqueCalculator.Calculate(r, sc, muG, muK, util, dW, dh, m.PG, pd, method);
            }

            var row = table.Get(TagOf(TqSizeBox, _job.Size));
            var res = Calc(row);
            string N0(double v) => v.ToString("0", CultureInfo.InvariantCulture);
            string N1(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
            TqTorqueLabel.Text = Loc.T(m.Conductor ? "Mechanical estimate – NOT a terminal torque" : "Tightening torque (estimate)");
            TqTorque.Text = $"{N1(res.TorqueNm)} N·m";
            TqRange.Text = Loc.F("range {0} – {1} N·m", N1(res.TorqueMinNm), N1(res.TorqueMaxNm));
            TqPreload.Text = res.PreloadKN.ToString("0.0", CultureInfo.InvariantCulture) + " kN";
            TqAs.Text = $"A_s {N1(res.As)} mm²  ·  Rp {N0(sc.Rp(row.D))} MPa  ·  K = {res.K.ToString("0.00", CultureInfo.InvariantCulture)}";
            TqClassInfo.Text = Loc.F("Class {0}: ", sc.Name) + Loc.T(sc.Usage) + " " + Loc.T("(Typical practice – follow the drawing or specification of the project.)");
            TqPressure.Text = res.PressureOk
                ? Loc.F("✓ Surface pressure under head {0} MPa ≤ {1} MPa allowed for {2}.", N0(res.SurfacePressure), N0(res.PG), Loc.T(m.Name))
                : Loc.F("⚠ Surface pressure {0} MPa exceeds {1} MPa for {2} – use a (large / hardened) washer or lower class.", N0(res.SurfacePressure), N0(res.PG), Loc.T(m.Name));
            TqPressureBorder.Background = new SolidColorBrush(res.PressureOk ? Color.FromRgb(0xEC, 0xFD, 0xF3) : Color.FromRgb(0xFE, 0xF2, 0xF2));
            TqNoteBorder.Visibility = m.Conductor ? Visibility.Visible : Visibility.Collapsed;
            if (m.Conductor)
                TqNote.Text = Loc.T("Electrical terminal / busbar: this is ONLY a mechanical bolt estimate, NOT a tightening instruction. Follow the equipment or terminal manufacturer's specified torque, contact treatment, bolt/washer hardware and maintenance instructions. The electrical connection may be damaged even below the calculated bolt torque.");
            TqGrid.ItemsSource = table.Rows.Select(Calc).ToList();
        }
    }

    static class JobExt
    {
        public static BoltJob WithRole(this BoltJob j, string role) { j.Role = role; return j; }
    }
}
