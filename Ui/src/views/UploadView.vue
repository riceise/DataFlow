<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { useRoute, useRouter, onBeforeRouteLeave } from 'vue-router'
import { http, ApiError } from '@/api'
import type { Page, Source } from '@/models'
import { useResource } from '@/composables/useResource'
import ApiState from '@/components/ApiState.vue'
import PageHeader from '@/components/PageHeader.vue'
const route = useRoute(),
  router = useRouter(),
  sourceId = ref(String(route.query.sourceId ?? '')),
  file = ref<File>(),
  input = ref<HTMLInputElement>(),
  busy = ref(false),
  error = ref(''),
  percent = ref<number>(),
  dragging = ref(false),
  key = ref(''),
  uncertain = ref(false)
let controller: AbortController | undefined
const {
  data,
  loading,
  error: loadError,
  refresh,
} = useResource(async (signal) => {
  const sources: Source[] = []
  let page = 1,
    total = 0
  do {
    const result = await http.get<Page<Source>>('/data-sources', {
      signal,
      params: { page: page++, pageSize: 200 },
    })
    sources.push(...result.data.items)
    total = result.data.total
  } while (sources.length < total)
  const options = await http.get<{ maxFileBytes: number; maxXlsxFileBytes?: number }>(
    '/files/options',
    { signal },
  )
  return { sources, options: options.data }
})
void refresh()
const source = computed(() => data.value?.sources.find((s) => s.id === sourceId.value))
const rules = ref<unknown>()
async function loadRules() {
  rules.value = undefined
  if (source.value?.defaultRuleSetVersionId)
    try {
      rules.value = (
        await http.get(`/rule-set-versions/${source.value.defaultRuleSetVersionId}`)
      ).data.definition
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
    }
}
function reset() {
  if (!busy.value) {
    key.value = ''
    uncertain.value = false
    error.value = ''
    void loadRules()
  }
}
function newAttempt() {
  uncertain.value = false
  reset()
  file.value = undefined
}
function choose(value?: File) {
  file.value = undefined
  reset()
  if (!value) return
  if (!/\.(csv|xlsx)$/i.test(value.name)) {
    error.value = 'Выберите CSV или XLSX.'
    return
  }
  const isXlsx = /\.xlsx$/i.test(value.name)
  const maxBytes = data.value
    ? isXlsx
      ? (data.value.options.maxXlsxFileBytes ?? data.value.options.maxFileBytes)
      : data.value.options.maxFileBytes
    : undefined
  if (!value.size || (maxBytes !== undefined && value.size > maxBytes)) {
    error.value = 'Файл пустой или превышает допустимый размер.'
    return
  }
  file.value = value
}
function drop(event: DragEvent) {
  dragging.value = false
  if (busy.value) return
  if (event.dataTransfer?.files.length !== 1) {
    error.value = 'Выберите один файл.'
    return
  }
  choose(event.dataTransfer.files[0])
}
async function upload() {
  if (!file.value || !source.value) return
  if (
    !source.value.acceptedFormats.some(
      (f) => f.toLowerCase() === file.value!.name.split('.').pop()?.toLowerCase(),
    )
  ) {
    error.value = 'Формат файла не разрешён источником.'
    return
  }
  busy.value = true
  error.value = ''
  percent.value = undefined
  key.value ||= crypto.randomUUID()
  controller = new AbortController()
  const form = new FormData()
  form.append('file', file.value)
  try {
    const result = await http.post<{ jobId: string }>(
      `/data-sources/${sourceId.value}/files`,
      form,
      {
        signal: controller.signal,
        params: source.value.defaultRuleSetVersionId
          ? { ruleSetVersionId: source.value.defaultRuleSetVersionId }
          : undefined,
        headers: { 'Idempotency-Key': key.value },
        onUploadProgress: (p) => {
          percent.value = p.total
            ? Math.min(100, Math.round((p.loaded / p.total) * 100))
            : undefined
        },
      },
    )
    busy.value = false
    uncertain.value = false
    await router.push(`/imports/${result.data.jobId}`)
  } catch (e) {
    uncertain.value = !(e instanceof ApiError) || e.status === 0
    error.value = uncertain.value
      ? 'Подтверждение не получено. Сервер мог принять файл. Повторите отправку с тем же файлом — ключ запроса сохранён.'
      : e instanceof Error
        ? e.message
        : String(e)
  } finally {
    busy.value = false
  }
}
onBeforeRouteLeave(() => !busy.value || window.confirm('Прервать загрузку и покинуть страницу?'))
onBeforeUnmount(() => controller?.abort())
</script>
<template>
  <PageHeader title="Загрузить файл" description="Выберите источник и файл для обработки.">
    <RouterLink to="/imports" class="text-button">К истории</RouterLink>
  </PageHeader>
  <ApiState :error="loadError" :loading="loading && !data" @retry="refresh" />
  <form v-if="data" class="panel operator-form" @submit.prevent="upload">
    <section class="form-section">
      <h2>Источник данных</h2>
      <p>Определяет форматы файла и правила проверки строк.</p>
      <label
        >Источник<select
          v-model="sourceId"
          class="form-control"
          :disabled="busy || uncertain"
          required
          @change="reset"
        >
          <option value="">Выберите источник</option>
          <option
            v-for="item in data.sources.filter((s) => s.isActive)"
            :key="item.id"
            :value="item.id"
          >
            {{ item.name }}
          </option>
        </select></label
      >
      <p v-if="!data.sources.some((s) => s.isActive)">
        Нет активных источников. <RouterLink to="/sources/new">Создать источник</RouterLink>
      </p>
      <section v-if="source" class="upload-requirements">
        <h2>Требования к файлу</h2>
        <p>
          {{ source.acceptedFormats.join(', ') }} · максимум
          {{ Math.round(data.options.maxFileBytes / 1024 / 1024) }} МиБ{{
            data.options.maxXlsxFileBytes &&
            data.options.maxXlsxFileBytes < data.options.maxFileBytes &&
            source.acceptedFormats.includes('Xlsx')
              ? ` (XLSX — до ${Math.round(data.options.maxXlsxFileBytes / 1024 / 1024)} МиБ)`
              : ''
          }}
        </p>
        <p>CSV: UTF-8, первая строка — заголовки. XLSX: первый лист, первая строка — заголовки.</p>
        <template v-if="!source.defaultRuleSetVersionId"
          ><p>
            Схема v{{ source.schemaVersion }} · разделитель CSV:
            <code>{{ source.schema.csvDelimiter }}</code>
          </p>
          <p>
            Колонки: {{ source.schema.fields.map((f) => f.name).join(', ') }}. Даты: yyyy-MM-dd.
            Логические значения: true/false.
          </p></template
        ><template v-else
          ><p>Будет закреплена версия правил источника.</p>
          <button type="button" class="text-button" @click="loadRules">Показать правила</button>
          <pre v-if="rules">{{ JSON.stringify(rules, null, 2) }}</pre>
        </template>
      </section>
    </section>
    <section class="form-section">
      <h2>Файл для импорта</h2>
      <div
        class="dropzone"
        :class="{ 'drag-over': dragging }"
        @dragover.prevent="dragging = true"
        @dragleave.prevent="dragging = false"
        @drop.prevent="drop"
      >
        <strong>{{ file?.name ?? 'Перетащите CSV или XLSX' }}</strong>
        <p v-if="file">{{ Math.ceil(file.size / 1024) }} КиБ</p>
        <button
          type="button"
          class="button button-secondary"
          :disabled="busy || uncertain"
          @click="input?.click()"
        >
          {{ file ? 'Заменить файл' : 'Выбрать файл' }}</button
        ><input
          ref="input"
          type="file"
          class="sr-only"
          accept=".csv,.xlsx"
          :disabled="busy || uncertain"
          aria-label="Файл CSV или XLSX"
          @change="choose(($event.target as HTMLInputElement).files?.[0])"
        />
      </div>
      <p v-if="error" class="form-error" role="alert">{{ error }}</p>
      <div v-if="busy" role="status">
        <progress
          v-if="percent !== undefined"
          :value="percent"
          max="100"
          aria-label="Передача файла"
        />
        <p>
          {{
            percent === 100
              ? 'Файл передан. Ожидаем подтверждения…'
              : `Передача файла${percent === undefined ? '…' : `: ${percent}%`}`
          }}
        </p>
      </div>
      <p v-if="!sourceId || !file">Для отправки выберите источник и файл.</p>
    </section>
    <div class="page-actions">
      <button class="button button-primary" :disabled="busy || !file || !source?.isActive">
        {{ uncertain ? 'Повторить с тем же ключом' : 'Загрузить и начать импорт' }}</button
      ><button
        v-if="busy"
        type="button"
        class="button button-secondary"
        @click="controller?.abort()"
      >
        Прервать загрузку</button
      ><button v-if="uncertain" type="button" class="text-button" @click="newAttempt">
        Начать новую попытку
      </button>
    </div>
  </form>
</template>
