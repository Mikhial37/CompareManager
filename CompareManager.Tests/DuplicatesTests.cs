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
    }
}
