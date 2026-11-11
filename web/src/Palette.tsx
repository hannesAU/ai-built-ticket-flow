import { useEffect, useMemo, useRef, useState } from 'react'
import { useApp } from './store'
import { ticketAction } from './Board'
import { Icon } from './ui'

interface Command { id: string; label: string; hint?: string; icon: string; run(): void }

export function Palette() {
  const app = useApp()
  const [q, setQ] = useState('')
  const [idx, setIdx] = useState(0)
  const listRef = useRef<HTMLDivElement>(null)
  const s = app.state

  const commands = useMemo<Command[]>(() => {
    const c: Command[] = []
    if (!s) return c
    if (s.unassigned.length > 0)
      c.push({ id: 'assign', label: 'Assign unassigned changes to a ticket', icon: 'warn', run: () => app.openOp('assign', { ticket: '', description: '', type: 'feat' }) })
    const icons: Record<string, string> = { commit: 'check', resume: 'undo', push: 'pr', complete: 'merged', check: 'flask', discard: 'trash' }
    for (const t of s.tickets) {
      for (const [name, a] of Object.entries(t.actions)) {
        if (!a.enabled) continue
        c.push({ id: `${t.key}:${name}`, label: `${a.label.replace('…', '')} ${t.key}`, hint: t.description, icon: icons[name] ?? 'dot', run: () => ticketAction(app, t, name) })
      }
      c.push({ id: `${t.key}:open`, label: `Open ${t.key}`, hint: t.description, icon: 'chevron', run: () => { app.setView('board'); app.select(t.key) } })
    }
    c.push({ id: 'sync', label: 'Fetch all repos & update default branches', icon: 'arrowDown', run: () => app.openOp('sync', {}) })
    c.push({ id: 'refresh', label: 'Refresh now (rescan + PR status)', icon: 'refresh', run: () => app.refresh() })
    c.push({ id: 'v-board', label: 'Go to Board', icon: 'board', run: () => app.setView('board') })
    c.push({ id: 'v-archive', label: 'Go to Archive', icon: 'archive', run: () => app.setView('archive') })
    c.push({ id: 'v-recovery', label: 'Go to Recovery', icon: 'life', run: () => app.setView('recovery') })
    c.push({ id: 'v-settings', label: 'Go to Settings', icon: 'gear', run: () => app.setView('settings') })
    return c
  }, [s, app])

  const tokens = q.toLowerCase().split(/\s+/).filter(Boolean)
  const filtered = commands.filter(c => tokens.every(t => `${c.label} ${c.hint ?? ''}`.toLowerCase().includes(t)))

  useEffect(() => setIdx(0), [q])
  useEffect(() => {
    listRef.current?.querySelector('.pal-item.active')?.scrollIntoView({ block: 'nearest' })
  }, [idx])

  const exec = (c?: Command) => {
    if (!c) return
    app.closeDialog()
    window.setTimeout(c.run, 0)
  }

  return (
    <div className="modal-backdrop top" onMouseDown={e => { if (e.target === e.currentTarget) app.closeDialog() }}>
      <div className="palette" role="dialog" aria-label="Command palette">
        <div className="pal-input">
          <Icon name="search" />
          <input autoFocus placeholder="Type a command or ticket…" value={q} onChange={e => setQ(e.target.value)}
            onKeyDown={e => {
              if (e.key === 'ArrowDown') { e.preventDefault(); setIdx(i => Math.min(filtered.length - 1, i + 1)) }
              if (e.key === 'ArrowUp') { e.preventDefault(); setIdx(i => Math.max(0, i - 1)) }
              if (e.key === 'Enter') exec(filtered[idx])
              if (e.key === 'Escape') app.closeDialog()
            }} />
        </div>
        <div className="pal-list" ref={listRef}>
          {filtered.length === 0 && <div className="muted small pad">No matching commands</div>}
          {filtered.map((c, i) => (
            <button key={c.id} className={`pal-item ${i === idx ? 'active' : ''}`} onMouseEnter={() => setIdx(i)} onClick={() => exec(c)}>
              <Icon name={c.icon} size={14} />
              <span>{c.label}</span>
              {c.hint && <span className="muted small pal-hint">{c.hint}</span>}
            </button>
          ))}
        </div>
      </div>
    </div>
  )
}
