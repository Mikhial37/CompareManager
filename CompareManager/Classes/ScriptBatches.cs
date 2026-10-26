using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CompareManager.Classes
{
    /// <summary>One batch of a script: the text between two GOs, where it starts, and how many times GO asked for it.</summary>
    public sealed class ScriptBatch
    {
        public string Text { get; set; } = "";

        /// <summary>The script line the batch's first line is on, 1-based — so a server's error line can be read back as a script line.</summary>
        public int StartLine { get; set; } = 1;

        /// <summary><c>GO 3</c> runs the batch three times.</summary>
        public int Repeat { get; set; } = 1;
    }

    /// <summary>A script made ready to send: aimed at a database, with what that did and what to watch for.</summary>
    public sealed class PreparedScript
    {
        public string Text { get; set; } = "";

        /// <summary>The database the script's opening <c>USE</c> pointed at before it was aimed, or null when it had none.</summary>
        public string OriginalUse { get; set; }

        /// <summary>True when the opening <c>USE</c> (or the <c>--USE []</c> placeholder) now names the chosen database.</summary>
        public bool Aimed { get; set; }

        /// <summary>
        /// <c>USE</c> lines further down, by script line and database. Left as they are — a
        /// script that changes database part way is doing it on purpose — but shown, since
        /// those statements land somewhere other than the chosen database.
        /// </summary>
        public List<(int Line, string Database)> LaterUses { get; } = new List<(int, string)>();

        /// <summary>
        /// True when the code switches <c>NOEXEC</c> or <c>PARSEONLY</c> itself. A check run relies
        /// on <c>SET NOEXEC ON</c>; a script that turns it off again would start really running.
        /// </summary>
        public bool SwitchesExecution { get; set; }

        public List<ScriptBatch> Batches { get; set; } = new List<ScriptBatch>();
    }

    /// <summary>
    /// Splits scripts on GO and aims them at a database, the way Issue Manager's publish does
    /// (<c>ScriptDatabaseRewriter</c>): an opening <c>USE [x]</c> is re-aimed at the chosen
    /// database, a <c>--USE []</c> placeholder is filled in and its <c>--GO</c> markers become GO,
    /// and a <c>USE</c> further down is left alone. The file on disk is never changed.
    ///
    /// GO is found in text whose comments and strings are blanked (<see cref="ScriptParser.Blank"/>),
    /// so a GO in a comment block, or a column called GO, splits nothing.
    /// </summary>
    public static class ScriptBatches
    {
        private static readonly Regex Go = new Regex(
            @"^[ \t]*GO(?:[ \t]+(?<n>\d+))?[ \t]*;?[ \t]*\r?$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex Use = new Regex(
            @"^[ \t]*USE[ \t]+(?<db>\[[^\]\r\n]+\]|""[^""\r\n]+""|[\w$#@]+)[ \t]*;?[ \t]*\r?$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex UsePlaceholder = new Regex(
            @"^[ \t]*--[ \t]*USE[ \t]*\[[ \t]*\][ \t]*;?[ \t]*\r?$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex GoMarker = new Regex(
            @"^(?<lead>[ \t]*)--[ \t]*GO[ \t]*\r?$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex ExecutionSwitch = new Regex(
            @"\bSET\s+(?:NOEXEC|PARSEONLY)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Splits on GO. Whitespace-only batches are dropped; line numbers are kept.</summary>
        public static List<ScriptBatch> Split(string script)
        {
            var batches = new List<ScriptBatch>();
            if (string.IsNullOrEmpty(script)) return batches;

            string blanked = ScriptParser.Blank(script, blankStrings: true);
            int start = 0;

            void Add(int end, int repeat)
            {
                string text = script.Substring(start, end - start);
                if (text.Trim().Length > 0)
                    batches.Add(new ScriptBatch { Text = text, StartLine = LineOf(script, start), Repeat = Math.Max(1, repeat) });
            }

            foreach (Match go in Go.Matches(blanked))
            {
                Add(go.Index, go.Groups["n"].Success && int.TryParse(go.Groups["n"].Value, out int n) ? n : 1);
                start = go.Index + go.Length;
                if (start < script.Length && script[start] == '\n') start++;
            }
            Add(script.Length, 1);
            return batches;
        }

        /// <summary>
        /// The script as it will be sent. With <paramref name="aimAt"/>, its opening <c>USE</c>
        /// names that database instead, and a <c>--USE []</c> placeholder is filled in (with its
        /// <c>--GO</c> markers uncommented, as the placeholder's convention has it).
        /// </summary>
        public static PreparedScript Prepare(string script, string aimAt)
        {
            script ??= "";
            var prepared = new PreparedScript();
            string text = script;

            string blanked = ScriptParser.Blank(text, blankStrings: true);
            var uses = Use.Matches(blanked).Cast<Match>().ToList();
            var first = uses.FirstOrDefault();
            bool opening = first != null && blanked.Substring(0, first.Index).Trim().Length == 0;

            if (opening)
            {
                prepared.OriginalUse = Unquote(text.Substring(first.Groups["db"].Index, first.Groups["db"].Length));
                if (!string.IsNullOrWhiteSpace(aimAt))
                {
                    text = text.Substring(0, first.Index) + $"USE [{aimAt.Trim()}]" + LineEnd(first) +
                           text.Substring(first.Index + first.Length);
                    prepared.Aimed = true;
                }
            }
            else if (!string.IsNullOrWhiteSpace(aimAt) && UsePlaceholder.IsMatch(text))
            {
                text = UsePlaceholder.Replace(text, m => $"USE [{aimAt.Trim()}]" + LineEnd(m), 1);
                text = GoMarker.Replace(text, m => m.Groups["lead"].Value + "GO" + LineEnd(m));
                prepared.Aimed = true;
            }

            // Later USE lines, read from the text as it will run.
            string finalBlanked = ScriptParser.Blank(text, blankStrings: true);
            foreach (Match use in Use.Matches(finalBlanked).Cast<Match>().Skip(opening || prepared.Aimed ? 1 : 0))
                prepared.LaterUses.Add((LineOf(text, use.Index), Unquote(text.Substring(use.Groups["db"].Index, use.Groups["db"].Length))));

            prepared.SwitchesExecution = ExecutionSwitch.IsMatch(finalBlanked);
            prepared.Text    = text;
            prepared.Batches = Split(text);
            return prepared;
        }

        /// <summary>The patterns take a CRLF line's <c>\r</c> with them; a replacement puts it back.</summary>
        private static string LineEnd(Match match) => match.Value.EndsWith("\r") ? "\r" : "";

        private static string Unquote(string name) => name.Trim().TrimStart('[', '"').TrimEnd(']', '"');

        private static int LineOf(string text, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
            return line;
        }
    }
}
