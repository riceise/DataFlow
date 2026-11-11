<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { http } from '@/api'
import { type Job, type Page, type Row, active, number, date, statuses } from '@/models'
import { useResource } from '@/composables/useResource'
import ApiState from '@/components/ApiState.vue'
import LiveBadge from '@/components/LiveBadge.vue'
import PaginationBar from '@/components/PaginationBar.vue'
import ResultBreakdown from '@/components/ResultBreakdown.vue'
const route = useRoute(),
  router = useRouter(),
  id = String(route.params.id)
const expanded = ref<number[]>([]),
  actionError = ref(''),
  busy = ref(false),
  dialog = ref<HTMLDialogElement>(),
  action = ref<'cancel' | 'retry'>('cancel'),
  definition = ref<unknown>(),
  showDefinition = ref(false)
const page = computed(() => Math.max(1, Number(route.query.page) || 1)),
  pageSize = computed(() =>
    [25, 50, 100].includes(Number(route.query.pageSize)) ? Number(route.query.pageSize) : 25,
  )
const job = useResource(
  (signal) => http.get<Job>(`/jobs/${id}`, { signal }).then((r) => r.data),
  (j) => active(j.status),
)
const rows = useResource((signal) =>
  http
    .get<Page<Row>>(`/import-jobs/${id}/rows`, {
      signal,
      params: { page: page.value, pageSize: pageSize.value, status: route.query.rows || undefined },
    })
    .then((r) => r.data),
)
void job.refresh()
watch(() => route.query, rows.refresh, { immediate: true })
watch(
  () => job.data.value?.status,
  (status, old) => {
    if (old && status !== old) void rows.refresh()
  },
)
function query(values: Record<string, string | number | undefined>) {
  void router.replace({ query: { ...route.query, ...values } })
}
function toggle(row: number) {
  expanded.value = expanded.value.includes(row)
    ? expanded.value.filter((n) => n !== row)
    : [...expanded.value, row]
}
function confirm(value: 'cancel' | 'retry') {
  action.value = value
  dialog.value?.showModal()
}
async function control() {
  busy.value = true
  actionError.value = ''
  try {
    await http.post(`/import-jobs/${id}/${action.value}`)
    dialog.value?.close()
  } catch (e) {
    actionError.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = false
    await job.refresh()
  }
}
async function download() {
  busy.value = true
  actionError.value = ''
  try {
    const result = await http.get(`/import-jobs/${id}/errors.csv`, { responseType: 'blob' })
    const url = URL.createObjectURL(result.data)
    const a = document.createElement('a')
    a.href = url
    a.download = `import-${id}-errors.csv`
    a.click()
    setTimeout(() => URL.revokeObjectURL(url), 1000)
  } catch (e) {
    actionError.value = String(e)
  } finally {
    busy.value = false
  }
}
async function loadDefinition() {
  showDefinition.value = !showDefinition.value
  if (!definition.value && showDefinition.value)
    try {
      definition.value = (await http.get(`/jobs/${id}/rule-definition`)).data
    } catch (e) {
      actionError.value = String(e)
    }
}
</script>
<template>
  <RouterLink to="/imports" class="text-button">← Импорты</RouterLink
  ><ApiState
    :error="job.error.value"
    :loading="job.loading.value && !job.data.value"
    :stale="!!job.data.value"
    @retry="job.refresh"
  /><template v-if="job.data.value"
    ><div class="page-heading">
      <div>
        <h1>{{ job.data.value.originalFileName }}</h1>
        <p>
          <RouterLink :to="`/sources/${job.data.value.dataSourceId}`">Источник</RouterLink> ·
          {{ job.data.value.ruleSetName ?? 'Схема' }} v{{
            job.data.value.ruleVersionNumber ?? job.data.value.schemaVersion
          }}
          · {{ date(job.data.value.createdAt) }}
        </p>
        <LiveBadge :status="job.data.value.status" />
      </div>
      <div class="page-actions">
        <button
          v-if="job.data.value.invalidRows"
          class="button button-secondary"
          :disabled="busy"
          @click="download"
        >
          {{
            active(job.data.value.status) ? 'Ошибки на текущий момент' : 'Скачать ошибки CSV'
          }}</button
        ><button
          v-if="job.data.value.canCancel"
          class="button button-secondary"
          :disabled="busy"
          @click="confirm('cancel')"
        >
          Запросить отмену</button
        ><button
          v-if="job.data.value.canRetry"
          class="button button-primary"
          :disabled="busy"
          @click="confirm('retry')"
        >
          Продолжить обработку
        </button>
      </div>
    </div>
    <p v-if="actionError" class="form-error" role="alert">{{ actionError }}</p>
    <p v-if="job.data.value.cancelRequested && active(job.data.value.status)" role="status">
      Отмена запрошена. Ожидаем остановки обработки.
    </p>
    <section class="panel operator-panel import-progress-panel">
      <div class="results-total">
        <strong>{{ number(job.data.value.processedRows) }}</strong
        ><span>строк обработано</span>
      </div>
      <p v-if="job.data.value.totalRows === null">
        Всего строк станет известно после чтения файла.
      </p>
      <p v-else>
        Всего строк: {{ number(job.data.value.totalRows)
        }}{{ job.data.value.totalRows === 0 ? ' — в файле нет строк данных' : '' }}
      </p>
      <div
        v-if="active(job.data.value.status) && job.data.value.totalRows === null"
        class="indeterminate-track"
        role="progressbar"
        :aria-label="`Обработано ${job.data.value.processedRows} строк`"
      >
        <span />
      </div>
      <progress
        v-else-if="active(job.data.value.status) && job.data.value.totalRows"
        :value="Math.min(job.data.value.processedRows, job.data.value.totalRows)"
        :max="job.data.value.totalRows"
        aria-label="Обработка строк"
      />
      <ResultBreakdown
        :inserted="job.data.value.insertedRows"
        :updated="job.data.value.updatedRows"
        :invalid="job.data.value.invalidRows"
        :duplicate="job.data.value.duplicateRows"
        :valid="job.data.value.validRows"
      />
      <p v-if="['Failed', 'Cancelled'].includes(job.data.value.status)">
        Сохранено результатов: {{ number(job.data.value.processedRows) }}. Они не были отменены.
        <RouterLink :to="{ path: '/imports/new', query: { sourceId: job.data.value.dataSourceId } }"
          >Загрузить исправленный файл</RouterLink
        >
      </p>
      <p v-if="job.data.value.lastErrorCode">
        Причина сбоя: <code>{{ job.data.value.lastErrorCode }}</code>
      </p>
      <p v-if="job.data.value.nextAttemptAt">
        Следующая попытка: {{ date(job.data.value.nextAttemptAt) }}
      </p>
      <details>
        <summary>Сведения об импорте</summary>
        <p>
          ID: <code>{{ id }}</code>
        </p>
        <p>
          Начало: {{ date(job.data.value.startedAt) }} · Завершение:
          {{ date(job.data.value.completedAt) }}
        </p>
        <button class="text-button" @click="loadDefinition">
          {{ showDefinition ? 'Скрыть правила' : 'Показать закреплённые правила' }}
        </button>
        <pre v-if="showDefinition">{{ JSON.stringify(definition, null, 2) }}</pre>
      </details>
    </section>
    <section class="panel row-results-panel">
      <div class="row-results-heading">
        <h2>Результаты строк</h2>
        <div class="filter-bar">
          <label
            >Результат<select
              class="form-control"
              :value="route.query.rows ?? ''"
              @change="
                query({ rows: ($event.target as HTMLSelectElement).value || undefined, page: 1 })
              "
            >
              <option value="">Все строки</option>
              <option
                v-for="status in ['Invalid', 'Inserted', 'Updated', 'Duplicate', 'Valid']"
                :key="status"
                :value="status"
              >
                {{ statuses[status] }}
              </option>
            </select></label
          ><button class="button button-secondary" @click="rows.refresh">Обновить результаты</button
          ><span v-if="active(job.data.value.status)"
            >Строки обновляются по запросу; счётчики — автоматически.</span
          >
        </div>
      </div>
      <ApiState
        :error="rows.error.value"
        :loading="rows.loading.value && !rows.data.value"
        :empty="rows.data.value?.total === 0"
        :stale="!!rows.data.value"
        @retry="rows.refresh"
      />
      <div v-if="rows.data.value" class="table-scroll">
        <table class="operator-table row-results-table">
          <thead>
            <tr>
              <th>Строка</th>
              <th>Результат</th>
              <th>Ключ</th>
              <th>Ошибки</th>
              <th>Сведения</th>
            </tr>
          </thead>
          <tbody>
            <template v-for="row in rows.data.value.items" :key="row.rowNumber"
              ><tr>
                <td data-label="Строка">{{ row.rowNumber }}</td>
                <td data-label="Результат"><LiveBadge :status="row.status" /></td>
                <td data-label="Ключ">
                  <code>{{ row.naturalKey ?? '—' }}</code>
                </td>
                <td data-label="Ошибки">
                  {{ row.errors.map((e) => e.message).join('; ') || '—' }}
                </td>
                <td class="row-details-action">
                  <button
                    class="text-button"
                    :aria-expanded="expanded.includes(row.rowNumber)"
                    @click="toggle(row.rowNumber)"
                  >
                    {{ expanded.includes(row.rowNumber) ? 'Свернуть' : 'Раскрыть' }}
                  </button>
                </td>
              </tr>
              <tr v-if="expanded.includes(row.rowNumber)" class="expanded-result-row">
                <td colspan="5">
                  <div v-for="(item, index) in row.errors" :key="index" class="row-error">
                    <strong>{{ item.field }}</strong> — {{ item.message }}
                    <code>{{ item.code }}</code>
                  </div>
                  <pre v-if="row.rawData">{{ row.rawData }}</pre>
                  <p v-else>Исходные значения не сохранены.</p>
                </td>
              </tr></template
            >
          </tbody>
        </table>
      </div>
      <PaginationBar
        v-if="rows.data.value"
        :page="page"
        :page-size="pageSize"
        :total="rows.data.value.total"
        @change="query({ page: $event })"
        @size="query({ pageSize: $event, page: 1 })"
      /></section
  ></template>
  <dialog ref="dialog" class="operator-dialog" @cancel="busy && $event.preventDefault()">
    <h2>{{ action === 'cancel' ? 'Остановить импорт?' : 'Продолжить обработку?' }}</h2>
    <p>
      {{
        action === 'cancel'
          ? 'Уже сохранённые записи останутся. Обработка остановится после текущего пакета.'
          : 'Этот импорт использует исходный файл. Продолжение старого задания может заменить более новые данные в этом источнике. Обработка продолжится с последнего сохранённого пакета.'
      }}
    </p>
    <p v-if="actionError" role="alert">{{ actionError }}</p>
    <div class="page-actions">
      <button autofocus class="button button-secondary" :disabled="busy" @click="dialog?.close()">
        Вернуться</button
      ><button class="button button-primary" :disabled="busy" @click="control">
        {{ busy ? 'Отправка…' : 'Подтвердить' }}
      </button>
    </div>
  </dialog>
</template>
