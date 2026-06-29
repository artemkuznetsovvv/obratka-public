namespace Obratka.WebApi.Geo;

// Reference list of Russian cities, seeded via migration.
// Powers the autocomplete on the "New analysis" form.
public class CityReference
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NameNormalized { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
}
