import { createContext, ReactNode, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react'
import { api, ApiError, DashboardState, OpProgress, ReviewsState } from './api'

export type View = 'board' | 'reviews' | 'archive' | 'recovery' | 'settings'

export type Dialog =
  | { type: 'op'; kind: string; req: Record<string, unknown> }
  | { type: 'palette' }

export interface Toast { id: number; title: string; body?: string; tone: 'ok' | 'warn' | 'bad' | 'info' | 'done'; url?: string }

interface AppCtx {
  state: DashboardState | null
  reviews: ReviewsState | null
  setReviews(r: ReviewsState): void
  authError: boolean
  connected: boolean
  ops: Record<string, OpProgress>
  trackOp(p: OpProgress): void
  view: View
  setView(v: View): void
  selected: string | null
  /** Opens a ticket's drawer, optionally on a given tab. */
  select(key: string | null, tab?: string): void
  drawerTab: { key: string; tab: string } | null
  dialog: Dialog | null
  openOp(kind: string, req?: Record<string, unknown>): void
  openPalette(): void
  closeDialog(): void
  toasts: Toast[]
  toast(t: Omit<Toast, 'id'>): void
  dismissToast(id: number): void
  refresh(): Promise<void>
}

const Ctx = createContext<AppCtx | null>(null)

export function useApp(): AppCtx {
  const c = useContext(Ctx)
  if (!c) throw new Error('no app context')
  return c
}

let toastSeq = 1

export function AppProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<DashboardState | null>(null)
  const [reviews, setReviews] = useState<ReviewsState | null>(null)
  const [authError, setAuthError] = useState(false)
  const [connected, setConnected] = useState(false)
  const [ops, setOps] = useState<Record<string, OpProgress>>({})
  const [view, setViewRaw] = useState<View>(() => (sessionStorage.getItem('wip.view') as View) || 'board')
  // deep link: http://127.0.0.1:5199/#PROJ-1234 opens that ticket
  const [selected, setSelectedRaw] = useState<string | null>(() => decodeURIComponent(location.hash.slice(1)) || null)
  // which drawer tab to open with the next selection (e.g. "checks" from a ticket's check chip)
  const [drawerTab, setDrawerTab] = useState<{ key: string; tab: string } | null>(null)
  const setSelected = useCallback((key: string | null, tab?: string) => {
    setSelectedRaw(key)
    setDrawerTab(key && tab ? { key, tab } : null)
    history.replaceState(null, '', key ? `#${encodeURIComponent(key)}` : location.pathname)
  }, [])
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const [toasts, setToasts] = useState<Toast[]>([])
  const timers = useRef<number[]>([])

  const setView = useCallback((v: View) => {
    setViewRaw(v)
    try { sessionStorage.setItem('wip.view', v) } catch { /* ignore */ }
  }, [])

  const toast = useCallback((t: Omit<Toast, 'id'>) => {
    const id = toastSeq++
    setToasts(ts => [...ts.slice(-4), { ...t, id }])
    timers.current.push(window.setTimeout(() => setToasts(ts => ts.filter(x => x.id !== id)), t.tone === 'bad' ? 12000 : 6000))
  }, [])

  const trackOp = useCallback((p: OpProgress) => setOps(o => ({ ...o, [p.id]: p })), [])

  // initial load + server-sent events
  useEffect(() => {
    api.state().then(setState).catch(e => { if (e instanceof ApiError && e.status === 401) setAuthError(true) })
    const es = new EventSource('/api/events')
    es.addEventListener('open', () => setConnected(true))
    es.addEventListener('error', () => {
      setConnected(false)
      api.state().catch(e => { if (e instanceof ApiError && e.status === 401) { setAuthError(true); es.close() } })
    })
    es.addEventListener('state', e => setState(JSON.parse((e as MessageEvent).data)))
    es.addEventListener('reviews', e => setReviews(JSON.parse((e as MessageEvent).data)))
    es.addEventListener('op', e => {
      const p: OpProgress = JSON.parse((e as MessageEvent).data)
      setOps(o => ({ ...o, [p.id]: p }))
    })
    es.addEventListener('notify', e => {
      const n = JSON.parse((e as MessageEvent).data) as { title: string; body: string; tone: Toast['tone']; url?: string }
      toast({ title: n.title, body: n.body, tone: n.tone, url: n.url })
      if (document.hidden && 'Notification' in window && Notification.permission === 'granted') {
        const sys = new Notification(n.title, { body: n.body, tag: n.title })
        if (n.url) sys.onclick = () => window.open(n.url, '_blank')
      }
    })
    return () => es.close()
  }, [toast])

  // tell the server the UI is visible so PR polling runs at the fast rate
  useEffect(() => {
    const ping = () => { if (!document.hidden) api.visible().catch(() => {}) }
    ping()
    const t = window.setInterval(ping, 60_000)
    document.addEventListener('visibilitychange', ping)
    return () => { window.clearInterval(t); document.removeEventListener('visibilitychange', ping) }
  }, [])

  useEffect(() => () => timers.current.forEach(clearTimeout), [])

  const refresh = useCallback(async () => {
    try { setState(await api.refresh()) } catch (e) { toast({ title: 'Refresh failed', body: String((e as Error).message), tone: 'bad' }) }
  }, [toast])

  const value = useMemo<AppCtx>(() => ({
    state, reviews, setReviews, authError, connected, ops, trackOp, view, setView, selected,
    select: setSelected,
    drawerTab,
    dialog,
    openOp: (kind, req = {}) => setDialog({ type: 'op', kind, req }),
    openPalette: () => setDialog({ type: 'palette' }),
    closeDialog: () => setDialog(null),
    toasts, toast,
    dismissToast: id => setToasts(ts => ts.filter(t => t.id !== id)),
    refresh,
  }), [state, reviews, authError, connected, ops, trackOp, view, setView, selected, drawerTab, setSelected, dialog, toasts, toast, refresh])

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>
}
