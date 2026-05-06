using MassTransit;
using ProcessingGateway.Application.Messaging.Contracts;

namespace ProcessingGateway.Application.Consumers;

/// MassTransit-определение для `LlmResultMessageConsumer`:
/// - Подписка на очередь `llm.results` (а не на дефолтное имя `LlmResultMessage`,
///   которое сгенерил бы `ConfigureEndpoints`).
/// - **Raw JSON deserializer** — LLM-сервис публикует body без MassTransit envelope
///   (см. LLM_PYTHON_QUICKSTART.md §4 «Транспорт»).
///   `RawSerializerOptions.AnyMessageType` говорит MT интерпретировать payload как
///   `LlmResultMessage` (наш registered consumer) независимо от отсутствия `messageType`.
///
/// `LlmRequestMessage` (что мы публикуем) остаётся в **envelope-формате** — это публикация,
/// и сериализер для исходящих сообщений не меняется.
public sealed class LlmResultMessageConsumerDefinition : ConsumerDefinition<LlmResultMessageConsumer>
{
    public LlmResultMessageConsumerDefinition()
    {
        EndpointName = "llm.results";
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<LlmResultMessageConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseRawJsonDeserializer(RawSerializerOptions.AnyMessageType);

        // EF Outbox/UseBusOutbox зарегистрированы глобально в Program.cs `AddEntityFrameworkOutbox`
        // → publish из consumer-а (AnalysisCompletedEvent) автоматически кладётся в Outbox-таблицу
        // и отправляется ПОСЛЕ commit DbContext.SaveChangesAsync. Дополнительная per-endpoint
        // конфигурация не нужна.
    }
}
