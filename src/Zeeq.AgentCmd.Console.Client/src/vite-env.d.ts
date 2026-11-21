/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Management API root. Defaults to the same-origin `/api/v1`. */
  readonly VITE_AGENTCMD_API_BASE?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
