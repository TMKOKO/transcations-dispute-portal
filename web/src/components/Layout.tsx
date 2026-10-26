import { useEffect, useRef } from 'react'
import { Link, NavLink, Navigate, Outlet, useLocation } from 'react-router'
import { useAuth } from '../auth/AuthContext'

/** Gate for every signed-in page. Remembers where the customer was going, for after login. */
export function RequireAuth() {
  const { isAuthenticated } = useAuth()
  const location = useLocation()
  if (!isAuthenticated) return <Navigate to="/login" replace state={{ from: location.pathname + location.search, fromState: location.state }} />
  return <Layout />
}

const navClass = ({ isActive }: { isActive: boolean }) =>
  `rounded-md px-3 py-2 text-sm font-medium transition-colors ${
    isActive ? 'bg-white/15 text-white' : 'text-brand-100 hover:bg-white/10 hover:text-white'
  }`

function Layout() {
  const { session, logout } = useAuth()
  const { pathname } = useLocation()
  const mainRef = useRef<HTMLElement>(null)

  // Move focus to the new page on navigation, so screen-reader and keyboard users are not left
  // on the link they just activated in a page that no longer exists.
  useEffect(() => {
    mainRef.current?.focus()
  }, [pathname])

  return (
    <div className="min-h-dvh bg-slate-50">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:left-4 focus:top-4 focus:z-50 focus:rounded focus:bg-white focus:px-3 focus:py-2">
        Skip to content
      </a>
      <header className="bg-brand-900">
        <div className="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-3 px-4 py-3">
          <Link to="/transactions" className="flex items-center gap-2 text-white">
            <img src="/favicon.svg" alt="" className="size-7" />
            <span className="font-semibold tracking-tight">Dispute Portal</span>
          </Link>
          <nav aria-label="Main" className="order-3 flex w-full gap-1 sm:order-none sm:w-auto">
            <NavLink to="/transactions" className={navClass}>
              Transactions
            </NavLink>
            <NavLink to="/disputes" className={navClass}>
              My disputes
            </NavLink>
          </nav>
          <div className="flex items-center gap-3 text-sm text-brand-100">
            <span className="hidden sm:inline">Hi, {session?.displayName}</span>
            <button type="button" onClick={() => logout()} className="rounded-md px-2 py-1 font-medium text-white hover:bg-white/10">
              Sign out
            </button>
          </div>
        </div>
      </header>
      <main id="main" ref={mainRef} tabIndex={-1} className="mx-auto max-w-5xl px-4 py-8 outline-none">
        <Outlet />
      </main>
    </div>
  )
}
