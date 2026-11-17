# Публичные samples для демо

Синтетические файлы без реальных персональных данных. Генерируются детерминированно
командой (результат стабилен между запусками):

```powershell
dotnet run --project tools/DataFlow.SampleGenerator
```

| Файл | Содержимое | Ожидаемый результат импорта |
|---|---|---|
| `customers-valid.csv` / `.xlsx` | 8 валидных строк (`ext-100`…`ext-107`) | `Completed`, добавлено 8 |
| `customers-with-errors.csv` / `.xlsx` | 12 строк: 5 валидных, 7 с воспроизводимыми ошибками | `CompletedWithErrors`: добавлено 5, с ошибками 7 |
| `customers-with-errors-fixed.csv` | Исправление файла с ошибками | `Completed`: добавлено 7, обновлено 1, без изменений 4 |

Схема источника для всех файлов (ключ — `externalId`):

```json
{
  "keyFields": ["externalId"],
  "csvDelimiter": ",",
  "fields": [
    {"name": "externalId", "type": "string", "required": true, "maxLength": 80},
    {"name": "name", "type": "string", "required": true, "maxLength": 40},
    {"name": "email", "type": "email", "required": true, "maxLength": 254},
    {"name": "registeredAt", "type": "date", "required": false, "format": "yyyy-MM-dd"},
    {"name": "isActive", "type": "boolean", "required": true}
  ]
}
```

CSV — UTF-8 без BOM, разделитель «запятая», даты `yyyy-MM-dd`, булевы `true`/`false`.
XLSX-файлы содержат те же строки и дают тот же результат импорта (одинаковые
`NaturalKey`, `RowHash`, статусы и ошибки — закреплено интеграционными тестами).

## Воспроизводимые ошибки в `customers-with-errors.csv`

Физическая строка 1 — заголовок; номера ниже — физические строки файла,
как они показываются в интерфейсе и CSV-отчёте ошибок.

| Строка | Ключ | Ошибка | Код |
|---|---|---|---|
| 6 | `ext-005` | пустое имя | `required` (поле `name`) |
| 7 | `ext-006` | пустой email | `required` (поле `email`) |
| 8 | `ext-007` | email без `@` и домена | `invalid_email` |
| 9 | `ext-008` | дата `15.03.2026` вместо `yyyy-MM-dd` | `invalid_date` |
| 10 | `ext-009` | `isActive: maybe` | `invalid_type` |
| 11 | `ext-010` | имя длиннее 40 символов | `max_length` |
| 12 | `ext-001` | повтор ключа первой строки | `duplicate_key_in_file` (поле `$row`) |

Строка 13 (`ext-012`) валидна, но без необязательной даты — в исправленном
файле дата добавлена, поэтому при повторной загрузке строка становится
`Обновлено` (тот же натуральный ключ, другой `RowHash`).

## Как провести демо

1. Создайте источник со схемой выше (через интерфейс или API).
2. Загрузите `customers-with-errors.csv` — статус `Завершён с ошибками строк`,
   счётчики: добавлено 5, с ошибками 7.
3. Откройте ошибочные строки (фильтр «С ошибкой») и скачайте `errors.csv` —
   отчёт содержит все 7 строк с номерами, полями, кодами и исходными данными.
4. Загрузите `customers-with-errors-fixed.csv` — статус `Завершён`,
   счётчики: добавлено 7, обновлено 1, без изменений 4.
   Валидные строки исходного файла не меняются («без изменений»), исправленные
   вставляются, дополненная дата `ext-012` обновляет запись.
5. Загрузите `customers-valid.csv` в тот же источник — 8 новых клиентов.
6. Повтор того же HTTP-запроса с тем же `Idempotency-Key` и файлом возвращает
   то же задание и не создаёт дубликатов.

### Через API (curl)

```bash
# 1. Аутентификация (cookie + CSRF)
curl -c cookies.txt -X POST https://localhost:5030/api/v1/auth/register \
  -H "Content-Type: application/json" \
  -d '{"email":"demo@example.test","password":"Demo-2026!pass"}'
curl -c cookies.txt -X POST https://localhost:5030/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"demo@example.test","password":"Demo-2026!pass"}'
CSRF=$(curl -b cookies.txt -c cookies.txt https://localhost:5030/api/v1/auth/antiforgery | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')

# 2. Источник (сохраните id из ответа)
curl -b cookies.txt -X POST https://localhost:5030/api/v1/data-sources \
  -H "Content-Type: application/json" -H "X-CSRF-TOKEN: $CSRF" \
  -d '{"name":"Демо клиенты","acceptedFormats":["Csv","Xlsx"],"schema":{…выше…}}'

# 3. Загрузка файла
curl -b cookies.txt -X POST https://localhost:5030/api/v1/data-sources/$SOURCE_ID/files \
  -H "Idempotency-Key: $(uuidgen)" -H "X-CSRF-TOKEN: $CSRF" \
  -F "file=@samples/customers-with-errors.csv"   # → 202 Accepted + jobId

# 4. Прогресс и результаты
curl -b cookies.txt https://localhost:5030/api/v1/jobs/$JOB_ID
curl -b cookies.txt "https://localhost:5030/api/v1/import-jobs/$JOB_ID/rows?status=Invalid&pageSize=200"
curl -b cookies.txt -o errors.csv https://localhost:5030/api/v1/import-jobs/$JOB_ID/errors.csv
```

Сценарий закреплён сквозными тестами `AcceptanceStage12Tests` (реальные
PostgreSQL/RabbitMQ/MinIO через Testcontainers) и Playwright-сценарием
основного пути (макет в CI, live-вариант `operator.live.ts` против API+Worker).
