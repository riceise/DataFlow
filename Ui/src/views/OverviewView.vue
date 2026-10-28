<script setup lang="ts">
import { computed, ref } from 'vue'
import AppIcon from '@/components/AppIcon.vue'
import DemoDialog from '@/components/DemoDialog.vue'
import JobTable from '@/components/JobTable.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import { useTheme } from '@/composables/useTheme'
import { jobs, sources, number, processed, jobDate, type ImportJob } from '@/data/dashboard'

const { theme, setTheme } = useTheme()
const navigation = ref<HTMLDialogElement>()
const period = ref('week')
const panel = ref<'imports' | 'sources' | 'upload' | 'help' | 'job' | null>(null)
const selectedJob = ref<ImportJob | null>(null)
const listFilter = ref('all')
const uploadSource = ref(sources[0]!.name)
const uploadFile = ref<File | null>(null)
const uploadError = ref('')
const dragOver = ref(false)
const fileInput = ref<HTMLInputElement>()
const prepared = ref(false)

const periodJobs = computed(() =>
  jobs.filter((job) => period.value === 'week' || job.date.startsWith('2026-10-26')),
)
const summary = computed(() => {
  const list = periodJobs.value
  return {
    total: list.length,
    completed: list.filter((job) => job.status === 'completed').length,
    attention: list.filter((job) => ['errors', 'failed'].includes(job.status)).length,
    active: list.filter((job) => ['processing', 'queued', 'retry'].includes(job.status)).length,
    inserted: list.reduce((sum, job) => sum + job.inserted, 0),
    updated: list.reduce((sum, job) => sum + job.updated, 0),
    invalid: list.reduce((sum, job) => sum + job.invalid, 0),
    duplicate: list.reduce((sum, job) => sum + job.duplicate, 0),
    processed: list.reduce((sum, job) => sum + processed(job), 0),
  }
})
const results = computed(() => [
  { label: 'Добавлено', value: summary.value.inserted, kind: 'inserted' },
  { label: 'Обновлено', value: summary.value.updated, kind: 'updated' },
  { label: 'С ошибками', value: summary.value.invalid, kind: 'invalid' },
  { label: 'Без изменений', value: summary.value.duplicate, kind: 'duplicate' },
])
const filteredJobs = computed(() =>
  periodJobs.value.filter(
    (job) =>
      listFilter.value === 'all' ||
      (listFilter.value === 'attention'
        ? ['errors', 'failed'].includes(job.status)
        : ['processing', 'queued', 'retry'].includes(job.status)),
  ),
)
const activeJob = computed(() => periodJobs.value.find((job) => job.status === 'processing'))
const panelTitle = computed(
  () =>
    ({
      imports: 'История импортов',
      sources: 'Источники данных',
      upload: 'Новый импорт',
      help: 'От файла к проверенным данным',
      job: selectedJob.value?.file ?? 'Сведения об импорте',
    })[panel.value ?? 'help'],
)

function openPanel(value: typeof panel.value, filter = 'all') {
  navigation.value?.close()
  listFilter.value = filter
  panel.value = value
  if (value === 'upload') {
    prepared.value = false
    uploadFile.value = null
    uploadError.value = ''
  }
}
function openJob(job: ImportJob) {
  selectedJob.value = job
  panel.value = 'job'
}
function chooseFile(file?: File) {
  prepared.value = false
  uploadError.value = ''
  uploadFile.value = null
  if (!file) return
  if (!/\.(csv|xlsx)$/i.test(file.name)) {
    uploadError.value = 'Выберите файл в формате CSV или XLSX.'
    return
  }
  uploadFile.value = file
}
function onFileChange(event: Event) {
  chooseFile((event.target as HTMLInputElement).files?.[0])
}
function onDrop(event: DragEvent) {
  dragOver.value = false
  if ((event.dataTransfer?.files.length ?? 0) > 1) {
    uploadError.value = 'Выберите один файл для импорта.'
    return
  }
  chooseFile(event.dataTransfer?.files[0])
}
</script>

<template>
  <a class="skip-link" href="#main-content">Перейти к содержимому</a>
  <div class="app-layout">
    <aside class="sidebar" aria-label="Боковая панель">
      <a href="/" class="brand" aria-label="DataFlow — главная">
        <span class="brand-mark"
          ><svg viewBox="0 0 32 32" fill="none" aria-hidden="true">
            <path
              d="M5 8h22M5 16h22M5 24h22"
              stroke="currentColor"
              stroke-width="1.7"
              stroke-linecap="round"
            />
            <path
              d="M12 5v6m9 2v6M10 21v6"
              stroke="currentColor"
              stroke-width="4"
              stroke-linecap="round"
            /></svg
        ></span>
        <span>Data<span class="brand-light">Flow</span></span>
      </a>
      <div class="workspace-label">УПРАВЛЕНИЕ ДАННЫМИ</div>
      <nav class="main-navigation" aria-label="Основная навигация">
        <a href="/" class="nav-item active" aria-current="page"
          ><AppIcon name="overview" :size="18" />Обзор</a
        >
        <button class="nav-item" @click="openPanel('imports')">
          <AppIcon name="imports" :size="18" />Импорты<span class="nav-count">{{
            summary.active
          }}</span>
        </button>
        <button class="nav-item" @click="openPanel('sources')">
          <AppIcon name="sources" :size="18" />Источники
        </button>
      </nav>
      <div class="sidebar-bottom">
        <div class="sidebar-note">
          <span class="sidebar-note-line" /><span
            >Хорошие данные.<br /><strong>Понятный результат.</strong></span
          >
        </div>
        <button class="help-link" @click="openPanel('help')">
          <AppIcon name="help" :size="17" />Как устроен импорт<AppIcon name="external" :size="13" />
        </button>
        <fieldset class="theme-control">
          <legend class="sr-only">Цветовая тема</legend>
          <label :class="{ selected: theme === 'light' }"
            ><input
              type="radio"
              name="theme"
              value="light"
              :checked="theme === 'light'"
              @change="setTheme('light')"
            /><AppIcon name="sun" :size="15" />Светлая</label
          >
          <label :class="{ selected: theme === 'dark' }"
            ><input
              type="radio"
              name="theme"
              value="dark"
              :checked="theme === 'dark'"
              @change="setTheme('dark')"
            /><AppIcon name="moon" :size="15" />Тёмная</label
          >
        </fieldset>
        <div class="environment">
          <span class="environment-dot" />Локальная среда<span class="version-label">v0.1</span>
        </div>
      </div>
    </aside>

    <div class="workspace">
      <header class="topbar">
        <div class="breadcrumb">
          <button
            class="icon-button mobile-menu-button"
            aria-label="Открыть меню"
            @click="navigation?.showModal()"
          >
            <AppIcon name="menu" /></button
          ><AppIcon name="overview" :size="16" /><span class="breadcrumb-root"
            >Рабочее пространство</span
          ><span class="breadcrumb-divider">/</span><span>Обзор</span>
        </div>
        <span class="demo-indicator"><span />Демонстрационные данные</span>
      </header>

      <main id="main-content" class="main-content" tabindex="-1">
        <section class="page-heading" aria-labelledby="page-title">
          <div>
            <div class="eyebrow page-eyebrow">КАЖДАЯ СТРОКА НА СВОЁМ МЕСТЕ</div>
            <h1 id="page-title">Обзор</h1>
            <p>Всё, что происходит с вашими данными.</p>
          </div>
          <div class="page-actions">
            <label class="period-control"
              ><AppIcon name="calendar" :size="17" /><span class="sr-only">Период сводки</span
              ><select v-model="period">
                <option value="week">20–26 октября 2026</option>
                <option value="today">26 октября 2026</option></select
              ><AppIcon name="down" :size="14" /></label
            ><button class="button button-primary" @click="openPanel('upload')">
              <AppIcon name="plus" :size="18" />Загрузить файл
            </button>
          </div>
        </section>

        <section class="metric-strip" aria-label="Сводка заданий за выбранный период">
          <div class="metric">
            <span class="metric-label">Всего заданий</span
            ><strong>{{ number(summary.total) }}</strong
            ><span class="metric-caption">За выбранный период</span>
          </div>
          <div class="metric">
            <span class="metric-label"
              >Завершено без ошибок<span class="metric-marker marker-success" /></span
            ><strong>{{ number(summary.completed) }}</strong
            ><span class="metric-caption">Все строки проверены</span>
          </div>
          <button
            class="metric metric-interactive"
            aria-label="Показать задания, требующие внимания"
            @click="openPanel('imports', 'attention')"
          >
            <span class="metric-label"
              >Требуют внимания<span class="metric-marker marker-warning" /></span
            ><strong>{{ number(summary.attention) }}<AppIcon name="arrow" :size="20" /></strong
            ><span class="metric-caption">Ошибки строк или сбой</span>
          </button>
          <button
            class="metric metric-interactive"
            aria-label="Показать задания в работе"
            @click="openPanel('imports', 'active')"
          >
            <span class="metric-label">В работе<span class="metric-marker marker-info" /></span
            ><strong>{{ number(summary.active) }}<AppIcon name="arrow" :size="20" /></strong
            ><span class="metric-caption">Обработка, очередь и повтор</span>
          </button>
        </section>

        <div class="summary-grid">
          <section class="results-panel panel" aria-labelledby="results-title">
            <div class="section-heading">
              <h2 id="results-title">Результаты обработки</h2>
              <span class="section-note">За выбранный период</span>
            </div>
            <div class="results-total">
              <strong>{{ number(summary.processed) }}</strong
              ><span>строк обработано</span
              ><span class="results-total-icon" aria-hidden="true"
                ><AppIcon name="check" :size="18"
              /></span>
            </div>
            <div
              class="result-bar"
              role="img"
              :aria-label="
                results.map((result) => `${result.label}: ${number(result.value)}`).join(', ')
              "
            >
              <span
                v-for="result in results"
                :key="result.kind"
                :class="`segment-${result.kind}`"
                :style="{
                  width: `${summary.processed ? (result.value / summary.processed) * 100 : 0}%`,
                }"
              />
            </div>
            <div class="result-legend">
              <div v-for="result in results" :key="result.kind">
                <span class="legend-label"
                  ><span class="legend-dot" :class="`segment-${result.kind}`" />{{
                    result.label
                  }}</span
                ><strong>{{ number(result.value) }}</strong>
              </div>
            </div>
          </section>

          <section class="active-panel panel" aria-labelledby="active-title">
            <div class="section-heading">
              <h2 id="active-title">Сейчас в работе</h2>
              <span class="live-label"><span />Демо</span>
            </div>
            <template v-if="activeJob"
              ><button class="active-file" @click="openJob(activeJob)">
                <AppIcon name="file" :size="18" /><span>{{ activeJob.file }}</span
                ><AppIcon name="chevron" :size="15" />
              </button>
              <div class="active-source">
                {{ activeJob.source }}<span>Схема v{{ activeJob.version }}</span>
              </div>
              <div class="active-progress-label">
                <strong>{{ number(processed(activeJob)) }} <span>строк</span></strong
                ><span>В обработке</span>
              </div>
              <div class="indeterminate-track" aria-hidden="true"><span /></div>
              <p class="active-explanation">
                Всего строк станет известно после чтения файла.
              </p></template
            >
            <div v-else class="active-empty">
              <AppIcon name="check" :size="24" /><span>Активной обработки нет</span>
            </div>
            <button class="active-footer" @click="openPanel('imports', 'active')">
              <span><AppIcon name="clock" :size="14" />Очередь и ожидающие повтора</span
              ><span
                >{{ periodJobs.filter((job) => ['queued', 'retry'].includes(job.status)).length
                }}<AppIcon name="arrow" :size="15"
              /></span>
            </button>
          </section>
        </div>

        <section class="recent-section panel" aria-labelledby="recent-title">
          <div class="recent-header">
            <div>
              <h2 id="recent-title">Последние импорты</h2>
              <p>Файлы, статусы и результаты — в одном месте.</p>
            </div>
            <button class="text-button" @click="openPanel('imports')">
              Все импорты<AppIcon name="arrow" :size="17" />
            </button>
          </div>
          <div v-if="summary.attention" class="attention-notice">
            <AppIcon name="alert" :size="16" />
            <p>
              <strong
                >{{ summary.attention }}
                {{
                  summary.attention === 1 ? 'задание требует' : 'задания требуют'
                }}
                внимания.</strong
              >
              Проверьте ошибки перед следующей загрузкой.
            </p>
            <button @click="openPanel('imports', 'attention')">
              Посмотреть<AppIcon name="arrow" :size="15" />
            </button>
          </div>
          <JobTable :jobs="periodJobs.slice(0, 8)" @select="openJob" />
          <div class="table-footer">
            <span
              >Показано {{ Math.min(8, periodJobs.length) }} из
              {{ periodJobs.length }} заданий</span
            ><span class="table-footer-note"
              ><span class="subtle-dot" />Синтетические данные для предпросмотра</span
            >
          </div>
        </section>

        <footer class="page-footer">
          <span class="footer-brand"
            >DataFlow<span class="footer-separator">/</span>Порядок в потоке данных</span
          ><span
            >CSV и XLSX<span class="footer-separator">·</span>Один файл — понятный результат</span
          >
        </footer>
      </main>
    </div>
  </div>

  <dialog ref="navigation" class="navigation-dialog" aria-label="Навигация">
    <div class="mobile-nav-header">
      <strong>DataFlow</strong
      ><button class="icon-button" aria-label="Закрыть меню" @click="navigation?.close()">
        <AppIcon name="close" />
      </button>
    </div>
    <nav class="main-navigation" aria-label="Мобильная навигация">
      <button class="nav-item active" @click="navigation?.close()">
        <AppIcon name="overview" :size="18" />Обзор</button
      ><button class="nav-item" @click="openPanel('imports')">
        <AppIcon name="imports" :size="18" />Импорты</button
      ><button class="nav-item" @click="openPanel('sources')">
        <AppIcon name="sources" :size="18" />Источники
      </button>
    </nav>
    <fieldset class="theme-control">
      <legend class="sr-only">Цветовая тема</legend>
      <label :class="{ selected: theme === 'light' }"
        ><input
          type="radio"
          name="mobile-theme"
          :checked="theme === 'light'"
          @change="setTheme('light')"
        /><AppIcon name="sun" :size="16" />Светлая</label
      ><label :class="{ selected: theme === 'dark' }"
        ><input
          type="radio"
          name="mobile-theme"
          :checked="theme === 'dark'"
          @change="setTheme('dark')"
        /><AppIcon name="moon" :size="16" />Тёмная</label
      >
    </fieldset>
    <p class="mobile-nav-note">Демонстрационная версия интерфейса</p>
  </dialog>

  <DemoDialog
    :open="panel !== null"
    :title="panelTitle"
    :wide="panel === 'imports' || panel === 'job'"
    @close="panel = null"
  >
    <template v-if="panel === 'imports'"
      ><div class="dialog-toolbar">
        <p>Задания за {{ period === 'week' ? '20–26 октября' : '26 октября' }} 2026</p>
        <label class="filter-select"
          ><span class="sr-only">Фильтр заданий</span
          ><select v-model="listFilter">
            <option value="all">Все задания</option>
            <option value="attention">Требуют внимания</option>
            <option value="active">В работе</option>
          </select></label
        >
      </div>
      <JobTable :jobs="filteredJobs" @select="openJob" />
      <p class="demo-footnote">
        Демонстрационные данные. Статусы не обновляются с сервера.
      </p></template
    >

    <template v-if="panel === 'sources'"
      ><p class="dialog-intro">
        Источники определяют, какие файлы принимать и как проверять каждую строку.
      </p>
      <div v-for="source in sources" :key="source.name" class="source-preview">
        <span class="source-preview-icon"><AppIcon name="sources" :size="21" /></span>
        <div>
          <h3>{{ source.name }}</h3>
          <p>{{ source.description }}</p>
          <span class="source-meta"
            >{{ source.formats }}<span>·</span>Схема v{{ source.version
            }}<span>·</span>Клиенты</span
          >
        </div>
        <span class="source-active">Активен</span>
      </div>
      <div class="inline-note">
        <AppIcon name="info" :size="18" />
        <p>
          Это предпросмотр. Создание и настройка источников будут доступны после подключения API.
        </p>
      </div></template
    >

    <template v-if="panel === 'upload'"
      ><p class="dialog-intro">Выберите источник и подготовьте файл для импорта.</p>
      <form @submit.prevent="prepared = true">
        <label class="field-label" for="upload-source">Источник данных</label
        ><select id="upload-source" v-model="uploadSource" class="form-control">
          <option v-for="source in sources" :key="source.name">{{ source.name }}</option>
        </select>
        <div class="upload-rules">
          <span class="schema-tag"
            >Схема v{{ sources.find((source) => source.name === uploadSource)?.version }}</span
          ><span>UTF-8 · заголовки в первой строке</span>
        </div>
        <div
          class="dropzone"
          :class="{ 'drag-over': dragOver, 'has-file': uploadFile }"
          @dragover.prevent="dragOver = true"
          @dragleave.prevent="dragOver = false"
          @drop.prevent="onDrop"
        >
          <span class="upload-symbol"
            ><AppIcon :name="uploadFile ? 'file' : 'upload'" :size="26" /></span
          ><strong>{{ uploadFile?.name ?? 'Перетащите CSV или XLSX' }}</strong>
          <p v-if="uploadFile">
            {{
              new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 1 }).format(
                uploadFile.size / 1024,
              )
            }}
            КБ · файл выбран
          </p>
          <p v-else>Один файл — один импорт</p>
          <button type="button" class="button button-secondary" @click="fileInput?.click()">
            {{ uploadFile ? 'Заменить файл' : 'Выбрать файл' }}</button
          ><input
            ref="fileInput"
            type="file"
            class="sr-only"
            accept=".csv,.xlsx"
            aria-label="Выбрать файл CSV или XLSX"
            @change="onFileChange"
          />
        </div>
        <p v-if="uploadError" class="form-error" role="alert">{{ uploadError }}</p>
        <div class="inline-note">
          <AppIcon name="info" :size="18" />
          <p>
            Режим предпросмотра: выбранный файл остаётся на вашем устройстве. Отправка и обработка
            ещё не подключены.
          </p>
        </div>
        <p v-if="prepared" class="prepared-message" role="status">
          <AppIcon name="check" :size="18" />Файл выбран. Интерфейс готов к подключению загрузки.
        </p>
        <div class="dialog-actions">
          <button type="button" class="button button-secondary" @click="panel = null">
            Закрыть</button
          ><button type="submit" class="button button-primary" :disabled="!uploadFile">
            <AppIcon name="check" :size="17" />Проверить выбор
          </button>
        </div>
      </form></template
    >

    <template v-if="panel === 'job' && selectedJob"
      ><div class="job-preview-top">
        <StatusBadge :status="selectedJob.status" /><span class="job-id">{{ selectedJob.id }}</span>
      </div>
      <div class="job-preview-meta">
        {{ selectedJob.source }}<span>·</span>Схема v{{ selectedJob.version }}<span>·</span
        >{{ jobDate(selectedJob.date) }}
      </div>
      <div class="job-preview-progress">
        <strong>{{ number(processed(selectedJob)) }}</strong
        ><span>строк обработано</span>
      </div>
      <p v-if="selectedJob.status === 'processing'" class="muted">
        Общее число строк определится после чтения файла.
      </p>
      <div class="job-preview-results">
        <div>
          <span>Добавлено</span><strong>{{ number(selectedJob.inserted) }}</strong>
        </div>
        <div>
          <span>Обновлено</span><strong>{{ number(selectedJob.updated) }}</strong>
        </div>
        <div>
          <span>С ошибками</span
          ><strong :class="{ 'text-danger': selectedJob.invalid }">{{
            number(selectedJob.invalid)
          }}</strong>
        </div>
        <div>
          <span>Без изменений</span><strong>{{ number(selectedJob.duplicate) }}</strong>
        </div>
      </div>
      <div v-if="selectedJob.invalid" class="example-error">
        <div class="example-error-title">
          <AppIcon name="alert" :size="18" /><strong>Пример ошибки строки</strong
          ><span>Строка 24</span>
        </div>
        <p><code>email</code><span class="error-value">anna@</span></p>
        <p>Укажите полный email, например name@example.com.</p>
        <code class="muted">invalid_email</code>
      </div>
      <div v-if="selectedJob.status === 'failed'" class="inline-note warning-note">
        <AppIcon name="alert" :size="18" />
        <p>
          Обработка остановлена. {{ number(processed(selectedJob)) }} сохранённых результатов
          остаются в базе. В работающем приложении здесь появятся диагностика и допустимые действия.
        </p>
      </div>
      <p class="demo-footnote">Пример задания. Счётчики и сведения демонстрационные.</p></template
    >

    <template v-if="panel === 'help'"
      ><p class="dialog-intro">
        DataFlow принимает файл, проверяет строки по схеме источника и показывает результат
        обработки.
      </p>
      <ol class="help-steps">
        <li>
          <span>01</span>
          <div>
            <h3>Настройте источник</h3>
            <p>
              Задайте поля, обязательность и ключ записи. Каждое задание сохранит свою версию схемы.
            </p>
          </div>
        </li>
        <li>
          <span>02</span>
          <div>
            <h3>Загрузите файл</h3>
            <p>
              CSV с заголовками или XLSX с данными на первом листе. Обработка выполняется в фоне.
            </p>
          </div>
        </li>
        <li>
          <span>03</span>
          <div>
            <h3>Проверьте результат</h3>
            <p>
              Новые записи добавляются, изменённые обновляются. Совпадающие данные остаются без
              изменений.
            </p>
          </div>
        </li>
        <li>
          <span>04</span>
          <div>
            <h3>Исправьте ошибки</h3>
            <p>Скачайте отчёт, исправьте исходный файл и загрузите его снова в тот же источник.</p>
          </div>
        </li>
      </ol>
      <div class="inline-note">
        <AppIcon name="info" :size="18" />
        <p>Отмена останавливает дальнейшую обработку. Уже сохранённые записи остаются.</p>
      </div></template
    >
  </DemoDialog>
</template>
