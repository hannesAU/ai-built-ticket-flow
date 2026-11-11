import { useEffect } from 'react'
import { api, timeAgo } from './api'
import { Board, ticketAction } from './Board'
import { TicketDrawer } from './Drawer'
import { OpDialog } from './OpDialog'
import { Palette } from './Palette'
import { useApp, View } from './store'
import { Icon, Kbd, StatusDot } from './ui'
import { ArchiveView, RecoveryView, SettingsView } from './Views'
import { ReviewsView } from './Reviews'

const views: [View, string, string][] = [
  ['board', 'Board', 'board'],
  ['reviews', 'PR reviews', 'pr'],
  ['archive', 'Archive', 'archive'],
  ['recovery', 'Recovery', 'life'],
  ['settings', 'Settings', 'gear'],
]

export function App() {
  const app = useApp()
  useShortcuts()

  if (app.authError)
    return (
      <div className="center-screen">
        <Icon name="lock" size={32} />
        <h1>Open Ticket Flow from its link</h1>
        <p className="muted">For safety the API only answers this browser once it has visited the sign-in link the server prints on start
          (<span className="mono">http://127.0.0.1:5199/auth?t=…</span>). Restart Ticket Flow or copy the link from its console window.</p>
      </div>
    )
  if (!app.state)
    return <div className="center-screen"><span className="spinner big" /><p className="muted">Scanning repositories…</p></div>

  const s = app.state
  const showDrawer = app.view === 'board' && app.selected
  const reviewCount = app.reviews?.repos.reduce((n, r) => n + r.prs.length, 0) ?? 0
  return (
    <div className={`app ${showDrawer ? 'with-drawer' : ''} ${s.runningOp && !app.dialog ? 'with-banner' : ''}`}>
      <header className="topbar">
        <div className="brand"><span className="logo">T</span> Ticket Flow</div>
        <nav className="nav">
          {views.map(([id, label, icon]) => (
            <button key={id} className={`nav-btn ${app.view === id ? 'active' : ''}`} onClick={() => app.setView(id)}>
              <Icon name={icon} size={14} /> {label}
              {id === 'board' && s.unassigned.length > 0 && <span className="nav-alert" title="Unassigned changes">{s.unassigned.length}</span>}
              {id === 'reviews' && app.reviews?.running && <span className="spinner small" title={`Reviewing ${app.reviews.running.pr}`} />}
              {id === 'reviews' && !app.reviews?.running && reviewCount > 0 && <span className="nav-count">{reviewCount}</span>}
              {id === 'archive' && s.archiveCount > 0 && <span className="nav-count">{s.archiveCount}</span>}
            </button>
          ))}
        </nav>
        <div className="top-right">
          <span className="gh" title={s.gitHub.error ?? `PR status updated ${timeAgo(s.gitHub.lastPoll)}`}>
            <StatusDot tone={!app.connected ? 'warn' : s.gitHub.ok ? 'ok' : 'bad'} />
            {!app.connected ? 'reconnecting…' : s.gitHub.ok ? (s.gitHub.user ?? 'GitHub') : 'GitHub error'}
            <span className="muted small"> · PRs {timeAgo(s.gitHub.lastPoll)}</span>
          </span>
          <button className="btn ghost small" onClick={() => app.openOp('sync', {})} title="Fetch every repo and fast-forward default branches">
            <Icon name="arrowDown" size={14} /> Fetch all
          </button>
          <button className="icon-btn" onClick={() => app.refresh()} title="Rescan + refresh PR status"><Icon name="refresh" /></button>
          <button className="btn ghost small" onClick={() => app.openPalette()}><Icon name="search" size={14} /> <Kbd>Ctrl K</Kbd></button>
        </div>
      </header>

      {s.runningOp && !app.dialog && (
        <div className="running-banner">
          <span className="spinner" /> {s.runningOp.title} — running…
          {s.runningOp.kind === 'check' && <span className="muted small">other actions wait until it finishes</span>}
          <span className="grow" />
          {s.runningOp.key && s.runningOp.kind === 'check' && (
            <button className="btn small ghost" onClick={() => { app.setView('board'); app.select(s.runningOp!.key!, 'checks') }}>Show</button>
          )}
          {s.runningOp.cancellable && (
            <button className="btn small ghost danger" onClick={() => api.cancelOp().catch(e => app.toast({ title: 'Could not cancel', body: (e as Error).message, tone: 'bad' }))}>
              <Icon name="stop" size={13} /> Cancel
            </button>
          )}
        </div>
      )}

      <main className="main">
        {app.view === 'board' && <Board />}
        {app.view === 'reviews' && <ReviewsView />}
        {app.view === 'archive' && <ArchiveView />}
        {app.view === 'recovery' && <RecoveryView />}
        {app.view === 'settings' && <SettingsView />}
      </main>
      {showDrawer && <TicketDrawer ticketKey={app.selected!} />}

      {app.dialog?.type === 'op' && <OpDialog key={JSON.stringify(app.dialog)} kind={app.dialog.kind} initial={app.dialog.req} />}
      {app.dialog?.type === 'palette' && <Palette />}

      <div className="toasts" aria-live="polite">
        {app.toasts.map(t => (
          <div key={t.id} className={`toast tone-${t.tone}`} onClick={() => { if (t.url) window.open(t.url, '_blank'); app.dismissToast(t.id) }}>
            <StatusDot tone={t.tone} />
            <div>
              <div className="toast-title">{t.title}</div>
              {t.body && <div className="toast-body">{t.body}</div>}
            </div>
          </div>
        ))}
      </div>

      {app.view === 'board' && (
        <footer className="hints">
          <Kbd>j</Kbd>/<Kbd>k</Kbd> move · <Kbd>Enter</Kbd> open · <Kbd>c</Kbd> commit · <Kbd>r</Kbd> resume · <Kbd>p</Kbd> PR · <Kbd>t</Kbd> build &amp; test · <Kbd>a</Kbd> assign · <Kbd>Esc</Kbd> close
          <span className="muted"> · scanned {timeAgo(s.scannedAt)}</span>
        </footer>
      )}
    </div>
  )
}

function useShortcuts() {
  const app = useApp()
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault()
        app.openPalette()
        return
      }
      const target = e.target as HTMLElement
      if (app.dialog || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName) || e.ctrlKey || e.metaKey || e.altKey) return
      const s = app.state
      if (!s || app.view !== 'board') return
      const order = ['working', 'parked', 'review', 'ready']
      const keys = [...s.tickets].sort((a, b) => order.indexOf(a.stage) - order.indexOf(b.stage)).map(t => t.key)
      const cur = app.selected ? keys.indexOf(app.selected) : -1
      const t = s.tickets.find(x => x.key === app.selected)
      const act = (name: string) => { if (t?.actions[name]?.enabled) { e.preventDefault(); ticketAction(app, t, name) } }
      switch (e.key) {
        case 'j': case 'ArrowDown':
          if (keys.length) { e.preventDefault(); app.select(keys[Math.min(keys.length - 1, cur + 1)]) }
          break
        case 'k': case 'ArrowUp':
          if (keys.length) { e.preventDefault(); app.select(keys[Math.max(0, cur - 1)]) }
          break
        case 'Escape': app.select(null); break
        case '/': e.preventDefault(); app.openPalette(); break
        case 'c': act('commit'); break
        case 'r': act('resume'); break
        case 'p': act('push'); break
        case 't': act('check'); break
        case 'a':
          if (s.unassigned.length) { e.preventDefault(); app.openOp('assign', { ticket: '', description: '', type: 'feat', repos: s.unassigned.filter(u => !u.blockReason).map(u => u.repo) }) }
          break
      }
      if (e.key === 'j' || e.key === 'k' || e.key === 'ArrowDown' || e.key === 'ArrowUp')
        window.setTimeout(() => document.querySelector('.ticket-row.selected')?.scrollIntoView({ block: 'nearest' }), 0)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [app])
}
