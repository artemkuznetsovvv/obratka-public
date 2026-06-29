using FluentAssertions;
using ProcessingGateway.Infrastructure.Storage;

namespace ProcessingGateway.Tests;

public class S3UrlParserTests
{
    [Fact]
    public void Parses_typical_url()
    {
        var (bucket, key) = S3UrlParser.Parse("s3://obratka-jobs/abc-123/raw/yandex.json");
        bucket.Should().Be("obratka-jobs");
        key.Should().Be("abc-123/raw/yandex.json");
    }

    [Fact]
    public void Parses_url_with_nested_keys()
    {
        var (bucket, key) = S3UrlParser.Parse("s3://obratka-jobs/job/uuid/raw/some/deep/path.json");
        bucket.Should().Be("obratka-jobs");
        key.Should().Be("job/uuid/raw/some/deep/path.json");
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://obratka-jobs/foo")]
    [InlineData("s3://onlybucket")]
    [InlineData("s3://bucket/")]
    [InlineData("s3:///key")]
    public void Throws_on_malformed_input(string url)
    {
        var act = () => S3UrlParser.Parse(url);
        act.Should().Throw<ArgumentException>();
    }
}
