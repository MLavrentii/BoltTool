using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoltToolApp.Data
{
    public enum FastenerStandard { ISO, JIS }

    public enum PartKind
    {
        HexBolt,            // partially threaded hex bolt   ISO 4014 / JIS B1180
        HexBoltFullThread,  // fully threaded hex screw      ISO 4017 / JIS B1180
        SocketCapScrew,     // hexagon socket head cap screw ISO 4762 / JIS B1176
        ThreadedRod,        // threaded rod / stud           DIN 976
        HexNut,             // ISO 4032 / JIS B1181 type 1
        ThinNut,            // ISO 4035 / JIS B1181 type 3
        PlainWasher,        // ISO 7089 / JIS B1256
        LargeWasher,        // ISO 7093 / JIS B1256 large
        SpringWasher,       // DIN 127 B / JIS B1251 No.2
        FlangeNut,          // nut with built-in washer face, ISO 4161 / JIS B1190
        ChannelNut,         // rectangular nut for 41 mm strut channel (ダクターチャンネル), grooves grip the channel lips
        ChannelNutHolder    // 翼型ナット + 固定ブロック: channel nut with a plastic holder plate outside the channel
    }

    public static class PartKindInfo
    {
        public static bool IsBolt(PartKind k) => k <= PartKind.ThreadedRod;
        public static bool IsNut(PartKind k) => k == PartKind.HexNut || k == PartKind.ThinNut || k == PartKind.FlangeNut || k == PartKind.ChannelNut || k == PartKind.ChannelNutHolder;
        public static bool IsWasher(PartKind k) => k == PartKind.PlainWasher || k == PartKind.LargeWasher || k == PartKind.SpringWasher;
        public static bool HasLength(PartKind k) => IsBolt(k);

        public static string DisplayName(PartKind k) => k switch
        {
            PartKind.HexBolt => "Hex bolt (partial thread)",
            PartKind.HexBoltFullThread => "Hex bolt (full thread)",
            PartKind.SocketCapScrew => "Socket head cap screw",
            PartKind.ThreadedRod => "Threaded rod / stud",
            PartKind.HexNut => "Hex nut",
            PartKind.ThinNut => "Thin (jam) nut",
            PartKind.PlainWasher => "Plain washer",
            PartKind.LargeWasher => "Large washer",
            PartKind.SpringWasher => "Spring lock washer",
            PartKind.FlangeNut => "Flange nut (nut with washer face)",
            PartKind.ChannelNut => "Channel nut (for 41 channel)",
            PartKind.ChannelNutHolder => "Channel nut + holder (wing nut + fixing block)",
            _ => k.ToString()
        };

        public static string Designation(PartKind k, FastenerStandard s) => (s, k) switch
        {
            (FastenerStandard.ISO, PartKind.HexBolt) => "ISO 4014",
            (FastenerStandard.ISO, PartKind.HexBoltFullThread) => "ISO 4017",
            (FastenerStandard.ISO, PartKind.SocketCapScrew) => "ISO 4762",
            (FastenerStandard.ISO, PartKind.ThreadedRod) => "DIN 976",
            (FastenerStandard.ISO, PartKind.HexNut) => "ISO 4032",
            (FastenerStandard.ISO, PartKind.ThinNut) => "ISO 4035",
            (FastenerStandard.ISO, PartKind.PlainWasher) => "ISO 7089",
            (FastenerStandard.ISO, PartKind.LargeWasher) => "ISO 7093",
            (FastenerStandard.ISO, PartKind.SpringWasher) => "DIN 127B",
            (FastenerStandard.ISO, PartKind.FlangeNut) => "ISO 4161",
            (FastenerStandard.JIS, PartKind.FlangeNut) => "JIS B1190",
            (_, PartKind.ChannelNut) => "Channel nut 41",
            (_, PartKind.ChannelNutHolder) => "Channel nut 41 + holder",
            (FastenerStandard.JIS, PartKind.HexBolt) => "JIS B1180",
            (FastenerStandard.JIS, PartKind.HexBoltFullThread) => "JIS B1180 FT",
            (FastenerStandard.JIS, PartKind.SocketCapScrew) => "JIS B1176",
            (FastenerStandard.JIS, PartKind.ThreadedRod) => "JIS ROD",
            (FastenerStandard.JIS, PartKind.HexNut) => "JIS B1181-1",
            (FastenerStandard.JIS, PartKind.ThinNut) => "JIS B1181-3",
            (FastenerStandard.JIS, PartKind.PlainWasher) => "JIS B1256",
            (FastenerStandard.JIS, PartKind.LargeWasher) => "JIS B1256 L",
            (FastenerStandard.JIS, PartKind.SpringWasher) => "JIS B1251-2",
            _ => k.ToString()
        };

        /// <summary>Short ASCII code used in block names.</summary>
        public static string Code(PartKind k) => k switch
        {
            PartKind.HexBolt => "HB",
            PartKind.HexBoltFullThread => "HBF",
            PartKind.SocketCapScrew => "SHCS",
            PartKind.ThreadedRod => "ROD",
            PartKind.HexNut => "NUT",
            PartKind.ThinNut => "NUTT",
            PartKind.PlainWasher => "WSH",
            PartKind.LargeWasher => "WSHL",
            PartKind.SpringWasher => "SPW",
            PartKind.FlangeNut => "NUTF",
            PartKind.ChannelNut => "NUTC",
            PartKind.ChannelNutHolder => "NUTCH",
            _ => "P"
        };
    }

    /// <summary>All dimensions in mm for one nominal size. Values are typical catalogue values –
    /// they are written to %AppData%\BoltTool\standard_*.json so they can be checked/edited.</summary>
    public class SizeRow
    {
        public string Size { get; set; }          // "M12"
        public double D { get; set; }             // nominal diameter
        public double P { get; set; }             // coarse pitch
        public double S { get; set; }             // across flats (bolt head & nut)
        public double K { get; set; }             // hex head height
        public double M { get; set; }             // nut height
        public double MThin { get; set; }         // thin nut height
        public double WasherD1 { get; set; }      // plain washer inner
        public double WasherD2 { get; set; }      // plain washer outer
        public double WasherH { get; set; }       // plain washer thickness
        public double LargeD2 { get; set; }       // large washer outer
        public double LargeH { get; set; }        // large washer thickness
        public double SpringD1 { get; set; }      // spring washer inner
        public double SpringD2 { get; set; }      // spring washer outer
        public double SpringT { get; set; }       // spring washer thickness
        public double SocketDk { get; set; }      // socket head diameter
        public double SocketK { get; set; }       // socket head height
        public double SocketS { get; set; }       // hex key size
        public double SocketT { get; set; }       // socket depth
        public double HoleMedium { get; set; }    // clearance hole, medium series (ISO 273)
        public double DefaultLength { get; set; } // most used length for this size

        [JsonIgnore] public double D2 => D - 0.649519 * P;     // pitch diameter
        [JsonIgnore] public double D3 => D - 1.226869 * P;     // minor diameter (bolt)
        [JsonIgnore] public double D1Nut => D - 1.082532 * P;  // minor diameter (nut)
        [JsonIgnore] public double FlangeD => Math.Round(2.1 * D + 1.3, 1);   // flange nut: flange diameter (approximation of ISO 4161)
        [JsonIgnore] public double FlangeM => D;                              // flange nut: height
    }

    public class StandardTable
    {
        public FastenerStandard Standard { get; set; }
        public List<SizeRow> Rows { get; set; } = new();
        public List<double> Lengths { get; set; } = new();

        public SizeRow Get(string size) =>
            Rows.FirstOrDefault(r => string.Equals(r.Size, size, StringComparison.OrdinalIgnoreCase)) ?? Rows[0];

        /// <summary>Thread length b of a partially threaded bolt (ISO 4014 reference values).</summary>
        public static double ThreadLength(PartKind kind, SizeRow r, double length)
        {
            if (kind == PartKind.HexBoltFullThread || kind == PartKind.ThreadedRod) return length;
            double b = kind == PartKind.SocketCapScrew ? 2 * r.D + 12
                     : length <= 125 ? 2 * r.D + 6
                     : length <= 200 ? 2 * r.D + 12 : 2 * r.D + 25;
            // short bolts are threaded (almost) to the head
            if (b > length - 0.5 * r.D) b = length;
            return b;
        }

        public double NextStandardLength(double minLength)
        {
            foreach (var l in Lengths.OrderBy(x => x)) if (l >= minLength - 1e-6) return l;
            return Math.Ceiling(minLength / 10.0) * 10.0;
        }
    }

    public static class Standards
    {
        static readonly Dictionary<FastenerStandard, StandardTable> _cache = new();

        public static string DataFolder
        {
            get
            {
                var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BoltTool");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        public static StandardTable Get(FastenerStandard s)
        {
            if (_cache.TryGetValue(s, out var t)) return t;
            string file = Path.Combine(DataFolder, $"standard_{s}.json");
            try
            {
                if (File.Exists(file))
                {
                    t = JsonSerializer.Deserialize<StandardTable>(File.ReadAllText(file));
                    if (t != null && t.Rows.Count > 0) { _cache[s] = t; return t; }
                }
            }
            catch { /* corrupt user file -> fall back to built-in */ }
            t = s == FastenerStandard.ISO ? BuildIso() : BuildJis();
            try { File.WriteAllText(file, JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true })); } catch { }
            _cache[s] = t;
            return t;
        }

        public static void Reload() => _cache.Clear();

        static readonly double[] StdLengths =
            { 5, 6, 8, 10, 12, 14, 16, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 90, 100, 110, 120, 130,
              140, 150, 160, 180, 200, 220, 240, 260, 280, 300 };

        static SizeRow R(string size, double d, double p, double s, double k, double m, double mt,
                         double w1, double w2, double wh, double l2, double lh,
                         double sp1, double sp2, double spt,
                         double dk, double sk, double ss, double st, double hole, double defL) => new SizeRow
        {
            Size = size, D = d, P = p, S = s, K = k, M = m, MThin = mt,
            WasherD1 = w1, WasherD2 = w2, WasherH = wh, LargeD2 = l2, LargeH = lh,
            SpringD1 = sp1, SpringD2 = sp2, SpringT = spt,
            SocketDk = dk, SocketK = sk, SocketS = ss, SocketT = st, HoleMedium = hole, DefaultLength = defL
        };

        static StandardTable BuildIso() => new StandardTable
        {
            Standard = FastenerStandard.ISO,
            Lengths = StdLengths.ToList(),
            Rows = new List<SizeRow>
            {
                //   size    d   P     s    k     m    mThin w1   w2  wh   L2   Lh   sp1  sp2  spT  dk  sk  ss  st   hole  defL
                R("M3",   3, 0.5,  5.5, 2.0,  2.4, 1.8,  3.2,  7, 0.5,  9, 0.8,  3.1, 6.2, 0.8, 5.5, 3, 2.5, 1.3, 3.4, 10),
                R("M4",   4, 0.7,  7,   2.8,  3.2, 2.2,  4.3,  9, 0.8, 12, 1.0,  4.1, 7.6, 0.9, 7,   4, 3,   2,   4.5, 12),
                R("M5",   5, 0.8,  8,   3.5,  4.7, 2.7,  5.3, 10, 1.0, 15, 1.2,  5.1, 9.2, 1.2, 8.5, 5, 4,   2.5, 5.5, 16),
                R("M6",   6, 1.0, 10,   4.0,  5.2, 3.2,  6.4, 12, 1.6, 18, 1.6,  6.1, 11.8,1.6, 10,  6, 5,   3,   6.6, 20),
                R("M8",   8, 1.25,13,   5.3,  6.8, 4.0,  8.4, 16, 1.6, 24, 2.0,  8.1, 14.8,2.0, 13,  8, 6,   4,   9,   25),
                R("M10", 10, 1.5, 16,   6.4,  8.4, 5.0, 10.5, 20, 2.0, 30, 2.5, 10.2, 18.1,2.2, 16, 10, 8,   5,  11,   30),
                R("M12", 12, 1.75,18,   7.5, 10.8, 6.0, 13,   24, 2.5, 37, 3.0, 12.2, 21.1,2.5, 18, 12, 10,  6,  13.5, 40),
                R("M14", 14, 2.0, 21,   8.8, 12.8, 7.0, 15,   28, 2.5, 44, 3.0, 14.2, 24.1,3.0, 21, 14, 12,  7,  15.5, 45),
                R("M16", 16, 2.0, 24,  10,   14.8, 8.0, 17,   30, 3.0, 50, 3.0, 16.2, 27.4,3.5, 24, 16, 14,  8,  17.5, 50),
                R("M18", 18, 2.5, 27,  11.5, 15.8, 9.0, 19,   34, 3.0, 56, 4.0, 18.2, 29.4,3.5, 27, 18, 14,  9,  20,   60),
                R("M20", 20, 2.5, 30,  12.5, 18,  10,   21,   37, 3.0, 60, 4.0, 20.2, 33.6,4.0, 30, 20, 17, 10,  22,   60),
                R("M22", 22, 2.5, 34,  14,   19.4,11,   23,   39, 3.0, 66, 5.0, 22.5, 35.9,4.0, 33, 22, 17, 11,  24,   70),
                R("M24", 24, 3.0, 36,  15,   21.5,12,   25,   44, 4.0, 72, 5.0, 24.5, 40,  5.0, 36, 24, 19, 12,  26,   80),
                R("M27", 27, 3.0, 41,  17,   23.8,13.5, 28,   50, 4.0, 85, 6.0, 27.5, 43,  5.0, 40, 27, 19, 13.5,30,   90),
                R("M30", 30, 3.5, 46,  18.7, 25.6,15,   31,   56, 4.0, 92, 6.0, 30.5, 48.2,6.0, 45, 30, 22, 15.5,33,  100),
                R("M36", 36, 4.0, 55,  22.5, 31,  18,   37,   66, 5.0,110, 8.0, 36.5, 58.2,6.0, 54, 36, 27, 19,  39,  120),
                R("M42", 42, 4.5, 65,  26,   34,  21,   45,   78, 8.0,125,10.0, 42.5, 67.7,7.0, 63, 42, 32, 24,  45,  140),
                R("M48", 48, 5.0, 75,  30,   38,  24,   50,   92, 8.0,145,10.0, 49,   77.5,8.0, 72, 48, 36, 28,  52,  160),
            }
        };

        static StandardTable BuildJis()
        {
            // JIS B1180/B1181 "annex" (traditional JIS) sizes differ from ISO in across-flats,
            // head heights and nut heights. Washers follow JIS B1256 / B1251 No.2.
            var t = BuildIso();
            t.Standard = FastenerStandard.JIS;
            void Set(string size, double s, double k, double m, double spT, double spD2)
            {
                var r = t.Get(size); r.S = s; r.K = k; r.M = m; r.SpringT = spT; r.SpringD2 = spD2;
            }
            //       size   s   k    m   springT springD2
            Set("M3",   5.5, 2,   2.4, 0.7, 6.2);
            Set("M4",   7,   2.8, 3.2, 1.0, 7.6);
            Set("M5",   8,   3.5, 4,   1.3, 9.2);
            Set("M6",  10,   4,   5,   1.5, 12.2);
            Set("M8",  13,   5.5, 6.5, 2.0, 15.4);
            Set("M10", 17,   7,   8,   2.5, 18.4);
            Set("M12", 19,   8,  10,   3.0, 21.5);
            Set("M14", 22,   9,  11,   3.5, 24.5);
            Set("M16", 24,  10,  13,   4.0, 28);
            Set("M18", 27,  12,  15,   4.5, 31);
            Set("M20", 30,  13,  16,   5.0, 33.8);
            Set("M22", 32,  14,  18,   5.5, 35.9);
            Set("M24", 36,  15,  19,   6.0, 40);
            Set("M27", 41,  17,  22,   6.0, 43);
            Set("M30", 46,  19,  24,   7.0, 48.2);
            Set("M36", 55,  23,  29,   8.0, 58.2);
            foreach (var r in t.Rows) r.SpringD1 = Math.Round(r.D + (r.D < 22 ? 0.2 : 0.5), 1);
            return t;
        }
    }
}
