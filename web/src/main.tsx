import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router'
import { App } from './App'
import { createQueryClient } from './api/queryClient'
import './index.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <App queryClient={createQueryClient()} />
    </BrowserRouter>
  </StrictMode>,
)
