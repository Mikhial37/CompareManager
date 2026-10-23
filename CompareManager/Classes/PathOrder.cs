using System;
using System.Collections.Generic;

namespace CompareManager.Classes
{
    /// <summary>
    /// Orders script paths the way Explorer lists them, and so the way a folder of Change
    /// Control scripts is run: folder by folder, file by file, with runs of digits compared as
    /// numbers — <c>2 - Tables</c> before <c>10 - Data</c>, <c>-0604</c> before <c>-2001</c>.
    /// Letters compare without regard to case.
    /// </summary>
    public sealed class PathOrder : IComparer<string>
    {
        public static readonly PathOrder Instance = new PathOrder();

        public int Compare(string x, string y)
        {
            x ??= ""; y ??= "";
            int i = 0, j = 0;

            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;

                    string a = x.Substring(si, i - si).TrimStart('0');
                    string b = y.Substring(sj, j - sj).TrimStart('0');
                    if (a.Length != b.Length) return a.Length.CompareTo(b.Length);

                    int digits = string.CompareOrdinal(a, b);
                    if (digits != 0) return digits;
                    continue;
                }

                // A folder ends before any character of a longer name: "a\z" runs before "a b\c".
                char cx = x[i] == '\\' || x[i] == '/' ? '\0' : char.ToUpperInvariant(x[i]);
                char cy = y[j] == '\\' || y[j] == '/' ? '\0' : char.ToUpperInvariant(y[j]);
                if (cx != cy) return cx.CompareTo(cy);
                i++; j++;
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
