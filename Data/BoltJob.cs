using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoltToolApp.Data
{
    public enum ThreadStyle { None, Simplified, Detailed }
    public enum OutputMode { View2D, Mesh3D, Solid3D }
    public enum BasePoint { HeadTop, UnderHead, ThreadEnd, BearingFace, OuterFace, Center }
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
        /// <summary>Manual axial shift per part role (mm), set with Ctrl+drag in the preview.</summary>
        public Dictionary<string, double> Offsets { get; set; } = new();
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
        public string Role { get; set; }   // bolt / headwasher / nutwasher / spring / nut / jamnut / single

        public BoltJob Clone() => FromJson(ToJson());

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
