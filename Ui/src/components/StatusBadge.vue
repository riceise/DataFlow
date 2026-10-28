<script setup lang="ts">
import { statusLabels, type JobStatus } from '@/data/dashboard'
import AppIcon from './AppIcon.vue'
defineProps<{ status: JobStatus }>()
</script>

<template>
  <span class="status-badge" :class="`status-${status}`">
    <span v-if="status === 'processing' || status === 'queued'" class="status-dot" />
    <AppIcon
      v-else
      :name="status === 'completed' ? 'check' : status === 'retry' ? 'clock' : 'alert'"
      :size="13"
    />
    {{ statusLabels[status] }}
  </span>
</template>
