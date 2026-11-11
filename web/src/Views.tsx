import { useEffect, useMemo, useState } from 'react'
import { api, AppConfig, ArchivedTicket, BackupView, RepoConfig, ReviewConfig, timeAgo } from './api'
import { useApp } from './store'
import { Empty, Icon, TypePill } from './ui'

export function ArchiveView() {
  const [items, setItems] = useState<ArchivedTicket[] | null>(null)
  const [q, setQ] = useState('')
  useEffect(() => { api.archive().then(setItems).catch(() => setItems([])) }, [])
  const filtered = (items ?? []).filter(a => `${a.key} ${a.description}`.toLowerCase().includes(q.toLowerCase()))
  return (
    <div className="page">
      <div className="page-head">
        <h1>Archive</h1>
        <input className="search" placeholder="Search completed tickets…" value={q} onChange={e => setQ(e.target.value)} />
      </div>
      {items === null ? <div className="muted">Loading…</div> : filtered.length === 0 ? (
        <Empty icon="archive" title={items.length === 0 ? 'Nothing completed yet' : 'No matches'}>Completed tickets land here after their PRs are merged and branches cleaned up.</Empty>
      ) : (
        <table className="table">
          <thead><tr><th>Ticket</th><th>Description</th><th>Pull requests</th><th>Completed</th></tr></thead>
          <tbody>
            {filtered.map(a => (
              <tr key={a.key + a.completedAt}>
                <td><span className="key">{a.key}</span> <TypePill type={a.type} /></td>
                <td>{a.description}</td>
                <td className="row-gap wrap">
                  {a.legs.map(l => l.prUrl
                    ? <a key={l.repo} className="link small" href={l.prUrl} target="_blank" rel="noreferrer">{l.repo} #{l.prNumber}</a>
                    : <span key={l.repo} className="muted small">{l.repo}</span>)}
                </td>
                <td className="muted small" title={new Date(a.completedAt).toLocaleString()}>{timeAgo(a.completedAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}

export function RecoveryView() {
  const app = useApp()
  const [items, setItems] = useState<BackupView[] | null>(null)
  const version = app.state?.version
  useEffect(() => { api.backups().then(setItems).catch(() => setItems([])) }, [version])
  return (
    <div className="page">
      <div className="page-head">
        <h1>Recovery</h1>
      </div>
      <p className="muted lead">
        Before anything is switched, discarded or deleted, Ticket Flow snapshots your uncommitted files and keeps deleted branches.
        Snapshots are local git refs under <code>refs/ticket-flow/</code> (never pushed) and are pruned after the retention period on “Fetch all”.
      </p>
      {items === null ? <div className="muted">Loading…</div> : items.length === 0 ? (
        <Empty icon="life" title="No backups yet" />
      ) : (
        <table className="table">
          <thead><tr><th>When</th><th>Repo</th><th>What</th><th /></tr></thead>
          <tbody>
            {items.map(b => (
              <tr key={b.repo + b.ref}>
                <td className="muted small nowrap" title={new Date(b.date).toLocaleString()}>{timeAgo(b.date)}</td>
                <td>{b.repo}</td>
                <td>
                  <span className={`badge ${b.kind === 'branch' ? 'info' : ''}`}>{b.kind === 'branch' ? 'deleted branch' : 'files'}</span> {b.title}
                  <div className="muted small pre">{b.detail}</div>
                </td>
                <td className="right">
                  <button className="btn small" onClick={() => app.openOp('restore', { repo: b.repo, ref: b.ref })}>
                    {b.kind === 'branch' ? 'Restore branch…' : 'Restore files…'}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}

export function SettingsView() {
  const app = useApp()
  const [cfg, setCfg] = useState<AppConfig | null>(null)
  const [saved, setSaved] = useState<string>('')
  const [paths, setPaths] = useState<{ config: string; data: string; home: string } | null>(null)
  const [hook, setHook] = useState<{ path: string; patterns: string[]; error?: string } | null>(null)
  const [newPath, setNewPath] = useState('')
  const [scanDir, setScanDir] = useState('')
  const [found, setFound] = useState<string[] | null>(null)
  const [pick, setPick] = useState<string[]>([])
  const [excludedText, setExcludedText] = useState('')
  const [matches, setMatches] = useState<{ repo: string; matches: string[]; others: string[] }[]>([])
  const [saving, setSaving] = useState(false)
  // what "Build & test" auto-detects per repo path (shown as the placeholder of the command fields)
  const [detected, setDetected] = useState<Record<string, { build?: string; test?: string }>>({})
  const loadDetected = () => api.detectedChecks()
    .then(list => setDetected(Object.fromEntries(list.map(d => [d.path.toLowerCase(), d]))))
    .catch(() => {})

  useEffect(() => {
    loadDetected()
    api.config().then(r => {
      setCfg(r.config)
      setSaved(JSON.stringify(r.config))
      setPaths(r.paths)
      setScanDir(d => d || r.paths.home)
      setHook(r.claudeHook)
      setExcludedText(r.config.excluded.join('\n'))
    })
  }, [])

  const globs = useMemo(() => excludedText.split('\n').map(s => s.trim()).filter(Boolean), [excludedText])
  const useHook = cfg?.excludeFromClaudeHook ?? true
  useEffect(() => {
    const h = window.setTimeout(() => api.excludedPreview(globs, useHook).then(setMatches).catch(() => {}), 300)
    return () => window.clearTimeout(h)
  }, [globs, useHook])

  if (!cfg) return <div className="page muted">Loading…</div>
  const current: AppConfig = { ...cfg, excluded: globs }
  const dirty = JSON.stringify(current) !== saved

  const rv: ReviewConfig = cfg.reviews ?? { timeoutMinutes: 20, botPrs: 'hidden' }
  const setReviews = (patch: Partial<ReviewConfig>) => setCfg(c => c && { ...c, reviews: { ...rv, ...patch } })
  const setRepo = (i: number, patch: Partial<RepoConfig>) =>
    setCfg(c => c && { ...c, repos: c.repos.map((r, j) => (j === i ? { ...r, ...patch } : r)) })
  const move = (i: number, d: number) => setCfg(c => {
    if (!c) return c
    const repos = [...c.repos]
    const j = i + d
    if (j < 0 || j >= repos.length) return c
    ;[repos[i], repos[j]] = [repos[j], repos[i]]
    return { ...c, repos }
  })
  const addRepos = (ps: string[]) => setCfg(c => c && {
    ...c,
    repos: [...c.repos, ...ps.filter(p => !c.repos.some(r => r.path.toLowerCase() === p.toLowerCase()))
      .map(p => ({ path: p, mergeMethod: 'squash', requireApproval: true }))],
  })

  const save = async () => {
    setSaving(true)
    try {
      const r = await api.saveConfig(current)
      loadDetected()
      setCfg(r)
      setSaved(JSON.stringify(r))
      setExcludedText(r.excluded.join('\n'))
      app.toast({ title: 'Settings saved', tone: 'ok' })
    } catch (e) {
      app.toast({ title: 'Could not save settings', body: (e as Error).message, tone: 'bad' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="page settings">
      <div className="page-head">
        <h1>Settings</h1>
        <div className="row-gap">
          {dirty && <span className="muted small">Unsaved changes</span>}
          <button className="btn primary" disabled={!dirty || saving} onClick={save}>Save</button>
        </div>
      </div>

      <section className="card">
        <h2>Repositories</h2>
        <p className="muted small">The order here is the merge order used by “Complete” (e.g. shared libraries first).</p>
        {cfg.repos.length === 0 && <div className="muted small pad">No repos yet — add one below or scan a folder.</div>}
        {cfg.repos.map((r, i) => (
          <div key={i} className="repo-cfg">
            <div className="reorder">
              <button className="icon-btn" disabled={i === 0} onClick={() => move(i, -1)} aria-label="Move up"><Icon name="up" size={14} /></button>
              <span className="muted small">{i + 1}</span>
            </div>
            <label className="field grow"><span className="label">Folder</span><input className="mono" value={r.path} onChange={e => setRepo(i, { path: e.target.value })} /></label>
            <label className="field w-140"><span className="label">Name</span><input value={r.name ?? ''} placeholder={r.path.split(/[\\/]/).filter(Boolean).pop()} onChange={e => setRepo(i, { name: e.target.value || undefined })} /></label>
            <label className="field w-120"><span className="label">Default branch</span><input value={r.defaultBranch ?? ''} placeholder="auto" onChange={e => setRepo(i, { defaultBranch: e.target.value || undefined })} /></label>
            <label className="field w-110"><span className="label">Merge</span>
              <select value={r.mergeMethod} onChange={e => setRepo(i, { mergeMethod: e.target.value })}>
                <option value="squash">squash</option><option value="rebase">rebase</option><option value="merge">merge</option>
              </select>
            </label>
            <label className="check w-150" title="When off, a PR in a repo without review rules counts as approved">
              <input type="checkbox" checked={r.requireApproval} onChange={e => setRepo(i, { requireApproval: e.target.checked })} /> Require approval
            </label>
            <label className="check w-170" title="Poll this repo for other people's open PRs and review each new commit with Claude Code (see PR reviews below)">
              <input type="checkbox" checked={r.reviewPrs ?? false} onChange={e => setRepo(i, { reviewPrs: e.target.checked })} /> Review others' PRs
            </label>
            <button className="icon-btn danger" onClick={() => setCfg(c => c && { ...c, repos: c.repos.filter((_, j) => j !== i) })} aria-label="Remove"><Icon name="trash" size={14} /></button>
            <div className="repo-cfg-checks">
              {(['buildCommand', 'testCommand'] as const).map(f => {
                const auto = detected[r.path.toLowerCase()]?.[f === 'buildCommand' ? 'build' : 'test']
                return (
                  <label key={f} className="field grow">
                    <span className="label">{f === 'buildCommand' ? 'Build command' : 'Unit test command'} <span className="muted">(Build &amp; test · “none” to skip)</span></span>
                    <input className="mono small-input" value={r[f] ?? ''} placeholder={auto ? `auto: ${auto}` : 'auto: nothing detected'}
                      title={auto ? `Detected: ${auto}` : undefined} onChange={e => setRepo(i, { [f]: e.target.value || undefined })} />
                  </label>
                )
              })}
            </div>
          </div>
        ))}
        <div className="add-row">
          <input className="mono grow" placeholder="Path to a git repo" value={newPath} onChange={e => setNewPath(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter' && newPath.trim()) { addRepos([newPath.trim()]); setNewPath('') } }} />
          <button className="btn" disabled={!newPath.trim()} onClick={() => { addRepos([newPath.trim()]); setNewPath('') }}><Icon name="plus" size={14} /> Add</button>
        </div>
        <div className="add-row">
          <input className="mono grow" placeholder="Folder that contains your repos" value={scanDir} onChange={e => setScanDir(e.target.value)} />
          <button className="btn" onClick={() => api.discover(scanDir).then(f => { setFound(f); setPick([]) }).catch(e => app.toast({ title: 'Scan failed', body: e.message, tone: 'bad' }))}>
            <Icon name="search" size={14} /> Find repos in folder
          </button>
        </div>
        {found && (
          <div className="found">
            {found.length === 0 && <span className="muted small">No git repos found there.</span>}
            {found.map(f => {
              const already = cfg.repos.some(r => r.path.toLowerCase() === f.toLowerCase())
              return (
                <label key={f} className="check">
                  <input type="checkbox" disabled={already} checked={already || pick.includes(f)}
                    onChange={e => setPick(p => (e.target.checked ? [...p, f] : p.filter(x => x !== f)))} />
                  <span className="mono small">{f}</span>{already && <span className="muted small"> (added)</span>}
                </label>
              )
            })}
            {pick.length > 0 && <button className="btn small primary" onClick={() => { addRepos(pick); setFound(null) }}>Add {pick.length} selected</button>}
          </div>
        )}
      </section>

      <section className="card">
        <h2>Never commit these files</h2>
        <p className="muted small">Matching files are carried across branch switches byte-for-byte but never committed or discarded.</p>
        <div className="hook-box">
          <label className="check">
            <input type="checkbox" checked={cfg.excludeFromClaudeHook} onChange={e => setCfg({ ...cfg, excludeFromClaudeHook: e.target.checked })} />
            <strong>Use my Claude Code protected-file hook</strong>
          </label>
          <div className="muted small">
            Reads the deny pattern(s) from the PreToolUse hook in <span className="mono">{hook?.path}</span> on every scan, so this list stays in sync with the hook.
          </div>
          {hook?.error && <div className="callout warn small">{hook.error} — only the globs below apply.</div>}
          {hook && hook.patterns.length > 0 && (
            <div className="hook-patterns">
              {hook.patterns.flatMap(p => p.split('|')).map(p => <code key={p}>{p}</code>)}
            </div>
          )}
        </div>
        <span className="label">Additional globs</span>
        <p className="muted small">One per line, matched against repo-relative paths (<code>*</code>, <code>**</code>, <code>?</code>; no <code>/</code> = any folder).</p>
        <div className="two-col">
          <textarea className="mono" rows={5} value={excludedText} placeholder="e.g. **/local.settings.json" onChange={e => setExcludedText(e.target.value)} spellCheck={false} />
          <div className="match-preview">
            <div className="label">Currently changed files</div>
            {matches.every(m => m.matches.length + m.others.length === 0) && <div className="muted small">No uncommitted files right now.</div>}
            {matches.map(m => (m.matches.length + m.others.length > 0) && (
              <div key={m.repo} className="small">
                <strong>{m.repo}</strong>
                {m.matches.map(p => <div key={p} className="mono excluded-match">✕ {p}</div>)}
                {m.others.map(p => <div key={p} className="mono muted">· {p}</div>)}
              </div>
            ))}
          </div>
        </div>
      </section>

      <section className="card">
        <h2>Tickets &amp; GitHub</h2>
        <div className="field-row">
          <label className="field w-200"><span className="label">Ticket prefixes</span>
            <input value={cfg.ticketPrefixes.join(', ')} onChange={e => setCfg({ ...cfg, ticketPrefixes: e.target.value.split(',').map(s => s.trim().toUpperCase()).filter(Boolean) })} />
          </label>
          <label className="field w-200"><span className="label">GitHub account (gh)</span>
            <input value={cfg.gitHub.user} placeholder="active gh account" onChange={e => setCfg({ ...cfg, gitHub: { user: e.target.value.trim() } })} />
          </label>
          <label className="field grow"><span className="label">Jira browse URL</span>
            <input value={cfg.jiraBaseUrl ?? ''} placeholder="https://yourorg.atlassian.net/browse/" onChange={e => setCfg({ ...cfg, jiraBaseUrl: e.target.value || undefined })} />
          </label>
        </div>
        <div className="field-row">
          <label className="field w-160"><span className="label">PR poll (UI open), s</span>
            <input type="number" min={15} value={cfg.pollSeconds} onChange={e => setCfg({ ...cfg, pollSeconds: +e.target.value })} /></label>
          <label className="field w-160"><span className="label">PR poll (background), s</span>
            <input type="number" min={60} value={cfg.backgroundPollSeconds} onChange={e => setCfg({ ...cfg, backgroundPollSeconds: +e.target.value })} /></label>
          <label className="field w-160"><span className="label">Keep backups, days</span>
            <input type="number" min={1} value={cfg.backupRetentionDays} onChange={e => setCfg({ ...cfg, backupRetentionDays: +e.target.value })} /></label>
          <label className="field w-160" title="Build & test stops any build or test command that runs longer than this"><span className="label">Build &amp; test timeout, min</span>
            <input type="number" min={1} value={cfg.checkTimeoutMinutes} onChange={e => setCfg({ ...cfg, checkTimeoutMinutes: +e.target.value })} /></label>
          <label className="field w-120"><span className="label">Port (restart)</span>
            <input type="number" value={cfg.port} onChange={e => setCfg({ ...cfg, port: +e.target.value })} /></label>
        </div>
        <div className="row-gap">
          <label className="check"><input type="checkbox" checked={cfg.openBrowserOnStart} onChange={e => setCfg({ ...cfg, openBrowserOnStart: e.target.checked })} /> Open browser when the server starts</label>
          {'Notification' in window && Notification.permission !== 'granted' && (
            <button className="btn small" onClick={() => Notification.requestPermission()}>Enable desktop notifications</button>
          )}
        </div>
      </section>
      <section className="card">
        <h2>PR reviews (Claude Code)</h2>
        <p className="muted small">
          For repos with <strong>Review others' PRs</strong> on, Ticket Flow checks for open PRs on the PR poll interval above and runs
          Claude Code headless (<code>claude -p</code>) once for each new commit. Claude can only run a read-only helper that fetches that one
          PR from GitHub. It gets no other tools, your Claude settings and MCP servers aren't loaded, and it can't comment, approve or merge.
          Each review uses your Claude Code plan.
        </p>
        <div className="field-row">
          <label className="field grow"><span className="label">Claude Code executable</span>
            <input className="mono small-input" value={rv.claudePath ?? ''}
              placeholder={app.reviews?.claudePath ? `auto: ${app.reviews.claudePath}` : 'auto: not found — enter the path to claude.exe'}
              onChange={e => setReviews({ claudePath: e.target.value || undefined })} />
          </label>
          <label className="field w-140"><span className="label">Model</span>
            <input value={rv.model ?? ''} placeholder="default" title="e.g. sonnet, opus or a full model id"
              onChange={e => setReviews({ model: e.target.value || undefined })} />
          </label>
          <label className="field w-140"><span className="label">Timeout, min</span>
            <input type="number" min={1} value={rv.timeoutMinutes} onChange={e => setReviews({ timeoutMinutes: +e.target.value })} />
          </label>
          <label className="field w-140" title="Passed to --max-budget-usd; empty = no limit"><span className="label">Max per review, USD</span>
            <input type="number" min={0} step={0.5} value={rv.maxBudgetUsd ?? ''} placeholder="no limit"
              onChange={e => setReviews({ maxBudgetUsd: e.target.value === '' ? undefined : +e.target.value })} />
          </label>
        </div>
        {app.reviews?.claudeError && !rv.claudePath && <div className="callout warn small">{app.reviews.claudeError}</div>}
        <label className="field w-200" title="PRs opened by bots such as dependabot"><span className="label">Bot PRs</span>
          <select value={rv.botPrs ?? 'hidden'} onChange={e => setReviews({ botPrs: e.target.value as ReviewConfig['botPrs'] })}>
            <option value="hidden">Hidden (not listed)</option>
            <option value="manual">Manual (listed, Review now)</option>
            <option value="auto">Auto (reviewed like any PR)</option>
          </select>
        </label>
        <label className="field"><span className="label">Extra review instructions</span>
          <textarea rows={3} value={rv.instructions ?? ''} placeholder="e.g. We use EF Core migrations; flag any migration that drops or renames a column."
            onChange={e => setReviews({ instructions: e.target.value || undefined })} />
        </label>
      </section>
      {paths && <p className="muted small">Config: <span className="mono">{paths.config}</span> · Data: <span className="mono">{paths.data}</span></p>}
    </div>
  )
}
