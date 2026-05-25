using Obratka.WebApi.Contracts.Companies;

namespace Obratka.WebApi.Companies.Grouping;

// Автогруппировка карточек с разных источников в «физические» филиалы.
// Алгоритм:
//   1) Для каждой пары карточек (из разных источников) считаем score:
//        +1 если house-anchor совпал (дом+литера)
//        +1 если Jaccard по токенам адреса ≥ AddressJaccardThreshold
//        +1 если Jaccard по токенам имени   ≥ NameJaccardThreshold
//   2) Если score ≥ MinScore — связываем парой в Union-Find.
//   3) Connected components = логические филиалы.
//   4) Карточки без пары (singleton-component) уходят в unmatched.
//
// Координаты сейчас не учитываются (парсер не отдаёт), но интерфейс готов:
// когда появятся, добавится 4-й критерий «координаты в пределах ~100м».
public interface IBranchGroupingService
{
    GroupingResult Group(IReadOnlyList<BranchSearchResultItem> items, string city);
}

public sealed record GroupingResult(
    IReadOnlyList<LogicalGroupCandidate> Groups,
    IReadOnlyList<BranchSearchResultItem> Unmatched);

public sealed record LogicalGroupCandidate(
    string GroupKey,                              // временный id (g-1, g-2, …) — нестабилен между вызовами
    string CanonicalName,
    string CanonicalAddress,
    string City,
    int MatchScore,                               // максимальный score внутри группы — UI показывает как бейдж
    IReadOnlyList<BranchSearchResultItem> Items);

public sealed class BranchGroupingService : IBranchGroupingService
{
    // Параметры алгоритма — оставлены константами, потому что на MVP-данных так
    // даёт минимальное число ложных склеек. Если эмпирика поменяется — выведем
    // в IOptions.
    private const double AddressJaccardThreshold = 0.45;
    private const double NameJaccardThreshold = 0.40;
    private const int MinScore = 2;

    public GroupingResult Group(IReadOnlyList<BranchSearchResultItem> items, string city)
    {
        if (items.Count == 0)
            return new GroupingResult([], []);

        // Подготовка: индексируем токены каждого айтема ОДИН раз.
        var ctx = new BranchContext[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            ctx[i] = new BranchContext(
                Item: it,
                Anchor: BranchTextNormalizer.ExtractHouseAnchor(it.Address),
                AddressTokens: BranchTextNormalizer.AddressTokens(it.Address, city),
                NameTokens: BranchTextNormalizer.NameTokens(it.Name));
        }

        // Union-Find по индексам.
        var uf = new UnionFind(items.Count);
        var maxScoreByRoot = new Dictionary<int, int>();

        for (var i = 0; i < items.Count; i++)
        {
            for (var j = i + 1; j < items.Count; j++)
            {
                // Не склеиваем карточки одного источника — это разные точки, парсер
                // вернул несколько результатов на запрос (Skuratov coffee, 16 карточек 2GIS).
                if (string.Equals(ctx[i].Item.Source, ctx[j].Item.Source, StringComparison.OrdinalIgnoreCase))
                    continue;

                var score = ScorePair(ctx[i], ctx[j]);
                if (score >= MinScore)
                {
                    uf.Union(i, j);
                    var root = uf.Find(i);
                    if (!maxScoreByRoot.TryGetValue(root, out var prev) || score > prev)
                        maxScoreByRoot[root] = score;
                }
            }
        }

        // Собираем компоненты.
        var componentsByRoot = new Dictionary<int, List<int>>();
        for (var i = 0; i < items.Count; i++)
        {
            var root = uf.Find(i);
            if (!componentsByRoot.TryGetValue(root, out var list))
            {
                list = new List<int>();
                componentsByRoot[root] = list;
            }
            list.Add(i);
        }

        var groups = new List<LogicalGroupCandidate>();
        var unmatched = new List<BranchSearchResultItem>();
        var groupIndex = 0;

        foreach (var (root, memberIndices) in componentsByRoot)
        {
            if (memberIndices.Count == 1)
            {
                // Singleton — карточка не нашла пары → unmatched.
                unmatched.Add(ctx[memberIndices[0]].Item);
                continue;
            }

            var members = memberIndices.Select(i => ctx[i].Item).ToList();
            var canonical = PickCanonical(members);
            groupIndex++;
            groups.Add(new LogicalGroupCandidate(
                GroupKey: $"g-{groupIndex}",
                CanonicalName: canonical.Name,
                CanonicalAddress: canonical.Address ?? string.Empty,
                City: city,
                MatchScore: maxScoreByRoot.TryGetValue(root, out var ms) ? ms : MinScore,
                Items: members));
        }

        return new GroupingResult(groups, unmatched);
    }

    private static int ScorePair(in BranchContext a, in BranchContext b)
    {
        var score = 0;
        if (a.Anchor is not null && a.Anchor == b.Anchor) score++;
        if (BranchTextNormalizer.Jaccard(a.AddressTokens, b.AddressTokens) >= AddressJaccardThreshold) score++;
        if (BranchTextNormalizer.Jaccard(a.NameTokens, b.NameTokens) >= NameJaccardThreshold) score++;
        return score;
    }

    // «Каноническое» представление группы. Имя — самое длинное (обычно самое полное,
    // напр. «Skuratov Coffee roasters» > «Skuratov Coffee»). Адрес — тоже самое длинное
    // среди непустых, на эвристике «длиннее = подробнее».
    private static BranchSearchResultItem PickCanonical(List<BranchSearchResultItem> members)
    {
        var byName = members.OrderByDescending(m => m.Name?.Length ?? 0).First();
        var byAddress = members
            .Where(m => !string.IsNullOrWhiteSpace(m.Address))
            .OrderByDescending(m => m.Address!.Length)
            .FirstOrDefault();
        if (byAddress is null) return byName;
        // Возвращаем «склейку» — имя из одного, адрес из другого. Это не настоящий
        // BranchSearchResultItem (там есть Id), но для канонизации хватит.
        return byName with { Address = byAddress.Address };
    }

    private readonly record struct BranchContext(
        BranchSearchResultItem Item,
        string? Anchor,
        HashSet<string> AddressTokens,
        HashSet<string> NameTokens);

    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly int[] _rank;

        public UnionFind(int n)
        {
            _parent = new int[n];
            _rank = new int[n];
            for (var i = 0; i < n; i++) _parent[i] = i;
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        public void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb) return;
            if (_rank[ra] < _rank[rb]) (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            if (_rank[ra] == _rank[rb]) _rank[ra]++;
        }
    }
}
