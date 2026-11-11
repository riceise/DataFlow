<script setup lang="ts">
import { ref, watch } from 'vue'
import { http } from '@/api'
import { type Summary, active, number } from '@/models'
import { useResource } from '@/composables/useResource'
import ApiState from '@/components/ApiState.vue'
import ImportTable from '@/components/ImportTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import DashboardMetrics from '@/components/DashboardMetrics.vue'
import ResultBreakdown from '@/components/ResultBreakdown.vue'
import AppIcon from '@/components/AppIcon.vue'
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
  <PageHeader title="Обзор" description="Состояние импортов и результаты обработки.">
    <label
      >Период (UTC)<select v-model="period" class="form-control">
        <option value="today">Сегодня</option>
        <option value="week">Последние 7 дней</option>
        <option value="month">Последние 30 дней</option>
        <option value="all">Всё время</option>
      </select></label
    ><RouterLink to="/imports/new" class="button button-primary"
      ><AppIcon name="upload" :size="18" />Загрузить файл</RouterLink
    >
  </PageHeader>
  <ApiState :error="error" :loading="loading && !data" :stale="!!data" @retry="refresh" />
  <template v-if="data"
    ><DashboardMetrics :summary="data" />
    <section class="panel dashboard-results">
      <div class="section-heading">
        <h2>Результаты обработки</h2>
        <span class="section-note">За выбранный период</span>
      </div>
      <div class="results-total">
        <strong>{{ number(data.processedRows) }}</strong
        ><span>строк обработано</span>
      </div>
      <ResultBreakdown
        :inserted="data.insertedRows"
        :updated="data.updatedRows"
        :invalid="data.invalidRows"
        :duplicate="data.duplicateRows"
        :valid="data.validRows"
      />
      <p v-if="!data.processedRows" class="section-note results-empty">
        За этот период обработанных строк пока нет.
      </p>
    </section>
    <section class="panel recent-section">
      <div class="recent-header">
        <div>
          <h2>Последние импорты</h2>
          <p>Последние загрузки за выбранный период</p>
        </div>
        <RouterLink to="/imports" class="text-button"
          >Все импорты <AppIcon name="arrow" :size="16"
        /></RouterLink>
      </div>
      <div v-if="data.failedJobs" class="dashboard-notice">
        <AppIcon name="alert" :size="16" />
        <p>
          Есть сбои обработки: {{ number(data.failedJobs) }}. Часть результатов могла сохраниться.
        </p>
        <RouterLink to="/imports?status=Failed">Посмотреть</RouterLink>
      </div>
      <ImportTable
        v-if="data.recentJobs.length"
        :jobs="data.recentJobs"
        :sources="data.recentJobs.map((j) => ({ id: j.dataSourceId, name: j.dataSourceName }))"
      />
      <div v-else class="empty-state">
        <h3>{{ data.totalJobs ? 'В этом периоде импортов нет' : 'Начните с источника данных' }}</h3>
        <p>Выберите или создайте источник, затем загрузите CSV или XLSX.</p>
        <RouterLink to="/sources" class="button button-secondary">К источникам</RouterLink>
      </div>
    </section></template
  >
</template>
