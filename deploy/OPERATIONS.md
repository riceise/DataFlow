# Эксплуатация DataFlow

## Полный запуск

Из корня репозитория, Docker Linux engine и Docker Compose v2:

```powershell
Copy-Item .env.example .env # только если локального .env ещё нет
docker compose --env-file .env -f deploy/docker-compose.yml -f deploy/docker-compose.app.yml up -d --build --wait --wait-timeout 240
```

Зависимости запускаются с health checks. Одноразовый `migrate` применяет EF migrations
под PostgreSQL session advisory lock; `storage-init` создаёт bucket и убирает публичную
bucket policy. API/Worker запускаются после успешного завершения обоих заданий.
Ошибка миграции останавливает эту цепочку. Приложения сами миграции не применяют.

| Компонент | Адрес по умолчанию |
|---|---|
| Web (Vue + `/api` proxy) | http://127.0.0.1:8080 |
| API live / ready | http://127.0.0.1:5030/health/live, `/health/ready` |
| Worker live / ready | http://127.0.0.1:5031/health/live, `/health/ready` |
| Jaeger | http://127.0.0.1:16686 |
| Prometheus | http://127.0.0.1:9090 |
| RabbitMQ management | http://127.0.0.1:15672 |
| MinIO console | http://127.0.0.1:9001 |

Регистрация и вход доступны в Web. Nginx раздаёт SPA и проксирует `/api` в одном origin,
передаёт cookie/CSRF и потоковую загрузку/отчёты. Предел тела Nginx 52 МиБ включает
multipart overhead; лимит приложения остаётся `Storage:MaxFileBytes` (50 МиБ), XLSX —
дополнительно `Import:MaxFileBytes` (25 МиБ). При изменении лимитов согласуйте Nginx.
Ключи ASP.NET Data Protection сохраняются в `dataflow_api_keys`: cookie переживают
пересоздание API. Тома с данными не зависят от жизненного цикла контейнера.

```powershell
docker compose --env-file .env -f deploy/docker-compose.yml -f deploy/docker-compose.app.yml ps -a
docker compose --env-file .env -f deploy/docker-compose.yml -f deploy/docker-compose.app.yml logs --tail 100 api worker migrate storage-init
node tools/smoke-compose.mjs
docker compose --env-file .env -f deploy/docker-compose.yml -f deploy/docker-compose.app.yml down
```

`smoke-compose.mjs` регистрирует синтетического пользователя, создаёт источник, выполняет
успешный импорт с ошибкой строки и Failed-импорт, проверяет реальный trace в Jaeger и
метрики Prometheus. Адреса переопределяются `DATAFLOW_WEB_URL`,
`DATAFLOW_JAEGER_URL`, `DATAFLOW_PROMETHEUS_URL`. Проверка создаёт небольшие демо-данные.
`node tools/smoke-compose.mjs --recreate-api` дополнительно пересоздаёт API и проверяет
сохранение той же cookie и истории. Для этого варианта задайте `DATAFLOW_COMPOSE_ENV_FILE`
(default `.env`) и, если использовали `-p`, `DATAFLOW_COMPOSE_PROJECT`.

Для проверки пустых volumes используйте новый project name: добавьте `-p dataflow-clean`
во **все** команды. Порты должны быть свободны либо переопределены через env.
`down` сохраняет volumes; `down -v` удаляет данные только выбранного Compose project.

## Миграции и обновление

Остановите API/Worker перед обновлением несовместимой схемы, соберите образы,
запустите одноразовый migrator и снова поднимите приложение. Не запускайте migrator
на старом коде после применения более новых migrations. Для запуска вне Docker:

```powershell
# ConnectionStrings__DataFlow задаётся в окружении; .env автоматически не читается.
dotnet run --project tools/DataFlow.Migrator
```

Лимит migrator — 5 минут. Ошибки выводятся безопасным типом без connection string.
Если требуется подробная диагностика, используйте логи PostgreSQL и локальный EF CLI
с явно заданным подключением. Не изменяйте служебные таблицы migrations вручную.

## Health

Liveness проверяет только HTTP host: сбой БД/брокера/storage не превращает его в failed.
Readiness возвращает 503 при проблеме с `postgres`, `rabbitmq` или `storage`; для Worker
также проверяется активная подписка `consumer`. PostgreSQL check требует применённых
migrations. Проверки ограничены таймаутами. Ответ содержит только названия компонентов
и статусы, без endpoint, exception, credentials и connection strings.
Readiness API намеренно отражает готовность всего import path, включая broker.
Отказ Jaeger/collector не блокирует приём и обработку импорта.

## Логи и traces

Структурированные JSON-логи — stdout. `CorrelationId` относится к HTTP-запросу;
`TraceId` — W3C trace; `JobId` — постоянный ключ задания. Outbox сохраняет `traceparent`
и `tracestate` атомарно с сообщением. Publisher создаёт Producer span от сохранённого
контекста; consumer восстанавливает remote parent и создаёт Consumer span. Retry/outbox
сохраняет текущий контекст снова. Для исторических сообщений без контекста начинается
новый trace; по `JobId` события остаются связаны. Ручной retry имеет trace нового HTTP
запроса, `RunVersion` увеличивается.

В Jaeger выберите `DataFlow.Api`/`DataFlow.Worker`, tag `job.id=<JobId>`; откройте
`import upload`, `import publish`, `import process`, HTTP и PostgreSQL spans.
`job.status` и `error.type` показывают итог обработки, `messaging.delivery.attempt` —
попытку обработки. Не путайте её с числом попыток публикации outbox.
SQL text/parameters и содержимое импортируемых строк не экспортируются в DB spans.
Ячейки, файлы, пароли и cookies не добавляются в прикладные tags/log scopes.

OTLP включается, когда задан `OTEL_EXPORTER_OTLP_ENDPOINT`; стандартные
`OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_TRACES_SAMPLER`, `OTEL_TRACES_SAMPLER_ARG`,
`OTEL_METRIC_EXPORT_INTERVAL` применяются SDK. По умолчанию локальный Compose экспортирует
через collector в Jaeger/Prometheus. Collector ограничен 128 МиБ; Prometheus хранит
15 дней. Jaeger в этом локальном Compose использует память: traces исчезают после
перезапуска Jaeger.

## Метрики

Prometheus нормализует точки в `_` и добавляет `_total` counters:

| Инструмент | Значение |
|---|---|
| `dataflow.import.duration` | histogram секунд одной обработки после acquire lease, включая heartbeat/finalization |
| `dataflow.import.rows` | counter только успешно закоммиченных строк, label `outcome` |
| `dataflow.import.retries` | counter автоматических retry, записанных в БД |
| `dataflow.message.redeliveries` | counter deliveries с RabbitMQ redelivered flag |
| `dataflow.dependency.errors` | counter ошибок publish/import IO, label `dependency` |
| `dataflow.jobs` | snapshot количества заданий по `status` |
| `dataflow.outbox.pending` | количество неопубликованных сообщений |
| `dataflow.outbox.oldest_age` | возраст старейшего pending, секунд; 0 при пустом outbox |
| `dataflow.dlq.ready` | реальное ready-число сообщений `imports.dead`, passive AMQP declare |
| `dataflow.metrics.db.last_success`, `.broker.last_success` | Unix seconds последнего успешного снимка |
| `dataflow.cleanup.deleted` | counter удалённых файлов/объектов по `kind` |

Snapshots обновляются раз в 15 секунд; при сбое сохраняется прошлое значение, поэтому
контролируйте freshness `last_success`. DLQ gauge не включает unacked сообщения —
при ручном разборе сверяйте management UI. Глобальные DB/DLQ snapshots от нескольких
Worker не суммируйте: используйте `max`, разбивку по статусу сохраняйте.
`JobId`, user id, object key и исходные ошибки не используются как labels метрик.

Примеры PromQL:

```promql
sum(rate(dataflow_import_rows_total[5m]))
histogram_quantile(0.95, sum by (le) (rate(dataflow_import_duration_seconds_bucket[5m])))
max(dataflow_outbox_oldest_age_seconds)
max(dataflow_dlq_ready)
time() - max(dataflow_metrics_broker_last_success_seconds)
```

Для предупреждений: DLQ > 0, растущий outbox age > 60 секунд, отсутствие свежего
снимка > 60 секунд, длительный RetryScheduled/Processing. Порог длительности выбирайте
по рабочим файлам; одна обработка ограничена 20 минутами.

## Failed, DLQ и восстановление

1. Найдите job в Web и запишите `JobId`, статус, safe error code и counters/checkpoint.
2. По `JobId` найдите API/Worker логи и trace. Проверьте readiness и доступность
   PostgreSQL, приватного bucket и RabbitMQ, свободное место и outbox age.
3. Для ошибки файла исправьте исходный CSV/XLSX и загрузите новый файл с новым
   Idempotency-Key. Уже совпадающие Customer становятся Duplicate, изменённые — Updated.
4. Для временного сбоя восстановите зависимость. Автоматические retry — 5/30/300 секунд,
   затем Failed retryable и outbox в `imports.dead`. После исправления выполните
   «Продолжить обработку» в Web или `POST /api/v1/import-jobs/{id}/retry` с cookie/CSRF.
   Resume сохраняет checkpoint, увеличивает RunVersion и публикует новый outbox.
5. В RabbitMQ management откройте `imports.dead`, получите одно сообщение в режиме
   **requeue** для просмотра JobId/RunVersion/Attempt. Не делайте bulk purge и не
   перепубликовывайте старый envelope. После проверки результата нового запуска
   подтвердите только соответствующее старое DLQ-сообщение через management UI.
6. При падении Worker запустите его снова. Незавершённая доставка остаётся unacked;
   lease/inbox/fencing и reconciliation восстанавливают обработку. Не сбрасывайте lease,
   статусы, outbox или ProcessedMessages ручными UPDATE/DELETE.

`Queued` при остановленном Worker не теряется. При остановленном брокере outbox остаётся
pending и публикуется после восстановления. Retry TTL transfer в RabbitMQ 4.0.5 может
задерживаться около 180 секунд при недоступной целевой очереди; reconciliation допускает
безопасные дубли и восстанавливает overdue задания. Healthcheck RabbitMQ идемпотентно
устанавливает quorum policy `dataflow-retry` (аналог `rabbitmq-definitions.json`) после
создания bootstrap пользователя; это также работает с custom vhost. Worker получает 35 секунд Docker stop grace при host timeout
30 секунд; незавершённый батч откатывается.

## Retention и cleanup

Настройки `Storage` валидируются на старте:

| Параметр | Default | Назначение |
|---|---:|---|
| `CleanupIntervalSeconds` | 300 | период фоновой очистки |
| `CleanupBatchSize` | 100 | предел страницы объектов / удалённых temp файлов |
| `OrphanAgeSeconds` | 86400 | grace period S3 объектов |
| `TempGraceSeconds` | 86400 | grace period temp-файлов |
| `CompletedFileRetentionDays` | 0 | хранить исходники бессрочно; >0 включает retention |

В Compose срок задаётся `COMPLETED_FILE_RETENTION_DAYS`. При включённом retention
удаляются только исходники терминальных невозобновляемых заданий старше срока
(Completed/CompletedWithErrors/Cancelled/неретраебельный Failed). Активные и retryable
Failed сохраняются независимо от возраста. История, ошибки строк, checkpoints,
идемпотентные ответы, inbox/outbox и Customer сохраняются.

S3 sweep листает только `sources/` ограниченными страницами с continuation. Удаляется
только ключ ожидаемой формы, старше grace period; перед удалением ссылки в БД проверяются
повторно, задания блокируются `FOR UPDATE`. API upload и sweep используют один
PostgreSQL advisory lock по object key, защищая длительный upload/commit от гонки.
Отсутствие записи компенсации после crash не мешает обнаружить orphan через S3 listing.
Неуверенная ошибка БД приводит к пропуску удаления, а не к предположению об отсутствии job.
Повторное удаление отсутствующего S3 объекта идемпотентно.

Temp cleanup удаляет только случайные 64-hex файлы приложения старше grace period.
API держит эксклюзивный `.lock` sidecar до окончания upload/dispatch/компенсации;
активные файлы сохраняются. Локальные temp directories API и Worker раздельны.
Если sweep не успевает за ростом bucket, увеличьте batch/частоту по метрикам и IO.
