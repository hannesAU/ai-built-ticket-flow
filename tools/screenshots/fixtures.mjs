// Mock API data for README screenshots. Generic, made-up project: an "api", "web" and "shared" repo.
const now = Date.now()
const ago = (min) => new Date(now - min * 60_000).toISOString()
const sha = (s) => (s.repeat(40)).slice(0, 40)

const on = (label, extra = {}) => ({ enabled: true, label, ...extra })
const off = (label, reason) => ({ enabled: false, label, reason })

const f = (path, status = 'M') => ({ path, status, excluded: false })

const pr = (number, repo, o = {}) => ({
  number, url: `https://github.com/example-org/${repo}/pull/${number}`, state: 'OPEN', isDraft: false,
  status: 'Waiting', statusText: 'Waiting for review', tone: 'info', mergeable: 'MERGEABLE', mergeState: 'BLOCKED',
  reviewDecision: 'REVIEW_REQUIRED', checks: 'SUCCESS', headSha: sha('a1b2c3d4'), title: '', ...o,
})

const leg = (repo, key, o = {}) => ({
  repo, branch: `wip/${key}`, remoteBranch: `feat/${key}`, state: 'working', stateText: 'Uncommitted changes', isCurrent: false,
  ahead: 0, behind: 0, sha: sha('9f8e7d6c'), subject: '', files: [], localAhead: false, ready: false, needsPush: false, ...o,
})

const passedCheck = (repos, minAgo, stale = false) => ({
  status: 'passed', stale, text: `Build & test passed in ${repos.length} repos`, at: ago(minAgo), seconds: 94, staleRepos: stale ? [repos[0]] : [],
  repos: repos.map(r => ({ repo: r, status: 'passed', summary: 'build ok · 212 tests passed', phases: [] })),
})

export const tickets = [
  {
    key: 'PROJ-1421', ticket: 'PROJ-1421', description: 'Add retry with backoff to the nightly export job', type: 'feat', stage: 'working',
    statusText: 'Working in api, web · checked out', commitMessage: 'feat(PROJ-1421): Add retry with backoff to the nightly export job',
    legs: [
      leg('api', 'PROJ-1421', { isCurrent: true, files: [f('src/Export/ExportJob.cs'), f('src/Export/RetryPolicy.cs', 'A'), f('tests/Export/ExportJobTests.cs')] }),
      leg('web', 'PROJ-1421', { isCurrent: true, files: [f('src/pages/admin/Exports.tsx'), f('src/api/exports.ts')] }),
    ],
    flags: [], primaryAction: 'commit', jiraUrl: 'https://yourorg.atlassian.net/browse/PROJ-1421',
    actions: { commit: on('Commit locally'), resume: off('Resume', 'Already checked out'), push: on('Move to PR'), complete: off('Complete…', 'No PRs yet'), check: on('Build & test') },
    check: passedCheck(['api', 'web'], 18, true),
  },
  {
    key: 'PROJ-1418', ticket: 'PROJ-1418', description: 'Show order history on the account page', type: 'feat', stage: 'parked',
    statusText: 'Committed locally in web, shared', commitMessage: 'feat(PROJ-1418): Show order history on the account page',
    legs: [
      leg('web', 'PROJ-1418', { state: 'parked', stateText: 'Committed, not pushed', ahead: 1, files: [f('src/pages/account/Orders.tsx', 'A'), f('src/pages/account/index.tsx')] }),
      leg('shared', 'PROJ-1418', { state: 'parked', stateText: 'Committed, not pushed', ahead: 1, behind: 2, files: [f('src/models/OrderSummary.ts', 'A')] }),
    ],
    flags: [], primaryAction: 'push',
    actions: { commit: off('Commit locally', 'Nothing to commit'), resume: on('Resume'), push: on('Move to PR'), complete: off('Complete…', 'No PRs yet'), check: off('Build & test', 'Resume the ticket first') },
    check: passedCheck(['web', 'shared'], 140),
  },
  {
    key: 'PROJ-1409', ticket: 'PROJ-1409', description: 'Rate-limit password reset requests', type: 'fix', stage: 'review',
    statusText: 'PRs open in api, web', commitMessage: 'fix(PROJ-1409): Rate-limit password reset requests',
    legs: [
      leg('api', 'PROJ-1409', { remoteBranch: 'fix/PROJ-1409', state: 'review', stateText: 'PR #482 · changes requested', pr: pr(482, 'api', { status: 'ChangesRequested', statusText: 'Changes requested', tone: 'bad', reviewDecision: 'CHANGES_REQUESTED' }), files: [f('src/Auth/PasswordResetController.cs'), f('src/Auth/ResetRateLimiter.cs', 'A')] }),
      leg('web', 'PROJ-1409', { remoteBranch: 'fix/PROJ-1409', state: 'review', stateText: 'PR #911 · approved', pr: pr(911, 'web', { status: 'Approved', statusText: 'Approved', tone: 'ok', reviewDecision: 'APPROVED', mergeState: 'CLEAN' }), files: [f('src/pages/ResetPassword.tsx')] }),
    ],
    flags: ['changes requested on api'], primaryAction: undefined,
    actions: { commit: off('Commit locally', 'Nothing to commit'), resume: on('Re-open'), push: off('Update PRs', 'Up to date'), complete: off('Complete…', 'api #482: changes requested'), check: off('Build & test', 'Resume the ticket first') },
    check: passedCheck(['api', 'web'], 300),
  },
  {
    key: 'PROJ-1397', ticket: 'PROJ-1397', description: 'Switch request logging to structured JSON', type: 'feat', stage: 'review',
    statusText: 'PR open in api · checks running', commitMessage: 'feat(PROJ-1397): Switch request logging to structured JSON',
    legs: [
      leg('api', 'PROJ-1397', { state: 'review', stateText: 'PR #479 · checks running', pr: pr(479, 'api', { status: 'Checks', statusText: 'Checks running', tone: 'warn', checks: 'PENDING' }), files: [f('src/Logging/JsonRequestLogger.cs', 'A'), f('src/Program.cs')] }),
    ],
    flags: [], primaryAction: undefined,
    actions: { commit: off('Commit locally', 'Nothing to commit'), resume: on('Re-open'), push: off('Update PRs', 'Up to date'), complete: off('Complete…', 'Waiting for checks'), check: off('Build & test', 'Resume the ticket first') },
  },
  {
    key: 'PROJ-1384', ticket: 'PROJ-1384', description: 'Fix rounding of tax totals on invoices', type: 'fix', stage: 'ready',
    statusText: 'All PRs approved', commitMessage: 'fix(PROJ-1384): Fix rounding of tax totals on invoices',
    legs: [
      leg('api', 'PROJ-1384', { remoteBranch: 'fix/PROJ-1384', state: 'review', stateText: 'PR #475 · approved', ready: true, pr: pr(475, 'api', { status: 'Approved', statusText: 'Approved', tone: 'ok', reviewDecision: 'APPROVED', mergeState: 'CLEAN' }) }),
      leg('shared', 'PROJ-1384', { remoteBranch: 'fix/PROJ-1384', state: 'merged', stateText: 'Merged', ready: true, pr: pr(208, 'shared', { state: 'MERGED', status: 'Merged', statusText: 'Merged', tone: 'done' }) }),
    ],
    flags: [], primaryAction: 'complete',
    actions: { commit: off('Commit locally', 'Nothing to commit'), resume: on('Re-open'), push: off('Update PRs', 'Up to date'), complete: on('Complete…'), check: off('Build & test', 'Resume the ticket first') },
    check: passedCheck(['api', 'shared'], 1500),
  },
]

export const state = {
  version: 42, scannedAt: ago(0.2), configured: true,
  repos: [
    { name: 'api', path: 'C:\\code\\api', ok: true, defaultBranch: 'main', currentBranch: 'wip/PROJ-1421', dirty: 3, dirtyExcluded: 1, ahead: 0, behind: 0, workingKey: 'PROJ-1421', gitHubSlug: 'example-org/api', files: [{ path: 'config.local.json', status: 'M', excluded: true }] },
    { name: 'web', path: 'C:\\code\\web', ok: true, defaultBranch: 'main', currentBranch: 'wip/PROJ-1421', dirty: 2, dirtyExcluded: 0, ahead: 0, behind: 0, workingKey: 'PROJ-1421', gitHubSlug: 'example-org/web', files: [] },
    { name: 'shared', path: 'C:\\code\\shared', ok: true, defaultBranch: 'main', currentBranch: 'main', dirty: 1, dirtyExcluded: 0, ahead: 0, behind: 2, gitHubSlug: 'example-org/shared', files: [] },
  ],
  unassigned: [
    { repo: 'shared', branch: 'main', isDefault: true, files: [f('src/utils/dates.ts')], excludedFiles: [] },
  ],
  tickets,
  gitHub: { user: 'octo-dev', ok: true, lastPoll: ago(0.7) },
  jiraBaseUrl: 'https://yourorg.atlassian.net/browse/', archiveCount: 37,
}

const ticketReview = {
  id: 'r-t1', pr: 'ticket:PROJ-1421', focus: 'api', repos: [{ repo: 'api' }, { repo: 'web' }], headSha: '', trigger: 'manual', at: ago(6), seconds: 148,
  status: 'ok', verdict: 'minor-issues', risk: 'medium', costUsd: 0.41, turns: 23, model: 'claude-sonnet-5-5',
  summary: 'Wraps each export batch in a `RetryPolicy` (3 attempts, exponential backoff with jitter) and records the attempt count on the export run. ' +
    'The admin **Exports** page shows the attempt count and a "Retried" badge.',
  concerns: [
    { severity: 'medium', repo: 'api', file: 'src/Export/ExportJob.cs', line: 88, title: 'Retries re-send rows that were already written',
      detail: 'A batch that fails after `WriteRowsAsync` succeeds partway is retried from the start, so the target file can get duplicate rows. Track the last written offset, or write each batch to a temp file and move it on success.' },
    { severity: 'medium', repo: 'web', file: 'src/api/exports.ts', line: 41, title: 'Client still expects `attempts` to be a string',
      detail: 'The API now returns `attempts` as a number, but `ExportRun.attempts` is typed `string` and rendered with `.padStart`. The page throws for any run with retries.' },
    { severity: 'low', repo: 'api', file: 'src/Export/RetryPolicy.cs', line: 17, title: 'Cancellation is swallowed during backoff',
      detail: '`Task.Delay(delay)` ignores the job\'s `CancellationToken`, so stopping the host waits for the full backoff.' },
  ],
  previousRequests: [],
}

const prReviews = [
  {
    id: 'r-p2', pr: 'web#918', repos: [{ repo: 'web' }], headSha: sha('7c41e2aa'), sinceSha: sha('3be09f12'), trigger: 'commits', at: ago(12), seconds: 121,
    status: 'ok', verdict: 'minor-issues', risk: 'low', costUsd: 0.33, turns: 17, model: 'claude-sonnet-5-5',
    summary: 'Adds a debounced search box to the customer list. Typing filters server-side through `GET /customers?q=`, and the query is kept in the URL so the view can be shared.',
    previousRequests: [
      { request: 'Debounce the search input instead of firing a request on every keystroke', status: 'addressed', note: 'useDebouncedValue(query, 300) now feeds the request.' },
      { request: 'Cancel in-flight requests when the query changes', status: 'partially', note: 'An AbortController is created, but the old request is not aborted when the component unmounts.' },
      { request: 'Add a test for the empty-results state', status: 'not-addressed', note: 'No new test covers the empty state.' },
    ],
    concerns: [
      { severity: 'medium', file: 'src/pages/customers/CustomerList.tsx', line: 64, title: 'Stale results can overwrite newer ones',
        detail: 'Responses are applied in arrival order. If the request for `"jo"` returns after `"john"`, the list shows the older result. Compare against the latest query before calling `setRows`.' },
      { severity: 'low', file: 'src/pages/customers/CustomerList.tsx', line: 22, title: 'Search term is not trimmed',
        detail: 'A trailing space produces a separate request and URL. Trim before debouncing.' },
    ],
  },
  {
    id: 'r-p1', pr: 'web#918', repos: [{ repo: 'web' }], headSha: sha('3be09f12'), trigger: 'new', at: ago(60 * 26), seconds: 133,
    status: 'ok', verdict: 'needs-changes', risk: 'medium', summary: 'First version of the customer search.', concerns: [], previousRequests: [],
  },
]

export const reviews = {
  anyEnabled: true, claudePath: 'C:\\Users\\dev\\.local\\bin\\claude.exe', queue: [],
  repos: [
    {
      name: 'web', slug: 'example-org/web', lastPoll: ago(1), polling: false,
      prs: [
        {
          key: 'web#918', repo: 'web', number: 918, url: 'https://github.com/example-org/web/pull/918', filesUrl: 'https://github.com/example-org/web/pull/918/files',
          title: 'Customer list: server-side search', author: 'sam-lee', authorIsBot: false, isDraft: false, headRef: 'feat/PROJ-1415', baseRef: 'main',
          headSha: sha('7c41e2aa'), additions: 186, deletions: 41, changedFiles: 7, updatedAt: ago(15),
          myReviewState: 'CHANGES_REQUESTED', myReviewAt: ago(60 * 25), newCommitsSinceMyReview: true, openThreads: 2,
          status: 'reviewed', statusText: 'Reviewed', reviews: prReviews,
        },
        {
          key: 'web#921', repo: 'web', number: 921, url: 'https://github.com/example-org/web/pull/921', filesUrl: 'https://github.com/example-org/web/pull/921/files',
          title: 'Upgrade date-fns to v4', author: 'alex-kim', authorIsBot: false, isDraft: false, headRef: 'chore/PROJ-1420', baseRef: 'main',
          headSha: sha('d00d1e55'), additions: 64, deletions: 58, changedFiles: 12, updatedAt: ago(40), newCommitsSinceMyReview: false, openThreads: 0,
          status: 'reviewed', statusText: 'Reviewed',
          reviews: [{ id: 'r-q1', pr: 'web#921', repos: [{ repo: 'web' }], headSha: sha('d00d1e55'), trigger: 'new', at: ago(35), seconds: 88, status: 'ok', verdict: 'looks-good', risk: 'low', summary: 'Mechanical upgrade.', concerns: [], previousRequests: [] }],
        },
      ],
    },
    {
      name: 'api', slug: 'example-org/api', lastPoll: ago(1), polling: false,
      prs: [
        {
          key: 'api#486', repo: 'api', number: 486, url: 'https://github.com/example-org/api/pull/486', filesUrl: 'https://github.com/example-org/api/pull/486/files',
          title: 'Add idempotency keys to payment webhooks', author: 'priya-n', authorIsBot: false, isDraft: false, headRef: 'feat/PROJ-1412', baseRef: 'main',
          headSha: sha('beefcafe'), additions: 312, deletions: 27, changedFiles: 9, updatedAt: ago(3), newCommitsSinceMyReview: false, openThreads: 0,
          status: 'reviewing', statusText: 'Reviewing', reviews: [],
        },
      ],
    },
  ],
  tickets: [{ key: 'PROJ-1421', status: 'reviewed', stale: false, staleRepos: [], reviews: [ticketReview] }],
}

export const pushPreview = {
  kind: 'push', title: 'Move PROJ-1418 to PR', summary: 'feat(PROJ-1418): Show order history on the account page',
  buttonLabel: 'Push & create PRs', destructive: false, warnings: ['shared: origin/main moved 2 commits since this branch was made. It will be rebased onto it.'], blockers: [],
  data: { description: 'Show order history on the account page', type: 'feat' },
  steps: [
    { repo: 'web', text: 'Squash 1 commit and rebase onto origin/main (off to the side, your working copy is untouched)', commands: ['git fetch origin', 'git commit-tree … -p origin/main'] },
    { repo: 'web', text: 'Push feat/PROJ-1418 with --force-with-lease', commands: ['git push --force-with-lease origin <sha>:refs/heads/feat/PROJ-1418'] },
    { repo: 'web', text: 'Create PR “feat(PROJ-1418): Show order history on the account page” → main', commands: ['POST /repos/example-org/web/pulls'] },
    { repo: 'shared', text: 'Squash 1 commit and rebase onto origin/main (2 new commits upstream)', commands: ['git fetch origin', 'git commit-tree … -p origin/main'] },
    { repo: 'shared', text: 'Push feat/PROJ-1418 with --force-with-lease', commands: ['git push --force-with-lease origin <sha>:refs/heads/feat/PROJ-1418'] },
    { repo: 'shared', text: 'Create PR “feat(PROJ-1418): Show order history on the account page” → main', commands: ['POST /repos/example-org/shared/pulls'] },
  ],
}
