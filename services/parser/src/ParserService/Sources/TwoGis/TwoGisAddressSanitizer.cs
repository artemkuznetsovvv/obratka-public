using System.Text.RegularExpressions;

namespace ParserService.Sources.TwoGis;

/// <summary>
/// Чистит адрес карточки 2GIS от мусорных хвостов.
///
/// На листинге поиска ячейка адреса может склеиваться с бейджом «N филиалов»
/// (отдельный &lt;a href="/.../branches/..."&gt; внутри того же контейнера).
/// JS-extractor уже пытается вырезать этот бейдж по селектору, но если 2GIS
/// поменяет href / класс / DOM — мы получим адрес с хвостом. Этот санитайзер
/// — второй рубеж: чистит то, что не отрезал DOM, плюс zero-width символы.
///
/// Юнит-тестами покрыт `<see cref="TwoGisAddressSanitizerTests"/>`.
/// </summary>
internal static class TwoGisAddressSanitizer
{
    // \w в JS regex без /u флага = [A-Za-z0-9_], не матчит кириллицу — поэтому окончания
    // "ов"/"а" после "филиал" указываем явно через [а-яё]. В C# regex Unicode по умолчанию,
    // и \w матчит кириллицу — но \w также захватил бы соседний "м"/"н" перед последним
    // словом адреса, что мусорнее. Явный [а-яё] — однозначно.
    private static readonly Regex BranchesSuffixRegex = new(
        @"\s*\d+\s+филиал[а-яё]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // U+200B..U+200D zero-width spaces/joiners, U+FEFF BOM/zero-width nbsp.
    // 2GIS вставляет U+200B как «склейку» между адресом и бейджем «N филиалов»,
    // а также в начале текста. Обычный .Trim() их не убирает.
    private static readonly Regex ZeroWidthRegex = new(
        "[​‌‍﻿]",
        RegexOptions.Compiled);

    /// <summary>
    /// Возвращает чистый адрес без zero-width символов и без хвоста «N филиалов».
    /// Null/whitespace → пустая строка.
    /// </summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var t = ZeroWidthRegex.Replace(raw, string.Empty).Trim();
        t = BranchesSuffixRegex.Replace(t, string.Empty).Trim();
        return t;
    }
}
