<script setup lang="ts">
defineProps<{ error?: string; loading?: boolean; empty?: boolean; stale?: boolean }>()
defineEmits<{ retry: [] }>()
</script>
<template>
  <div v-if="error" class="inline-note warning-note" role="alert">
    <div>
      {{ stale ? 'Связь потеряна. Показаны последние полученные данные. ' : '' }}{{ error }}
      <button class="text-button" @click="$emit('retry')">Повторить сейчас</button>
    </div>
  </div>
  <div v-else-if="loading" class="resource-loading" role="status">
    <span class="loading-mark" aria-hidden="true" />Загружаем данные…
  </div>
  <div v-else-if="empty" class="empty-state">Данных пока нет. <slot /></div>
</template>
