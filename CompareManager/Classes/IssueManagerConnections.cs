using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CompareManager.Classes
{
    /// <summary>
    /// The SQL connections saved in Issue Manager (Scripts → SQL connections), offered in
    /// the connection picker so nobody types a server, login and password a second time.
    ///
    /// Read from <c>%LOCALAPPDATA%\IssueManager\sql_connections.json</c> and never written:
    /// that list belongs to Issue Manager. Its passwords are DPAPI-encrypted for the current
    /// Windows user, which is who is running this, so <see cref="Secret.RevealIssueManager"/>
    /// can read them.
    /// </summary>
    public static class IssueManagerConnections
    {
        public sealed class Entry
        {
            public string Name           { get; set; } = "";
            public string Server         { get; set; } = "";
            public string Database       { get; set; } = "";
            public string Login          { get; set; } = "";
            public string PasswordStored { get; set; } = "";

            /// <summary>"Payroll Dev — SQLDEV01 · Payroll", or without the name when it has none.</summary>
            public override string ToString()
            {
                string where = $"{Server} · {Database}";
                return string.IsNullOrWhiteSpace(Name) ? where : $"{Name} — {where}";
            }

            /// <summary>As a source of this app's, its password re-encrypted under this app's own key.</summary>
            public CompareSource ToSource() => new CompareSource
            {
                Kind     = SourceKind.Database,
                Server   = Server,
                Database = Database,
                Login    = Login,
                Password = Secret.RevealIssueManager(PasswordStored)
            };
        }

        public static string FilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IssueManager", "sql_connections.json");

        /// <summary>The saved connections; empty when Issue Manager has none or is not installed for this user.</summary>
        public static List<Entry> Load()
        {
            try
            {
                return File.Exists(FilePath) ? FromJson(File.ReadAllText(FilePath)) : new List<Entry>();
            }
            catch
            {
                return new List<Entry>();
            }
        }

        public static List<Entry> FromJson(string json)
        {
            List<Entry> list;
            try { list = JsonSerializer.Deserialize<List<Entry>>(json ?? ""); }
            catch (JsonException) { return new List<Entry>(); }

            return (list ?? new List<Entry>())
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Server) && !string.IsNullOrWhiteSpace(e.Database))
                .ToList();
        }
    }
}
