import { test, expect } from '@playwright/test'

const appUrl = process.env.POOLSENSE_TEST_URL ?? 'http://localhost:4200'

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
  await page.goto(appUrl)
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

for (const saveFails of [false, true]) {
  test(`pool report troubleshooting ${saveFails ? 'shows a history save warning without losing the answer' : 'creates a resumable main conversation and groups follow-ups'}`, async ({ page }) => {
    const now = '2026-11-20T10:00:00Z'
    const result = {
      suggestedRootCause: 'Mapping needs validation.', suggestedResolution: 'Original recommendation',
      detailedAnalysisMarkdown: 'Original saved analysis', confidence: .7, reasoning: 'Check the mapping.',
      similarIncidents: [], nyraDocuments: [], codeDocuments: [],
      failurePattern: { id: 0, system: '', component: '', failureType: '', resolutionCategory: '', ticketId: '', createdAt: now },
      failurePatternFrequency: 0,
    }
    const summaries: { id: string; title: string; selectedGroupIds: string[]; isPinned: boolean; createdAt: string; updatedAt: string; messageCount: number }[] = []
    const messages = new Map<string, { role: string; content: string; result?: typeof result; createdAt: string }[]>()
    const troubleshootRequests: { question: string; chatSessionId?: string }[] = []
    const mainRequests: { chatSessionId?: string; selectedGroupIds?: string[] }[] = []
    await page.route('**/api/**', async route => {
      const path = new URL(route.request().url()).pathname
      let body: unknown = []
      if (path === '/api/auth/session') body = { success: true, user: { username: 'test', displayName: 'Test operator', isAdmin: true } }
      else if (path === '/api/projects/groups') body = { groups: [{ groupId: 'A', displayName: 'Application A' }] }
      else if (path === '/api/settings/deployment') body = { environmentLabel: 'Report fixture', environmentName: 'Test' }
      else if (path === '/api/settings/ticket-automation') body = { pollingEnabled: false, email: {} }
      else if (path === '/api/feedback/application/insights') body = { usersToday: 0, usersYesterday: 0 }
      else if (path === '/api/pool/reports') body = { items: [{ sourceEventId: 'P-100', projectId: 'A', projectName: 'Application A', summary: result.suggestedResolution, confidence: .7, similarIncidentCount: 0, processedAt: now }], totalCount: 1, page: 1, pageSize: 25 }
      else if (path === '/api/pool/P-100/report') body = { sourceEventId: 'P-100', projectId: 'A', isReady: true, status: 'Ready', workflowResult: result }
      else if (path === '/api/ticket/history') body = summaries
      else if (path.startsWith('/api/ticket/history/')) {
        const id = path.split('/').at(-1)!
        body = { session: summaries.find(summary => summary.id === id), messages: messages.get(id) }
      } else if (path === '/api/pool/P-100/troubleshoot-progress') {
        const request = route.request().postDataJSON() as { question: string; chatSessionId?: string }
        troubleshootRequests.push(request)
        const answer = `Troubleshooting answer ${troubleshootRequests.length}`
        let chatSessionId = ''
        if (!saveFails) {
          chatSessionId = request.chatSessionId || `report-chat-${summaries.length + 1}`
          if (!messages.has(chatSessionId)) {
            summaries.push({ id: chatSessionId, title: 'Pool P-100', selectedGroupIds: ['A'], isPinned: false, createdAt: now, updatedAt: now, messageCount: 2 })
            messages.set(chatSessionId, [
              { role: 'user', content: 'Pool P-100', createdAt: now },
              { role: 'assistant', content: result.suggestedResolution, result, createdAt: now },
            ])
          }
          messages.get(chatSessionId)!.push(
            { role: 'user', content: request.question, createdAt: now },
            { role: 'assistant', content: answer, result: { ...result, suggestedResolution: answer, detailedAnalysisMarkdown: answer }, createdAt: now },
          )
        }
        await route.fulfill({ contentType: 'application/x-ndjson', body: JSON.stringify({ type: 'result', result: {
          sourceEventId: 'P-100', question: request.question, answer, generatedAt: now, chatSessionId,
          historyPersistenceError: saveFails ? 'The answer was generated but could not be saved to your chat history.' : '',
        } }) + '\n' })
        return
      } else if (path === '/api/ticket/process-progress') {
        mainRequests.push(route.request().postDataJSON() as { chatSessionId?: string; selectedGroupIds?: string[] })
        await route.fulfill({ contentType: 'application/x-ndjson', body: JSON.stringify({ type: 'result', result: { ...result, chatSessionId: mainRequests[0].chatSessionId } }) + '\n' })
        return
      }
      await route.fulfill({ json: body })
    })
    await page.goto(appUrl)
    const openReport = async () => {
      await page.getByRole('button', { name: 'Open Pool Recommendations', exact: true }).click()
      await page.getByRole('button', { name: 'Open report for pool P-100' }).click()
      await expect(page.locator('#pool-troubleshoot-input')).toBeVisible()
    }
    const ask = async (question: string, answerNumber: number) => {
      await page.locator('#pool-troubleshoot-input').fill(question)
      await page.locator('.troubleshoot-send-button').click()
      await expect(page.locator('.pool-troubleshoot-history')).toContainText(`Troubleshooting answer ${answerNumber}`)
    }
    await openReport()
    await ask('First validation question', 1)
    expect(troubleshootRequests[0].chatSessionId).toBeUndefined()
    if (saveFails) {
      await expect(page.getByRole('status').filter({ hasText: 'could not be saved' })).toBeVisible()
      await expect(page.locator('.chat-history-open')).toHaveCount(0)
      return
    }
    await expect(page.locator('.chat-history-open').filter({ hasText: 'Pool P-100' })).toHaveCount(1)
    await ask('Second validation question', 2)
    expect(troubleshootRequests[1].chatSessionId).toBe('report-chat-1')
    await page.locator('.chat-history-open').filter({ hasText: 'Pool P-100' }).click()
    await expect(page.locator('.workspace-chat .message-bubble-assistant')).toHaveCount(3)
    await expect(page.locator('.workspace-chat')).toContainText('Original recommendation')
    await expect(page.locator('.workspace-chat')).toContainText('First validation question')
    await expect(page.locator('select[name="searchScopeMode"]')).toHaveValue('A')
    await page.locator('#incident-input').fill('Continue from saved troubleshooting')
    await page.locator('.composer-poolsense-button').click()
    await expect.poll(() => mainRequests.length).toBe(1)
    expect(mainRequests[0].chatSessionId).toBe('report-chat-1')
    expect(mainRequests[0].selectedGroupIds).toEqual(['A'])

    await openReport()
    await ask('New investigation', 3)
    expect(troubleshootRequests[2].chatSessionId).toBeUndefined()
    await expect(page.locator('.chat-history-open').filter({ hasText: 'Pool P-100' })).toHaveCount(2)
  })
}
