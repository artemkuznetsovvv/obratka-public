using FluentAssertions;
using ParserService.Sources.YandexMaps;

namespace ParserService.IntegrationTests.YandexMaps;

public class Djb2HasherTests
{
    [Fact]
    public void ComputeHash_EmptyString_Returns5381()
    {
        Djb2Hasher.ComputeHash("").Should().Be(5381);
    }

    [Fact]
    public void ComputeHash_KnownInput_ReturnsDeterministicResult()
    {
        var hash1 = Djb2Hasher.ComputeHash("hello");
        var hash2 = Djb2Hasher.ComputeHash("hello");

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ComputeHash_DifferentInputs_ReturnDifferentHashes()
    {
        var hash1 = Djb2Hasher.ComputeHash("hello");
        var hash2 = Djb2Hasher.ComputeHash("world");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void ComputeS_ConcatenatesValuesAndHashes()
    {
        var queryParams = new List<KeyValuePair<string, string>>
        {
            new("ajax", "1"),
            new("businessId", "12345"),
            new("page", "1"),
        };

        var s = Djb2Hasher.ComputeS(queryParams);

        s.Should().NotBeNullOrEmpty();
        // Should be a valid uint32 string
        uint.TryParse(s, out _).Should().BeTrue();
    }

    [Fact]
    public void ComputeS_SameParams_ReturnsSameResult()
    {
        var params1 = new List<KeyValuePair<string, string>>
        {
            new("businessId", "999"),
            new("page", "2"),
        };
        var params2 = new List<KeyValuePair<string, string>>
        {
            new("businessId", "999"),
            new("page", "2"),
        };

        Djb2Hasher.ComputeS(params1).Should().Be(Djb2Hasher.ComputeS(params2));
    }

    [Fact]
    public void ComputeS_DifferentParams_ReturnsDifferentResult()
    {
        var params1 = new List<KeyValuePair<string, string>>
        {
            new("businessId", "999"),
            new("page", "1"),
        };
        var params2 = new List<KeyValuePair<string, string>>
        {
            new("businessId", "999"),
            new("page", "2"),
        };

        Djb2Hasher.ComputeS(params1).Should().NotBe(Djb2Hasher.ComputeS(params2));
    }
}
