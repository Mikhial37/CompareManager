using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace CompareManager.Classes
{
    /// <summary>
    /// Reads a folder of scripts — a repository, an issue's Scripts folder, a Change Control
    /// folder — into the objects they define.
    ///
    /// Every <c>.sql</c> below the folder is read. One that defines an object is that object
    /// (<see cref="ScriptParser.Identify"/>); one that defines none is kept as a script,
    /// known by its path under the folder, so two folders can still be compared file for
    /// file. Where several scripts define the same object the newest is the one compared,
    /// and the others are listed on it.
    /// </summary>
    public static class FolderSource
    {
        /// <summary>Folders that never hold anything worth comparing.</summary>
        private static readonly HashSet<string> Skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", "bin", "obj", "node_modules"
        };

        public static List<SqlObject> Load(string folder, CancellationToken cancel = default) =>
            Collapse(ReadAll(folder, cancel));

        /// <summary>
        /// Every script below the folder as its own entry, duplicates and all — what
        /// <see cref="Duplicates"/> looks through. <see cref="Load"/> folds these to one per object.
        /// </summary>
        public static List<SqlObject> ReadAll(string folder, CancellationToken cancel = default)
        {
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException($"The folder \"{folder}\" does not exist or cannot be reached.");

            var all = new List<SqlObject>();
            foreach (string path in ScriptFiles(folder))
            {
                cancel.ThrowIfCancellationRequested();
                var entry = Read(folder, path);
                if (entry != null) all.Add(entry);
            }
            return all;
        }

        /// <summary>One entry per key: the newest script, with the paths of the others on it.</summary>
        public static List<SqlObject> Collapse(IEnumerable<SqlObject> scripts)
        {
            var byKey = new Dictionary<string, SqlObject>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in scripts)
            {
                if (byKey.TryGetValue(entry.Key, out var existing))
                {
                    // The date written in the script first, as Duplicates does: a copy's file
                    // date is only when it was copied.
                    static (DateTime, DateTime) Age(SqlObject o) => (o.ScriptDate ?? DateTime.MinValue, o.Modified ?? DateTime.MinValue);
                    var (newer, older) = Age(entry).CompareTo(Age(existing)) > 0 ? (entry, existing) : (existing, entry);

                    newer.OtherCopies.Add(older.Origin);
                    newer.OtherCopies.AddRange(older.OtherCopies);
                    byKey[entry.Key] = newer;
                }
                else byKey[entry.Key] = entry;
            }

            return byKey.Values.OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>One script as an entry; null when it cannot be read (locked, gone since it was listed).</summary>
        public static SqlObject Read(string root, string path)
        {
            string text;
            DateTime modified, created;
            try
            {
                text     = File.ReadAllText(path);
                modified = File.GetLastWriteTime(path);
                created  = File.GetCreationTime(path);
            }
            catch (IOException)                 { return null; }
            catch (UnauthorizedAccessException) { return null; }

            string relative = Path.GetRelativePath(root, path);
            var    found    = ScriptParser.Identify(text);

            // A script is the table only when it creates it. A repository holds the table's
            // foreign keys, indexes and column changes in scripts of their own, each starting
            // ALTER TABLE on it; taken as the table, any of them could stand in for its CREATE
            // TABLE script, and every one counted as a duplicate of it. They stay scripts.
            string structure = found?.Kind == ObjectKind.Table ? TableStructure.FromScript(text, found.Schema, found.Name) : null;
            if (found?.Kind == ObjectKind.Table && structure == null) found = null;

            var entry = found == null
                ? new SqlObject
                {
                    Kind       = ObjectKind.Script,
                    Name       = Path.GetFileName(path),
                    Definition = ScriptParser.TrimBlankLines(text)
                }
                : new SqlObject
                {
                    Schema     = found.Schema,
                    Name       = found.Name,
                    Kind       = found.Kind,
                    Definition = found.Batch,
                    Structure  = structure
                };

            entry.Modified     = modified;
            entry.Created      = created;
            // The whole file, not just the batch: a Change Control header usually sits in a
            // batch of its own above the CREATE.
            entry.ScriptDate   = ScriptDates.Latest(text);
            entry.Origin       = path;
            entry.IsFile       = true;
            entry.RelativePath = relative;
            return entry;
        }

        private static IEnumerable<string> ScriptFiles(string folder)
        {
            var pending = new Stack<string>();
            pending.Push(folder);

            while (pending.Count > 0)
            {
                string dir = pending.Pop();

                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(dir, "*.sql");
                    dirs  = Directory.GetDirectories(dir);
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException)                 { continue; }

                foreach (string file in files) yield return file;
                foreach (string sub in dirs)
                    if (!Skipped.Contains(Path.GetFileName(sub))) pending.Push(sub);
            }
        }
    }
}
