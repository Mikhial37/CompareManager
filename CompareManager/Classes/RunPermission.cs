using System.Collections.Generic;
using System.Linq;

namespace CompareManager.Classes
{
    public enum RunPermissionKind
    {
        /// <summary>Issue Manager's list says scripts may run here.</summary>
        Allowed,

        /// <summary>Not on Issue Manager's list: running needs the database name typed to confirm.</summary>
        ConfirmByTyping,

        /// <summary>Issue Manager's list has it as read-only — a live server, typically. Nothing runs.</summary>
        Refused
    }

    /// <summary>
    /// Whether scripts may be run on a database, by the same rule Issue Manager applies
    /// (<c>SqlConnectionStore.AllowsScripts</c>): every connection on its list is marked Can run
    /// or Read only, and live servers are on it as Read only. Compare Manager keeps to that list
    /// rather than keeping a second one — a server someone has said must not be changed is not
    /// changed from here either.
    ///
    /// A server that is not on the list at all is neither: it can be run on, but only after
    /// the database's name is typed, so a slip in the picker cannot reach a server nobody chose.
    /// </summary>
    public static class RunPermission
    {
        public static (RunPermissionKind Kind, string Reason) For(CompareSource target, IEnumerable<IssueManagerConnections.Entry> issueManagerList)
        {
            var matches = (issueManagerList ?? Enumerable.Empty<IssueManagerConnections.Entry>())
                          .Where(e => e.SameAs(target.Server, target.Database))
                          .ToList();

            if (matches.Count == 0)
                return (RunPermissionKind.ConfirmByTyping,
                        "Not in Issue Manager's SQL connections — you will be asked to type the database name before anything runs.");

            if (matches.Any(m => m.RunScripts))
            {
                var entry = matches.First(m => m.RunScripts);
                string name = string.IsNullOrWhiteSpace(entry.Name) ? "" : $" (\"{entry.Name}\")";
                return (RunPermissionKind.Allowed, $"Issue Manager allows scripts to run here{name}.");
            }

            var readOnly = matches.First();
            string label = string.IsNullOrWhiteSpace(readOnly.Name) ? "" : $" as \"{readOnly.Name}\"";
            return (RunPermissionKind.Refused,
                    $"Issue Manager has this connection{label} marked Read only, so scripts cannot be run on it from here. " +
                    "Change that in Issue Manager (Scripts → SQL connections) if it really is meant to take scripts.");
        }
    }
}
