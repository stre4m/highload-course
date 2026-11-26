# Социальная сеть

Учебный монолит на ASP.NET Core 10 и PostgreSQL 17. API реализует регистрацию, вход по UUID и паролю, чтение и поиск анкет. Доступ к PostgreSQL реализован через Npgsql (ADO.NET).

Команды выполняются из корня проекта в PowerShell 7. Для запуска контейнеров требуется Podman с провайдером Compose или Docker Compose. Для сборки и запуска API на хосте требуется .NET 10 SDK.

## ДЗ №1 - Проблемы высоких нагрузок

1. Подготовить [файл `.env`](#настройка) и [запустить API и PostgreSQL через Compose](#запуск-api-и-postgresql-в-контейнерах).
2. Импортировать [Postman-коллекцию](SocialNetwork.postman_collection.json) и запустить её целиком через Collection Runner. По умолчанию коллекция обращается к `http://127.0.0.1:5080`.

## ДЗ №2 - Производительность индексов

### Apache JMeter в WSL Ubuntu

[Отчёт JMeter](docs/homework-2/jmeter/README.md) содержит отдельные замеры и архив JTL. Нужны WSL Ubuntu в режиме NAT, JMeter 5.6.3, OpenJDK 17 и свободные порты 5082 и 5083. Скрипт запускает измерения в изолированной `app_tests_jmeter`. При первом импорте эта база должна отсутствовать. Команды выполняются из корня проекта в PowerShell.

```powershell
$postgres = 'codex-dev-postgres-1'
$env:PYTHONUTF8 = '1'
dotnet build SocialNetwork.sln -c Release --no-restore
python scripts/search-benchmark.py $postgres TestResults/hw2-jmeter --jmeter
python scripts/search-charts.py TestResults/hw2-jmeter/metrics.json TestResults/hw2-jmeter
wsl -d Ubuntu -- jmeter -t scripts/search-jmeter.jmx -j TestResults/hw2-jmeter/gui.log
```

Команда `-t` без `-n` открывает тест-план в GUI. Нагрузочный скрипт выполняет JMeter с `-n` и сохраняет JTL. Пример генерации дашборда по завершённому замеру.

```powershell
wsl -d Ubuntu -- jmeter -g TestResults/hw2-jmeter/after-1000-measured.jtl -o TestResults/hw2-jmeter/after-1000-dashboard -j TestResults/hw2-jmeter/dashboard.log
```

### Grafana k6

[Отчёт эксперимента](docs/homework-2/README.md) содержит замеры `GET /user/search` на 1 000 000 анкет до и после создания индекса с Grafana k6.

Для повторения нужны работающий проектный PostgreSQL 17, свободный порт `127.0.0.1:5082`, .NET 10 SDK, Python 3.13, Grafana k6 2.4 и `matplotlib`. В переменной `$postgres` указывается имя контейнера проектного PostgreSQL. Для первого импорта требуется экземпляр PostgreSQL без базы `app_tests_k6`. Измерения создают индекс только в `app_tests_k6` и оставляют базу с загруженными данными.

```powershell
$postgres = 'codex-dev-postgres-1'
$env:PYTHONUTF8 = '1'
dotnet restore SocialNetwork.sln
dotnet build SocialNetwork.sln -c Release --no-restore
python scripts/search-benchmark.py $postgres TestResults/hw2-k6 --phase prepare
python scripts/search-benchmark.py $postgres TestResults/hw2-k6 --phase measure --k6 'C:/Program Files/k6/k6.exe'
python scripts/search-charts.py TestResults/hw2-k6/metrics.json TestResults/hw2-k6
```

Если `k6` доступен в `PATH`, параметр `--k6` можно опустить. При установке в другой каталог указывается фактический путь к `k6.exe`. При публикации PostgreSQL на другом порту передаётся `--port` с фактическим значением. Фаза `measure` запускает отдельный Release API и останавливает его по завершении. CSV и промежуточные файлы сохраняются в `TestResults/hw2-k6`.

## ДЗ №3. Репликация

[Инструкция по стенду и замерам в JMeter](docs/homework-3/README.md) содержит порядок запуска эксперимента и проверки потоковой репликации. Для двух фаз чтения используется один [план JMeter](scripts/replication-reads.jmx).

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
