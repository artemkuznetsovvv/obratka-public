using System.Text.RegularExpressions;

namespace Obratka.WebApi.Companies.Grouping;

// Чистит шум из имён и адресов карточек разных источников, чтобы автогруппировка
// сравнивала «суть»: дом+литера + ключевые слова улицы + ключевые слова имени.
// Реальные примеры (Скуратов, СПб):
//   2GIS:    «Улица Восстания, 35, Санкт-Петербург5 филиалов»   ← мусор «5 филиалов»
//   Google:  «ул. Восстания, 35»                                 ← без города
//   Yandex:  «ул. Восстания, 35, Санкт-Петербург»                ← с городом в хвосте
// Все три после нормализации должны давать похожий набор токенов.
public static class BranchTextNormalizer
{
    // Префикс/суффикс улиц: убираем целиком, чтобы «улица Восстания» / «ул. Восстания»
    // не сравнивались как разные строки.
    private static readonly string[] StreetMarkers =
    {
        "улица", "ул.", "ул",
        "проспект", "пр-кт", "просп.", "просп", "пр-т", "пр.",
        "набережная", "наб.", "наб",
        "переулок", "пер.", "пер",
        "площадь", "пл.", "пл",
        "бульвар", "б-р", "бул.",
        "шоссе", "ш.",
        "проезд",
        "тупик", "туп.",
        "линия", "лин.",
        "микрорайон", "мкр.", "мкр",
        "корпус", "корп.", "корп", "к.",
        "строение", "стр.", "стр",
        "литера", "лит.", "лит",
        "здание", "зд.",
        "дом", "д.",
    };

    private static readonly string[] NameNoise =
    {
        "кафе", "ресторан", "бар", "бистро", "кофейня",
        "cafe", "coffee", "café", "restaurant", "bar", "bistro",
        "roasters", "roastery",
        "ооо", "ип", "зао", "оао",
    };

    // Артефакты конкретных парсеров. Должны срезаться ДО токенизации.
    private static readonly Regex BranchCountArtifact =
        new(@"\d+\s*филиал[ао]в?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BuildingLetter =
        new(@"\b(\d+)\s*(?:лит\.?\s*)?([А-Яа-я])\b", RegexOptions.Compiled);

    private static readonly Regex Punctuation = new(@"[^\p{L}\p{Nd}\s]+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    // Извлекает «якорь» — дом+литера, главный признак физического адреса.
    // «118 лит С» / «118С» / «118 с» → «118с». «35» → «35». Возвращает null,
    // если ничего похожего на номер дома нет.
    public static string? ExtractHouseAnchor(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var cleaned = BranchCountArtifact.Replace(address, " ").ToLowerInvariant();
        var match = BuildingLetter.Match(cleaned);
        if (match.Success)
            return match.Groups[1].Value + match.Groups[2].Value.ToLowerInvariant();
        // Дом без литеры — ищем самое последнее число длиной 1-4 цифр.
        var fallback = Regex.Match(cleaned, @"\b(\d{1,4})\b(?!\s*\d)");
        return fallback.Success ? fallback.Groups[1].Value : null;
    }

    // Возвращает множество значимых токенов адреса: lowercase, без города, без шума,
    // без префиксов улиц, без артефактов парсера.
    public static HashSet<string> AddressTokens(string? address, string? city)
    {
        if (string.IsNullOrWhiteSpace(address))
            return new HashSet<string>(StringComparer.Ordinal);

        var s = address.ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(city))
        {
            // Иногда город склеен с другим текстом (`Санкт-Петербург5 филиалов`) —
            // регуляркой по подстроке всё равно вырежем.
            var cityLower = city.ToLowerInvariant();
            s = s.Replace(cityLower, " ");
        }

        s = BranchCountArtifact.Replace(s, " ");
        s = Punctuation.Replace(s, " ");
        s = Whitespace.Replace(s, " ").Trim();

        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var filtered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tok in tokens)
        {
            if (StreetMarkers.Contains(tok)) continue;
            if (tok.Length <= 1) continue;
            filtered.Add(tok);
        }

        // Якорь (дом+литера) — даже если бы он отфильтровался, всегда добавляем.
        var anchor = ExtractHouseAnchor(address);
        if (anchor is not null) filtered.Add(anchor);

        return filtered;
    }

    public static HashSet<string> NameTokens(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new HashSet<string>(StringComparer.Ordinal);

        var s = name.ToLowerInvariant();
        s = Punctuation.Replace(s, " ");
        s = Whitespace.Replace(s, " ").Trim();

        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var filtered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tok in tokens)
        {
            if (NameNoise.Contains(tok)) continue;
            if (tok.Length <= 1) continue;
            filtered.Add(tok);
        }
        return filtered;
    }

    // Jaccard на множествах токенов. 0..1. Пустые множества считаем не-похожими.
    public static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = 0;
        foreach (var t in a)
            if (b.Contains(t)) intersection++;
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }
}
