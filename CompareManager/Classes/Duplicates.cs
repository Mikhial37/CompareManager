using System;
using System.Collections.Generic;
using System.Linq;

namespace CompareManager.Classes
{
    public enum DuplicateReason
    {
        /// <summary>Several scripts create or alter the same object.</summary>
        SameObject,

        /// <summary>Several scripts that define no object have the same text under different names.</summary>
        SameContent
    }

    /// <summary>
    /// A set of scripts in one folder that are copies of one another — the same procedure in
    /// <c>Edited</c> and in <c>Testing</c>, or one data fix saved twice under two names.
    /// </summary>
    public sealed class DuplicateGroup
    {
        public DuplicateReason Reason { get; set; }

        /// <summary>The object's name, or the first file's name for a set of identical scripts.</summary>
        public string Title { get; set; } = "";

        public ObjectKind Kind { get; set; }

        /// <summary>The copies, newest first: the one a compare uses is <c>Copies[0]</c>.</summary>
        public List<SqlObject> Copies { get; set; } = new List<SqlObject>();

        /// <summary>
        /// How many different texts there are among the copies, after the same normalising a
        /// compare uses. One means they are true copies; more means someone has edited one and
        /// not the other — the case worth a look before anything is sent to Change Control.
        /// </summary>
        public int Versions { get; set; }

        /// <summary>For each copy, in step with <see cref="Copies"/>: whether its text is the newest one's.</summary>
        public List<bool> SameAsNewest { get; set; } = new List<bool>();

        public SqlObject Newest => Copies[0];

        public bool AllIdentical => Versions <= 1;

        public string VersionsLabel => AllIdentical ? "All identical" : $"{Versions} different versions";
    }

    /// <summary>
    /// Finds the duplicate scripts in a folder.
    ///
    /// Two kinds: scripts that define the same object (matched on <see cref="SqlObject.Key"/>,
    /// so <c>dbo.spX</c> and <c>[dbo].[spX]</c> are one), and scripts that define no object but
    /// hold the same text. Whether copies are "the same" is decided by
    /// <see cref="ObjectComparer.Normalize"/> under the compare options in force — a re-indented
    /// copy is the same copy when whitespace is being ignored.
    /// </summary>
    public static class Duplicates
    {
        public static List<DuplicateGroup> Find(IEnumerable<SqlObject> scripts, CompareOptions options)
        {
            options ??= new CompareOptions();
            var list   = (scripts ?? Enumerable.Empty<SqlObject>()).Where(s => s != null).ToList();
            var groups = new List<DuplicateGroup>();

            foreach (var sameObject in list.Where(s => s.Kind != ObjectKind.Script)
                                           .GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                                           .Where(g => g.Count() > 1))
            {
                groups.Add(Group(DuplicateReason.SameObject, sameObject, options, s => s.QualifiedName));
            }

            foreach (var sameText in list.Where(s => s.Kind == ObjectKind.Script && s.Definition.Trim().Length > 0)
                                         .GroupBy(s => ObjectComparer.Normalize(s.Definition, options), StringComparer.Ordinal)
                                         .Where(g => g.Count() > 1))
            {
                groups.Add(Group(DuplicateReason.SameContent, sameText, options, s => s.Name));
            }

            // The ones that have drifted apart first: those are the ones that need deciding.
            return groups.OrderBy(g => g.AllIdentical)
                         .ThenBy(g => g.Reason)
                         .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                         .ToList();
        }

        private static DuplicateGroup Group(DuplicateReason reason, IEnumerable<SqlObject> members,
                                            CompareOptions options, Func<SqlObject, string> title)
        {
            var copies = members.OrderByDescending(s => s.Modified ?? DateTime.MinValue)
                                .ThenBy(s => s.RelativePath, StringComparer.OrdinalIgnoreCase)
                                .ToList();
            var texts  = copies.Select(c => ObjectComparer.Normalize(c.Definition, options)).ToList();

            return new DuplicateGroup
            {
                Reason       = reason,
                Title        = title(copies[0]),
                Kind         = copies[0].Kind,
                Copies       = copies,
                Versions     = texts.Distinct(StringComparer.Ordinal).Count(),
                SameAsNewest = texts.Select(t => string.Equals(t, texts[0], StringComparison.Ordinal)).ToList()
            };
        }
    }
}
