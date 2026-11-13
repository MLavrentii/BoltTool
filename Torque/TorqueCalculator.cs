using System;
using System.Collections.Generic;
using System.Linq;
using BoltToolApp.Data;

namespace BoltToolApp.Torque
{
    /// <summary>Bolt property class. Usage = where this class is typically chosen (guidance text for the UI).</summary>
    public record StrengthClass(string Name, double RpSmall, double RpLarge, string Usage)
    {
        /// <summary>Yield / 0.2 % proof stress in MPa (8.8 changes above M16).</summary>
        public double Rp(double d) => d <= 16 ? RpSmall : RpLarge;
        public override string ToString() => Name;
    }

    /// <summary>Coating / surface of the bolt with typical friction coefficients (dry / oiled / MoS2 paste).</summary>
    public record Finish(string Name, double MuDry, double MuOil, double MuPaste)
    {
        public override string ToString() => Name;
    }

    public record Lubrication(string Name, int Index) { public override string ToString() => Name; }

    /// <summary>Clamped part material: influence on head friction and permissible surface pressure pG (approximate).
    /// Conductor = busbar / terminal material, where the equipment maker's torque governs.</summary>
    public record ClampMaterial(string Name, double HeadFrictionFactor, double PG, bool Conductor = false)
    {
        public override string ToString() => Name;
    }

    /// <summary>How the preload is defined.</summary>
    public enum TorqueMethod
    {
        /// <summary>VDI 2230-1 (Europe / ISO practice): utilisation of the yield point by the combined
        /// tension + torsion stress during tightening, usually 90 %.</summary>
        Vdi2230,
        /// <summary>Torque-coefficient method of JIS B 1083 (Japanese practice): T = K·d·F with the axial force
        /// F = user-selected fraction of yield × stress area. The 70 % UI default is an
        /// illustrative assumption, not a universal value prescribed by JIS B 1083.</summary>
        JisTorqueCoefficient
    }

    public class TorqueResult
    {
        public string Size { get; set; }
        public double MuG { get; set; }
        public double MuK { get; set; }
        public double As { get; set; }               // mm²
        public double PreloadKN { get; set; }        // preload / axial force (kN)
        public double TorqueNm { get; set; }         // tightening torque (N·m)
        public double TorqueMinNm { get; set; }      // μ - 0.02
        public double TorqueMaxNm { get; set; }      // μ + 0.02
        public double K { get; set; }                // torque coefficient T / (d·F)
        public double SurfacePressure { get; set; }  // MPa under the head
        public double PG { get; set; }
        public bool PressureOk => SurfacePressure <= PG;
    }

    /// <summary>
    /// Tightening torque for metric coarse threads.
    /// Torque for a given preload F (both methods):  T = F·(0.16·P + 0.58·d2·μG + μK·D_Km/2),  D_Km = (dW + dh)/2,
    /// which is the same as T = K·d·F with K = (0.16·P + 0.58·d2·μG + μK·D_Km/2) / d.
    /// Preload: VDI 2230  F = ν·Rp·As / √(1 + 3·[1.5·d2/d0·(P/(π·d2) + 1.155·μG)]²)
    ///          JIS-style torque-coefficient estimate   F = ν·Rp·As (user-specified ν; illustrative)
    /// Friction values and surface pressures are typical – confirm with the supplier for critical joints.
    /// </summary>
    public static class TorqueCalculator
    {
        public static readonly List<StrengthClass> Classes = new()
        {
            new("4.6", 240, 240, "Ordinary bolts for light, non-critical fixing: covers, brackets, ducts, cable racks, secondary steel members."),
            new("4.8", 340, 340, "The usual ordinary bolt (中ボルト) in Japan and for general hardware: frames, stands, secondary steel members. Not for the main friction joints of a steel frame."),
            new("5.6", 300, 300, "Ductile low-carbon bolts, e.g. flanges and pressure equipment where toughness matters more than strength."),
            new("6.8", 480, 480, "General machinery and equipment with medium loads."),
            new("8.8", 640, 660, "The standard high-tensile bolt: machinery, equipment bases, conveyors, structural bolting in European / ISO practice."),
            new("10.9", 940, 940, "Highly loaded machine joints, vehicles, cranes. Main joints of Japanese steel frames (H-beams) use high-strength sets F10T / S10T of similar strength, but those are tightened by the JASS 6 procedure, not by a torque from this calculator."),
            new("12.9", 1100, 1100, "Socket head cap screws for moulds, dies, tooling and compact high-load joints. Avoid plated 12.9 and outdoor use (hydrogen embrittlement)."),
            new("A2-50 / A4-50", 210, 210, "Soft stainless: light outdoor, food and chemical equipment with low loads."),
            new("A2-70 / A4-70", 450, 450, "The usual stainless bolt (SUS304 / SUS316): outdoor, food, chemical and marine equipment. Lubricate to avoid galling."),
            new("A4-80", 600, 600, "High-strength stainless for marine and chemical plant joints. Lubricate to avoid galling."),
        };

        public const int DefaultClassIndex = 4;      // 8.8

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
            // --- JIS materials
            new("SS400 – rolled steel (JIS G 3101)", 1.00, 490),
            new("SM490 / SN490 – structural steel", 1.00, 760),
            new("S45C – carbon steel", 1.00, 850),
            new("SCM440 – alloy steel", 1.00, 850),
            new("SUS304 – stainless", 1.15, 630),
            new("FC250 – grey cast iron", 1.00, 850),
            new("FCD450 – ductile cast iron", 1.00, 600),
            new("A5052 – aluminium plate", 1.10, 230),
            new("A6063-T5 – aluminium extrusion", 1.10, 200),
            new("ADC12 – aluminium die casting", 1.10, 220),
            // --- conductors (electrical terminals / busbars)
            new("C1100 – copper busbar / terminal", 1.10, 200, true),
            new("C2801 / C3604 – brass terminal", 1.10, 300, true),
            new("A1050 / A6101 – aluminium busbar", 1.10, 100, true),
            // --- EN materials
            new("Steel S235", 1.00, 490),
            new("Steel S355", 1.00, 760),
            new("Steel C45 / 42CrMo4", 1.00, 850),
            new("Stainless 1.4301 (304)", 1.15, 630),
            new("Cast iron GJL-250", 1.00, 850),
            new("Ductile iron GJS-400", 1.00, 600),
            new("Aluminium wrought AlMgSi1", 1.10, 360),
            new("Aluminium cast AlSi9Cu3", 1.10, 220),
        };

        /// <summary>Usual yield utilisation of each method (%).</summary>
        public static double DefaultUtilisation(TorqueMethod m) => m == TorqueMethod.Vdi2230 ? 90 : 70;

        public static TorqueResult Calculate(SizeRow r, StrengthClass sc, double muG, double muK, double utilization,
                                             double dW, double dh, double pG, double pressureDw = 0,
                                             TorqueMethod method = TorqueMethod.Vdi2230)
        {
            double d2 = r.D2, d3 = r.D3, P = r.P;
            double d0 = (d2 + d3) / 2;
            double As = Math.PI / 4 * d0 * d0;
            double rp = sc.Rp(r.D);
            double fm = utilization * rp * As;                               // N
            if (method == TorqueMethod.Vdi2230)
            {
                double k = 1.5 * d2 / d0 * (P / (Math.PI * d2) + 1.155 * muG);
                fm /= Math.Sqrt(1 + 3 * k * k);
            }
            double dkm = (dW + dh) / 2;
            double Lever(double mg, double mk) => 0.16 * P + 0.58 * d2 * mg + mk * dkm / 2;   // mm
            double Ma(double mg, double mk) => fm * Lever(mg, mk) / 1000.0;
            double dp = Math.Max(dW, pressureDw);
            double area = Math.PI / 4 * (dp * dp - dh * dh);
            return new TorqueResult
            {
                Size = r.Size, MuG = muG, MuK = muK, As = As,
                PreloadKN = fm / 1000.0,
                TorqueNm = Ma(muG, muK),
                TorqueMinNm = Ma(Math.Max(0.04, muG - 0.02), Math.Max(0.04, muK - 0.02)),
                TorqueMaxNm = Ma(muG + 0.02, muK + 0.02),
                K = Lever(muG, muK) / r.D,
                SurfacePressure = area > 0 ? fm / area : 0,
                PG = pG
            };
        }

        public static double Mu(Finish f, Lubrication l) => l.Index switch { 0 => f.MuDry, 1 => f.MuOil, _ => f.MuPaste };

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
