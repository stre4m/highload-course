# Архитектура

`src/SocialNetwork` — монолит на ASP.NET Core 10 с MVC-контроллерами. Конфигурация приложения находится в `Program.cs`. `AuthController` обрабатывает вход, `UsersController` — регистрацию, чтение и префиксный поиск анкет. Контроллеры валидируют запросы. `UserStore` выполняет SQL-запросы и преобразует результаты чтения в DTO.

Приложение использует один `NpgsqlDataSource`, асинхронные операции и параметризованные запросы. Данные хранятся в PostgreSQL 17. Регистрация выполняется одним атомарным `INSERT`. Поиск использует два параметризованных `LIKE`, сортировку по UUID и отдельный DTO публичного ответа.

ASP.NET Core Identity `PasswordHasher` вычисляет хеш пароля. ASP.NET Core BearerToken и Data Protection обеспечивают выпуск и проверку токенов. Публичная анкета доступна по UUID. Доступ к `/user/me` требует аутентификации. Swashbuckle формирует OpenAPI и Swagger UI в окружении `Development`.

Локальный Compose запускает API и PostgreSQL в отдельных контейнерах. API стартует после успешной проверки готовности PostgreSQL и подключается к `postgres:5432`. HTTP-порт API и порт PostgreSQL публикуются на loopback хоста. Параметры роли PostgreSQL передаются в контейнер API через `PGUSER` и `PGPASSWORD`.

Решения зафиксированы в [ADR-0001](decisions/0001-no-orm.md) и [ADR-0002](decisions/0002-postgresql-preferred.md).
