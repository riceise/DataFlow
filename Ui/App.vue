<template>
  <div class="layout" :class="{ 'theme-dark': isDark }">
    <header class="navbar">
      <div class="logo">DataFlow</div>
      <button @click="toggleTheme" class="btn-toggle">
        {{ isDark ? 'Light' : 'Dark' }} Mode
      </button>
    </header>
    <main class="content">
      <FileUpload />
    </main>
  </div>
</template>

<script setup>
import { ref } from 'vue';
import FileUpload from './components/FileUpload.vue';

const isDark = ref(false);
const toggleTheme = () => {
  isDark.value = !isDark.value;
};
</script>

<style>
:root {
  --bg-primary: #F8FAFB;
  --surface: #FFFFFF;
  --text-main: #37494D;
  --accent: #6A8D94;
  --border: #B1C4C7;
}

.theme-dark {
  --bg-primary: #1A2024;
  --surface: #242C31;
  --text-main: #BCCCCF;
  --accent: #6A8D94;
  --border: #37494D;
}

body {
  margin: 0;
  background-color: var(--bg-primary);
  color: var(--text-main);
  font-family: 'Inter', sans-serif;
  transition: background-color 0.3s, color 0.3s;
}

.layout {
  min-height: 100vh;
}

.navbar {
  display: flex;
  justify-content: space-between;
  padding: 1rem 2rem;
  background-color: var(--surface);
  border-bottom: 1px solid var(--border);
}

.btn-toggle {
  background: var(--accent);
  color: white;
  border: none;
  padding: 0.5rem 1rem;
  border-radius: 4px;
  cursor: pointer;
}
</style>
