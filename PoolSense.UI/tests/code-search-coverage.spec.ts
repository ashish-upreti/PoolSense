import { test, expect } from '@playwright/test'

for (const [status, message] of [
  ['partial', 'Code search is incomplete'],
  ['timed-out', 'Code search reached its deadline'],
  ['not-ready', 'Code indexes are not ready'],
  ['no-accessible-matches', 'No matching source was found'],
  ['no-accessible-repositories', 'No repository source is currently accessible'],
  ['disabled', 'Code retrieval is disabled'],
]) {
  test(`code search reports ${status} honestly and restores coverage from history`, async ({ page }) => {
    const now = '2026-11-10T10:00:00Z'
    const coverage = {
      authorizedRepositories: status === 'no-accessible-repositories' || status === 'disabled' ? 0 : 2,
      searchedRepositories: status === 'partial' ? 1 : status === 'no-accessible-matches' ? 2 : 0,
      failedRepositories: 0, authorizationComplete: status !== 'partial' && status !== 'timed-out',
      deadlineExceeded: status === 'partial' || status === 'timed-out', expandedFallback: false,
      semanticStatus: status === 'partial' ? 'unavailable' : 'disabled', knowledgeStatus: 'ready',
      ...(status === 'partial' ? { semanticIndexedChunks: 64, semanticTargetChunks: 768, semanticEligibleChunks: 49050, semanticTargetReached: false } : {}),
      oldestIndexedAt: now, warnings: status === 'partial' ? ['Deadline reached; accumulated evidence retained.'] : [],
    }
    const commit = 'a'.repeat(40)
    const document = { repositoryId: 'repo', projectId: 'test', repository: 'example/service', branch: 'main', commit,
      filePath: 'Handler.cs', symbol: 'Handler', startLine: 1, endLine: 2, content: 'class Handler {}',
      sourceUrl: `https://github.com/example/service/blob/${commit}/Handler.cs#L1-L2` }
    const result = {
      suggestedRootCause: 'Investigate supplied evidence.', suggestedResolution: 'Validate the current source.',
      detailedAnalysisMarkdown: '## Assessment\n\nUse the evidence actually retrieved.', confidence: .5, reasoning: 'Coverage is explicit.',
      similarIncidents: [], nyraDocuments: [], codeDocuments: status === 'partial' ? [document] : [],
      codeKnowledgeStatus: status, codeSearchCoverage: coverage,
      failurePattern: { id: 0, system: '', component: '', failureType: '', resolutionCategory: '', ticketId: '', createdAt: now },
      failurePatternFrequency: 0, chatSessionId: 'saved',
    }
    const summary = { id: 'saved', title: 'Saved code coverage', selectedGroupIds: [], isPinned: true, updatedAt: now, createdAt: now, messageCount: 2 }
    await page.route('**/api/**', async route => {
      const path = new URL(route.request().url()).pathname
      let body: unknown = []
      if (path === '/api/auth/session') body = { success: true, user: { username: 'test', displayName: 'Test operator', isAdmin: true } }
      else if (path === '/api/projects/groups') body = { groups: [] }
      else if (path === '/api/settings/deployment') body = { environmentLabel: 'Coverage fixture', environmentName: 'Test' }
      else if (path === '/api/settings/ticket-automation') body = { pollingEnabled: false, pollIntervalSeconds: 60, email: { deliveryMode: 'Smtp' } }
      else if (path === '/api/feedback/application/insights') { await route.fulfill({ status: 503, json: { message: 'Fixture has no usage data' } }); return }
      else if (path === '/api/ticket/history') body = [summary]
      else if (path === '/api/ticket/history/saved') body = { session: summary, messages: [
        { role: 'user', content: 'Investigate code', createdAt: now },
        { role: 'assistant', content: result.suggestedResolution, result, createdAt: now },
      ] }
      else if (path === '/api/ticket/process-progress') {
        await route.fulfill({ contentType: 'application/x-ndjson', body: JSON.stringify({ type: 'result', result }) + '\n' }); return
      }
      await route.fulfill({ json: body })
    })
    await page.goto('http://localhost:4200')
    await page.locator('#incident-input').fill('Investigate code')
    await page.locator('.composer-poolsense-button').click()
    const context = page.locator('#operational-context')
    await context.getByRole('tab', { name: /^Code/ }).click()
    await expect(context.locator('app-code-search-status')).toContainText(message)
    if (status === 'partial') {
      await expect(context.locator('app-code-search-status')).toContainText('Searched 1 of 2 confirmed accessible repositories')
      await expect(context.getByRole('link', { name: /^Open source:/ })).toHaveAttribute('href', document.sourceUrl)
      await expect(page.locator('.workspace-chat app-code-search-status')).toContainText('Code search is incomplete')
      await expect(context.locator('app-code-search-status')).toContainText('64 / 768 target vectors')
      await expect(context.locator('app-code-search-status')).toContainText('49050 eligible source chunks')
      await expect(context.locator('app-code-search-status')).toContainText('bounded background batches')
    }
    await page.getByRole('button', { name: 'Saved code coverage', exact: false }).click()
    await expect(context.locator('app-code-search-status')).toContainText(message)
    if (status === 'partial') {
      await page.reload()
      await page.getByRole('button', { name: 'Saved code coverage', exact: true }).click()
      await page.locator('#operational-context').getByRole('tab', { name: /^Code/ }).click()
      await expect(context.locator('app-code-search-status')).toContainText('64 / 768 target vectors')
      Object.assign(coverage, { semanticIndexedChunks: 768, semanticTargetReached: true })
      await page.reload()
      await page.getByRole('button', { name: 'Saved code coverage', exact: true }).click()
      await page.locator('#operational-context').getByRole('tab', { name: /^Code/ }).click()
      await expect(context.locator('app-code-search-status')).toContainText('Configured targets reached; this is not full-corpus coverage.')
    }
    await expect(page.locator('.workspace-chat app-code-evidence')).toHaveCount(0)
  })
}
