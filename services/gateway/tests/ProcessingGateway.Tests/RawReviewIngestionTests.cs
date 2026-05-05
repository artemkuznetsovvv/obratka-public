using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcessingGateway.Application.Ingestion;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Tests.Infrastructure;

namespace ProcessingGateway.Tests;

[Collection("Postgres")]
public class RawReviewIngestionTests
{
    private readonly PgFixture _pg;

    public RawReviewIngestionTests(PgFixture pg) => _pg = pg;

    private NpgsqlConnectionFactory Factory() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ProcessingDb"] = _pg.ConnectionString
            })
            .Build());

    [Theory]
    [InlineData("yandex", 4)]
    [InlineData("2gis",   5)]
    [InlineData("google", 2)]
    public void Fixture_deserializes_with_expected_review_count(string source, int expected)
    {
        var payload = FixtureLoader.LoadRaw(source);

        payload.Source.Should().Be(source);
        payload.Reviews.Should().HaveCount(expected);
    }

    [Fact]
    public void Yandex_fixture_emoji_text_preserved_through_deserialization()
    {
        var payload = FixtureLoader.LoadRaw("yandex");

        payload.Reviews.Should().Contain(r => r.Text.Contains("👌") && r.Text.Contains("👍"));
    }

    [Fact]
    public void Mapper_fills_all_fields_from_real_yandex_fixture()
    {
        var payload = FixtureLoader.LoadRaw("yandex");
        var dto = payload.Reviews[0];

        var entity = RawReviewMapper.ToEntity(payload, dto);

        entity.CompanyId.Should().Be(payload.CompanyId);
        entity.BranchId.Should().Be(dto.BranchId);
        entity.Source.Should().Be("yandex");
        entity.ExternalId.Should().Be(dto.ExternalId);
        entity.RawText.Should().Be(dto.Text);
        entity.NormalizedText.Should().BeNull("это зона LLM, на ингесте не заполняется");
        entity.TextLanguage.Should().Be("ru");
        entity.ReviewDate.Should().Be(dto.Date);
        entity.Stars.Should().Be(3);
        entity.AuthorName.Should().Be("Дмитрий .Т");
        entity.AuthorPublicId.Should().Be("hh5tc3xazcpqtg1b1w2d72qjwc");
        entity.CollectedAt.Should().Be(payload.CollectedAt);
        entity.CompositeKey.Should()
            .Be($"yandex:{dto.BranchId:N}:ext:{dto.ExternalId}");
    }

    [Fact]
    public void Mapper_handles_2gis_fixture_without_text_language()
    {
        // Реальные 2gis-данные приходят без поля text_language вообще.
        var payload = FixtureLoader.LoadRaw("2gis");
        var dto = payload.Reviews[0];

        var entity = RawReviewMapper.ToEntity(payload, dto);

        entity.TextLanguage.Should().BeNull();
        entity.Source.Should().Be("2gis");
        entity.ExternalId.Should().Be("235151411");
    }

    [Fact]
    public async Task Bulk_insert_persists_full_yandex_fixture_through_factory()
    {
        var payload = FixtureLoader.LoadRaw("yandex");
        var entities = payload.Reviews.Select(r => RawReviewMapper.ToEntity(payload, r)).ToList();

        await ResetReviews();
        var inserter = new RawReviewBulkInserter(Factory());
        var inserted = await inserter.InsertAsync(entities);

        inserted.Should().Be(payload.Reviews.Count);

        await using var ctx = _pg.NewDbContext();
        var stored = await ctx.Reviews
            .Where(r => r.Source == "yandex")
            .OrderBy(r => r.ReviewDate)
            .ToListAsync();

        stored.Should().HaveCount(payload.Reviews.Count);
        stored.Should().AllSatisfy(r =>
        {
            r.Id.Should().BeGreaterThan(0, "bigint identity должен сам проставиться");
            r.CompanyId.Should().Be(payload.CompanyId);
            r.CollectedAt.Should().Be(payload.CollectedAt);
        });
        stored.Select(r => r.RawText).Should()
            .Contain("Все отлично 👌 менеджеры молодцы, обслуживание хорошее 👍",
                "эмодзи и UTF-8 не должны теряться при INSERT через UNNEST");
    }

    [Fact]
    public async Task Bulk_insert_is_idempotent_on_duplicate_composite_keys()
    {
        var payload = FixtureLoader.LoadRaw("yandex");
        var entities = payload.Reviews.Select(r => RawReviewMapper.ToEntity(payload, r)).ToList();

        await ResetReviews();
        var inserter = new RawReviewBulkInserter(Factory());

        var first = await inserter.InsertAsync(entities);
        var second = await inserter.InsertAsync(entities);

        first.Should().Be(payload.Reviews.Count, "первый прогон вставляет всё");
        second.Should().Be(0, "ON CONFLICT (composite_key) DO NOTHING — повтор должен дать 0");

        await using var ctx = _pg.NewDbContext();
        (await ctx.Reviews.CountAsync(r => r.Source == "yandex"))
            .Should().Be(payload.Reviews.Count, "БД содержит ровно одну копию каждого отзыва");
    }

    [Fact]
    public async Task Bulk_insert_handles_mixed_sources_in_one_batch()
    {
        var yandex = FixtureLoader.LoadRaw("yandex");
        var twogis = FixtureLoader.LoadRaw("2gis");

        var batch = yandex.Reviews.Select(r => RawReviewMapper.ToEntity(yandex, r))
            .Concat(twogis.Reviews.Select(r => RawReviewMapper.ToEntity(twogis, r)))
            .ToList();

        await ResetReviews();
        var inserted = await new RawReviewBulkInserter(Factory()).InsertAsync(batch);
        inserted.Should().Be(batch.Count);

        await using var ctx = _pg.NewDbContext();
        (await ctx.Reviews.CountAsync(r => r.Source == "yandex")).Should().Be(yandex.Reviews.Count);
        (await ctx.Reviews.CountAsync(r => r.Source == "2gis")).Should().Be(twogis.Reviews.Count);
    }

    [Fact]
    public async Task Bulk_insert_1000_rows_completes_under_500ms()
    {
        // SLO Этапа 3: 1000 строк должны вставляться быстро. Измеряем для одного источника
        // с уникальными external_id, чтобы UNIQUE-индекс работал на полный скан.
        var companyId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var collectedAt = DateTimeOffset.UtcNow;

        var entities = Enumerable.Range(1, 1000).Select(i => new Review
        {
            CompanyId = companyId,
            BranchId = branchId,
            Source = "yandex",
            ExternalId = $"perf-{i:D6}",
            CompositeKey = $"yandex:{branchId:N}:ext:perf-{i:D6}",
            RawText = $"Тестовый отзыв номер {i}",
            ReviewDate = collectedAt.AddMinutes(-i),
            Stars = (short)((i % 5) + 1),
            CollectedAt = collectedAt
        }).ToList();

        await ResetReviews();
        var inserter = new RawReviewBulkInserter(Factory());

        // Прогрев — первый вызов через Dapper компилирует кэш и плодит accessors.
        await inserter.InsertAsync(entities.Take(1).ToArray());

        var sw = Stopwatch.StartNew();
        var inserted = await inserter.InsertAsync(entities);
        sw.Stop();

        inserted.Should().Be(999, "1 уже была вставлена прогревом");
        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            $"UNNEST batch must be fast; actual {sw.ElapsedMilliseconds}ms");
    }

    private async Task ResetReviews()
    {
        await using var ctx = _pg.NewDbContext();
        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM review_llm_results; DELETE FROM reviews;");
    }
}
