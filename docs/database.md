# PostgreSQL

Приложение работает с PostgreSQL 17 через Npgsql. Строка подключения передаётся через `ConnectionStrings__Postgres`. Локальный Compose публикует порт базы на `127.0.0.1:15432`. Порт задаётся через `POSTGRES_HOST_PORT`. Имя базы, роль и пароль задаются в `.env` переменными `POSTGRES_DB`, `POSTGRES_USER` и `POSTGRES_PASSWORD`.

Таблица `users` создаётся скриптом [001_create_users.sql](../infrastructure/sql/001_create_users.sql). В ней хранятся `id` (первичный ключ UUID), `birthdate` (date), имя, фамилия, пол, интересы, город и `password_hash`. PostgreSQL создаёт индекс для первичного ключа. Запрос чтения анкеты выбирает только поля профиля.

Хеш пароля хранится в формате ASP.NET Core Identity V3. Хеш вычисляется с индивидуальной случайной солью методом PBKDF2-HMAC-SHA512 с 210 000 итераций.

При первом запуске на пустом томе PostgreSQL создаёт базу из `POSTGRES_DB` и выполняет `001_create_users.sql` и `003_posts_and_friends.sql` из `/docker-entrypoint-initdb.d`.

## Посты и друзья

[003_posts_and_friends.sql](../infrastructure/sql/003_posts_and_friends.sql) создаёт таблицы `posts` и `friendships` в одной транзакции. `posts` хранит UUID поста, UUID автора, текст и время создания. Индекс `posts_author_created_idx` покрывает выборку последних постов автора по `author_id, created_at DESC, id DESC`.

Первичный ключ `friendships` состоит из `user_id` и `friend_id`. Ограничение запрещает связь пользователя с собой. Обратный индекс `friendships_friend_user_idx` используется при поиске подписчиков автора для инвалидации. Ссылки на `users` используют каскадное удаление.

## Индекс префиксного поиска

Скрипт [002_users_search_prefix.sql](../infrastructure/sql/002_users_search_prefix.sql) создаёт составной B-tree по `second_name` и `first_name` с классом операторов `varchar_pattern_ops`. Эксперимент применяет скрипт после базовых замеров в изолированной `app_tests_k6` или `app_tests_jmeter`. Локальная инициализация Compose применяет `001_create_users.sql`. Для применения индекса в `app_dev` требуется отдельное согласование изменения схемы.

Планы выполнения и аргументация порядка ключей приведены в [отчёте эксперимента](homework-2/README.md).

## Существующий том

Инициализация тома выполняется однократно. Смена `POSTGRES_DB` в `.env` меняет целевую базу подключения. На существующем томе базу с новым именем требуется создать отдельно.

Скрипт содержит `CREATE TABLE IF NOT EXISTS`. Для существующей таблицы структура сохраняется без проверки соответствия схеме.

При отсутствии `users` применение схемы требует проверки целевой базы и явного согласования изменения. Команда для запущенного PostgreSQL со стандартным именем базы и ролью.

```powershell
podman compose --env-file .env -f infrastructure/compose.yaml exec -T postgres psql -U appuser -d app_dev -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/001_create_users.sql
```

В конфигурации с изменёнными `POSTGRES_USER` или `POSTGRES_DB` аргументы `-U` и `-d` задаются по фактическим параметрам существующей базы.

Для существующей базы с таблицей `users` применить `003_posts_and_friends.sql` отдельно после проверки целевой базы и согласования изменения схемы. Команда применяется при наличии файла в контейнере PostgreSQL согласно актуальной конфигурации Compose.

```powershell
podman compose --env-file .env -f infrastructure/compose.yaml exec -T postgres psql -U appuser -d app_dev -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/003_posts_and_friends.sql
```
