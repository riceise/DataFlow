<script setup lang="ts">
import { jobDate, number, processed, type ImportJob } from '@/data/dashboard'
import StatusBadge from './StatusBadge.vue'
import AppIcon from './AppIcon.vue'
defineProps<{ jobs: ImportJob[] }>()
defineEmits<{ select: [job: ImportJob] }>()
</script>

<template>
  <div class="job-table-container">
    <table class="job-table">
      <caption class="sr-only">
        Импорты: файл, источник, статус, обработанные строки, ошибки и дата создания
      </caption>
      <thead>
        <tr>
          <th scope="col" class="file-column">Файл</th>
          <th scope="col">Источник</th>
          <th scope="col">Статус</th>
          <th scope="col" class="numeric">Обработано</th>
          <th scope="col" class="numeric">Ошибки</th>
          <th scope="col" class="date-column">Создан <span aria-hidden="true">↓</span></th>
          <th scope="col"><span class="sr-only">Открыть</span></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="job in jobs" :key="job.id">
          <td class="file-column">
            <div class="file-cell">
              <span class="file-type" :class="{ 'file-type-excel': job.format === 'XLSX' }"
                ><AppIcon name="file" :size="18" /><small>{{ job.format }}</small></span
              >
              <div class="file-name">
                <button class="file-link" @click="$emit('select', job)">{{ job.file }}</button
                ><span class="job-id">{{ job.id }}</span>
              </div>
            </div>
          </td>
          <td>
            <span class="source-cell">{{ job.source }}</span>
          </td>
          <td><StatusBadge :status="job.status" /></td>
          <td class="numeric">
            <span :class="{ 'processing-number': job.status === 'processing' }">{{
              job.status === 'queued' ? '—' : number(processed(job))
            }}</span>
          </td>
          <td class="numeric">
            <button
              v-if="job.invalid"
              class="error-count"
              :aria-label="`Открыть ${number(job.invalid)} ошибок в ${job.file}`"
              @click="$emit('select', job)"
            >
              {{ number(job.invalid) }}</button
            ><span v-else class="muted">{{ job.status === 'queued' ? '—' : '0' }}</span>
          </td>
          <td class="date-column">
            <time :datetime="job.date">{{ jobDate(job.date) }}</time>
          </td>
          <td class="row-action">
            <button
              class="icon-button"
              :aria-label="`Посмотреть импорт ${job.file}`"
              @click="$emit('select', job)"
            >
              <AppIcon name="chevron" :size="16" />
            </button>
          </td>
        </tr>
        <tr v-if="!jobs.length">
          <td colspan="7" class="table-empty">По этим условиям ничего не найдено.</td>
        </tr>
      </tbody>
    </table>
  </div>
  <div class="job-mobile-list">
    <article v-for="job in jobs" :key="job.id" class="mobile-job">
      <div class="mobile-job-top">
        <span class="file-format-label">{{ job.format }}</span
        ><StatusBadge :status="job.status" />
      </div>
      <button class="file-link" @click="$emit('select', job)">
        {{ job.file }} <AppIcon name="chevron" :size="15" />
      </button>
      <p>{{ job.source }} <span>·</span> {{ jobDate(job.date) }}</p>
      <div class="mobile-job-bottom">
        <span
          >Обработано
          <strong>{{ job.status === 'queued' ? '—' : number(processed(job)) }}</strong></span
        ><span :class="{ 'text-danger': job.invalid }"
          >Ошибки <strong>{{ job.status === 'queued' ? '—' : number(job.invalid) }}</strong></span
        >
      </div>
    </article>
    <p v-if="!jobs.length" class="table-empty">По этим условиям ничего не найдено.</p>
  </div>
</template>
