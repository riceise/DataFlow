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
export const number = (value: number) => new Intl.NumberFormat('ru-RU').format(value)
export const date = (value?: string | null) =>
  value ? new Date(value).toLocaleString('ru-RU') : '—'
