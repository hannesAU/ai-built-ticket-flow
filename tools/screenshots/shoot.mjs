// Renders the real UI (Vite dev server) against mock API data and saves README screenshots.
// usage: npm run dev in web/, then here: npm install && node shoot.mjs [outDir] [baseUrl]
import { chromium } from 'playwright-core'
import { mkdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { state, reviews, pushPreview } from './fixtures.mjs'

const out = process.argv[2] ?? join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'docs', 'screenshots')
const base = process.argv[3] ?? 'http://localhost:5173'
mkdirSync(out, { recursive: true })

const browser = await chromium.launch(process.env.CHROME_PATH ? { executablePath: process.env.CHROME_PATH } : { channel: 'chrome' }) // installed Chrome, no download
const ctx = await browser.newContext({ viewport: { width: 1360, height: 860 }, deviceScaleFactor: 1.5, colorScheme: 'light' })

// no server: fake the SSE stream so the reviews state arrives like it does live
await ctx.addInitScript((rv) => {
  try { localStorage.clear(); sessionStorage.clear() } catch {}
  class FakeES {
    constructor() { this.l = {}; setTimeout(() => { this.emit('open', {}); this.emit('reviews', { data: JSON.stringify(rv) }) }, 30) }
    addEventListener(t, f) { (this.l[t] ??= []).push(f) }
    emit(t, e) { (this.l[t] ?? []).forEach(f => f(e)) }
    close() {}
  }
  window.EventSource = FakeES
}, reviews)

const json = (body) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
await ctx.route('**/api/**', (route) => {
  const url = new URL(route.request().url())
  const p = url.pathname
  if (p === '/api/state' || p === '/api/refresh') return route.fulfill(json(state))
  if (p === '/api/reviews') return route.fulfill(json(reviews))
  if (p === '/api/ops/push/preview') return route.fulfill(json(pushPreview))
  if (p === '/api/journal') return route.fulfill(json([]))
  if (p === '/api/visible') return route.fulfill({ status: 204 })
  return route.fulfill(json({}))
})

const page = await ctx.newPage()
const settle = () => page.waitForTimeout(400)

// 1. board
await page.goto(base + '/')
await page.waitForSelector('.ticket-row')
await settle()
await page.screenshot({ path: join(out, 'board.png') })

// 2. "Move to PR" preview
await page.locator('.ticket-row[data-key="PROJ-1418"] .t-actions .btn.primary').click()
await page.waitForSelector('.plan')
await settle()
await page.locator('.modal').screenshot({ path: join(out, 'move-to-pr.png') })
await page.keyboard.press('Escape')

// 3. own-ticket review in the drawer
await page.setViewportSize({ width: 1360, height: 1020 })
await page.locator('.ticket-row[data-key="PROJ-1421"]').click()
await page.getByRole('tab', { name: /Review/ }).click()
await page.waitForSelector('.concern')
await settle()
await page.screenshot({ path: join(out, 'ticket-review.png') })

// 4. PR reviews page with a re-review expanded
await page.setViewportSize({ width: 1360, height: 860 })
await page.locator('.nav-btn', { hasText: 'PR reviews' }).click()
await page.locator('.review-pr-row', { hasText: '#918' }).click()
await page.waitForSelector('.request-list')
await settle()
await page.screenshot({ path: join(out, 'pr-review.png'), fullPage: true })

await browser.close()
console.log('saved to', out)
