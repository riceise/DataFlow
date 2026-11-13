<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api, ApiError, type RuleDefinitionIssue } from '@/api'
import { useAuth } from '@/stores/auth'
import { useResource } from '@/composables/useResource'
import { active, number, statuses, jobErrorMessages, type Job as ImportJob } from '@/models'
import { storeToRefs } from 'pinia'
import PageHeader from '@/components/PageHeader.vue'
import ResultBreakdown from '@/components/ResultBreakdown.vue'
import RuleDefinitionEditor from '@/components/RuleDefinitionEditor.vue'
import {
  diagnosticPosition,
  locateIssues,
  syntaxDiagnostics,
} from '@/utils/ruleDefinitionDiagnostics'

type Set = {
  id: string
  name: string
  revision: number
  description: string | null
  isArchived: boolean
}
type Version = {
  id: string
  ruleSetId: string
  name: string
  versionNumber: number
  label?: string
  isAvailable?: boolean
  definition?: unknown
}
type Page<T> = { items: T[]; total: number }
type Source = { id: string; name: string }
type Job = {
  id: string
  status: string
  totalRows?: number | null
  processedRows: number
  acceptedRows: number
  validRows: number
  insertedRows: number
  updatedRows: number
  duplicateRows: number
  invalidRows: number
  ruleVersionNumber: number
  lastErrorCode?: string | null
}
const { user } = storeToRefs(useAuth())
const email = ref(''),
  password = ref(''),
  error = ref(''),
  message = ref(''),
  busy = ref(false)
const sets = ref<Set[]>([]),
  versions = ref<Version[]>([]),
  sources = ref<Source[]>([])
const selectedSet = ref(''),
  selectedVersion = ref(''),
  selectedSource = ref('')
const name = ref(''),
  label = ref(''),
  recipient = ref(''),
  sourceName = ref('')
const section = ref('mine'),
  job = ref<Job | null>(null),
  file = ref<File | null>(null)
function changeSection(value: string) {
  section.value = value
  versions.value = []
  if (value !== 'mine') void loadVersions()
}
const definition = ref(
  JSON.stringify(
    {
      definitionFormatVersion: 1,
      dataType: 'Tabular',
      csv: { delimiter: ';' },
      keyFields: ['code'],
      fields: [
        { name: 'code', header: 'Код', type: 'string', required: true, maxLength: 80 },
        { name: 'amount', header: 'Сумма', type: 'decimal', required: true, minimum: 0 },
      ],
    },
    null,
    2,
  ),
)
const jobId = ref('')
const editor = ref<InstanceType<typeof RuleDefinitionEditor>>()
const definitionIssues = ref<RuleDefinitionIssue[]>([])
const checkedDefinition = ref<string | null>(null)
const definitionValid = ref(false)
const syntaxErrors = computed(() => syntaxDiagnostics(definition.value))
const diagnostics = computed(() =>
  syntaxErrors.value.length
    ? syntaxErrors.value
    : checkedDefinition.value === definition.value
      ? locateIssues(definition.value, definitionIssues.value)
      : [],
)
const validationStale = computed(
  () => checkedDefinition.value !== null && checkedDefinition.value !== definition.value,
)
async function validateDefinition() {
  if (syntaxErrors.value.length) {
    checkedDefinition.value = definition.value
    definitionValid.value = false
    definitionIssues.value = []
    return false
  }
  const snapshot = definition.value
  try {
    await api('/rule-definitions/validate', { method: 'POST', body: snapshot })
    if (snapshot !== definition.value) return false
    checkedDefinition.value = snapshot
    definitionIssues.value = []
    definitionValid.value = true
    return true
  } catch (e) {
    if (!(e instanceof ApiError) || e.code !== 'invalid_rule_definition') throw e
    if (snapshot === definition.value) {
      checkedDefinition.value = snapshot
      definitionValid.value = false
      definitionIssues.value = e.issues.length
        ? e.issues
        : Object.entries(e.errors).flatMap(([path, messages]) =>
            messages.map((message) => ({ code: e.code, path, message, hint: '' })),
          )
    }
    return false
  }
}
const tracked = useResource(
  (signal) => api<ImportJob>(`/jobs/${jobId.value}`, { signal }),
  (j) => active(j.status),
  () => !!jobId.value,
)
watch(tracked.data, (value) => {
  if (value) {
    const validRows = value.validRows ?? 0
    job.value = {
      ...value,
      validRows,
      acceptedRows: validRows + value.insertedRows + value.updatedRows + value.duplicateRows,
      ruleVersionNumber: value.ruleVersionNumber ?? value.schemaVersion,
    }
  }
})
watch(tracked.error, (value) => {
  if (value) error.value = value
})
async function run(action: () => Promise<void>) {
  busy.value = true
  error.value = ''
  message.value = ''
  try {
    await action()
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = false
  }
}
async function refresh() {
  sets.value = (await api<Page<Set>>('/rule-sets?pageSize=200')).items
  sources.value = (await api<Page<Source>>('/data-sources?pageSize=200')).items
}
async function login(register = false) {
  await run(async () => {
    if (register)
      await api('/auth/register', {
        method: 'POST',
        body: JSON.stringify({ email: email.value, password: password.value }),
      })
    user.value = await api('/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email: email.value, password: password.value }),
    })
    password.value = ''
    await refresh()
  })
}
async function loadVersions(id?: string) {
  await run(async () => {
    selectedSet.value = id ?? ''
    selectedVersion.value = ''
    const path = id
      ? `/rule-sets/${id}/versions`
      : section.value === 'catalog'
        ? '/rule-catalog'
        : '/rule-library'
    versions.value = (await api<Page<Version>>(`${path}?pageSize=200`)).items
  })
}
async function selectVersion(id: string) {
  await run(async () => {
    const version = await api<Version>(`/rule-set-versions/${id}`)
    selectedVersion.value = id
    definition.value = JSON.stringify(version.definition, null, 2)
    label.value = ''
    name.value = version.name
  })
}
async function save(newSet: boolean) {
  await run(async () => {
    if (!(await validateDefinition())) return
    const body = newSet
      ? { name: name.value, definition: JSON.parse(definition.value) }
      : {
          definition: JSON.parse(definition.value),
          label: label.value || null,
          basedOnVersionId: selectedVersion.value || null,
        }
    let version: Version
    try {
      version = await api<Version>(
        newSet ? '/rule-sets' : `/rule-sets/${selectedSet.value}/versions`,
        { method: 'POST', body: JSON.stringify(body) },
      )
    } catch (e) {
      if (e instanceof ApiError && e.issues.length) {
        checkedDefinition.value = definition.value
        definitionValid.value = false
        definitionIssues.value = e.issues
        return
      }
      throw e
    }
    await refresh()
    await loadVersions(version.ruleSetId)
    await selectVersion(version.id)
    message.value = 'Версия сохранена'
  })
}
async function importJson(event: Event) {
  await run(async () => {
    const input = event.target as HTMLInputElement
    const value = input.files?.[0]
    if (!value) return
    if (value.size > 256 * 1024) throw new Error('Максимум 256 КиБ')
    definition.value = await value.text()
    input.value = ''
    await validateDefinition()
  })
}
async function access(action: string, method = 'PUT') {
  await run(async () => {
    await api(`/rule-set-versions/${selectedVersion.value}/${action}`, {
      method,
      body: action === 'grants' ? JSON.stringify({ email: recipient.value }) : undefined,
    })
    message.value = 'Доступ обновлён'
  })
}
async function poll(id: string) {
  jobId.value = id
  await tracked.refresh()
}
async function upload() {
  await run(async () => {
    if (!file.value) throw new Error('Выберите CSV или XLSX')
    const form = new FormData()
    form.append('file', file.value)
    const accepted = await api<{ jobId: string }>(
      `/data-sources/${selectedSource.value}/files?ruleSetVersionId=${selectedVersion.value}`,
      {
        method: 'POST',
        body: form,
        headers: { 'Idempotency-Key': crypto.randomUUID() },
      },
    )
    await poll(accepted.jobId)
  })
}
onMounted(async () => {
  try {
    user.value = await api('/auth/me')
    await refresh()
  } catch {
    /* Login form is the anonymous state. */
  }
})
</script>

<template>
  <div class="rules-page">
    <PageHeader
      title="Библиотека правил"
      description="Наборы проверок, закреплённые версии и доступ к ним."
    />
    <p v-if="error" role="alert" class="error">{{ error }}</p>
    <p v-if="message" role="status">{{ message }}</p>
    <form v-if="!user" @submit.prevent="login()" class="card">
      <h2>Вход</h2>
      <label>Email<input v-model="email" type="email" required autocomplete="username" /></label>
      <label
        >Пароль<input v-model="password" type="password" required autocomplete="current-password"
      /></label>
      <button :disabled="busy">Войти</button
      ><button type="button" :disabled="busy" @click="login(true)">Зарегистрироваться</button>
    </form>
    <template v-else>
      <nav>
        <button :aria-pressed="section === 'mine'" @click="changeSection('mine')">Мои наборы</button
        ><button :aria-pressed="section === 'library'" @click="changeSection('library')">
          Доступные мне</button
        ><button :aria-pressed="section === 'catalog'" @click="changeSection('catalog')">
          Каталог
        </button>
        <button
          @click="
            run(async () => {
              await api('/auth/logout', { method: 'POST' })
              user = null
            })
          "
        >
          Выйти
        </button>
      </nav>
      <div class="layout">
        <section class="card">
          <h2>Наборы и версии</h2>
          <template v-if="section === 'mine'"
            ><p v-if="!sets.length">Создайте первый набор справа.</p>
            <div v-for="set in sets" :key="set.id">
              <button @click="loadVersions(set.id)">
                {{ set.name }} {{ set.isArchived ? '(архив)' : '' }}
              </button>
              <button
                :aria-label="`Архивировать ${set.name}`"
                @click="
                  run(async () => {
                    await api(`/rule-sets/${set.id}`, {
                      method: 'PATCH',
                      body: JSON.stringify({
                        name: set.name,
                        description: set.description,
                        revision: set.revision,
                        isArchived: !set.isArchived,
                      }),
                    })
                    await refresh()
                  })
                "
              >
                {{ set.isArchived ? 'Восстановить' : 'В архив' }}
              </button>
            </div>
          </template>
          <ul>
            <li v-for="version in versions" :key="version.id">
              <button :disabled="version.isAvailable === false" @click="selectVersion(version.id)">
                {{ version.name }} · v{{ version.versionNumber }} {{ version.label }}</button
              ><span v-if="version.isAvailable === false">Доступ отозван</span>
            </li>
          </ul>
        </section>
        <section class="card">
          <h2>Определение проверки</h2>
          <label>Название<input v-model="name" maxlength="200" /></label>
          <label
            >Загрузить JSON<input type="file" accept=".json,application/json" @change="importJson"
          /></label>
          <p id="definition-label">JSON правил</p>
          <div class="definition-workspace" :class="{ 'has-errors': diagnostics.length }">
            <RuleDefinitionEditor
              ref="editor"
              v-model="definition"
              :diagnostics="diagnostics"
              :disabled="busy"
            />
            <div v-if="diagnostics.length" class="definition-errors" role="alert">
              <h3>Исправьте ошибки в определении: {{ diagnostics.length }}</h3>
              <ul>
                <li v-for="(issue, index) in diagnostics" :key="`${issue.path}-${index}`">
                  <strong v-if="issue.fieldName">Поле «{{ issue.fieldName }}»</strong>
                  <p>{{ issue.message }}</p>
                  <p v-if="issue.hint" class="hint">{{ issue.hint }}</p>
                  <button type="button" @click="editor?.focusIssue(issue)">
                    Перейти к ошибке · {{ issue.path }} · строка
                    {{ diagnosticPosition(definition, issue.from).line }}, столбец
                    {{ diagnosticPosition(definition, issue.from).column }}
                  </button>
                </li>
              </ul>
            </div>
          </div>
          <p v-if="!diagnostics.length && validationStale" role="status">
            Определение изменено. Повторите проверку.
          </p>
          <p
            v-else-if="!diagnostics.length && definitionValid && checkedDefinition === definition"
            role="status"
          >
            Определение корректно
          </p>
          <button
            :disabled="busy"
            @click="
              run(async () => {
                await validateDefinition()
              })
            "
          >
            Проверить определение
          </button>
          <button :disabled="busy || !name" @click="save(true)">Создать новый набор</button>
          <template v-if="selectedSet && section === 'mine'"
            ><label>Метка новой версии<input v-model="label" maxlength="100" /></label
            ><button :disabled="busy" @click="save(false)">Сохранить новую версию</button></template
          >
          <template v-if="selectedVersion"
            ><h3>Выбранная версия</h3>
            <a :href="`/api/v1/rule-set-versions/${selectedVersion}/export`">Скачать JSON</a>
            <button
              @click="
                run(async () => {
                  await api(`/rule-library/${selectedVersion}`, { method: 'PUT' })
                  message = 'Добавлено в библиотеку'
                })
              "
            >
              В библиотеку
            </button>
            <button
              @click="
                run(async () => {
                  const v = await api<Version>(`/rule-set-versions/${selectedVersion}/fork`, {
                    method: 'POST',
                    body: JSON.stringify({ name: `${name} — копия` }),
                  })
                  section = 'mine'
                  await refresh()
                  await loadVersions(v.ruleSetId)
                })
              "
            >
              Создать независимую копию
            </button>
            <template v-if="section === 'mine'"
              ><label>Email получателя<input v-model="recipient" type="email" /></label
              ><button @click="access('grants', 'POST')">Поделиться с пользователем</button
              ><button @click="access('publication')">Опубликовать версию</button
              ><button @click="access('publication', 'DELETE')">Снять публикацию</button></template
            >
          </template>
        </section>
      </div>
      <section v-if="selectedVersion" class="card">
        <h2>Проверить файл выбранной версией</h2>
        <label>Название нового источника<input v-model="sourceName" /></label>
        <button
          :disabled="!sourceName || busy"
          @click="
            run(async () => {
              const s = await api<Source>('/rule-sources', {
                method: 'POST',
                body: JSON.stringify({ name: sourceName, ruleSetVersionId: selectedVersion }),
              })
              await refresh()
              selectedSource = s.id
            })
          "
        >
          Создать источник
        </button>
        <label
          >Источник<select v-model="selectedSource">
            <option value="">Выберите источник</option>
            <option v-for="source in sources" :key="source.id" :value="source.id">
              {{ source.name }}
            </option>
          </select></label
        >
        <label
          >Файл (CSV или XLSX)<input
            type="file"
            accept=".csv,.xlsx"
            @change="file = ($event.target as HTMLInputElement).files?.[0] ?? null"
        /></label>
        <button :disabled="busy || !selectedSource || !file" @click="upload">
          Начать проверку
        </button>
        <div v-if="job" class="job-summary">
          <p>
            {{ statuses[job.status] ?? job.status }} · Версия {{ job.ruleVersionNumber }} ·
            Проверено {{ number(job.processedRows)
            }}<template v-if="job.totalRows !== null && job.totalRows !== undefined">
              из {{ number(job.totalRows) }}</template
            >
            · Принято {{ number(job.acceptedRows) }} (<template
              v-if="
                job.validRows > 0 ||
                (job.insertedRows === 0 && job.updatedRows === 0 && job.duplicateRows === 0)
              "
              >Валидно {{ number(job.validRows) }}</template
            ><template v-if="job.insertedRows || job.updatedRows || job.duplicateRows"
              ><template v-if="job.validRows > 0"> · </template>Добавлено
              {{ number(job.insertedRows) }} · Обновлено {{ number(job.updatedRows) }} · Без
              изменений {{ number(job.duplicateRows) }}</template
            >) · Ошибочных {{ number(job.invalidRows) }}
          </p>
          <ResultBreakdown
            :inserted="job.insertedRows"
            :updated="job.updatedRows"
            :invalid="job.invalidRows"
            :duplicate="job.duplicateRows"
            :valid="job.validRows"
            :show-valid="
              job.validRows > 0 ||
              (job.insertedRows === 0 && job.updatedRows === 0 && job.duplicateRows === 0)
            "
          />
          <p v-if="job.lastErrorCode" class="error">
            Причина сбоя:
            {{ jobErrorMessages[job.lastErrorCode] ?? '' }}
            <code>{{ job.lastErrorCode }}</code>
          </p>
          <RouterLink :to="`/imports/${job.id}`">Открыть результаты и ошибки</RouterLink
          ><template v-if="job.invalidRows">
            · <a :href="`/api/v1/import-jobs/${job.id}/errors.csv`">Скачать ошибки CSV</a></template
          >
        </div>
      </section>
    </template>
  </div>
</template>

<style scoped>
.rules-page {
  max-width: 1280px;
  margin: auto;
  padding: 2rem;
  color: var(--text-primary);
}
header,
nav {
  display: flex;
  align-items: center;
  gap: 1rem;
  flex-wrap: wrap;
  margin-bottom: 1rem;
}
h1 {
  flex: 1;
}
.layout {
  display: grid;
  grid-template-columns: minmax(240px, 1fr) minmax(300px, 2fr);
  gap: 1rem;
}
.card {
  min-width: 0;
  border: 1px solid var(--border-subtle);
  border-radius: 10px;
  padding: 24px;
  margin-bottom: 24px;
}
label {
  display: block;
  margin: 0.75rem 0;
}
input,
textarea,
select {
  display: block;
  width: 100%;
  box-sizing: border-box;
  padding: 0.6rem;
  margin-top: 0.25rem;
  border: 1px solid var(--border-control);
  border-radius: 6px;
  background: var(--bg-surface);
  color: var(--text-primary);
}
textarea {
  font-family: 'IBM Plex Mono', Consolas, monospace;
  font-size: 12px;
  line-height: 20px;
  resize: vertical;
}
button {
  padding: 0.55rem 0.8rem;
  margin: 0.25rem;
  cursor: pointer;
  border: 1px solid var(--border-control);
  border-radius: 5px;
}
button:disabled {
  cursor: default;
}
.error {
  color: var(--danger);
  white-space: pre-wrap;
}
.definition-errors {
  border: 1px solid var(--danger);
  border-radius: 6px;
  padding: 12px;
  margin-top: 12px;
  overflow-wrap: anywhere;
}
.card .definition-errors h3 {
  margin-top: 0;
}
.definition-errors p {
  margin: 6px 0;
}
.definition-errors button {
  text-align: left;
  overflow-wrap: anywhere;
  max-width: 100%;
}
.hint {
  color: var(--text-primary);
}
li {
  margin: 0.5rem 0;
}
.card h2 {
  margin-bottom: 16px;
}
.card h3 {
  margin-top: 24px;
}
nav {
  border-bottom: 1px solid var(--border-subtle);
  padding-bottom: 12px;
}
nav button[aria-pressed='true'] {
  background: var(--bg-selected);
  color: var(--accent);
  border-color: var(--accent);
}

/* Dark theme: the rules page leans on native controls, so the surfaces and
   focus states need explicit help to stay legible on navy. */
:root[data-theme='dark'] .rules-page .card {
  background: var(--bg-surface);
  border-color: var(--border-subtle);
}

:root[data-theme='dark'] .rules-page button {
  background: var(--bg-subtle);
  color: var(--text-primary);
  border-color: var(--border-control);
}

:root[data-theme='dark'] .rules-page button:hover:not(:disabled) {
  background: var(--bg-hover);
  border-color: var(--accent);
  color: var(--text-primary);
}

:root[data-theme='dark'] .rules-page button:disabled {
  color: var(--text-muted);
  background: var(--bg-subtle);
  border-color: var(--border-subtle);
}

:root[data-theme='dark'] .rules-page nav button[aria-pressed='true'],
:root[data-theme='dark'] .rules-page nav button[aria-pressed='true']:hover {
  background: var(--bg-selected);
  color: var(--accent);
  border-color: var(--accent);
  font-weight: 500;
}

:root[data-theme='dark'] .rules-page .definition-errors {
  background: var(--danger-bg);
  border-color: var(--danger);
}

:root[data-theme='dark'] .rules-page .definition-errors button {
  background: transparent;
  border-color: var(--danger);
  color: var(--danger);
}

:root[data-theme='dark'] .rules-page .definition-errors button:hover {
  background: var(--danger);
  color: var(--on-accent);
}

:root[data-theme='dark'] .rules-page .hint {
  color: var(--text-muted);
}

:root[data-theme='dark'] .rules-page .error {
  color: var(--danger);
}

:root[data-theme='dark'] .rules-page .card h3 {
  color: var(--text-secondary);
}
@media (min-width: 1024px) {
  .rules-page {
    max-width: none;
    margin: 0;
    padding: 0;
  }
  .layout {
    grid-template-columns: minmax(160px, 0.55fr) minmax(0, 2.45fr);
    align-items: start;
  }
  .definition-workspace.has-errors {
    display: grid;
    grid-template-columns: minmax(0, 1.5fr) minmax(220px, 1fr);
    align-items: start;
    gap: 16px;
  }
  .definition-errors {
    min-width: 0;
    margin-top: 0;
  }
}
@media (max-width: 760px) {
  .layout {
    grid-template-columns: 1fr;
  }
  .rules-page {
    padding: 1rem;
  }
  .card {
    padding: 16px;
  }
}
</style>
