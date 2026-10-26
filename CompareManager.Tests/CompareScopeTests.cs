using CompareManager.Classes;
using Xunit;

namespace CompareManager.Tests
{
    public class CompareScopeTests
    {
        [Theory]
        [InlineData(13,    6509, CheckedSide.Left)]    // an issue's scripts against a database
        [InlineData(27339, 130,  CheckedSide.Right)]   // the repository against a go-live folder
        [InlineData(40,    40,   CheckedSide.Both)]
        [InlineData(0,     6509, CheckedSide.Both)]
        public void The_smaller_side_is_the_one_being_checked(int left, int right, CheckedSide expected)
        {
            Assert.Equal(expected, CompareScope.For(left, right));
        }
    }
}
