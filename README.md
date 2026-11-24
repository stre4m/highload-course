# Социальная сеть

Учебный монолит на ASP.NET Core 10 и PostgreSQL 17. API реализует регистрацию, вход по UUID и паролю и чтение анкет. Доступ к PostgreSQL реализован через Npgsql (ADO.NET).

Команды выполняются из корня проекта в PowerShell 7. Для запуска контейнеров требуется Podman с провайдером Compose или Docker Compose. Для сборки и запуска API на хосте требуется .NET 10 SDK.

## ДЗ №1 - Проблемы высоких нагрузок

1. Подготовить [файл `.env`](#настройка) и [запустить API и PostgreSQL через Compose](#запуск-api-и-postgresql-в-контейнерах).
2. Импортировать [Postman-коллекцию](SocialNetwork.postman_collection.json) и запустить её целиком через Collection Runner. По умолчанию коллекция обращается к `http://127.0.0.1:5080`.

## Настройка

При отсутствии `.env` создать файл из шаблона.

```powershell
Copy-Item .env.example .env
```

Для нового тома заменить в `.env` значение `REPLACE_WITH_STRONG_LOCAL_PASSWORD` в `POSTGRES_PASSWORD` на пароль локальной роли. Для [существующего тома](docs/database.md#существующий-том) указать действующие `POSTGRES_DB`, `POSTGRES_USER` и `POSTGRES_PASSWORD`. По умолчанию API доступен на `127.0.0.1:5080`, PostgreSQL — на `127.0.0.1:15432`. Порты Compose задаются через `API_HOST_PORT` и `POSTGRES_HOST_PORT`.

На Windows для Podman требуется запущенная Podman Machine. При отсутствии машины выполнить `podman machine init`. Для остановленной машины выполнить `podman machine start`.

## Запуск API и PostgreSQL в контейнерах

```powershell
podman compose --env-file .env -f infrastructure/compose.yaml up -d --build
```

Команда для Docker Compose.

```powershell
docker compose --env-file .env -f infrastructure/compose.yaml up -d --build
```

Compose задаёт окружение `Development` и запускает API после успешной проверки PostgreSQL.

## Запуск API на хосте

Запустить PostgreSQL и проверить готовность сервера.

```powershell
podman compose --env-file .env -f infrastructure/compose.yaml up -d postgres
podman compose --env-file .env -f infrastructure/compose.yaml exec -T postgres pg_isready -h 127.0.0.1
```

Ответ `accepting connections` подтверждает готовность сервера принимать подключения по TCP. Доступ к конкретной базе и наличие схемы проверяются отдельно. Для тома без таблицы `users` требуется [применение схемы](docs/database.md#существующий-том).

В строке подключения указать порт `POSTGRES_HOST_PORT`, имя базы `POSTGRES_DB` и роль `POSTGRES_USER` из `.env`. Пароль `POSTGRES_PASSWORD` вводится в приглашении PowerShell.

```powershell
$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
$connection['Host'] = '127.0.0.1'
$connection['Port'] = '15432'
$connection['Database'] = 'app_dev'
$connection['Username'] = 'appuser'
$connection['Password'] = Read-Host 'Пароль PostgreSQL' -MaskInput
$env:ConnectionStrings__Postgres = $connection.ConnectionString
dotnet run --project src/SocialNetwork -- --urls http://127.0.0.1:5080
```

Профиль запуска `SocialNetwork` задаёт окружение `Development`.

## Проверка

В окружении `Development` доступны [Swagger UI](http://127.0.0.1:5080/swagger/index.html) и [OpenAPI JSON](http://127.0.0.1:5080/swagger/v1/swagger.json). Для проверки `GET /user/me` после запроса `POST /login` ввести значение `token` в поле **Authorize** без префикса `Bearer`.

Сборка решения.

```powershell
dotnet build SocialNetwork.sln
```

[Postman-коллекция](SocialNetwork.postman_collection.json) выполняется целиком через Collection Runner. Значение `base_url` по умолчанию — `http://127.0.0.1:5080`. При изменении порта API требуется изменить `base_url`. Каждый успешный запрос регистрации создаёт анкету в подключённой БД.

Для запуска коллекции из командной строки требуются работающий API, Node.js и npm.

```powershell
npx --yes newman run SocialNetwork.postman_collection.json
```

См. также [REST API](docs/api.md), [PostgreSQL](docs/database.md), [Локальная разработка](docs/development.md), [Архитектура](docs/architecture.md).
