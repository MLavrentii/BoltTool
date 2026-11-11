using System;
using System.Collections.Generic;
using System.Linq;
using WS.BoltTool.Data;

namespace WS.BoltTool.Torque
{
    public record StrengthClass(string Name, double RpSmall, double RpLarge, string Note)
    {
        /// <summary>0.2 % proof stress in MPa (8.8 changes above M16).</summary>
        public double Rp(double d) => d <= 16 ? RpSmall : RpLarge;
        public override string ToString() => Name;
    }

    /// <summary>Coating / surface of the bolt with typical friction coefficients (dry / oiled / MoS2 paste).</summary>
    public record Finish(string Name, double MuDry, double MuOil, double MuPaste)
    {
        public override string ToString() => Name;
    }

    public record Lubrication(string Name, int Index) { public override string ToString() => Name; }

    /// <summary>Clamped part material: influence on head friction and permissible surface pressure pG.</summary>
    public record ClampMaterial(string Name, double HeadFrictionFactor, double PG)
    {
        public override string ToString() => Name;
    }

    public class TorqueResult
    {
        public string Size { get; set; }
        public double MuG { get; set; }
        public double MuK { get; set; }
        public double As { get; set; }               // mm²
        public double PreloadKN { get; set; }        // assembly preload F_M (kN)
        public double TorqueNm { get; set; }         // tightening torque M_A (N·m)
        public double TorqueMinNm { get; set; }      // μ - 0.02
        public double TorqueMaxNm { get; set; }      // μ + 0.02
        public double SurfacePressure { get; set; }  // MPa under the head
        public double PG { get; set; }
        public bool PressureOk => SurfacePressure <= PG;
    }

    /// <summary>
    /// Simplified VDI 2230-1 (2015) tightening calculation for metric coarse threads:
    /// F_M = ν·Rp0.2·A0 / √(1 + 3·[1.5·d2/d0·(P/(π·d2) + 1.155·μG)]²)
    /// M_A = F_M·(0.16·P + 0.58·d2·μG + μK·D_Km/2),  D_Km = (dW + dh)/2
    /// Typical friction values – always confirm with the fastener supplier for critical joints.
    /// </summary>
    public static class TorqueCalculator
    {
        public static readonly List<StrengthClass> Classes = new()
        {
            new("4.6", 240, 240, "Mild steel"),
            new("5.6", 300, 300, "Mild steel"),
            new("8.8", 640, 660, "Quenched & tempered"),
            new("10.9", 940, 940, "Alloy steel"),
            new("12.9", 1100, 1100, "Alloy steel"),
            new("A2-50 / A4-50", 210, 210, "Stainless, soft"),
            new("A2-70 / A4-70", 450, 450, "Stainless, cold worked"),
            new("A4-80", 600, 600, "Stainless, high strength"),
        };

        public static readonly List<Finish> Finishes = new()
        {
            new("Black oxide / plain steel", 0.16, 0.12, 0.10),
            new("Phosphated", 0.16, 0.12, 0.10),
            new("Zinc plated (electro)", 0.16, 0.13, 0.10),
            new("Zinc flake (Geomet / Delta)", 0.14, 0.12, 0.10),
            new("Hot-dip galvanized", 0.20, 0.14, 0.11),
            new("Stainless steel A2 / A4", 0.35, 0.20, 0.12),
        };

        public static readonly List<Lubrication> Lubrications = new()
        {
            new("Dry (as delivered, no extra lube)", 0),
            new("Lightly oiled", 1),
            new("MoS2 / anti-seize paste", 2),
        };

        public static readonly List<ClampMaterial> Materials = new()
        {
            new("Steel S235", 1.00, 490),
            new("Steel S355", 1.00, 760),
            new("Steel C45 / 42CrMo4", 1.00, 850),
            new("Stainless 1.4301 (304)", 1.15, 630),
            new("Cast iron GJL-250", 1.00, 850),
            new("Ductile iron GJS-400", 1.00, 600),
            new("Aluminium wrought AlMgSi1", 1.10, 360),
            new("Aluminium cast AlSi9Cu3", 1.10, 220),
        };

        public static TorqueResult Calculate(SizeRow r, StrengthClass sc, double muG, double muK,
                                             double utilization, double dW, double dh, double pG, double pressureDw = 0)
        {
            double d2 = r.D2, d3 = r.D3, P = r.P;
            double d0 = (d2 + d3) / 2;
            double As = Math.PI / 4 * d0 * d0;
            double rp = sc.Rp(r.D);
            double k = 1.5 * d2 / d0 * (P / (Math.PI * d2) + 1.155 * muG);
            double fm = utilization * rp * As / Math.Sqrt(1 + 3 * k * k);    // N
            double dkm = (dW + dh) / 2;
            double Ma(double mg, double mk) => fm * (0.16 * P + 0.58 * d2 * mg + mk * dkm / 2) / 1000.0;
            double dp = Math.Max(dW, pressureDw);
            double area = Math.PI / 4 * (dp * dp - dh * dh);
            return new TorqueResult
            {
                Size = r.Size, MuG = muG, MuK = muK, As = As,
                PreloadKN = fm / 1000.0,
                TorqueNm = Ma(muG, muK),
                TorqueMinNm = Ma(Math.Max(0.04, muG - 0.02), Math.Max(0.04, muK - 0.02)),
                TorqueMaxNm = Ma(muG + 0.02, muK + 0.02),
                SurfacePressure = area > 0 ? fm / area : 0,
                PG = pG
            };
        }

        public static double Mu(Finish f, Lubrication l) => l.Index switch { 0 => f.MuDry, 1 => f.MuOil, _ => f.MuPaste };

        /// <summary>Bearing diameter under the head and hole diameter for a bolt kind / optional washer.</summary>
        /// <summary>dW/dh of the head contact (friction radius) and the outer diameter that loads the clamped part.</summary>
        public static (double dW, double dh, double pressureDw) Bearing(SizeRow r, PartKind kind, bool washer)
        {
            double dW = kind == PartKind.SocketCapScrew ? 0.95 * r.SocketDk : 0.92 * r.S;
            double dh = washer ? r.WasherD1 : r.HoleMedium;
            double pd = washer ? Math.Max(dW, 0.95 * r.WasherD2) : dW;
            return (dW, dh, pd);
        }
    }
}
