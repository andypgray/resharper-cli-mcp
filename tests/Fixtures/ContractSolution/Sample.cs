namespace ContractFixture;

/// <summary>
///     Suggestion-tier bait: inspections ReSharper reports below WARNING. The suite does not care which of
///     them survives a given jb release — only that some do, which is what proves
///     <c>--severity=SUGGESTION</c> widens a report rather than narrowing it.
/// </summary>
internal class Sample
{
    private int _total;

    public int Total
    {
        get { return _total; }
        set { _total = value; }
    }

    public string Describe(Sample other)
    {
        int factor = 3;

        return "total is " + other.Total * factor;
    }

    public int SumOf(int[] values)
    {
        int sum = 0;
        for (int index = 0; index < values.Length; index++) sum += values[index];

        return sum;
    }

    public bool IsPositive(int value)
    {
        if (value > 0) return true;

        return false;
    }
}
