from datetime import datetime
from typing import TYPE_CHECKING

from obratka.llm.client import LLMClient
from obratka.llm.schemas import TopicMap, ReviewAnalysis
from obratka.logging_setup import get_logger
from obratka.observability.spans import step_span
from obratka.utils.topics import BASE_TOPICS, normalize_topic

if TYPE_CHECKING:
    from obratka.report.artifacts import ArtifactCollector

log = get_logger(__name__)

async def cluster_topics(
    raw_topics: list[str],
    base_topics: list[str],
    llm: LLMClient,
    *,
    collector: "ArtifactCollector | None" = None,
) -> TopicMap:
    started_at = datetime.now()
    unique_topics = sorted(set(raw_topics))
    async with step_span("step21", unique_topics_in=len(unique_topics)) as span:
        deterministic_mapping = {
            topic: normalize_topic(topic)
            for topic in unique_topics
            if normalize_topic(topic) != topic
        }
        remaining_topics = [
            topic for topic in unique_topics if topic not in deterministic_mapping
        ]

        if not remaining_topics:
            topic_map = TopicMap(mapping=deterministic_mapping, canonical_topics=[])
            span.set_attribute("clusters_out", 0)
            if collector is not None:
                from obratka.report.artifacts import Step21Artifact, make_stage_stats

                collector.record_step21(
                    Step21Artifact(
                        stats=make_stage_stats("step21", started_at),
                        unique_freeform_topics_in=len(unique_topics),
                        clusters_out=0,
                        mapping=deterministic_mapping,
                        canonical_topics=[],
                    )
                )
            return topic_map

        sys_prompt = (
            "Тебе дан список тем, которые модель выделила в отзывах о бизнесе.\n"
            "Многие из них — синонимы или вариации одного и того же.\n\n"
            "Сгруппируй их в кластеры. Для каждой исходной темы укажи каноническое имя.\n"
            "Старайся переиспользовать уже существующие имена базового набора, если они подходят.\n\n"
            "Базовый набор:\n"
            + ", ".join(base_topics) + "\n\n"
            "Верни JSON с ДВУМЯ полями:\n"
            "1. \"mapping\" — словарь, где ключ = исходная тема, значение = каноническое имя.\n"
            "   Пример: {\"вкус еды\": \"еда/напитки\", \"вежливость\": \"персонал\", \"дизайн\": \"интерьер\"}\n"
            "2. \"canonical_topics\" — список новых канонических тем, которых НЕТ в базовом наборе.\n"
            "   Если все темы удалось замапить на базовый набор — верни пустой список [].\n\n"
            "Оба поля обязательны. Строго JSON по схеме."
        )
    
        prompt = f"Темы для кластеризации: {remaining_topics}"
        cost_before = llm.total_cost if llm is not None else 0.0
    
        try:
            content, _ = await llm.complete(
                model="cluster",
                response_model=TopicMap,
                messages=[
                    {"role": "system", "content": sys_prompt},
                    {"role": "user", "content": prompt}
                ]
            )
            topic_map = content
        except Exception as e:
            log.error("Clustering failed", error=str(e))
            topic_map = TopicMap(mapping={}, canonical_topics=[])

        topic_map.mapping = {**deterministic_mapping, **topic_map.mapping}
        topic_map.canonical_topics = [
            normalize_topic(t) for t in topic_map.canonical_topics
        ]
        topic_map.canonical_topics = sorted(
            {t for t in topic_map.canonical_topics if t not in base_topics}
        )

        cost_after = llm.total_cost if llm is not None else 0.0
        span.set_attribute("clusters_out", len(topic_map.canonical_topics))
        span.set_attribute("cost_usd", cost_after - cost_before)

        if collector is not None:
            from obratka.report.artifacts import Step21Artifact, make_stage_stats

            mapping = dict(list(topic_map.mapping.items())[:50])
            collector.record_step21(
                Step21Artifact(
                    stats=make_stage_stats(
                        "step21", started_at, cost_usd=cost_after - cost_before
                    ),
                    unique_freeform_topics_in=len(unique_topics),
                    clusters_out=len(topic_map.canonical_topics),
                    mapping=mapping,
                    canonical_topics=topic_map.canonical_topics,
                )
            )
        return topic_map

def apply_topic_map(
    analyses: list[ReviewAnalysis],
    topic_map: TopicMap,
) -> list[ReviewAnalysis]:
    for a in analyses:
        for aspect in a.aspects:
            if aspect.topic in topic_map.mapping:
                aspect.topic = topic_map.mapping[aspect.topic]
            aspect.topic = normalize_topic(aspect.topic, aspect.fragment)
            aspect.is_freeform = (
                aspect.topic not in BASE_TOPICS
                and aspect.topic not in topic_map.canonical_topics
            )
    return analyses
