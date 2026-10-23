using CompareManager.Classes;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CompareManager.Tests
{
    public class DuplicatesTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "CompareManagerTests", Guid.NewGuid().ToString("N"));

        public DuplicatesTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); } catch { /* temp; left for the OS */ }
        }

        private void Write(string relative, string text, DateTime modified)
        {
            string path = Path.Combine(_folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
            File.SetLastWriteTime(path, modified);
        }

        [Fact]
        public void Scripts_for_one_object_are_a_group_newest_first()
        {
            Write(@"Original\spPay.sql", "CREATE PROCEDURE dbo.spPay AS SELECT 1", new DateTime(2026, 1, 1));
            Write(@"Edited\001 spPay.sql", "ALTER PROCEDURE [dbo].[spPay] AS SELECT 2", new DateTime(2026, 3, 1));
            Write(@"Testing\spPay.sql", "ALTER PROCEDURE dbo.spPay AS SELECT 1", new DateTime(2026, 2, 1));
            Write(@"Edited\002 spOther.sql", "ALTER PROCEDURE dbo.spOther AS SELECT 1", new DateTime(2026, 3, 1));

            var group = Duplicates.Find(FolderSource.ReadAll(_folder), new CompareOptions()).Single();

            Assert.Equal(DuplicateReason.SameObject, group.Reason);
            Assert.Equal("dbo.spPay", group.Title);
            Assert.Equal(new[] { @"Edited\001 spPay.sql", @"Testing\spPay.sql", @"Original\spPay.sql" },
                         group.Copies.Select(c => c.RelativePath));
            Assert.Equal(2, group.Versions);
            Assert.Equal(new[] { true, false, false }, group.SameAsNewest);
        }

        [Fact]
        public void Copies_that_differ_only_in_spacing_are_identical_when_whitespace_is_ignored()
        {
            Write(@"a\v.sql", "CREATE VIEW dbo.v AS\nSELECT 1 AS A", new DateTime(2026, 1, 1));
            Write(@"b\v.sql", "ALTER VIEW dbo.v AS\n    SELECT 1 AS A", new DateTime(2026, 1, 2));

            var scripts = FolderSource.ReadAll(_folder);

            Assert.True(Duplicates.Find(scripts, new CompareOptions { IgnoreWhitespace = true }).Single().AllIdentical);
            Assert.False(Duplicates.Find(scripts, new CompareOptions { IgnoreWhitespace = false }).Single().AllIdentical);
        }

        [Fact]
        public void A_data_script_saved_twice_under_two_names_is_found()
        {
            Write(@"Data\fix flags.sql", "UPDATE dbo.Employee SET Active = 1", new DateTime(2026, 1, 1));
            Write(@"Copy of fix flags.sql", "UPDATE dbo.Employee SET Active = 1\n", new DateTime(2026, 1, 2));
            Write(@"Data\other.sql", "UPDATE dbo.Employee SET Active = 0", new DateTime(2026, 1, 3));

            var group = Duplicates.Find(FolderSource.ReadAll(_folder), new CompareOptions()).Single();

            Assert.Equal(DuplicateReason.SameContent, group.Reason);
            Assert.Equal(2, group.Copies.Count);
            Assert.True(group.AllIdentical);
        }

        [Fact]
        public void A_folder_without_duplicates_has_none()
        {
            Write("a.sql", "CREATE VIEW dbo.a AS SELECT 1 AS A", DateTime.Now);
            Write("b.sql", "CREATE VIEW dbo.b AS SELECT 1 AS A", DateTime.Now);

            Assert.Empty(Duplicates.Find(FolderSource.ReadAll(_folder), new CompareOptions()));
        }

        [Fact]
        public void Groups_whose_copies_differ_come_first()
        {
            Write(@"x\a.sql", "CREATE VIEW dbo.a AS SELECT 1 AS A", new DateTime(2026, 1, 1));
            Write(@"y\a.sql", "CREATE VIEW dbo.a AS SELECT 1 AS A", new DateTime(2026, 1, 2));
            Write(@"x\z.sql", "CREATE VIEW dbo.z AS SELECT 1 AS A", new DateTime(2026, 1, 1));
            Write(@"y\z.sql", "CREATE VIEW dbo.z AS SELECT 2 AS A", new DateTime(2026, 1, 2));

            var groups = Duplicates.Find(FolderSource.ReadAll(_folder), new CompareOptions());

            Assert.Equal(new[] { "dbo.z", "dbo.a" }, groups.Select(g => g.Title));
        }

        [Fact]
        public void An_older_script_that_runs_last_is_caught_overwriting_the_newer_one()
        {
            // Two go-live scripts alter one function. The 06 folder runs after the 02 folder,
            // so the one dated September undoes October's change when the folder is applied.
            Write(@"02 - Procs\SYM0000002-0100 AB fn_X.sql",
                  "-- 06-Oct-2026 | AB | new rule\nALTER FUNCTION dbo.fn_X() RETURNS int AS BEGIN RETURN 2 END", new DateTime(2026, 10, 6));
            Write(@"06 - Functions\SYM0000001-0604 CD fn_X.sql",
                  "-- 15-Sep-2026 | CD | old rule\nALTER FUNCTION dbo.fn_X() RETURNS int AS BEGIN RETURN 1 END", new DateTime(2026, 10, 7));

            var group = Duplicates.Find(FolderSource.ReadAll(_folder), new CompareOptions(), "Right", _folder).Single();

            Assert.Equal("Right", group.Side);
            Assert.Equal(@"02 - Procs\SYM0000002-0100 AB fn_X.sql", group.Newest.RelativePath);
            Assert.Equal(@"06 - Functions\SYM0000001-0604 CD fn_X.sql", group.RunsLast.RelativePath);
            Assert.True(group.OlderRunsLast);
        }

        [Fact]
        public void The_newest_running_last_is_not_flagged()
        {
            Write(@"01\a.sql", "-- 15-Sep-2026\nALTER VIEW dbo.v AS SELECT 1 AS A", DateTime.Now);
            Write(@"02\a.sql", "-- 06-Oct-2026\nALTER VIEW dbo.v AS SELECT 2 AS A", DateTime.Now);

            Assert.False(Duplicates.Find(FolderSource.ReadAll(_folder), new CompareOptions()).Single().OlderRunsLast);
        }

        [Fact]
        public void Run_order_follows_Explorer_with_numbers_as_numbers()
        {
            var paths = new[] { @"10 - Data\a.sql", @"2 - Tables\b.sql", @"2 - Tables\a10.sql", @"2 - Tables\a9.sql", @"2 - Tables x\a.sql" };

            Assert.Equal(new[] { @"2 - Tables\a9.sql", @"2 - Tables\a10.sql", @"2 - Tables\b.sql", @"2 - Tables x\a.sql", @"10 - Data\a.sql" },
                         paths.OrderBy(p => p, PathOrder.Instance));
        }
    }
}
