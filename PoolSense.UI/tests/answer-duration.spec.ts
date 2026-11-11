import { test, expect, type Page, type Route } from '@playwright/test'

const now = '2026-11-10T10:00:00Z'

function baseResult(overrides: Record<string, unknown> = {}) {
  return {
    suggestedRootCause: 'Mapping needs validation.',
    suggestedResolution: '1. Inspect.\n2. Compare.\n3. Validate VG-123.',
    detailedAnalysisMarkdown: '## Validation\n\nStep 3 checks the VG-123 mapping against current evidence.',
    reasoning: 'Use fresh evidence for each question.', confidence: .7, similarIncidents: [], nyraDocuments: [], codeDocuments: [],
    failurePattern: { id: 0, system: '', component: '', failureType: '', resolutionCategory: '', ticketId: '', createdAt: now },
    failurePatternFrequency: 0,
    ...overrides,
  }
}

type SavedSession = { id: string, title: string, processingDurationMs?: number }

async function mockApp(
  page: Page,
  sessions: SavedSession[],
  stream?: (request: Record<string, unknown>, route: Route) => Promise<void>,
) {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  const projects = ['A', 'B'].map((projectId, index) => ({
    id: index + 1, projectId, projectName: `Application ${projectId}`, knowledgeLookbackYears: 2,
    similaritySearchLimit: 5, sendEmail: false, poolingEnabled: false, emailRecipients: '', applicationFilter: projectId,
    nyraKbNames: '', githubUrls: '', createdAt: now,
  }))
  const summaries = sessions.map(session => ({
    id: session.id, title: session.title, selectedGroupIds: ['A'], isPinned: false, createdAt: now, updatedAt: now, messageCount: 2,
  }))
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    let body: unknown = []
    if (path === '/api/auth/session') body = { success: true, user: { username: 'test', displayName: 'Test operator', isAdmin: true } }
    else if (path === '/api/projects') body = projects
    else if (path === '/api/projects/groups') body = { groups: projects.map(project => ({ groupId: project.projectId, displayName: project.projectName })) }
    else if (path === '/api/settings/deployment') body = { environmentLabel: 'Duration fixture', environmentName: 'Test' }
    else if (path === '/api/settings/ticket-automation') body = { pollingEnabled: false, pollIntervalSeconds: 60, email: { deliveryMode: 'Smtp' } }
    else if (path === '/api/feedback/application/insights') { await route.fulfill({ status: 503, json: { message: 'Not available in fixture' } }); return }
    else if (path === '/api/ticket/history') body = summaries
    else if (path === '/api/ticket/process-progress' && stream) {
      await stream(route.request().postDataJSON() as Record<string, unknown>, route)
      return
    } else {
      const session = sessions.find(candidate => path === `/api/ticket/history/${candidate.id}`)
      if (session) {
        body = {
          session: summaries.find(summary => summary.id === session.id),
          messages: [
            { role: 'user', content: 'Investigate VG-123', createdAt: now },
            { role: 'assistant', content: 'Saved resolution text.', result: baseResult(
              session.processingDurationMs === undefined ? {} : { processingDurationMs: session.processingDurationMs }), createdAt: now },
          ],
        }
      }
    }
    await route.fulfill({ json: body })
  })
  return errors
}

test('restored history shows the persisted server generation timing in every format', async ({ page }) => {
  const errors = await mockApp(page, [
    { id: 'seconds', title: 'Seconds answer', processingDurationMs: 18400 },
    { id: 'minutes', title: 'Minutes answer', processingDurationMs: 72000 },
    { id: 'subsecond', title: 'Subsecond answer', processingDurationMs: 40 },
    { id: 'invalid', title: 'Invalid duration answer', processingDurationMs: -5 },
  ])
  await page.goto('http://localhost:4200')

  await page.getByRole('button', { name: 'Seconds answer' }).click()
  const durations = page.locator('.workspace-chat .message-bubble-assistant .answer-duration-text')
  await expect(durations).toHaveCount(1)
  await expect(durations).toHaveText('Generated in 18.4 s')
  await expect(page.locator('.workspace-chat .message-bubble-assistant .answer-duration')).toHaveAttribute('title', 'Server-measured generation time recorded when this answer was saved.')

  await page.getByRole('button', { name: 'Minutes answer' }).click()
  await expect(page.locator('.workspace-chat .message-bubble-assistant .answer-duration-text')).toHaveText('Generated in 1 min 12 s')

  await page.getByRole('button', { name: 'Subsecond answer' }).click()
  await expect(page.locator('.workspace-chat .message-bubble-assistant .answer-duration-text')).toHaveText('Generated in <0.1 s')

  await page.getByRole('button', { name: 'Invalid duration answer' }).click()
  await expect(page.locator('.workspace-chat .message-bubble-assistant .answer-duration')).toHaveCount(0)

  expect(errors).toEqual([])
})

test('old answers without a persisted duration stay unlabeled', async ({ page }) => {
  const errors = await mockApp(page, [{ id: 'legacy', title: 'Legacy answer' }])
  await page.goto('http://localhost:4200')
  await page.getByRole('button', { name: 'Legacy answer' }).click()
  await expect(page.locator('.workspace-chat .message-bubble-assistant .message-label')).toHaveText('PoolSense')
  await expect(page.locator('.workspace-chat .message-bubble-assistant .answer-duration')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('live answers show client-observed completion time that survives history switches', async ({ page }) => {
  let release: (() => void) | undefined
  const errors = await mockApp(page, [{ id: 'other', title: 'Other conversation' }], async (_request, route) => {
    await new Promise<void>(resolve => { release = resolve })
    await route.fulfill({
      contentType: 'application/x-ndjson',
      body: JSON.stringify({ type: 'result', result: baseResult({ suggestedResolution: 'Fresh live resolution.' }) }) + '\n',
    })
  })
  await page.goto('http://localhost:4200')

  await page.locator('#incident-input').fill('Investigate the live duration')
  await page.locator('.composer-poolsense-button').click()
  await expect(page.locator('.workspace-chat .message-bubble-assistant')).toHaveCount(0)

  await page.getByRole('button', { name: 'Other conversation' }).click()
  await expect(page.locator('.workspace-chat .message-bubble-assistant .answer-duration')).toHaveCount(0)
  await expect(page.locator('.workspace-chat .message-bubble-assistant')).toHaveCount(1)

  await page.locator('.chat-history-new-button').click()
  await expect(page.locator('.workspace-chat .message-bubble')).toHaveCount(0)

  // Hold the answer back while the user is on other workspaces, so the captured
  // duration must include time spent away from the origin chat.
  await page.waitForTimeout(1000)
  const releaseStarted = Date.now()
  release!()
  await page.getByRole('button', { name: 'Investigate the live duration', exact: false }).first().click()
  const durations = page.locator('.workspace-chat .message-bubble-assistant .answer-duration-text')
  await expect(durations).toHaveCount(1)
  const label = await durations.textContent()
  expect(label).toMatch(/^Completed in \d+\.\d s$/)
  const seconds = Number.parseFloat(label!.replace(/[^\d.]/g, ''))
  expect(seconds).toBeGreaterThanOrEqual(1.0)
  expect(seconds).toBeLessThan(60)
  await expect(page.locator('.workspace-chat .message-bubble-assistant .answer-duration')).toHaveAttribute('title', 'Elapsed time from sending your question to receiving this completed response.')
  expect(Date.now() - releaseStarted).toBeLessThan(10_000)
  expect(errors).toEqual([])
})
