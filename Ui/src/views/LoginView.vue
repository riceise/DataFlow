<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, ApiError } from '@/api'
import { useAuth } from '@/stores/auth'
const email = ref(''),
  password = ref(''),
  busy = ref(false),
  error = ref(''),
  registration = ref(false)
const auth = useAuth(),
  route = useRoute(),
  router = useRouter()
async function submit() {
  busy.value = true
  error.value = ''
  try {
    if (registration.value)
      await api('/auth/register', {
        method: 'POST',
        body: JSON.stringify({ email: email.value, password: password.value }),
      })
    auth.user = await api('/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email: email.value, password: password.value }),
    })
    password.value = ''
    const target = String(route.query.redirect ?? '/')
    await router.push(target.startsWith('/') && !target.startsWith('//') ? target : '/')
  } catch (e) {
    error.value =
      e instanceof ApiError ? [e.message, ...Object.values(e.errors).flat()].join(' ') : String(e)
  } finally {
    busy.value = false
  }
}
</script>
<template>
  <section class="panel operator-form">
    <h1>{{ registration ? 'Регистрация' : 'Вход' }}</h1>
    <p>Войдите, чтобы управлять своими источниками и импортами.</p>
    <p v-if="route.query.network" role="alert">
      Не удалось проверить сессию. Проверьте подключение к API.
    </p>
    <p v-if="error" class="form-error" role="alert">{{ error }}</p>
    <form @submit.prevent="submit">
      <label
        >Email<input
          v-model="email"
          class="form-control"
          type="email"
          autocomplete="username"
          required /></label
      ><label
        >Пароль<input
          v-model="password"
          class="form-control"
          type="password"
          :autocomplete="registration ? 'new-password' : 'current-password'"
          required
      /></label>
      <div class="page-actions">
        <button class="button button-primary" :disabled="busy">
          {{ busy ? 'Подождите…' : registration ? 'Зарегистрироваться' : 'Войти' }}</button
        ><button
          type="button"
          class="text-button"
          :disabled="busy"
          @click="registration = !registration"
        >
          {{ registration ? 'Уже есть аккаунт' : 'Создать аккаунт' }}
        </button>
      </div>
    </form>
  </section>
</template>
