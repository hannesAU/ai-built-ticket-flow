import { Fragment, ReactNode, useEffect, useState } from 'react'
import { api, PrReviewResult, RemotePrView, RequestCheck, ReviewRepoView, TicketReviewView, timeAgo } from './api'
import { useApp } from './store'
import { Empty, Icon } from './ui'

const verdictMeta: Record<string, { text: string; tone: string }> = {
  'looks-good': { text: 'Looks good', tone: 'ok' },
  'minor-issues': { text: 'Minor issues', tone: 'warn' },
  'needs-changes': { text: 'Needs changes', tone: 'bad' },
}

const requestMeta: Record<RequestCheck['status'], { text: string; tone: string }> = {
  addressed: { text: 'addressed', tone: 'ok' },
  partially: { text: 'partially', tone: 'warn' },
  'not-addressed': { text: 'not addressed', tone: 'bad' },
  unclear: { text: 'unclear', tone: 'muted' },
}

const myReviewText: Record<string, string> = {
  APPROVED: 'You approved',
  CHANGES_REQUESTED: 'You requested changes',
  COMMENTED: 'You commented',
  DISMISSED: 'Your review was dismissed',
}

const short = (sha?: string) => (sha ? sha.slice(0, 8) : '')

export function ReviewsView() {
  const app = useApp()
  const r = app.reviews
  useEffect(() => { api.reviews().then(app.setReviews).catch(() => {}) }, []) // eslint-disable-line react-hooks/exhaustive-deps

  if (!r) return <div className="page muted">Loading…</div>
  return (
    <div className="page reviews">
      <div className="page-head">
        <h1>PR reviews</h1>
        <span className="muted small" title={r.claudePath}>{r.claudePath ? 'Claude Code found' : ''}</span>
      </div>
      <p className="muted lead">
        Open PRs by other people in the repos you watch. Claude Code reviews each new commit and reads the PR straight from GitHub.
        It can't change anything there. It reviews again after new pushes and checks whether the changes you asked for were made.
      </p>
      {r.claudeError && <div className="callout warn">{r.claudeError}</div>}
      {r.running && <RunningCard />}
      {!r.anyEnabled ? (
        <Empty icon="pr" title="No repos are watched">
          Turn on <strong>Review others' PRs</strong> for a repo under Settings → Repositories.
          <div style={{ marginTop: 10 }}><button className="btn small" onClick={() => app.setView('settings')}><Icon name="gear" size={13} /> Settings</button></div>
        </Empty>
      ) : r.repos.map(repo => <RepoSection key={repo.name} repo={repo} />)}
    </div>
  )
}

function RunningCard() {
  const app = useApp()
  const run = app.reviews!.running!
  const [, tick] = useState(0)
  useEffect(() => { const t = window.setInterval(() => tick(x => x + 1), 1000); return () => window.clearInterval(t) }, [])
  const secs = Math.max(0, Math.round((Date.now() - new Date(run.startedAt).getTime()) / 1000))
  return (
    <section className="card review-running">
      <div className="check-head">
        <div className="check-title"><span className="spinner" /> <strong>Reviewing {run.pr.replace(/^ticket:/, 'your ticket ')}</strong>
          <span className="muted small">{Math.floor(secs / 60)}:{String(secs % 60).padStart(2, '0')}</span>
          {app.reviews!.queue.length > 0 && <span className="muted small">· {app.reviews!.queue.length} queued</span>}
        </div>
        <button className="btn small ghost danger" onClick={() => api.cancelReview(run.pr).catch(e => app.toast({ title: 'Could not cancel', body: e.message, tone: 'bad' }))}>
          <Icon name="stop" size={13} /> Cancel
        </button>
      </div>
      {run.progress.length > 0 && <pre className="log tall review-progress">{run.progress.slice(-12).join('\n')}</pre>}
    </section>
  )
}

function RepoSection({ repo }: { repo: ReviewRepoView }) {
  const app = useApp()
  const [busy, setBusy] = useState(false)
  const poll = async () => {
    setBusy(true)
    try { app.setReviews(await api.pollReviews(repo.name)) }
    catch (e) { app.toast({ title: `Poll of ${repo.name} failed`, body: (e as Error).message, tone: 'bad' }) }
    finally { setBusy(false) }
  }
  const polling = busy || repo.polling
  return (
    <section className="review-repo">
      <div className="review-repo-head">
        <h2>{repo.name}</h2>
        {repo.slug && <span className="muted small mono">{repo.slug}</span>}
        <span className="muted small">· {repo.prs.length} open · polled {timeAgo(repo.lastPoll)}</span>
        <span className="grow" />
        <button className="btn small" disabled={polling} onClick={poll} title="Fetch this repo's open PRs now and queue anything new">
          {polling ? <span className="spinner small" /> : <Icon name="refresh" size={13} />} Poll now
        </button>
      </div>
      {repo.error && <div className="callout bad small">{repo.error}</div>}
      {repo.lastPoll && repo.prs.length === 0 && !repo.error && <div className="muted small pad">No open PRs by other people.</div>}
      {!repo.lastPoll && !repo.error && !polling && <div className="muted small pad">Not polled yet. It runs on the PR poll interval, or press Poll now.</div>}
      <div className="review-list">
        {repo.prs.map(pr => <PrCard key={pr.key} pr={pr} />)}
      </div>
    </section>
  )
}

function PrCard({ pr }: { pr: RemotePrView }) {
  const app = useApp()
  const [open, setOpen] = useState(false)
  const [shownId, setShownId] = useState<string | null>(null)
  const latest = pr.reviews[0]
  const shown = pr.reviews.find(r => r.id === shownId) ?? latest
  const isCurrent = latest?.headSha === pr.headSha
  const act = (f: () => Promise<void>, what: string) => f().catch(e => app.toast({ title: `Could not ${what}`, body: (e as Error).message, tone: 'bad' }))
  const statusTone = { reviewing: 'info', queued: 'info', reviewed: 'ok', failed: 'bad', draft: 'muted', skipped: 'muted', pending: 'warn' }[pr.status]
  const v = latest?.status === 'ok' && latest.verdict ? verdictMeta[latest.verdict] : null

  return (
    <div className={`review-pr ${open ? 'open' : ''}`}>
      <div className="review-pr-row" onClick={() => setOpen(o => !o)}>
        <Icon name="chevron" size={14} className={`chev ${open ? 'down' : ''}`} />
        <div className="review-pr-main">
          <div className="review-pr-title">
            <span className="key">#{pr.number}</span> {pr.title}
          </div>
          <div className="muted small review-pr-meta">
            @{pr.author}{pr.authorIsBot ? ' (bot)' : ''} · <span className="mono">{pr.headRef}</span> → <span className="mono">{pr.baseRef}</span>
            {' '}· <span className="tone-ok">+{pr.additions}</span> <span className="tone-bad">−{pr.deletions}</span> in {pr.changedFiles} files · updated {timeAgo(pr.updatedAt)}
          </div>
        </div>
        <div className="row-gap wrap review-pr-chips" onClick={e => e.stopPropagation()}>
          {pr.myReviewState && (
            <a className={`chip tone-${pr.myReviewState === 'APPROVED' ? 'ok' : pr.myReviewState === 'CHANGES_REQUESTED' ? 'bad' : 'muted'}`}
              href={pr.myReviewUrl ?? pr.url} target="_blank" rel="noreferrer" title={pr.myReviewAt ? `${timeAgo(pr.myReviewAt)}` : undefined}>
              {myReviewText[pr.myReviewState] ?? pr.myReviewState}
              {pr.newCommitsSinceMyReview && <span className="chip-flag" title="New commits since your review">· new commits</span>}
            </a>
          )}
          {pr.openThreads > 0 && <span className="chip" title="Your unresolved inline comments">{pr.openThreads} open thread{pr.openThreads === 1 ? '' : 's'}</span>}
          {v && <span className={`chip tone-${v.tone}`} title={isCurrent ? 'Latest commit' : 'An older commit'}>{v.text}{!isCurrent && <span className="chip-extra"> · older</span>}</span>}
          <span className={`chip tone-${statusTone}`}>
            {pr.status === 'reviewing' && <span className="spinner small" />} {pr.statusText}
          </span>
          <a className="btn small" href={pr.filesUrl} target="_blank" rel="noreferrer" title="Open the PR's Files changed tab on GitHub">
            <Icon name="external" size={13} /> GitHub
          </a>
          {pr.status === 'reviewing' || pr.status === 'queued' ? (
            <button className="btn small ghost danger" onClick={() => act(() => api.cancelReview(pr.key), 'cancel')}>Cancel</button>
          ) : (
            <button className="btn small" onClick={() => act(() => api.runReview(pr.key), 'start the review')}
              title={latest ? 'Review the current commit again' : 'Review now'}>
              <Icon name="play" size={12} /> {isCurrent ? 'Re-run' : 'Review now'}
            </button>
          )}
        </div>
      </div>
      {open && (
        <div className="review-body">
          {!shown && <div className="muted small">No review yet. {pr.statusText}.</div>}
          {pr.reviews.length > 1 && (
            <div className="row-gap wrap small">
              <span className="muted">Reviews:</span>
              {pr.reviews.map(r => (
                <button key={r.id} className={`chip-btn ${r.id === shown?.id ? 'active' : ''}`} onClick={() => setShownId(r.id)}
                  title={new Date(r.at).toLocaleString()}>
                  {short(r.headSha)} · {timeAgo(r.at)}{r.status === 'failed' ? ' · failed' : ''}
                </button>
              ))}
            </div>
          )}
          {shown && <ReviewDetail r={shown} latestHead={pr.headSha} link={pr.filesUrl} />}
        </div>
      )}
    </div>
  )
}

/** One review result. latestHead marks whether it's of the current code; link (PR reviews) opens GitHub. */
export function ReviewDetail({ r, latestHead, link, stale }: { r: PrReviewResult; latestHead?: string; link?: string; stale?: string[] }) {
  const isTicket = r.pr.startsWith('ticket:')
  const [log, setLog] = useState<string | null>(null)
  useEffect(() => setLog(null), [r.id])
  const v = r.verdict ? verdictMeta[r.verdict] : null
  const meta = [
    isTicket ? `${r.repos.map(x => x.repo).join(', ')}` : `commit ${short(r.headSha)}${r.headSha === latestHead ? ' (latest)' : ''}`,
    isTicket && r.focus ? `started from ${r.focus}` : null,
    r.sinceSha ? `changes since ${short(r.sinceSha)}` : null,
    isTicket ? null : r.trigger === 'manual' ? 'run by you' : r.trigger === 'commits' ? 'after new commits' : null,
    timeAgo(r.at),
    `${Math.round(r.seconds)}s`,
    r.turns ? `${r.turns} turns` : null,
    r.costUsd != null ? `$${r.costUsd.toFixed(2)}` : null,
    r.model,
  ].filter(Boolean).join(' · ')

  return (
    <div className="review-detail">
      <div className="row-gap wrap">
        {v && <span className={`badge verdict tone-${v.tone}`}>{v.text}</span>}
        {r.risk && <span className={`badge risk-${r.risk}`}>{r.risk} risk</span>}
        <span className="muted small">{meta}</span>
      </div>
      {r.status === 'failed' && <div className="callout bad small">{r.error}</div>}
      {stale && stale.length > 0 && <div className="callout warn small">Stale: the code changed since this review in {stale.join(', ')}.</div>}

      {r.previousRequests.length > 0 && (
        <div>
          <h3>{isTicket ? 'Earlier concerns' : 'Your earlier requests'}</h3>
          <ul className="request-list">
            {r.previousRequests.map((q, i) => (
              <li key={i}>
                <span className={`badge req tone-${requestMeta[q.status]?.tone ?? 'muted'}`}>{requestMeta[q.status]?.text ?? q.status}</span>
                <div><div>{q.request}</div>{q.note && <div className="muted small">{q.note}</div>}</div>
              </li>
            ))}
          </ul>
        </div>
      )}

      {r.summary && (
        <div>
          <h3>What it does</h3>
          <Markdown text={r.summary} />
        </div>
      )}

      {r.status === 'ok' && (
        <div>
          <h3>Concerns {r.concerns.length > 0 && <span className="tab-count">{r.concerns.length}</span>}</h3>
          {r.concerns.length === 0 ? <div className="muted small">None found.</div> : (
            <div className="concern-list">
              {r.concerns.map((c, i) => (
                <div key={i} className={`concern sev-${c.severity}`}>
                  <div className="concern-head">
                    <span className={`badge sev sev-${c.severity}`}>{c.severity}</span>
                    <strong>{c.title}</strong>
                    {(c.repo || c.file) && <span className="mono small muted">{c.repo && c.file ? `${c.repo}: ` : c.repo}{c.file}{c.file && c.line ? `:${c.line}` : ''}</span>}
                  </div>
                  <Markdown text={c.detail} />
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      <div className="row-gap">
        {link && <a className="link small" href={link} target="_blank" rel="noreferrer"><Icon name="external" size={12} /> Review on GitHub</a>}
        <button className="link-btn small" onClick={() => (log === null ? api.reviewLog(r.id).then(setLog).catch(e => setLog(String(e.message))) : setLog(null))}>
          {log === null ? 'Show transcript' : 'Hide transcript'}
        </button>
      </div>
      {log !== null && <pre className="log tall">{log}</pre>}
    </div>
  )
}

/** Drawer tab: review your own in-flight ticket with Claude Code, across all of its repos. */
export function TicketReviewPanel({ ticketKey, repos }: { ticketKey: string; repos: string[] }) {
  const app = useApp()
  const tr: TicketReviewView | undefined = app.reviews?.tickets.find(x => x.key === ticketKey)
  const [shownId, setShownId] = useState<string | null>(null)
  const latest = tr?.reviews[0]
  const shown = tr?.reviews.find(r => r.id === shownId) ?? latest
  const busy = tr?.status === 'reviewing' || tr?.status === 'queued'
  const start = (repo?: string) => api.reviewTicket(ticketKey, repo)
    .catch(e => app.toast({ title: 'Could not start the review', body: (e as Error).message, tone: 'bad' }))
  const run = app.reviews?.running?.pr === 'ticket:' + ticketKey ? app.reviews.running : undefined

  return (
    <div className="review-detail">
      <p className="muted small">
        Claude Code reviews this ticket's changes in {repos.length > 1 ? `all ${repos.length} repos together, so it can spot mismatches between them` : 'this repo'}:
        your uncommitted work while the branch is checked out, or the committed branch otherwise. It can only read. Files you never commit,
        and files your Claude settings deny, are off limits.
      </p>
      <div className="row-gap wrap">
        {busy ? (
          <>
            <span className="chip tone-info"><span className="spinner small" /> {tr!.status === 'queued' ? 'Queued' : 'Reviewing…'}</span>
            <button className="btn small ghost danger" onClick={() => api.cancelReview('ticket:' + ticketKey).catch(() => {})}><Icon name="stop" size={13} /> Cancel</button>
          </>
        ) : repos.length > 1 ? repos.map(r => (
          <button key={r} className="btn small" onClick={() => start(r)} title={`Review all repos of ${ticketKey}, starting from ${r}`}>
            <Icon name="play" size={12} /> Review, from {r}
          </button>
        )) : (
          <button className="btn small" onClick={() => start(repos[0])}><Icon name="play" size={12} /> {latest ? 'Review again' : 'Review with Claude'}</button>
        )}
      </div>
      {run && run.progress.length > 0 && <pre className="log tall review-progress">{run.progress.slice(-12).join('\n')}</pre>}
      {tr && tr.reviews.length > 1 && (
        <div className="row-gap wrap small">
          <span className="muted">Reviews:</span>
          {tr.reviews.map(r => (
            <button key={r.id} className={`chip-btn ${r.id === shown?.id ? 'active' : ''}`} onClick={() => setShownId(r.id)} title={new Date(r.at).toLocaleString()}>
              {timeAgo(r.at)}{r.status === 'failed' ? ' · failed' : ''}
            </button>
          ))}
        </div>
      )}
      {shown ? <ReviewDetail r={shown} stale={shown.id === latest?.id ? tr?.staleRepos : undefined} />
        : !busy && <div className="muted small">Not reviewed yet.</div>}
    </div>
  )
}

// ---- a small, safe Markdown renderer (React nodes only; review text comes from untrusted PR content) ----

function inline(text: string): ReactNode[] {
  const out: ReactNode[] = []
  const re = /(`[^`]+`|\*\*[^*]+\*\*)/g
  let last = 0
  let m: RegExpExecArray | null
  let k = 0
  while ((m = re.exec(text))) {
    if (m.index > last) out.push(text.slice(last, m.index))
    const t = m[0]
    out.push(t.startsWith('`') ? <code key={k++}>{t.slice(1, -1)}</code> : <strong key={k++}>{t.slice(2, -2)}</strong>)
    last = m.index + t.length
  }
  if (last < text.length) out.push(text.slice(last))
  return out
}

export function Markdown({ text }: { text: string }) {
  const lines = text.replace(/\r\n/g, '\n').split('\n')
  const blocks: ReactNode[] = []
  let i = 0
  let k = 0
  while (i < lines.length) {
    const line = lines[i]
    if (line.trim().startsWith('```')) {
      const code: string[] = []
      i++
      while (i < lines.length && !lines[i].trim().startsWith('```')) code.push(lines[i++])
      i++
      blocks.push(<pre key={k++} className="md-code">{code.join('\n')}</pre>)
    } else if (/^\s*([-*]|\d+\.)\s+/.test(line)) {
      const ordered = /^\s*\d+\./.test(line)
      const items: string[] = []
      while (i < lines.length && /^\s*([-*]|\d+\.)\s+/.test(lines[i])) items.push(lines[i++].replace(/^\s*([-*]|\d+\.)\s+/, ''))
      const Tag = ordered ? 'ol' : 'ul'
      blocks.push(<Tag key={k++} className="md-list">{items.map((t, j) => <li key={j}>{inline(t)}</li>)}</Tag>)
    } else if (/^#{1,6}\s/.test(line)) {
      blocks.push(<div key={k++} className="md-h">{inline(line.replace(/^#+\s*/, ''))}</div>)
      i++
    } else if (line.trim() === '') {
      i++
    } else {
      const para: string[] = []
      while (i < lines.length && lines[i].trim() !== '' && !/^\s*([-*]|\d+\.)\s+|^#{1,6}\s|^\s*```/.test(lines[i])) para.push(lines[i++])
      blocks.push(<p key={k++} className="md-p">{para.map((p, j) => <Fragment key={j}>{j > 0 && <br />}{inline(p)}</Fragment>)}</p>)
    }
  }
  return <div className="md">{blocks}</div>
}
