# Архитектура

`src/SocialNetwork` — монолит на ASP.NET Core 10 с MVC-контроллерами. Конфигурация приложения находится в `Program.cs`. `AuthController` обрабатывает вход, `UsersController` — регистрацию, чтение и префиксный поиск анкет. Контроллеры валидируют запросы. `UserStore` выполняет SQL-запросы и преобразует результаты чтения в DTO.

Приложение использует `NpgsqlDataSource`, асинхронные операции и параметризованные запросы. По умолчанию один пул обслуживает все операции. Параметр `ConnectionStrings__PostgresRead` включает отдельный пул для публичного чтения и поиска анкет. Регистрация, проверка пароля и `GET /user/me` используют основной узел. Данные хранятся в PostgreSQL 17. Регистрация выполняется одним атомарным `INSERT`. Поиск использует два параметризованных `LIKE`, сортировку по UUID и отдельный DTO публичного ответа.

ASP.NET Core Identity `PasswordHasher` вычисляет хеш пароля. ASP.NET Core BearerToken и Data Protection обеспечивают выпуск и проверку токенов. Публичная анкета доступна по UUID. Доступ к `/user/me` требует аутентификации. Swashbuckle формирует OpenAPI и Swagger UI в окружении `Development`.

Локальный Compose запускает API и PostgreSQL в отдельных контейнерах. API стартует после успешной проверки готовности PostgreSQL и подключается к `postgres:5432`. HTTP-порт API и порт PostgreSQL публикуются на loopback хоста. Параметры роли PostgreSQL передаются в контейнер API через `PGUSER` и `PGPASSWORD`.

Стенд потоковой репликации разворачивается отдельным Compose-проектом. Параметры запуска и результаты приведены в [отчёте ДЗ №3](homework-3/README.md).

Без `ConnectionStrings__RabbitMq` лента формируется через `FeedCache`. После записи кеш инвалидирует снимки и ставит задачи обновления в `FeedRefreshQueue`. Режим описан в [ADR-0004](decisions/0004-friend-feed-cache.md).

С `ConnectionStrings__RabbitMq` создание поста записывает задачу в PostgreSQL outbox. `FeedDeliveryWorker` передаёт задачу в RabbitMQ, материализует адресные записи и публикует WebSocket-события. `FeedSocketSubscriptions` маршрутизирует их к соединениям текущего экземпляра. Обе реплики читают ленту через `FeedDeliveryStore` из общей БД и проверяют токены через общий каталог ключей Data Protection. Решение описано в [ADR-0006](decisions/0006-feed-delivery.md).

`DialogsController` обслуживает отправку и чтение сообщений через `DialogStore`. Хранилище проверяет собеседника и получает карту бакета из основного PostgreSQL за один запрос. Отдельные пулы Npgsql направляют записи и чтения на шарды. Выбор ключа и схема переключения зафиксированы в [ADR-0005](decisions/0005-dialog-sharding.md).

Решения о доступе к данным и маршрутизации публичного чтения зафиксированы в [ADR-0001](decisions/0001-no-orm.md), [ADR-0002](decisions/0002-postgresql-preferred.md) и [ADR-0003](decisions/0003-postgresql-read-routing.md).
