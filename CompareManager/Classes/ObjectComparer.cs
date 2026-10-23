using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CompareManager.Classes
{
    /// <summary>What counts as a difference. Saved with the settings.</summary>
    public sealed class CompareOptions
    {
        /// <summary>Indentation, blank lines and spacing do not count. On by default — a re-indent is not a change.</summary>
        public bool IgnoreWhitespace { get; set; } = true;

        /// <summary><c>select</c> and <c>SELECT</c> are the same. Off by default: it also folds the case of string literals.</summary>
        public bool IgnoreCase { get; set; }

        /// <summary>Comments do not count — only the code that runs.</summary>
        public bool IgnoreComments { get; set; }

        /// <summary>Report objects whose text is the same but whose change dates are not.</summary>
        public bool CompareDates { get; set; } = true;
    }

    public enum CompareStatus
    {
        Different,
        DateDiffers,
        OnlyLeft,
        OnlyRight,
        Identical
    }

    /// <summary>One object (or script) as it stands on both sides, and what the compare made of it.</summary>
    public sealed class CompareResult
    {
        public string    Key   { get; set; }
        public SqlObject Left  { get; set; }
        public SqlObject Right { get; set; }

        public bool TextDiffers { get; set; }
        public bool DatesDiffer { get; set; }

        public CompareStatus Status =>
            Left  == null ? CompareStatus.OnlyRight :
            Right == null ? CompareStatus.OnlyLeft  :
            TextDiffers   ? CompareStatus.Different :
            DatesDiffer   ? CompareStatus.DateDiffers :
                            CompareStatus.Identical;

        public SqlObject Any => Left ?? Right;

        public string StatusLabel => Status switch
        {
            CompareStatus.Different   => DatesDiffer ? "Different (dates too)" : "Different",
            CompareStatus.DateDiffers => "Change date differs",
            CompareStatus.OnlyLeft    => "Only in left",
            CompareStatus.OnlyRight   => "Only in right",
            _                         => "Identical"
        };

        /// <summary>"Left", "Right" or "" — which side changed last, where both have a date and they differ.</summary>
        public string Newer
        {
            get
            {
                if (Left?.Modified == null || Right?.Modified == null || !DatesDiffer) return "";
                return Left.Modified > Right.Modified ? "Left" : "Right";
            }
        }
    }

    /// <summary>One row of the object list: an object found on either side, or both.</summary>
    public sealed class CatalogEntry
    {
        public string    Key   { get; set; }
        public SqlObject Left  { get; set; }
        public SqlObject Right { get; set; }

        public SqlObject Any => Left ?? Right;

        public string Where => Left != null && Right != null ? "Both" : Left != null ? "Left" : "Right";
    }

    /// <summary>
    /// Pairs up the two sides and decides, for the objects someone ticked, which differ.
    ///
    /// Two definitions are compared after <see cref="Normalize"/>: line endings, trailing
    /// spaces and the blank lines at either end never count, nor does how the header was
    /// written — <c>ALTER PROCEDURE dbo.X</c> in a script and <c>CREATE PROCEDURE [dbo].[X]</c>
    /// on a server are the same object with the same body. The options decide the rest.
    /// </summary>
    public static class ObjectComparer
    {
        /// <summary>Every object on either side, matched on <see cref="SqlObject.Key"/>, sorted by name.</summary>
        public static List<CatalogEntry> Catalog(IEnumerable<SqlObject> left, IEnumerable<SqlObject> right)
        {
            var byKey = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var o in left ?? Enumerable.Empty<SqlObject>())
                byKey[o.Key] = new CatalogEntry { Key = o.Key, Left = o };

            foreach (var o in right ?? Enumerable.Empty<SqlObject>())
            {
                if (byKey.TryGetValue(o.Key, out var entry)) entry.Right = o;
                else byKey[o.Key] = new CatalogEntry { Key = o.Key, Right = o };
            }

            return byKey.Values
                        .OrderBy(e => e.Any.Kind == ObjectKind.Script)
                        .ThenBy(e => e.Any.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
        }

        /// <summary>Compares the entries given — the ticked ones — and nothing else.</summary>
        public static List<CompareResult> Compare(IEnumerable<CatalogEntry> entries, CompareOptions options)
        {
            options ??= new CompareOptions();
            return (entries ?? Enumerable.Empty<CatalogEntry>()).Select(e => Compare(e, options)).ToList();
        }

        public static CompareResult Compare(CatalogEntry entry, CompareOptions options)
        {
            var result = new CompareResult { Key = entry.Key, Left = entry.Left, Right = entry.Right };
            if (entry.Left == null || entry.Right == null) return result;

            result.TextDiffers = !string.Equals(Normalize(entry.Left.Definition, options),
                                                Normalize(entry.Right.Definition, options),
                                                StringComparison.Ordinal);

            result.DatesDiffer = options.CompareDates && DatesDiffer(entry.Left.Modified, entry.Right.Modified);
            return result;
        }

        /// <summary>
        /// Different to the second. SQL Server keeps <c>modify_date</c> to a few milliseconds
        /// and file systems to 100 ns, so anything finer would call every pair different.
        /// </summary>
        public static bool DatesDiffer(DateTime? left, DateTime? right) =>
            left.HasValue && right.HasValue && Math.Abs((left.Value - right.Value).TotalSeconds) >= 1;

        // ── Normalising ───────────────────────────────────────────────────────

        private static readonly Regex Whitespace  = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex Punctuation = new Regex(@" ?([(),;=]) ?", RegexOptions.Compiled);

        /// <summary>
        /// The form two definitions are compared in. Always: one kind of line ending, no
        /// trailing spaces, no blank lines at either end, and the header rewritten as
        /// <c>CREATE &lt;kind&gt; [schema].[name]</c>. Then, as the options say: comments
        /// blanked, whitespace collapsed (and dropped around brackets, commas and equals
        /// signs), case folded.
        /// </summary>
        public static string Normalize(string definition, CompareOptions options)
        {
            string text = (definition ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

            text = CanonicalHeader(text);

            if (options.IgnoreComments) text = ScriptParser.Blank(text, blankStrings: false);

            // A comment on a line of its own leaves the line empty; that line was the comment.
            var lines = text.Split('\n').Select(l => l.TrimEnd());
            if (options.IgnoreComments) lines = lines.Where(l => l.Length > 0);

            text = ScriptParser.TrimBlankLines(string.Join("\n", lines));

            if (options.IgnoreWhitespace)
            {
                text = Whitespace.Replace(text, " ").Trim();
                text = Punctuation.Replace(text, "$1");
            }

            if (options.IgnoreCase) text = text.ToUpperInvariant();
            return text;
        }

        /// <summary>
        /// Rewrites the first CREATE / ALTER / CREATE OR ALTER as CREATE, and the name after
        /// it as <c>[schema].[name]</c>. A server keeps whichever verb the object was last
        /// sent with, and a script names it however its author typed it; neither is a change.
        /// </summary>
        public static string CanonicalHeader(string text)
        {
            var ddl = ScriptParser.FirstDdl(ScriptParser.Blank(text, blankStrings: true));
            if (ddl == null) return text;

            var verb = ddl.Groups["verb"];
            var name = ddl.Groups["name"];
            var (schema, bare) = ScriptParser.SplitName(name.Value);

            // Name first: it is further along, so the verb's position still holds.
            var result = new StringBuilder(text);
            result.Remove(name.Index, name.Length).Insert(name.Index, $"[{schema}].[{bare}]");
            result.Remove(verb.Index, verb.Length).Insert(verb.Index, "CREATE");
            return result.ToString();
        }
    }
}
