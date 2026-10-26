using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CompareManager.Classes
{
    public enum RunState
    {
        Waiting,
        Running,
        Succeeded,
        CheckedOk,
        Failed,
        Skipped,
        NotRun,
        Cancelled
    }

    /// <summary>One script in the runner's list, and how its run went.</summary>
    public sealed class RunItem
    {
        public string Path         { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public bool   Ticked       { get; set; } = true;

        public RunState     State    { get; set; } = RunState.Waiting;
        public TimeSpan     Duration { get; set; }
        public int          Rows     { get; set; }
        public List<string> Messages { get; } = new List<string>();

        /// <summary>The script as it will be sent — aimed, split — read when the list is built and again just before it runs.</summary>
        public PreparedScript Prepared { get; set; }

        public string StateLabel => State switch
        {
            RunState.Waiting   => "",
            RunState.Running   => "Running…",
            RunState.Succeeded => "Ran",
            RunState.CheckedOk => "Checks out",
            RunState.Failed    => "Failed",
            RunState.Skipped   => "Skipped",
            RunState.NotRun    => "Not run",
            _                  => "Cancelled"
        };
    }

    public sealed class RunOptions
    {
        /// <summary>
        /// Compile only: <c>SET NOEXEC ON</c> first, so every batch is parsed and compiled and
        /// none is executed. Catches syntax errors and missing columns in what is being created,
        /// without changing anything.
        /// </summary>
        public bool CheckOnly { get; set; }

        /// <summary>Re-aim each script's opening USE (and fill a <c>--USE []</c>) at the chosen database.</summary>
        public bool AimAtDatabase { get; set; } = true;

        /// <summary>Stop at the first failure. On by default: scripts are numbered in dependency order.</summary>
        public bool StopOnError { get; set; } = true;

        /// <summary>Seconds a batch may run; 0 waits as long as it takes, which a data load may need.</summary>
        public int TimeoutSeconds { get; set; }
    }

    /// <summary>
    /// Runs a folder's scripts on one database, in the order the folder lists them
    /// (<see cref="PathOrder"/>, subfolders included: <c>01 - Setup</c> before <c>02 - Tables</c>).
    ///
    /// <list type="bullet">
    /// <item>Each script gets a connection of its own, opened on the chosen database, so one
    /// script's <c>SET</c> options or <c>USE</c> do not carry into the next.</item>
    /// <item>A script runs batch by batch (<see cref="ScriptBatches"/>) and stops at its first
    /// error, as <c>sqlcmd -b</c> does. If that leaves a transaction open — a wrapper that began
    /// one and never reached its CATCH — it is rolled back, and the log says so, rather than
    /// left holding locks on a shared server.</item>
    /// <item>No transaction is put around a script: Change Control scripts manage their own,
    /// and one around them would change what their COMMIT and ROLLBACK mean.</item>
    /// <item>PRINT output and rows affected are kept per script; the whole run is written to
    /// <c>%LOCALAPPDATA%\CompareManager\run_logs</c>.</item>
    /// </list>
    /// </summary>
    public static class ScriptRunner
    {
        public static string LogFolder { get; } = System.IO.Path.Combine(AppSettings.Folder, "run_logs");

        /// <summary>Every <c>.sql</c> below the folder, in run order, each read and prepared for the target.</summary>
        public static List<RunItem> ListScripts(string folder, string aimAt)
        {
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException($"The folder \"{folder}\" does not exist or cannot be reached.");

            return Directory.EnumerateFiles(folder, "*.sql", SearchOption.AllDirectories)
                .Select(path => new RunItem { Path = path, RelativePath = System.IO.Path.GetRelativePath(folder, path) })
                .OrderBy(item => item.RelativePath, PathOrder.Instance)
                .Select(item => { item.Prepared = TryPrepare(item.Path, aimAt); return item; })
                .ToList();
        }

        private static PreparedScript TryPrepare(string path, string aimAt)
        {
            try { return ScriptBatches.Prepare(File.ReadAllText(path), aimAt); }
            catch (IOException)                 { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// Runs the ticked scripts in order. Blocks; call it off the UI thread. <paramref name="changed"/>
        /// is called each time a script's state moves on. Returns the log file's path.
        /// </summary>
        public static string Run(IReadOnlyList<RunItem> items, CompareSource target, RunOptions options,
                                 Action<RunItem> changed, CancellationToken cancel)
        {
            var ticked = items.Where(i => i.Ticked).ToList();
            foreach (var item in ticked) { item.State = RunState.Waiting; item.Messages.Clear(); item.Rows = 0; item.Duration = TimeSpan.Zero; }

            bool stopped = false;
            foreach (var item in ticked)
            {
                if (cancel.IsCancellationRequested) { item.State = RunState.Cancelled; changed?.Invoke(item); continue; }
                if (stopped)                        { item.State = RunState.NotRun;    changed?.Invoke(item); continue; }

                item.State = RunState.Running;
                changed?.Invoke(item);

                RunOne(item, target, options, cancel);
                changed?.Invoke(item);

                if (item.State == RunState.Failed && options.StopOnError) stopped = true;
            }

            return WriteLog(items, target, options);
        }

        private static void RunOne(RunItem item, CompareSource target, RunOptions options, CancellationToken cancel)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                // Read again: the list may have been built a while ago, and the file is what runs.
                item.Prepared = ScriptBatches.Prepare(File.ReadAllText(item.Path), options.AimAtDatabase ? target.Database : null);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                item.State = RunState.Failed;
                item.Messages.Add("Could not read the script: " + ex.Message);
                return;
            }

            if (options.CheckOnly && item.Prepared.SwitchesExecution)
            {
                item.State = RunState.Skipped;
                item.Messages.Add("Not checked: the script sets NOEXEC or PARSEONLY itself, and could switch execution back on part way. " +
                                  "Check it by eye, or on a dev server.");
                return;
            }

            using var connection = new SqlConnection(target.ConnectionString());
            connection.FireInfoMessageEventOnUserErrors = false;
            connection.InfoMessage += (s, e) =>
            {
                lock (item.Messages) foreach (SqlError message in e.Errors) item.Messages.Add(message.Message);
            };

            ScriptBatch current = null;
            try
            {
                connection.Open();

                if (options.CheckOnly)
                    using (var noexec = new SqlCommand("SET NOEXEC ON;", connection)) noexec.ExecuteNonQuery();

                foreach (var batch in item.Prepared.Batches)
                {
                    current = batch;
                    for (int n = 0; n < batch.Repeat; n++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        using var command = new SqlCommand(batch.Text, connection) { CommandTimeout = options.TimeoutSeconds };
                        using var registration = cancel.Register(() => { try { command.Cancel(); } catch { /* already finished */ } });

                        int rows = command.ExecuteNonQuery();
                        if (rows > 0) item.Rows += rows;
                    }
                }

                item.State = options.CheckOnly ? RunState.CheckedOk : RunState.Succeeded;
            }
            catch (SqlException ex) when (cancel.IsCancellationRequested)
            {
                item.State = RunState.Cancelled;
                item.Messages.Add("Cancelled while running: " + ex.Message);
                RollBackIfOpen(connection, item);
            }
            catch (OperationCanceledException)
            {
                item.State = RunState.Cancelled;
                item.Messages.Add("Cancelled before the next batch.");
                RollBackIfOpen(connection, item);
            }
            catch (SqlException ex)
            {
                item.State = RunState.Failed;
                foreach (SqlError error in ex.Errors)
                {
                    if (error.Class <= 10) continue;   // informational; already in the messages
                    string where = current != null && error.LineNumber > 0
                        ? $"line {current.StartLine + error.LineNumber - 1}: "
                        : "";
                    item.Messages.Add($"Error {error.Number}, {where}{error.Message}");
                }
                RollBackIfOpen(connection, item);
            }
            catch (Exception ex)
            {
                item.State = RunState.Failed;
                item.Messages.Add(ex.Message);
            }
            finally
            {
                item.Duration = watch.Elapsed;
            }
        }

        /// <summary>Rolls back a transaction a failed script left open, and says so.</summary>
        private static void RollBackIfOpen(SqlConnection connection, RunItem item)
        {
            if (connection.State != System.Data.ConnectionState.Open) return;
            try
            {
                using var count = new SqlCommand("SELECT @@TRANCOUNT", connection) { CommandTimeout = 30 };
                if (Convert.ToInt32(count.ExecuteScalar()) > 0)
                {
                    using var rollback = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", connection) { CommandTimeout = 60 };
                    rollback.ExecuteNonQuery();
                    item.Messages.Add("The script left a transaction open; it was rolled back.");
                }
            }
            catch (Exception ex)
            {
                item.Messages.Add("Could not check for an open transaction: " + ex.Message);
            }
        }

        // ── The log ───────────────────────────────────────────────────────────

        public static string Report(IReadOnlyList<RunItem> items, CompareSource target, RunOptions options)
        {
            var text = new StringBuilder();
            text.AppendLine($"Compare Manager — {(options.CheckOnly ? "check (SET NOEXEC ON, nothing executed)" : "script run")}");
            text.AppendLine($"When:     {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            text.AppendLine($"Target:   {target.Describe()}");
            text.AppendLine($"Login:    {(target.WindowsAuth ? "Windows (" + Environment.UserName + ")" : target.Login)}");
            text.AppendLine($"Options:  {(options.AimAtDatabase ? "opening USE aimed at the target" : "USE lines as written")}, " +
                            $"{(options.StopOnError ? "stop at first failure" : "carry on after failures")}");
            text.AppendLine();

            foreach (var item in items.Where(i => i.Ticked))
            {
                text.AppendLine($"[{item.StateLabel,-10}] {item.RelativePath}  ({item.Duration.TotalSeconds:0.0}s, {item.Rows} rows)");
                foreach (string message in item.Messages) text.AppendLine("             " + message);
            }

            var counts = items.Where(i => i.Ticked).GroupBy(i => i.StateLabel).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}");
            text.AppendLine();
            text.AppendLine(string.Join(", ", counts));
            return text.ToString();
        }

        private static string WriteLog(IReadOnlyList<RunItem> items, CompareSource target, RunOptions options)
        {
            try
            {
                Directory.CreateDirectory(LogFolder);
                string name = $"{DateTime.Now:yyyy-MM-dd HHmmss} {(options.CheckOnly ? "check" : "run")} {Safe(target.Database)}.txt";
                string path = System.IO.Path.Combine(LogFolder, name);
                File.WriteAllText(path, Report(items, target, options), new UTF8Encoding(true));
                return path;
            }
            catch
            {
                return null;
            }
        }

        private static string Safe(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            return new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
