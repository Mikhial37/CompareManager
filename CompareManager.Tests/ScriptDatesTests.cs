using CompareManager.Classes;
using System;
using Xunit;

namespace CompareManager.Tests
{
    public class ScriptDatesTests
    {
        private static readonly DateTime Today = new DateTime(2026, 10, 23);

        private static DateTime? Latest(string sql) => ScriptDates.Latest(sql, Today);

        [Fact]
        public void Reads_the_Change_Control_header()
        {
            const string sql =
                "/*\nCHANGE CONTROL\nDate       : 06-Oct-2026\nUser       : AB\n*/\n;\nGO\n" +
                "-- 06-Oct-2026 | AB | SYM0000001 | Reworded\nALTER FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END";

            Assert.Equal(new DateTime(2026, 10, 6), Latest(sql));
        }

        [Fact]
        public void The_latest_line_of_the_history_wins()
        {
            const string sql =
                "/* Version | Date     | By  | Description\n" +
                "* 1.11.01 | 03/02/24 | AB  | Create\n" +
                "* 1.11.02 | 15/08/2025 | CD | Fix\n" +
                "* 1.11.03 | 2026-01-09 | EF | Tidy */\n" +
                "CREATE PROCEDURE dbo.p AS SELECT 1";

            Assert.Equal(new DateTime(2026, 1, 9), Latest(sql));
        }

        [Theory]
        [InlineData("-- 6 October 2026",   2026, 10, 6)]
        [InlineData("-- October 6, 2026",  2026, 10, 6)]
        [InlineData("-- 06/10/2026",       2026, 10, 6)]
        [InlineData("-- 10/13/2026 in US order", 2026, 10, 13)]
        [InlineData("-- 06.10.2026",       2026, 10, 6)]
        public void Reads_the_usual_ways_of_writing_a_date(string comment, int y, int m, int d)
        {
            Assert.Equal(new DateTime(y, m, d), Latest(comment + "\nSELECT 1"));
        }

        [Fact]
        public void Dates_in_code_placeholders_and_version_numbers_do_not_count()
        {
            const string sql =
                "/* 1.11.01 | DD/MM/YY | ??? | Create */\n" +
                "SELECT * FROM t WHERE d > '2026-09-01'\n" +
                "-- next review 01/01/2030\n";

            Assert.Null(Latest(sql));
        }

        [Fact]
        public void A_different_day_differs_but_a_missing_date_does_not()
        {
            Assert.True(ObjectComparer.DaysDiffer(new DateTime(2026, 10, 6), new DateTime(2026, 10, 7)));
            Assert.False(ObjectComparer.DaysDiffer(new DateTime(2026, 10, 6, 8, 0, 0), new DateTime(2026, 10, 6, 17, 0, 0)));
            Assert.False(ObjectComparer.DaysDiffer(new DateTime(2026, 10, 6), null));
        }
    }
}
