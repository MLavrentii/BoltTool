using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BoltToolApp.Data
{
    /// <summary>One saved set (bolt + elements) that the user can load again.</summary>
    public class SavedSet
    {
        public string Name { get; set; }       // user name, default = simple code (12x40_BWSN)
        public string Comment { get; set; }    // free text, e.g. "bolt for Gaday Okayama"
        public string Code { get; set; }       // simple code when saved (12x40_BWSN)
        public DateTime Saved { get; set; }
        public string Job { get; set; }        // BoltJob JSON

        public override string ToString() =>
            string.IsNullOrWhiteSpace(Comment) ? Name : $"{Name}  –  {Comment}";
    }

    /// <summary>The list of saved sets in %APPDATA%\BoltTool\saved_sets.json.</summary>
    public static class SavedSets
    {
        static string FilePath => Path.Combine(Standards.DataFolder, "saved_sets.json");
        static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

        public static List<SavedSet> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<List<SavedSet>>(File.ReadAllText(FilePath), Opt) ?? new List<SavedSet>();
            }
            catch { }
            return new List<SavedSet>();
        }

        public static void Save(List<SavedSet> list)
        {
            Directory.CreateDirectory(Standards.DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(), Opt));
        }

        /// <summary>Adds or replaces (same name, ignoring case) a set.</summary>
        public static void Put(SavedSet set)
        {
            var list = Load();
            list.RemoveAll(s => string.Equals(s.Name, set.Name, StringComparison.OrdinalIgnoreCase));
            list.Add(set);
            Save(list);
        }

        public static void Delete(string name)
        {
            var list = Load();
            list.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            Save(list);
        }

        public static bool Exists(string name) =>
            Load().Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
