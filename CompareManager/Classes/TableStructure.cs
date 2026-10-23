using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CompareManager.Classes
{
    /// <summary>
    /// A table as its columns: one line per column — name, type, identity, NULL or NOT NULL,
    /// default — in column order, written the same way whether it was read from a server's
    /// catalog (<see cref="DatabaseSource"/>) or from a script's <c>CREATE TABLE</c>.
    ///
    /// A table script is mostly not the table: a Change Control wrapper, <c>DROP INDEX</c>
    /// lines, an <c>IF OBJECT_ID(…) IS NULL</c> guard, the audit call at the end. Compared as
    /// text, any two of them differ. Compared as structure, they differ only where a column
    /// does — which is the question being asked.
    ///
    /// Keys, indexes and table constraints are left out on both sides, as the database side
    /// always has; so are collations, filegroups and NOT FOR REPLICATION.
    /// </summary>
    public static class TableStructure
    {
        private static readonly Regex CreateTable = new Regex(
            @"\bCREATE\s+TABLE\s+(?<name>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_#@][\w$#@]*)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_#@][\w$#@]*)?)*)\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TableConstraint = new Regex(
            @"^(?:CONSTRAINT|PRIMARY|UNIQUE|FOREIGN|CHECK|INDEX|PERIOD)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Type = new Regex(
            @"^(?:(?:\[[^\]]+\]|\w+)\s*\.\s*)?(?:\[(?<t>[^\]]+)\]|(?<t>\w+))\s*(?:\((?<a>[^)]*)\))?",
            RegexOptions.Compiled);

        private static readonly Regex NotNull  = new Regex(@"\bNOT\s+NULL\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Identity = new Regex(@"\bIDENTITY\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Primary  = new Regex(@"\bPRIMARY\s+KEY\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Default  = new Regex(@"\bDEFAULT\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Where a default's expression ends, when it is not in brackets: the next column keyword.</summary>
        private static readonly Regex AfterDefault = new Regex(
            @"\s(?:NOT\s+NULL|NULL|CONSTRAINT|PRIMARY|UNIQUE|CHECK|REFERENCES|FOREIGN|IDENTITY|COLLATE|WITH|ROWGUIDCOL|SPARSE)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The structure of <paramref name="schema"/>.<paramref name="name"/> from the first
        /// <c>CREATE TABLE</c> of it in <paramref name="script"/>, or null when the script has
        /// none — one that only alters the table, say.
        /// </summary>
        public static string FromScript(string script, string schema, string name)
        {
            if (string.IsNullOrWhiteSpace(script)) return null;

            string blanked = ScriptParser.Blank(script, blankStrings: true);
            foreach (Match match in CreateTable.Matches(blanked))
            {
                var (s, n) = ScriptParser.SplitName(match.Groups["name"].Value);
                if (!s.Equals(schema, StringComparison.OrdinalIgnoreCase) || !n.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;

                int open  = match.Index + match.Length - 1;
                int close = Matching(blanked, open);
                if (close < 0) return null;

                var lines = Items(script, blanked, open + 1, close)
                    .Select(item => ColumnLine(item))
                    .Where(line => line != null)
                    .ToList();
                return lines.Count == 0 ? null : DatabaseSource.TableScript(schema, name, lines);
            }
            return null;
        }

        /// <summary>The bracket that closes the one at <paramref name="open"/>, or -1.</summary>
        private static int Matching(string blanked, int open)
        {
            int depth = 0;
            for (int i = open; i < blanked.Length; i++)
            {
                if (blanked[i] == '(') depth++;
                else if (blanked[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>The column list split at its top-level commas, comments removed, strings kept.</summary>
        private static IEnumerable<string> Items(string script, string blanked, int start, int end)
        {
            int depth = 0, from = start;
            for (int i = start; i <= end; i++)
            {
                char c = i < end ? blanked[i] : ',';
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    string item = ScriptParser.Blank(script.Substring(from, i - from), blankStrings: false);
                    item = Regex.Replace(item, @"\s+", " ").Trim();
                    if (item.Length > 0) yield return item;
                    from = i + 1;
                }
            }
        }

        /// <summary>One column definition as a structure line; null for a table constraint.</summary>
        public static string ColumnLine(string item)
        {
            if (TableConstraint.IsMatch(item)) return null;

            var (name, rest) = TakeName(item);
            if (name == null) return null;

            if (rest.StartsWith("AS ", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("AS(", StringComparison.OrdinalIgnoreCase))
                return DatabaseSource.ColumnLine(name, "", nullable: true, identity: false, defaultSql: null, computed: rest.Substring(2));

            var type = Type.Match(rest);
            if (!type.Success) return null;
            string typeName = CanonicalType(type.Groups["t"].Value, type.Groups["a"].Success ? type.Groups["a"].Value : null);
            string flags    = rest.Substring(type.Length);

            // The default first, and out of the way: DEFAULT NULL is not the column's nullability.
            string defaultSql = null;
            var def = Default.Match(flags);
            if (def.Success)
            {
                string after = flags.Substring(def.Index + def.Length).TrimStart();
                int length;
                if (after.StartsWith("("))
                {
                    length = Matching(after, 0) + 1;
                    if (length <= 0) length = after.Length;
                }
                else
                {
                    var stop = AfterDefault.Match(after);
                    length = stop.Success ? stop.Index : after.Length;
                }
                defaultSql = after.Substring(0, length);
                flags = flags.Substring(0, def.Index) + " " + after.Substring(length);
            }

            bool identity = Identity.IsMatch(flags);
            bool nullable = !NotNull.IsMatch(flags) && !identity && !Primary.IsMatch(flags);

            return DatabaseSource.ColumnLine(name, typeName, nullable, identity, defaultSql, computed: null);
        }

        private static (string Name, string Remainder) TakeName(string item)
        {
            if (item.StartsWith("["))
            {
                int end = item.IndexOf(']');
                return end < 0 ? (null, "") : (item.Substring(1, end - 1), item.Substring(end + 1).Trim());
            }
            if (item.StartsWith("\""))
            {
                int end = item.IndexOf('"', 1);
                return end < 0 ? (null, "") : (item.Substring(1, end - 1), item.Substring(end + 1).Trim());
            }

            int space = item.IndexOf(' ');
            return space < 0 ? (item, "") : (item.Substring(0, space), item.Substring(space + 1).Trim());
        }

        /// <summary>
        /// A type as the catalog would give it back: lower case, with the length, precision or
        /// scale SQL Server fills in when a script leaves it out — <c>varchar</c> is
        /// <c>varchar(1)</c>, <c>decimal</c> is <c>decimal(18,0)</c>, <c>datetime2</c> is
        /// <c>datetime2(7)</c>; and <c>float(53)</c> is just <c>float</c>.
        /// </summary>
        public static string CanonicalType(string type, string args)
        {
            string t = type.Trim().ToLowerInvariant();
            string a = (args ?? "").Replace(" ", "").ToLowerInvariant();

            switch (t)
            {
                case "varchar": case "char": case "varbinary": case "binary": case "nvarchar": case "nchar":
                    return $"{t}({(a.Length == 0 ? "1" : a)})";
                case "decimal": case "numeric":
                    if (a.Length == 0) return $"{t}(18,0)";
                    return a.Contains(',') ? $"{t}({a})" : $"{t}({a},0)";
                case "datetime2": case "time": case "datetimeoffset":
                    return $"{t}({(a.Length == 0 ? "7" : a)})";
                default:
                    return t;
            }
        }

        /// <summary>
        /// A default or computed expression in one form: no outer brackets (the catalog wraps
        /// <c>0</c> as <c>((0))</c>), no spaces, lower case outside string literals.
        /// </summary>
        public static string Expression(string sql)
        {
            if (sql == null) return null;
            string text = sql.Trim();

            while (text.StartsWith("(") && Matching(text, 0) == text.Length - 1)
                text = text.Substring(1, text.Length - 2).Trim();

            var output = new StringBuilder(text.Length);
            bool inString = false;
            foreach (char c in text)
            {
                if (c == '\'') inString = !inString;
                if (!inString && char.IsWhiteSpace(c)) continue;
                output.Append(inString ? c : char.ToLowerInvariant(c));
            }
            return output.ToString();
        }
    }
}
