import { test, expect, type Page } from '@playwright/test'

declare global {
  interface Window {
    historyFixture: {
      requests: { title: string; selectedGroupIds: string[]; chatSessionId?: string; aborted: boolean }[]
      ignoreAbort?: boolean
      finish: (query: string, result?: object, error?: string) => void
      progress: (query: string, detail: string) => void
    }
  }
}

const now = '2026-11-10T10:00:00Z'
const result = (answer: string, chatSessionId: string) => ({
  suggestedRootCause: answer, suggestedResolution: answer, detailedAnalysisMarkdown: `## Evidence\n\n${answer}`,
  reasoning: '', confidence: .7, similarIncidents: [], nyraDocuments: [], codeDocuments: [],
  failurePatternFrequency: 0, chatSessionId,
})
const summary = (id: string) => ({
  id, title: `Conversation ${id}`, selectedGroupIds: [id], isPinned: false,
  createdAt: now, updatedAt: now, messageCount: 2,
})

async function setup(page: Page, delayedReads: boolean | 'both' = false) {
  const summaries = ['A', 'B'].map(summary)
  let releaseReadA: (() => void) | undefined
  let releaseReadB: (() => void) | undefined
  let secondAccount = false
  await page.addInitScript(() => {
    const originalFetch = window.fetch.bind(window)
    const streams = new Map<string, ReadableStreamDefaultController<Uint8Array>>()
    const encoder = new TextEncoder()
    const event = (query: string, value: object) => streams.get(query)!.enqueue(encoder.encode(JSON.stringify(value) + '\n'))
    window.historyFixture = {
      requests: [],
      finish(query, result, error) {
        event(query, error ? { type: 'error', error: { message: error } } : { type: 'result', result })
        streams.get(query)!.close()
      },
      progress(query, detail) {
        event(query, { type: 'progress', progress: {
          stage: 'retrieval', title: 'Retrieving evidence', detail, state: 'running', order: 2, timestampUtc: new Date().toISOString(),
        } })
      },
    }
    window.fetch = async (input, init) => {
      if (!String(input).includes('/ticket/process-progress')) return originalFetch(input, init)
      const request = { ...JSON.parse(String(init?.body)), aborted: false }
      window.historyFixture.requests.push(request)
      const stream = new ReadableStream<Uint8Array>({
        start(controller) {
          streams.set(request.title, controller)
          init?.signal?.addEventListener('abort', () => {
            request.aborted = true
            if (!window.historyFixture.ignoreAbort) controller.error(new DOMException('Aborted', 'AbortError'))
          })
        },
      })
      return new Response(stream, { headers: { 'Content-Type': 'application/x-ndjson' } })
    }
  })
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    let body: unknown = []
    if (path === '/api/auth/session') body = { success: true, user: { username: 'test', displayName: 'Test operator', isAdmin: true } }
    else if (path === '/api/auth/login') {
      secondAccount = true
      body = { success: true, user: { username: 'new-owner', displayName: 'New operator', isAdmin: true } }
    }
    else if (path === '/api/auth/logout') body = { success: true }
    else if (path === '/api/projects/groups') body = { groups: ['A', 'B'].map(id => ({ groupId: id, displayName: `Application ${id}` })) }
    else if (path === '/api/settings/deployment') body = { environmentLabel: 'Navigation fixture', environmentName: 'Test' }
    else if (path === '/api/settings/ticket-automation') body = { pollingEnabled: false, email: {} }
    else if (path === '/api/ticket/history') body = secondAccount ? [summary('C')] : summaries
    else if (path.startsWith('/api/ticket/history/')) {
      const id = path.split('/').pop()!
      if (id === 'A' && delayedReads) await new Promise<void>(resolve => { releaseReadA = resolve })
      if (id === 'B' && delayedReads === 'both') await new Promise<void>(resolve => { releaseReadB = resolve })
      body = { session: summary(id), messages: [
        { role: 'user', content: `Question ${id}`, createdAt: now },
        { role: 'assistant', content: `Answer ${id}`, result: result(`Answer ${id}`, id), createdAt: now },
      ] }
    }
    await route.fulfill({ json: body }).catch(error => {
      if (!route.request().failure()) throw error
    })
  })
  await page.goto('http://localhost:4200')
  await expect(page.getByRole('button', { name: 'Conversation B', exact: true })).toBeVisible()
  return {
    releaseReadA: () => releaseReadA!(),
    releaseReadB: () => releaseReadB!(),
    finish: async (query: string, id: string, answer = `Completed ${query}`) => {
      if (!summaries.some(item => item.id === id)) summaries.push(summary(id))
      await page.evaluate(({ query, value }) => window.historyFixture.finish(query, value), { query, value: result(answer, id) })
    },
  }
}

async function send(page: Page, query: string) {
  await page.locator('#incident-input').fill(query)
  await page.locator('.composer-poolsense-button').click()
  await expect.poll(() => page.evaluate(q => window.historyFixture.requests.some(request => request.title === q), query)).toBe(true)
}

test('switches during streaming, restores pending progress, preserves B draft and safely finishes A', async ({ page }) => {
  const fixture = await setup(page)
  await page.getByRole('button', { name: 'Conversation A', exact: true }).click()
  await expect(page.locator('.message-bubble-assistant')).toContainText('Answer A')
  await send(page, 'Continue A')
  await page.getByRole('button', { name: 'Conversation B', exact: true }).click()
  await expect(page.locator('.message-bubble-assistant')).toContainText('Answer B')
  await expect(page.locator('select[name="searchScopeMode"]')).toHaveValue('B')
  await page.locator('#incident-input').fill('Draft for B')
  await page.evaluate(() => window.historyFixture.progress('Continue A', 'A-only progress'))
  await expect(page.locator('.workspace-chat')).not.toContainText('A-only progress')
  await page.getByRole('button', { name: 'Conversation A', exact: true }).click()
  await expect(page.locator('.workspace-chat')).toContainText('A-only progress')
  await expect(page.locator('.chat-history-item-active').getByRole('button', { name: 'Delete conversation' })).toBeDisabled()
  await page.getByRole('button', { name: 'Conversation B', exact: true }).click()
  await fixture.finish('Continue A', 'A')
  await expect(page.locator('#incident-input')).toHaveValue('Draft for B')
  await expect(page.locator('.message-bubble-assistant')).toHaveCount(1)
  await expect(page.locator('.workspace-chat')).not.toContainText('Completed Continue A')
  expect(await page.evaluate(() => window.historyFixture.requests[0].aborted)).toBe(false)
  await page.getByRole('button', { name: 'Conversation A', exact: true }).click()
  await expect(page.locator('.message-bubble-assistant').last()).toContainText('Completed Continue A')
})

test('new pending chats stay selectable, accept concurrent saved-chat requests and adopt late server ids', async ({ page }) => {
  const fixture = await setup(page)
  await send(page, 'New origin')
  await page.getByRole('button', { name: '+ New chat', exact: true }).click()
  await expect(page.locator('.message-bubble')).toHaveCount(0)
  await page.getByRole('button', { name: 'New origin', exact: true }).click()
  await expect(page.locator('#incident-input')).toBeDisabled()
  await page.getByRole('button', { name: 'Conversation B', exact: true }).click()
  await expect(page.locator('.message-bubble-assistant')).toContainText('Answer B')
  await send(page, 'Continue B')
  await fixture.finish('New origin', 'new-server-id')
  await expect(page.locator('#incident-input')).toBeDisabled()
  await expect(page.locator('.workspace-chat')).not.toContainText('Completed New origin')
  await fixture.finish('Continue B', 'B')
  await expect(page.locator('#incident-input')).toBeEnabled()
  await page.getByRole('button', { name: 'Conversation new-server-id', exact: true }).click()
  await expect(page.locator('.message-bubble-assistant')).toContainText('Completed New origin')
  await send(page, 'Follow new origin')
  expect(await page.evaluate(() => window.historyFixture.requests.at(-1)?.chatSessionId)).toBe('new-server-id')
  await fixture.finish('Follow new origin', 'new-server-id')
})

test('obsolete history reads cannot replace selected B or clear its loading state', async ({ page }) => {
  const fixture = await setup(page, true)
  await page.getByRole('button', { name: 'Conversation A', exact: true }).click()
  await expect(page.getByRole('status').filter({ hasText: 'Opening conversation' })).toBeVisible()
  await page.getByRole('button', { name: 'Conversation B', exact: true }).click()
  await expect(page.locator('.message-bubble-assistant')).toContainText('Answer B')
  fixture.releaseReadA()
  await expect(page.locator('.chat-history-open[aria-current="true"]')).toHaveAttribute('title', 'Conversation B')
  await expect(page.locator('select[name="searchScopeMode"]')).toHaveValue('B')
  await expect(page.locator('#incident-input')).toBeEnabled()
  await expect(page.locator('.message-bubble-assistant')).not.toContainText('Answer A')
})

test('errors belong to the origin and logout aborts pending streams without restoring old history', async ({ page }) => {
  await setup(page)
  await send(page, 'Failing origin')
  await page.getByRole('button', { name: 'Conversation B', exact: true }).click()
  await expect(page.locator('.message-bubble-assistant')).toContainText('Answer B')
  await page.evaluate(() => window.historyFixture.finish('Failing origin', undefined, 'Origin-only failure'))
  await expect(page.locator('.workspace-chat .error-banner')).toHaveCount(0)
  await page.getByRole('button', { name: 'Failing origin', exact: true }).click()
  await expect(page.locator('.workspace-chat .error-banner')).toHaveText('Origin-only failure')
  await send(page, 'Abort on logout')
  await page.getByRole('button', { name: 'Expand navigation sidebar' }).click()
  await page.getByRole('button', { name: /sign out/i }).click()
  await expect.poll(() => page.evaluate(() => window.historyFixture.requests.at(-1)?.aborted)).toBe(true)
  await expect(page.locator('.chat-history-open')).toHaveCount(0)
})

test('three concurrent chats are bounded without disabling navigation or losing drafts', async ({ page }) => {
  const fixture = await setup(page)
  for (const query of ['First', 'Second', 'Third']) {
    await send(page, query)
    await page.getByRole('button', { name: '+ New chat', exact: true }).click()
  }
  await page.locator('#incident-input').fill('Fourth')
  await page.locator('.composer-poolsense-button').click()
  await expect(page.locator('.workspace-chat .error-banner')).toContainText('Three chats are already investigating')
  expect(await page.evaluate(() => window.historyFixture.requests.length)).toBe(3)
  await expect(page.locator('#incident-input')).toHaveValue('Fourth')
  await page.getByRole('button', { name: 'First', exact: true }).click()
  await fixture.finish('First', 'first-id')
  await expect(page.locator('.message-bubble-assistant')).toContainText('Completed First')
})

test('obsolete A finally cannot clear the current B hydration state', async ({ page }) => {
  const fixture = await setup(page, 'both')
  await page.getByRole('button', { name: 'Conversation A', exact: true }).click()
  await expect(page.getByRole('status').filter({ hasText: 'Opening conversation' })).toBeVisible()
  await page.getByRole('button', { name: 'Conversation B', exact: true }).click()
  fixture.releaseReadA()
  await expect(page.locator('.chat-history-open[aria-current="true"]')).toHaveAttribute('title', 'Conversation B')
  await expect(page.getByRole('status').filter({ hasText: 'Opening conversation' })).toBeVisible()
  await expect(page.locator('#incident-input')).toBeDisabled()
  fixture.releaseReadB()
  await expect(page.locator('.message-bubble-assistant')).toContainText('Answer B')
  await expect(page.locator('#incident-input')).toBeEnabled()
})

test('even an abort-ignoring old response cannot leak into a newly signed-in account', async ({ page }) => {
  const fixture = await setup(page)
  await page.evaluate(() => { window.historyFixture.ignoreAbort = true })
  await send(page, 'Old private investigation')
  await page.getByRole('button', { name: 'Expand navigation sidebar' }).click()
  await page.getByRole('button', { name: 'Sign Out', exact: true }).click()
  await page.getByLabel('Username', { exact: true }).fill('new-owner')
  await page.getByLabel('Password', { exact: true }).fill('fixture-password')
  await page.getByRole('button', { name: 'Sign In', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Conversation C', exact: true })).toBeVisible()
  await fixture.finish('Old private investigation', 'old-server-id', 'Old private answer')
  await expect(page.locator('.workspace-chat')).not.toContainText('Old private answer')
  await expect(page.locator('.chat-history-open')).toHaveCount(1)
  await expect(page.locator('#incident-input')).toHaveValue('')
})
