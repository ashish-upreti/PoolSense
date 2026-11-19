import { test, expect, type Page } from '@playwright/test'

async function mockWorkspace(page: Page) {
  const errors: string[] = []
  let queries = 0
  page.on('pageerror', error => errors.push(error.message))
  const now = '2026-11-10T10:00:00Z'
  const summary = { id: 'panels', title: 'Validate the application mapping and inspect operational evidence', selectedGroupIds: ['test'], isPinned: true, createdAt: now, updatedAt: now, messageCount: 2 }
  const project = { id: 1, projectId: 'test', projectName: 'Production application scope', knowledgeLookbackYears: 2, similaritySearchLimit: 5, sendEmail: false, poolingEnabled: false, emailRecipients: '', applicationFilter: 'Test', nyraKbNames: '', githubUrls: '', createdAt: now }
  const result = {
    suggestedRootCause: 'Application mapping requires validation.',
    suggestedResolution: '1. Inspect the mapping.\n2. Validate against the operating procedure.',
    detailedAnalysisMarkdown: '## Validation\n\nReview the supplied operational evidence before changing the mapping.',
    confidence: .8, reasoning: 'The incident and operating procedure both describe the mapping.',
    similarIncidents: [{ ticketId: 'T-100', problem: 'Application mapping validation', rootCause: '', resolution: 'Validate the mapping.', similarity: .9 }],
    nyraDocuments: [{ documentId: 'doc', kbName: 'Operations', title: 'Operations handbook', content: 'Validate the application mapping against the current configuration.', citation: 'OPS-1', sourceUrl: '', score: .9 }],
    codeDocuments: [], failurePattern: { id: 1, system: 'Application system', component: 'Mapping', failureType: 'Configuration', resolutionCategory: 'Validation', ticketId: 'T-100', createdAt: now }, failurePatternFrequency: 1,
  }
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    let body: unknown = []
    if (path === '/api/auth/session') body = { success: true, user: { username: 'test', displayName: 'Test operator', isAdmin: true } }
    else if (path === '/api/projects') body = [project]
    else if (path === '/api/projects/groups') body = [{ groupId: 'test', displayName: project.projectName }]
    else if (path === '/api/settings/deployment') body = { environmentLabel: 'Panel fixture', environmentName: 'Test' }
    else if (path === '/api/settings/ticket-automation') body = { pollingEnabled: false, pollIntervalSeconds: 60, email: { deliveryMode: 'Smtp' } }
    else if (path === '/api/ticket/history') body = [summary]
    else if (path === '/api/ticket/history/panels') body = { session: summary, messages: [{ role: 'user', content: 'Validate application mapping', createdAt: now }, { role: 'assistant', content: result.suggestedResolution, result, createdAt: now }] }
    else if (path === '/api/feedback/application/insights') {
      await route.fulfill({ status: 503, json: { message: 'Not available in this fixture' } }); return
    }
    else if (path.startsWith('/api/ticket/process')) queries++
    await route.fulfill({ json: body })
  })
  return { errors, queries: () => queries }
}

const originalGrid = `
  .app-shell-history { grid-template-columns: minmax(232px, .58fr) minmax(248px, .56fr) minmax(0, 1.5fr) minmax(316px, .9fr) !important; }
  .app-shell-history.app-shell-sidebar-collapsed { grid-template-columns: 84px minmax(248px, .56fr) minmax(0, 1.5fr) minmax(316px, .9fr) !important; }
`

for (const width of [1280, 1440, 1680, 1920]) {
  test(`desktop rail ratio and panel permutations at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1100 })
    const state = await mockWorkspace(page)
    await page.goto('http://localhost:4200')
    await page.locator('.chat-history-open').click()
    const context = page.locator('#operational-context')
    const workspace = page.locator('.workspace-chat')
    const history = page.locator('.chat-history-panel')
    const shell = page.locator('.app-shell')
    await expect(context.getByRole('heading', { name: 'Operational context' })).toBeVisible()
    await expect(context.getByRole('heading', { name: 'Telemetry snapshot' })).toHaveCount(0)
    await expect(context.locator('.confidence-value')).toHaveText('80%')
    await expect(context.getByRole('tab', { name: /Similar Incidents 1/ })).toBeVisible()
    await expect(context.locator('.evidence-card-id')).toHaveText('#T-100')
    for (const navigationExpanded of [false, true]) {
      if (navigationExpanded) await page.getByRole('button', { name: 'Expand navigation sidebar' }).click()
      const legacy = await page.addStyleTag({ content: originalGrid })
      const before = (await history.boundingBox())!.width
      if (width === 1680 && !navigationExpanded) await page.screenshot({ path: testInfo.outputPath('panels-before-width.png') })
      await legacy.evaluate(element => element.remove())
      const after = (await history.boundingBox())!.width
      const ratio = after / before
      console.log(`${width}px navigation ${navigationExpanded ? 'expanded' : 'collapsed'}: history ${before.toFixed(1)} -> ${after.toFixed(1)}px (${ratio.toFixed(3)})`)
      expect(ratio).toBeGreaterThan(.53)
      expect(ratio).toBeLessThan(.68)
      if (width === 1680 && !navigationExpanded) await page.screenshot({ path: testInfo.outputPath('panels-compact-history-light.png') })
      for (const button of await history.locator('.chat-history-header button').all()) {
        const bounds = (await button.boundingBox())!
        const rail = (await history.boundingBox())!
        expect(bounds.x).toBeGreaterThanOrEqual(rail.x)
        expect(bounds.x + bounds.width).toBeLessThanOrEqual(rail.x + rail.width + 1)
      }
      await history.hover()
      const rename = history.getByRole('button', { name: 'Rename conversation' })
      await rename.click()
      await expect(history.getByRole('textbox', { name: 'Conversation title' })).toBeVisible()
      await history.getByRole('button', { name: 'Cancel rename' }).click()
      for (const historyOpen of [true, false]) {
        if (!historyOpen) await page.getByRole('button', { name: 'Toggle chat history' }).click()
        await context.getByRole('tab', { name: 'NYRA KB (Wiki)', exact: false }).click()
        await expect(context.getByText('Operations handbook', { exact: true })).toBeVisible()
        const previousMainWidth = (await workspace.boundingBox())!.width
        const toggle = page.getByRole('button', { name: 'Collapse operational context' })
        await expect(toggle).toHaveAttribute('aria-controls', 'operational-context')
        await expect(toggle).toHaveAttribute('aria-expanded', 'true')
        await toggle.click()
        await expect(context).toBeHidden()
        await expect(page.getByRole('button', { name: 'Expand operational context' })).toHaveAttribute('aria-expanded', 'false')
        const expandedMain = (await workspace.boundingBox())!
        expect(expandedMain.width).toBeGreaterThan(previousMainWidth + 150)
        await expect(workspace.getByText('Application mapping requires validation.', { exact: true })).toBeVisible()
        if (width === 1680 && !navigationExpanded && historyOpen) console.log(`1680px main conversation: ${previousMainWidth.toFixed(1)} -> ${expandedMain.width.toFixed(1)}px after context collapse`)
        const shellBounds = (await shell.boundingBox())!
        expect(expandedMain.x + expandedMain.width).toBeCloseTo(shellBounds.x + shellBounds.width, 0)
        const columns = await shell.evaluate(element => getComputedStyle(element).gridTemplateColumns.split(' ').length)
        expect(columns).toBe(historyOpen ? 3 : 2)
        if (width === 1680 && !navigationExpanded && historyOpen) {
          await page.screenshot({ path: testInfo.outputPath('panels-context-collapsed.png'), animations: 'disabled' })
        }
        await page.getByRole('button', { name: 'Expand operational context' }).click()
        await expect(context).toBeVisible()
        await expect(context.getByRole('tab', { name: 'NYRA KB (Wiki)', exact: false })).toHaveClass(/evidence-tab-active/)
        await expect(context.getByText('Operations handbook', { exact: true })).toBeVisible()
        await expect(context.getByText('Application system', { exact: true })).toBeVisible()
        expect(await shell.evaluate(element => getComputedStyle(element).gridTemplateColumns.split(' ').length)).toBe(historyOpen ? 4 : 3)
      }
      await page.getByRole('button', { name: 'Toggle chat history' }).click()
    }
    await page.evaluate(() => { document.documentElement.dataset['theme'] = 'dark' })
    if (width === 1680) await page.screenshot({ path: testInfo.outputPath('panels-compact-history-dark.png') })
    expect(state.queries()).toBe(0)
    expect(state.errors).toEqual([])
  })
}

for (const width of [1000, 390]) {
  test(`narrow drawer and operational context at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 })
    const state = await mockWorkspace(page)
    await page.goto('http://localhost:4200')
    await page.getByRole('button', { name: 'Toggle chat history' }).click()
    const drawer = page.locator('.chat-history-drawer')
    const drawerBounds = (await drawer.boundingBox())!
    expect(drawerBounds.width).toBeGreaterThan(250)
    expect(drawerBounds.x + drawerBounds.width).toBeLessThanOrEqual(width)
    await drawer.locator('.chat-history-open').click()
    await expect(drawer).toHaveCount(0)
    const context = page.locator('#operational-context')
    await context.getByRole('tab', { name: 'NYRA KB (Wiki)', exact: false }).click()
    for (const theme of ['light', 'dark']) {
      await page.evaluate(theme => { document.documentElement.dataset['theme'] = theme }, theme)
      await page.getByRole('button', { name: 'Collapse operational context' }).click()
      await expect(context).toBeHidden()
      const bounds = (await page.locator('.workspace-chat').boundingBox())!
      expect(bounds.x + bounds.width).toBeLessThanOrEqual(width + 1)
      await page.getByRole('button', { name: 'Expand operational context' }).click()
      await expect(context.getByText('Operations handbook', { exact: true })).toBeVisible()
      await expect(context.getByRole('tab', { name: 'NYRA KB (Wiki)', exact: false })).toHaveClass(/evidence-tab-active/)
    }
    await page.getByRole('button', { name: 'Toggle chat history' }).click()
    await expect(drawer).toBeVisible()
    await page.getByRole('button', { name: 'Close chat history', exact: true }).click()
    if (width === 390) await page.screenshot({ path: testInfo.outputPath('panels-mobile.png') })
    expect(state.queries()).toBe(0)
    expect(state.errors).toEqual([])
  })
}
