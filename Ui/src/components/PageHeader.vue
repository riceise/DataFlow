<script setup lang="ts">
defineProps<{ title: string; description?: string }>()
</script>

<template>
  <div class="page-heading">
    <div class="page-title">
      <h1>{{ title }}</h1>
      <p v-if="description">{{ description }}</p>
      <slot name="meta" />
    </div>
    <div v-if="$slots.default" class="page-actions"><slot /></div>
  </div>
</template>
