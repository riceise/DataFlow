<script setup lang="ts">
import { ref } from 'vue'
import { useRoute } from 'vue-router'
import { http } from '@/api'
import type { Source } from '@/models'
import { useResource } from '@/composables/useResource'
import ApiState from '@/components/ApiState.vue'
const id = String(useRoute().params.id),
  definition = ref<unknown>(),
  busy = ref(false),
  actionError = ref(''),
  dialog = ref<HTMLDialogElement>()
const { data, error, loading, refresh } = useResource(async (signal) => {
  const source = (await http.get<Source>(`/data-sources/${id}`, { signal })).data
  if (source.defaultRuleSetVersionId)
    definition.value = (
      await http.get(`/rule-set-versions/${source.defaultRuleSetVersionId}`, { signal })
    ).data.definition
  return source
})
void refresh()
async function toggle() {
  busy.value = true
  actionError.value = ''
  try {
    await http.post(`/data-sources/${id}/${data.value?.isActive ? 'deactivate' : 'activate'}`)
    dialog.value?.close()
    await refresh()
  } catch (e) {
    actionError.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = false
  }
}
</script>
<template>
  <RouterLink to="/sources" class="text-button">← Источники</RouterLink
  ><ApiState :error="error" :loading="loading && !data" @retry="refresh" /><template v-if="data"
    ><div class="page-heading">
      <div>
        <h1>{{ data.name }}</h1>
        <p>
          <span class="live-badge" :class="{ good: data.isActive }">{{
            data.isActive ? 'Активен' : 'Неактивен'
          }}</span>
          · {{ data.dataType === 'Customer' ? 'Клиенты' : 'Табличные данные' }} ·
          {{ data.acceptedFormats.join(', ').toUpperCase() }}
        </p>
      </div>
      <div class="page-actions">
        <RouterLink
          v-if="data.isActive"
          :to="{ path: '/imports/new', query: { sourceId: id } }"
          class="button button-primary"
          >Загрузить файл</RouterLink
        ><RouterLink :to="`/sources/${id}/edit`" class="button button-secondary"
          >Редактировать</RouterLink
        ><button
          class="text-button"
          :disabled="busy"
          @click="data.isActive ? dialog?.showModal() : toggle()"
        >
          {{ data.isActive ? 'Деактивировать' : 'Активировать' }}
        </button>
      </div>
    </div>
    <p v-if="!data.isActive">Новые загрузки недоступны, пока источник не активирован.</p>
    <p v-if="actionError" role="alert" class="form-error">{{ actionError }}</p>
    <section class="panel operator-panel">
      <h2>Правила проверки</h2>
      <template v-if="definition">
        <pre>{{ JSON.stringify(definition, null, 2) }}</pre>
        <RouterLink to="/rules">Открыть библиотеку правил</RouterLink></template
      ><template v-else
        ><p class="source-schema-meta">
          Схема v{{ data.schemaVersion }} · Ключ: {{ data.schema.keyFields.join(', ') }} ·
          Разделитель: <code>{{ data.schema.csvDelimiter }}</code>
        </p>
        <div class="table-scroll">
          <table class="operator-table">
            <thead>
              <tr>
                <th>Поле</th>
                <th>Тип</th>
                <th>Обязательное</th>
                <th>Макс. длина</th>
                <th>Формат</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="field in data.schema.fields" :key="field.name">
                <td>
                  <code>{{ field.name }}</code>
                </td>
                <td>{{ field.type }}</td>
                <td>{{ field.required ? 'Да' : 'Нет' }}</td>
                <td>{{ field.maxLength ?? '—' }}</td>
                <td>{{ field.format ?? '—' }}</td>
              </tr>
            </tbody>
          </table>
        </div></template
      >
    </section>
    <RouterLink :to="{ path: '/imports', query: { sourceId: id } }" class="button button-secondary"
      >Импорты этого источника →</RouterLink
    ></template
  >
  <dialog ref="dialog" class="operator-dialog">
    <h2>Деактивировать источник?</h2>
    <p>
      Новые загрузки будут запрещены. Уже созданные задания продолжат обработку со своими правилами.
    </p>
    <p v-if="actionError" role="alert">{{ actionError }}</p>
    <div class="page-actions">
      <button autofocus class="button button-secondary" :disabled="busy" @click="dialog?.close()">
        Вернуться</button
      ><button class="button button-primary" :disabled="busy" @click="toggle">
        Деактивировать
      </button>
    </div>
  </dialog>
</template>
