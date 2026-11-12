<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Compartment, EditorState } from '@codemirror/state'
import {
  EditorView,
  drawSelection,
  highlightActiveLine,
  keymap,
  lineNumbers,
} from '@codemirror/view'
import { defaultKeymap, history, historyKeymap } from '@codemirror/commands'
import { json } from '@codemirror/lang-json'
import { defaultHighlightStyle, syntaxHighlighting } from '@codemirror/language'
import { lintGutter, setDiagnostics } from '@codemirror/lint'
import type { DefinitionDiagnostic } from '@/utils/ruleDefinitionDiagnostics'

const props = defineProps<{
  modelValue: string
  diagnostics: DefinitionDiagnostic[]
  disabled: boolean
}>()
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()
const host = ref<HTMLDivElement>()
const editable = new Compartment()
let view: EditorView | undefined

function updateDiagnostics() {
  if (!view) return
  view.dispatch(
    setDiagnostics(
      view.state,
      props.diagnostics.map((issue) => ({
        from: issue.from,
        to: issue.to,
        severity: 'error' as const,
        message: `${issue.message} ${issue.hint}`,
      })),
    ),
  )
}
function focusIssue(issue: DefinitionDiagnostic) {
  if (!view) return
  view.dispatch({ selection: { anchor: issue.from, head: issue.to }, scrollIntoView: true })
  view.focus()
}
defineExpose({ focusIssue })

onMounted(() => {
  view = new EditorView({
    parent: host.value,
    state: EditorState.create({
      doc: props.modelValue,
      extensions: [
        lineNumbers(),
        lintGutter(),
        history(),
        drawSelection(),
        highlightActiveLine(),
        json(),
        syntaxHighlighting(defaultHighlightStyle),
        EditorView.lineWrapping,
        keymap.of([...defaultKeymap, ...historyKeymap]),
        editable.of(EditorState.readOnly.of(props.disabled)),
        EditorView.contentAttributes.of({ 'aria-label': 'JSON правил', spellcheck: 'false' }),
        EditorView.updateListener.of((update) => {
          if (update.docChanged) emit('update:modelValue', update.state.doc.toString())
        }),
        EditorView.theme({
          '&': { backgroundColor: 'var(--bg-surface)', color: 'var(--text-primary)' },
          '&.cm-focused': { outline: '2px solid var(--accent)' },
          '.cm-scroller': {
            fontFamily: "'IBM Plex Mono', Consolas, monospace",
            fontSize: '12px',
            lineHeight: '20px',
            overflow: 'auto',
            maxHeight: '600px',
            minHeight: '340px',
          },
          '.cm-gutters': {
            backgroundColor: 'var(--bg-surface)',
            color: 'var(--text-primary)',
            borderColor: 'var(--border-control)',
          },
          '.cm-activeLine, .cm-activeLineGutter': { backgroundColor: 'var(--bg-selected)' },
          '.cm-cursor': { borderLeftColor: 'var(--text-primary)' },
          '.cm-tooltip': {
            backgroundColor: 'var(--bg-surface)',
            color: 'var(--text-primary)',
            borderColor: 'var(--border-control)',
          },
          '.cm-diagnostic-error': { borderLeftColor: 'var(--danger)' },
        }),
      ],
    }),
  })
  updateDiagnostics()
})
watch(
  () => props.modelValue,
  (value) => {
    if (view && value !== view.state.doc.toString())
      view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: value } })
  },
)
watch(() => props.diagnostics, updateDiagnostics, { flush: 'post' })
watch(
  () => props.disabled,
  (value) => view?.dispatch({ effects: editable.reconfigure(EditorState.readOnly.of(value)) }),
)
onBeforeUnmount(() => view?.destroy())
</script>

<template>
  <div ref="host" class="definition-editor" />
</template>

<style scoped>
.definition-editor {
  min-width: 0;
  border: 1px solid var(--border-control);
  border-radius: 6px;
  overflow: hidden;
}
</style>
