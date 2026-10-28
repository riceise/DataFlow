<script setup lang="ts">
withDefaults(defineProps<{ name: string; size?: number }>(), { size: 20 })

const paths: Record<string, string> = {
  overview: 'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z',
  imports: 'M4 4h16v16H4z M4 9h16 M9 9v11',
  sources: 'M4 6c0-4 16-4 16 0s-16 4-16 0 M4 6v6c0 4 16 4 16 0V6 M4 12v6c0 4 16 4 16 0v-6',
  arrow: 'M5 12h14 M14 7l5 5-5 5',
  chevron: 'M9 5l7 7-7 7',
  down: 'M6 9l6 6 6-6',
  upload: 'M12 16V3 M7 8l5-5 5 5 M4 15v5h16v-5',
  plus: 'M12 5v14 M5 12h14',
  sun: 'M12 3V1 M12 23v-2 M3 12H1 M23 12h-2 M4.2 4.2l1.4 1.4 M18.4 18.4l1.4 1.4 M4.2 19.8l1.4-1.4 M18.4 5.6l1.4-1.4 M16 12a4 4 0 1 1-8 0 4 4 0 0 1 8 0',
  moon: 'M20.8 13.2A9 9 0 0 1 10.8 3 9 9 0 1 0 20.8 13.2Z',
  calendar: 'M5 5h14a1 1 0 0 1 1 1v14H4V6a1 1 0 0 1 1-1Z M8 3v4 M16 3v4 M4 10h16 M8 14h2 M14 14h2',
  check: 'M5 12l4 4L19 6',
  alert: 'M12 3 2 21h20L12 3Z M12 9v5 M12 17v.5',
  info: 'M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0 M12 11v6 M12 7v.5',
  clock: 'M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0 M12 7v5l3 2',
  close: 'M6 6l12 12 M18 6 6 18',
  file: 'M14 2H5v20h14V7l-5-5Z M14 2v6h5 M8 12h8 M8 16h6',
  download: 'M12 3v13 M7 11l5 5 5-5 M4 17v4h16v-4',
  external: 'M14 3h7v7 M21 3 10 14 M10 3H3v18h18v-7',
  menu: 'M4 6h16 M4 12h16 M4 18h16',
  help: 'M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0 M9.5 9a2.5 2.5 0 1 1 4 2c-1 .6-1.5 1-1.5 2 M12 17v.5',
  activity: 'M2 12h5l3-8 4 16 3-8h5',
  retry: 'M3 10a9 9 0 1 1 2 8 M3 4v6h6',
  stop: 'M6 6h12v12H6z',
}
</script>

<template>
  <svg
    :width="size"
    :height="size"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="1.65"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
  >
    <path :d="paths[name] || paths.info" />
  </svg>
</template>
