import { onScopeDispose, ref, shallowRef } from 'vue'
import axios from 'axios'

// One request at a time; stale responses never replace the current route's data.
export function useResource<T>(
  load: (signal: AbortSignal) => Promise<T>,
  isActive: (data: T) => boolean = () => false,
  enabled: () => boolean = () => true,
) {
  const data = shallowRef<T>()
  const error = ref(''),
    loading = ref(false),
    updated = ref('')
  let controller: AbortController | undefined, timer: ReturnType<typeof setTimeout> | undefined
  let disposed = false,
    failures = 0
  async function refresh() {
    if (!enabled()) return
    clearTimeout(timer)
    controller?.abort()
    const current = new AbortController()
    controller = current
    loading.value = true
    try {
      const result = await load(current.signal)
      if (disposed || current.signal.aborted) return
      data.value = result
      error.value = ''
      failures = 0
      updated.value = new Date().toLocaleTimeString('ru-RU')
    } catch (e) {
      if (current.signal.aborted || axios.isCancel(e) || disposed) return
      error.value = e instanceof Error ? e.message : String(e)
      failures++
    } finally {
      if (!disposed && !current.signal.aborted) {
        loading.value = false
        if (!document.hidden && (failures > 0 || (data.value && isActive(data.value))))
          timer = setTimeout(
            () => void refresh(),
            failures ? Math.min(30000, 2000 * 2 ** failures) : 2000,
          )
      }
    }
  }
  function visibility() {
    clearTimeout(timer)
    if (!document.hidden) void refresh()
    else controller?.abort()
  }
  document.addEventListener('visibilitychange', visibility)
  onScopeDispose(() => {
    disposed = true
    clearTimeout(timer)
    controller?.abort()
    document.removeEventListener('visibilitychange', visibility)
  })
  return { data, error, loading, updated, refresh }
}
