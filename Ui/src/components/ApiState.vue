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
  <p v-else-if="loading" role="status">Загрузка…</p>
  <div v-else-if="empty" class="empty-state">Данных пока нет. <slot /></div>
</template>
