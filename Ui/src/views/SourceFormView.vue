<script setup lang="ts">
import { computed, ref, onBeforeUnmount } from 'vue'
import { useRoute, useRouter, onBeforeRouteLeave } from 'vue-router'
import { http, ApiError } from '@/api'
import type { Field, Page, Source } from '@/models'
import ApiState from '@/components/ApiState.vue'
import PageHeader from '@/components/PageHeader.vue'
const route = useRoute(),
  router = useRouter(),
  id = route.params.id ? String(route.params.id) : undefined
const name = ref(''),
  formats = ref(['Csv', 'Xlsx']),
  delimiter = ref(','),
  registeredAt = ref(true),
  busy = ref(false),
  loading = ref(false),
  error = ref(''),
  errors = ref<Record<string, string[]>>({}),
  mode = ref('customer'),
  versionId = ref(''),
  saved = ref(false),
  initial = ref(''),
  source = ref<Source>(),
  versions = ref<{ id: string; name: string; versionNumber: number }[]>([])
const fields = ref<Field[]>([
  { name: 'externalId', type: 'string', required: true, maxLength: 80 },
  { name: 'name', type: 'string', required: true, maxLength: 200 },
  { name: 'email', type: 'email', required: true, maxLength: 254 },
  { name: 'registeredAt', type: 'date', required: false, format: 'yyyy-MM-dd' },
  { name: 'isActive', type: 'boolean', required: true },
])
const snapshot = () =>
  JSON.stringify([
    name.value,
    formats.value,
    delimiter.value,
    registeredAt.value,
    fields.value,
    mode.value,
    versionId.value,
  ])
const dirty = computed(() => !!initial.value && !saved.value && snapshot() !== initial.value)
const labels: Record<string, string> = {
  externalId: 'Внешний ID',
  name: 'Имя',
  email: 'Email',
  registeredAt: 'Дата регистрации',
  isActive: 'Активен',
}
async function load() {
  loading.value = true
  error.value = ''
  try {
    const all: typeof versions.value = []
    let page = 1,
      total = 0
    do {
      const result = await http.get<Page<(typeof versions.value)[number]>>('/rule-library', {
        params: { page: page++, pageSize: 200 },
      })
      all.push(...result.data.items)
      total = result.data.total
    } while (all.length < total)
    versions.value = all
    if (id) {
      source.value = (await http.get<Source>(`/data-sources/${id}`)).data
      name.value = source.value.name
      formats.value = source.value.acceptedFormats
      delimiter.value = source.value.schema.csvDelimiter
      registeredAt.value = source.value.schema.fields.some((f) => f.name === 'registeredAt')
      if (source.value.defaultRuleSetVersionId) {
        mode.value = 'rules'
        versionId.value = source.value.defaultRuleSetVersionId
      } else {
        fields.value = fields.value.map(
          (field) => source.value?.schema.fields.find((f) => f.name === field.name) ?? field,
        )
      }
    }
    initial.value = snapshot()
  } catch (e) {
    error.value = String(e)
  } finally {
    loading.value = false
  }
}
void load()
async function save() {
  busy.value = true
  error.value = ''
  errors.value = {}
  try {
    let result: { id: string }
    if (mode.value === 'rules') {
      if (id) {
        await http.put(`/data-sources/${id}/rule-version`, { ruleSetVersionId: versionId.value })
        result = { id }
      } else
        result = (
          await http.post('/rule-sources', { name: name.value, ruleSetVersionId: versionId.value })
        ).data
    } else {
      const body = {
        name: name.value,
        acceptedFormats: formats.value,
        schema: {
          keyFields: ['externalId'],
          csvDelimiter: delimiter.value,
          fields: fields.value.filter((f) => registeredAt.value || f.name !== 'registeredAt'),
        },
      }
      result = (
        await http.request<{ id: string }>({
          method: id ? 'PUT' : 'POST',
          url: `/data-sources${id ? `/${id}` : ''}`,
          data: body,
        })
      ).data
    }
    saved.value = true
    await router.push(`/sources/${result.id}`)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
    if (e instanceof ApiError) errors.value = e.errors
    document.getElementById('source-errors')?.focus()
  } finally {
    busy.value = false
  }
}
onBeforeRouteLeave(
  () =>
    !dirty.value || window.confirm('Покинуть страницу? Несохранённые изменения будут потеряны.'),
)
function unload(event: BeforeUnloadEvent) {
  if (dirty.value) event.preventDefault()
}
window.addEventListener('beforeunload', unload)
onBeforeUnmount(() => window.removeEventListener('beforeunload', unload))
</script>
<template>
  <PageHeader
    :title="id ? 'Редактировать источник' : 'Создать источник'"
    description="Настройте приём файлов и проверку данных."
  />
  <ApiState :loading="loading" @retry="load" />
  <form v-if="!loading" class="panel operator-form" @submit.prevent="save">
    <div v-if="error" id="source-errors" tabindex="-1" class="form-error" role="alert">
      <p>{{ error }}</p>
      <ul>
        <li v-for="(messages, path) in errors" :key="path">{{ path }}: {{ messages.join(' ') }}</li>
      </ul>
    </div>
    <section class="form-section">
      <h2>Основное</h2>
      <label v-if="!id"
        >Способ настройки<select v-model="mode" class="form-control">
          <option value="customer">Клиенты — ограниченный конструктор</option>
          <option value="rules">Версия из библиотеки правил (Customer / Tabular)</option>
        </select></label
      ><label
        >Название<input
          v-model="name"
          class="form-control"
          maxlength="200"
          required
          :disabled="!!id && mode === 'rules'"
          :aria-invalid="!!errors.name"
      /></label>
    </section>
    <section class="form-section">
      <template v-if="mode === 'rules'"
        ><p v-if="id">На этой странице изменяется версия правил источника.</p>
        <label
          >Версия правил<select v-model="versionId" class="form-control" required>
            <option value="">Выберите версию</option>
            <option
              v-if="
                source?.defaultRuleSetVersionId &&
                !versions.some((v) => v.id === source?.defaultRuleSetVersionId)
              "
              :value="source.defaultRuleSetVersionId"
            >
              Текущая версия
            </option>
            <option v-for="v in versions" :key="v.id" :value="v.id">
              {{ v.name }} · v{{ v.versionNumber }}
            </option>
          </select></label
        >
        <p>
          Нужную версию можно добавить в библиотеку на странице
          <RouterLink to="/rules">«Правила»</RouterLink>.
        </p></template
      ><template v-else
        ><h2>Формат данных</h2>
        <fieldset>
          <legend>Форматы (выберите хотя бы один)</legend>
          <label v-for="format in ['Csv', 'Xlsx']" :key="format" class="check-label"
            ><input v-model="formats" type="checkbox" :value="format" />{{ format }}</label
          >
        </fieldset>
      </template>
    </section>
    <section v-if="mode !== 'rules'" class="form-section">
      <h2>Поля и правила</h2>
      <p>
        Ключ записи: externalId. Типы и обязательность основных полей фиксированы моделью Customer.
      </p>
      <label class="check-label"
        ><input v-model="registeredAt" type="checkbox" />Включить необязательное поле
        registeredAt</label
      >
      <div class="table-scroll">
        <table class="operator-table schema-table">
          <thead>
            <tr>
              <th>Поле</th>
              <th>Тип</th>
              <th>Обязательно</th>
              <th>Максимальная длина</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="(field, index) in fields.filter(
                (f) => registeredAt || f.name !== 'registeredAt',
              )"
              :key="field.name"
            >
              <td class="schema-field-name">
                {{ labels[field.name]
                }}<small
                  ><code>{{ field.name }}</code></small
                >
              </td>
              <td data-label="Тип">{{ field.type }}</td>
              <td data-label="Обязательно">{{ field.required ? 'Да' : 'Нет' }}</td>
              <td data-label="Максимальная длина / формат">
                <input
                  v-if="['string', 'email'].includes(field.type)"
                  v-model.number="field.maxLength"
                  class="form-control"
                  type="number"
                  min="1"
                  :max="field.name === 'externalId' ? 80 : field.name === 'name' ? 200 : 254"
                  :aria-label="`Максимальная длина ${field.name}`"
                  required
                /><span v-else>{{ field.format ?? '—' }}</span
                ><small v-if="errors[`schema.fields[${index}].maxLength`]" class="form-error">{{
                  errors[`schema.fields[${index}].maxLength`]?.join(' ')
                }}</small>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>
    <section v-if="mode !== 'rules'" class="form-section">
      <h2>Чтение файла</h2>
      <label
        >Разделитель CSV<input
          v-model="delimiter"
          class="form-control"
          required
          minlength="1"
          maxlength="1"
      /></label>
      <p>CSV: UTF-8. Даты: yyyy-MM-dd. Логические значения: true/false.</p>
    </section>
    <p v-if="id">
      Изменения применятся к новым импортам. Уже созданные задания используют сохранённую версию
      правил.
    </p>
    <div class="page-actions">
      <button
        class="button button-primary"
        :disabled="busy || (mode === 'customer' && !formats.length)"
      >
        {{ busy ? 'Сохранение…' : 'Сохранить' }}</button
      ><RouterLink :to="id ? `/sources/${id}` : '/sources'" class="button button-secondary"
        >Отмена</RouterLink
      >
    </div>
  </form>
</template>
