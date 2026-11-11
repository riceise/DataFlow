<script setup lang="ts">
import { computed } from 'vue'
import { number } from '@/models'

const props = defineProps<{
  inserted: number
  updated: number
  invalid: number
  duplicate: number
  valid: number
  showValid?: boolean
}>()
const results = computed(() => [
  { key: 'inserted', label: 'Добавлено', value: props.inserted },
  { key: 'updated', label: 'Обновлено', value: props.updated },
  { key: 'invalid', label: 'С ошибками', value: props.invalid },
  { key: 'duplicate', label: 'Без изменений', value: props.duplicate },
  ...(props.valid > 0 || props.showValid
    ? [{ key: 'valid', label: 'Прошли проверку', value: props.valid }]
    : []),
])
const total = computed(() => results.value.reduce((sum, result) => sum + result.value, 0))
</script>

<template>
  <div class="result-breakdown">
    <div v-if="total > 0" class="result-bar" aria-hidden="true">
      <template v-for="result in results" :key="result.key">
        <span
          v-if="result.value > 0"
          :class="`segment-${result.key}`"
          :style="{ flexGrow: result.value, flexBasis: '0%' }"
        />
      </template>
    </div>
    <dl class="breakdown-values">
      <div v-for="result in results" :key="result.key">
        <dt><i :class="`segment-${result.key}`" />{{ result.label }}</dt>
        <dd :class="{ 'text-danger': result.key === 'invalid' && result.value > 0 }">
          {{ number(result.value) }}
        </dd>
      </div>
    </dl>
  </div>
</template>
