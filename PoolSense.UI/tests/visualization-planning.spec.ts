import { test, expect, type Page } from '@playwright/test'

const now = '2026-11-10T12:00:00Z'
const exactQuery = 'Can you create a image to explain how the multiple project applications are related to each other if at all'
const relationship = `## Application relationships

\`\`\`mermaid
flowchart TD
  p_a_ui["Application A UI"]
  p_a_api["Application A API"]
  p_b_ui["Application B UI"]
  p_b_api["Application B API"]
  p_a_ui --> p_a_api
  p_b_ui --> p_b_api
\`\`\`

Supported partial map only. A relationship between applications A and B is not established.

Source evidence: [Application A fixture](https://example.test/fixture/a); [Application B fixture](https://example.test/fixture/b).

## Assessment

**Mock evidence fixture, not a verified production application map.** Two independent source excerpts support these links; no cross-application connection is asserted.`

async function fixture(page: Page, kind: string, markdown: string, status = 'provided', category = 'Info') {
  const message = status === 'unavailable' ? 'Unable to build a relationship diagram from retrieved evidence. Connections are not established.' : 'Only source-supported relationships are shown.'
  const value = {
    queryCategory: category, usedPoolDatabase: category === 'Issue',
    sourcePlan: { pool: category === 'Issue', wiki: true, code: true, label: category === 'Issue' ? 'Pool, Wiki and code' : 'Wiki and code', origin: 'classification' },
    visualizationPlan: { needed: kind !== 'none', kind, explicitRequest: true, purpose: 'Clarify the supplied evidence without assuming missing relationships.' },
    visualizationStatus: status, visualizationMessage: message,
    processDiagramStatus: kind === 'relationship' || kind === 'flowchart' ? status : '',
    suggestedRootCause: status === 'unavailable' ? 'Application relationships are not established.' : 'Source-supported application groups.',
    suggestedResolution: status === 'unavailable' ? message : 'The retrieved fixtures establish two separate groups, not a relationship between every application.',
    detailedAnalysisMarkdown: markdown,
    confidence: .66, reasoning: 'Use only current authorized evidence.', similarIncidents: [], nyraDocuments: [], codeDocuments: [],
    chatSessionId: 'visual', failurePatternFrequency: 0,
  }
  const summary = { id: 'visual', title: 'Saved planned visualization', selectedGroupIds: [], isPinned: true, createdAt: now, updatedAt: now, messageCount: 2 }
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    let body: unknown = []
    if (path === '/api/auth/session') body = { success: true, user: { username: 'test', displayName: 'Test operator', isAdmin: true } }
    else if (path === '/api/projects/groups') body = { groups: [] }
    else if (path === '/api/settings/deployment') body = { environmentLabel: 'Mock relationship fixture', environmentName: 'Test' }
    else if (path === '/api/settings/ticket-automation') body = { pollingEnabled: false, email: {} }
    else if (path === '/api/ticket/history') body = [summary]
    else if (path === '/api/ticket/history/visual') body = { session: summary, messages: [
      { role: 'user', content: exactQuery, createdAt: now },
      { role: 'assistant', content: value.suggestedResolution, result: value, createdAt: now },
    ] }
    else if (path === '/api/ticket/process-progress') {
      await route.fulfill({ contentType: 'application/x-ndjson', body: JSON.stringify({ type: 'result', result: value }) + '\n' })
      return
    }
    await route.fulfill({ json: body })
  })
  await page.goto('http://localhost:4200')
}

for (const query of [exactQuery, 'how do these apps talk to each other', 'draw that']) {
  test(`planned relationship visibly renders limited SVG and reopens: ${query}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 1680, height: 1100 })
    await fixture(page, 'relationship', relationship)
    await page.locator('#incident-input').fill(query)
    await page.locator('.composer-poolsense-button').click()
    const detail = page.locator('.detailed-analysis-content')
    await expect(page.getByRole('button', { name: /Visualization and details/ })).toHaveAttribute('aria-expanded', 'true')
    await expect(detail.locator('.rich-diagram svg')).toBeVisible()
    await expect(detail.locator('.rich-diagram svg .edgePaths path')).toHaveCount(2)
    await expect(detail).toContainText('relationship between applications A and B is not established')
    await expect(detail.locator('.rich-error')).toHaveCount(0)
    await expect(page.locator('.result-source-chips')).toContainText('Pool not searched')
    await expect(page.getByRole('heading', { name: 'Explanation', exact: true })).toBeVisible()
    if (query === exactQuery) {
      await page.getByRole('button', { name: 'Collapse operational context' }).click()
      await detail.screenshot({ path: testInfo.outputPath('corrected-relationship-fixture.png'), animations: 'disabled' })
    }
    await page.reload()
    await page.getByRole('button', { name: 'Saved planned visualization', exact: true }).click()
    await expect(page.getByRole('button', { name: /Visualization and details/ })).toHaveAttribute('aria-expanded', 'true')
    await expect(detail.locator('.rich-diagram svg')).toBeVisible()
    await expect(detail.locator('.rich-diagram svg .edgePaths path')).toHaveCount(2)
  })
}

test('planned actual-value chart renders canvas and accessible supplied values after reopening', async ({ page }) => {
  const chart = { type: 'bar', title: 'Supplied request counts (requests)', labels: ['A', 'B'], datasets: [{ label: 'Requests', data: [3, 5] }] }
  await fixture(page, 'bar', '## Supplied observations\n\n```chart\n' + JSON.stringify(chart) + '\n```\n\nMock source supplied A=3 requests and B=5 requests.')
  await page.locator('#incident-input').fill('Compare the supplied request counts')
  await page.locator('.composer-poolsense-button').click()
  await expect(page.locator('.rich-chart canvas')).toBeVisible()
  await page.getByText('Chart data (accessible table)', { exact: true }).click()
  await expect(page.locator('.rich-data table')).toContainText('3')
  await expect(page.locator('.rich-data table')).toContainText('5')
  await page.reload()
  await page.getByRole('button', { name: 'Saved planned visualization', exact: true }).click()
  await expect(page.locator('.rich-chart canvas')).toBeVisible()
})

test('planned table remains a real table and Issue retains incident headings', async ({ page }) => {
  await fixture(page, 'table', '## Comparison\n\n| Application | Confirmed relationship |\n|---|---|\n| A | UI calls API |\n| B | UI calls API |', 'provided', 'Issue')
  await page.locator('#incident-input').fill('Compare dependencies while troubleshooting')
  await page.locator('.composer-poolsense-button').click()
  await expect(page.locator('.detailed-analysis-content table')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Root Cause', exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Overview', exact: true })).toHaveCount(0)
})

test('unavailable planned visual is explicit with no false accompanying-diagram claim', async ({ page }) => {
  await fixture(page, 'relationship', '## Visualization unavailable\n\nUnable to build a relationship diagram from retrieved evidence. Connections are not established.', 'unavailable')
  await page.locator('#incident-input').fill(exactQuery)
  await page.locator('.composer-poolsense-button').click()
  await expect(page.getByRole('status').filter({ hasText: 'Unable to build a relationship diagram' })).toBeVisible()
  await expect(page.locator('.detailed-analysis-content')).toContainText('Connections are not established')
  await expect(page.locator('.rich-diagram svg')).toHaveCount(0)
  await expect(page.locator('.assistant-card-resolution')).not.toContainText('accompanying')
})
