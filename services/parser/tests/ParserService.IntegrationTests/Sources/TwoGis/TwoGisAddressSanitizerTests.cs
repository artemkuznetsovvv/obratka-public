using FluentAssertions;
using ParserService.Sources.TwoGis;

namespace ParserService.IntegrationTests.Sources.TwoGis;

/// <summary>
/// Unit-тесты на <see cref="TwoGisAddressSanitizer.Clean"/>.
///
/// Жил-был баг: на поиске Skuratov Coffee в Казани адреса карточек приходили
/// как «Улица Волкова, 61, Казань​11 филиалов» — слипшийся бейдж «N филиалов»
/// прихватывался innerText'ом блока с адресом. JS-extractor теперь срезает
/// этот бейдж по DOM-селектору; санитайзер — второй рубеж: гарантирует, что
/// если DOM-чистка не сработала (изменения вёрстки 2GIS), C#-уровень всё
/// равно отдаст пользователю чистый адрес.
/// </summary>
public class TwoGisAddressSanitizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Clean_NullOrWhitespace_ReturnsEmpty(string? input, string expected)
    {
        TwoGisAddressSanitizer.Clean(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Улица Пушкина, 5/43, Москва", "Улица Пушкина, 5/43, Москва")]
    [InlineData("Невский проспект, 28", "Невский проспект, 28")]
    public void Clean_AddressWithoutSuffix_ReturnsAsIs(string input, string expected)
    {
        TwoGisAddressSanitizer.Clean(input).Should().Be(expected);
    }

    /// <summary>
    /// Реальные адреса с 2gis.ru/kazan/search/Skuratov%20Coffee (verified 2026-05).
    /// raw = textContent блока адреса, включает U+200B префикс/инфикс и U+00A0 (NBSP).
    /// </summary>
    [Theory]
    [InlineData(
        "​Улица Волкова, 61, Казань​11 филиалов",
        "Улица Волкова, 61, Казань")]
    [InlineData(
        "​Петербургская улица, 37, Казань​11 филиалов",
        "Петербургская улица, 37, Казань")]
    [InlineData(
        "​Улица Щапова, 14/31, Казань​11 филиалов",
        "Улица Щапова, 14/31, Казань")]
    public void Clean_RealWorld2GisCases_StripsZeroWidthAndBranchesSuffix(string raw, string expected)
    {
        TwoGisAddressSanitizer.Clean(raw).Should().Be(expected);
    }

    /// <summary>
    /// Грамматика: «филиал» (1), «филиала» (2-4), «филиалов» (5+) — все три формы должны срезаться.
    /// </summary>
    [Theory]
    [InlineData("Улица Ленина, 1, Москва 1 филиал", "Улица Ленина, 1, Москва")]
    [InlineData("Улица Ленина, 1, Москва 2 филиала", "Улица Ленина, 1, Москва")]
    [InlineData("Улица Ленина, 1, Москва 11 филиалов", "Улица Ленина, 1, Москва")]
    [InlineData("Улица Ленина, 1, Москва 100 филиалов", "Улица Ленина, 1, Москва")]
    public void Clean_BranchesSuffix_AllRussianForms_AreStripped(string input, string expected)
    {
        TwoGisAddressSanitizer.Clean(input).Should().Be(expected);
    }

    /// <summary>
    /// Кейс-инсенситив — на случай если 2GIS отрендерит uppercase / mixed-case.
    /// </summary>
    [Theory]
    [InlineData("Улица Ленина, 1, Москва 11 ФИЛИАЛОВ", "Улица Ленина, 1, Москва")]
    [InlineData("Улица Ленина, 1, Москва 11 Филиалов", "Улица Ленина, 1, Москва")]
    public void Clean_BranchesSuffix_IsCaseInsensitive(string input, string expected)
    {
        TwoGisAddressSanitizer.Clean(input).Should().Be(expected);
    }

    /// <summary>
    /// Якорь $ — срезаем только если «N филиалов» в КОНЦЕ. Слово «филиал» в середине
    /// адреса (например в названии улицы) трогать нельзя.
    /// </summary>
    [Theory]
    [InlineData("Филиальная улица, 5, Москва", "Филиальная улица, 5, Москва")]
    [InlineData("улица 11 Филиалов имени Иванова, 5", "улица 11 Филиалов имени Иванова, 5")]
    public void Clean_BranchesWordInMiddle_NotStripped(string input, string expected)
    {
        TwoGisAddressSanitizer.Clean(input).Should().Be(expected);
    }

    /// <summary>
    /// Если хвост уже отрезан DOM-чисткой — санитайзер не должен ничего ломать
    /// и не должен трогать конечные пробелы/zero-width у нормального адреса.
    /// </summary>
    [Theory]
    [InlineData("​Улица Волкова, 61, Казань", "Улица Волкова, 61, Казань")]
    [InlineData("​Улица Волкова, 61, Казань​", "Улица Волкова, 61, Казань")]
    [InlineData("﻿Улица Волкова, 61, Казань", "Улица Волкова, 61, Казань")]
    public void Clean_AddressWithZeroWidthOnly_StripsThem(string input, string expected)
    {
        TwoGisAddressSanitizer.Clean(input).Should().Be(expected);
    }

    /// <summary>
    /// Идемпотентность — повторный вызов на уже чистом адресе не меняет результат.
    /// Важно потому что Clean применяется и к адресам, прошедшим DOM-чистку
    /// (там уже чисто), и к адресам без неё.
    /// </summary>
    [Theory]
    [InlineData("​Улица Волкова, 61, Казань​11 филиалов")]
    [InlineData("Улица Ленина, 1, Москва 11 филиалов")]
    [InlineData("Улица Пушкина, 5/43, Москва")]
    public void Clean_IsIdempotent(string input)
    {
        var once = TwoGisAddressSanitizer.Clean(input);
        var twice = TwoGisAddressSanitizer.Clean(once);
        twice.Should().Be(once);
    }
}
