import { test, expect } from '@playwright/test'

for (const [label, pool, wiki, code, category] of [
  ['Wiki only', false, true, false, 'Info'],
  ['Code only', false, false, true, 'Info'],
  ['Wiki and code', false, true, true, 'Info'],
  ['Pool, Wiki and code', true, true, true, 'Issue'],
] as const) {
  test(`result and restored history show actual ${label} routing and skipped stages`, async ({ page }) => {
    const now = '2026-11-10T12:00:00Z'
    const result = {
      queryCategory: category, queryCategorizationReasoning: 'Selected by current source instruction.',
      sourcePlan: { pool, wiki, code, label, origin: 'explicit' }, usedPoolDatabase: pool,
      suggestedRootCause: category === 'Info' ? 'Source-scoped answer' : 'Reported failure investigation',
      suggestedResolution: 'Review the evidence from the selected sources.',
      detailedAnalysisMarkdown: '## Evidence\n\nOnly the selected sources were searched.',
      confidence: .6,
      similarIncidents: category === 'Issue'
        ? [{ ticketId: 'T-100', problem: 'Application mapping incident', rootCause: 'Invalid mapping', resolution: 'Validate the mapping.', similarity: .88 }]
        : [],
      nyraDocuments: [], codeDocuments: [],
      nyraKnowledgeBaseUsed: wiki, nyraKnowledgeBaseNames: wiki ? ['Operations'] : [],
      codeKnowledgeStatus: code ? 'no-accessible-matches' : 'not-requested', chatSessionId: 'scope',
      failurePattern: { id: 0, system: '', component: '', failureType: '', resolutionCategory: '', ticketId: '', createdAt: now }, failurePatternFrequency: 0,
    }
    const summary = { id: 'scope', title: 'Saved source-scoped answer', selectedGroupIds: [], isPinned: true, createdAt: now, updatedAt: now, messageCount: 2 }
    await page.route('**/api/**', async route => {
      const path = new URL(route.request().url()).pathname
      let body: unknown = []
      if (path === '/api/auth/session') body = { success: true, user: { username: 'test', displayName: 'Test operator', isAdmin: true } }
      else if (path === '/api/projects/groups') body = { groups: [] }
      else if (path === '/api/settings/deployment') body = { environmentLabel: 'Scope fixture', environmentName: 'Test' }
      else if (path === '/api/settings/ticket-automation') body = { pollingEnabled: false, pollIntervalSeconds: 60, email: { deliveryMode: 'Smtp' } }
      else if (path === '/api/feedback/application/insights') { await route.fulfill({ status: 503, json: { message: 'No fixture usage data' } }); return }
      else if (path === '/api/ticket/history') body = [summary]
      else if (path === '/api/ticket/history/scope') body = { session: summary, messages: [
        { role: 'user', content: `search ${label}`, createdAt: now },
        { role: 'assistant', content: result.suggestedResolution, result, createdAt: now },
      ] }
      else if (path === '/api/ticket/process-progress') {
        const progress = [
          { stage: 'categorize', title: 'Source selection', detail: `${label} (explicit)`, state: 'completed', order: 1, timestampUtc: now },
          ...[['pool-data', pool, 3], ['nyra-wiki', wiki, 4], ['code-repositories', code, 5]].map(([stage, selected, order]) =>
            ({ stage, title: stage, detail: selected ? 'Selected source searched.' : 'Excluded by source plan.', state: selected ? 'completed' : 'skipped', order, timestampUtc: now })),
        ]
        await route.fulfill({ contentType: 'application/x-ndjson', body: progress.map(progress => JSON.stringify({ type: 'progress', progress })).join('\n') + '\n' + JSON.stringify({ type: 'result', result }) + '\n' }); return
      }
      await route.fulfill({ json: body })
    })
    await page.goto('http://localhost:4200')
    await page.locator('#incident-input').fill(`search ${label}`)
    await page.locator('.composer-poolsense-button').click()
    const badges = page.locator('.result-source-chips')
    const context = page.locator('#operational-context')
    await expect(badges).toContainText(label)
    if (!pool) await expect(badges).toContainText('Pool not searched')
    if (!wiki) await expect(badges).toContainText('Wiki not searched')
    if (!code) await expect(badges).toContainText('Code not searched')
    await expect(page.getByRole('heading', { name: category === 'Info' ? 'Explanation' : 'Recommended Resolution', exact: true })).toBeVisible()
    await expect(context.getByRole('heading', { name: 'Confidence and routing' })).toBeVisible()
    await expect(context.locator('.confidence-value')).toHaveText('60%')
    await expect(context.getByRole('heading', { name: 'Telemetry snapshot' })).toHaveCount(0)
    await expect(context.locator('.evidence-panel')).toBeVisible()
    if (category === 'Issue') {
      await expect(context.getByRole('tab', { name: /Similar Incidents 1/ })).toBeVisible()
      await expect(context.locator('.evidence-card-id')).toHaveText('#T-100')
    }
    await page.getByRole('button', { name: /Workflow activity/ }).click()
    if (!pool || !wiki || !code) await expect(page.locator('.workflow-activity-state').filter({ hasText: 'skipped' }).first()).toBeVisible()
    await page.getByRole('button', { name: 'Saved source-scoped answer', exact: false }).click()
    await expect(badges).toContainText(label)
    await expect(context.getByRole('heading', { name: 'Confidence and routing' })).toBeVisible()
    await expect(context.getByRole('heading', { name: 'Telemetry snapshot' })).toHaveCount(0)
    await expect(page.locator('.workspace-chat app-code-evidence')).toHaveCount(0)
  })
}
