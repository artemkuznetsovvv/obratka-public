namespace ParserService.Sources.YandexMaps;

internal static class Djb2Hasher
{
    public static uint ComputeHash(string input)
    {
        uint n = 5381;
        foreach (char c in input)
            n = (33 * n) ^ c;
        return n;
    }

    public static string ComputeS(IReadOnlyList<KeyValuePair<string, string>> queryParams)
    {
        var concatenated = string.Concat(queryParams.Select(kv => kv.Value));
        return ComputeHash(concatenated).ToString();
    }
}
