using CompareManager.Classes;
using System;
using System.Linq;
using Xunit;

namespace CompareManager.Tests
{
    public class ObjectComparerTests
    {
        private static readonly DateTime When = new DateTime(2026, 10, 1, 9, 30, 0);

        private static SqlObject Proc(string name, string definition, DateTime? modified = null) => new SqlObject
        {
            Schema = "dbo", Name = name, Kind = ObjectKind.Procedure, Definition = definition, Modified = modified ?? When
        };

        private static CompareResult CompareOne(SqlObject left, SqlObject right, CompareOptions options = null) =>
            ObjectComparer.Compare(ObjectComparer.Catalog(new[] { left }, new[] { right }), options ?? new CompareOptions()).Single();

        [Fact]
        public void A_script_that_alters_matches_the_server_copy_that_creates()
        {
            var script = Proc("spX", "ALTER PROCEDURE dbo.spX\r\nAS\r\nSELECT 1\r\n");
            var server = Proc("spX", "CREATE PROCEDURE [dbo].[spX]\nAS\nSELECT 1");

            Assert.Equal(CompareStatus.Identical, CompareOne(script, server, new CompareOptions { IgnoreWhitespace = false }).Status);
        }

        [Fact]
        public void A_changed_body_is_different()
        {
            var result = CompareOne(Proc("spX", "CREATE PROCEDURE dbo.spX AS SELECT 1"),
                                    Proc("spX", "CREATE PROCEDURE dbo.spX AS SELECT 2"));

            Assert.Equal(CompareStatus.Different, result.Status);
        }

        [Fact]
        public void Whitespace_counts_only_when_asked()
        {
            var left  = Proc("spX", "CREATE PROCEDURE dbo.spX AS\n    SELECT a,b FROM t");
            var right = Proc("spX", "CREATE PROCEDURE dbo.spX AS\n\tSELECT a, b\n\tFROM t");

            Assert.Equal(CompareStatus.Identical, CompareOne(left, right, new CompareOptions { IgnoreWhitespace = true }).Status);
            Assert.Equal(CompareStatus.Different, CompareOne(left, right, new CompareOptions { IgnoreWhitespace = false }).Status);
        }

        [Fact]
        public void Case_and_comments_count_only_when_asked()
        {
            var left  = Proc("spX", "CREATE PROCEDURE dbo.spX AS\n-- added a note\nselect 1");
            var right = Proc("spX", "CREATE PROCEDURE dbo.spX AS\nSELECT 1");

            Assert.Equal(CompareStatus.Different, CompareOne(left, right).Status);
            Assert.Equal(CompareStatus.Identical, CompareOne(left, right, new CompareOptions { IgnoreCase = true, IgnoreComments = true }).Status);
        }

        [Fact]
        public void Same_text_with_another_date_is_reported_as_a_date_difference()
        {
            var left  = Proc("spX", "CREATE PROCEDURE dbo.spX AS SELECT 1", When);
            var right = Proc("spX", "CREATE PROCEDURE dbo.spX AS SELECT 1", When.AddDays(2));

            var result = CompareOne(left, right);

            Assert.Equal(CompareStatus.DateDiffers, result.Status);
            Assert.Equal("Right", result.Newer);
            Assert.Equal(CompareStatus.Identical, CompareOne(left, right, new CompareOptions { CompareDates = false }).Status);
        }

        [Fact]
        public void Under_a_second_apart_is_the_same_date()
        {
            Assert.False(ObjectComparer.DatesDiffer(When, When.AddMilliseconds(400)));
            Assert.True(ObjectComparer.DatesDiffer(When, When.AddSeconds(1)));
            Assert.False(ObjectComparer.DatesDiffer(When, null));
        }

        [Fact]
        public void Objects_on_one_side_only_are_reported_by_side()
        {
            var catalog = ObjectComparer.Catalog(new[] { Proc("spLeft", "x"), Proc("spBoth", "y") },
                                                 new[] { Proc("SPBOTH", "y"), Proc("spRight", "z") });

            var results = ObjectComparer.Compare(catalog, new CompareOptions()).ToDictionary(r => r.Any.Name);

            Assert.Equal(3, catalog.Count);
            Assert.Equal(CompareStatus.OnlyLeft,  results["spLeft"].Status);
            Assert.Equal(CompareStatus.OnlyRight, results["spRight"].Status);
            Assert.Equal(CompareStatus.Identical, results["spBoth"].Status);
        }

        [Fact]
        public void Only_the_entries_given_are_compared()
        {
            var catalog = ObjectComparer.Catalog(new[] { Proc("a", "1"), Proc("b", "2") }, new[] { Proc("a", "1"), Proc("b", "3") });

            var results = ObjectComparer.Compare(catalog.Where(e => e.Any.Name == "a"), new CompareOptions());

            Assert.Single(results);
            Assert.Equal("a", results[0].Any.Name);
        }

        [Fact]
        public void Scripts_that_define_no_object_match_on_their_path()
        {
            var left  = new SqlObject { Kind = ObjectKind.Script, Name = "fix.sql", RelativePath = @"Data\fix.sql",  Definition = "UPDATE t SET a = 1" };
            var right = new SqlObject { Kind = ObjectKind.Script, Name = "fix.sql", RelativePath = @"data\FIX.sql",  Definition = "UPDATE t SET a = 2" };

            Assert.Equal(CompareStatus.Different, CompareOne(left, right).Status);
        }
    }
}
