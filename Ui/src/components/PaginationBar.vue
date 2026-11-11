<script setup lang="ts">
defineProps<{ page: number; pageSize: number; total: number }>()
defineEmits<{ change: [page: number]; size: [size: number] }>()
</script>
<template>
  <div class="pagination">
    <span
      >{{ total ? (page - 1) * pageSize + 1 : 0 }}–{{ Math.min(page * pageSize, total) }} из
      {{ total }}</span
    ><label
      >На странице
      <select
        :value="pageSize"
        @change="$emit('size', Number(($event.target as HTMLSelectElement).value))"
      >
        <option>25</option>
        <option>50</option>
        <option>100</option>
      </select></label
    ><button
      class="button button-secondary"
      :disabled="page <= 1"
      @click="$emit('change', page - 1)"
    >
      Назад</button
    ><span>{{ page }}</span
    ><button
      class="button button-secondary"
      :disabled="page * pageSize >= total"
      @click="$emit('change', page + 1)"
    >
      Вперёд
    </button>
  </div>
</template>
