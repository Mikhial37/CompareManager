using CompareManager.Classes;
using System.Linq;
using Xunit;

namespace CompareManager.Tests
{
    public class LineDiffTests
    {
        [Fact]
        public void Identical_text_is_all_same_rows()
        {
            var rows = LineDiff.Compare("a\nb\nc", "a\r\nb\r\nc");

            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal(DiffKind.Same, r.Kind));
        }

        [Fact]
        public void An_edited_line_is_one_changed_row()
        {
            var rows = LineDiff.Compare("a\nb\nc", "a\nB\nc");

            Assert.Equal(new[] { DiffKind.Same, DiffKind.Changed, DiffKind.Same }, rows.Select(r => r.Kind));
            Assert.Equal("b", rows[1].LeftText);
            Assert.Equal("B", rows[1].RightText);
            Assert.Equal(2, rows[1].LeftNumber);
        }

        [Fact]
        public void Insertions_and_removals_keep_their_line_numbers()
        {
            var rows = LineDiff.Compare("a\nb\nc\nd", "a\nc\nd\ne");

            Assert.Equal(new[] { DiffKind.Same, DiffKind.Removed, DiffKind.Same, DiffKind.Same, DiffKind.Added }, rows.Select(r => r.Kind));
            Assert.Equal(2, rows[1].LeftNumber);
            Assert.Null(rows[1].RightNumber);
            Assert.Equal(4, rows[4].RightNumber);
            Assert.Equal("e", rows[4].RightText);
        }

        [Fact]
        public void Every_line_of_both_sides_appears_once_in_order()
        {
            string left  = string.Join("\n", Enumerable.Range(0, 300).Select(i => i % 7 == 0 ? "x" + i : "line " + i));
            string right = string.Join("\n", Enumerable.Range(0, 300).Select(i => i % 11 == 0 ? "y" + i : "line " + i));

            var rows = LineDiff.Compare(left, right);

            Assert.Equal(Enumerable.Range(1, 300), rows.Where(r => r.LeftNumber.HasValue).Select(r => r.LeftNumber.Value));
            Assert.Equal(Enumerable.Range(1, 300), rows.Where(r => r.RightNumber.HasValue).Select(r => r.RightNumber.Value));
            Assert.All(rows.Where(r => r.Kind == DiffKind.Same), r => Assert.Equal(r.LeftText, r.RightText));
        }

        [Fact]
        public void A_key_can_set_whitespace_aside()
        {
            var rows = LineDiff.Compare("SELECT  a", "SELECT a", key: s => string.Join(" ", s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)));

            Assert.Equal(DiffKind.Same, rows.Single().Kind);
        }
    }
}
