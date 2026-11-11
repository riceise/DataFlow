# DataFlow Ui

Интерфейс оператора DataFlow: Vue 3 + TypeScript + Vite, Pinia, Vue Router, Axios.

Страницы: вход/регистрация, Обзор (Dashboard summary), Загрузка файла, Импорты
(список с фильтрами и пагинацией), Детали импорта (счётчики, строки, ошибки,
cancel/retry, CSV ошибок), Источники и ограниченный schema builder, Библиотека
правил (`/rules`). Типы API синхронизированы с OpenAPI (`src/api-schema.d.ts`).

## Требования

- Node `^22.18.0` или `>=24.12.0`, npm.
- Запущенный API DataFlow на `http://localhost:5030` (см. README корня).
- Авторизация — cookie-сессия (`/auth/*`); для мутаций Axios сам запрашивает
  antiforgery-токен и отправляет `X-CSRF-TOKEN`.

## Команды

```sh
npm install

# Разработка: Vite dev server с прокси /api → API
npm run dev                    # API_PROXY_TARGET=http://localhost:5100 npm run dev — другой API

# Регенерация типов из OpenAPI
npm run api:generate           # OPENAPI_URL=http://localhost:5030/openapi/v1.json

# Проверки
npm run lint                   # oxlint + eslint
npm run type-check             # vue-tsc --build
npm run build                  # type-check + vite build (в dist/)

# E2E (Playwright поднимает dev-сервер на порту 5178)
npm run test:e2e               # мок-сценарий без бэкенда (route-перехват API)
DATAFLOW_LIVE=1 npm run test:e2e   # сквозной сценарий через реальный API + Worker
```

Форматирование: `npm run format` (prettier). Настройки линтеров
— `eslint.config.ts`, `.oxlintrc.json`, `.prettierrc.json`.

## Как устроено

- `src/api.ts` — Axios instance: cookie-сессии (`withCredentials`), CSRF для
  мутаций, разбор Problem Details в `ApiError` (status/code/correlation id).
- `src/composables/useResource.ts` — загрузка + polling 2 с только для активных
  заданий, backoff до 30 с при сбоях, пауза в скрытой вкладке, отмена запросов и
  таймеров при уходе со страницы.
- `src/views/` — страницы; `src/components/` — таблицы, пагинация, бейджи статусов.
- `src/models.ts` — типы из `api-schema.d.ts` + словари статусов; `src/stores/auth.ts`
  — сессия (Pinia), восстанавливается при каждом переходе (`router/index.ts`).
- `src/styles/operator.css` + `useTheme.ts` — светлая/тёмная темы.

## E2E

- `tests/operator.spec.ts` — mocked: источник → upload → ошибки строк → CSV,
  unknown total, обрыв сети, 404 и keyboard-навигация.
- `tests/operator.live.ts` (только с `DATAFLOW_LIVE=1`): реальная регистрация,
  создание источника, fixture-импорт через Worker, фильтр Invalid, скачивание CSV,
  страница правил. Требует запущенных API, Worker и инфраструктуры.
