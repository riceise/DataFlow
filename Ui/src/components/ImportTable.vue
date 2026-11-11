<script setup lang="ts">
import type { Job, Source } from '@/models'
import { date, number } from '@/models'
import LiveBadge from './LiveBadge.vue'
import AppIcon from './AppIcon.vue'
defineProps<{
  jobs: Pick<
    Job,
    | 'id'
    | 'dataSourceId'
    | 'originalFileName'
    | 'status'
    | 'createdAt'
    | 'processedRows'
    | 'totalRows'
    | 'invalidRows'
  >[]
  sources?: Pick<Source, 'id' | 'name'>[]
}>()
</script>
<template>
  <div class="table-scroll import-ledger">
    <table class="operator-table import-table">
      <thead>
        <tr>
          <th scope="col" class="import-file-column">Файл</th>
          <th scope="col">Источник</th>
          <th scope="col">Статус</th>
          <th scope="col" class="numeric">Обработано</th>
          <th scope="col" class="numeric">Ошибки</th>
          <th scope="col">Создан</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="job in jobs" :key="job.id">
          <td data-label="Файл" class="import-file-cell">
            <div class="import-file">
              <AppIcon name="file" :size="18" />
              <RouterLink :to="`/imports/${job.id}`" :title="job.originalFileName">{{
                job.originalFileName
              }}</RouterLink>
            </div>
          </td>
          <td data-label="Источник" class="import-source-cell">
            <RouterLink :to="`/sources/${job.dataSourceId}`">{{
              sources?.find((s) => s.id === job.dataSourceId)?.name ?? 'Источник'
            }}</RouterLink>
          </td>
          <td data-label="Статус" class="import-status-cell"><LiveBadge :status="job.status" /></td>
          <td data-label="Обработано" class="numeric import-processed-cell">
            {{ number(job.processedRows)
            }}<span v-if="job.totalRows !== null" class="muted">
              / {{ number(job.totalRows) }}</span
            >
          </td>
          <td data-label="Ошибки" class="numeric import-errors-cell">
            <RouterLink
              v-if="job.invalidRows"
              :to="`/imports/${job.id}?rows=Invalid`"
              class="import-error-count"
              :aria-label="`Ошибки строк: ${number(job.invalidRows)}, ${job.originalFileName}`"
              >{{ number(job.invalidRows) }}</RouterLink
            ><span v-else class="muted">0</span>
          </td>
          <td data-label="Создан" class="import-date-cell">
            <time :datetime="job.createdAt">{{ date(job.createdAt) }}</time>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
