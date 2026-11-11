<script setup lang="ts">
import { RouterView } from 'vue-router'
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { useTheme } from '@/composables/useTheme'
import { useAuth } from '@/stores/auth'
const { theme, setTheme } = useTheme()
const auth = useAuth(),
  router = useRouter(),
  menu = ref(false),
  error = ref('')
const links = [
  { to: '/', label: 'Обзор' },
  { to: '/imports', label: 'Импорты' },
  { to: '/sources', label: 'Источники' },
  { to: '/rules', label: 'Правила' },
]
async function logout() {
  try {
    await auth.logout()
    await router.push('/login')
  } catch (e) {
    error.value = String(e)
  }
}
</script>

<template>
  <a class="skip-link" href="#main-content">Перейти к содержимому</a>
  <div class="app-layout operator-shell">
    <aside class="sidebar" :class="{ 'mobile-open': menu }" aria-label="Основная навигация">
      <RouterLink to="/" class="brand" @click="menu = false">DataFlow</RouterLink>
      <button class="mobile-menu-button button" @click="menu = false">Закрыть меню</button>
      <nav class="main-navigation">
        <RouterLink
          v-for="link in links"
          :key="link.to"
          :to="link.to"
          class="nav-item"
          :class="{
            active: link.to === '/' ? $route.path === '/' : $route.path.startsWith(link.to),
          }"
          @click="menu = false"
          >{{ link.label }}</RouterLink
        >
      </nav>
      <div class="sidebar-bottom">
        <fieldset class="theme-control">
          <legend class="sr-only">Цветовая тема</legend>
          <label
            v-for="mode in ['light', 'dark'] as const"
            :key="mode"
            :class="{ selected: theme === mode }"
            ><input
              type="radio"
              name="theme"
              :checked="theme === mode"
              @change="setTheme(mode)"
            />{{ mode === 'light' ? 'Светлая' : 'Тёмная' }}</label
          >
        </fieldset>
      </div>
    </aside>
    <div class="workspace">
      <header class="topbar">
        <button class="mobile-menu-button button" :aria-expanded="menu" @click="menu = !menu">
          Меню</button
        ><span>Рабочее пространство / {{ $route.meta.title }}</span>
        <div class="account">
          <span v-if="auth.user">{{ auth.user.email }}</span
          ><button v-if="auth.user" class="text-button" @click="logout">Выйти</button
          ><RouterLink v-else to="/login">Войти</RouterLink>
        </div>
      </header>
      <main id="main-content" class="main-content" tabindex="-1">
        <p v-if="error" role="alert">{{ error }}</p>
        <RouterView v-slot="{ Component }"
          ><component :is="Component" :key="$route.path"
        /></RouterView>
      </main>
    </div>
  </div>
</template>
