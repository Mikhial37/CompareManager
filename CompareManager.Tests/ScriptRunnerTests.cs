using CompareManager.Classes;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CompareManager.Tests
{
    public class ScriptRunnerTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "CompareManagerTests", Guid.NewGuid().ToString("N"));

        public ScriptRunnerTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); } catch { /* temp; left for the OS */ }
        }

        // ── Batches ───────────────────────────────────────────────────────────

        [Fact]
        public void Splits_on_GO_lines_only_and_keeps_line_numbers()
        {
            const string script =
                "USE [Payroll]\r\nGO\r\n" +                       // lines 1-2
                "/*\r\nGO\r\n*/\r\nSELECT 'GO'\r\n" +              // lines 3-6: a GO in a comment and a string
                "go\r\n" +                                          // line 7
                "\r\n\r\nPRINT 1\r\nGO 3\r\n";                      // lines 8-11

            var batches = ScriptBatches.Split(script);

            Assert.Equal(3, batches.Count);
            Assert.Equal(1, batches[0].StartLine);
            Assert.Equal(3, batches[1].StartLine);
            Assert.Contains("SELECT 'GO'", batches[1].Text);
            Assert.Equal(8, batches[2].StartLine);
            Assert.Equal(3, batches[2].Repeat);
        }

        [Fact]
        public void The_opening_USE_is_aimed_at_the_chosen_database()
        {
            var prepared = ScriptBatches.Prepare("\r\nUSE [Client Live] \r\nGO\r\nALTER VIEW dbo.v AS SELECT 1 AS A\r\n", "Client Dev");

            Assert.True(prepared.Aimed);
            Assert.Equal("Client Live", prepared.OriginalUse);
            Assert.StartsWith("\r\nUSE [Client Dev]\r\nGO\r\n", prepared.Text);
            Assert.Empty(prepared.LaterUses);
        }

        [Fact]
        public void A_USE_further_down_is_left_alone_but_reported()
        {
            var prepared = ScriptBatches.Prepare("USE [A]\nGO\nSELECT 1\nGO\nUSE [B]\nGO\nSELECT 2", "Chosen");

            Assert.Contains("USE [B]", prepared.Text);
            Assert.Equal((5, "B"), Assert.Single(prepared.LaterUses));
        }

        [Fact]
        public void The_placeholder_is_filled_and_its_GO_markers_uncommented()
        {
            var prepared = ScriptBatches.Prepare("--USE []\n--GO\nALTER VIEW dbo.v AS SELECT 1 AS A\n--GO\n", "Client Dev");

            Assert.True(prepared.Aimed);
            Assert.Equal("USE [Client Dev]\nGO\nALTER VIEW dbo.v AS SELECT 1 AS A\nGO\n", prepared.Text);
            Assert.Equal(2, prepared.Batches.Count);
        }

        [Fact]
        public void Without_a_database_nothing_is_aimed()
        {
            var prepared = ScriptBatches.Prepare("USE [A]\nGO\nSELECT 1", null);

            Assert.False(prepared.Aimed);
            Assert.Equal("A", prepared.OriginalUse);
            Assert.Contains("USE [A]", prepared.Text);
        }

        [Fact]
        public void A_script_that_switches_NOEXEC_itself_is_spotted_but_not_a_comment_about_it()
        {
            Assert.True(ScriptBatches.Prepare("IF @@ERROR <> 0 SET NOEXEC ON\nGO\nSET NOEXEC OFF", null).SwitchesExecution);
            Assert.False(ScriptBatches.Prepare("-- SET NOEXEC ON when testing\nSELECT 1", null).SwitchesExecution);
        }

        // ── The folder ────────────────────────────────────────────────────────

        [Fact]
        public void A_folder_is_listed_in_the_order_it_runs()
        {
            foreach (string path in new[] { @"10 - Data\b.sql", @"02 - Tables\x.sql", @"02 - Tables\a10.sql", @"02 - Tables\a9.sql", "readme.txt" })
            {
                string full = Path.Combine(_folder, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, "SELECT 1");
            }

            var items = ScriptRunner.ListScripts(_folder, "Chosen");

            Assert.Equal(new[] { @"02 - Tables\a9.sql", @"02 - Tables\a10.sql", @"02 - Tables\x.sql", @"10 - Data\b.sql" },
                         items.Select(i => i.RelativePath));
            Assert.All(items, i => Assert.NotNull(i.Prepared));
        }

        // ── Where scripts may run ─────────────────────────────────────────────

        private static readonly IssueManagerConnections.Entry[] IssueManagerList =
        {
            new IssueManagerConnections.Entry { Name = "Payroll Dev",  Server = "SQLDEV01",  Database = "Payroll", RunScripts = true },
            new IssueManagerConnections.Entry { Name = "Payroll Live", Server = "SQLLIVE01", Database = "Payroll", RunScripts = false }
        };

        private static CompareSource Db(string server, string database) =>
            new CompareSource { Kind = SourceKind.Database, Server = server, Database = database, Login = "x" };

        [Fact]
        public void Scripts_run_where_Issue_Manager_allows_them()
        {
            Assert.Equal(RunPermissionKind.Allowed, RunPermission.For(Db("sqldev01", "payroll"), IssueManagerList).Kind);
        }

        [Fact]
        public void A_read_only_connection_is_refused()
        {
            var (kind, reason) = RunPermission.For(Db("SQLLIVE01", "Payroll"), IssueManagerList);

            Assert.Equal(RunPermissionKind.Refused, kind);
            Assert.Contains("Payroll Live", reason);
        }

        [Fact]
        public void An_unknown_database_needs_its_name_typed()
        {
            Assert.Equal(RunPermissionKind.ConfirmByTyping, RunPermission.For(Db("SQLDEV01", "Other"), IssueManagerList).Kind);
            Assert.Equal(RunPermissionKind.ConfirmByTyping, RunPermission.For(Db("SQLDEV01", "Payroll"), null).Kind);
        }

        [Fact]
        public void The_runner_window_can_be_built()
        {
            using var form = new ScriptRunnerForm(new AppSettings(), null, Db("SQLDEV01", "Payroll"));
            Assert.Contains("Run scripts", form.Text);
        }
    }
}
