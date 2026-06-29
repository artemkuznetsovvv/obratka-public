// Тесты используют ENV vars для конфига сервиса (см. ProcessingGatewayFactory),
// которые глобальны для процесса. Параллельные коллекции (Postgres / Minio / Pipeline)
// перезаписывают друг друга и сломают setup. Делаем все тесты последовательными.
//
// Если станет узким горлышком, переходим на ConfigureAppConfiguration в IHostBuilder
// либо разделяем сборки тестов на unit / integration.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
