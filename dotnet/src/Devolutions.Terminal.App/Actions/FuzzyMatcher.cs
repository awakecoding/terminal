namespace Devolutions.Terminal.App.Actions;

public static class FuzzyMatcher
{
    public static int Score(string query, string candidate)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        if (string.IsNullOrEmpty(candidate))
        {
            return int.MinValue;
        }

        query = query.Trim();
        var queryIndex = 0;
        var score = 0;
        var consecutive = 0;
        for (var candidateIndex = 0;
             candidateIndex < candidate.Length && queryIndex < query.Length;
             candidateIndex++)
        {
            if (char.ToUpperInvariant(candidate[candidateIndex]) !=
                char.ToUpperInvariant(query[queryIndex]))
            {
                consecutive = 0;
                score--;
                continue;
            }

            score += 10;
            consecutive++;
            score += consecutive * 4;
            if (candidateIndex == 0 || IsWordBoundary(candidate[candidateIndex - 1], candidate[candidateIndex]))
            {
                score += 12;
            }

            queryIndex++;
        }

        if (queryIndex != query.Length)
        {
            return int.MinValue;
        }

        score -= candidate.Length - query.Length;
        return score;
    }

    public static IReadOnlyList<T> Rank<T>(
        IEnumerable<T> items,
        string query,
        Func<T, string> textSelector)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(textSelector);
        if (string.IsNullOrWhiteSpace(query))
        {
            return items.ToArray();
        }

        return items
            .Select((item, index) => new
            {
                Item = item,
                Index = index,
                Score = Score(query, textSelector(item)),
            })
            .Where(static item => item.Score != int.MinValue)
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Item)
            .ToArray();
    }

    private static bool IsWordBoundary(char previous, char current) =>
        char.IsWhiteSpace(previous) ||
        previous is '-' or '_' or '/' or '\\' or ':' or '.' ||
        (char.IsLower(previous) && char.IsUpper(current));
}
