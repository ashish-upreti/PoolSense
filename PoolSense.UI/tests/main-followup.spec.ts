import { test, expect } from '@playwright/test'

test('saved main chat continues by session id and changing application scope starts a clean chat', async ({ page }) => {
  const requests: Record<string, unknown>[] = []
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  const now = '2026-11-10T10:00:00Z'
  const projects = ['A', 'B'].map((projectId, index) => ({
    id: index + 1, projectId, projectName: `Application ${projectId}`, knowledgeLookbackYears: 2,
    similaritySearchLimit: 5, sendEmail: false, poolingEnabled: false, emailRecipients: '', applicationFilter: projectId,
    nyraKbNames: '', githubUrls: '', createdAt: now,
  }))
  const summary = { id: 'saved', title: 'Saved mapping investigation', selectedGroupIds: ['A'], isPinned: true, createdAt: now, updatedAt: now, messageCount: 2 }
  const result = {
    suggestedRootCause: 'Mapping needs validation.',
    suggestedResolution: '1. Inspect.\n2. Compare.\n3. Validate VG-123.',
    detailedAnalysisMarkdown: '## Validation\n\nStep 3 checks the VG-123 mapping against current evidence.',
    reasoning: 'Use fresh evidence for each question.', confidence: .7, similarIncidents: [], nyraDocuments: [], codeDocuments: [],
    failurePattern: { id: 0, system: '', component: '', failureType: '', resolutionCategory: '', ticketId: '', createdAt: now }, failurePatternFrequency: 0,
  }
  let release: (() => void) | undefined
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    let body: unknown = []
    if (path === '/api/auth/session') body = { success: true, user: { username: 'test', displayName: 'Test operator', isAdmin: true } }
    else if (path === '/api/projects') body = projects
    else if (path === '/api/projects/groups') body = { groups: projects.map(project => ({ groupId: project.projectId, displayName: project.projectName })) }
    else if (path === '/api/settings/deployment') body = { environmentLabel: 'Follow-up fixture', environmentName: 'Test' }
    else if (path === '/api/settings/ticket-automation') body = { pollingEnabled: false, pollIntervalSeconds: 60, email: { deliveryMode: 'Smtp' } }
    else if (path === '/api/feedback/application/insights') { await route.fulfill({ status: 503, json: { message: 'Not available in fixture' } }); return }
    else if (path === '/api/ticket/history') body = [summary]
    else if (path === '/api/ticket/history/saved') body = { session: summary, messages: [
      { role: 'user', content: 'Investigate VG-123', createdAt: now },
      { role: 'assistant', content: result.suggestedResolution, result, createdAt: now },
    ] }
    else if (path === '/api/ticket/process-progress') {
      const request = route.request().postDataJSON() as Record<string, unknown>
      requests.push(request)
      if (requests.length === 1) await new Promise<void>(resolve => { release = resolve })
      await route.fulfill({
        contentType: 'application/x-ndjson',
        body: JSON.stringify({ type: 'result', result: { ...result, suggestedResolution: 'Follow-up investigated against fresh evidence.', chatSessionId: request['chatSessionId'] || 'new-session' } }) + '\n',
      })
      return
    }
    await route.fulfill({ json: body })
  })
  await page.goto('http://localhost:4200')
  await page.getByRole('button', { name: 'Saved mapping investigation', exact: false }).click()
  await expect(page.locator('.detailed-analysis-content')).toContainText('VG-123')
  const scope = page.locator('select[name="searchScopeMode"]')
  await expect(scope).toHaveValue('A')
  await page.locator('#incident-input').fill('Explain step 3')
  await page.locator('.composer-poolsense-button').click()
  await expect.poll(() => requests.length).toBe(1)
  await expect(scope).toBeDisabled()
  expect(requests[0]).toEqual({ title: 'Explain step 3', description: 'Explain step 3', selectedGroupIds: ['A'], chatSessionId: 'saved' })
  release!()
  await expect(scope).toBeEnabled()
  await expect(page.locator('.workspace-chat .message-bubble-assistant')).toHaveCount(2)
  await expect(page.locator('.workspace-chat app-code-evidence')).toHaveCount(0)
  await scope.selectOption('A')
  await expect(page.locator('.workspace-chat .message-bubble-assistant')).toHaveCount(2)
  await scope.selectOption('B')
  await expect(page.getByRole('status').filter({ hasText: 'Application scope changed. Started a new chat' })).toBeVisible()
  await expect(page.locator('.workspace-chat .message-bubble')).toHaveCount(0)
  await expect(page.locator('#incident-input')).toHaveValue('')
  await expect(page.locator('.chat-history-open[aria-current="true"]')).toHaveCount(0)
  await page.locator('#incident-input').fill('Other application question')
  await page.locator('.composer-poolsense-button').click()
  await expect.poll(() => requests.length).toBe(2)
  expect(requests[1]).toEqual({ title: 'Other application question', description: 'Other application question', selectedGroupIds: ['B'] })
  await expect(page.locator('.workspace-chat .message-bubble-assistant')).toHaveCount(1)
  expect(errors).toEqual([])
})
