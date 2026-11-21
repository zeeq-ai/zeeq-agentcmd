import './assets/css/main.css'

import { createApp } from 'vue'
import { createRouter, createWebHistory } from 'vue-router'
import { routes, handleHotUpdate } from 'vue-router/auto-routes'
import { createHead } from '@unhead/vue/client'
import ui from '@nuxt/ui/vue-plugin'

import App from './App.vue'
import { installScopeSync } from './composables/useScope'

const app = createApp(App)

const router = createRouter({
  routes,
  history: createWebHistory()
})

installScopeSync(router)

app.use(createHead())
app.use(router)
app.use(ui)

app.mount('#app')

if (import.meta.hot) {
  handleHotUpdate(router)
}
