using Obratka.WebApi.Companies.Grouping;
using Obratka.WebApi.Contracts.Companies;

namespace Obratka.WebApi.IntegrationTests.Companies;

// Реальные кейсы из Skuratov Coffee SPb — три источника, разные форматы адресов/имён.
// Если эти проходят, алгоритм покрывает 90% типичных косяков парсера.
public class BranchGroupingTests
{
    [Fact]
    public void HouseAnchor_Extracts_NumberPlusLetter()
    {
        Assert.Equal("118с", BranchTextNormalizer.ExtractHouseAnchor("наб. Обводного канала, 118 лит С"));
        Assert.Equal("118с", BranchTextNormalizer.ExtractHouseAnchor("набережная Обводного канала, 118С"));
        Assert.Equal("4а", BranchTextNormalizer.ExtractHouseAnchor("Малая Посадская ул., 4А"));
        Assert.Equal("35", BranchTextNormalizer.ExtractHouseAnchor("ул. Восстания, 35"));
        Assert.Equal("83", BranchTextNormalizer.ExtractHouseAnchor("Большой просп. Васильевского острова, 83"));
    }

    [Fact]
    public void HouseAnchor_Null_For_AddressWithoutNumber()
    {
        Assert.Null(BranchTextNormalizer.ExtractHouseAnchor("просто строка"));
        Assert.Null(BranchTextNormalizer.ExtractHouseAnchor(""));
        Assert.Null(BranchTextNormalizer.ExtractHouseAnchor(null));
    }

    [Fact]
    public void AddressTokens_Strip_City_And_BranchCountArtifact()
    {
        // 2GIS грабит "5 филиалов" вместе с адресом — это надо вырезать.
        var tokens = BranchTextNormalizer.AddressTokens(
            "Улица Восстания, 35, Санкт-Петербург5 филиалов", "Санкт-Петербург");
        Assert.Contains("восстания", tokens);
        Assert.Contains("35", tokens);
        Assert.DoesNotContain("филиалов", tokens);
        Assert.DoesNotContain("санкт-петербург", tokens);
        // Префикс «улица» отфильтрован.
        Assert.DoesNotContain("улица", tokens);
    }

    [Fact]
    public void NameTokens_Strip_Common_Brand_Noise()
    {
        var tokens = BranchTextNormalizer.NameTokens("Skuratov Coffee roasters");
        Assert.Contains("skuratov", tokens);
        Assert.DoesNotContain("coffee", tokens);
        Assert.DoesNotContain("roasters", tokens);
    }

    [Fact]
    public void Group_SameStreet_DifferentFormats_AcrossThreeSources()
    {
        var sut = new BranchGroupingService();
        var items = new[]
        {
            // Восстания 35 в трёх форматах.
            Item("2gis",   "Skuratov coffee",         "Улица Восстания, 35, Санкт-Петербург5 филиалов"),
            Item("google", "Skuratov Coffee roasters","ул. Восстания, 35"),
            Item("yandex", "Skuratov Coffee",         "ул. Восстания, 35, Санкт-Петербург"),
        };

        var result = sut.Group(items, "Санкт-Петербург");

        // Все три карточки должны попасть в ОДНУ группу.
        Assert.Single(result.Groups);
        Assert.Empty(result.Unmatched);
        Assert.Equal(3, result.Groups[0].Items.Count);
    }

    [Fact]
    public void Group_Vasilievsky_Island_DespiteShortenedStreet()
    {
        // «Большой проспект В.О.» vs «Большой проспект Васильевского острова» —
        // адрес у одного сильно сокращён, у другого полный. Объединиться должны
        // по якорю «83» + имени.
        var sut = new BranchGroupingService();
        var items = new[]
        {
            Item("2gis",   "Skuratov coffee",          "ДК им. Кирова, Большой проспект В.О., 83, Санкт-Петербург5 филиалов"),
            Item("google", "Skuratov Coffee roasters", "ДК Кирова, Большой проспект Васильевского острова, 83"),
            Item("yandex", "Skuratov Coffee",          "Большой просп. Васильевского острова, 83, Санкт-Петербург"),
        };

        var result = sut.Group(items, "Санкт-Петербург");

        Assert.Single(result.Groups);
        Assert.Equal(3, result.Groups[0].Items.Count);
    }

    [Fact]
    public void Group_Obvodny_Canal_DespiteVokzal1853Prefix()
    {
        // Google добавил префикс «Vokzal1853» (название бизнес-центра), 2GIS — «лит С».
        // По якорю «118с» + общему имени должны склеиться.
        var sut = new BranchGroupingService();
        var items = new[]
        {
            Item("2gis",   "Skuratov coffee",          "Vokzal 1853, набережная Обводного канала, 118 лит С, Санкт-Петербург5 филиалов"),
            Item("google", "Skuratov Coffee roasters", "Vokzal1853, набережная Обводного канала, 118С"),
            Item("yandex", "Skuratov Coffee",          "наб. Обводного канала, 118С, Санкт-Петербург, этаж 1"),
        };

        var result = sut.Group(items, "Санкт-Петербург");

        Assert.Single(result.Groups);
        Assert.Equal(3, result.Groups[0].Items.Count);
    }

    [Fact]
    public void Group_DifferentBranches_StaySeparate()
    {
        // Два разных филиала на разных улицах — должны быть unmatched (singleton-каждый).
        var sut = new BranchGroupingService();
        var items = new[]
        {
            Item("2gis",   "Skuratov coffee", "ул. Восстания, 35"),
            Item("google", "Skuratov coffee", "Малая Посадская ул., 4А"),
        };

        var result = sut.Group(items, "Санкт-Петербург");

        Assert.Empty(result.Groups);
        Assert.Equal(2, result.Unmatched.Count);
    }

    [Fact]
    public void Group_DoesNotMerge_TwoCardsOfSameSource()
    {
        // Одна карточка с 2GIS и вторая тоже с 2GIS — даже если адреса одинаковые,
        // не должны склеиваться (это разные точки в каталоге одного источника).
        var sut = new BranchGroupingService();
        var items = new[]
        {
            Item("2gis", "Skuratov coffee A", "ул. Восстания, 35"),
            Item("2gis", "Skuratov coffee B", "ул. Восстания, 35"),
        };

        var result = sut.Group(items, "Санкт-Петербург");

        Assert.Empty(result.Groups);
        Assert.Equal(2, result.Unmatched.Count);
    }

    [Fact]
    public void Group_PicksLongestName_And_LongestAddress_As_Canonical()
    {
        var sut = new BranchGroupingService();
        var items = new[]
        {
            Item("2gis",   "Skuratov coffee",          "ул. Восстания, 35, Санкт-Петербург5 филиалов"),
            Item("google", "Skuratov Coffee roasters", "ул. Восстания, 35"),
            Item("yandex", "Skuratov Coffee",          "ул. Восстания, 35, Санкт-Петербург"),
        };

        var result = sut.Group(items, "Санкт-Петербург");

        Assert.Single(result.Groups);
        var g = result.Groups[0];
        // Имя — самое длинное из трёх.
        Assert.Equal("Skuratov Coffee roasters", g.CanonicalName);
        // Адрес — самый длинный.
        Assert.Equal("ул. Восстания, 35, Санкт-Петербург5 филиалов", g.CanonicalAddress);
    }

    [Fact]
    public void Group_EmptyInput_ReturnsEmpty()
    {
        var sut = new BranchGroupingService();
        var result = sut.Group(Array.Empty<BranchSearchResultItem>(), "Санкт-Петербург");
        Assert.Empty(result.Groups);
        Assert.Empty(result.Unmatched);
    }

    private static BranchSearchResultItem Item(string source, string name, string address) =>
        new(
            Id: Guid.NewGuid(),
            Source: source,
            ExternalId: Guid.NewGuid().ToString(),
            ExternalUrl: $"https://example.com/{source}",
            Name: name,
            Address: address,
            Rating: 4.5,
            ReviewCount: 100,
            RealReviewsCount: 80);
}
