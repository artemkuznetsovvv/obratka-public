using FluentAssertions;
using ProcessingGateway.Application.Ingestion;

namespace ProcessingGateway.Tests;

public class CompositeKeyBuilderTests
{
    private static readonly Guid Branch = Guid.Parse("e309d01c-c44b-4412-9455-3714ca056549");
    private static readonly DateTimeOffset Date = DateTimeOffset.Parse("2026-03-30T10:11:55.113+00:00");

    [Fact]
    public void With_external_id_uses_ext_format()
    {
        var key = CompositeKeyBuilder.Build("yandex", Branch, "JoS68IiWmeE1u-qEEzk_52w16kfBxP", Date, "any text");

        key.Should().Be("yandex:e309d01cc44b441294553714ca056549:ext:JoS68IiWmeE1u-qEEzk_52w16kfBxP");
    }

    [Fact]
    public void Without_external_id_uses_dt_fallback_with_unix_seconds()
    {
        var key = CompositeKeyBuilder.Build("google", Branch, externalId: null, Date, "Хорошо");

        key.Should().StartWith("google:e309d01cc44b441294553714ca056549:dt:");
        key.Should().Contain(Date.ToUnixTimeSeconds().ToString());
        key.Should().EndWith(":Хорошо");
    }

    [Fact]
    public void Without_external_id_truncates_long_text_to_200_chars()
    {
        var longText = new string('a', 500);
        var key = CompositeKeyBuilder.Build("2gis", Branch, externalId: null, Date, longText);

        key.Length.Should().BeLessThanOrEqualTo(CompositeKeyBuilder.MaxLength);
        var prefix = key.Substring(key.LastIndexOf(':') + 1);
        prefix.Should().HaveLength(200);
        prefix.Should().Be(new string('a', 200));
    }

    [Fact]
    public void Trims_leading_and_trailing_whitespace_in_fallback()
    {
        var key = CompositeKeyBuilder.Build("yandex", Branch, externalId: null, Date, "  Хорошо  ");
        key.Should().EndWith(":Хорошо");
    }

    [Fact]
    public void Empty_external_id_is_treated_as_null()
    {
        var key = CompositeKeyBuilder.Build("yandex", Branch, externalId: "", Date, "text");
        key.Should().Contain(":dt:").And.NotContain(":ext:");
    }

    [Fact]
    public void Empty_source_throws()
    {
        var act = () => CompositeKeyBuilder.Build("", Branch, "ext-1", Date, "x");
        act.Should().Throw<ArgumentException>();
    }
}
