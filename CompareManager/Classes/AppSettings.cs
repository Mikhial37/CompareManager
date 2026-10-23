using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CompareManager.Classes
{
    /// <summary>
    /// What Compare Manager remembers between runs, in
    /// <c>%LOCALAPPDATA%\CompareManager\settings.json</c>: the two sides last compared, the
    /// options, and the folders used lately. Passwords are inside the sources, encrypted.
    /// </summary>
    public sealed class AppSettings
    {
        public CompareSource  Left    { get; set; } = new CompareSource();
        public CompareSource  Right   { get; set; } = new CompareSource { Kind = SourceKind.Database };
        public CompareOptions Options { get; set; } = new CompareOptions();

        /// <summary>Whether identical objects are listed in the results, not only counted.</summary>
        public bool ShowIdentical { get; set; }

        /// <summary>Folders picked lately, newest first — offered by the folder picker.</summary>
        public List<string> RecentFolders { get; set; } = new List<string>();

        /// <summary>SQL connections used lately, newest first, passwords encrypted.</summary>
        public List<CompareSource> RecentConnections { get; set; } = new List<CompareSource>();

        private const int RecentLimit = 12;

        public void RememberFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            folder = folder.Trim();
            RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            RecentFolders.Insert(0, folder);
            if (RecentFolders.Count > RecentLimit) RecentFolders.RemoveRange(RecentLimit, RecentFolders.Count - RecentLimit);
        }

        public void RememberConnection(CompareSource source)
        {
            if (source == null || source.Kind != SourceKind.Database || !source.IsComplete) return;
            RecentConnections.RemoveAll(c => SameConnection(c, source));
            RecentConnections.Insert(0, source.Clone());
            if (RecentConnections.Count > RecentLimit) RecentConnections.RemoveRange(RecentLimit, RecentConnections.Count - RecentLimit);
        }

        private static bool SameConnection(CompareSource a, CompareSource b) =>
            string.Equals(a.Server?.Trim(), b.Server?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Database?.Trim(), b.Database?.Trim(), StringComparison.OrdinalIgnoreCase);

        // ── Stored ────────────────────────────────────────────────────────────

        public static string Folder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompareManager");

        private static string FilePath => Path.Combine(Folder, "settings.json");

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return FromJson(File.ReadAllText(FilePath));
            }
            catch
            {
                // An unreadable file starts from the defaults rather than stopping the app.
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, ToJson());
            }
            catch
            {
                // Not being able to remember the last compare is not worth an error box.
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        public static AppSettings FromJson(string json)
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json ?? "") ?? new AppSettings();
            settings.Left              ??= new CompareSource();
            settings.Right             ??= new CompareSource { Kind = SourceKind.Database };
            settings.Options           ??= new CompareOptions();
            settings.RecentFolders     = (settings.RecentFolders ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            settings.RecentConnections = (settings.RecentConnections ?? new List<CompareSource>()).Where(c => c != null).ToList();
            return settings;
        }
    }
}
