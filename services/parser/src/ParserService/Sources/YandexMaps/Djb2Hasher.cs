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

    public static string ComputeS(IDictionary<string, string> queryParams)
    {
        var concatenated = string.Concat(queryParams.Values);
        return ComputeHash(concatenated).ToString();
    }
}
