export type JobStatus = 'processing' | 'completed' | 'errors' | 'retry' | 'failed' | 'queued'

export interface ImportJob {
  id: string
  file: string
  format: 'CSV' | 'XLSX'
  source: string
  date: string
  status: JobStatus
  inserted: number
  updated: number
  invalid: number
  duplicate: number
  version: number
}

export const statusLabels: Record<JobStatus, string> = {
  processing: 'В обработке',
  completed: 'Завершён',
  errors: 'Ошибки строк',
  retry: 'Ожидает повтора',
  failed: 'Сбой обработки',
  queued: 'В очереди',
}

export const sources = [
  {
    name: 'Клиенты · CRM',
    description: 'Основная клиентская база',
    formats: 'CSV, XLSX',
    version: 3,
    active: true,
  },
  {
    name: 'Партнёры',
    description: 'Клиенты партнёрской сети',
    formats: 'CSV, XLSX',
    version: 2,
    active: true,
  },
  {
    name: 'Интернет-магазин',
    description: 'Покупатели интернет-магазина',
    formats: 'CSV',
    version: 1,
    active: true,
  },
]

// Only synthetic fixtures. No API requests or live processing in this visual prototype.
export const jobs: ImportJob[] = [
  {
    id: 'DF-8A24F1',
    file: 'clients_october_26.csv',
    format: 'CSV',
    source: 'Клиенты · CRM',
    date: '2026-10-26T14:32:00',
    status: 'processing',
    inserted: 10800,
    updated: 1200,
    invalid: 150,
    duplicate: 300,
    version: 3,
  },
  {
    id: 'DF-7B19E2',
    file: 'partners_update.xlsx',
    format: 'XLSX',
    source: 'Партнёры',
    date: '2026-10-26T13:48:00',
    status: 'errors',
    inserted: 720,
    updated: 180,
    invalid: 60,
    duplicate: 40,
    version: 2,
  },
  {
    id: 'DF-6C08D3',
    file: 'customers_2026-10-26.csv',
    format: 'CSV',
    source: 'Интернет-магазин',
    date: '2026-10-26T12:15:00',
    status: 'completed',
    inserted: 2340,
    updated: 120,
    invalid: 0,
    duplicate: 40,
    version: 1,
  },
  {
    id: 'DF-5D97C4',
    file: 'crm_clients_export.xlsx',
    format: 'XLSX',
    source: 'Клиенты · CRM',
    date: '2026-10-26T11:06:00',
    status: 'completed',
    inserted: 1820,
    updated: 260,
    invalid: 0,
    duplicate: 120,
    version: 3,
  },
  {
    id: 'DF-4E86B5',
    file: 'partners_batch_025.csv',
    format: 'CSV',
    source: 'Партнёры',
    date: '2026-10-25T17:42:00',
    status: 'retry',
    inserted: 750,
    updated: 100,
    invalid: 0,
    duplicate: 150,
    version: 2,
  },
  {
    id: 'DF-3F75A6',
    file: 'store_customers_25.csv',
    format: 'CSV',
    source: 'Интернет-магазин',
    date: '2026-10-25T16:30:00',
    status: 'completed',
    inserted: 1450,
    updated: 420,
    invalid: 0,
    duplicate: 130,
    version: 1,
  },
  {
    id: 'DF-2A64F7',
    file: 'clients_archive.xlsx',
    format: 'XLSX',
    source: 'Клиенты · CRM',
    date: '2026-10-25T15:18:00',
    status: 'failed',
    inserted: 450,
    updated: 50,
    invalid: 0,
    duplicate: 0,
    version: 3,
  },
  {
    id: 'DF-1B53E8',
    file: 'partners_new_clients.csv',
    format: 'CSV',
    source: 'Партнёры',
    date: '2026-10-25T14:05:00',
    status: 'queued',
    inserted: 0,
    updated: 0,
    invalid: 0,
    duplicate: 0,
    version: 2,
  },
  ...Array.from({ length: 16 }, (_, index): ImportJob => ({
    id: `DF-H${String(index + 1).padStart(5, '0')}`,
    file: `clients_2026-10-${24 - Math.floor(index / 3)}_${index + 1}.csv`,
    format: 'CSV',
    source: sources[index % sources.length]!.name,
    date: `2026-10-${24 - Math.floor(index / 3)}T${String(16 - (index % 3) * 2).padStart(2, '0')}:20:00`,
    status: 'completed',
    inserted: 1800 + index * 87,
    updated: 120 + index * 13,
    invalid: 0,
    duplicate: 40 + index * 3,
    version: sources[index % sources.length]!.version,
  })),
]

export const processed = (job: ImportJob) =>
  job.inserted + job.updated + job.invalid + job.duplicate
export const number = (value: number) => new Intl.NumberFormat('ru-RU').format(value)
export const jobDate = (date: string) =>
  new Intl.DateTimeFormat('ru-RU', {
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
  })
    .format(new Date(date))
    .replace(' г.', '')
