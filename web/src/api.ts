// Types mirror the C# DTOs (camelCase JSON).

export interface FileView { path: string; origPath?: string; status: string; excluded: boolean }

export interface RepoView {
  name: string; path: string; ok: boolean; error?: string; defaultBranch: string; currentBranch?: string
  dirty: number; dirtyExcluded: number; ahead: number; behind: number; inProgress?: string
  workingKey?: string; gitHubSlug?: string; files: FileView[]
}

export interface UnassignedView {
  repo: string; branch: string; isDefault: boolean; files: FileView[]; excludedFiles: FileView[]; blockReason?: string
}

export interface PrView {
  number: number; url: string; state: string; isDraft: boolean; status: string; statusText: string
  tone: 'ok' | 'warn' | 'bad' | 'info' | 'done'; mergeable?: string; mergeState?: string; reviewDecision?: string
  checks?: string; headSha: string; title: string
}

export interface LegView {
  repo: string; branch: string; remoteBranch: string; state: string; stateText: string; isCurrent: boolean
  ahead: number; behind: number; sha: string; subject: string; files: FileView[]; pr?: PrView
  localAhead: boolean; ready: boolean; needsPush: boolean
}

export interface ActionState { enabled: boolean; reason?: string; label: string }

export type Stage = 'working' | 'parked' | 'review' | 'ready'

export interface TicketView {
  key: string; ticket: string; description: string; type: 'feat' | 'fix'; stage: Stage; statusText: string
  commitMessage: string; legs: LegView[]; flags: string[]; actions: Record<string, ActionState>
  primaryAction?: string; jiraUrl?: string; check?: CheckView
}

export interface TestCounts { total: number; passed: number; failed: number; skipped: number }

export interface CheckPhase {
  phase: 'build' | 'test'; label: string; command: string; status: 'ok' | 'failed' | 'skipped'; exitCode?: number
  seconds: number; summary: string; tests?: TestCounts; errors: string[]
}

export interface RepoCheck { repo: string; status: 'passed' | 'failed' | 'skipped'; summary: string; phases: CheckPhase[] }

/** Latest "Build & test" result of a ticket; stale once its code changed since. */
export interface CheckView {
  status: 'passed' | 'failed'; stale: boolean; text: string; at: string; seconds: number; staleRepos: string[]; repos: RepoCheck[]
}

export interface OpStepView { repo: string; text: string; status: 'running' | 'ok' | 'failed' | 'skipped' | 'warn'; log: string[] }

export interface OpProgress {
  id: string; kind: string; title: string; key?: string; status: 'running' | 'succeeded' | 'failed'
  cancellable?: boolean; error?: string; warnings: string[]; links: string[]; steps: OpStepView[]; startedAt: string; endedAt?: string
}

export interface DashboardState {
  version: number; scannedAt: string; configured: boolean; repos: RepoView[]; unassigned: UnassignedView[]
  tickets: TicketView[]; gitHub: { user?: string; ok: boolean; error?: string; lastPoll?: string }
  jiraBaseUrl?: string; runningOp?: OpProgress; archiveCount: number
}

export interface PreviewStep { repo: string; text: string; commands: string[] }

export interface OpPreview {
  kind: string; title: string; summary: string; steps: PreviewStep[]; warnings: string[]; blockers: string[]
  buttonLabel: string; confirmText?: string; destructive: boolean; data?: any
}

export interface RepoConfig {
  path: string; name?: string; defaultBranch?: string; mergeMethod: string; requireApproval: boolean
  buildCommand?: string; testCommand?: string; reviewPrs?: boolean
}

export interface ReviewConfig {
  claudePath?: string; model?: string; timeoutMinutes: number; maxBudgetUsd?: number; botPrs: 'hidden' | 'manual' | 'auto'; instructions?: string
}

export interface ReviewConcern { severity: 'high' | 'medium' | 'low'; title: string; detail: string; file?: string; line?: number; repo?: string }

export interface RequestCheck { request: string; status: 'addressed' | 'partially' | 'not-addressed' | 'unclear'; note: string }

export interface PrReviewResult {
  id: string; pr: string; focus?: string; repos: { repo: string; fingerprint?: string; tree?: string }[]; headSha: string; sinceSha?: string; trigger: 'new' | 'commits' | 'manual'; at: string; seconds: number
  status: 'ok' | 'failed'; error?: string; verdict?: 'looks-good' | 'minor-issues' | 'needs-changes'; risk?: 'low' | 'medium' | 'high'
  summary?: string; concerns: ReviewConcern[]; previousRequests: RequestCheck[]; costUsd?: number; turns?: number; model?: string
}

export type RemotePrStatus = 'reviewing' | 'queued' | 'reviewed' | 'failed' | 'draft' | 'skipped' | 'pending'

export interface RemotePrView {
  key: string; repo: string; number: number; url: string; filesUrl: string; title: string; author: string; authorIsBot: boolean
  isDraft: boolean; headRef: string; baseRef: string; headSha: string; additions: number; deletions: number; changedFiles: number
  updatedAt: string; myReviewState?: string; myReviewAt?: string; myReviewUrl?: string; newCommitsSinceMyReview: boolean
  openThreads: number; status: RemotePrStatus; statusText: string; reviews: PrReviewResult[]
}

export interface ReviewRepoView { name: string; slug?: string; lastPoll?: string; polling: boolean; error?: string; prs: RemotePrView[] }

/** Review of one of your own in-flight tickets (all of its repos). */
export interface TicketReviewView {
  key: string; status: 'reviewing' | 'queued' | 'reviewed' | 'failed' | 'none'; stale: boolean; staleRepos: string[]; reviews: PrReviewResult[]
}

export interface ReviewsState {
  anyEnabled: boolean; claudePath?: string; claudeError?: string; repos: ReviewRepoView[]
  running?: { pr: string; trigger: string; startedAt: string; progress: string[] }; queue: string[]; tickets: TicketReviewView[]
}

export interface AppConfig {
  ticketPrefixes: string[]; repos: RepoConfig[]; excluded: string[]; excludeFromClaudeHook: boolean; claudeSettingsPath?: string
  gitHub: { user: string }; reviews: ReviewConfig
  jiraBaseUrl?: string; pollSeconds: number; backgroundPollSeconds: number; backupRetentionDays: number
  checkTimeoutMinutes: number; port: number; openBrowserOnStart: boolean
}

export interface ArchivedTicket {
  key: string; ticket: string; description: string; type: string; completedAt: string
  legs: { repo: string; remoteBranch: string; prNumber?: number; prUrl?: string; mergeSha?: string }[]
}

export interface BackupView { repo: string; kind: 'files' | 'branch'; ref: string; sha: string; date: string; title: string; detail: string }

export interface JournalEntry {
  id: string; kind: string; title: string; key?: string; status: string; error?: string
  startedAt: string; endedAt?: string; steps: OpStepView[]
}

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message) }
}

async function call<T>(method: string, url: string, body?: unknown): Promise<T> {
  const res = await fetch(url, {
    method,
    credentials: 'same-origin',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!res.ok) {
    let msg = res.statusText
    try { const j = await res.json(); msg = j.error ?? j.title ?? msg } catch { /* not json */ }
    throw new ApiError(res.status, msg)
  }
  if (res.status === 204) return undefined as T
  const type = res.headers.get('content-type') ?? ''
  return (type.includes('json') ? res.json() : res.text()) as Promise<T>
}

export const api = {
  state: () => call<DashboardState>('GET', '/api/state'),
  refresh: () => call<DashboardState>('POST', '/api/refresh'),
  visible: () => call<void>('POST', '/api/visible'),
  preview: (kind: string, req: object) => call<OpPreview>('POST', `/api/ops/${kind}/preview`, req),
  run: (kind: string, req: object) => call<OpProgress>('POST', `/api/ops/${kind}/run`, req),
  cancelOp: () => call<void>('POST', '/api/ops/cancel'),
  checkLog: (key: string, repo: string, index: number) =>
    call<string>('GET', `/api/checks/log?key=${encodeURIComponent(key)}&repo=${encodeURIComponent(repo)}&index=${index}`),
  detectedChecks: () => call<{ path: string; build?: string; test?: string }[]>('GET', '/api/config/checks'),
  config: () => call<{ config: AppConfig; paths: { config: string; data: string; home: string }; claudeHook: { path: string; patterns: string[]; error?: string } }>('GET', '/api/config'),
  saveConfig: (c: AppConfig) => call<AppConfig>('PUT', '/api/config', c),
  discover: (folder: string) => call<string[]>('POST', '/api/config/discover', { folder }),
  excludedPreview: (globs: string[], useClaudeHook: boolean) =>
    call<{ repo: string; matches: string[]; others: string[] }[]>('POST', '/api/config/excluded-preview', { globs, useClaudeHook }),
  archive: () => call<ArchivedTicket[]>('GET', '/api/archive'),
  journal: (key?: string) => call<JournalEntry[]>('GET', `/api/journal${key ? `?key=${encodeURIComponent(key)}` : ''}`),
  backups: () => call<BackupView[]>('GET', '/api/backups'),
  diff: (repo: string, path: string, key?: string) =>
    call<string>('GET', `/api/diff?repo=${encodeURIComponent(repo)}&path=${encodeURIComponent(path)}${key ? `&key=${encodeURIComponent(key)}` : ''}`),
  reviews: () => call<ReviewsState>('GET', '/api/reviews'),
  pollReviews: (repo?: string) => call<ReviewsState>('POST', '/api/reviews/poll', { repo }),
  runReview: (pr: string) => call<void>('POST', '/api/reviews/run', { pr }),
  reviewTicket: (key: string, repo?: string) => call<void>('POST', '/api/reviews/ticket', { key, repo }),
  cancelReview: (pr: string) => call<void>('POST', '/api/reviews/cancel', { pr }),
  reviewLog: (id: string) => call<string>('GET', `/api/reviews/log?id=${encodeURIComponent(id)}`),
  open: (repo: string, withApp: 'code' | 'terminal' | 'explorer', path?: string) =>
    call<void>('POST', '/api/open', { repo, with: withApp, path }),
}

export function timeAgo(iso?: string): string {
  if (!iso) return 'never'
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000)
  if (s < 10) return 'just now'
  if (s < 60) return `${Math.floor(s)}s ago`
  if (s < 3600) return `${Math.floor(s / 60)}m ago`
  if (s < 86400) return `${Math.floor(s / 3600)}h ago`
  return `${Math.floor(s / 86400)}d ago`
}

export const plural = (n: number, w: string) => `${n} ${w}${n === 1 ? '' : 's'}`
