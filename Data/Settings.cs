using System;
using System.IO;
using System.Text.Json;

namespace BoltToolApp.Data
{
    /// <summary>Remembers the last used job between sessions and lets the user change default lengths.</summary>
    /// <summary>Window state remembered between sessions (not part of a block's parameters).</summary>
    public class UiState
    {
        public double Left { get; set; } = double.NaN;
        public double Top { get; set; } = double.NaN;
        public double Width { get; set; }
        public double Height { get; set; }
        public int Tab { get; set; }
        public int TqClass { get; set; } = -1;
        public int TqFinish { get; set; } = -1;
        public int TqLube { get; set; } = -1;
        public int TqMaterial { get; set; } = -1;
        public string TqMuG { get; set; }
        public string TqMuK { get; set; }
        public string TqUtil { get; set; }
        public bool TqWasher { get; set; } = true;
        public bool ShowWipeouts { get; set; } = true;
    }

    public static class Settings
    {
        static string UiPath => Path.Combine(Standards.DataFolder, "ui.json");
        static readonly JsonSerializerOptions UiOpt = new()
        {
            WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        public static UiState LoadUi()
        {
            try { if (File.Exists(UiPath)) return JsonSerializer.Deserialize<UiState>(File.ReadAllText(UiPath), UiOpt); } catch { }
            return null;
        }

        public static void SaveUi(UiState state)
        {
            try { File.WriteAllText(UiPath, JsonSerializer.Serialize(state, UiOpt)); } catch { }
        }

        static string FilePath => Path.Combine(Standards.DataFolder, "settings.json");
        static string LangPath => Path.Combine(Standards.DataFolder, "language.txt");

        /// <summary>UI language: "en" or "ja". English until the user picks Japanese.</summary>
        public static string Language
        {
            get
            {
                try { if (File.Exists(LangPath)) return File.ReadAllText(LangPath).Trim(); } catch { }
                return "en";
            }
            set { try { File.WriteAllText(LangPath, value); } catch { } }
        }

        public static BoltJob LoadLastJob()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var j = BoltJob.FromJson(File.ReadAllText(FilePath));
                    if (j != null) { j.AssemblyId = null; j.Role = null; j.Offsets = new(); return j; }
                }
            }
            catch { }
            return new BoltJob();
        }

        public static void SaveLastJob(BoltJob job)
        {
            try
            {
                var j = job.Clone(); j.AssemblyId = null; j.Role = null;
                File.WriteAllText(FilePath, j.ToJson());
            }
            catch { }
        }

        /// <summary>Stores a new default length for a size in the editable standard table.</summary>
        public static void SetDefaultLength(FastenerStandard std, string size, double length)
        {
            var t = Standards.Get(std);
            t.Get(size).DefaultLength = length;
            try
            {
                File.WriteAllText(Path.Combine(Standards.DataFolder, $"standard_{std}.json"),
                    JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
