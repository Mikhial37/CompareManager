using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace CompareManager.Classes
{
    /// <summary>
    /// Finds and starts WinMerge, the same way Issue Manager's Compare with server does. It is
    /// installed on the desk's machines but not shipped with this app; without it, the
    /// built-in viewer still shows the difference.
    /// </summary>
    public static class WinMerge
    {
        private const string Exe = "WinMergeU.exe";

        public static IEnumerable<string> CandidatePaths()
        {
            foreach (var root in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramFiles"),
                         Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
                     })
            {
                if (!string.IsNullOrWhiteSpace(root))
                    yield return Path.Combine(root, "WinMerge", Exe);
            }

            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                if (!string.IsNullOrWhiteSpace(dir))
                    yield return Path.Combine(dir.Trim(), Exe);
        }

        public static string Locate()
        {
            foreach (string candidate in CandidatePaths())
            {
                try { if (File.Exists(candidate)) return candidate; }
                catch { /* an unreadable PATH entry is not a reason to stop looking */ }
            }
            return null;
        }

        public static bool IsInstalled => Locate() != null;

        /// <summary>
        /// Opens one compare result. A side that is a script file opens as that file, so an
        /// edit made in WinMerge lands in the script; a side from a database is written to a
        /// read-only temp copy first, since editing it would change nothing.
        /// </summary>
        public static void Open(CompareResult result, string leftLabel, string rightLabel)
        {
            string exe = Locate() ?? throw new FileNotFoundException(
                "WinMerge was not found. Looked for " + Exe + " in:\n" +
                string.Join("\n", CandidatePaths().Take(3)) + "\nand on the PATH.");

            var (left,  leftReadOnly)  = PathFor(result.Left,  "left");
            var (right, rightReadOnly) = PathFor(result.Right, "right");

            string args = "/e /u" + (leftReadOnly ? " /wl" : "") + (rightReadOnly ? " /wr" : "") +
                          $" /dl {Quote(leftLabel)} /dr {Quote(rightLabel)} {Quote(left)} {Quote(right)}";

            Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = false });
        }

        private static (string Path, bool ReadOnly) PathFor(SqlObject obj, string side)
        {
            if (obj != null && obj.IsFile && File.Exists(obj.Origin)) return (obj.Origin, false);

            string folder = Path.Combine(Path.GetTempPath(), "CompareManager", side);
            Directory.CreateDirectory(folder);

            string name = obj == null ? "missing.sql" : Safe(obj.Kind == ObjectKind.Script ? obj.Name : obj.QualifiedName + ".sql");
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, (obj?.Definition ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"),
                              new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return (path, true);
        }

        private static string Safe(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private static string Quote(string value) => "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
    }
}
