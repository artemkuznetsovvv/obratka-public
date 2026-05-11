using System.Collections.Concurrent;
using Microsoft.Playwright;
using ParserService.Core.Models;

namespace ParserService.Sources.GoogleMaps;

/// <summary>
/// Collects reviews by scrolling Google Maps reviews tab in browser
/// and intercepting listugcposts API responses.
/// Best of both worlds: looks like real user + full API data quality
/// (exact timestamps, original text, authorId, language).
/// </summary>
internal sealed class GoogleMapsHybridCollector
{
    private readonly GoogleMapsOptions _options;
    private readonly ILogger _logger;

    public GoogleMapsHybridCollector(GoogleMapsOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RawReview>> CollectAllReviewsAsync(
        IBrowserContext browserContext,
        string placeUrl,
        BranchTarget branch,
        DateRange period,
        CancellationToken ct)
    {
        var reviews = new List<RawReview>();
        var seenIds = new HashSet<string>();
        var interceptedBatch = new ConcurrentQueue<IReadOnlyList<GoogleMapsReviewDto>>();

        var page = await browserContext.NewPageAsync();
        try
        {
            _logger.LogInformation("[GMaps-Hybrid] Начинаю сбор для {ExternalId} (URL: {Url})",
                branch.ExternalId, placeUrl);

            // --- Set up API response interception ---
            page.Response += (_, response) =>
            {
                if (!response.Url.Contains("/maps/rpc/listugcposts")) return;
                if (response.Status != 200) return;

                _ = CaptureResponseAsync(response, interceptedBatch);
            };
            _logger.LogDebug("[GMaps-Hybrid] Перехват listugcposts настроен");

            // --- Navigate to place page ---
            await page.GotoAsync(placeUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _options.NavigationTimeoutMs
            });
            await GoogleMapsConsentHelper.DismissConsentIfNeededAsync(page, _logger, ct);
            await Task.Delay(Random.Shared.Next(2000, 4000), ct);

            // --- Click reviews tab ---
            await ClickReviewsTabAsync(page, ct);

            // --- Select sort by newest ---
            await SelectSortByNewestAsync(page, ct);

            // --- Process initial intercepted responses ---
            await Task.Delay(Random.Shared.Next(1000, 2000), ct);
            var initial = DrainQueue(interceptedBatch, reviews, seenIds, branch, period);
            var reachedDateBound = initial.ReachedDateBound;

            _logger.LogInformation(
                "[GMaps-Hybrid] Первая порция: батчей={Batches}, всего видели={Seen}, добавлено={Added}, дубли={Dup}, дата-граница: {DateBound}",
                initial.BatchesProcessed, initial.TotalSeen, initial.Added, initial.Duplicates, reachedDateBound);

            // --- Scroll loop ---
            int consecutiveEmpty = 0;
            const int maxConsecutiveEmpty = 5;
            int totalDuplicates = initial.Duplicates;
            int totalBatches = initial.BatchesProcessed;
            int lastScrollHeight = 0;
            int scrollHeightStuckStreak = 0;

            for (int attempt = 0; attempt < _options.MaxScrollAttempts && !reachedDateBound; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var beforeCount = reviews.Count;

                // Scroll down to trigger next page load
                var scroll = await ScrollReviewsPanelAsync(page);

                if (!scroll.Found)
                    _logger.LogWarning("[GMaps-Hybrid] Скролл #{N}: scroll-контейнер не найден!", attempt + 1);

                // Wait for intercepted response
                var waitDeadline = DateTime.UtcNow.AddMilliseconds(_options.DelayBetweenPagesMaxMs * 2);
                while (interceptedBatch.IsEmpty && DateTime.UtcNow < waitDeadline)
                    await Task.Delay(300, ct);

                // Human-like pause
                await Task.Delay(Random.Shared.Next(
                    _options.DelayBetweenPagesMinMs,
                    _options.DelayBetweenPagesMaxMs + 1), ct);

                var stats = DrainQueue(interceptedBatch, reviews, seenIds, branch, period);
                reachedDateBound = stats.ReachedDateBound;
                totalDuplicates += stats.Duplicates;
                totalBatches += stats.BatchesProcessed;

                var newReviews = reviews.Count - beforeCount;
                if (newReviews == 0)
                {
                    consecutiveEmpty++;
                    _logger.LogDebug(
                        "[GMaps-Hybrid] Скролл #{N}: нет новых (пустых: {Streak}/{Max}). batches={B}, видели={S}, дубли={D}, scroll={SH}/{ST}/{CH}, в DOM={Dom}",
                        attempt + 1, consecutiveEmpty, maxConsecutiveEmpty,
                        stats.BatchesProcessed, stats.TotalSeen, stats.Duplicates,
                        scroll.ScrollHeight, scroll.ScrollTop, scroll.ClientHeight, scroll.ReviewCountInDom);

                    if (consecutiveEmpty >= maxConsecutiveEmpty)
                    {
                        _logger.LogInformation(
                            "[GMaps-Hybrid] {Max} пустых скроллов подряд — останавливаюсь. Всего: батчей={Batches}, дублей={Dup}, отзывов={Total}, scrollHeight={SH}, в DOM={Dom}",
                            maxConsecutiveEmpty, totalBatches, totalDuplicates, reviews.Count,
                            scroll.ScrollHeight, scroll.ReviewCountInDom);
                        break;
                    }
                }
                else
                {
                    consecutiveEmpty = 0;
                    _logger.LogDebug(
                        "[GMaps-Hybrid] Скролл #{N}: +{New}, всего {Total} (batches={B}, дубли={D}, scroll={SH}/{ST}, в DOM={Dom})",
                        attempt + 1, newReviews, reviews.Count,
                        stats.BatchesProcessed, stats.Duplicates,
                        scroll.ScrollHeight, scroll.ScrollTop, scroll.ReviewCountInDom);
                }

                // Дополнительный сигнал «всё, больше не подгружается»: scrollHeight не меняется
                // 5 раз подряд — Google перестал отдавать новые страницы (даже если в очереди
                // что-то приходит). Полезно когда дубли маскируют пустые скроллы как «новые=0».
                if (scroll.Found && scroll.ScrollHeight == lastScrollHeight)
                    scrollHeightStuckStreak++;
                else
                    scrollHeightStuckStreak = 0;
                lastScrollHeight = scroll.ScrollHeight;

                if (scrollHeightStuckStreak >= 5 && newReviews == 0)
                {
                    _logger.LogInformation(
                        "[GMaps-Hybrid] scrollHeight={SH} не растёт 5 скроллов подряд — Google больше не подгружает. Стоп.",
                        scroll.ScrollHeight);
                    break;
                }
            }

            _logger.LogInformation(
                "[GMaps-Hybrid] Сбор завершён: {ExternalId}, {Count} отзывов (уникальных: {Unique}), батчей: {Batches}, дублей отброшено: {Dup}",
                branch.ExternalId, reviews.Count, seenIds.Count, totalBatches, totalDuplicates);

            return reviews;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task CaptureResponseAsync(
        IResponse response,
        ConcurrentQueue<IReadOnlyList<GoogleMapsReviewDto>> queue)
    {
        try
        {
            var body = await response.TextAsync();
            _logger.LogDebug("[GMaps-Hybrid] Перехвачен listugcposts ответ ({Length} байт)", body.Length);

            var root = GoogleMapsResponseParser.Parse(body);
            var reviews = GoogleMapsResponseParser.GetReviews(root);

            if (reviews.Count > 0)
            {
                queue.Enqueue(reviews);
                _logger.LogDebug("[GMaps-Hybrid] Распарсено: {Count} отзывов", reviews.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GMaps-Hybrid] Ошибка парсинга listugcposts ответа");
        }
    }

    private DrainStats DrainQueue(
        ConcurrentQueue<IReadOnlyList<GoogleMapsReviewDto>> queue,
        List<RawReview> reviews,
        HashSet<string> seenIds,
        BranchTarget branch,
        DateRange period)
    {
        bool reachedDateBound = false;
        int batchesProcessed = 0, totalSeen = 0, dropDup = 0, dropEmpty = 0, dropOutOfRange = 0, added = 0;

        while (queue.TryDequeue(out var batch))
        {
            batchesProcessed++;
            foreach (var dto in batch)
            {
                totalSeen++;

                if (string.IsNullOrEmpty(dto.ReviewId) || dto.Rating == null || dto.Date == null)
                {
                    dropEmpty++;
                    continue;
                }

                if (!seenIds.Add(dto.ReviewId))
                {
                    dropDup++;
                    continue;
                }

                if (dto.Date < period.From)
                {
                    reachedDateBound = true;
                    dropOutOfRange++;
                    continue;
                }

                if (dto.Date > period.To)
                {
                    dropOutOfRange++;
                    continue;
                }

                added++;
                reviews.Add(new RawReview(
                    ExternalId: dto.ReviewId,
                    Text: dto.Text ?? "",
                    Date: dto.Date.Value,
                    Stars: dto.Rating.Value,
                    BranchId: branch.BranchId,
                    AuthorName: dto.AuthorName,
                    AuthorPublicId: dto.AuthorId,
                    TextLanguage: dto.Language));
            }
        }

        return new DrainStats(reachedDateBound, batchesProcessed, totalSeen, added, dropDup, dropEmpty, dropOutOfRange);
    }

    private record DrainStats(
        bool ReachedDateBound,
        int BatchesProcessed,
        int TotalSeen,
        int Added,
        int Duplicates,
        int Empty,
        int OutOfRange);

    private async Task ClickReviewsTabAsync(IPage page, CancellationToken ct)
    {
        // Google Maps SPA sometimes doesn't render tabs on first load — retry with reload
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var tab = await page.WaitForSelectorAsync(
                    "button[role='tab'][aria-label*='Отзывы'], button[role='tab'][aria-label*='Reviews']",
                    new PageWaitForSelectorOptions { Timeout = 10_000 });
                if (tab != null)
                {
                    await tab.ClickAsync();
                    _logger.LogDebug("[GMaps-Hybrid] Вкладка 'Отзывы' нажата");
                    await Task.Delay(Random.Shared.Next(2000, 3000), ct);
                    return;
                }
            }
            catch when (attempt == 0)
            {
                _logger.LogDebug("[GMaps-Hybrid] Вкладка 'Отзывы' не найдена, перезагружаю страницу...");
                await page.ReloadAsync(new PageReloadOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = _options.NavigationTimeoutMs
                });
                await Task.Delay(Random.Shared.Next(3000, 5000), ct);
                continue;
            }
            catch { /* fallback below */ }

            break;
        }

        // Fallback: try by tab text content
        var clicked = await page.EvaluateAsync<bool>("""
            () => {
                const tabs = document.querySelectorAll('button[role="tab"]');
                for (const tab of tabs) {
                    if (tab.textContent.includes('Отзывы') || tab.textContent.includes('Reviews')) {
                        tab.click();
                        return true;
                    }
                }
                return false;
            }
        """);

        if (clicked)
            _logger.LogDebug("[GMaps-Hybrid] Вкладка 'Отзывы' нажата (fallback)");
        else
            _logger.LogWarning("[GMaps-Hybrid] Вкладка 'Отзывы' не найдена");

        await Task.Delay(Random.Shared.Next(2000, 3000), ct);
    }

    private async Task SelectSortByNewestAsync(IPage page, CancellationToken ct)
    {
        // ВАЖНО: у Google sort-кнопка имеет aria-haspopup="true" (НЕ "listbox" — старый код
        // искал именно listbox и всегда промахивался). Идентифицируется по тексту = названию
        // текущей сортировки. Класс HQzyZ обфусцирован и нестабилен — на него не опираемся.
        // Ловим уже выбранную "Сначала новые" / "Newest" — если так, ничего не делаем.
        var sortResult = await page.EvaluateAsync<string>("""
            () => {
                const candidates = Array.from(document.querySelectorAll('button[aria-haspopup]'));
                const sortNames = [
                    'Самые релевантные', 'Сначала новые', 'Лучшие', 'По новизне',
                    'Most relevant', 'Newest', 'Highest rating', 'Lowest rating'
                ];
                const newestNames = ['Сначала новые', 'По новизне', 'Newest'];

                const sortBtn = candidates.find(b => {
                    const text = (b.textContent || '').trim();
                    const aria = b.getAttribute('aria-label') || '';
                    return sortNames.some(n => text === n || aria === n);
                });

                if (!sortBtn) return 'NOT_FOUND';

                const currentText = (sortBtn.textContent || '').trim();
                if (newestNames.some(n => currentText === n)) return 'ALREADY_NEWEST';

                sortBtn.click();
                return 'OPENED';
            }
        """);

        if (sortResult == "ALREADY_NEWEST")
        {
            _logger.LogDebug("[GMaps-Hybrid] Сортировка уже 'Сначала новые', пропускаю");
            return;
        }

        if (sortResult == "NOT_FOUND")
        {
            // Дамп всех haspopup-кнопок для диагностики — увидим, что Google отдал.
            var dump = await page.EvaluateAsync<string>("""
                () => JSON.stringify(
                    Array.from(document.querySelectorAll('button[aria-haspopup]')).map(b => ({
                        aria_label: b.getAttribute('aria-label'),
                        aria_haspopup: b.getAttribute('aria-haspopup'),
                        text: (b.textContent || '').trim().slice(0, 80)
                    }))
                )
            """);
            _logger.LogWarning("[GMaps-Hybrid] Не нашёл sort dropdown. button[aria-haspopup] кандидаты: {Dump}", dump);
            return;
        }

        _logger.LogDebug("[GMaps-Hybrid] Sort dropdown открыт");
        await Task.Delay(Random.Shared.Next(500, 1000), ct);

        var optionClicked = await page.EvaluateAsync<bool>("""
            () => {
                // Опции могут быть [role="menuitemradio"], [role="menuitem"] или <li>
                const items = document.querySelectorAll('[role="menuitemradio"], [role="menuitem"], [role="option"]');
                for (const item of items) {
                    const text = (item.textContent || '').trim();
                    if (text === 'Сначала новые' || text === 'По новизне' || text === 'Newest') {
                        item.click();
                        return true;
                    }
                }
                return false;
            }
        """);

        if (optionClicked)
        {
            _logger.LogInformation("[GMaps-Hybrid] Сортировка 'Сначала новые' выбрана");
            await Task.Delay(Random.Shared.Next(2000, 3000), ct);
        }
        else
        {
            var menuDump = await page.EvaluateAsync<string>("""
                () => {
                    const items = document.querySelectorAll('[role="menuitemradio"], [role="menuitem"], [role="option"]');
                    return JSON.stringify(Array.from(items).map(i => ({
                        role: i.getAttribute('role'),
                        text: (i.textContent || '').trim().slice(0, 80)
                    })));
                }
            """);
            _logger.LogWarning("[GMaps-Hybrid] Не выбрал 'Сначала новые'. Найденные опции в открытом меню: {Dump}", menuDump);
        }
    }

    private async Task<ScrollMetrics> ScrollReviewsPanelAsync(IPage page)
    {
        // Контейнер ищем по факту: overflow-y: auto/scroll + есть [data-review-id] дети.
        // Хардкод классов (.m6QErb.DxyBCb) ненадёжен — на странице несколько .m6QErb с разными
        // комбинациями доп-классов, querySelector мог брать не тот.
        var json = await page.EvaluateAsync<string>("""
            async () => {
                const findScroller = () => {
                    const all = document.querySelectorAll('*');
                    for (const el of all) {
                        const ovy = getComputedStyle(el).overflowY;
                        if (ovy !== 'auto' && ovy !== 'scroll') continue;
                        if (el.scrollHeight <= el.clientHeight) continue;
                        if (el.querySelector('[data-review-id]')) return el;
                    }
                    // Fallback на старый селектор, если по overflow не нашли (например, до клика на tab)
                    return document.querySelector('.m6QErb.DxyBCb') || document.querySelector('.m6QErb');
                };

                const c = findScroller();
                if (!c) return JSON.stringify({ found: false });

                const before = { sh: c.scrollHeight, st: c.scrollTop, ch: c.clientHeight };
                const target = c.scrollHeight;
                const distance = target - c.scrollTop;
                if (distance > 0) {
                    const steps = 3 + Math.floor(Math.random() * 4);
                    for (let i = 1; i <= steps; i++) {
                        const progress = 1 - Math.pow(1 - i / steps, 2);
                        c.scrollTop = c.scrollHeight - c.clientHeight; // всегда в конец
                        await new Promise(r => setTimeout(r, 80 + Math.random() * 120));
                    }
                }
                const after = { sh: c.scrollHeight, st: c.scrollTop, ch: c.clientHeight };
                const reviewCount = c.querySelectorAll('[data-review-id]').length;
                return JSON.stringify({ found: true, before, after, reviewCount });
            }
        """);

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.GetProperty("found").GetBoolean())
                return new ScrollMetrics(false, 0, 0, 0, 0);

            var after = root.GetProperty("after");
            return new ScrollMetrics(
                Found: true,
                ScrollHeight: after.GetProperty("sh").GetInt32(),
                ScrollTop: after.GetProperty("st").GetInt32(),
                ClientHeight: after.GetProperty("ch").GetInt32(),
                ReviewCountInDom: root.GetProperty("reviewCount").GetInt32());
        }
        catch
        {
            return new ScrollMetrics(false, 0, 0, 0, 0);
        }
    }

    private record ScrollMetrics(bool Found, int ScrollHeight, int ScrollTop, int ClientHeight, int ReviewCountInDom);
}
