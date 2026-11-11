import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api, ApiError } from '@/api'
export const useAuth = defineStore('auth', () => {
  const user = ref<{ id: string; email: string } | null>(null)
  let ready = false
  async function restore() {
    if (ready || user.value) return
    try {
      user.value = await api('/auth/me')
      ready = true
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) ready = true
      else throw error
    }
  }
  async function logout() {
    await api('/auth/logout', { method: 'POST' })
    user.value = null
  }
  return { user, restore, logout }
})
