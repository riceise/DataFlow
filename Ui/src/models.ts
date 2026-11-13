export interface Page<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}
import type { components } from '@/api-schema'
type Dto = components['schemas']
export type Field = Omit<Dto['DataSourceFieldDto'], 'maxLength' | 'format'> & {
  maxLength?: number | null
  format?: string | null
}
export interface Schema {
  version?: number
  keyFields: string[]
  csvDelimiter: string
  fields: Field[]
}
export type Source = Dto['DataSourceDto']
export type Job = Dto['JobDto']
export type Row = Dto['JobRowDto']
export type Summary = Dto['DashboardSummaryDto']
export const statuses: Record<string, string> = {
  Queued: 'В очереди',
  Processing: 'В обработке',
  RetryScheduled: 'Ожидает повтора',
  Completed: 'Завершён',
  CompletedWithErrors: 'Завершён с ошибками строк',
  Failed: 'Сбой обработки',
  Cancelled: 'Отменён',
  Inserted: 'Добавлено',
  Updated: 'Обновлено',
  Invalid: 'С ошибкой',
  Duplicate: 'Без изменений',
  Valid: 'Валидно',
}
export const active = (status: string) =>
  ['Queued', 'Processing', 'RetryScheduled'].includes(status)
export const errorMessages: Record<string, string> = {
  source_not_found: 'Источник данных не найден.',
  source_inactive: 'Источник данных деактивирован. Активируйте его перед загрузкой.',
  job_not_found: 'Импорт не найден.',
  job_not_retryable: 'Этот импорт нельзя перезапустить.',
  job_not_cancellable: 'Этот импорт уже нельзя отменить.',
  idempotency_conflict: 'Ключ запроса уже использовался для другого файла или правил.',
  upload_incomplete: 'Предыдущая загрузка с этим ключом ещё не завершена.',
  invalid_idempotency_key: 'Некорректный заголовок Idempotency-Key.',
  invalid_multipart: 'Не удалось прочитать переданный файл.',
  unexpected_form_field: 'Передайте ровно один файл.',
  invalid_file_name: 'Недопустимое имя файла.',
  file_format_not_allowed: 'Формат файла не разрешён настройками источника.',
  file_too_large: 'Файл превышает допустимый размер.',
  empty_file: 'Файл пуст.',
  invalid_file_format: 'Содержимое файла не соответствует формату CSV (UTF-8) или XLSX.',
  rule_version_not_found: 'Выбранная версия правил не найдена или недоступна.',
  rule_set_archived: 'Набор правил находится в архиве.',
  rule_type_mismatch: 'Тип данных набора правил не совпадает с типом источника.',
  invalid_rule_definition: 'Определение правил некорректно.',
  revision_conflict: 'Набор правил был изменён параллельно. Обновите страницу.',
  share_recipient_not_found: 'Пользователь с указанным email не найден.',
  invalid_schema: 'Схема источника данных некорректна.',
  invalid_antiforgery_token: 'Сессия защиты формы устарела. Обновите страницу и повторите действие.',
}
export const jobErrorMessages: Record<string, string> = {
  empty_file: 'В файле отсутствует строка заголовков.',
  empty_csv_header: 'В строке заголовков есть пустое имя колонки.',
  duplicate_csv_header: 'В строке заголовков повторяются имена колонок.',
  unknown_csv_header: 'В строке заголовков есть колонка, не описанная в схеме или правилах.',
  missing_csv_header: 'В строке заголовков отсутствуют обязательные колонки схемы.',
  invalid_csv_delimiter: 'Неподдерживаемый разделитель CSV в схеме.',
  invalid_csv_encoding: 'Файл CSV должен быть в кодировке UTF-8.',
  invalid_csv_syntax: 'Нарушен синтаксис CSV или кавычек (RFC 4180).',
  csv_record_too_large: 'Превышен допустимый размер одной записи CSV.',
  invalid_xlsx_archive: 'Файл XLSX повреждён или не является корректным архивом книги.',
  invalid_xlsx_syntax: 'Структура XML книги XLSX повреждена.',
  invalid_xlsx_header: 'В строке заголовков XLSX обнаружена формула или ошибка Excel.',
  xlsx_zip_entry_limit_exceeded: 'Превышено допустимое число элементов внутри архива XLSX.',
  xlsx_uncompressed_limit_exceeded: 'Превышен допустимый объём распакованных данных XLSX.',
  row_limit_exceeded: 'Превышено максимальное число строк данных.',
  column_limit_exceeded: 'Превышено максимальное число колонок.',
  cell_limit_exceeded: 'Превышена максимальная длина значения ячейки.',
  file_too_large: 'Размер файла превышает допустимый лимит обработки.',
  unsupported_file_type: 'Поддерживаются только файлы CSV и XLSX.',
  rule_execution_limit_exceeded: 'Превышено время проверки регулярного выражения в правилах.',
  invalid_schema_snapshot: 'Снимок схемы или правил не поддерживается обработчиком.',
  import_deadline_exceeded: 'Превышено допустимое время обработки файла.',
  import_retry_exhausted: 'Исчерпан лимит автоматических попыток из-за инфраструктурного сбоя.',
  import_infrastructure_error: 'Временный инфраструктурный сбой при обработке файла.',
}
export const number = (value: number) => new Intl.NumberFormat('ru-RU').format(value)
export const date = (value?: string | null) =>
  value
    ? new Intl.DateTimeFormat('ru-RU', {
        day: 'numeric',
        month: 'short',
        year: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
      }).format(new Date(value))
    : '—'
