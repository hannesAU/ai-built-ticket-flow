import { useEffect, useState } from 'react'
import { api, CheckPhase, JournalEntry, LegView, RepoCheck, TicketView, timeAgo } from './api'
import { useApp } from './store'
import { ticketAction } from './Board'
import { ProgressView } from './OpDialog'
import { Icon, StatusDot, TypePill } from './ui'
import { TicketReviewPanel } from './Reviews'

export function DiffView({ repo, path, ticketKey }: { repo: string; path: string; ticketKey?: string }) {
  const [text, setText] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)
  useEffect(() => {
    let live = true
    setText(null)
    api.diff(repo, path, ticketKey).then(t => live && setText(t)).catch(e => live && setErr(e.message))
    return () => { live = false }
  }, [repo, path, ticketKey])
  if (err) return <div className="diff error">{err}</div>
  if (text === null) return <div className="diff loading">Loading diff…</div>
  const lines = text.split('\n')
  return (
    <pre className="diff" aria-label={`Diff of ${path}`}>
      {lines.map((l, i) => {
        const cls = l.startsWith('+++') || l.startsWith('---') || l.startsWith('diff ') || l.startsWith('index ') || l.startsWith('new file') || l.startsWith('deleted file')
          ? 'meta' : l.startsWith('@@') ? 'hunk' : l.startsWith('+') ? 'add' : l.startsWith('-') ? 'del' : ''
        return <div key={i} className={`dl ${cls}`}>{l || ' '}</div>
      })}
    </pre>
  )
}

type Tab = 'changes' | 'prs' | 'checks' | 'review' | 'activity'

export function TicketDrawer({ ticketKey }: { ticketKey: string }) {
  const app = useApp()
  const t = app.state?.tickets.find(x => x.key === ticketKey)
  const [tab, setTab] = useState<Tab>('changes')
  useEffect(() => {
    if (app.drawerTab?.key === ticketKey) setTab(app.drawerTab.tab as Tab)
    else if (t?.stage === 'review' || t?.stage === 'ready') setTab('prs')
    else setTab('changes')
  }, [ticketKey, app.drawerTab]) // eslint-disable-line react-hooks/exhaustive-deps
  // starting a check from anywhere brings this ticket's Checks tab to the front
  const run = app.state?.runningOp
  const checking = run?.kind === 'check' && run.key === ticketKey
  useEffect(() => { if (checking) setTab('checks') }, [checking])
  const review = app.reviews?.tickets.find(x => x.key === ticketKey)
  const reviewing = review?.status === 'reviewing' || review?.status === 'queued'
  const startReview = (repo: string) => {
    setTab('review')
    api.reviewTicket(ticketKey, repo).catch(e => app.toast({ title: 'Could not start the review', body: (e as Error).message, tone: 'bad' }))
  }

  if (!t)
    return (
      <aside className="drawer">
        <div className="drawer-head">
          <div className="drawer-title"><span className="key big">{ticketKey}</span></div>
          <button className="icon-btn" onClick={() => app.select(null)} aria-label="Close"><Icon name="close" /></button>
        </div>
        <div className="drawer-body"><p className="muted">This ticket is no longer in flight. Completed tickets are in the Archive.</p></div>
      </aside>
    )

  return (
    <aside className="drawer" aria-label={`Ticket ${t.key}`}>
      <DrawerHeader t={t} />
      <ActionBar t={t} />
      <div className="tabs" role="tablist">
        {([['changes', 'Changes'], ['prs', 'Pull requests'], ['checks', 'Checks'], ['review', 'Review'], ['activity', 'Activity']] as [Tab, string][]).map(([id, label]) => (
          <button key={id} role="tab" aria-selected={tab === id} className={`tab ${tab === id ? 'active' : ''}`} onClick={() => setTab(id)}>
            {label}
            {id === 'prs' && t.legs.some(l => l.pr) && <span className="tab-count">{t.legs.filter(l => l.pr).length}</span>}
            {id === 'checks' && (checking ? <span className="spinner small" />
              : t.check && <StatusDot tone={t.check.stale ? 'muted' : t.check.status === 'passed' ? 'ok' : 'bad'} />)}
            {id === 'review' && (reviewing ? <span className="spinner small" />
              : review?.reviews[0] && <StatusDot tone={review.stale || review.reviews[0].status !== 'ok' ? 'muted'
                : review.reviews[0].verdict === 'looks-good' ? 'ok' : review.reviews[0].verdict === 'needs-changes' ? 'bad' : 'warn'} />)}
          </button>
        ))}
      </div>
      <div className="drawer-body">
        {tab === 'changes' && t.legs.map(l => <LegChanges key={l.repo} t={t} leg={l} reviewing={reviewing} onReview={() => startReview(l.repo)} />)}
        {tab === 'prs' && t.legs.map(l => <LegPr key={l.repo} t={t} leg={l} />)}
        {tab === 'checks' && <ChecksPanel t={t} />}
        {tab === 'review' && <TicketReviewPanel ticketKey={t.key} repos={t.legs.map(l => l.repo)} />}
        {tab === 'activity' && <Activity ticketKey={t.key} />}
      </div>
    </aside>
  )
}

function DrawerHeader({ t }: { t: TicketView }) {
  const app = useApp()
  const [desc, setDesc] = useState(t.description)
  useEffect(() => setDesc(t.description), [t.description])

  const save = async (description: string, type: string) => {
    if (!description.trim()) return
    try {
      const p = await api.run('meta', { key: t.key, description, type })
      app.trackOp(p)
    } catch (e) {
      app.toast({ title: 'Could not save', body: (e as Error).message, tone: 'bad' })
    }
  }

  return (
    <div className="drawer-head">
      <div className="drawer-title">
        <div className="row-gap">
          <span className="key big">{t.key}</span>
          <button className="pill-btn" title="Toggle feat/fix" onClick={() => save(t.description, t.type === 'feat' ? 'fix' : 'feat')}>
            <TypePill type={t.type} />
          </button>
          {t.jiraUrl && <a className="link small" href={t.jiraUrl} target="_blank" rel="noreferrer">Jira <Icon name="external" size={11} /></a>}
        </div>
        <input className="desc-input" value={desc} placeholder="Short description (used in the commit message)"
          onChange={e => setDesc(e.target.value)}
          onBlur={() => desc.trim() !== t.description && save(desc, t.type)}
          onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); if (e.key === 'Escape') setDesc(t.description) }} />
        <div className="commit-msg mono" title="Commit message / PR title">{t.commitMessage}</div>
      </div>
      <button className="icon-btn" onClick={() => app.select(null)} aria-label="Close"><Icon name="close" /></button>
    </div>
  )
}

function ActionBar({ t }: { t: TicketView }) {
  const app = useApp()
  const names = ['commit', 'resume', 'push', 'complete'] as const
  const icons: Record<string, string> = { commit: 'check', resume: 'undo', push: 'pr', complete: 'merged' }
  const next = t.primaryAction ? t.actions[t.primaryAction] : undefined
  return (
    <div className="action-bar">
      <div className="row-gap wrap">
        {names.map(n => {
          const a = t.actions[n]
          const isPrimary = t.primaryAction === n
          return (
            <button key={n} className={`btn ${isPrimary ? (n === 'complete' ? 'success' : 'primary') : ''}`} disabled={!a.enabled}
              title={a.reason} onClick={() => ticketAction(app, t, n)}>
              <Icon name={icons[n]} size={14} /> {a.label}
            </button>
          )
        })}
        <button className="btn" disabled={!t.actions.check.enabled} title={t.actions.check.reason ?? 'Build and run the unit tests of every repo of this ticket (t)'}
          onClick={() => ticketAction(app, t, 'check')}>
          <Icon name="flask" size={14} /> {t.actions.check.label}
        </button>
        <button className="btn ghost danger" onClick={() => ticketAction(app, t, 'discard')}><Icon name="trash" size={14} /> Discard…</button>
      </div>
      {next && !next.enabled && next.reason && <div className="why"><Icon name="clock" size={13} /> {next.reason}</div>}
      {!next && t.stage === 'review' && !t.actions.complete.enabled && <div className="why"><Icon name="clock" size={13} /> {t.actions.complete.reason}</div>}
    </div>
  )
}

function LegChanges({ t, leg, reviewing, onReview }: { t: TicketView; leg: LegView; reviewing: boolean; onReview(): void }) {
  const app = useApp()
  const [diff, setDiff] = useState<string | null>(null)
  const canRebase = !leg.state.startsWith('working') && leg.state !== 'merged' && leg.state !== 'empty' &&
    (leg.behind > 0 || leg.pr?.status === 'Conflicts')
  return (
    <div className="leg">
      <div className="leg-head">
        <div>
          <span className="leg-repo">{leg.repo}</span>
          <span className="branch-tag"><Icon name="branch" size={12} /> {leg.branch}</span>
          {leg.isCurrent && <span className="badge info">checked out</span>}
        </div>
        <div className="row-gap">
          <span className="muted small">{leg.stateText}</span>
          <button className="btn small ghost" disabled={reviewing}
            title={t.legs.length > 1 ? `Review ${t.key} with Claude Code — all ${t.legs.length} repos, starting from ${leg.repo}` : `Review ${t.key} with Claude Code`}
            onClick={onReview}>
            <Icon name="search" size={13} /> Review
          </button>
          {canRebase && (
            <button className="btn small ghost" title="Rebase this branch in your working copy to resolve conflicts by hand"
              onClick={() => app.openOp('resolve', { key: t.key, repo: leg.repo })}>Rebase in working copy</button>
          )}
          {t.legs.length > 1 && (
            <button className="icon-btn" title={`Remove ${leg.repo} from this ticket`}
              onClick={() => app.openOp('discard-ticket', { key: t.key, repos: [leg.repo], closePrs: false, deleteRemote: false })}>
              <Icon name="trash" size={14} />
            </button>
          )}
        </div>
      </div>
      {leg.files.length === 0 && <div className="muted small pad">No changes.</div>}
      {leg.files.map(f => (
        <div key={f.path}>
          <button className={`file-line ${diff === f.path ? 'active' : ''}`} onClick={() => setDiff(d => (d === f.path ? null : f.path))}>
            <span className={`st st-${f.status}`}>{f.status}</span>
            <span className="file-path">{f.path}</span>
          </button>
          {diff === f.path && <DiffView repo={leg.repo} path={f.path} ticketKey={t.key} />}
        </div>
      ))}
    </div>
  )
}

function LegPr({ t, leg }: { t: TicketView; leg: LegView }) {
  const app = useApp()
  const pr = leg.pr
  return (
    <div className="leg">
      <div className="leg-head">
        <div>
          <span className="leg-repo">{leg.repo}</span>
          <span className="branch-tag mono">{leg.remoteBranch}</span>
        </div>
        {pr && <a className="link" href={pr.url} target="_blank" rel="noreferrer">#{pr.number} <Icon name="external" size={12} /></a>}
      </div>
      {!pr ? (
        <div className="muted small pad">No pull request yet{leg.state === 'parked' ? ' — use “Move to PR”.' : '.'}</div>
      ) : (
        <div className="pr-card">
          <div className={`pr-status tone-${pr.tone}`}><StatusDot tone={pr.tone} /> {pr.statusText}</div>
          <dl className="pr-grid">
            <dt>Review</dt><dd>{pr.reviewDecision?.replace('_', ' ').toLowerCase() ?? 'no review rule'}</dd>
            <dt>Checks</dt><dd>{pr.checks?.toLowerCase() ?? 'none'}</dd>
            <dt>Mergeable</dt><dd>{(pr.mergeable ?? '?').toLowerCase()} · {(pr.mergeState ?? '?').toLowerCase()}</dd>
            <dt>Head</dt>
            <dd className="mono">
              {pr.headSha.slice(0, 7)}
              {leg.localAhead ? <span className="tone-warn"> · local {leg.sha.slice(0, 7)} not pushed</span> : <span className="muted"> · matches local</span>}
            </dd>
          </dl>
          {(pr.status === 'Conflicts' || pr.status === 'Behind' || leg.localAhead) && t.actions.push.enabled && (
            <button className="btn small primary" onClick={() => ticketAction(app, t, 'push')}>Update PRs</button>
          )}
        </div>
      )}
    </div>
  )
}

function Activity({ ticketKey }: { ticketKey: string }) {
  const app = useApp()
  const [items, setItems] = useState<JournalEntry[] | null>(null)
  const [open, setOpen] = useState<string | null>(null)
  const version = app.state?.version
  useEffect(() => {
    api.journal(ticketKey).then(setItems).catch(() => setItems([]))
  }, [ticketKey, version])
  if (!items) return <div className="muted small">Loading…</div>
  if (items.length === 0) return <div className="muted small">No activity yet.</div>
  return (
    <div className="activity">
      {items.map(e => (
        <div key={e.id} className="act">
          <button className="act-head" onClick={() => setOpen(o => (o === e.id ? null : e.id))}>
            <StatusDot tone={e.status === 'succeeded' ? 'ok' : e.status === 'failed' ? 'bad' : 'info'} />
            <span className="act-title">{e.title}</span>
            <span className="muted small">{timeAgo(e.startedAt)}</span>
          </button>
          {e.error && <div className="act-error">{e.error}</div>}
          {open === e.id && (
            <div className="steps compact">
              {e.steps.map((s, i) => (
                <div key={i} className={`step st-${s.status}`}>
                  <span className="step-repo">{s.repo}</span> {s.text}
                  {s.log.length > 0 && <pre className="log">{s.log.join('\n')}</pre>}
                </div>
              ))}
            </div>
          )}
        </div>
      ))}
    </div>
  )
}

function duration(s: number) {
  return s < 60 ? `${Math.round(s)}s` : `${Math.floor(s / 60)}m ${Math.round(s % 60)}s`
}

/** "Build & test": live progress while it runs, otherwise the ticket's latest result per repo. */
function ChecksPanel({ t }: { t: TicketView }) {
  const app = useApp()
  const run = app.state?.runningOp
  const live = run?.kind === 'check' && run.key === t.key ? (app.ops[run.id] ?? run) : undefined
  const c = t.check
  const action = t.actions.check
  const tone = !c ? 'muted' : c.stale ? 'muted' : c.status === 'passed' ? 'ok' : 'bad'

  const cancel = () => api.cancelOp().catch(e => app.toast({ title: 'Could not cancel', body: (e as Error).message, tone: 'bad' }))

  return (
    <>
      <div className="check-head">
        <div className="check-title">
          {live ? <><span className="spinner" /> <span>Building and testing — other actions wait until this finishes.</span></>
            : c ? <><StatusDot tone={tone} /> <span><strong>{c.text}</strong><span className="muted small"> · {timeAgo(c.at)} · took {duration(c.seconds)}</span></span></>
            : <span className="muted">Not built or tested yet. Runs each repo's build and unit tests in your working copy.</span>}
        </div>
        {live ? (
          live.cancellable && <button className="btn small ghost danger" onClick={cancel}><Icon name="stop" size={13} /> Cancel</button>
        ) : (
          <button className="btn small" disabled={!action.enabled} title={action.reason} onClick={() => ticketAction(app, t, 'check')}>
            <Icon name="flask" size={13} /> {c ? 'Run again' : action.label}
          </button>
        )}
      </div>
      {!live && !action.enabled && action.reason && <div className="why"><Icon name="clock" size={13} /> {action.reason}</div>}
      {live && <ProgressView progress={live} />}
      {!live && c?.stale && (
        <div className="callout warn small">
          The code changed since this ran ({c.staleRepos.join(', ')}) — this result may no longer hold.
        </div>
      )}
      {!live && c?.repos.map(r => <RepoCheckCard key={r.repo} ticketKey={t.key} r={r} />)}
    </>
  )
}

function RepoCheckCard({ ticketKey, r }: { ticketKey: string; r: RepoCheck }) {
  const tone = r.status === 'passed' ? 'ok' : r.status === 'failed' ? 'bad' : 'muted'
  return (
    <div className="leg">
      <div className="leg-head">
        <div><StatusDot tone={tone} /><span className="leg-repo">{r.repo}</span></div>
        <span className={`small tone-${tone}`}>{r.summary}</span>
      </div>
      <div className="phases">
        {r.phases.map((p, i) => <PhaseRow key={i} ticketKey={ticketKey} repo={r.repo} index={i} p={p} />)}
      </div>
    </div>
  )
}

function PhaseRow({ ticketKey, repo, index, p }: { ticketKey: string; repo: string; index: number; p: CheckPhase }) {
  const [open, setOpen] = useState(p.status === 'failed')
  const [log, setLog] = useState<string | null>(null)
  const icon = p.status === 'ok' ? 'check' : p.status === 'failed' ? 'close' : 'circle'
  const tone = p.status === 'ok' ? 'ok' : p.status === 'failed' ? 'bad' : 'muted'
  const showLog = () => {
    if (log !== null) return setLog(null)
    setLog('Loading…')
    api.checkLog(ticketKey, repo, index).then(setLog).catch(e => setLog(String((e as Error).message)))
  }
  return (
    <div className={`phase st-${p.status}`}>
      <button className="step-head" onClick={() => setOpen(o => !o)}>
        <span className="step-icon"><Icon name={icon} size={14} className={`tone-${tone}`} /></span>
        <span className="step-text">{p.label} <span className="muted small">— {p.summary}</span></span>
        {p.status !== 'skipped' && <span className="muted small">{duration(p.seconds)}</span>}
        <Icon name={open ? 'down' : 'chevron'} size={12} className="muted" />
      </button>
      {open && (
        <div className="phase-body">
          <code className="phase-cmd">{p.command}</code>
          {p.errors.length > 0 && <pre className="check-errors">{p.errors.join(p.errors.some(e => e.includes('\n')) ? '\n\n' : '\n')}</pre>}
          {p.status !== 'skipped' && (
            <button className="link-btn small" onClick={showLog}>{log === null ? 'Show full output' : 'Hide full output'}</button>
          )}
          {log !== null && <pre className="log tall">{log}</pre>}
        </div>
      )}
    </div>
  )
}
