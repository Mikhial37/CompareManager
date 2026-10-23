using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CompareManager.Classes
{
    /// <summary>
    /// The date a script says it was last changed: the latest date written in its comments —
    /// a Change Control block's <c>Date : 06-Oct-2026</c>, a <c>-- 06-Oct-2026 | PK | SYM…</c>
    /// line, a row of the modification history.
    ///
    /// This is the date that survives a copy. A file's own times change whenever it is copied
    /// to a share or pulled from a repository; what the developer wrote in the header does not.
    ///
    /// Only comments are read, so a date in the code (<c>WHERE d &gt; '2024-01-01'</c>) never
    /// counts. Day-first is assumed where a date could be read either way (06/10/2026 is
    /// 6 October), as it is written here; a version number like <c>1.11.01</c> is not a date,
    /// so a dotted date needs a four-digit year. Dates before 1990 or more than a day ahead
    /// are ignored — the template's <c>DD/MM/YY</c> placeholder, a typo.
    /// </summary>
    public static class ScriptDates
    {
        private const string Month = @"(?<mon>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[a-z]*";

        private static readonly Regex[] Patterns =
        {
            // 2026-10-06, 2026/10/06
            new Regex(@"\b(?<y>\d{4})[-/](?<m>\d{1,2})[-/](?<d>\d{1,2})\b", RegexOptions.Compiled),
            // 06/10/2026, 06-10-26 — and 06.10.2026, but not 1.11.01
            new Regex(@"\b(?<d>\d{1,2})[-/](?<m>\d{1,2})[-/](?<y>\d{4}|\d{2})\b", RegexOptions.Compiled),
            new Regex(@"\b(?<d>\d{1,2})\.(?<m>\d{1,2})\.(?<y>\d{4})\b", RegexOptions.Compiled),
            // 06-Oct-2026, 6 October 2026, 06 Oct 26
            new Regex(@"\b(?<d>\d{1,2})[-\s/]?" + Month + @"[-\s/,]*(?<y>\d{4}|\d{2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            // October 6, 2026
            new Regex(@"\b" + Month + @"\s+(?<d>\d{1,2}),?\s+(?<y>\d{4})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)
        };

        /// <summary>The latest date in the script's comments, or null when it gives none.</summary>
        public static DateTime? Latest(string sql, DateTime? today = null)
        {
            if (string.IsNullOrWhiteSpace(sql)) return null;

            DateTime limit = (today ?? DateTime.Today).AddDays(1);
            string comments = CommentsOnly(sql);
            DateTime? latest = null;

            foreach (var pattern in Patterns)
            {
                foreach (Match match in pattern.Matches(comments))
                {
                    var date = Read(match);
                    if (date == null || date.Value.Year < 1990 || date.Value > limit) continue;
                    if (latest == null || date > latest) latest = date;
                }
            }
            return latest;
        }

        private static DateTime? Read(Match match)
        {
            if (!int.TryParse(match.Groups["y"].Value, out int year)) return null;
            if (year < 100) year += 2000;

            int day = int.Parse(match.Groups["d"].Value);
            int month;

            if (match.Groups["mon"].Success)
            {
                string mon = match.Groups["mon"].Value.Substring(0, 3);
                month = DateTime.ParseExact(mon, "MMM", CultureInfo.InvariantCulture).Month;
            }
            else month = int.Parse(match.Groups["m"].Value);

            // Day-first; if that is impossible but month-first is not (10/25/2026), take that.
            if (month > 12 && day <= 12) (day, month) = (month, day);

            try { return new DateTime(year, month, day); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        /// <summary>
        /// The script with its code blanked and its comments kept, position for position — the
        /// mirror of <see cref="ScriptParser.Blank"/>. String literals count as code here.
        /// </summary>
        public static string CommentsOnly(string sql)
        {
            string code = ScriptParser.Blank(sql, blankStrings: false);
            var output  = new StringBuilder(sql.Length);
            for (int i = 0; i < sql.Length; i++)
            {
                char c = sql[i];
                bool commentChar = code[i] != c;
                output.Append(commentChar || char.IsWhiteSpace(c) ? c : ' ');
            }
            return output.ToString();
        }
    }
}
