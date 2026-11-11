<script setup lang="ts">
import type { Job, Source } from '@/models'
import { date, number } from '@/models'
import LiveBadge from './LiveBadge.vue'
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
  <div class="table-scroll">
    <table class="operator-table">
      <thead>
        <tr>
          <th>Файл</th>
          <th>Источник</th>
          <th>Статус</th>
          <th>Обработано</th>
          <th>Ошибки</th>
          <th>Создан</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="job in jobs" :key="job.id">
          <td>
            <RouterLink :to="`/imports/${job.id}`">{{ job.originalFileName }}</RouterLink
            ><small>{{ job.id }}</small>
          </td>
          <td>
            <RouterLink :to="`/sources/${job.dataSourceId}`">{{
              sources?.find((s) => s.id === job.dataSourceId)?.name ?? job.dataSourceId.slice(0, 8)
            }}</RouterLink>
          </td>
          <td><LiveBadge :status="job.status" /></td>
          <td>
            {{ number(job.processedRows)
            }}{{ job.totalRows === null ? '' : ` / ${number(job.totalRows)}` }}
          </td>
          <td>{{ number(job.invalidRows) }}</td>
          <td>{{ date(job.createdAt) }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
