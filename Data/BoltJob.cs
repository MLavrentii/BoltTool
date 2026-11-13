using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoltToolApp.Data
{
    public enum ThreadStyle { None, Simplified, Detailed }
    public enum OutputMode { View2D, Mesh3D, Solid3D }
    public enum BasePoint { HeadTop, UnderHead, ThreadEnd, BearingFace, OuterFace, Center, ClampHead, ClampNut }
    public enum StackSide { Head, Nut }

    /// <summary>One washer / nut on the bolt. The list order is the physical order from the head to the bolt end.</summary>
    public class StackItem
    {
        public string Id { get; set; }
        public PartKind Kind { get; set; } = PartKind.PlainWasher;
        public StackSide Side { get; set; } = StackSide.Nut;   // which side of the clamped parts
        /// <summary>Free axial space immediately before this element (mm). The first nut-side
        /// gap is the space AFTER the clamped material; ClampLength stores the solid material
        /// thickness separately, so editing gaps never changes the plate thickness.</summary>
        public double Gap { get; set; }
        public double Thickness { get; set; }                  // 0 = standard value of the size
        public double OuterD { get; set; }                     // 0 = standard (washers, spring washer, flange)
    }
    public enum WasherChoice { None, Plain, Large }
    public enum SpringPosition { None, UnderNut, UnderHead }

    /// <summary>Everything needed to (re)generate a block. Serialized to JSON and stored in XData,
    /// so a block can be edited later (BOLTEDIT) or stretched with its length grip.</summary>
    public class BoltJob
    {
        // ---- part ----
        public FastenerStandard Standard { get; set; } = FastenerStandard.ISO;
        public string Size { get; set; } = "M12";
        public PartKind Kind { get; set; } = PartKind.HexBolt;
        public double Length { get; set; } = 40;
        public BasePoint BasePoint { get; set; } = BasePoint.HeadTop;

        // ---- assembly ----
        public bool IsAssembly { get; set; }
        public double ClampLength { get; set; } = 20;
        public WasherChoice HeadWasher { get; set; } = WasherChoice.Plain;
        public WasherChoice NutWasher { get; set; } = WasherChoice.Plain;
        public SpringPosition Spring { get; set; } = SpringPosition.UnderNut;
        public PartKind NutKind { get; set; } = PartKind.HexNut;
        public int NutCount { get; set; } = 1;            // 0, 1 or 2 (2 = nut + lock nut)
        public bool AutoLength { get; set; } = true;       // choose the next standard length
        public double MinProtrusionPitches { get; set; } = 2; // thread pitches beyond the last nut
        public bool SeparateBlocks { get; set; } = true;   // one block per part, inserted as a linked set
        /// <summary>Washers and nuts on the bolt in physical order (head -> end). null = old data, see EnsureStack.</summary>
        public List<StackItem> Stack { get; set; }
        public bool ViewSnap { get; set; } = true;         // orbiting catches on standard view angles
        public bool StickToBoltEnd { get; set; } = false;  // nut side follows the bolt end when the bolt length changes

        // ---- view ----
        public double Azimuth { get; set; } = 0;     // deg, rotation about the vertical screen axis
        public double Elevation { get; set; } = 0;   // deg, rotation about the horizontal screen axis
        public double Spin { get; set; } = 0;        // deg, rotation of the part about its own axis (flats/corners)
        public ThreadStyle Thread { get; set; } = ThreadStyle.Simplified;
        public bool HiddenLines { get; set; } = false;
        public OutputMode Output { get; set; } = OutputMode.View2D;

        // ---- block ----
        public double Scale { get; set; } = 1.0;
        public bool Wipeout { get; set; } = true;
        public string WipeoutLayer { get; set; } = "01_WIPEOUT";
        public bool SnapLengthToStandard { get; set; } = true;

        // ---- link info (filled when inserted) ----
        public string AssemblyId { get; set; }
        public string Role { get; set; }   // single / assembly / bolt / id of a stack element (e1, e2 …)

        public BoltJob Clone() => FromJson(ToJson());

        // ---------------------------------------------------------------- element stack

        /// <summary>Usual arrangement: head | spring | washer | clamped parts | washer | spring | nut(s).</summary>
        public static List<StackItem> BuildStack(WasherChoice headWasher, WasherChoice nutWasher, SpringPosition spring,
                                                 PartKind nutKind, int nuts, bool rod)
        {
            var list = new List<StackItem>();
            void Add(PartKind k, StackSide s) => list.Add(new StackItem { Kind = k, Side = s });
            PartKind W(WasherChoice c) => c == WasherChoice.Large ? PartKind.LargeWasher : PartKind.PlainWasher;
            if (rod) Add(nutKind, StackSide.Head);
            if (spring == SpringPosition.UnderHead) Add(PartKind.SpringWasher, StackSide.Head);
            if (headWasher != WasherChoice.None) Add(W(headWasher), StackSide.Head);
            if (nutWasher != WasherChoice.None) Add(W(nutWasher), StackSide.Nut);
            if (spring == SpringPosition.UnderNut) Add(PartKind.SpringWasher, StackSide.Nut);
            for (int i = 0; i < Math.Clamp(nuts, 0, 2); i++) Add(nutKind, StackSide.Nut);   // double nut = two equal nuts
            return list;
        }

        /// <summary>Makes sure the stack exists (old data used fixed washer / spring / nut fields) and is consistent:
        /// head-side elements first, unique ids, no negative values.</summary>
        public void EnsureStack()
        {
            Stack ??= BuildStack(HeadWasher, NutWasher, Spring, NutKind, NutCount, BoltKind == PartKind.ThreadedRod);
            var ordered = new List<StackItem>();
            foreach (var it in Stack) if (it != null && it.Side == StackSide.Head) ordered.Add(it);
            foreach (var it in Stack) if (it != null && it.Side == StackSide.Nut) ordered.Add(it);
            var used = new HashSet<string>();
            int n = 1;
            foreach (var it in ordered)
            {
                if (PartKindInfo.IsBolt(it.Kind)) it.Kind = PartKind.PlainWasher;
                if (string.IsNullOrEmpty(it.Id) || !used.Add(it.Id))
                {
                    while (!used.Add("e" + n)) n++;
                    it.Id = "e" + n;
                }
                if (!double.IsFinite(it.Gap) || it.Gap < 0) it.Gap = 0;
                if (!double.IsFinite(it.Thickness) || it.Thickness < 0) it.Thickness = 0;
                if (!double.IsFinite(it.OuterD) || it.OuterD < 0) it.OuterD = 0;
            }
            Stack = ordered;
            if (!double.IsFinite(ClampLength) || ClampLength < 0) ClampLength = 0;
            if (!double.IsFinite(MinProtrusionPitches) || MinProtrusionPitches < 0) MinProtrusionPitches = 2;
            if (!double.IsFinite(Length) || Length <= 0) Length = Row.DefaultLength;
        }

        /// <summary>Index of the first nut-side element (= number of head-side elements).</summary>
        [JsonIgnore] public int HeadCount
        {
            get { int c = 0; if (Stack != null) foreach (var it in Stack) if (it.Side == StackSide.Head) c++; return c; }
        }

        /// <summary>Free space immediately before element i (not the clamped material).</summary>
        public double GapBefore(int i) => Stack[i].Gap;

        public void SetGapBefore(int i, double value) => Stack[i].Gap = double.IsFinite(value) ? Math.Max(0, value) : 0;

        static readonly JsonSerializerOptions Opt = new()
        {
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        public string ToJson() => JsonSerializer.Serialize(this, Opt);
        public static BoltJob FromJson(string s)
        {
            try { return JsonSerializer.Deserialize<BoltJob>(s, Opt); } catch { return null; }
        }

        [JsonIgnore] public SizeRow Row => Standards.Get(Standard).Get(Size);
        [JsonIgnore] public StandardTable Table => Standards.Get(Standard);

        /// <summary>Bolt kind used in an assembly (Kind may be a nut/washer when IsAssembly is false).</summary>
        [JsonIgnore] public PartKind BoltKind => PartKindInfo.IsBolt(Kind) ? Kind : PartKind.HexBolt;

        public string ViewCode()
        {
            if (Output != OutputMode.View2D) return "3D";
            double a = Norm(Azimuth), e = Norm(Elevation);
            if (Math.Abs(e) < 0.01 && Math.Abs(a) < 0.01) return "SIDE";
            if (Math.Abs(e) < 0.01 && Math.Abs(a - 90) < 0.01) return "HEAD";
            if (Math.Abs(e) < 0.01 && Math.Abs(a + 90) < 0.01) return "END";
            if (Math.Abs(Math.Abs(e) - 90) < 0.01) return "TOP";
            if (Math.Abs(Math.Abs(e) - 35.26) < 0.05 && Math.Abs(Math.Abs(a) - 45) < 0.01) return "ISO";
            return $"V{Math.Round(a)}_{Math.Round(e)}";
        }

        static double Norm(double deg)
        {
            deg %= 360; if (deg > 180) deg -= 360; if (deg <= -180) deg += 360; return deg;
        }
    }
}
