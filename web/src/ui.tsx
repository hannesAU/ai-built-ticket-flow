import { ReactNode, useEffect, useRef, useState } from 'react'
import { CheckView, LegView, Stage, timeAgo } from './api'

const paths: Record<string, string> = {
  check: 'M4 12.5l5 5L20 6.5',
  dot: 'M12 12m-4 0a4 4 0 1 0 8 0a4 4 0 1 0 -8 0',
  circle: 'M12 12m-6 0a6 6 0 1 0 12 0a6 6 0 1 0 -12 0',
  branch: 'M7 4v12M7 16a2.5 2.5 0 1 0 0.01 0M17 8a2.5 2.5 0 1 0 0.01 0M17 10.5c0 4-10 2-10 5.5',
  pr: 'M7 5.5a2 2 0 1 0 .01 0M7 7.5v9M7 18.5a2 2 0 1 0 .01 0M17 18.5a2 2 0 1 0 .01 0M17 16.5V10a3 3 0 0 0-3-3h-3M12.5 4.5L10.5 7l2 2.5',
  merged: 'M7 5.5a2 2 0 1 0 .01 0M7 7.5v11M17 14.5a2 2 0 1 0 .01 0M7 9c0 4 4 5.5 8 5.5',
  warn: 'M12 4l9 16H3zM12 10v4M12 17v.5',
  refresh: 'M20 11a8 8 0 1 0-2.3 5.7M20 5v6h-6',
  search: 'M11 11m-6.5 0a6.5 6.5 0 1 0 13 0a6.5 6.5 0 1 0 -13 0M16 16l4.5 4.5',
  close: 'M6 6l12 12M18 6L6 18',
  more: 'M5 12h.01M12 12h.01M19 12h.01',
  external: 'M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5',
  folder: 'M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z',
  terminal: 'M4 5h16v14H4zM7.5 9.5l3 2.5-3 2.5M12.5 15h4',
  code: 'M9 7l-5 5 5 5M15 7l5 5-5 5',
  chevron: 'M9 6l6 6-6 6',
  down: 'M6 9l6 6 6-6',
  up: 'M12 19V5M6 11l6-6 6 6',
  arrowDown: 'M12 5v14M6 13l6 6 6-6',
  play: 'M8 5l11 7-11 7z',
  trash: 'M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13',
  undo: 'M9 14L4 9l5-5M4 9h10a6 6 0 0 1 0 12h-3',
  plus: 'M12 5v14M5 12h14',
  gear: 'M12 9a3 3 0 1 0 .01 0M19.4 15a1.6 1.6 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.6 1.6 0 0 0-2.7 1.1V21a2 2 0 1 1-4 0v-.1a1.6 1.6 0 0 0-2.7-1.1l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.6 1.6 0 0 0-1.1-2.7H3a2 2 0 1 1 0-4h.1a1.6 1.6 0 0 0 1.1-2.7l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.6 1.6 0 0 0 2.7-1.1V3a2 2 0 1 1 4 0v.1a1.6 1.6 0 0 0 2.7 1.1l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.6 1.6 0 0 0 1.1 2.7H21a2 2 0 1 1 0 4h-.1a1.6 1.6 0 0 0-1.5 1.3z',
  clock: 'M12 12m-8 0a8 8 0 1 0 16 0a8 8 0 1 0 -16 0M12 8v4l3 2',
  archive: 'M4 5h16v4H4zM5 9v10h14V9M10 13h4',
  life: 'M12 12m-8 0a8 8 0 1 0 16 0a8 8 0 1 0 -16 0M12 12m-3.5 0a3.5 3.5 0 1 0 7 0a3.5 3.5 0 1 0 -7 0M6.3 6.3l3.2 3.2M14.5 14.5l3.2 3.2M17.7 6.3l-3.2 3.2M9.5 14.5l-3.2 3.2',
  board: 'M4 5h16M4 12h16M4 19h10',
  lock: 'M6 11h12v9H6zM8.5 11V8a3.5 3.5 0 0 1 7 0v3',
  flask: 'M9 3h6M10 3v6l-5 9a2 2 0 0 0 1.7 3h10.6a2 2 0 0 0 1.7-3l-5-9V3M7.5 15h9',
  stop: 'M7 7h10v10H7z',
}

export function Icon({ name, size = 16, className }: { name: keyof typeof paths | string; size?: number; className?: string }) {
  return (
    <svg className={'icon ' + (className ?? '')} width={size} height={size} viewBox="0 0 24 24" fill="none"
      stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={paths[name] ?? paths.dot} />
    </svg>
  )
}

export const stageMeta: Record<Stage, { label: string; hint: string }> = {
  working: { label: 'Working', hint: 'Uncommitted changes on a wip branch' },
  parked: { label: 'Parked', hint: 'Committed locally, not pushed yet' },
  review: { label: 'In review', hint: 'Pushed with open pull requests' },
  ready: { label: 'Ready to complete', hint: 'Every PR approved or merged' },
}

/** Compact per-repo status chip used in ticket rows. */
export function LegChip({ leg }: { leg: LegView }) {
  let icon = 'check'
  let tone = 'muted'
  let text = ''
  switch (leg.state) {
    case 'working':
    case 'working-committed':
      icon = 'dot'; tone = 'working'; text = String(leg.files.length || '')
      break
    case 'empty':
      icon = 'circle'; tone = 'muted'
      break
    case 'parked':
      icon = 'check'; tone = 'parked'
      break
    case 'review':
      icon = 'pr'; tone = leg.pr?.tone ?? 'info'; text = `#${leg.pr?.number}`
      break
    case 'merged':
      icon = 'merged'; tone = 'done'; text = leg.pr ? `#${leg.pr.number}` : ''
      break
  }
  const title = `${leg.repo}: ${leg.stateText}${leg.isCurrent ? ' (checked out)' : ''}`
  return (
    <span className={`chip tone-${tone}`} title={title}>
      <Icon name={icon} size={12} />
      <span className="chip-repo">{leg.repo}</span>
      {text && <span className="chip-extra">{text}</span>}
      {leg.localAhead && <span className="chip-flag" title="Local commit not pushed">↑</span>}
      {leg.state === 'parked' && leg.behind > 0 && <span className="chip-flag" title={`${leg.behind} behind base`}>↓{leg.behind}</span>}
    </span>
  )
}

/** Latest "Build & test" result of a ticket (or a spinner while it runs). */
export function CheckChip({ check, running, onClick }: { check?: CheckView; running?: boolean; onClick?(): void }) {
  if (running)
    return <span className="chip tone-info check-chip" title="Build & test is running"><span className="spinner small" /> checking</span>
  if (!check) return null
  const passed = check.status === 'passed'
  const tone = check.stale ? 'muted' : passed ? 'ok' : 'bad'
  const title = `${check.text} · ${timeAgo(check.at)}` +
    (check.stale ? `\nStale — code changed since in ${check.staleRepos.join(', ')}` : '')
  return (
    <button className={`chip check-chip tone-${tone}`} title={title} onClick={onClick} type="button">
      <Icon name={passed ? 'check' : 'close'} size={12} />
      {passed ? 'checks pass' : 'checks fail'}{check.stale && <span className="chip-extra"> · stale</span>}
    </button>
  )
}

export function TypePill({ type }: { type: string }) {
  return <span className={`pill pill-${type}`}>{type}</span>
}

/** Popover menu anchored to a trigger button. */
export function Menu({ trigger, children, align = 'right' }: { trigger: ReactNode; children: ReactNode; align?: 'left' | 'right' }) {
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!open) return
    const close = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false) }
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    document.addEventListener('mousedown', close)
    document.addEventListener('keydown', esc)
    return () => { document.removeEventListener('mousedown', close); document.removeEventListener('keydown', esc) }
  }, [open])
  return (
    <div className="menu-wrap" ref={ref} onClick={e => e.stopPropagation()}>
      <span onClick={() => setOpen(o => !o)}>{trigger}</span>
      {open && <div className={`menu menu-${align}`} onClick={() => setOpen(false)}>{children}</div>}
    </div>
  )
}

export function MenuItem({ icon, label, onClick, disabled, reason, danger }: {
  icon?: string; label: string; onClick(): void; disabled?: boolean; reason?: string; danger?: boolean
}) {
  return (
    <button className={`menu-item ${danger ? 'danger' : ''}`} disabled={disabled} onClick={onClick} title={disabled ? reason : undefined}>
      {icon ? <Icon name={icon} size={14} /> : <span className="icon-spacer" />}
      <span className="menu-label">{label}</span>
      {disabled && reason && <span className="menu-reason">{reason}</span>}
    </button>
  )
}

export function StatusDot({ tone }: { tone: string }) {
  return <span className={`status-dot tone-${tone}`} />
}

export function Kbd({ children }: { children: ReactNode }) {
  return <kbd>{children}</kbd>
}

export function Empty({ icon, title, children }: { icon: string; title: string; children?: ReactNode }) {
  return (
    <div className="empty">
      <Icon name={icon} size={28} />
      <div className="empty-title">{title}</div>
      {children && <div className="empty-body">{children}</div>}
    </div>
  )
}
