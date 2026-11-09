# DataFlow

Платформа импорта, валидации и фоновой обработки данных.
Реализован сквозной импорт CSV и XLSX: API управления источниками и схемой Customer, библиотека пользовательских правил (Customer/Tabular), потоковая загрузка в S3/MinIO, transactional outbox в PostgreSQL, доставка через RabbitMQ и обработка Worker с lease/fencing и транзакционными батчами. Результаты строк и ошибки доступны через API и в виде потокового CSV-отчёта; агрегированные показатели — через Dashboard Summary. Есть валидация конфигурации, структурированные JSON-логи, локальная инфраструктура Docker Compose и CI.

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

### Библиотека правил и исторические данные

В `Ui/` доступен маршрут `/rules`: вход, загрузка JSON-правил, версии, каталог, обмен и запуск CSV-проверки. API использует cookie-аутентификацию; изменяющие запросы передают `X-CSRF-TOKEN`, полученный через `GET /api/v1/auth/antiforgery`.

Для существующей БД после расширяющих migrations запустите перенос с явным UserId владельца:

```powershell
dotnet run --project tools/DataFlow.BackfillTool -- "<existing-user-guid>"
```

Порядок остановки приложений, назначения владельца и повторного запуска описан в [docs/rules-backfill.md](docs/rules-backfill.md). Фактический статус расширения — [RULES_IMPLEMENTATION.md](RULES_IMPLEMENTATION.md).

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

При старте API и Worker валидируют секции `ConnectionStrings`, `RabbitMq`, `Outbox`, `Storage` и `Import` и пишут структурированные JSON-логи в stdout.

Worker требует PostgreSQL и RabbitMQ из Compose. При недоступном брокере host продолжает работать, dispatcher и consumer переподключаются с backoff до 30 секунд; outbox сохраняется в PostgreSQL. Подключение настраивается через `RabbitMq__Host`, `RabbitMq__Port`, `RabbitMq__VirtualHost`, `RabbitMq__User`, `RabbitMq__Password`.

Dispatcher опрашивает outbox через `PeriodicTimer`: `Outbox:BatchSize` = 32 (1–100), `LeaseSeconds` = 30 (10–300), `PollIntervalSeconds` = 2 (1–60). Короткая транзакция делает `FOR UPDATE SKIP LOCKED` и устанавливает owner/lease; публикация идёт после commit. Durable direct exchange `dataflow.imports` маршрутизирует `import.job.created` в durable quorum queue `imports.process`. Сообщения persistent; `PublishedAt` выставляется только после publisher confirm и при ещё действующем claim. При сбое сохраняется только тип ошибки, счётчик попыток увеличивается, claim освобождается. Истёкший claim можно забрать повторно; доставка at-least-once допускает дубли.

Envelope имеет `contractVersion: "v1"`, `messageId` (outbox id), `type`, `occurredAt` (UTC), `attempt` (начиная с 1), `traceParent` и объект `payload` с `jobId`, `dataSourceId`, `schemaVersion`, `runVersion`. Версия контракта независима от версии схемы и версии запуска. Файлы и содержимое строк в RabbitMQ не передаются.

Consumer использует manual ack и prefetch 1. Неподдерживаемая версия контракта, уже существующий `ProcessedMessage`, отсутствующий job и устаревший `RunVersion` подтверждаются без эффектов. Worker читает CSV из S3 построчно по snapshot схемы; ack отправляется после commit терминального job, `ProcessedMessage` и итогового outbox. Retry-очереди и DLQ появятся на этапе 8.

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

Тесты API используют WebApplicationFactory и отдельный PostgreSQL 18 Testcontainer с миграциями; storage в upload-тестах заменён fake. Broker-тесты поднимают `rabbitmq:4.0.5-alpine`; сквозные тесты CSV дополнительно запускают MinIO с тем же закреплённым digest, что и Compose. Для полного набора нужен Docker Linux engine; зависимости из локального Compose для тестов не требуются.

### CSV parser (этап 7, пакет B)

`ICsvRowParser` / `CsvRowParser` читает переданный поток через async I/O и выдаёт
`IAsyncEnumerable<ParsedRow>` без накопления файла. Поток остаётся собственностью
вызывающего кода, в том числе при ранней остановке и отмене. CsvHelper 33.0.1
разбирает RFC4180 quoting, двойные кавычки и многострочные ячейки. Кодировка —
строго UTF-8, BOM допустим; delimiter берётся из snapshot схемы, без автоопределения.

Заголовок — первая запись: точные имена полей snapshot, порядок произвольный,
все поля snapshot должны присутствовать. Пустые/неизвестные/повторные имена
контролируемо отклоняют файл; повторы проверяются без учёта регистра.
Строки без символов или только с whitespace пропускаются как данные, но выдаются
с `IsEmpty=true` для продвижения checkpoint. Явные пустые ячейки (`,,,,`, `""`)
считаются данными и проходят валидацию. Комментарии не интерпретируются.

`RowNumber` — первая физическая строка записи, включая заголовок, пустые строки
и ошибочные записи. Для многострочной ячейки `CheckpointRowNumber` указывает на
последнюю физическую строку записи: checkpoint не должен попадать внутрь ячейки.
CRLF, LF и CR поддерживаются. Завершающий перевод строки не создаёт лишнюю запись.
`RawData` — JSON-массив всех исходных декодированных ячеек с сохранением пробелов;
лишние ячейки также сохраняются. Несовпадение числа колонок даёт ошибку строки
`column_count_mismatch`, последующие записи продолжают обрабатываться.

Лимиты настраиваются секцией `Import` (или env `Import__MaxRows` и т. д.):

| Параметр | По умолчанию | Допустимый диапазон |
|---|---:|---:|
| `MaxRows` | 50 000 | 1–500 000 записей данных, включая невалидные, без пустых строк |
| `MaxColumns` | 64 | 1–256 |
| `MaxCellLength` | 4 096 | 1–65 536 UTF-16 code units декодированной ячейки |
| `MaxZipEntries` | 128 | 1–10 000 записей ZIP-архива (только XLSX) |
| `MaxUncompressedBytes` | 100 МБ | 1 КиБ–512 МиБ суммарного распакованного объёма (только XLSX) |

Лимиты колонок/ячеек применяются и к заголовку. Дополнительно ограничен сырой
размер одной записи с учётом quoting: память парсера ограничена записью, а не
размером файла, даже для незакрытой кавычки или множества коротких колонок.
Ошибки лимитов (`row_limit_exceeded`, `column_limit_exceeded`,
`cell_limit_exceeded`, `csv_record_too_large`), кодировки (`invalid_csv_encoding`)
и quoting (`invalid_csv_syntax`) отклоняют файл через `ImportFileException` с
безопасным сообщением. Внутренние диагностики CsvHelper и значения ячеек в
исключения не включаются.

### XLSX parser (этап 9)

`IXlsxRowParser` / `XlsxRowParser` читает первый worksheet книги и выдаёт тот же
`IAsyncEnumerable<ParsedRow>`, что и CSV-парсер: валидация строк не зависит от
формата файла. Реализация — собственный потоковый разбор OOXML через
`ZipArchive` + `XmlReader` (async, `DtdProcessing.Prohibit`, `XmlResolver = null`,
запрет DTD/entities): формулы и внешние ссылки никогда не исполняются.

Гарантии безопасности XLSX:

- Лимит числа ZIP-записей (`MaxZipEntries`) и суммарного распакованного объёма
  (`MaxUncompressedBytes`). Объём проверяется и по заявленным размерам записей,
  и фактически при чтении каждого потока — zip-bomb с поддельными заголовками
  отклоняется.
- Формулы (`<f>`) не вычисляются: значение из кеша игнорируется, ячейка даёт
  ошибку строки `formula_cell_not_supported`. Формула или ошибка Excel в строке
  заголовка отклоняет файл целиком (`invalid_xlsx_header`).
- Ошибочные значения Excel (`t="e"`, например `#DIV/0!`, `#VALUE!`) и
  неподдерживаемые типы делают строку `Invalid` с `unsupported_cell_value`,
  обработка файла продолжается.
- Первая worksheet выбирается по `xl/workbook.xml` и её relationships; вложенные
  пути нормализуются (защита от `../` traversal).

Семантика значений согласована с CSV: shared/inline строки — текст,
`t="b"` → `true`/`false`, `t="d"` и числовые serial-даты со стилем даты →
`yyyy-MM-dd`, числа → invariant decimal (`G29`). Пустые физические строки
выдаются с `IsEmpty=true` и продвигают checkpoint; явные пустые ячейки являются
данными и проверяются на required. Заголовок — первая строка первого листа:
пустые/повторные (без учёта регистра)/неизвестные/отсутствующие имена отклоняют
файл кодами `empty_csv_header`, `duplicate_csv_header`, `unknown_csv_header`,
`missing_csv_header`. Лимиты `MaxRows`/`MaxColumns`/`MaxCellLength` применяются
так же, как в CSV; лишние колонки дают ошибку строки `column_count_mismatch`.

Эквивалентные CSV и XLSX дают одинаковые `NaturalKey`, `RowHash`, статусы строк
и ошибки — это закреплено интеграционными тестами этапа 9.

### CSV-отчёт ошибок и Dashboard

`GET /api/v1/import-jobs/{id}/errors.csv` отдаёт потоковый отчёт по невалидным
строкам (RFC 4180, `\r\n`, `rowNumber,field,code,message,rawData`). Значения
текстовых ячеек, начинающихся с `=`, `+`, `-`, `@`, `\t`, `\r` (в том числе
после ведущих пробелов), предваряются апострофом — защита от spreadsheet
formula injection. `rawData` — JSON-массив исходных ячеек с экранированием
каждого строкового элемента. Доступ только владельцу источника; неизвестное
задание — 404 `job_not_found`.

`GET /api/v1/dashboard/summary?period=today|week|month|all&recentLimit=1..50`
возвращает агрегаты по заданиям текущего пользователя. Определения показателей:

- `totalJobs` — задания, созданные за период (`CreatedAt`);
- `completedJobs` / `completedWithErrorsJobs` — терминальные статусы полного
  разбора файла (без ошибок строк / с ними);
- `failedJobs`, `cancelledJobs` — сбои обработки и отменённые задания;
- `attentionJobs` = `completedWithErrorsJobs + failedJobs`;
- `activeJobs` = `queuedJobs + processingJobs`, где `queuedJobs` — `Queued` и
  `RetryScheduled`, `processingJobs` — `Processing`;
- `completionSuccessRate` — доля полностью разобранных файлов среди завершённых
  и упавших: `(completed + completedWithErrors) / (completed + completedWithErrors + failed) * 100`,
  0, если таких нет;
- `processedRows` = `insertedRows + updatedRows + invalidRows + duplicateRows + validRows`;
- `validRows` — валидные строки универсальных пользовательских правил (Tabular);
- `acceptedRows` = `insertedRows + updatedRows + duplicateRows + validRows`;
- `recentJobs` — последние задания периода с именем источника, файлом, форматом
  (CSV/XLSX по расширению), статусом и счётчиками.

### Сквозной импорт CSV

Создайте приватный bucket в MinIO и задайте одинаковые настройки `Storage` для API и Worker
(`Endpoint`, `Bucket`, `AccessKey`, `SecretKey`, `Region`). Для загрузки используйте
`POST /api/v1/data-sources/{id}/files`, multipart с единственным файлом и обязательным
заголовком `Idempotency-Key`. Ответ `202` содержит `jobId` и Location `/api/v1/jobs/{jobId}`.

`GET /api/v1/jobs/{jobId}` возвращает статус и счётчики. `totalRows` равен `null` до EOF;
после EOF — числу записей данных, без пустых строк. Конечные статусы — `Completed`
или `CompletedWithErrors`, при отказе файла/инфраструктуры — `Failed`.

`GET /api/v1/import-jobs/{jobId}/rows?status=Invalid&page=1&pageSize=200`
возвращает `{items, page, pageSize, total}`. Статус необязателен: `Inserted`, `Updated`,
`Invalid`, `Duplicate`; pageSize — 1–200 (default 50), порядок — RowNumber ASC.
`total` здесь означает число уже сохранённых строк с учётом фильтра.
Invalid содержит `rawData` и ошибки `{field, code, message}`; неизвестный job даёт
404 `job_not_found`, неверные параметры — 400 `validation_failed` Problem Details.

Worker сохраняет по `Import:BatchSize` строк (250–500, default 500). Lease
`Import:LeaseSeconds` (15–600, default 60) продлевается в транзакции батча; owner,
version, run и срок проверяются по времени PostgreSQL с блокировкой job. Customer,
ImportRow, счётчики и физический checkpoint коммитятся вместе. В памяти остаются
текущий батч и HashSet ключей файла; EF tracker очищается после каждого батча.
Новый ключ создаёт Customer, изменённый hash обновляет его, одинаковый hash даёт
Duplicate; повтор ключа внутри файла — Invalid `duplicate_key_in_file` даже при иных значениях.

Сквозной fixture `tests/DataFlow.IntegrationTests/Fixtures/MixedCustomerCsv.cs`
генерирует 1 000 синтетических строк: 100 Inserted, 100 Updated, 100 Duplicate,
700 Invalid (required/type/length/email/date и повторы ключей). Перед ним импортируются
200 исходных клиентов; итог — 300 валидных Customer. `CsvEndToEndStage7Tests`
проверяет полный путь API → S3 → outbox → RabbitMQ → Worker → API при батчах 250 и 500,
результат каждой строки, повтор доставки и HTTP idempotency.

Ограничения: при отказе файла ранее закоммиченные батчи сохраняются: атомарность —
на уровне батча. Экспорт ошибок и Dashboard доступны, но их UI-интеграция в
`Ui/` ограничена страницей правил; полноценный операторский интерфейс — этап 10.

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
