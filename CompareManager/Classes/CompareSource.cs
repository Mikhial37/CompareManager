using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;

namespace CompareManager.Classes
{
    public enum SourceKind
    {
        Folder,
        Database
    }

    /// <summary>
    /// One side of a compare: a folder of scripts, or a database on a SQL Server. Saved in
    /// the settings as it is, so the last two sides come back next time — the password
    /// encrypted (<see cref="PasswordStored"/>), never in clear.
    /// </summary>
    public sealed class CompareSource
    {
        public SourceKind Kind { get; set; } = SourceKind.Folder;

        public string Folder { get; set; } = "";

        public string Server      { get; set; } = "";
        public string Database    { get; set; } = "";
        public bool   WindowsAuth { get; set; }
        public string Login       { get; set; } = "";

        /// <summary>The password as stored: encrypted by <see cref="Secret"/>, or empty.</summary>
        public string PasswordStored { get; set; } = "";

        [JsonIgnore]
        public string Password
        {
            get => Secret.Reveal(PasswordStored);
            set => PasswordStored = Secret.Protect(value);
        }

        /// <summary>Whether there is enough here to load anything.</summary>
        [JsonIgnore]
        public bool IsComplete => Kind == SourceKind.Folder
            ? !string.IsNullOrWhiteSpace(Folder)
            : !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(Database) &&
              (WindowsAuth || !string.IsNullOrWhiteSpace(Login));

        /// <summary>The long form, for the source cards: the folder's path, or "SQLDEV01 · Payroll".</summary>
        public string Describe() => !IsComplete
            ? "Not chosen yet"
            : Kind == SourceKind.Folder ? Folder.Trim() : $"{Server.Trim()} · {Database.Trim()}";

        /// <summary>The short form, for diff headers: the folder's own name, or the database's.</summary>
        public string ShortLabel() => Kind == SourceKind.Folder
            ? new DirectoryInfo(Folder.Trim().TrimEnd('\\', '/')).Name
            : $"{Database.Trim()} ({Server.Trim()})";

        public string ConnectionString(int connectTimeoutSeconds = 15, string database = null)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource      = Server.Trim(),
                InitialCatalog  = (database ?? Database ?? "").Trim(),
                ConnectTimeout  = connectTimeoutSeconds,
                ApplicationName = "Compare Manager"
            };

            if (WindowsAuth) builder.IntegratedSecurity = true;
            else
            {
                builder.UserID   = Login.Trim();
                builder.Password = Password;
            }
            return builder.ConnectionString;
        }

        /// <summary>Reads everything on this side. Throws on a missing folder or a connection that fails.</summary>
        public List<SqlObject> Load(CancellationToken cancel = default) => Kind == SourceKind.Folder
            ? FolderSource.Load(Folder.Trim(), cancel)
            : DatabaseSource.Load(this, cancel);

        public CompareSource Clone() => (CompareSource)MemberwiseClone();
    }
}
