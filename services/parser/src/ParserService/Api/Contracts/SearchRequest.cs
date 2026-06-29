namespace ParserService.Api.Contracts;

public record SearchRequest(string Query, string? City, string[] Sources);
