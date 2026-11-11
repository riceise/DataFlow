<script setup lang="ts">
import { statuses } from '@/models'
defineProps<{ status: string }>()
</script>
<template>
  <span
    class="live-badge"
    :class="{
      bad: ['Failed', 'Invalid'].includes(status),
      good: ['Completed', 'Valid', 'Inserted'].includes(status),
      warn: ['CompletedWithErrors', 'RetryScheduled'].includes(status),
    }"
    >{{ statuses[status] ?? 'Неизвестный статус' }}</span
  >
</template>
