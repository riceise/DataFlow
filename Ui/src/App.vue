<script setup lang="ts">
import { RouterView } from 'vue-router'
import { ref, watch, onBeforeUnmount } from 'vue'
import { useRouter } from 'vue-router'
import { useTheme } from '@/composables/useTheme'
import { useAuth } from '@/stores/auth'
import AppIcon from '@/components/AppIcon.vue'
const { theme, setTheme } = useTheme()
const auth = useAuth(),
  router = useRouter(),
  menu = ref(false),
  error = ref('')
const links = [
  { to: '/', label: 'Обзор', icon: 'overview' },
  { to: '/imports', label: 'Импорты', icon: 'imports' },
  { to: '/sources', label: 'Источники', icon: 'sources' },
  { to: '/rules', label: 'Правила', icon: 'rules' },
]
const navigation = ref<HTMLDialogElement>()
const menuTrigger = ref<HTMLButtonElement>()
const desktop = window.matchMedia('(min-width: 1024px)')
const wide = ref(desktop.matches)
watch(menu, (open) => {
  if (open) navigation.value?.showModal?.()
  else {
    navigation.value?.close?.()
    menuTrigger.value?.focus()
  }
})
function closeOnDesktop(event: MediaQueryListEvent) {
  if (event.matches) menu.value = false
  wide.value = event.matches
}
desktop.addEventListener('change', closeOnDesktop)
onBeforeUnmount(() => desktop.removeEventListener('change', closeOnDesktop))
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
    <component
      :is="wide ? 'div' : 'dialog'"
      ref="navigation"
      class="mobile-navigation"
      aria-label="Навигация"
      @close="menu = false"
      @click="$event.target === navigation && (menu = false)"
    >
      <aside class="sidebar" aria-label="Основная навигация">
        <RouterLink to="/" class="brand" @click="menu = false"
          ><svg
            class="brand-mark"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="1.75"
            aria-hidden="true"
          >
            <path d="M3 6h18M3 12h18M3 18h18" />
            <path d="M8 3v6M16 9v6M10 15v6" /></svg
          >DataFlow</RouterLink
        >
        <button class="mobile-menu-button button" @click="menu = false">Закрыть меню</button>
        <nav class="main-navigation">
          <RouterLink
            v-for="link in links"
            :key="link.to"
            :to="link.to"
            :aria-current="
              (link.to === '/' ? $route.path === '/' : $route.path.startsWith(link.to))
                ? 'page'
                : undefined
            "
            class="nav-item"
            :class="{
              active: link.to === '/' ? $route.path === '/' : $route.path.startsWith(link.to),
            }"
            @click="menu = false"
            ><AppIcon :name="link.icon" :size="18" />{{ link.label }}</RouterLink
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
              /><AppIcon :name="mode === 'light' ? 'sun' : 'moon'" :size="15" />{{
                mode === 'light' ? 'Светлая' : 'Тёмная'
              }}</label
            >
          </fieldset>
        </div>
      </aside>
    </component>
    <div class="workspace">
      <header class="topbar">
        <button
          ref="menuTrigger"
          class="mobile-menu-button button"
          :aria-expanded="menu"
          @click="menu = !menu"
        >
          <AppIcon name="menu" :size="18" />Меню</button
        ><span class="workspace-breadcrumb"
          ><span>Рабочее пространство</span><span aria-hidden="true">/</span
          ><strong>{{ $route.meta.title }}</strong></span
        >
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
