using System;
using System.Collections.Generic;

namespace CompareManager.Classes
{
    /// <summary>What sort of thing an entry is. <see cref="Script"/> is a file that defines no object.</summary>
    public enum ObjectKind
    {
        Procedure,
        Function,
        View,
        Trigger,
        Table,
        Script
    }

    /// <summary>
    /// One thing found on one side of a compare: an object a database holds, or the object a
    /// script in a folder defines — or, for a script that defines none (a data fix, say), the
    /// script itself, known by its path under the folder.
    ///
    /// Both sides are read into this one shape so the compare never has to know where an
    /// entry came from: a procedure on a server and the script that creates it match on
    /// <see cref="Key"/>, and their <see cref="Definition"/>s are put side by side.
    /// </summary>
    public sealed class SqlObject
    {
        public string     Schema { get; set; } = "dbo";
        public string     Name   { get; set; } = "";
        public ObjectKind Kind   { get; set; }

        /// <summary>
        /// The text that defines it. From a database, <c>sys.sql_modules.definition</c> (or,
        /// for a table, a script built from its columns); from a script, the batch holding
        /// the CREATE or ALTER. A <see cref="ObjectKind.Script"/> entry holds the whole file.
        /// </summary>
        public string Definition { get; set; } = "";

        /// <summary>
        /// For a table, its columns in the one shape both sides share (<see cref="TableStructure"/>):
        /// built from the catalog on a server, read from the CREATE TABLE in a script. Null for
        /// anything else, and for a script that alters a table without creating it. When both
        /// sides have one, it is what is compared and shown, rather than the scripts' text.
        /// </summary>
        public string Structure { get; set; }

        /// <summary>When it last changed: <c>modify_date</c> on a server, the file's last write time in a folder.</summary>
        public DateTime? Modified { get; set; }

        /// <summary>When it was made: <c>create_date</c> on a server, the file's creation time in a folder.</summary>
        public DateTime? Created { get; set; }

        /// <summary>
        /// The latest date written in its comments — the Change Control header's date, the
        /// newest line of the modification history (<see cref="ScriptDates"/>). From the whole
        /// file for a script; from the stored definition on a server. Null when it gives none.
        /// </summary>
        public DateTime? ScriptDate { get; set; }

        /// <summary>The file it was read from, or "server · database". For the tooltips, the export and WinMerge.</summary>
        public string Origin { get; set; } = "";

        /// <summary>True when <see cref="Origin"/> is a file on disk that WinMerge can open as it is.</summary>
        public bool IsFile { get; set; }

        /// <summary>
        /// Other scripts in the same folder that define the same object. The newest one is
        /// the entry; these are listed so nobody is surprised which copy was compared.
        /// </summary>
        public List<string> OtherCopies { get; } = new List<string>();

        /// <summary>For a <see cref="ObjectKind.Script"/> entry, its path under the folder — which is what it is matched on.</summary>
        public string RelativePath { get; set; } = "";

        /// <summary>
        /// What it is matched on across the two sides: <c>obj:dbo.symsp_payslip</c> for an
        /// object, <c>file:data\fix.sql</c> for a script. Case-insensitive, as SQL Server and
        /// Windows both are.
        /// </summary>
        public string Key => Kind == ObjectKind.Script
            ? "file:" + (RelativePath ?? "").Replace('/', '\\').ToLowerInvariant()
            : "obj:" + QualifiedName.ToLowerInvariant();

        public string QualifiedName => $"{Schema}.{Name}";

        /// <summary>What the lists show: <c>dbo.SYMsp_Payslip</c>, or the script's path.</summary>
        public string DisplayName => Kind == ObjectKind.Script ? RelativePath : QualifiedName;

        /// <summary>"Stored procedure", "Function", … as people say them.</summary>
        public string KindLabel => Label(Kind);

        public static string Label(ObjectKind kind) => kind switch
        {
            ObjectKind.Procedure => "Stored procedure",
            ObjectKind.Function  => "Function",
            ObjectKind.View      => "View",
            ObjectKind.Trigger   => "Trigger",
            ObjectKind.Table     => "Table",
            _                    => "Script"
        };

        public override string ToString() => DisplayName;
    }
}
