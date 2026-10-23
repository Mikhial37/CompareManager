using CompareManager.Classes;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CompareManager.Tests
{
    public class SourcesTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "CompareManagerTests", Guid.NewGuid().ToString("N"));

        public SourcesTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); } catch { /* temp; left for the OS */ }
        }

        private string Write(string relative, string text, DateTime modified)
        {
            string path = Path.Combine(_folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
            File.SetLastWriteTime(path, modified);
            return path;
        }

        [Fact]
        public void A_folder_reads_into_objects_and_scripts()
        {
            Write(@"Procs\001 spPay.sql", "ALTER PROCEDURE dbo.spPay AS SELECT 1", new DateTime(2026, 1, 1));
            Write(@"Data\fix.sql", "UPDATE dbo.t SET a = 1", new DateTime(2026, 1, 2));
            Write(@"notes.txt", "not a script", new DateTime(2026, 1, 3));

            var objects = FolderSource.Load(_folder);

            Assert.Equal(2, objects.Count);
            var proc = objects.Single(o => o.Kind == ObjectKind.Procedure);
            Assert.Equal("obj:dbo.sppay", proc.Key);
            Assert.True(proc.IsFile);
            Assert.Equal(new DateTime(2026, 1, 1), proc.Modified);
            Assert.Equal(@"file:data\fix.sql", objects.Single(o => o.Kind == ObjectKind.Script).Key);
        }

        [Fact]
        public void Two_scripts_for_one_object_keep_the_newest()
        {
            string older = Write(@"Original\spPay.sql", "CREATE PROCEDURE dbo.spPay AS SELECT 1", new DateTime(2026, 1, 1));
            Write(@"Edited\spPay.sql", "ALTER PROCEDURE dbo.spPay AS SELECT 2", new DateTime(2026, 3, 1));

            var proc = FolderSource.Load(_folder).Single();

            Assert.Contains("SELECT 2", proc.Definition);
            Assert.Equal(new[] { older }, proc.OtherCopies);
        }

        [Fact]
        public void Skips_build_and_git_folders()
        {
            Write(@".git\x.sql", "CREATE VIEW dbo.vGit AS SELECT 1 AS A", DateTime.Now);
            Write(@"bin\y.sql", "CREATE VIEW dbo.vBin AS SELECT 1 AS A", DateTime.Now);

            Assert.Empty(FolderSource.Load(_folder));
        }

        [Fact]
        public void A_missing_folder_says_so()
        {
            Assert.Throws<DirectoryNotFoundException>(() => FolderSource.Load(Path.Combine(_folder, "nope")));
        }

        [Fact]
        public void Table_scripts_are_built_the_same_way_every_time()
        {
            var lines = new[]
            {
                DatabaseSource.ColumnLine("Id", "int", 4, 10, 0, nullable: false, identity: true, defaultSql: null, computed: null),
                DatabaseSource.ColumnLine("Name", "nvarchar", 100, 0, 0, nullable: true, identity: false, defaultSql: "('')", computed: null),
                DatabaseSource.ColumnLine("Rate", "decimal", 9, 18, 2, nullable: false, identity: false, defaultSql: null, computed: null),
                DatabaseSource.ColumnLine("Notes", "varchar", -1, 0, 0, nullable: true, identity: false, defaultSql: null, computed: null)
            };

            Assert.Equal(
                "CREATE TABLE [dbo].[Employee]\n(\n" +
                "    [Id] int IDENTITY NOT NULL,\n" +
                "    [Name] nvarchar(50) NULL DEFAULT (''),\n" +
                "    [Rate] decimal(18,2) NOT NULL,\n" +
                "    [Notes] varchar(max) NULL\n" +
                ")",
                DatabaseSource.TableScript("dbo", "Employee", lines));
        }

        [Fact]
        public void A_saved_password_is_encrypted_and_reads_back()
        {
            var source = new CompareSource { Kind = SourceKind.Database, Server = "S", Database = "D", Login = "L", Password = "p;w=d'\"" };

            Assert.StartsWith("enc:v1:", source.PasswordStored);
            Assert.DoesNotContain("p;w", source.PasswordStored);

            var settings = AppSettings.FromJson(new AppSettings { Left = source }.ToJson());
            Assert.Equal("p;w=d'\"", settings.Left.Password);
        }

        [Fact]
        public void Issue_Manager_connections_are_read_without_their_extra_fields()
        {
            const string json = @"[
                { ""Server"": ""SQLDEV01"", ""Database"": ""Payroll"", ""Login"": ""cc_reader"",
                  ""RunScripts"": true, ""Name"": ""Payroll Dev"", ""IsDevServer"": true, ""ReportsShare"": """", ""PasswordStored"": ""clear"" },
                { ""Server"": """", ""Database"": ""x"" }
            ]";

            var entry = IssueManagerConnections.FromJson(json).Single();

            Assert.Equal("Payroll Dev — SQLDEV01 · Payroll", entry.ToString());
            Assert.Equal("clear", entry.ToSource().Password);
        }

        [Fact]
        public void Recent_folders_are_newest_first_without_repeats()
        {
            var settings = new AppSettings();
            settings.RememberFolder(@"C:\a");
            settings.RememberFolder(@"C:\b");
            settings.RememberFolder(@"c:\A");

            Assert.Equal(new[] { @"c:\A", @"C:\b" }, settings.RecentFolders);
        }
    }
}
