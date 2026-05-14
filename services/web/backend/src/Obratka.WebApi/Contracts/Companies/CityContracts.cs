namespace Obratka.WebApi.Contracts.Companies;

public sealed record CitySuggestion(int Id, string Name, string Region);

public sealed record CitySuggestResponse(IReadOnlyList<CitySuggestion> Items);
