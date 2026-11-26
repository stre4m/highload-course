# Архитектура

`src/SocialNetwork` — монолит на ASP.NET Core 10 с MVC-контроллерами. Конфигурация приложения находится в `Program.cs`. `AuthController` обрабатывает вход, `UsersController` — регистрацию, чтение и префиксный поиск анкет. Контроллеры валидируют запросы. `UserStore` выполняет SQL-запросы и преобразует результаты чтения в DTO.

Приложение использует `NpgsqlDataSource`, асинхронные операции и параметризованные запросы. По умолчанию один пул обслуживает все операции. Параметр `ConnectionStrings__PostgresRead` включает отдельный пул для публичного чтения и поиска анкет. Регистрация, проверка пароля и `GET /user/me` используют основной узел. Данные хранятся в PostgreSQL 17. Регистрация выполняется одним атомарным `INSERT`. Поиск использует два параметризованных `LIKE`, сортировку по UUID и отдельный DTO публичного ответа.

ASP.NET Core Identity `PasswordHasher` вычисляет хеш пароля. ASP.NET Core BearerToken и Data Protection обеспечивают выпуск и проверку токенов. Публичная анкета доступна по UUID. Доступ к `/user/me` требует аутентификации. Swashbuckle формирует OpenAPI и Swagger UI в окружении `Development`.

Локальный Compose запускает API и PostgreSQL в отдельных контейнерах. API стартует после успешной проверки готовности PostgreSQL и подключается к `postgres:5432`. HTTP-порт API и порт PostgreSQL публикуются на loopback хоста. Параметры роли PostgreSQL передаются в контейнер API через `PGUSER` и `PGPASSWORD`.

Стенд потоковой репликации разворачивается отдельным Compose-проектом. Параметры запуска и результаты приведены в [отчёте ДЗ №3](homework-3/README.md).

Решения зафиксированы в [ADR-0001](decisions/0001-no-orm.md), [ADR-0002](decisions/0002-postgresql-preferred.md) и [ADR-0003](decisions/0003-postgresql-read-routing.md).
