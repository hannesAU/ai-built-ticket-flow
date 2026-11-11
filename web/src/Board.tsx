import { useState } from 'react'
import { api, plural, RepoView, Stage, TicketView, UnassignedView } from './api'
import { useApp } from './store'
import { DiffView } from './Drawer'
import { CheckChip, Empty, Icon, Kbd, LegChip, Menu, MenuItem, stageMeta, TypePill } from './ui'

type App = ReturnType<typeof useApp>

/** Opens the review-and-run dialog for a ticket action. */
export function ticketAction(app: App, t: TicketView, action: string) {
  switch (action) {
    case 'commit': return app.openOp('commit', { key: t.key })
    case 'resume': return app.openOp('resume', { key: t.key })
    case 'push': return app.openOp('push', { key: t.key, draft: false, overwriteRemote: false })
    case 'complete': return app.openOp('complete', { key: t.key })
    case 'check': return app.openOp('check', { key: t.key })
    case 'discard': return app.openOp('discard-ticket', { key: t.key, closePrs: false, deleteRemote: false })
    case 'inspect': return app.select(t.key)
  }
}

export function Board() {
  const app = useApp()
  const s = app.state!
  if (!s.configured)
    return (
      <div className="board">
        <Empty icon="folder" title="No repositories configured yet">
          Add the local repo folders you work in. Ticket Flow derives everything else from their <code>wip/</code> branches.
          <div style={{ marginTop: 14 }}>
            <button className="btn primary" onClick={() => app.setView('settings')}>Open settings</button>
          </div>
        </Empty>
      </div>
    )
  return (
    <div className="board">
      {s.unassigned.length > 0 && <UnassignedBanner items={s.unassigned} />}
      <RepoStrip repos={s.repos} />
      <TicketGroups tickets={s.tickets} />
    </div>
  )
}

function UnassignedBanner({ items }: { items: UnassignedView[] }) {
  const app = useApp()
  const [open, setOpen] = useState<Record<string, boolean>>({})
  const [diff, setDiff] = useState<{ repo: string; path: string } | null>(null)
  const assignable = items.filter(i => !i.blockReason)
  return (
    <section className="unassigned" aria-label="Unassigned changes">
      <header>
        <div className="unassigned-title">
          <Icon name="warn" size={18} />
          <div>
            <strong>Unassigned changes</strong>
            <span className="muted"> — uncommitted work that isn't on a ticket yet. Branch switching is blocked in these repos until it's assigned.</span>
          </div>
        </div>
        <button className="btn primary" disabled={assignable.length === 0}
          onClick={() => app.openOp('assign', { ticket: '', description: '', type: 'feat', repos: assignable.map(i => i.repo) })}>
          Assign to ticket… <Kbd>A</Kbd>
        </button>
      </header>
      {items.map(u => (
        <div key={u.repo} className="unassigned-row">
          <div className="u-main" onClick={() => setOpen(o => ({ ...o, [u.repo]: !o[u.repo] }))}>
            <Icon name={open[u.repo] ? 'down' : 'chevron'} size={14} />
            <span className="u-repo">{u.repo}</span>
            <span className={`branch-tag ${u.isDefault ? '' : 'warn'}`} title={u.isDefault ? '' : 'Not the default branch'}>
              <Icon name="branch" size={12} /> {u.branch}
            </span>
            <span className="u-files">
              {plural(u.files.length, 'file')}
              <span className="muted"> · {u.files.slice(0, 3).map(f => f.path.split('/').pop()).join(', ')}{u.files.length > 3 ? ' …' : ''}</span>
            </span>
            {u.excludedFiles.length > 0 && <span className="muted small" title={u.excludedFiles.map(f => f.path).join('\n')}>+{u.excludedFiles.length} excluded</span>}
            {u.blockReason && <span className="badge bad">{u.blockReason}</span>}
          </div>
          <div className="u-actions">
            <button className="btn ghost small" onClick={() => app.openOp('assign', { ticket: '', description: '', type: 'feat', repos: [u.repo] })}>Assign this repo…</button>
            <button className="btn ghost small danger" onClick={() => app.openOp('discard-changes', { repo: u.repo, paths: u.files.map(f => f.path) })}>Discard…</button>
          </div>
          {open[u.repo] && (
            <div className="u-filelist">
              {[...u.files, ...u.excludedFiles].map(f => (
                <button key={f.path} className={`file-line ${f.excluded ? 'excluded' : ''} ${diff?.repo === u.repo && diff.path === f.path ? 'active' : ''}`}
                  onClick={() => setDiff(d => d?.repo === u.repo && d.path === f.path ? null : { repo: u.repo, path: f.path })}>
                  <span className={`st st-${f.status}`}>{f.status}</span>
                  <span className="file-path">{f.path}</span>
                  {f.excluded && <span className="muted small">excluded — never committed</span>}
                </button>
              ))}
              {diff?.repo === u.repo && <DiffView repo={diff.repo} path={diff.path} />}
            </div>
          )}
        </div>
      ))}
    </section>
  )
}

function RepoStrip({ repos }: { repos: RepoView[] }) {
  const app = useApp()
  return (
    <section className="repo-strip" aria-label="Repositories">
      {repos.map(r => {
        const onWip = r.currentBranch?.startsWith('wip/')
        const attention = !r.ok || !!r.inProgress
        return (
          <div key={r.name} className={`repo-card ${attention ? 'attention' : ''} ${r.workingKey ? 'is-working' : ''}`}>
            <div className="repo-top">
              <span className="repo-name" title={r.path}>{r.name}</span>
              <Menu trigger={<button className="icon-btn" aria-label={`${r.name} actions`}><Icon name="more" /></button>}>
                <MenuItem icon="arrowDown" label={`Fetch & update ${r.defaultBranch}`} onClick={() => app.openOp('sync', { repo: r.name })} />
                <MenuItem icon="code" label="Open in VS Code" onClick={() => api.open(r.name, 'code')} />
                <MenuItem icon="terminal" label="Open terminal" onClick={() => api.open(r.name, 'terminal')} />
                <MenuItem icon="folder" label="Open folder" onClick={() => api.open(r.name, 'explorer')} />
              </Menu>
            </div>
            {!r.ok ? <div className="repo-error"><Icon name="warn" size={13} /> {r.error}</div> : (
              <>
                <button className={`repo-branch ${onWip ? 'wip' : ''}`} disabled={!onWip}
                  onClick={() => onWip && app.select(r.currentBranch!.slice(4))} title={onWip ? 'Show ticket' : undefined}>
                  <Icon name="branch" size={12} /> {r.currentBranch ?? 'detached HEAD'}
                </button>
                <div className="repo-meta">
                  {r.dirty > 0 ? <span className="tone-working" title="Uncommitted (non-excluded) files">● {r.dirty}</span> : <span className="muted">clean</span>}
                  {r.dirtyExcluded > 0 && <span className="muted" title={r.files.filter(f => f.excluded).map(f => f.path).join('\n')}>{r.dirtyExcluded} excl</span>}
                  {r.behind > 0 && <button className="link-btn" title={`${r.defaultBranch} is ${r.behind} behind origin — click to update`}
                    onClick={() => app.openOp('sync', { repo: r.name })}>↓{r.behind}</button>}
                  {r.ahead > 0 && <span className="tone-warn" title={`${r.defaultBranch} has ${r.ahead} unpushed local commits`}>↑{r.ahead}</span>}
                </div>
                {r.inProgress && (
                  <div className="repo-inprogress">
                    <span className="badge bad">{r.inProgress} in progress</span>
                    {r.inProgress === 'rebase' && (
                      <span className="row-gap">
                        <button className="btn small primary" onClick={() => app.openOp('rebase', { repo: r.name, action: 'continue' })}>Continue</button>
                        <button className="btn small ghost" onClick={() => app.openOp('rebase', { repo: r.name, action: 'abort' })}>Abort</button>
                      </span>
                    )}
                  </div>
                )}
              </>
            )}
          </div>
        )
      })}
    </section>
  )
}

const order: Stage[] = ['working', 'parked', 'review', 'ready']

function TicketGroups({ tickets }: { tickets: TicketView[] }) {
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>(() => {
    try { return JSON.parse(localStorage.getItem('wip.collapsed') ?? '{}') } catch { return {} }
  })
  const toggle = (s: string) => setCollapsed(c => {
    const n = { ...c, [s]: !c[s] }
    try { localStorage.setItem('wip.collapsed', JSON.stringify(n)) } catch { /* ignore */ }
    return n
  })
  if (tickets.length === 0)
    return (
      <Empty icon="branch" title="No tickets in flight">
        Make a change in any repo — it shows up above as <em>unassigned</em>, ready to be put on a ticket.
      </Empty>
    )
  return (
    <div className="groups">
      {order.map(stage => {
        const list = tickets.filter(t => t.stage === stage)
        if (list.length === 0) return null
        return (
          <section key={stage} className={`group stage-${stage}`}>
            <button className="group-head" onClick={() => toggle(stage)} aria-expanded={!collapsed[stage]}>
              <Icon name={collapsed[stage] ? 'chevron' : 'down'} size={14} />
              <span className="group-label">{stageMeta[stage].label}</span>
              <span className="group-count">{list.length}</span>
              <span className="group-hint">{stageMeta[stage].hint}</span>
            </button>
            {!collapsed[stage] && list.map(t => <TicketRow key={t.key} t={t} />)}
          </section>
        )
      })}
    </div>
  )
}

function TicketRow({ t }: { t: TicketView }) {
  const app = useApp()
  const primary = t.primaryAction ? t.actions[t.primaryAction] : undefined
  const a = t.actions
  const run = app.state?.runningOp
  const checking = run?.kind === 'check' && run.key === t.key
  return (
    <div className={`ticket-row ${app.selected === t.key ? 'selected' : ''}`} data-key={t.key}
      onClick={() => app.select(app.selected === t.key ? null : t.key)} role="button" tabIndex={-1}>
      <div className="t-key">
        <span className="key">{t.key}</span>
        <TypePill type={t.type} />
      </div>
      <div className="t-desc">
        <div className="t-title">{t.description || <span className="muted">No description</span>}</div>
        <div className="t-status">
          {t.statusText}
          {t.flags.map(f => <span key={f} className="flag"> · {f}</span>)}
        </div>
      </div>
      <div className="t-legs">
        {t.legs.map(l => <LegChip key={l.repo} leg={l} />)}
        <span onClick={e => e.stopPropagation()}>
          <CheckChip check={t.check} running={checking} onClick={() => app.select(t.key, 'checks')} />
        </span>
      </div>
      <div className="t-actions" onClick={e => e.stopPropagation()}>
        {primary ? (
          <button className={`btn ${t.stage === 'ready' ? 'success' : 'primary'}`} disabled={!primary.enabled} title={primary.reason}
            onClick={() => ticketAction(app, t, t.primaryAction!)}>{primary.label}</button>
        ) : (
          <button className="btn" onClick={() => app.select(t.key)}>Inspect</button>
        )}
        <Menu trigger={<button className="icon-btn" aria-label="More actions"><Icon name="more" /></button>}>
          {t.primaryAction !== 'commit' && <MenuItem icon="check" label={a.commit.label} disabled={!a.commit.enabled} reason={a.commit.reason} onClick={() => ticketAction(app, t, 'commit')} />}
          <MenuItem icon="undo" label={a.resume.label} disabled={!a.resume.enabled} reason={a.resume.reason} onClick={() => ticketAction(app, t, 'resume')} />
          {t.primaryAction !== 'push' && <MenuItem icon="pr" label={a.push.label} disabled={!a.push.enabled} reason={a.push.reason} onClick={() => ticketAction(app, t, 'push')} />}
          {t.primaryAction !== 'complete' && <MenuItem icon="merged" label={a.complete.label} disabled={!a.complete.enabled} reason={a.complete.reason} onClick={() => ticketAction(app, t, 'complete')} />}
          <MenuItem icon="flask" label={a.check.label} disabled={!a.check.enabled} reason={a.check.reason} onClick={() => ticketAction(app, t, 'check')} />
          <div className="menu-sep" />
          {t.jiraUrl && <MenuItem icon="external" label="Open in Jira" onClick={() => window.open(t.jiraUrl, '_blank')} />}
          {t.legs.filter(l => l.pr).map(l => (
            <MenuItem key={l.repo} icon="external" label={`${l.repo} PR #${l.pr!.number}`} onClick={() => window.open(l.pr!.url, '_blank')} />
          ))}
          <MenuItem icon="code" label="Copy commit message" onClick={() => navigator.clipboard?.writeText(t.commitMessage)} />
          <div className="menu-sep" />
          <MenuItem icon="trash" label="Discard…" danger onClick={() => ticketAction(app, t, 'discard')} />
        </Menu>
      </div>
    </div>
  )
}
