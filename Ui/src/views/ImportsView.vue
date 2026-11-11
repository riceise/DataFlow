<script setup lang="ts">
import { ref, watch, computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { http } from '@/api'
import { type Job, type Page, type Source, statuses, active } from '@/models'
import { useResource } from '@/composables/useResource'
import ApiState from '@/components/ApiState.vue'
import ImportTable from '@/components/ImportTable.vue'
import PaginationBar from '@/components/PaginationBar.vue'
import PageHeader from '@/components/PageHeader.vue'
import AppIcon from '@/components/AppIcon.vue'
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
  <PageHeader title="Импорты" description="История загрузок и результаты обработки.">
    <RouterLink to="/imports/new" class="button button-primary"
      ><AppIcon name="upload" :size="18" />Загрузить файл</RouterLink
    >
  </PageHeader>
  <section class="panel imports-section">
    <div class="filter-bar ledger-toolbar">
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
    <ApiState :error="error" :loading="loading && !data" :stale="!!data" @retry="refresh"
      ><RouterLink to="/imports/new">Загрузить файл</RouterLink></ApiState
    >
    <div v-if="data?.total === 0" class="empty-state">
      <h3>
        {{
          route.query.status || route.query.sourceId
            ? 'По этим условиям ничего не найдено'
            : 'Импортов пока нет'
        }}
      </h3>
      <p>
        {{
          route.query.status || route.query.sourceId
            ? 'Измените источник или статус, чтобы увидеть другие загрузки.'
            : 'Загрузите CSV или XLSX, чтобы начать обработку.'
        }}
      </p>
      <button
        v-if="route.query.status || route.query.sourceId"
        class="button button-secondary"
        @click="router.replace({ query: {} })"
      >
        Сбросить фильтры
      </button>
      <RouterLink v-else to="/imports/new" class="button button-secondary"
        >Загрузить файл</RouterLink
      >
    </div>
    <template v-if="data && data.total > 0">
      <ImportTable :jobs="data.items" :sources="sources" /><PaginationBar
        :page="page"
        :page-size="pageSize"
        :total="data.total"
        @change="query({ page: $event })"
        @size="query({ pageSize: $event, page: 1 })"
      />
    </template>
  </section>
</template>
