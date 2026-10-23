using System;
using System.Collections.Generic;
using System.Linq;

namespace CompareManager.Classes
{
    public enum DiffKind
    {
        Same,
        Changed,
        Removed,
        Added
    }

    /// <summary>
    /// One row of a side-by-side diff: a line on the left, a line on the right, or both.
    /// Line numbers are 1-based; null where that side has no line on this row.
    /// </summary>
    public sealed class DiffRow
    {
        public DiffKind Kind        { get; set; }
        public int?     LeftNumber  { get; set; }
        public string   LeftText    { get; set; } = "";
        public int?     RightNumber { get; set; }
        public string   RightText   { get; set; } = "";
    }

    /// <summary>
    /// A line diff for the built-in viewer, so a difference can be read without WinMerge.
    ///
    /// Myers' algorithm, the one <c>git diff</c> uses: it finds the fewest lines to remove
    /// and add, in time and memory that grow with the size of the difference rather than of
    /// the files — two near-identical 5,000-line procedures cost almost nothing. The common
    /// start and end are stepped over first. A run of removed lines followed by added ones
    /// is shown as changed lines, paired row by row, which is how people read an edit.
    /// </summary>
    public static class LineDiff
    {
        public static List<string> Lines(string text) =>
            (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

        public static List<DiffRow> Compare(string left, string right, Func<string, string> key = null) =>
            Compare(Lines(left), Lines(right), key);

        /// <summary>
        /// The rows of a side-by-side diff. <paramref name="key"/> is what two lines are
        /// compared by — trimmed, case-folded — when they need not match exactly.
        /// </summary>
        public static List<DiffRow> Compare(IReadOnlyList<string> a, IReadOnlyList<string> b, Func<string, string> key = null)
        {
            key ??= s => s;
            var ka = a.Select(key).ToArray();
            var kb = b.Select(key).ToArray();

            int prefix = 0;
            while (prefix < ka.Length && prefix < kb.Length && ka[prefix] == kb[prefix]) prefix++;

            int suffix = 0;
            while (suffix < ka.Length - prefix && suffix < kb.Length - prefix &&
                   ka[ka.Length - 1 - suffix] == kb[kb.Length - 1 - suffix]) suffix++;

            var ops = new List<(DiffKind Kind, int A, int B)>();
            for (int i = 0; i < prefix; i++) ops.Add((DiffKind.Same, i, i));

            ops.AddRange(Myers(ka, prefix, ka.Length - suffix, kb, prefix, kb.Length - suffix));

            for (int i = suffix; i > 0; i--) ops.Add((DiffKind.Same, a.Count - i, b.Count - i));

            return Rows(ops, a, b);
        }

        /// <summary>
        /// The edit script for a[aStart..aEnd) against b[bStart..bEnd): Same, Removed (index
        /// into a) and Added (index into b), in order.
        /// </summary>
        private static List<(DiffKind Kind, int A, int B)> Myers(string[] a, int aStart, int aEnd,
                                                                    string[] b, int bStart, int bEnd)
        {
            int n = aEnd - aStart, m = bEnd - bStart, max = n + m;
            var ops = new List<(DiffKind, int, int)>();
            if (max == 0) return ops;

            int offset = max + 1;
            var v = new int[2 * max + 3];

            // Only the diagonals in play at each step are kept, so the trace is O(D²), not O(D·N).
            var trace = new List<int[]>();
            int dFound = -1;

            for (int d = 0; d <= max && dFound < 0; d++)
            {
                var snapshot = new int[2 * d + 1];
                Array.Copy(v, offset - d, snapshot, 0, snapshot.Length);
                trace.Add(snapshot);

                for (int k = -d; k <= d; k += 2)
                {
                    int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                        ? v[offset + k + 1]
                        : v[offset + k - 1] + 1;
                    int y = x - k;

                    while (x < n && y < m && a[aStart + x] == b[bStart + y]) { x++; y++; }
                    v[offset + k] = x;

                    if (x >= n && y >= m) { dFound = d; break; }
                }
            }

            // Walk back from the end, step by step, collecting the edits in reverse.
            int cx = n, cy = m;
            for (int d = dFound; d >= 0; d--)
            {
                var snap = trace[d];
                int At(int k) => snap[k + d];

                int kk = cx - cy;
                int prevK = kk == -d || (kk != d && At(kk - 1) < At(kk + 1)) ? kk + 1 : kk - 1;
                int prevX = d == 0 ? 0 : At(prevK);
                int prevY = prevX - prevK;
                if (d == 0) { prevX = 0; prevY = 0; }

                while (cx > prevX && cy > prevY)
                {
                    cx--; cy--;
                    ops.Add((DiffKind.Same, aStart + cx, bStart + cy));
                }

                if (d > 0)
                {
                    if (cx == prevX) ops.Add((DiffKind.Added,   -1, bStart + prevY));
                    else             ops.Add((DiffKind.Removed, aStart + prevX, -1));
                }

                cx = prevX;
                cy = prevY;
            }

            ops.Reverse();
            return ops;
        }

        /// <summary>Turns the edit script into rows, pairing each run of removals with the additions after it.</summary>
        private static List<DiffRow> Rows(List<(DiffKind Kind, int A, int B)> ops, IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            var rows = new List<DiffRow>();
            int i = 0;

            while (i < ops.Count)
            {
                if (ops[i].Kind == DiffKind.Same)
                {
                    rows.Add(new DiffRow
                    {
                        Kind        = DiffKind.Same,
                        LeftNumber  = ops[i].A + 1, LeftText  = a[ops[i].A],
                        RightNumber = ops[i].B + 1, RightText = b[ops[i].B]
                    });
                    i++;
                    continue;
                }

                var removed = new List<int>();
                var added   = new List<int>();
                while (i < ops.Count && ops[i].Kind != DiffKind.Same)
                {
                    if (ops[i].Kind == DiffKind.Removed) removed.Add(ops[i].A);
                    else                                 added.Add(ops[i].B);
                    i++;
                }

                for (int r = 0; r < Math.Max(removed.Count, added.Count); r++)
                {
                    bool hasLeft = r < removed.Count, hasRight = r < added.Count;
                    rows.Add(new DiffRow
                    {
                        Kind        = hasLeft && hasRight ? DiffKind.Changed : hasLeft ? DiffKind.Removed : DiffKind.Added,
                        LeftNumber  = hasLeft  ? removed[r] + 1 : null,
                        LeftText    = hasLeft  ? a[removed[r]]  : "",
                        RightNumber = hasRight ? added[r] + 1   : null,
                        RightText   = hasRight ? b[added[r]]    : ""
                    });
                }
            }

            return rows;
        }
    }
}
