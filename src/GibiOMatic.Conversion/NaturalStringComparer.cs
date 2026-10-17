namespace GibiOMatic.Conversion;

/// <summary>Orders "page2" before "page10" (numbers compared by value), ignoring case.</summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                var numberX = x.AsSpan(startX, i - startX).TrimStart('0');
                var numberY = y.AsSpan(startY, j - startY).TrimStart('0');
                var byLength = numberX.Length.CompareTo(numberY.Length);
                if (byLength != 0)
                {
                    return byLength;
                }

                var byDigits = numberX.SequenceCompareTo(numberY);
                if (byDigits != 0)
                {
                    return byDigits;
                }

                continue;
            }

            var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (byChar != 0)
            {
                return byChar;
            }

            i++;
            j++;
        }

        var byRest = (x.Length - i).CompareTo(y.Length - j);
        return byRest != 0 ? byRest : string.CompareOrdinal(x, y);
    }
}
