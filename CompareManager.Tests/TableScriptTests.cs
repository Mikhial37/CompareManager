using CompareManager.Classes;
using System.Linq;
using Xunit;

namespace CompareManager.Tests
{
    public class TableScriptTests
    {
        // ── Which scripts are table scripts ───────────────────────────────────

        [Fact]
        public void A_data_load_that_switches_triggers_off_is_not_a_table_script()
        {
            const string script =
                "CREATE TABLE #Employees ([Resource Tag] int)\n" +
                "INSERT INTO dbo.[Emp Group Transfer] SELECT 1\n" +
                "ALTER TABLE dbo.[Emp Termination]\nDISABLE TRIGGER SYMtr_EMP_Termination\n" +
                "UPDATE ewh SET x = 1 FROM dbo.[Emp Work History] ewh\n" +
                "ALTER TABLE dbo.[Emp Termination]\nENABLE TRIGGER SYMtr_EMP_Termination\n";

            Assert.Null(ScriptParser.Identify(script));
        }

        [Fact]
        public void Switching_checks_off_is_not_a_change_to_the_table()
        {
            Assert.Null(ScriptParser.Identify("ALTER TABLE dbo.t WITH NOCHECK CHECK CONSTRAINT ALL"));
            Assert.Null(ScriptParser.Identify("ALTER TABLE dbo.t NOCHECK CONSTRAINT FK_x"));
        }

        [Fact]
        public void Data_changed_before_the_table_statement_makes_a_data_script()
        {
            Assert.Null(ScriptParser.Identify("DELETE FROM dbo.Lookup WHERE Id = 4\nALTER TABLE dbo.Lookup ADD Active bit NULL"));
        }

        [Fact]
        public void A_table_script_that_drops_its_key_first_is_still_the_table()
        {
            const string script =
                "DECLARE @tbRT TABLE (resourcetag int)\nINSERT INTO @tbRT VALUES (1)\n" +
                "ALTER TABLE [dbo].[Emp Termination] DROP CONSTRAINT [PK_Emp Termination];\n" +
                "UPDATE STATISTICS dbo.x\n" +
                "CREATE TABLE [dbo].[Emp Termination] ([Resource Tag] int NOT NULL)";

            var found = ScriptParser.Identify(script);

            Assert.Equal(ObjectKind.Table, found.Kind);
            Assert.Equal("Emp Termination", found.Name);
        }

        [Fact]
        public void A_procedure_full_of_inserts_is_still_the_procedure()
        {
            Assert.Equal(ObjectKind.Procedure,
                ScriptParser.Identify("ALTER PROCEDURE dbo.p AS\nINSERT INTO dbo.t VALUES (1)\nCREATE TABLE dbo.made (a int)").Kind);
        }

        // ── Tables compared by their columns ──────────────────────────────────

        private const string RepositoryScript =
            "USE [Payroll]\n/* CONFIGURATION CONTROL\n* 1.11.01 | DD/MM/YY | ??? | Create */\n" +
            "DECLARE @User VARCHAR(50)\nSET @User = 'NE Body'\nExec SYMsp_ChangeCTRL 0, @User\nBegin Try\nbegin transaction\n" +
            "IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'PK_Emp Termination')\n" +
            "\tALTER TABLE [dbo].[Emp Termination] DROP CONSTRAINT [PK_Emp Termination];\n" +
            "DROP INDEX IF EXISTS [ix_Bonus Tem] ON [dbo].[Emp Termination];\n" +
            "IF OBJECT_ID('[dbo].[Emp Termination]') IS NULL BEGIN\n" +
            "CREATE TABLE [dbo].[Emp Termination] (\n" +
            "\t [Resource Tag] int NOT NULL\n" +
            "\t,[Date] datetime NOT NULL\n" +
            "\t,[Reason for Override] nvarchar(MAX) NULL -- free text\n" +
            "\t,[Rate] [decimal](18, 2) NOT NULL CONSTRAINT [DF_Rate] DEFAULT (0)\n" +
            "\t,[ID] int IDENTITY(1,1) NOT FOR REPLICATION\n" +
            "\t,CONSTRAINT [PK_Emp Termination] PRIMARY KEY CLUSTERED ([Resource Tag])\n" +
            ")\nON [fg_CoreFiles];\nEND\nExec SYMsp_ChangeCTRL 1, @User\nCOMMIT\nEnd Try\nBegin Catch\nrollback\nEnd Catch";

        [Fact]
        public void A_scripts_CREATE_TABLE_reads_as_the_catalog_would_give_it()
        {
            Assert.Equal(
                "CREATE TABLE [dbo].[Emp Termination]\n(\n" +
                "    [Resource Tag] int NOT NULL,\n" +
                "    [Date] datetime NOT NULL,\n" +
                "    [Reason for Override] nvarchar(max) NULL,\n" +
                "    [Rate] decimal(18,2) NOT NULL DEFAULT 0,\n" +
                "    [ID] int IDENTITY NOT NULL\n" +
                ")",
                TableStructure.FromScript(RepositoryScript, "dbo", "Emp Termination"));
        }

        [Fact]
        public void The_catalogs_copy_of_the_same_table_matches_it()
        {
            var lines = new[]
            {
                DatabaseSource.ColumnLine("Resource Tag", "int", 4, 10, 0, false, false, null, null),
                DatabaseSource.ColumnLine("Date", "datetime", 8, 23, 3, false, false, null, null),
                DatabaseSource.ColumnLine("Reason for Override", "nvarchar", -1, 0, 0, true, false, null, null),
                DatabaseSource.ColumnLine("Rate", "decimal", 9, 18, 2, false, false, "((0))", null),
                DatabaseSource.ColumnLine("ID", "int", 4, 10, 0, false, true, null, null)
            };

            Assert.Equal(DatabaseSource.TableScript("dbo", "Emp Termination", lines),
                         TableStructure.FromScript(RepositoryScript, "dbo", "Emp Termination"));
        }

        [Fact]
        public void Two_table_scripts_with_different_wrappers_but_the_same_columns_are_identical()
        {
            const string goLive =
                "USE [Payroll]\nSET @User = 'Someone Else'\n" +
                "CREATE TABLE dbo.[Emp Termination] ([Resource Tag] INT NOT NULL, [Date] DATETIME NOT NULL,\n" +
                "  [Reason for Override] NVARCHAR(max), [Rate] decimal(18,2) not null default ((0)), [ID] int identity)";

            var left  = FolderObject(RepositoryScript);
            var right = FolderObject(goLive);

            var result = ObjectComparer.Compare(new CatalogEntry { Key = left.Key, Left = left, Right = right },
                                                new CompareOptions { CompareDates = false, CompareCreated = false });

            Assert.True(result.ByStructure);
            Assert.Equal(CompareStatus.Identical, result.Status);
        }

        [Fact]
        public void A_column_that_changed_is_the_difference()
        {
            var left  = FolderObject(RepositoryScript);
            var right = FolderObject(RepositoryScript.Replace("nvarchar(MAX) NULL", "nvarchar(500) NULL"));

            var result = ObjectComparer.Compare(new CatalogEntry { Key = left.Key, Left = left, Right = right }, new CompareOptions());
            var changed = LineDiff.Compare(result.LeftText, result.RightText).Where(r => r.Kind != DiffKind.Same).ToList();

            Assert.Equal(CompareStatus.Different, result.Status);
            Assert.Equal("    [Reason for Override] nvarchar(500) NULL,", Assert.Single(changed).RightText);
        }

        private static SqlObject FolderObject(string script)
        {
            var found = ScriptParser.Identify(script);
            return new SqlObject
            {
                Schema = found.Schema, Name = found.Name, Kind = found.Kind, Definition = found.Batch,
                Structure = TableStructure.FromScript(script, found.Schema, found.Name)
            };
        }
    }
}
