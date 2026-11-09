import { createRouter, createWebHistory } from 'vue-router'

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    { path: '/', name: 'overview', component: () => import('@/views/OverviewView.vue') },
    { path: '/rules', name: 'rules', component: () => import('@/views/RulesView.vue') },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
})

export default router
