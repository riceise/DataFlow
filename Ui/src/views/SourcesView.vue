<script setup lang="ts">
import { computed, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { http } from '@/api'
import type { Page, Source } from '@/models'
import { date } from '@/models'
import { useResource } from '@/composables/useResource'
import ApiState from '@/components/ApiState.vue'
import PaginationBar from '@/components/PaginationBar.vue'
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
  <div class="page-heading">
    <div>
      <h1>Источники</h1>
      <p>Правила приёма и проверки данных.</p>
    </div>
    <RouterLink to="/sources/new" class="button button-primary">Создать источник</RouterLink>
  </div>
  <ApiState :error="error" :loading="loading && !data" :empty="data?.total === 0" @retry="refresh"
    ><RouterLink to="/sources/new">Создать первый источник</RouterLink></ApiState
  >
  <section v-if="data" class="panel">
    <div class="table-scroll">
      <table class="operator-table">
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
            <td>
              <RouterLink :to="`/sources/${source.id}`">{{ source.name }}</RouterLink>
            </td>
            <td>{{ source.dataType === 'Customer' ? 'Клиенты' : 'Табличные данные' }}</td>
            <td>{{ source.acceptedFormats.join(', ') }}</td>
            <td>
              {{
                source.defaultRuleSetVersionId
                  ? 'Версия из библиотеки'
                  : `Схема v${source.schemaVersion}`
              }}
            </td>
            <td>{{ source.isActive ? 'Активен' : 'Неактивен' }}</td>
            <td>{{ date(source.updatedAt) }}</td>
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
