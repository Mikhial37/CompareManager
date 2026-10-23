using CompareManager.Classes;
using Xunit;

namespace CompareManager.Tests
{
    public class ScriptParserTests
    {
        [Fact]
        public void Finds_the_procedure_a_script_alters()
        {
            var found = ScriptParser.Identify("ALTER PROCEDURE [dbo].[SYMsp_Payslip] @Id int AS SELECT 1");

            Assert.Equal("dbo", found.Schema);
            Assert.Equal("SYMsp_Payslip", found.Name);
            Assert.Equal(ObjectKind.Procedure, found.Kind);
        }

        [Theory]
        [InlineData("CREATE OR ALTER FUNCTION Payroll.fnTax() RETURNS int AS BEGIN RETURN 1 END", "Payroll", "fnTax", ObjectKind.Function)]
        [InlineData("CREATE VIEW vwEmployees AS SELECT 1 AS A", "dbo", "vwEmployees", ObjectKind.View)]
        [InlineData("create trigger [hr].[trEmp] on hr.Emp after insert as select 1", "hr", "trEmp", ObjectKind.Trigger)]
        [InlineData("CREATE PROC \"dbo\".\"spQuoted\" AS SELECT 1", "dbo", "spQuoted", ObjectKind.Procedure)]
        [InlineData("CREATE TABLE Payroll..Employee (Id int)", "dbo", "Employee", ObjectKind.Table)]
        public void Reads_kind_schema_and_name(string script, string schema, string name, ObjectKind kind)
        {
            var found = ScriptParser.Identify(script);

            Assert.Equal(schema, found.Schema);
            Assert.Equal(name, found.Name);
            Assert.Equal(kind, found.Kind);
        }

        [Fact]
        public void Steps_over_temp_tables_to_the_real_object()
        {
            var found = ScriptParser.Identify("CREATE TABLE #work (Id int)\nGO\nALTER VIEW dbo.vwReal AS SELECT 1 AS A");

            Assert.Equal("vwReal", found.Name);
            Assert.Equal(ObjectKind.View, found.Kind);
        }

        [Fact]
        public void Comments_and_strings_decide_nothing()
        {
            const string script =
                "-- ALTER PROCEDURE dbo.Commented\n" +
                "/* CREATE VIEW dbo.InBlock /* nested */ still a comment */\n" +
                "EXEC('CREATE PROCEDURE dbo.InString AS SELECT 1')\n" +
                "ALTER PROCEDURE dbo.TheRealOne AS SELECT 1";

            Assert.Equal("TheRealOne", ScriptParser.Identify(script).Name);
        }

        [Fact]
        public void A_data_script_defines_nothing()
        {
            Assert.Null(ScriptParser.Identify("UPDATE dbo.Employee SET Active = 0 WHERE Id = 4"));
            Assert.Null(ScriptParser.Identify("   "));
        }

        [Fact]
        public void The_batch_is_cut_at_the_GOs_around_it()
        {
            const string script =
                "USE [Payroll]\r\nGO\r\n" +
                "-- Header comment\r\n" +
                "ALTER PROCEDURE dbo.spX AS\r\nSELECT 1\r\n" +
                "GO\r\n" +
                "GRANT EXECUTE ON dbo.spX TO public\r\nGO\r\n";

            var found = ScriptParser.Identify(script);

            Assert.Equal("-- Header comment\nALTER PROCEDURE dbo.spX AS\nSELECT 1", found.Batch);
        }

        [Fact]
        public void A_GO_inside_a_comment_does_not_end_the_batch()
        {
            const string script = "CREATE VIEW dbo.v AS\n/*\nGO\n*/\nSELECT 1 AS A\nGO";

            Assert.Equal("CREATE VIEW dbo.v AS\n/*\nGO\n*/\nSELECT 1 AS A", ScriptParser.Identify(script).Batch);
        }

        [Fact]
        public void Blanking_keeps_every_position()
        {
            const string sql = "SELECT 'x--y' -- note\n/* a */ [col--name]";

            string blanked = ScriptParser.Blank(sql, blankStrings: true);

            Assert.Equal(sql.Length, blanked.Length);
            Assert.Equal("SELECT        ", blanked.Substring(0, 14));
            Assert.EndsWith("[col--name]", blanked);
            Assert.DoesNotContain("note", blanked);
        }
    }
}
