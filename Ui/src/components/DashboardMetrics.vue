<script setup lang="ts">
import { computed } from 'vue'
import { number, type Summary } from '@/models'
const props = defineProps<{ summary: Summary }>()
const metrics = computed(() => [
  { label: 'Всего заданий', value: props.summary.totalJobs, note: 'За выбранный период' },
  { label: 'Без ошибок', value: props.summary.completedJobs, note: 'Обработка завершена' },
  {
    label: 'Требуют внимания',
    value: props.summary.attentionJobs,
    note: `С ошибками строк: ${number(props.summary.completedWithErrorsJobs)} · Сбои: ${number(props.summary.failedJobs)}`,
  },
  { label: 'В работе', value: props.summary.activeJobs, note: 'В очереди и на обработке' },
])
</script>

<template>
  <section class="metric-strip" aria-label="Сводка заданий">
    <div v-for="metric in metrics" :key="metric.label" class="metric">
      <span class="metric-label">{{ metric.label }}</span>
      <strong>{{ number(metric.value) }}</strong>
      <span class="metric-caption">{{ metric.note }}</span>
    </div>
  </section>
</template>
