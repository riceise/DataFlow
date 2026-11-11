import { createRouter, createWebHistory } from 'vue-router'
import { useAuth } from '@/stores/auth'

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    {
      path: '/login',
      meta: { title: 'Вход', public: true },
      component: () => import('@/views/LoginView.vue'),
    },
    {
      path: '/',
      name: 'overview',
      meta: { title: 'Обзор' },
      component: () => import('@/views/DashboardView.vue'),
    },
    {
      path: '/imports',
      meta: { title: 'Импорты' },
      component: () => import('@/views/ImportsView.vue'),
    },
    {
      path: '/imports/new',
      meta: { title: 'Загрузка файла' },
      component: () => import('@/views/UploadView.vue'),
    },
    {
      path: '/imports/:id',
      meta: { title: 'Детали импорта' },
      component: () => import('@/views/ImportView.vue'),
    },
    {
      path: '/sources',
      meta: { title: 'Источники' },
      component: () => import('@/views/SourcesView.vue'),
    },
    {
      path: '/sources/new',
      meta: { title: 'Создать источник' },
      component: () => import('@/views/SourceFormView.vue'),
    },
    {
      path: '/sources/:id/edit',
      meta: { title: 'Редактировать источник' },
      component: () => import('@/views/SourceFormView.vue'),
    },
    {
      path: '/sources/:id',
      meta: { title: 'Источник' },
      component: () => import('@/views/SourceView.vue'),
    },
    {
      path: '/rules',
      name: 'rules',
      meta: { title: 'Правила' },
      component: () => import('@/views/RulesView.vue'),
    },
    {
      path: '/:pathMatch(.*)*',
      meta: { title: 'Страница не найдена', public: true },
      component: () => import('@/views/NotFoundView.vue'),
    },
  ],
})

router.beforeEach(async (to) => {
  if (to.meta.public) return
  try {
    await useAuth().restore()
  } catch {
    return { path: '/login', query: { redirect: to.fullPath, network: '1' } }
  }
  if (!useAuth().user) return { path: '/login', query: { redirect: to.fullPath } }
})
router.afterEach((to) => {
  document.title = `${String(to.meta.title ?? 'DataFlow')} — DataFlow`
})

export default router
