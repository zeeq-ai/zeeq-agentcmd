import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import vueRouter from 'vue-router/vite'
import ui from '@nuxt/ui/vite'

// The dev server proxies /api to the management API so the browser keeps one origin.
// A same-origin session cookie or a trusted reverse proxy then authenticates calls;
// the console never holds credentials itself.
const apiTarget = process.env.AGENTCMD_API_PROXY ?? 'http://localhost:5180'

export default defineConfig({
  plugins: [
    vueRouter({
      dts: 'src/route-map.d.ts'
    }),
    vue(),
    ui({
      ui: {
        colors: {
          primary: 'green',
          neutral: 'zinc'
        },
        // Operator density: smaller panel padding and gaps than the dashboard template default.
        dashboardPanel: {
          slots: { body: 'flex flex-col gap-3 flex-1 overflow-y-auto p-3' }
        },
        table: {
          slots: { th: 'px-2 py-1.5 text-xs', td: 'px-2 py-1.5 text-sm' }
        }
      }
    })
  ],
  server: {
    proxy: {
      '/api': { target: apiTarget, changeOrigin: false }
    }
  }
})
