import { test, expect } from '@playwright/test'

const now = '2026-11-10T12:00:00Z'
const commit = 'a'.repeat(40)
const sourceUrl = `https://github.com/example/publication/blob/${commit}/Publish.cs#L1-L10`
const flowMarkdown = `## Process flow

\`\`\`mermaid
flowchart TD
  p_validate["Validate request"]
  p_prepare["Prepare publication"]
  p_persist["Persist publication"]
  p_notify["Notify downstream"]
  p_validate --> p_prepare
  p_prepare --> p_persist
  p_persist --> p_notify
\`\`\`

Source evidence: [Publish.cs:1-10](<${sourceUrl}>)

## Assessment

The retrieved excerpt establishes validation, preparation, persistence and notification. This is a deterministic test fixture, not a live-system verification.`

for (const query of ['onemps publication flow', 'walk me through publication end to end', 'show that as a flow']) {
  test(`explicit process answer visibly renders SVG for "${query}" and restored history`, async ({ page }, testInfo) => {
    test.setTimeout(90000)
    await page.setViewportSize({ width: 1680, height: 1100 })
    const errors: string[] = []
    page.on('pageerror', error => errors.push(error.message))
    const result = {
      queryCategory: 'Info', queryCategorizationReasoning: 'Process explanation; Wiki and code only, Pool skipped.',
      sourcePlan: { pool: false, wiki: true, code: true, label: 'Wiki and code', origin: 'classification' },
      usedPoolDatabase: false,
      suggestedRootCause: 'Publication flow',
      suggestedResolution: 'Requests are validated and prepared before publication is persisted and downstream consumers are notified.',
      detailedAnalysisMarkdown: flowMarkdown, processDiagramStatus: 'provided', processDiagramMessage: 'Flow generated from source-referenced process data.',
      visualizationPlan: { needed: true, kind: 'process', explicitRequest: true, purpose: 'Explain the source-supported process.' },
      visualizationStatus: 'provided', visualizationMessage: 'Flow generated from source-referenced process data.',
      confidence: .85, similarIncidents: [], nyraDocuments: [{ documentId: 'n1', title: 'Publication overview', kbName: 'Operations', content: 'Validate, prepare, persist, notify.', citation: 'N1', score: .9 }],
      codeDocuments: [{ repositoryId: 'r', projectId: 'test', repository: 'example/publication', branch: 'main', commit, filePath: 'Publish.cs', symbol: 'Publish', startLine: 1, endLine: 10, content: 'void Publish() { Validate(); Prepare(); Persist(); Notify(); }', sourceUrl }],
      codeKnowledgeStatus: 'ready', chatSessionId: 'flow',
      failurePattern: { id: 0, system: '', component: '', failureType: '', resolutionCategory: '', ticketId: '', createdAt: now }, failurePatternFrequency: 0,
    }
    const summary = { id: 'flow', title: 'Saved publication flow', selectedGroupIds: [], isPinned: true, updatedAt: now, createdAt: now, messageCount: 2 }
    await page.route('**/api/**', async route => {
      const path = new URL(route.request().url()).pathname
      let body: unknown = []
      if (path === '/api/auth/session') body = { success: true, user: { username: 'test', displayName: 'Test operator', isAdmin: true } }
      else if (path === '/api/projects/groups') body = { groups: [] }
      else if (path === '/api/settings/deployment') body = { environmentLabel: 'Flow fixture', environmentName: 'Test' }
      else if (path === '/api/settings/ticket-automation') body = { pollingEnabled: false, pollIntervalSeconds: 60, email: { deliveryMode: 'Smtp' } }
      else if (path === '/api/feedback/application/insights') { await route.fulfill({ status: 503, json: { message: 'No fixture usage data' } }); return }
      else if (path === '/api/ticket/history') body = [summary]
      else if (path === '/api/ticket/history/flow') body = { session: summary, messages: [
        { role: 'user', content: query, createdAt: now },
        { role: 'assistant', content: result.suggestedResolution, result, createdAt: now },
      ] }
      else if (path === '/api/ticket/process-progress') {
        const progress = [
          { stage: 'categorize', title: 'Classifying request', detail: 'Info: Wiki and code only.', state: 'completed', order: 1, timestampUtc: now },
          { stage: 'pool-data', title: 'Pool incident search', detail: 'Skipped for Info: no Pool embedding or historical search.', state: 'skipped', order: 3, timestampUtc: now },
        ]
        await route.fulfill({ contentType: 'application/x-ndjson', body: progress.map(progress => JSON.stringify({ type: 'progress', progress })).join('\n') + '\n' + JSON.stringify({ type: 'result', result }) + '\n' }); return
      }
      await route.fulfill({ json: body })
    })
    await page.goto('http://localhost:4200')
    await page.locator('#incident-input').fill(query)
    await page.locator('.composer-poolsense-button').click()
    const detail = page.locator('.detailed-analysis-content')
    await expect(page.getByRole('heading', { name: 'Overview', exact: true })).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Explanation', exact: true })).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Root Cause', exact: true })).toHaveCount(0)
    await expect(page.locator('.result-source-chips')).toContainText('Pool not searched')
    await expect(page.locator('.result-source-chips')).toContainText('Wiki and code')
    await expect(detail.locator('.rich-diagram svg')).toHaveCount(1, { timeout: 60000 })
    await expect(detail.locator('.rich-diagram svg')).toBeVisible()
    await expect(detail.locator('.rich-error')).toHaveCount(0)
    await expect(detail.locator('.rich-diagram')).toContainText('Notify downstream')
    await expect(page.getByRole('button', { name: /Process flow and details/ })).toHaveAttribute('aria-expanded', 'true')
    await expect(detail.getByRole('link', { name: 'Publish.cs:1-10', exact: true })).toHaveAttribute('href', sourceUrl)
    await expect(page.locator('.workspace-chat app-code-evidence')).toHaveCount(0)
    if (query === 'onemps publication flow') {
      await page.getByRole('button', { name: 'Collapse operational context' }).click()
      await page.locator('.assistant-card-resolution').scrollIntoViewIfNeeded()
      await page.screenshot({ path: testInfo.outputPath('corrected-publication-flow.png'), animations: 'disabled' })
      await detail.screenshot({ path: testInfo.outputPath('corrected-process-diagram.png'), animations: 'disabled' })
    }
    await page.getByRole('button', { name: 'Saved publication flow', exact: false }).click()
    await expect(detail.locator('.rich-diagram svg')).toHaveCount(1)
    await expect(detail.locator('.rich-diagram svg')).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Explanation', exact: true })).toBeVisible()
    expect(errors).toEqual([])
  })
}
