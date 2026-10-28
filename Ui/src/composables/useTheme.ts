import { onMounted, onUnmounted, ref } from 'vue'

type Theme = 'light' | 'dark'

export function useTheme() {
  const theme = ref<Theme>(document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light')
  const preference = window.matchMedia('(prefers-color-scheme: dark)')
  let explicitChoice = false
  try {
    explicitChoice = localStorage.getItem('dataflow.theme') !== null
  } catch {
    /* Storage is optional. */
  }

  function setTheme(value: Theme, persist = true) {
    theme.value = value
    document.documentElement.dataset.theme = value
    if (persist) {
      explicitChoice = true
      try {
        localStorage.setItem('dataflow.theme', value)
      } catch {
        /* Keep in-memory choice. */
      }
    }
  }

  const onSystemChange = (event: MediaQueryListEvent) => {
    if (!explicitChoice) setTheme(event.matches ? 'dark' : 'light', false)
  }
  onMounted(() => preference.addEventListener('change', onSystemChange))
  onUnmounted(() => preference.removeEventListener('change', onSystemChange))

  return { theme, setTheme }
}
