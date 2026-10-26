using CompareManager.Classes;
using System;
using System.Linq;
using Xunit;

namespace CompareManager.Tests
{
    public class ChangeControlTests
    {
        private const string Declares =
            "DECLARE @User VARCHAR(50), @IssueNumber VARCHAR(20), @ScriptName VARCHAR(100), @IDENTITY INT\n" +
            "SET @User = 'Alex Brown'\nSET @IssueNumber = 'SYM0000002'\nSET @ScriptName = 'SYM0000002-0604 AB fn_X.sql'\n";

        // ── The header ────────────────────────────────────────────────────────

        [Fact]
        public void Reads_issue_script_name_and_person_from_the_header()
        {
            var h = ChangeControlHeader.Read(Declares, "renamed.sql");

            Assert.Equal("SYM0000002", h.Issue);
            Assert.Equal("SYM0000002-0604 AB fn_X.sql", h.ScriptName);
            Assert.Equal("Alex Brown", h.Person);
            Assert.True(h.FromHeader);
        }

        [Fact]
        public void The_start_and_end_form_has_Change_Control()
        {
            var h = ChangeControlHeader.Read(Declares +
                "Exec SYMsp_SymplexityChangeCTRL 0, @User, @IssueNumber, @ScriptName\nBegin Try\n" +
                "Exec SYMsp_SymplexityChangeCTRL @IDENTITY, @User, @IssueNumber, @ScriptName\nEnd Try", "x.sql");

            Assert.True(h.HasChangeControl);
            Assert.Equal("Yes", h.ChangeControlLabel);
        }

        [Fact]
        public void The_one_call_form_has_Change_Control()
        {
            var h = ChangeControlHeader.Read(Declares + "Set @Identity = -1\nExec SYMsp_SymplexityChangeCTRL 1, @User, @IssueNumber, @ScriptName", "x.sql");

            Assert.True(h.HasChangeControl);
        }

        [Theory]
        [InlineData("SELECT 1", "Missing")]
        [InlineData("-- Exec SYMsp_SymplexityChangeCTRL 0, @User\nSELECT 1", "Missing")]
        [InlineData("Exec SYMsp_SymplexityChangeCTRL 0, @User\nSELECT 1", "No completing call")]
        [InlineData("Exec dbo.SYMsp_SymplexityChangeCTRL 1, @User", "End call only — no row is inserted")]
        public void What_is_wrong_with_Change_Control_is_said(string body, string label)
        {
            var h = ChangeControlHeader.Read(body, "x.sql");

            Assert.False(h.HasChangeControl);
            Assert.Equal(label, h.ChangeControlLabel);
        }

        [Fact]
        public void Without_a_header_the_file_name_is_used()
        {
            var h = ChangeControlHeader.Read("UPDATE t SET a = 1", "SYM0000003-2001 CD Fix flags.sql");

            Assert.Equal("SYM0000003", h.Issue);
            Assert.Equal("SYM0000003-2001 CD Fix flags.sql", h.ScriptName);
            Assert.Equal("CD", h.Person);
            Assert.False(h.FromHeader);
            Assert.False(h.PersonFromHeader);
        }

        [Fact]
        public void The_templates_placeholder_is_nobody()
        {
            var h = ChangeControlHeader.Read("SET @User = 'NE Body'\nSET @IssueNumber = 'SYM12345'", "SYM12345-01 EF thing.sql");

            Assert.Equal("EF", h.Person);
            Assert.False(h.PersonFromHeader);
        }

        // ── People ────────────────────────────────────────────────────────────

        [Fact]
        public void Scripts_are_counted_per_person_whatever_the_case_and_initials_are_matched_to_names()
        {
            var headers = new[]
            {
                ChangeControlHeader.Read(Declares + "Set @Identity = -1\nExec SYMsp_SymplexityChangeCTRL 1", "SYM0000002-01 AB a.sql"),
                ChangeControlHeader.Read(Declares.Replace("Alex Brown", "alex brown"), "SYM0000002-02 AB b.sql"),
                ChangeControlHeader.Read("SELECT 1", "SYM0000002-03 AB c.sql"),                      // no header: initials only
                ChangeControlHeader.Read("SET @User = 'Cara Dee'\nSet @Identity = -1\nExec SYMsp_SymplexityChangeCTRL 1", "SYM0000004-01 CD d.sql")
            };

            var people = People.Count(headers);

            Assert.Equal(2, people.Count);
            Assert.Equal("Alex Brown", people[0].Person);
            Assert.Equal(3, people[0].Scripts);
            Assert.Equal(2, people[0].WithoutChangeControl);
            Assert.Equal("Cara Dee 1", people[1].ToString());
        }

        // ── History ───────────────────────────────────────────────────────────

        private static ChangeControlRun Run(string issue, string script, string status, int day) => new ChangeControlRun
        {
            Issue = issue, ScriptName = script, Status = status, Started = new DateTime(2026, 10, day, 9, 0, 0), User = "Alex Brown"
        };

        [Fact]
        public void A_scripts_runs_are_matched_on_issue_and_name_without_case_or_extension()
        {
            var header = ChangeControlHeader.Read(Declares, "x.sql");
            var runs = new[]
            {
                Run("SYM0000002", "sym0000002-0604 ab FN_X",     "Completed", 20),
                Run("SYM0000002", "SYM0000002-0604 AB fn_X.sql", "Failed",    6),
                Run("SYM0000002", "SYM0000002-0605 AB other.sql", "Completed", 7),
                Run("SYM0000009", "SYM0000002-0604 AB fn_X.sql", "Completed", 8)
            };

            var mine = ChangeControlHistory.For(header, runs);

            Assert.Equal(2, mine.Count);
            Assert.Equal("failed", mine[0].Outcome);
            Assert.Equal("completed", mine.Last().Outcome);
            Assert.Equal("2 (1 failed)", ChangeControlHistory.Summary(mine));
        }

        [Fact]
        public void A_run_still_marked_Complete_never_finished()
        {
            Assert.Equal("started, never finished", Run("S", "x", "Complete", 1).Outcome);
            Assert.Equal("1 (1 unfinished)", ChangeControlHistory.Summary(new[] { Run("S", "x", "Complete", 1) }));
            Assert.Equal("", ChangeControlHistory.Summary(Array.Empty<ChangeControlRun>()));
        }
    }
}
