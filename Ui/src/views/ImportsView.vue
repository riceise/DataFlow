<script setup lang="ts">
import { ref, watch, computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { http } from '@/api'
import { type Job, type Page, type Source, statuses, active } from '@/models'
import { useResource } from '@/composables/useResource'
import ApiState from '@/components/ApiState.vue'
import ImportTable from '@/components/ImportTable.vue'
import PaginationBar from '@/components/PaginationBar.vue'
const route = useRoute(),
  router = useRouter(),
  sources = ref<Source[]>([])
const page = computed(() => Math.max(1, Number(route.query.page) || 1)),
  pageSize = computed(() =>
    [25, 50, 100].includes(Number(route.query.pageSize)) ? Number(route.query.pageSize) : 25,
  )
function query(values: Record<string, string | number | undefined>) {
  void router.replace({ query: { ...route.query, ...values } })
}
const { data, error, loading, refresh } = useResource(
  (signal) =>
    http
      .get<Page<Job>>('/import-jobs', {
        params: {
          page: page.value,
          pageSize: pageSize.value,
          status: route.query.status || undefined,
          dataSourceId: route.query.sourceId || undefined,
        },
        signal,
      })
      .then((r) => r.data),
  (p) => p.items.some((j) => active(j.status)),
)
watch(() => route.query, refresh, { immediate: true })
async function loadSources() {
  const result = await http.get<Page<Source>>('/data-sources', { params: { pageSize: 200 } })
  sources.value = result.data.items
}
void loadSources().catch(() => {
  /* The list still works with source IDs. */
})
</script>
<template>
  <div class="page-heading">
    <div>
      <h1>Импорты</h1>
      <p>История загрузок и результаты обработки.</p>
    </div>
    <RouterLink to="/imports/new" class="button button-primary">Загрузить файл</RouterLink>
  </div>
  <div class="filter-bar">
    <label
      >Источник<select
        class="form-control"
        :value="route.query.sourceId ?? ''"
        @change="
          query({ sourceId: ($event.target as HTMLSelectElement).value || undefined, page: 1 })
        "
      >
        <option value="">Все источники</option>
        <option v-for="source in sources" :key="source.id" :value="source.id">
          {{ source.name }}
        </option>
      </select></label
    ><label
      >Статус<select
        class="form-control"
        :value="route.query.status ?? ''"
        @change="
          query({ status: ($event.target as HTMLSelectElement).value || undefined, page: 1 })
        "
      >
        <option value="">Все статусы</option>
        <option
          v-for="status in [
            'Queued',
            'Processing',
            'RetryScheduled',
            'Completed',
            'CompletedWithErrors',
            'Failed',
            'Cancelled',
          ]"
          :key="status"
          :value="status"
        >
          {{ statuses[status] }}
        </option>
      </select></label
    ><button class="text-button" @click="router.replace({ query: {} })">Сбросить фильтры</button>
  </div>
  <ApiState
    :error="error"
    :loading="loading && !data"
    :stale="!!data"
    :empty="data?.total === 0"
    @retry="refresh"
    ><RouterLink to="/imports/new">Загрузить файл</RouterLink></ApiState
  >
  <section v-if="data" class="panel">
    <ImportTable :jobs="data.items" :sources="sources" /><PaginationBar
      :page="page"
      :page-size="pageSize"
      :total="data.total"
      @change="query({ page: $event })"
      @size="query({ pageSize: $event, page: 1 })"
    />
  </section>
</template>
