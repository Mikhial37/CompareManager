using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CompareManager.Classes
{
    /// <summary>
    /// Reads a SQL script far enough to say which object it defines, and cuts out the batch
    /// that defines it.
    ///
    /// Comments and string literals are blanked before anything is looked for — replaced by
    /// spaces, character for character, so every position in the blanked text is the same
    /// position in the real one. A <c>-- ALTER PROCEDURE</c> in a note, a <c>GO</c> in a block
    /// comment, or a <c>CREATE TABLE</c> inside an <c>EXEC('…')</c> therefore decides nothing.
    /// </summary>
    public static class ScriptParser
    {
        /// <summary>What a script was found to define.</summary>
        public sealed class Found
        {
            public string     Schema { get; set; }
            public string     Name   { get; set; }
            public ObjectKind Kind   { get; set; }

            /// <summary>The batch that holds the CREATE or ALTER, without the GO that ends it.</summary>
            public string Batch { get; set; }
        }

        // ── Patterns ──────────────────────────────────────────────────────────

        /// <summary>One name part: bracketed, quoted, or bare.</summary>
        private const string Part = @"(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_#@][\w$#@]*)";

        /// <summary>A possibly qualified name, including <c>db..name</c>, which leaves the schema out.</summary>
        private const string Qualified = Part + @"(?:\s*\.\s*(?:" + Part + @")?)*";

        private static readonly Regex DdlPattern = new Regex(
            @"\b(?<verb>CREATE\s+OR\s+ALTER|CREATE|ALTER)\s+" +
            @"(?<kind>PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER|TABLE)\b\s*" +
            @"(?<name>" + Qualified + @")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// What follows <c>ALTER TABLE x</c> when the statement only switches something on or
        /// off — triggers before a data load, constraint checking — rather than changing the
        /// table. Such a statement says nothing about what the script is for.
        /// </summary>
        private static readonly Regex TableToggle = new Regex(
            @"\G\s*(?:WITH\s+(?:NO)?CHECK\s+)?(?:ENABLE|DISABLE|NOCHECK|CHECK|SET|SWITCH|REBUILD)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>A statement that changes data, and the table it changes.</summary>
        private static readonly Regex DataPattern = new Regex(
            @"(?<!\bON\s+)\b(?<verb>INSERT(?:\s+INTO)?|UPDATE|DELETE(?:\s+FROM)?|MERGE(?:\s+INTO)?|TRUNCATE\s+TABLE)\s+" +
            @"(?<name>" + Qualified + @")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// A batch separator: GO alone on its line, optionally with a count. The <c>\r?</c>
        /// matters: in multiline mode <c>$</c> stops before <c>\n</c> only, so without it no
        /// GO in a CRLF script — which is nearly all of them — would be seen.
        /// </summary>
        private static readonly Regex GoPattern = new Regex(
            @"^[ \t]*GO(?:[ \t]+\d+)?[ \t]*;?[ \t]*\r?$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        // ── Identification ────────────────────────────────────────────────────

        /// <summary>
        /// The object a script defines: the first CREATE or ALTER of a procedure, function,
        /// view, trigger or table that is not a temp table. Null when there is none — a data
        /// script, or an empty file.
        ///
        /// A table is held to more than that, because data scripts touch tables all the time.
        /// An <c>ALTER TABLE … DISABLE TRIGGER</c> before a load is not a change to the table
        /// (<see cref="FirstDdl"/> steps over it), and a script that inserts, updates or deletes
        /// before its first table statement is a data script that happens to alter a table on
        /// the way — read as a table, a 500-line data move was compared with the table's script.
        /// </summary>
        public static Found Identify(string script)
        {
            if (string.IsNullOrWhiteSpace(script)) return null;

            string blanked = Blank(script, blankStrings: true);
            var ddl = FirstDdl(blanked);
            if (ddl == null) return null;

            if (KindOf(ddl.Groups["kind"].Value) == ObjectKind.Table)
            {
                var data = FirstData(blanked);
                if (data != null && data.Index < ddl.Index) return null;
            }

            var (schema, name) = SplitName(script.Substring(ddl.Groups["name"].Index, ddl.Groups["name"].Length));

            return new Found
            {
                Schema = schema,
                Name   = name,
                Kind   = KindOf(ddl.Groups["kind"].Value),
                Batch  = BatchAround(script, ddl.Index)
            };
        }

        /// <summary>
        /// The first DDL match in already-blanked text that is not a temp object, nor an
        /// <c>ALTER TABLE</c> that only switches triggers or constraint checks on or off.
        /// </summary>
        internal static Match FirstDdl(string blanked)
        {
            foreach (Match match in DdlPattern.Matches(blanked))
            {
                var (_, name) = SplitName(match.Groups["name"].Value);
                if (name.StartsWith("#") || name.StartsWith("@") || name.Length == 0) continue;

                bool alterTable = match.Groups["verb"].Value.Equals("ALTER", StringComparison.OrdinalIgnoreCase) &&
                                  KindOf(match.Groups["kind"].Value) == ObjectKind.Table;
                if (alterTable && TableToggle.IsMatch(blanked, match.Index + match.Length)) continue;

                return match;
            }
            return null;
        }

        /// <summary>
        /// The first statement that changes data in a real table — not a temp table or a table
        /// variable, not <c>UPDATE STATISTICS</c>, not a foreign key's <c>ON DELETE CASCADE</c>.
        /// </summary>
        internal static Match FirstData(string blanked)
        {
            foreach (Match match in DataPattern.Matches(blanked))
            {
                var (_, name) = SplitName(match.Groups["name"].Value);
                if (name.Length == 0 || name.StartsWith("#") || name.StartsWith("@")) continue;
                if (name.Equals("STATISTICS", StringComparison.OrdinalIgnoreCase)) continue;
                return match;
            }
            return null;
        }

        private static ObjectKind KindOf(string keyword) => keyword.ToUpperInvariant() switch
        {
            "PROCEDURE" or "PROC" => ObjectKind.Procedure,
            "FUNCTION"            => ObjectKind.Function,
            "VIEW"                => ObjectKind.View,
            "TRIGGER"             => ObjectKind.Trigger,
            _                     => ObjectKind.Table
        };

        /// <summary>
        /// Splits <c>[Payroll].[dbo].[SYMsp_Payslip]</c> into its schema and name, brackets
        /// and quotes removed. No schema — or <c>db..name</c>, which leaves it empty — reads
        /// as dbo, where a script that named none would put the object.
        /// </summary>
        public static (string Schema, string Name) SplitName(string qualified)
        {
            var parts   = new List<string>();
            var current = new StringBuilder();
            char close  = '\0';

            foreach (char c in qualified ?? "")
            {
                if (close != '\0')
                {
                    if (c == close) close = '\0';
                    else current.Append(c);
                }
                else if (c == '[') close = ']';
                else if (c == '"') close = '"';
                else if (c == '.') { parts.Add(current.ToString().Trim()); current.Clear(); }
                else if (!char.IsWhiteSpace(c)) current.Append(c);
            }
            parts.Add(current.ToString().Trim());

            string name   = parts[^1];
            string schema = parts.Count >= 2 && parts[^2].Length > 0 ? parts[^2] : "dbo";
            return (schema, name);
        }

        // ── Batches ───────────────────────────────────────────────────────────

        /// <summary>
        /// The batch around <paramref name="position"/>: from just after the GO before it to
        /// just before the GO after it, with the blank lines at either end trimmed. That is
        /// what SQL Server keeps as an object's definition — comments above the CREATE
        /// included — so it is the fair thing to compare with one.
        /// </summary>
        public static string BatchAround(string script, int position)
        {
            int start = 0, end = script.Length;

            foreach (Match go in GoPattern.Matches(Blank(script, blankStrings: true)))
            {
                if (go.Index + go.Length <= position) start = go.Index + go.Length;
                else if (go.Index > position) { end = go.Index; break; }
            }

            return TrimBlankLines(script.Substring(start, end - start));
        }

        /// <summary>Drops whitespace-only lines from both ends and the trailing spaces of the last line.</summary>
        public static string TrimBlankLines(string text)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            int first = 0, last = lines.Length - 1;
            while (first <= last && lines[first].Trim().Length == 0) first++;
            while (last >= first && lines[last].Trim().Length == 0) last--;
            if (first > last) return "";

            return string.Join("\n", lines, first, last - first + 1).TrimEnd();
        }

        // ── Blanking ──────────────────────────────────────────────────────────

        /// <summary>
        /// Replaces every character of every comment — and, when <paramref name="blankStrings"/>,
        /// of every string literal — with a space, keeping line breaks so lines still line up.
        /// The result is exactly as long as the input.
        ///
        /// Block comments nest in T-SQL, so they are counted rather than ended at the first
        /// <c>*/</c>. A bracketed or quoted identifier is copied as it is: <c>[a--b]</c> is a
        /// name, not the start of a comment.
        /// </summary>
        public static string Blank(string sql, bool blankStrings)
        {
            if (string.IsNullOrEmpty(sql)) return sql ?? "";

            var output = new StringBuilder(sql);
            int i = 0, n = sql.Length;

            void Space(int from, int to)
            {
                for (int k = from; k < to; k++)
                    if (sql[k] != '\n' && sql[k] != '\r') output[k] = ' ';
            }

            while (i < n)
            {
                char c    = sql[i];
                char next = i + 1 < n ? sql[i + 1] : '\0';

                if (c == '-' && next == '-')
                {
                    int end = sql.IndexOf('\n', i);
                    if (end < 0) end = n;
                    Space(i, end);
                    i = end;
                }
                else if (c == '/' && next == '*')
                {
                    int depth = 0, k = i;
                    while (k < n)
                    {
                        if (k + 1 < n && sql[k] == '/' && sql[k + 1] == '*') { depth++; k += 2; }
                        else if (k + 1 < n && sql[k] == '*' && sql[k + 1] == '/') { depth--; k += 2; if (depth == 0) break; }
                        else k++;
                    }
                    Space(i, k);
                    i = k;
                }
                else if (c == '\'')
                {
                    int k = i + 1;
                    while (k < n)
                    {
                        if (sql[k] == '\'')
                        {
                            if (k + 1 < n && sql[k + 1] == '\'') { k += 2; continue; }
                            k++;
                            break;
                        }
                        k++;
                    }
                    if (blankStrings) Space(i, k);
                    i = k;
                }
                else if (c == '[' || c == '"')
                {
                    char close = c == '[' ? ']' : '"';
                    int end = sql.IndexOf(close, i + 1);
                    i = end < 0 ? n : end + 1;
                }
                else i++;
            }

            return output.ToString();
        }
    }
}
