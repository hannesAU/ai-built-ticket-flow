import { Dispatch, ReactNode, SetStateAction, useEffect, useMemo, useRef, useState } from 'react'
import { api, OpPreview, OpProgress, OpStepView, PreviewStep } from './api'
import { useApp } from './store'
import { Icon, Kbd } from './ui'

type Req = Record<string, any>
interface FieldProps { req: Req; setReq: Dispatch<SetStateAction<Req>>; preview: OpPreview | null }

export function OpDialog({ kind, initial }: { kind: string; initial: Req }) {
  const app = useApp()
  const [req, setReq] = useState<Req>(initial)
  const [preview, setPreview] = useState<OpPreview | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [busy, setBusy] = useState(true)
  const [confirm, setConfirm] = useState('')
  const [runId, setRunId] = useState<string | null>(null)
  const [showCmds, setShowCmds] = useState(false)
  const progress = runId ? app.ops[runId] : undefined
  const running = !!runId && (!progress || progress.status === 'running')
  const reqKey = JSON.stringify(req)
  const version = app.state?.version
  const first = useRef(true)

  // live preview (debounced) — re-evaluated when inputs or repo state change
  useEffect(() => {
    if (runId) return
    let live = true
    setBusy(true)
    const h = window.setTimeout(() => {
      api.preview(kind, req)
        .then(p => { if (live) { setPreview(p); setErr(null) } })
        .catch(e => live && setErr(e.message))
        .finally(() => live && setBusy(false))
    }, first.current ? 0 : 300)
    first.current = false
    return () => { live = false; window.clearTimeout(h) }
  }, [kind, reqKey, runId, version]) // eslint-disable-line react-hooks/exhaustive-deps

  // auto-close clean successes
  useEffect(() => {
    if (!progress || progress.status !== 'succeeded') return
    if (kind === 'assign' && progress.key) app.select(progress.key)
    if (progress.warnings.length === 0 && progress.links.length === 0) {
      app.toast({ title: progress.title, body: 'Done', tone: 'ok' })
      app.closeDialog()
    }
  }, [progress?.status]) // eslint-disable-line react-hooks/exhaustive-deps

  const confirmOk = !preview?.confirmText || confirm.trim().toLowerCase() === preview.confirmText.toLowerCase()
  // one op at a time (a Build & test can take minutes): say so up front rather than failing on Run
  const otherOp = !runId ? app.state?.runningOp : undefined
  const canRun = !!preview && preview.blockers.length === 0 && confirmOk && !busy && !runId && !otherOp

  const run = async () => {
    if (!canRun) return
    setErr(null)
    try {
      const p = await api.run(kind, req)
      app.trackOp(p)
      if (kind === 'check') {
        // builds take a while: follow along (or cancel) in the ticket's Checks tab instead of a modal
        app.closeDialog()
        app.setView('board')
        app.select(req.key, 'checks')
        return
      }
      setRunId(p.id)
    } catch (e) {
      setErr((e as Error).message)
    }
  }

  const close = () => { if (!running) app.closeDialog() }

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { e.preventDefault(); close() }
      if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); run() }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  })

  const fields = renderFields(kind, { req, setReq, preview })
  // the assign form shows its own "already in flight" prompt with actions, so don't repeat it as a warning
  const warnings = (preview?.warnings ?? []).filter(w => !(kind === 'assign' && preview?.data?.existing?.length && w.includes('already in flight')))
  const title = progress?.title ?? preview?.title ?? 'Preparing…'
  const failedStep = progress?.steps.slice().reverse().find(s => s.status === 'failed')

  return (
    <div className="modal-backdrop" onMouseDown={e => { if (e.target === e.currentTarget) close() }}>
      <div className={`modal ${preview?.destructive ? 'destructive' : ''}`} role="dialog" aria-modal="true" aria-label={title}>
        <header className="modal-head">
          <h2>{title}</h2>
          <button className="icon-btn" onClick={close} disabled={running} aria-label="Close"><Icon name="close" /></button>
        </header>

        <div className="modal-body">
          {!runId && (
            <>
              {fields}
              {preview?.summary && <div className="summary mono">{preview.summary}</div>}
              {err && <div className="callout bad"><Icon name="warn" size={14} /> {err}</div>}
              {otherOp && (
                <div className="callout info"><span className="spinner small" /> Waiting for “{otherOp.title}” to finish — this can run once it's done.</div>
              )}
              {preview && preview.blockers.length > 0 && (
                <div className="callout bad">
                  <strong>Can't run yet</strong>
                  <ul>{preview.blockers.map(b => <li key={b}>{b}</li>)}</ul>
                </div>
              )}
              {warnings.length > 0 && (
                <div className="callout warn"><ul>{warnings.map(w => <li key={w}>{w}</li>)}</ul></div>
              )}
              {preview && preview.steps.length > 0 && (
                <div className="plan">
                  <div className="plan-head">
                    <span>What will happen</span>
                    <label className="toggle small"><input type="checkbox" checked={showCmds} onChange={e => setShowCmds(e.target.checked)} /> show git commands</label>
                  </div>
                  <PlanSteps steps={preview.steps} showCmds={showCmds} />
                </div>
              )}
              {preview?.confirmText && preview.blockers.length === 0 && (
                <label className="confirm">
                  Type <strong className="mono">{preview.confirmText}</strong> to confirm
                  <input autoFocus value={confirm} onChange={e => setConfirm(e.target.value)} onKeyDown={e => e.key === 'Enter' && run()} />
                </label>
              )}
            </>
          )}
          {runId && <ProgressView progress={progress} />}
        </div>

        <footer className="modal-foot">
          {!runId && (
            <>
              <span className="muted small">{busy ? 'Checking…' : <><Kbd>Ctrl</Kbd>+<Kbd>Enter</Kbd> to run</>}</span>
              <div className="row-gap">
                <button className="btn ghost" onClick={close}>Cancel</button>
                <button className={`btn ${preview?.destructive ? 'danger-solid' : 'primary'}`} disabled={!canRun} onClick={run}>
                  {preview?.buttonLabel ?? 'Run'}
                </button>
              </div>
            </>
          )}
          {runId && running && <span className="muted small"><span className="spinner" /> Running — please leave your repos alone for a moment…</span>}
          {runId && !running && (
            <>
              <span className={`small ${progress?.status === 'failed' ? 'tone-bad' : 'tone-ok'}`}>
                {progress?.status === 'failed' ? 'Stopped — nothing after the failed step was run.' : 'Done.'}
              </span>
              <div className="row-gap">
                {progress?.status === 'failed' && failedStep?.repo && (
                  <>
                    <button className="btn ghost" onClick={() => api.open(failedStep.repo, 'terminal')}><Icon name="terminal" size={14} /> Terminal</button>
                    <button className="btn ghost" onClick={() => api.open(failedStep.repo, 'code')}><Icon name="code" size={14} /> VS Code</button>
                  </>
                )}
                {progress?.status === 'failed' && (
                  <button className="btn" onClick={() => { setRunId(null); setConfirm('') }}><Icon name="refresh" size={14} /> Review &amp; retry</button>
                )}
                <button className="btn primary" onClick={() => app.closeDialog()}>Close</button>
              </div>
            </>
          )}
        </footer>
      </div>
    </div>
  )
}

function PlanSteps({ steps, showCmds }: { steps: PreviewStep[]; showCmds: boolean }) {
  const groups = useMemo(() => {
    const m = new Map<string, PreviewStep[]>()
    for (const s of steps) m.set(s.repo, [...(m.get(s.repo) ?? []), s])
    return [...m.entries()]
  }, [steps])
  return (
    <div className="plan-groups">
      {groups.map(([repo, list]) => (
        <div key={repo || '_'} className="plan-group">
          {repo && <div className="plan-repo">{repo}</div>}
          <ol>
            {list.map((s, i) => (
              <li key={i}>
                {s.text}
                {showCmds && s.commands.length > 0 && <pre className="cmds">{s.commands.join('\n')}</pre>}
              </li>
            ))}
          </ol>
        </div>
      ))}
    </div>
  )
}

const stepIcon: Record<OpStepView['status'], ReactNode> = {
  running: <span className="spinner" />,
  ok: <Icon name="check" size={14} className="tone-ok" />,
  failed: <Icon name="close" size={14} className="tone-bad" />,
  warn: <Icon name="warn" size={14} className="tone-warn" />,
  skipped: <Icon name="circle" size={14} className="muted" />,
}

export function ProgressView({ progress }: { progress?: OpProgress }) {
  const [open, setOpen] = useState<Record<number, boolean>>({})
  if (!progress) return <div className="muted"><span className="spinner" /> Starting…</div>
  return (
    <div className="progress">
      <div className="steps">
        {progress.steps.map((s, i) => {
          const expanded = open[i] ?? (s.status === 'failed' || s.status === 'running')
          return (
            <div key={i} className={`step st-${s.status}`}>
              <button className="step-head" onClick={() => setOpen(o => ({ ...o, [i]: !expanded }))} disabled={s.log.length === 0}>
                <span className="step-icon">{stepIcon[s.status]}</span>
                {s.repo && <span className="step-repo">{s.repo}</span>}
                <span className="step-text">{s.text}</span>
                {s.log.length > 0 && <Icon name={expanded ? 'down' : 'chevron'} size={12} className="muted" />}
              </button>
              {expanded && s.log.length > 0 && <pre className="log">{s.log.slice(-40).join('\n')}</pre>}
            </div>
          )
        })}
      </div>
      {progress.error && <div className="callout bad"><strong>Failed:</strong> {progress.error}</div>}
      {progress.warnings.length > 0 && <div className="callout warn"><ul>{progress.warnings.map(w => <li key={w}>{w}</li>)}</ul></div>}
      {progress.links.length > 0 && (
        <div className="links">
          {progress.links.map(l => <a key={l} className="btn small" href={l} target="_blank" rel="noreferrer"><Icon name="external" size={12} /> {l.replace(/^https:\/\/github.com\//, '')}</a>)}
        </div>
      )}
    </div>
  )
}

// ---------------------------------------------------------------- per-operation inputs

function renderFields(kind: string, p: FieldProps) {
  switch (kind) {
    case 'assign': return <AssignFields {...p} />
    case 'commit': return <MessageFields {...p} />
    case 'push': return <><MessageFields {...p} /><PushOptions {...p} /></>
    case 'discard-ticket': return <DiscardTicketFields {...p} />
    case 'discard-changes': return <DiscardChangesFields {...p} />
    default: return null
  }
}

function TypeToggle({ value, onChange, disabled }: { value: string; onChange(v: string): void; disabled?: boolean }) {
  return (
    <div className="segmented" role="radiogroup" aria-label="Change type">
      {['feat', 'fix'].map(t => (
        <button key={t} role="radio" aria-checked={value === t} disabled={disabled} className={value === t ? 'on' : ''} onClick={() => onChange(t)}>{t}</button>
      ))}
    </div>
  )
}

function AssignFields({ req, setReq, preview }: FieldProps) {
  const app = useApp()
  const data = preview?.data ?? {}
  const existing: { key: string; description: string }[] = data.existing ?? []
  const unassigned = (app.state?.unassigned ?? []).filter(u => !u.blockReason)
  const repos: string[] = req.repos ?? unassigned.map(u => u.repo)
  const tickets = app.state?.tickets ?? []
  const chosen = req.existingKey ? tickets.find(t => t.key === req.existingKey) : undefined

  return (
    <div className="fields">
      {chosen ? (
        <div className="field">
          <span className="label">Adding to</span>
          <div className="chosen">
            <span className="key">{chosen.key}</span> {chosen.description}
            <button className="link-btn" onClick={() => setReq(r => ({ ...r, existingKey: undefined }))}>change</button>
          </div>
        </div>
      ) : (
        <>
          <div className="field-row">
            <label className="field grow-0">
              <span className="label">Ticket</span>
              <input autoFocus className="mono" placeholder="PROJ-1234" value={req.ticket ?? ''} style={{ width: 130 }}
                onChange={e => setReq(r => ({ ...r, ticket: e.target.value }))} />
            </label>
            <label className="field grow">
              <span className="label">Short description <span className="muted">(commit message)</span></span>
              <input placeholder="e.g. Add retry to export job" value={req.description ?? ''}
                onChange={e => setReq(r => ({ ...r, description: e.target.value }))} />
            </label>
            <div className="field grow-0">
              <span className="label">Type</span>
              <TypeToggle value={req.type ?? 'feat'} onChange={v => setReq(r => ({ ...r, type: v }))} />
            </div>
          </div>
          {existing.length > 0 && (
            <div className="callout info">
              <strong>{data.ticket}</strong> is already in flight. Add these changes to it instead of starting {data.key}?
              <div className="row-gap wrap" style={{ marginTop: 8 }}>
                {existing.map(x => (
                  <button key={x.key} className="btn small" onClick={() => setReq(r => ({ ...r, existingKey: x.key }))}>
                    Add to {x.key}{x.description ? ` — ${x.description}` : ''}
                  </button>
                ))}
              </div>
            </div>
          )}
          {existing.length === 0 && tickets.length > 0 && (
            <div className="quick-pick">
              <span className="muted small">or add to an in-flight ticket:</span>
              {tickets.slice(0, 8).map(t => (
                <button key={t.key} className="chip-btn" title={t.description} onClick={() => setReq(r => ({ ...r, existingKey: t.key }))}>{t.key}</button>
              ))}
            </div>
          )}
        </>
      )}
      {unassigned.length > 1 && (
        <div className="field">
          <span className="label">Repos</span>
          <div className="checks">
            {unassigned.map(u => (
              <label key={u.repo} className="check">
                <input type="checkbox" checked={repos.includes(u.repo)}
                  onChange={e => setReq(r => ({ ...r, repos: e.target.checked ? [...repos, u.repo] : repos.filter(x => x !== u.repo) }))} />
                {u.repo} <span className="muted small">({u.files.length} files on {u.branch})</span>
              </label>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}

function MessageFields({ req, setReq, preview }: FieldProps) {
  useEffect(() => {
    if (preview?.data && req.description === undefined)
      setReq(r => ({ ...r, description: preview.data.description, type: preview.data.type }))
  }, [preview]) // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <div className="fields">
      <div className="field-row">
        <label className="field grow">
          <span className="label">Description</span>
          <input value={req.description ?? ''} onChange={e => setReq(r => ({ ...r, description: e.target.value }))} />
        </label>
        <div className="field grow-0">
          <span className="label">Type</span>
          <TypeToggle value={req.type ?? preview?.data?.type ?? 'feat'} onChange={v => setReq(r => ({ ...r, type: v }))} />
        </div>
      </div>
    </div>
  )
}

function PushOptions({ req, setReq }: FieldProps) {
  return (
    <div className="checks inline">
      <label className="check"><input type="checkbox" checked={!!req.draft} onChange={e => setReq(r => ({ ...r, draft: e.target.checked }))} /> Create new PRs as drafts</label>
      <label className="check danger"><input type="checkbox" checked={!!req.overwriteRemote} onChange={e => setReq(r => ({ ...r, overwriteRemote: e.target.checked }))} /> Overwrite remote branch if someone else pushed to it</label>
    </div>
  )
}

function DiscardTicketFields({ req, setReq, preview }: FieldProps) {
  const app = useApp()
  const t = app.state?.tickets.find(x => x.key === req.key)
  const all = t?.legs.map(l => l.repo) ?? []
  const repos: string[] = req.repos ?? all
  return (
    <div className="fields">
      {all.length > 1 && (
        <div className="field">
          <span className="label">Repos</span>
          <div className="checks">
            {all.map(r => (
              <label key={r} className="check">
                <input type="checkbox" checked={repos.includes(r)}
                  onChange={e => setReq(x => ({ ...x, repos: e.target.checked ? [...repos, r] : repos.filter(y => y !== r) }))} /> {r}
              </label>
            ))}
          </div>
        </div>
      )}
      {(preview?.data?.hasPrs || preview?.data?.pushed) && (
        <div className="checks inline">
          {preview?.data?.hasPrs && <label className="check"><input type="checkbox" checked={!!req.closePrs} onChange={e => setReq(x => ({ ...x, closePrs: e.target.checked }))} /> Close the open PRs</label>}
          {preview?.data?.pushed && <label className="check"><input type="checkbox" checked={!!req.deleteRemote} onChange={e => setReq(x => ({ ...x, deleteRemote: e.target.checked }))} /> Delete remote branches</label>}
        </div>
      )}
    </div>
  )
}

function DiscardChangesFields({ req, setReq }: FieldProps) {
  const app = useApp()
  const repo = app.state?.repos.find(r => r.name === req.repo)
  const paths: string[] = req.paths ?? []
  if (!repo) return null
  return (
    <div className="fields">
      <span className="label">Files to discard in {repo.name} <span className="muted">(on {repo.currentBranch})</span></span>
      <div className="file-checks">
        {repo.files.map(f => (
          <label key={f.path} className={`check file ${f.excluded ? 'excluded' : ''}`}>
            <input type="checkbox" checked={paths.includes(f.path)}
              onChange={e => setReq(r => ({ ...r, paths: e.target.checked ? [...paths, f.path] : paths.filter(p => p !== f.path) }))} />
            <span className={`st st-${f.status}`}>{f.status}</span>
            <span className="file-path">{f.path}</span>
            {f.excluded && <span className="muted small">excluded</span>}
          </label>
        ))}
      </div>
    </div>
  )
}
