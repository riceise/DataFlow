<script setup lang="ts">
import { ref, watch } from 'vue'
import { http } from '@/api'
import { type Summary, active, number } from '@/models'
import { useResource } from '@/composables/useResource'
import ApiState from '@/components/ApiState.vue'
import ImportTable from '@/components/ImportTable.vue'
const period = ref('week')
const { data, error, loading, refresh } = useResource(
  (signal) =>
    http
      .get<Summary>('/dashboard/summary', { params: { period: period.value }, signal })
      .then((r) => r.data),
  (s) => s.activeJobs > 0 || s.recentJobs.some((j) => active(j.status)),
)
watch(period, refresh, { immediate: true })
</script>
<template>
  <div class="page-heading">
    <div>
      <h1>Обзор</h1>
      <p>Всё, что происходит с вашими данными.</p>
    </div>
    <div class="page-actions">
      <label
        >Период (UTC)<select v-model="period" class="form-control">
          <option value="today">Сегодня</option>
          <option value="week">Последние 7 дней</option>
          <option value="month">Последние 30 дней</option>
          <option value="all">Всё время</option>
        </select></label
      ><RouterLink to="/imports/new" class="button button-primary">Загрузить файл</RouterLink>
    </div>
  </div>
  <ApiState :error="error" :loading="loading && !data" :stale="!!data" @retry="refresh" />
  <template v-if="data"
    ><section class="metric-strip">
      <div
        v-for="metric in [
          { label: 'Всего заданий', value: data.totalJobs },
          { label: 'Завершено без ошибок', value: data.completedJobs },
          { label: 'Требуют внимания', value: data.attentionJobs },
          { label: 'В работе', value: data.activeJobs },
        ]"
        :key="metric.label"
        class="metric"
      >
        <span>{{ metric.label }}</span
        ><strong>{{ number(metric.value) }}</strong>
      </div>
    </section>
    <section class="panel operator-panel">
      <h2>Результаты обработки</h2>
      <div class="results-total">
        <strong>{{ number(data.processedRows) }}</strong
        ><span>строк обработано</span>
      </div>
      <div class="counter-grid">
        <div
          v-for="result in [
            { label: 'Добавлено', value: data.insertedRows },
            { label: 'Обновлено', value: data.updatedRows },
            { label: 'С ошибками', value: data.invalidRows },
            { label: 'Без изменений', value: data.duplicateRows },
            { label: 'Валидно (Tabular)', value: data.validRows },
          ]"
          :key="result.label"
        >
          <span>{{ result.label }}</span
          ><strong>{{ number(result.value) }}</strong>
        </div>
      </div>
    </section>
    <section class="panel operator-panel">
      <div class="page-heading">
        <h2>Последние импорты</h2>
        <RouterLink to="/imports" class="text-button">Все импорты →</RouterLink>
      </div>
      <ImportTable
        v-if="data.recentJobs.length"
        :jobs="data.recentJobs"
        :sources="data.recentJobs.map((j) => ({ id: j.dataSourceId, name: j.dataSourceName }))"
      />
      <div v-else class="empty-state">
        <h3>Подготовьте первый импорт</h3>
        <p>Создайте источник и загрузите CSV или XLSX.</p>
        <RouterLink to="/sources/new" class="button button-primary">Создать источник</RouterLink>
      </div>
    </section></template
  >
</template>
