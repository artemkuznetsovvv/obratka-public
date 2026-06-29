namespace Obratka.WebApi.Companies;

public static class BranchSources
{
    public const string TwoGis = "2gis";
    public const string Yandex = "yandex";
    public const string Google = "google";

    public static readonly IReadOnlyList<string> All = new[] { TwoGis, Yandex, Google };

    public static bool IsKnown(string source) =>
        source is TwoGis or Yandex or Google;

    // Человекочитаемое название источника для UI/уведомлений (slug → лейбл).
    public static string Label(string source) => source switch
    {
        TwoGis => "2ГИС",
        Yandex => "Яндекс.Карты",
        Google => "Google Maps",
        _ => source,
    };
}
