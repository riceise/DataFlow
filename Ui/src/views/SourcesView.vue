<script setup lang="ts">
import { computed, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { http } from '@/api'
import type { Page, Source } from '@/models'
import { date } from '@/models'
import { useResource } from '@/composables/useResource'
import ApiState from '@/components/ApiState.vue'
import PaginationBar from '@/components/PaginationBar.vue'
import PageHeader from '@/components/PageHeader.vue'
import AppIcon from '@/components/AppIcon.vue'
const route = useRoute(),
  router = useRouter()
const page = computed(() => Math.max(1, Number(route.query.page) || 1)),
  pageSize = computed(() =>
    [25, 50, 100].includes(Number(route.query.pageSize)) ? Number(route.query.pageSize) : 25,
  )
function query(values: Record<string, number>) {
  void router.replace({ query: { ...route.query, ...values } })
}
const { data, loading, error, refresh } = useResource((signal) =>
  http
    .get<Page<Source>>('/data-sources', {
      signal,
      params: { page: page.value, pageSize: pageSize.value },
    })
    .then((r) => r.data),
)
watch(() => route.query, refresh, { immediate: true })
</script>
<template>
  <PageHeader title="Источники" description="Правила приёма и проверки данных.">
    <RouterLink to="/sources/new" class="button button-primary"
      ><AppIcon name="plus" :size="18" />Создать источник</RouterLink
    >
  </PageHeader>
  <ApiState :error="error" :loading="loading && !data" :empty="data?.total === 0" @retry="refresh"
    ><RouterLink to="/sources/new">Создать первый источник</RouterLink></ApiState
  >
  <section v-if="data && data.total > 0" class="panel sources-section">
    <div class="table-scroll">
      <table class="operator-table sources-table">
        <thead>
          <tr>
            <th>Название</th>
            <th>Тип</th>
            <th>Форматы</th>
            <th>Правила</th>
            <th>Состояние</th>
            <th>Изменён</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="source in data.items" :key="source.id">
            <td data-label="Название" class="source-name-cell">
              <RouterLink :to="`/sources/${source.id}`">{{ source.name }}</RouterLink>
            </td>
            <td data-label="Тип">
              {{ source.dataType === 'Customer' ? 'Клиенты' : 'Табличные данные' }}
            </td>
            <td data-label="Форматы">{{ source.acceptedFormats.join(', ').toUpperCase() }}</td>
            <td data-label="Правила">
              {{
                source.defaultRuleSetVersionId
                  ? 'Версия из библиотеки'
                  : `Схема v${source.schemaVersion}`
              }}
            </td>
            <td data-label="Состояние">
              <span class="live-badge" :class="{ good: source.isActive }">{{
                source.isActive ? 'Активен' : 'Неактивен'
              }}</span>
            </td>
            <td data-label="Изменён" class="import-date-cell">{{ date(source.updatedAt) }}</td>
          </tr>
        </tbody>
      </table>
    </div>
    <PaginationBar
      :page="page"
      :page-size="pageSize"
      :total="data.total"
      @change="query({ page: $event })"
      @size="query({ pageSize: $event, page: 1 })"
    />
  </section>
</template>
