using Testcontainers.RabbitMq;

namespace ProcessingGateway.Tests.Infrastructure;

/// RabbitMQ-контейнер для интеграционных тестов с реальным MassTransit-брокером.
/// Используется только в Этапе 5+ end-to-end сценариях; unit-тесты Parser-клиента
/// и т.д. поднимают только Postgres/MinIO.
public class RabbitFixture : IAsyncLifetime
{
    public RabbitMqContainer Container { get; } = new RabbitMqBuilder("rabbitmq:3-management")
        .WithUsername("gateway")
        .WithPassword("gateway_pwd")
        .Build();

    public string Host => Container.Hostname;
    public int Port => Container.GetMappedPublicPort(5672);
    public string Username => "gateway";
    public string Password => "gateway_pwd";

    /// MassTransit принимает host без явной port — `RabbitMq:Host` = "host:port".
    public string HostWithPort => $"{Host}:{Port}";

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

[CollectionDefinition("Pipeline")]
public class PipelineCollection : ICollectionFixture<PgFixture>, ICollectionFixture<MinioFixture>, ICollectionFixture<RabbitFixture>
{
    // Объединённая коллекция для end-to-end pipeline-тестов: один Postgres + один MinIO +
    // один RabbitMQ на всю сборку. Сильно дешевле, чем поднимать каждый раз.
}
