/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_API_BASE?: string
  readonly VITE_DEMO_HINT?: string
  readonly VITE_KAFKA_UI_URL?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
