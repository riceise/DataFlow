# DataFlow

Платформа импорта, валидации и фоновой обработки данных.
Сейчас реализованы API управления источниками и схемой Customer, доменная модель импорта и хранение в PostgreSQL (EF Core, первая миграция), валидация конфигурации на старте, структурированное JSON-логирование с `X-Correlation-ID`, локальная инфраструктура в Docker Compose и базовый CI.

## Требования

- .NET SDK 10.0.100 или более новый стабильный SDK 10.0 (см. `global.json`).
- Node.js 24 LTS — для будущего Vue frontend.
- Docker Desktop с запущенным Linux engine — для PostgreSQL, RabbitMQ, MinIO и интеграционных тестов с Testcontainers.

## Локальная инфраструктура (Docker Compose)

1. Скопируйте шаблон переменных окружения в локальный `.env` (файл `.env` исключён из Git):

```powershell
Copy-Item .env.example .env
```

2. Запустите PostgreSQL, RabbitMQ и MinIO (порты публикуются только на `127.0.0.1`):

```powershell
docker compose --env-file .env -f deploy/docker-compose.yml up -d
```

3. Проверьте статус и health-проверки контейнеров:

```powershell
docker compose --env-file .env -f deploy/docker-compose.yml ps
```

4. Остановка контейнеров с сохранением данных в named volumes:

```powershell
docker compose --env-file .env -f deploy/docker-compose.yml down
```

5. Полный сброс локальных данных (удаление контейнеров и named volumes):

```powershell
docker compose --env-file .env -f deploy/docker-compose.yml down -v
```

Локальные интерфейсы инфраструктуры:
- PostgreSQL: `127.0.0.1:5432`
- RabbitMQ AMQP: `127.0.0.1:5672`, Management UI: `http://127.0.0.1:15672`
- MinIO S3 API: `http://127.0.0.1:9000`, Console UI: `http://127.0.0.1:9001`

## Сборка и тесты

Из корня проекта:

```powershell
dotnet restore DataFlow.sln
dotnet format DataFlow.sln --verify-no-changes --no-restore
dotnet build DataFlow.sln --no-restore
dotnet test DataFlow.sln --no-build
```

## Запуск приложений

### Миграции PostgreSQL

Версия EF CLI закреплена в `.config/dotnet-tools.json`. После запуска PostgreSQL примените миграции отдельно от запуска API/Worker:

```powershell
dotnet tool restore
# ConnectionStrings__DataFlow задаётся в окружении с параметрами вашей локальной БД.
dotnet ef database update --project src/DataFlow.Infrastructure
```

CLI читает `ConnectionStrings__DataFlow`; файл `.env` автоматически не загружается.
API и Worker получают scoped `DataFlowDbContext` через DI и не применяют миграции при старте.
Файлы миграций и snapshot находятся в `src/DataFlow.Infrastructure/Persistence/Migrations` и хранятся вместе с кодом.
Интеграционные тесты сами поднимают изолированный PostgreSQL 18 через Testcontainers и применяют миграции к пустой БД.

### API и Worker

API:

```powershell
dotnet run --project src/DataFlow.Api --launch-profile http
```

- `http://localhost:5030/health/live` — `200 Healthy` (возвращает заголовок `X-Correlation-ID`).
- `http://localhost:5030/openapi/v1.json` — спецификация в Development.
- `http://localhost:5030/scalar/v1` — интерактивный просмотр API в Development.
- Для HTTPS сохранён профиль `https`: `https://localhost:7242` и `http://localhost:5030`.

Worker в отдельном терминале:

```powershell
dotnet run --project src/DataFlow.Worker
```

При старте API и Worker валидируют секции `ConnectionStrings`, `RabbitMq`, `Storage` и `Import` и пишут структурированные JSON-логи в stdout.

### Источники данных

Префикс API — `/api/v1/data-sources`:

| Метод | Путь | Результат |
|---|---|---|
| POST | `/` | Создать источник: 201, DTO и Location |
| GET | `/?page=1&pageSize=50` | Список `{items, page, pageSize, total}` по CreatedAt desc |
| GET | `/{id}` | Получить источник |
| PUT | `/{id}` | Обновить имя, форматы и схему |
| POST | `/{id}/activate` | Активировать источник |
| POST | `/{id}/deactivate` | Деактивировать источник |

`page` начинается с 1; `pageSize` — от 1 до 200, по умолчанию 50. Пример тела POST/PUT:

```json
{
  "name": "Клиенты",
  "acceptedFormats": ["Csv", "Xlsx"],
  "schema": {
    "keyFields": ["externalId"],
    "csvDelimiter": ",",
    "fields": [
      {"name": "externalId", "type": "string", "required": true, "maxLength": 80},
      {"name": "name", "type": "string", "required": true, "maxLength": 200},
      {"name": "email", "type": "email", "required": true, "maxLength": 254},
      {"name": "registeredAt", "type": "date", "required": false, "format": "yyyy-MM-dd"},
      {"name": "isActive", "type": "boolean", "required": true}
    ]
  }
}
```

Тип данных всегда `Customer`; `dataType` в запросе запрещён. `registeredAt` можно исключить; остальные четыре поля обязательны с указанными типами и required. Длины строк можно уменьшить до 1; для date/boolean maxLength запрещён. Ключ — строго `["externalId"]`; delimiter — один символ, по умолчанию запятая; regexPattern запрещён. Неизвестные свойства запроса отклоняются.

Ответ содержит `id`, `name`, `dataType`, `acceptedFormats` (массив строк), `schema` с `version`, `schemaVersion`, `isActive`, `createdAt`, `updatedAt`. Новый источник активен и имеет версию 1. Изменение схемы увеличивает версию; перестановка полей, изменение имени и форматов её сохраняют. Snapshots существующих ImportJob сохраняются.

Ошибки возвращаются в формате RFC 9457 (`application/problem+json`): `source_not_found` (404), `invalid_schema` (400), `validation_failed` (400). Валидация добавляет `errors` со списками сообщений по путям, например `schema.fields[0].name`. Внутренние ошибки возвращают 500 с кодом `internal_error` без деталей исключения.

Тесты API используют WebApplicationFactory и отдельный PostgreSQL 18 Testcontainer с применением существующей миграции. Работающие RabbitMQ и MinIO для них не требуются. Загрузка и обработка файлов появятся на последующих этапах.

## Структура

```text
src/
  DataFlow.Domain/           # Бизнес-модель
  DataFlow.Application/      # Use cases и порты
  DataFlow.Infrastructure/   # Конфигурация, адаптеры хранения и транспорта
  DataFlow.Api/              # HTTP host
  DataFlow.Worker/           # Background host
tests/
  DataFlow.Domain.Tests/
  DataFlow.Application.Tests/
  DataFlow.IntegrationTests/
deploy/
  docker-compose.yml         # PostgreSQL, RabbitMQ, MinIO
.github/workflows/
  ci.yml                     # Сборка, проверка форматирования и тесты
```

Общие настройки сборки находятся в `Directory.Build.props`, версии пакетов — в `Directory.Packages.props`.
Локальные секреты и пользовательские конфигурации исключены через `.gitignore`.
