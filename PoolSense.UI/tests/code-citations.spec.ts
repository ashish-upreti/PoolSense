import { test, expect, type Locator, type Page } from '@playwright/test'
import type { CodeEvidence } from '../src/app/api.service'

const commit = 'a'.repeat(40)
const repository = 'intel-innersource/fixture'
const documents: CodeEvidence[] = [
  { repositoryId: 'repo', projectId: 'test', repository, branch: 'main', commit, filePath: 'IsaHttpClient/OneMPS/ATMPSPublishOneMPSHttpHandler.cs', symbol: 'ATMPSPublishOneMPSHttpHandler', startLine: 1, endLine: 70, content: '// Retrieved excerpt, lines 1-70 (fixture)', sourceUrl: `https://github.com/${repository}/blob/${commit}/IsaHttpClient/OneMPS/ATMPSPublishOneMPSHttpHandler.cs#L1-L70` },
  { repositoryId: 'repo', projectId: 'test', repository, branch: 'main', commit, filePath: 'UI/Grids/PreviousPublicationDataGrid.cs', symbol: 'PreviousPublicationDataGrid', startLine: 121, endLine: 190, content: '// Retrieved excerpt, lines 121-190 (fixture)', sourceUrl: `https://github.com/${repository}/blob/${commit}/UI/Grids/PreviousPublicationDataGrid.cs#L121-L190` },
]
const legacy = '## Evidence considered\n\n- NYRA: [Operations handbook](https://example.test/handbook).\n' + documents.map(doc =>
  `- Code: Repository \`${doc.repository}\`, FilePath \`${doc.filePath}\`, StartLine ${doc.startLine}, supplied range ${doc.startLine}-${doc.endLine}, Commit \`${doc.commit}\`.`).join('\n')
const linked = '## Evidence considered\n\n- NYRA: [Operations handbook](https://example.test/handbook).\n' + documents.map(doc =>
  `- Code: [${doc.filePath}:${doc.startLine}-${doc.endLine}](${doc.sourceUrl}) — Repository \`${doc.repository}\`, indexed commit \`${doc.commit}\`.`).join('\n')
const now = '2026-11-10T10:00:00Z'

async function setup(page: Page, unsafe = false) {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  const evidence = unsafe ? [...documents, ...['javascript:window.__codeAttack=true', 'http://example.test/code', 'https://user:password@example.test/code', ''].map((sourceUrl, index) => ({ ...documents[0], filePath: `Unsafe-${index}.cs`, sourceUrl }))] : documents
  const result = (markdown: string) => ({
    suggestedRootCause: 'Publication handler requires investigation.',
    suggestedResolution: 'Inspect the supplied source and validate the deployed version.',
    detailedAnalysisMarkdown: markdown,
    reasoning: 'The supplied handler and grid are relevant; deployment version is unconfirmed.',
    confidence: .7, similarIncidents: [], nyraDocuments: [], codeDocuments: evidence,
    codeKnowledgeStatus: 'ready', chatSessionId: 'main',
    failurePattern: { id: 0, system: '', component: '', failureType: '', resolutionCategory: '', ticketId: '', createdAt: now },
    failurePatternFrequency: 0,
  })
  const summary = { id: 'main', title: 'Saved prose-only code citations', selectedGroupIds: ['test'], isPinned: true, createdAt: now, updatedAt: now, messageCount: 2 }
  const project = { id: 1, projectId: 'test', projectName: 'Publication application', knowledgeLookbackYears: 2, similaritySearchLimit: 5, sendEmail: false, poolingEnabled: false, emailRecipients: '', applicationFilter: 'Test', nyraKbNames: '', githubUrls: '', createdAt: now }
  const unsafeProse = '\n\n[bad](javascript:window.__codeAttack=true)\n\n<script>window.__codeAttack=true</script>'
  await page.context().route('https://github.com/intel-innersource/fixture/**', route => route.fulfill({ contentType: 'text/html', body: '<h1>Pinned source fixture</h1>' }))
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    let body: unknown = []
    if (path === '/api/auth/session') body = { success: true, user: { username: 'test', displayName: 'Test operator', isAdmin: true } }
    else if (path === '/api/projects') body = [project]
    else if (path === '/api/projects/groups') body = [{ groupId: 'test', displayName: project.projectName }]
    else if (path === '/api/settings/deployment') body = { environmentLabel: 'Citation fixture', environmentName: 'Test' }
    else if (path === '/api/settings/ticket-automation') body = { pollingEnabled: false, pollIntervalSeconds: 60, email: { deliveryMode: 'Smtp' } }
    else if (path === '/api/feedback/application/insights') { await route.fulfill({ status: 503, json: { message: 'Fixture has no usage data' } }); return }
    else if (path === '/api/ticket/history') body = [summary]
    else if (path === '/api/ticket/history/main') body = { session: summary, messages: [
      { role: 'user', content: 'Explain publication behavior', createdAt: now },
      { role: 'assistant', content: 'Inspect the source.', result: result(legacy), createdAt: now },
    ] }
    else if (path === '/api/ticket/process-progress') {
      await route.fulfill({ contentType: 'application/x-ndjson', body: JSON.stringify({ type: 'result', result: result(linked + (unsafe ? unsafeProse : '')) }) + '\n' }); return
    }
    else if (path === '/api/github/access') body = { configured: true, available: true, login: 'test', message: '' }
    else if (path.endsWith('/repositories')) body = [{ id: 'repo', projectId: 'test', url: `https://github.com/${repository}`, branch: 'main', enabled: true, status: 'ready', message: '', indexedCommit: commit, fileCount: 2, symbolCount: 2 }]
    else if (path.endsWith('/code-chat/history/code')) body = { session: { id: 'code', projectId: 'test', title: 'Saved code prose', updatedAt: now, messageCount: 2 }, messages: [
      { role: 'user', content: 'Explain publication', citations: [], createdAt: now },
      { role: 'assistant', content: legacy, citations: evidence, createdAt: now },
    ] }
    else if (path.endsWith('/code-chat/history')) body = [{ id: 'code', projectId: 'test', title: 'Saved code prose', updatedAt: now, messageCount: 2 }]
    else if (path.endsWith('/code-chat/ask')) body = { projectId: 'test', sessionId: 'new', question: 'Explain publication', answer: linked, citations: evidence, generatedAt: now }
    else if (path === '/api/pool/123/report') body = { sourceEventId: '123', status: 'Ready', isReady: true, workflowResult: result(legacy), projectId: 'test', projectName: project.projectName, processedAt: now }
    else if (path.endsWith('/troubleshoot-progress')) {
      await route.fulfill({ contentType: 'application/x-ndjson', body: JSON.stringify({ type: 'result', result: { sourceEventId: '123', question: 'Explain publication', answer: linked, generatedAt: now, codeDocuments: evidence, codeKnowledgeStatus: 'ready', nyraDocuments: [] } }) + '\n' }); return
    }
    await route.fulfill({ json: body })
  })
  return errors
}

async function checkReferences(container: Locator) {
  const references = container.locator('app-code-evidence')
  await expect(references).toBeVisible()
  await expect(references.getByRole('link', { name: /^Open source:/ })).toHaveCount(2)
  for (const document of documents) {
    const anchor = references.getByRole('link', { name: `Open source: ${document.filePath}:${document.startLine}-${document.endLine}`, exact: true })
    await expect(anchor).toBeVisible()
    await expect(anchor).toHaveAttribute('href', document.sourceUrl)
    await expect(anchor).toHaveAttribute('target', '_blank')
    await expect(anchor).toHaveAttribute('rel', 'noopener noreferrer')
  }
  await expect(references.getByText('Indexed source; verify that this commit matches the deployed version.')).toBeVisible()
  await expect(references.locator('.commit').first()).toContainText(commit)
  await expect(references.locator('.repository').first()).toHaveText(repository)
}

test('main answers keep inline citations and rail sources without a duplicate source list', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1680, height: 1100 })
  const errors = await setup(page)
  await page.goto('http://localhost:4200')
  await page.locator('#incident-input').fill('Explain publication behavior')
  await page.locator('.composer-poolsense-button').click()
  const mainAnswer = page.locator('.message-bubble-assistant').filter({ has: page.locator('.detailed-analysis') }).last()
  await expect(page.locator('.detailed-analysis-content')).toBeVisible()
  for (const document of documents) {
    await expect(page.locator('.detailed-analysis-content').getByRole('link', { name: `${document.filePath}:${document.startLine}-${document.endLine}`, exact: true })).toHaveAttribute('href', document.sourceUrl)
  }
  await expect(mainAnswer.locator('app-code-evidence')).toHaveCount(0)
  await mainAnswer.locator('.detailed-analysis').screenshot({ path: testInfo.outputPath('code-citations-linked.png') })
  const context = page.locator('#operational-context')
  await context.getByRole('tab', { name: /^Code/ }).click()
  await checkReferences(context)
  const popupEvent = page.waitForEvent('popup')
  await mainAnswer.locator('.detailed-analysis-content').getByRole('link', { name: `${documents[0].filePath}:1-70`, exact: true }).click()
  const popup = await popupEvent
  await expect(popup).toHaveURL(documents[0].sourceUrl)
  await expect(popup.getByRole('heading', { name: 'Pinned source fixture' })).toBeVisible()
  await popup.close()
  // Reload to exercise the stored legacy fixture, not the live conversation's newer cached answer.
  await page.reload()
  await page.getByRole('button', { name: 'Saved prose-only code citations', exact: false }).click()
  await expect(page.locator('.detailed-analysis-content')).toContainText('FilePath')
  await expect(mainAnswer.locator('app-code-evidence')).toHaveCount(0)
  await context.getByRole('tab', { name: /^Code/ }).click()
  await checkReferences(context)
  await page.getByRole('button', { name: 'Collapse operational context' }).click()
  await expect(context).toBeHidden()
  await expect(mainAnswer.locator('app-code-evidence')).toHaveCount(0)
  await page.getByRole('button', { name: 'Expand operational context' }).click()
  await expect(context.getByRole('tab', { name: /^Code/ })).toHaveClass(/evidence-tab-active/)
  await checkReferences(context)
  await page.getByRole('button', { name: /Detailed analysis/ }).click()
  await expect(mainAnswer.locator('app-code-evidence')).toHaveCount(0)
  await expect(context.locator('app-code-evidence details').first()).not.toHaveAttribute('open', '')
  await context.locator('app-code-evidence summary').first().click()
  await expect(context.locator('app-code-evidence pre').first()).toContainText('Retrieved excerpt')
  expect(errors).toEqual([])
})

test('repository panels stay stationary at their shared edge and leave room for conversation', async ({ page }) => {
  await setup(page)
  await page.setViewportSize({ width: 1366, height: 768 })
  await page.goto('http://localhost:4200')
  await page.getByRole('button', { name: 'Open code repositories', exact: true }).click()
  const controls = page.locator('.code-rag-controls')
  const conversation = page.locator('.code-rag-chat-card')
  await expect(controls.getByRole('combobox', { name: 'Application', exact: true })).toBeEnabled()
  await page.mouse.move(0, 0)
  const beforeControls = await controls.boundingBox()
  const beforeConversation = await conversation.boundingBox()
  expect(beforeControls).not.toBeNull()
  expect(beforeConversation).not.toBeNull()
  if (!beforeControls || !beforeConversation) throw new Error('Code repository panels are missing')

  for (const y of [beforeControls.y + beforeControls.height - 1, beforeConversation.y + 1]) {
    await page.mouse.move(beforeControls.x + beforeControls.width / 2, y)
    // Sample across several hover-transition durations with the pointer stationary.
    for (let sample = 0; sample < 5; sample++) {
      await page.waitForTimeout(100)
      expect(await controls.boundingBox()).toEqual(beforeControls)
      expect(await conversation.boundingBox()).toEqual(beforeConversation)
    }
  }

  expect(beforeConversation.y).toBeLessThan(240)
  await expect(page.locator('.code-rag-composer')).toBeInViewport({ ratio: 1 })
  await expect(page.locator('.code-rag-transcript')).toBeInViewport({ ratio: 1 })

  await page.setViewportSize({ width: 390, height: 844 })
  await conversation.scrollIntoViewIfNeeded()
  const bounds = await conversation.boundingBox()
  expect(bounds).not.toBeNull()
  if (!bounds) throw new Error('Conversation panel is missing')
  expect(bounds.x).toBeGreaterThanOrEqual(0)
  expect(bounds.x + bounds.width).toBeLessThanOrEqual(390)
})

test('repository history remains selectable while its request is running', async ({ page }) => {
  const errors = await setup(page)
  let releaseRequest!: () => void
  const requestGate = new Promise<void>(resolve => { releaseRequest = resolve })
  await page.route(url => url.pathname.endsWith('/code-chat/ask'), async route => {
    await requestGate
    await route.fulfill({ json: { projectId: 'test', sessionId: 'new', question: 'Explain publication', answer: linked, citations: documents, generatedAt: now, searchMode: 'quick' } })
  })
  await page.goto('http://localhost:4200')
  await page.getByRole('button', { name: 'Open code repositories', exact: true }).click()
  await page.locator('#code-chat-question').fill('Explain publication')
  await page.locator('.code-rag-composer').getByRole('button', { name: 'Send', exact: true }).click()
  await expect(page.locator('.code-rag-message-pending')).toBeVisible()

  await page.getByRole('button', { name: 'Saved code prose', exact: false }).click()
  await expect(page.locator('.code-rag-transcript')).toContainText('Evidence considered')
  await expect(page.locator('.code-rag-message-pending')).toHaveCount(0)
  releaseRequest()
  await expect(page.locator('.code-rag-history-item')).toBeEnabled()
  await expect(page.locator('.code-rag-transcript')).toContainText('FilePath')
  expect(errors).toEqual([])
})

test('repository live and restored answers expose shared source links without expanding excerpts', async ({ page }) => {
  const errors = await setup(page)
  await page.goto('http://localhost:4200')
  await page.getByRole('button', { name: 'Open code repositories', exact: true }).click()
  await page.locator('#code-chat-question').fill('Explain publication')
  await page.locator('.code-rag-composer').getByRole('button', { name: 'Send', exact: true }).click()
  const answer = page.locator('.code-rag-transcript .message-bubble-assistant').last()
  await checkReferences(answer)
  await expect(answer.locator('app-rich-response').getByRole('link', { name: `${documents[0].filePath}:1-70`, exact: true })).toHaveAttribute('href', documents[0].sourceUrl)
  await page.getByRole('button', { name: 'Saved code prose', exact: false }).click()
  await checkReferences(answer)
  await expect(answer.locator('app-rich-response')).toContainText('FilePath')
  expect(errors).toEqual([])
})

test('saved report and pool followup retain authoritative linked source references', async ({ page }) => {
  const errors = await setup(page)
  await page.goto('http://localhost:4200/Pool/123')
  const savedAnswer = page.locator('.message-bubble-assistant').filter({ has: page.locator('.detailed-analysis') }).last()
  await expect(savedAnswer.locator('.detailed-analysis-content')).toBeVisible()
  await expect(savedAnswer.locator('app-code-evidence')).toHaveCount(0)
  const context = page.locator('#operational-context')
  await context.getByRole('tab', { name: /^Code/ }).click()
  await checkReferences(context)
  await page.locator('#pool-troubleshoot-input').fill('Explain publication')
  await page.locator('#pool-troubleshoot-input').press('Enter')
  const followup = page.locator('.pool-troubleshoot-answer')
  await checkReferences(followup)
  await expect(followup.locator('app-rich-response').getByRole('link', { name: `${documents[1].filePath}:121-190`, exact: true })).toHaveAttribute('href', documents[1].sourceUrl)
  expect(errors).toEqual([])
})

test('unsafe source URLs remain explicit non-links and malicious Markdown cannot execute', async ({ page }) => {
  const errors = await setup(page, true)
  await page.goto('http://localhost:4200')
  await page.locator('#incident-input').fill('Check source safety')
  await page.locator('.composer-poolsense-button').click()
  const answer = page.locator('.message-bubble-assistant').filter({ has: page.locator('.detailed-analysis') }).last()
  await expect(answer.locator('app-code-evidence')).toHaveCount(0)
  const context = page.locator('#operational-context')
  await context.getByRole('tab', { name: /^Code/ }).click()
  await checkReferences(context)
  await expect(context.locator('app-code-evidence .source-unavailable')).toHaveCount(4)
  expect(await page.locator('a[href^="javascript:"],a[href^="http:"],a[href*="user:password"],script:not([src]):not([type]),[onerror]').count()).toBe(0)
  expect(await page.evaluate(() => (window as unknown as Record<string, unknown>)['__codeAttack'])).toBeUndefined()
  expect(errors).toEqual([])
})

test('restored code references wrap on narrow screens in both themes', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 })
  const errors = await setup(page)
  await page.goto('http://localhost:4200')
  await page.getByRole('button', { name: 'Toggle chat history' }).click()
  await page.getByRole('button', { name: 'Saved prose-only code citations', exact: false }).click()
  const answer = page.locator('.message-bubble-assistant').filter({ has: page.locator('.detailed-analysis') }).last()
  await expect(answer.locator('.detailed-analysis-content')).toBeVisible()
  await expect(answer.locator('app-code-evidence')).toHaveCount(0)
  const context = page.locator('#operational-context')
  await context.getByRole('tab', { name: /^Code/ }).click()
  for (const theme of ['dark', 'light']) {
    await page.evaluate(theme => { document.documentElement.dataset['theme'] = theme }, theme)
    await checkReferences(context)
    const bounds = (await context.locator('app-code-evidence').boundingBox())!
    expect(bounds.x + bounds.width).toBeLessThanOrEqual(391)
  }
  expect(errors).toEqual([])
})
