<script setup lang="ts">
import { ref, watch } from 'vue'
import AppIcon from './AppIcon.vue'

const props = defineProps<{ open: boolean; title: string; wide?: boolean }>()
const emit = defineEmits<{ close: [] }>()
const dialog = ref<HTMLDialogElement>()
watch(
  () => props.open,
  (open) => {
    if (open && !dialog.value?.open) dialog.value?.showModal()
    else if (!open && dialog.value?.open) dialog.value?.close()
  },
)
</script>

<template>
  <dialog
    ref="dialog"
    class="dialog"
    :class="{ 'dialog-wide': wide }"
    aria-labelledby="dialog-title"
    @close="emit('close')"
    @click="
      (event) => {
        if (event.target === dialog) emit('close')
      }
    "
  >
    <div class="dialog-header">
      <div>
        <span class="eyebrow">DATAFLOW / ПРЕДПРОСМОТР</span>
        <h2 id="dialog-title">{{ title }}</h2>
      </div>
      <button class="icon-button" aria-label="Закрыть окно" @click="emit('close')">
        <AppIcon name="close" />
      </button>
    </div>
    <div class="dialog-content"><slot /></div>
  </dialog>
</template>
