namespace CompareManager.Classes
{
    public enum CheckedSide
    {
        Left,
        Right,
        Both
    }

    /// <summary>
    /// Which side is the one being checked, and which is the reference it is checked against.
    ///
    /// The smaller side. A compare is nearly always a handful of scripts against everything —
    /// an issue's Change Control folder against a database, a go-live folder against the
    /// repository — and it can be set up either way round. Ticking the big side's objects
    /// listed thousands that the small side never had, so the small side's are the ones ticked,
    /// whichever side it is on. Two sides of a size, or an empty one, check everything.
    /// </summary>
    public static class CompareScope
    {
        public static CheckedSide For(int leftCount, int rightCount)
        {
            if (leftCount == 0 || rightCount == 0 || leftCount == rightCount) return CheckedSide.Both;
            return leftCount < rightCount ? CheckedSide.Left : CheckedSide.Right;
        }
    }
}
