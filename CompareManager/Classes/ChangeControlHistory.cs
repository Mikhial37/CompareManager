using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CompareManager.Classes
{
    /// <summary>One row of <c>[Symplexity Change Control]</c>: one time a script was run.</summary>
    public sealed class ChangeControlRun
    {
        public int      ScriptId   { get; set; }
        public string   Issue      { get; set; } = "";
        public string   ScriptName { get; set; } = "";
        public string   Status     { get; set; } = "";
        public DateTime Started    { get; set; }
        public DateTime Ended      { get; set; }
        public string   Error      { get; set; }
        public string   User       { get; set; } = "";

        /// <summary>
        /// The procedure inserts a row as <c>Complete</c> when a script starts, then sets it to
        /// <c>Completed</c> or <c>Failed</c> at the end — so a row still on <c>Complete</c> is a
        /// run that started and never reached its last call.
        /// </summary>
        public string Outcome => Status.Trim().ToUpperInvariant() switch
        {
            "COMPLETED" => "completed",
            "FAILED"    => "failed",
            "COMPLETE"  => "started, never finished",
            _           => Status.Trim().ToLowerInvariant()
        };

        public override string ToString() =>
            $"{Started:yyyy-MM-dd HH:mm}  {Outcome}  by {User}" + (string.IsNullOrWhiteSpace(Error) ? "" : $" — {Error.Trim()}");
    }

    /// <summary>
    /// What a script's Change Control header says about it: <c>SET @IssueNumber = 'SYM…'</c>
    /// and <c>SET @ScriptName = '…'</c> — the two values <c>SYMsp_SymplexityChangeCTRL</c> writes
    /// into <c>[Symplexity Change Control]</c>, so the ones a script's runs are found by.
    /// </summary>
    public sealed class ChangeControlHeader
    {
        public string Issue      { get; set; }
        public string ScriptName { get; set; }

        /// <summary>
        /// Who the script is by: <c>SET @User = '…'</c>, which the procedure records as Person
        /// Responsible; failing that, the initials after the number in the file name
        /// (<c>SYM0000002-0725 AB sp_….sql</c>). Null when neither says.
        /// </summary>
        public string Person { get; set; }

        /// <summary>The initials in the file name (<c>PK</c>), whether or not the header names the person.</summary>
        public string Initials { get; set; }

        /// <summary>True when <see cref="Person"/> is the header's <c>@User</c>, not initials from the file name.</summary>
        public bool PersonFromHeader { get; set; }

        /// <summary>True when the values came from the header rather than from the file name.</summary>
        public bool FromHeader { get; set; }

        /// <summary>The script calls <c>SYMsp_SymplexityChangeCTRL</c> with 0 — the call that inserts the run's row.</summary>
        public bool StartsRecord { get; set; }

        /// <summary>It calls it with anything but 0 or -1 — the call that marks the run Completed.</summary>
        public bool CompletesRecord { get; set; }

        /// <summary>
        /// It sets <c>@Identity</c> below 1 (<c>SET @Identity = -1</c>). The procedure inserts a row
        /// whenever <c>@Identity &lt; 1</c>, so then a single completing call both inserts and
        /// completes the record — the object scripts' one-call form.
        /// </summary>
        public bool IdentityPreset { get; set; }

        /// <summary>A run of this script leaves a Completed row: a start call, or the one-call form, and a completing call.</summary>
        public bool HasChangeControl => CompletesRecord && (StartsRecord || IdentityPreset);

        /// <summary>"Yes", or what is wrong: every script is to carry Change Control, and a run of it must end up recorded.</summary>
        public string ChangeControlLabel =>
            HasChangeControl ? "Yes"
          : CompletesRecord  ? "End call only — no row is inserted"
          : StartsRecord     ? "No completing call"
                             : "Missing";

        private static readonly Regex IssueValue  = Value("IssueNumber");
        private static readonly Regex ScriptValue = Value("ScriptName");
        private static readonly Regex UserValue   = Value("User");
        private static readonly Regex IssueInName = new Regex(@"\bSYM\d{5,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The initials after the number: <c>SYM0000002-0725 AB …</c>, <c>SYM0000003 401 CD …</c>.</summary>
        private static readonly Regex InitialsInName = new Regex(
            @"^SYM\d{5,}(?:[-\s]+\d+)?\s+(?<who>[A-Z]{2,4})\s", RegexOptions.Compiled);

        private static readonly Regex IdentityBelowOne = new Regex(
            @"\bSET\s+@IDENTITY\s*=\s*(?:-\s*\d+|0)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>A call of the Change Control procedure, and its first argument.</summary>
        private static readonly Regex Call = new Regex(
            @"\bEXEC(?:UTE)?\s+(?:\[?dbo\]?\s*\.\s*)?\[?SYMsp_SymplexityChangeCTRL\]?\s+(?<first>[^,\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary><c>SET @Name = 'value'</c>, or a <c>DECLARE @Name type = 'value'</c>.</summary>
        private static Regex Value(string variable) => new Regex(
            @"@" + variable + @"\b[^=\r\n']*=\s*N?'(?<v>[^']*)'", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Reads the header, from code only — a commented-out SET does not count. Missing values
        /// fall back to the file: the SYM number at the start of its name, and the name itself.
        /// </summary>
        public static ChangeControlHeader Read(string script, string fileName)
        {
            string code   = ScriptParser.Blank(script ?? "", blankStrings: false);
            var issue     = IssueValue.Match(code);
            var name      = ScriptValue.Match(code);
            string issueV = issue.Success ? issue.Groups["v"].Value.Trim() : "";
            string nameV  = name.Success  ? name.Groups["v"].Value.Trim()  : "";

            var user      = UserValue.Match(code);
            string userV  = user.Success ? user.Groups["v"].Value.Trim() : "";

            var header = new ChangeControlHeader { FromHeader = issueV.Length > 0 };
            header.Issue      = issueV.Length > 0 ? issueV : IssueInName.Match(fileName ?? "").Value;
            header.ScriptName = nameV.Length  > 0 ? nameV  : fileName ?? "";
            if (header.Issue.Length == 0) header.Issue = null;

            // The template's placeholder, 'NE Body', is nobody.
            var initials = InitialsInName.Match(fileName ?? "");
            header.Initials         = initials.Success ? initials.Groups["who"].Value : null;
            header.PersonFromHeader = userV.Length > 0 && !userV.Equals("NE Body", StringComparison.OrdinalIgnoreCase);
            header.Person           = header.PersonFromHeader ? userV : header.Initials;

            // Strings are blanked too for this: a call written inside a string is not a call.
            string blanked = ScriptParser.Blank(script ?? "", blankStrings: true);
            header.IdentityPreset = IdentityBelowOne.IsMatch(blanked);
            foreach (Match call in Call.Matches(blanked))
            {
                string first = call.Groups["first"].Value.Trim();
                if (first == "0")       header.StartsRecord    = true;
                else if (first != "-1") header.CompletesRecord = true;
            }
            return header;
        }
    }

    /// <summary>One person's share of a folder: how many scripts, and how many of them lack Change Control.</summary>
    public sealed class PersonCount
    {
        public string Person            { get; set; } = "";
        public int    Scripts           { get; set; }
        public int    WithoutChangeControl { get; set; }

        public override string ToString() =>
            $"{Person} {Scripts}" + (WithoutChangeControl > 0 ? $" ({WithoutChangeControl} without Change Control)" : "");
    }

    public static class People
    {
        /// <summary>
        /// Scripts per person, most first. Names are one person whatever their case
        /// ("Mikhial da Silva", "Mikhial Da Silva"), and a script known only by the initials in
        /// its file name is put with the name the other scripts give for those initials, when
        /// they all give the same one.
        /// </summary>
        public static List<PersonCount> Count(IEnumerable<ChangeControlHeader> headers)
        {
            var list = (headers ?? Enumerable.Empty<ChangeControlHeader>()).Where(h => h != null).ToList();

            var nameForInitials = list.Where(h => h.PersonFromHeader && h.Initials != null)
                                      .GroupBy(h => h.Initials, StringComparer.Ordinal)
                                      .Select(g => (Initials: g.Key, Names: g.Select(h => h.Person).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
                                      .Where(x => x.Names.Count == 1)
                                      .ToDictionary(x => x.Initials, x => x.Names[0], StringComparer.Ordinal);

            string Who(ChangeControlHeader h) =>
                h.PersonFromHeader ? h.Person
              : h.Initials != null && nameForInitials.TryGetValue(h.Initials, out var name) ? name
              : h.Person ?? "(nobody named)";

            return list.GroupBy(Who, StringComparer.OrdinalIgnoreCase)
                       .Select(g => new PersonCount
                       {
                           // The spelling most of their scripts use.
                           Person  = g.Select(Who).GroupBy(n => n, StringComparer.Ordinal).OrderByDescending(n => n.Count()).First().Key,
                           Scripts = g.Count(),
                           WithoutChangeControl = g.Count(h => !h.HasChangeControl)
                       })
                       .OrderByDescending(p => p.Scripts)
                       .ThenBy(p => p.Person, StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }
    }

    /// <summary>
    /// Looks a folder's scripts up in the target database's <c>[Symplexity Change Control]</c>:
    /// for each, every recorded run — when, by whom, and how it ended.
    ///
    /// One query per refresh, for every issue number in the list at once, read-only and under
    /// READ UNCOMMITTED as the desk's own queries are, so a script running elsewhere does not
    /// block it. Rows are matched to a script on issue number and script name, the name
    /// compared without regard to case or a <c>.sql</c> on the end.
    /// </summary>
    public static class ChangeControlHistory
    {
        private const string Table = "[dbo].[Symplexity Change Control]";

        /// <summary>
        /// Every run of the given issues, or null when the database has no Change Control table.
        /// Throws on a connection that fails.
        /// </summary>
        public static List<ChangeControlRun> Load(CompareSource target, IEnumerable<string> issues)
        {
            var list = (issues ?? Enumerable.Empty<string>())
                       .Where(i => !string.IsNullOrWhiteSpace(i))
                       .Select(i => i.Trim())
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();

            using var connection = new SqlConnection(target.ConnectionString());
            connection.Open();

            using (var exists = new SqlCommand($"SELECT OBJECT_ID(N'{Table}', N'U')", connection))
                if (exists.ExecuteScalar() is DBNull or null) return null;

            var runs = new List<ChangeControlRun>();
            if (list.Count == 0) return runs;

            // In chunks: a parameter per issue, and SQL Server takes up to 2,100 a command.
            foreach (var chunk in list.Chunk(500))
            {
                var names = chunk.Select((_, i) => "@i" + i).ToList();
                string sql = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; " +
                             "SELECT [Script ID], [Issue Number], [Script Name], [Status], [Start Date], [End Date], [Error], [Person Responsible] " +
                             $"FROM {Table} WHERE [Issue Number] IN ({string.Join(", ", names)}) ORDER BY [Start Date]";

                using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
                for (int i = 0; i < chunk.Length; i++) command.Parameters.AddWithValue(names[i], chunk[i]);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    runs.Add(new ChangeControlRun
                    {
                        ScriptId   = reader.GetInt32(0),
                        Issue      = reader.GetString(1),
                        ScriptName = reader.GetString(2),
                        Status     = reader.GetString(3),
                        Started    = reader.GetDateTime(4),
                        Ended      = reader.GetDateTime(5),
                        Error      = reader.IsDBNull(6) ? null : reader.GetString(6),
                        User       = reader.GetString(7)
                    });
                }
            }
            return runs;
        }

        /// <summary>The runs of one script, oldest first.</summary>
        public static List<ChangeControlRun> For(ChangeControlHeader header, IEnumerable<ChangeControlRun> runs)
        {
            if (header?.Issue == null) return new List<ChangeControlRun>();
            string name = Bare(header.ScriptName);
            return (runs ?? Enumerable.Empty<ChangeControlRun>())
                   .Where(r => string.Equals(r.Issue.Trim(), header.Issue, StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(Bare(r.ScriptName), name, StringComparison.OrdinalIgnoreCase))
                   .OrderBy(r => r.Started)
                   .ToList();
        }

        /// <summary>"3 (1 failed)", "1", or "" when it has never run.</summary>
        public static string Summary(IReadOnlyList<ChangeControlRun> runs)
        {
            if (runs == null || runs.Count == 0) return "";
            int failed     = runs.Count(r => r.Outcome == "failed");
            int unfinished = runs.Count(r => r.Outcome == "started, never finished");
            var notes = new List<string>();
            if (failed > 0)     notes.Add($"{failed} failed");
            if (unfinished > 0) notes.Add($"{unfinished} unfinished");
            return runs.Count + (notes.Count > 0 ? $" ({string.Join(", ", notes)})" : "");
        }

        private static string Bare(string scriptName)
        {
            string name = Path.GetFileName((scriptName ?? "").Trim());
            return name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4).Trim() : name;
        }
    }
}
