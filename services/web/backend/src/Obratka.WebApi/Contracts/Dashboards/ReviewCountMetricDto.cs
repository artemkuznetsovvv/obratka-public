namespace Obratka.WebApi.Contracts.Dashboards;

// Метрика 1: «Количество отзывов».
// total_current / total_previous считаются только по тем источникам, которые
// сейчас выбраны в фильтре «Источник» (т.е. sum по тем элементам bySource,
// для которых source ∈ filter.sources). bySource всегда содержит ровно три
// записи (2gis / yandex / google) — даже если по какому-то источнику 0
// отзывов (см. спеку «3 строки всегда»).
//
// hasPreviousPeriod = false, когда period_from или period_to не заданы
// (фильтр периода = «с самого начала»). В этом случае UI показывает «—»
// вместо стрелки тренда.
public sealed record ReviewCountMetricDto(
    long TotalCurrent,
    long TotalPrevious,
    bool HasPreviousPeriod,
    IReadOnlyList<ReviewCountSourceDto> BySource);

public sealed record ReviewCountSourceDto(
    string Source,
    long Current,
    long Previous);
