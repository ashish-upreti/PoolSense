import { test, expect, type Page } from '@playwright/test'
import { readFile } from 'node:fs/promises'
import { prepareMermaid, richBlocks, validateChart, validateMermaid } from '../src/app/rich-response'

const fence = (language: string, value: string) => '```' + language + '\n' + value + '\n```'
const chart = (type: string) => JSON.stringify(type === 'histogram'
  ? { type, title: 'Observed duration distribution', samples: [1, 2, 2, 3, 4, 5], bins: 3 }
  : { type, title: `${type} evidence`, ...(type === 'scatter' ? {} : { labels: ['Observed A', 'Observed B', 'Observed C'] }),
    datasets: [{ label: 'Supplied observations', data: type === 'scatter' ? [{ x: 1, y: 3 }, { x: 2, y: 5 }] : [3, 5, 2] }] })
const diagrams = [
  'flowchart LR\n A[Reported symptom] --> B[Validate evidence]\n B --> C[Confirm fix]',
  'timeline\n title Supplied incident events\n Reported : Symptom captured\n Validated : Evidence reviewed',
  'mindmap\n root((Investigation))\n  Evidence\n   Incidents\n   Source code\n  Validation',
  'flowchart TD\n Lead[Incident lead] --> Ops[Operations]\n Lead --> Eng[Engineering]',
  'sequenceDiagram\n participant User\n participant API\n User->>API: Investigate\n API-->>User: Evidence',
  'pie title Supplied evidence\n "Logs" : 3\n "Tickets" : 2',
]
const markdown = [
  '## Evidence assessment\n\n**Illustrative test fixture** — not production measurements.\n\n> Confirm supplied evidence before acting.',
  '| Check | Observation |\n|---|---|\n| Mapping | Available |\n| Validation | Pending |\n\n- [x] Gather logs\n- [ ] Validate change',
  ...['bar', 'line', 'pie', 'scatter', 'histogram'].map(type => fence('chart', chart(type))),
  ...diagrams.map(source => fence('mermaid', source)),
  fence('typescript', 'const literal = "<script>not executable</script>";\n  // preserve indentation\nconsole.log(literal)'),
  fence('text', '+-------+     +--------+\n| Input | --> | Output |\n+-------+     +--------+'),
  '<script>window.__richAttack = true</script>\n\n<img src=x onerror="window.__richAttack=true">\n\n[unsafe](javascript:window.__richAttack=true)\n\n[safe](https://example.test)',
].join('\n\n')
const now = '2026-11-10T09:00:00Z'
const result = {
  suggestedRootCause: 'Missing mapping requires validation.',
  suggestedResolution: '1. Inspect supplied evidence.\n2. Validate the mapping before applying a change.',
  detailedAnalysisMarkdown: markdown,
  reasoning: '## Evidence basis\n\n' + fence('chart', chart('bar')),
  confidence: .75, similarIncidents: [], nyraDocuments: [], codeDocuments: [],
  failurePattern: { id: 0, system: 'Test system', component: 'Mapping', failureType: '', resolutionCategory: 'Validation', ticketId: '', createdAt: now },
  failurePatternFrequency: 0, chatSessionId: 'saved-main',
}
const summary = { id: 'saved-main', title: 'Saved rich investigation', selectedGroupIds: ['test-project'], isPinned: true, createdAt: now, updatedAt: now, messageCount: 2 }
const project = { id: 1, projectId: 'test-project', projectName: 'Test Project', knowledgeLookbackYears: 2, similaritySearchLimit: 5, sendEmail: false, poolingEnabled: false, emailRecipients: '', applicationFilter: 'Test', nyraKbNames: '', githubUrls: '', createdAt: now }

async function fixture(page: Page, responseMarkdown = markdown) {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    let body: unknown = []
    if (path === '/api/auth/session') body = { success: true, user: { username: 'test', displayName: 'Test User', isAdmin: true } }
    else if (path === '/api/projects') body = [project]
    else if (path === '/api/projects/groups') body = [{ groupId: 'test-project', displayName: 'Test Project' }]
    else if (path === '/api/settings/deployment') body = { environmentLabel: 'Visual fixture', environmentName: 'Test' }
    else if (path === '/api/settings/ticket-automation') body = { pollingEnabled: false, pollIntervalSeconds: 60, email: { deliveryMode: 'Smtp' } }
    else if (path === '/api/feedback/application/insights') body = {
      rangeDays: 30, totalFeedback: 0, feedbackLast30Days: 0, previousFeedback30Days: 0,
      uniqueActiveUsersLast30Days: 4, previousUniqueActiveUsers30Days: 4, usersToday: 4, usersYesterday: 4,
      totalLoginsLast30Days: 4, previousTotalLogins30Days: 4, recommendationsProcessedLast30Days: 0,
      previousRecommendationsProcessed30Days: 0, helpfulAiFeedbackLast30Days: 0, previousHelpfulAiFeedback30Days: 0,
      totalAiFeedbackLast30Days: 0, notHelpfulAiFeedbackLast30Days: 0,
      feedbackTypes: [], dailyFeedbackCounts: [], dailyAiFeedbackCounts: [], generatedAtUtc: now,
    }
    else if (path === '/api/ticket/history') body = [summary]
    else if (path === '/api/ticket/history/saved-main') body = { session: summary, messages: [
      { role: 'user', content: 'Investigate supplied evidence', createdAt: now },
      { role: 'assistant', content: result.suggestedResolution, result: { ...result, detailedAnalysisMarkdown: responseMarkdown }, createdAt: now },
    ] }
    else if (path === '/api/ticket/process-progress') {
      await route.fulfill({ contentType: 'application/x-ndjson', body: JSON.stringify({ type: 'result', result: { ...result, detailedAnalysisMarkdown: responseMarkdown } }) + '\n' }); return
    }
    else if (path === '/api/github/access') body = { configured: true, available: true, login: 'test', message: '' }
    else if (path.endsWith('/repositories')) body = [{ id: 'repo', projectId: 'test-project', url: 'https://github.com/example/test', branch: 'main', enabled: true, status: 'ready', message: '', indexedCommit: 'abc', fileCount: 2, symbolCount: 2 }]
    else if (path.endsWith('/code-chat/history/saved-code')) body = { session: { id: 'saved-code', projectId: 'test-project', title: 'Saved rich code', updatedAt: now, messageCount: 2 }, messages: [
      { role: 'user', content: 'Explain architecture', citations: [], createdAt: now },
      { role: 'assistant', content: responseMarkdown, citations: [], createdAt: now },
    ] }
    else if (path.endsWith('/code-chat/history')) body = [{ id: 'saved-code', projectId: 'test-project', title: 'Saved rich code', updatedAt: now, messageCount: 2 }]
    else if (path.endsWith('/code-chat/ask')) body = { projectId: 'test-project', sessionId: 'new-code', question: 'Explain architecture', answer: responseMarkdown, citations: [], generatedAt: now }
    else if (path === '/api/pool/123/report') body = { sourceEventId: '123', status: 'Ready', isReady: true, workflowResult: { ...result, detailedAnalysisMarkdown: responseMarkdown }, projectId: 'test-project', projectName: 'Test Project', processedAt: now }
    else if (path.endsWith('/troubleshoot-progress')) {
      await route.fulfill({ contentType: 'application/x-ndjson', body: JSON.stringify({ type: 'result', result: { sourceEventId: '123', question: 'Explain checks', answer: responseMarkdown, generatedAt: now, nyraDocuments: [] } }) + '\n' }); return
    }
    await route.fulfill({ json: body })
  })
  return errors
}

for (const type of ['bar', 'line', 'pie', 'scatter', 'histogram']) {
  test(`validates ${type} chart`, () => { expect(validateChart(chart(type)).type).toBe(type) })
}
test('explicit histogram and deterministic sample bins', () => {
  expect(validateChart(chart('histogram')).datasets[0].data).toEqual([3, 1, 2])
  expect(validateChart(JSON.stringify({ type: 'histogram', title: 'Counts', labels: ['0-2'], datasets: [{ label: 'Count', data: [3] }] })).labels).toEqual(['0-2'])
  const tiny = validateChart('{"type":"histogram","title":"Tiny","samples":[0,5e-324],"bins":40}')
  expect(tiny.datasets[0].data.reduce<number>((sum, value) => sum + Number(value), 0)).toBe(2)
})
for (const [label, source] of [
  ['invalid JSON', '{'],
  ['invalid type', chart('radar')],
  ['non-string type', chart('bar').replace('"type":"bar"', '"type":["bar"]')],
  ['nonfinite', chart('bar').replace('[3,5,2]', '[1e309,5,2]')],
  ['oversized', JSON.stringify({ type: 'bar', title: 'Large', labels: Array(201).fill('A'), datasets: [{ label: 'A', data: Array(201).fill(1) }] })],
  ['extra executable fields', chart('bar').replace('"type":', '"onclick":"alert(1)","type":')],
  ['invalid samples', '{"type":"histogram","title":"Bad","samples":[1,"two"]}'],
  ['invalid scatter', '{"type":"scatter","title":"Bad","datasets":[{"label":"A","data":[{"x":1,"y":null}]}]}'],
]) {
  test(`rejects ${label}`, () => { expect(() => validateChart(source)).toThrow() })
}
for (const [index, source] of diagrams.entries()) test(`accepts supported diagram ${index}`, () => { expect(() => validateMermaid(source)).not.toThrow() })
for (const source of [
  'flowchart LR\n A-->B\n click A "https://evil.test"',
  '%%{init: {"securityLevel":"loose"}}%%\nflowchart LR\n A-->B',
  'flowchart LR\n A["<img src=x onerror=alert(1)>"]',
  'flowchart LR\n' + 'A-->B\n'.repeat(101),
  'architecture-beta\nservice evil',
]) test(`rejects unsafe/oversized diagram ${source.slice(0, 20)}`, () => {
  expect(() => validateMermaid(source)).toThrow()
  expect(() => prepareMermaid(source)).toThrow()
})
test('route placeholders and parentheses are quoted only in plain flowchart rectangles', () => {
  const source = 'flowchart TD\n A[Submit question] --> B[POST /pool/{poolId}/troubleshoot (stream)]\n B --> C[Read response (NDJSON)]'
  expect(prepareMermaid(source)).toBe('flowchart TD\n A[Submit question] --> B["POST /pool/{poolId}/troubleshoot (stream)"]\n B --> C["Read response (NDJSON)"]')
  const quoted = 'graph LR\n A["Literal B[POST /pool/{id}]"] --> B["POST /pool/{poolId}/troubleshoot"]'
  expect(prepareMermaid(quoted)).toBe(quoted)
  const shapes = 'flowchart TD\n A[(Database)] --> B[[Read response]]\n B --> C[/Input/]\n C --> D{Ready?}'
  expect(prepareMermaid(shapes)).toBe(shapes)
  const sequence = 'sequenceDiagram\n participant API\n API->>API: POST /pool/{poolId}/troubleshoot'
  expect(prepareMermaid(sequence)).toBe(sequence)
})
test('GFM and verbatim code are split once without altering code', () => {
  const blocks = richBlocks(markdown)
  expect(blocks.filter(block => block.kind === 'chart')).toHaveLength(5)
  expect(blocks.find(block => block.language === 'typescript')?.source).toContain('  // preserve indentation')
  expect(blocks.find(block => block.kind === 'markdown')?.source).toContain('<table>')
})

test('live main answer renders all real visuals, safety, exports, theme and lifecycle', async ({ page }, testInfo) => {
  test.setTimeout(180000)
  await page.setViewportSize({ width: 1680, height: 1100 })
  const errors = await fixture(page)
  await page.goto('http://localhost:4200')
  await page.locator('#incident-input').fill('Investigate evidence')
  await page.locator('.composer-poolsense-button').click()
  const detail = page.locator('.detailed-analysis-content')
  await expect(detail.locator('.rich-chart:not([hidden]) canvas')).toHaveCount(5)
  await expect(detail.locator('.rich-diagram svg')).toHaveCount(6, { timeout: 90000 })
  await expect(detail.locator('.rich-error')).toHaveCount(0)
  for (const canvas of await detail.locator('.rich-chart:not([hidden]) canvas').all()) {
    expect(await canvas.evaluate(element => {
      const context = (element as HTMLCanvasElement).getContext('2d')!
      return context.getImageData(0, 0, context.canvas.width, context.canvas.height).data.some(value => value !== 0)
    })).toBe(true)
  }
  await expect(detail.locator('table').first()).toContainText('Mapping')
  await expect(detail.locator('input[type=checkbox]')).toHaveCount(2)
  expect(await page.evaluate(() => (window as unknown as Record<string, unknown>)['__richAttack'])).toBeUndefined()
  expect(await detail.locator('script,img,[onerror],a[href^="javascript:"]').count()).toBe(0)
  await expect(detail.getByRole('link', { name: 'safe', exact: true })).toHaveAttribute('rel', 'noopener noreferrer')
  const firstChart = detail.locator('app-rich-block').first()
  await firstChart.getByRole('button', { name: 'Expand', exact: true }).click()
  await expect(firstChart.locator('dialog')).toHaveJSProperty('open', true)
  expect(await firstChart.locator('dialog').evaluate(element => element.matches(':modal'))).toBe(true)
  await expect(firstChart.locator('dialog')).toHaveAttribute('class', /rich-block/)
  await page.keyboard.press('Escape')
  await expect(firstChart.getByRole('button', { name: 'Expand', exact: true })).toBeVisible()
  const png = page.waitForEvent('download')
  await firstChart.getByRole('button', { name: 'PNG', exact: true }).click()
  const pngFile = await png
  expect(pngFile.suggestedFilename()).toBe('chart.png')
  await pngFile.saveAs(testInfo.outputPath('exported-chart.png'))
  expect(Array.from((await readFile(testInfo.outputPath('exported-chart.png'))).subarray(0, 8))).toEqual([137, 80, 78, 71, 13, 10, 26, 10])
  const diagram = detail.locator('app-rich-block').nth(5)
  const svgDownload = page.waitForEvent('download')
  await diagram.getByRole('button', { name: 'SVG', exact: true }).click()
  const svgFile = await svgDownload
  expect(svgFile.suggestedFilename()).toBe('diagram.svg')
  await svgFile.saveAs(testInfo.outputPath('exported-diagram.svg'))
  const exportedSvg = await readFile(testInfo.outputPath('exported-diagram.svg'), 'utf8')
  expect(exportedSvg).toContain('<svg')
  expect(exportedSvg).not.toMatch(/<script|foreignObject|onerror=|javascript:/i)
  await diagram.getByRole('button', { name: 'Source', exact: true }).click()
  await expect(diagram.locator('pre')).toContainText('flowchart')
  const previousSvg = await diagram.locator('svg').innerHTML()
  await page.evaluate(() => { document.documentElement.dataset['theme'] = 'dark' })
  await expect.poll(() => diagram.locator('svg').innerHTML()).not.toBe(previousSvg)
  await expect(detail.locator('.rich-diagram svg')).toHaveCount(6)
  await expect(detail.locator('.rich-error')).toHaveCount(0)
  await detail.locator('app-rich-block').nth(0).screenshot({ path: testInfo.outputPath('rich-chart-dark.png') })
  await detail.locator('app-rich-block').nth(1).screenshot({ path: testInfo.outputPath('rich-line-dark.png') })
  await detail.locator('app-rich-block').nth(3).screenshot({ path: testInfo.outputPath('rich-scatter-dark.png') })
  await diagram.screenshot({ path: testInfo.outputPath('rich-flowchart-dark.png') })
  await detail.locator('app-rich-block').nth(6).screenshot({ path: testInfo.outputPath('rich-timeline-dark.png') })
  await detail.locator('app-rich-block').nth(7).screenshot({ path: testInfo.outputPath('rich-mindmap-dark.png') })
  await detail.locator('app-rich-block').nth(8).screenshot({ path: testInfo.outputPath('rich-organization-dark.png') })
  await page.evaluate(() => { document.documentElement.dataset['theme'] = 'light' })
  await expect(detail.locator('.rich-diagram svg')).toHaveCount(6)
  await detail.locator('app-rich-block').nth(2).screenshot({ path: testInfo.outputPath('rich-pie-light.png') })
  await page.getByRole('button', { name: 'Detailed analysis', exact: false }).click()
  await expect(detail).toHaveCount(0)
  await page.getByRole('button', { name: 'Detailed analysis', exact: false }).click()
  await expect(page.locator('.detailed-analysis-content .rich-diagram svg')).toHaveCount(6)
  await page.locator('.assistant-card-resolution').scrollIntoViewIfNeeded()
  await page.screenshot({ path: testInfo.outputPath('rich-result-overview.png') })
  const code = detail.locator('app-rich-block').filter({ hasText: 'const literal' })
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write'])
  await code.getByRole('button', { name: 'Copy', exact: true }).click()
  await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toContain('  // preserve indentation')
  const sourceDownload = page.waitForEvent('download')
  await code.getByRole('button', { name: 'Download source', exact: true }).click()
  const sourceFile = await sourceDownload
  await sourceFile.saveAs(testInfo.outputPath('exported-source.txt'))
  expect(await readFile(testInfo.outputPath('exported-source.txt'), 'utf8')).toContain('  // preserve indentation')
  await code.screenshot({ path: testInfo.outputPath('rich-code-controls.png') })
  expect(errors).toEqual([])
})

test('restored main history on narrow layout renders source safely in both themes', async ({ page }, testInfo) => {
  test.setTimeout(120000)
  await page.setViewportSize({ width: 390, height: 844 })
  const errors = await fixture(page)
  await page.goto('http://localhost:4200')
  await page.getByRole('button', { name: 'Toggle chat history' }).click()
  await page.getByRole('button', { name: 'Saved rich investigation', exact: false }).click()
  const detail = page.locator('.detailed-analysis-content')
  await expect(detail.locator('.rich-diagram svg')).toHaveCount(6, { timeout: 90000 })
  for (const theme of ['light', 'dark']) {
    await page.evaluate(theme => { document.documentElement.dataset['theme'] = theme }, theme)
    await expect(detail.locator('.rich-diagram svg')).toHaveCount(6)
    const bounds = await detail.boundingBox()
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(391)
  }
  await detail.locator('app-rich-block').nth(4).screenshot({ path: testInfo.outputPath('rich-histogram-mobile.png') })
  expect(errors).toEqual([])
})

test('repository live and restored chat use the same visual renderer', async ({ page }, testInfo) => {
  test.setTimeout(120000)
  const source = '## Repository flow (conceptual)\n\n' + fence('mermaid', diagrams[0]) + '\n\n' + fence('chart', chart('bar'))
  const errors = await fixture(page, source)
  await page.goto('http://localhost:4200')
  await page.getByRole('button', { name: 'Open code repositories', exact: true }).click()
  await page.locator('#code-chat-question').fill('Explain architecture')
  await page.locator('.code-rag-composer').getByRole('button', { name: 'Send', exact: true }).click()
  const transcript = page.locator('.code-rag-transcript')
  await expect(transcript.locator('.rich-diagram svg')).toHaveCount(1, { timeout: 60000 })
  await expect(transcript.locator('.rich-chart:not([hidden]) canvas')).toHaveCount(1)
  await page.getByRole('button', { name: 'Saved rich code', exact: false }).click()
  await expect(transcript.locator('.rich-diagram svg')).toHaveCount(1)
  await transcript.screenshot({ path: testInfo.outputPath('rich-repository.png') })
  expect(errors).toEqual([])
})

test('repository source-referenced graph renders without unsafe Mermaid warnings and survives history', async ({ page }) => {
  test.setTimeout(90000)
  const sourceUrl = 'https://github.com/org/repo/blob/abc123/src/Scheduler.cs#L1-L10'
  const source = '## Process flow\n\n' + fence('mermaid',
    'flowchart TD\n  p_run["Run scheduler"]\n  p_save["Save work"]\n  p_run --> p_save\n') +
    `\n\nSource evidence: [src/Scheduler.cs:1-10](<${sourceUrl}>)\n\nThe scheduler saves work.`
  const block = richBlocks(source).find(block => block.kind === 'mermaid')!
  expect(() => validateMermaid(block.source)).not.toThrow()
  const errors = await fixture(page, source)
  await page.goto(process.env['POOLSENSE_UI_TEST_URL'] ?? 'http://localhost:4200')
  await page.getByRole('button', { name: 'Open code repositories', exact: true }).click()
  await page.locator('#code-chat-question').fill('Show the scheduler workflow')
  await page.locator('.code-rag-composer').getByRole('button', { name: 'Send', exact: true }).click()
  const transcript = page.locator('.code-rag-transcript')
  await expect(transcript.locator('.rich-diagram svg')).toHaveCount(1, { timeout: 60000 })
  await expect(transcript.locator('.rich-diagram svg')).toContainText('Run scheduler')
  await expect(transcript.locator('.rich-error')).toHaveCount(0)
  await expect(transcript.getByRole('link', { name: 'src/Scheduler.cs:1-10' })).toHaveAttribute('href', sourceUrl)
  await page.getByRole('button', { name: 'Saved rich code', exact: false }).click()
  await expect(transcript.locator('.rich-diagram svg')).toHaveCount(1)
  await expect(transcript.locator('.rich-error')).toHaveCount(0)
  await expect(transcript).toContainText('The scheduler saves work.')
  expect(errors).toEqual([])
})

for (const width of [1440, 390]) {
  test(`answer paragraphs and headings retain readable gaps at ${width}px, live and restored`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 })
    const source = '## Overview\n\nFirst explanation paragraph.\n\nSecond explanation paragraph.\n\n### Details\n\nFurther explanation.'
    const errors = await fixture(page, source)
    await page.goto(process.env['POOLSENSE_UI_TEST_URL'] ?? 'http://localhost:4200')
    await page.getByRole('button', { name: 'Open code repositories', exact: true }).click()
    await page.locator('#code-chat-question').fill('Explain the repository')
    await page.locator('.code-rag-composer').getByRole('button', { name: 'Send', exact: true }).click()
    const response = page.locator('.code-rag-transcript .rich-response')
    for (const restored of [false, true]) {
      if (restored) {
        await page.getByRole('button', { name: 'Saved rich code', exact: false }).click()
        // The live answer stays on screen until the saved conversation replaces it.
        await expect(page.locator('.code-rag-transcript .message-bubble-user')).toContainText('Explain architecture')
      }
      await expect(response.locator('h3')).toHaveText('Details')
      await response.scrollIntoViewIfNeeded()
      const gaps = await response.evaluate(element => {
        const paragraphs = element.querySelectorAll('p')
        const heading = element.querySelector('h3')!.getBoundingClientRect()
        return {
          rem: Number.parseFloat(getComputedStyle(document.documentElement).fontSize),
          paragraphs: paragraphs[1].getBoundingClientRect().top - paragraphs[0].getBoundingClientRect().bottom,
          beforeHeading: heading.top - paragraphs[1].getBoundingClientRect().bottom,
          afterHeading: paragraphs[2].getBoundingClientRect().top - heading.bottom,
        }
      })
      expect(gaps.paragraphs).toBeGreaterThanOrEqual(.75 * gaps.rem)
      expect(gaps.beforeHeading).toBeGreaterThanOrEqual(1.25 * gaps.rem)
      expect(gaps.afterHeading).toBeGreaterThanOrEqual(.5 * gaps.rem)
    }
    expect(errors).toEqual([])
  })
}

test('repository answers show how long they took, in minutes once past sixty seconds', async ({ page }) => {
  const errors = await fixture(page, 'Timed repository answer.')
  let release: (() => void) | undefined
  await page.route('**/api/code-chat/ask', async route => {
    await new Promise<void>(resolve => { release = resolve })
    await route.fulfill({ json: { projectId: 'test-project', sessionId: 'new-code', question: 'How long?', answer: 'Timed repository answer.', citations: [], generatedAt: now } })
  })
  await page.clock.install()
  await page.goto(process.env['POOLSENSE_UI_TEST_URL'] ?? 'http://localhost:4200')
  await page.getByRole('button', { name: 'Open code repositories', exact: true }).click()
  await page.locator('#code-chat-question').fill('How long?')
  await page.locator('.code-rag-composer').getByRole('button', { name: 'Send', exact: true }).click()
  await expect.poll(() => typeof release).toBe('function')
  const duration = page.locator('.code-rag-transcript .message-bubble-assistant .answer-duration')
  await expect(duration).toHaveCount(0)
  await page.clock.fastForward(72_000)
  release!()
  // Real time also passes while the fixture responds, so allow a few seconds past the 72 s jump.
  await expect(duration.locator('.answer-duration-text')).toHaveText(/^Generated in 1 min 1[2-9] s$/)
  await expect(duration).toHaveAttribute('title', 'Elapsed time from sending your question to receiving this completed response.')

  await page.getByRole('button', { name: 'Saved rich code', exact: false }).click()
  await expect(page.locator('.code-rag-transcript .message-bubble-user')).toContainText('Explain architecture')
  await expect(duration).toHaveCount(0)
  expect(errors).toEqual([])
})

for (const surface of ['main', 'repository']) {
  test(`route placeholders render literally in ${surface} live and restored diagrams`, async ({ page }) => {
    test.setTimeout(90000)
    const diagram = 'flowchart TD\n A[Submit question] --> B[POST /pool/{poolId}/troubleshoot (stream)]\n B --> C[Read response (NDJSON)]'
    const errors = await fixture(page, fence('mermaid', diagram))
    await page.goto(process.env['POOLSENSE_UI_TEST_URL'] ?? 'http://localhost:4200')
    if (surface === 'repository') {
      await page.getByRole('button', { name: 'Open code repositories', exact: true }).click()
      await page.locator('#code-chat-question').fill('Explain the troubleshoot endpoint')
      await page.locator('.code-rag-composer').getByRole('button', { name: 'Send', exact: true }).click()
    } else {
      await page.locator('#incident-input').fill('Explain the troubleshoot endpoint')
      await page.locator('.composer-poolsense-button').click()
    }
    const response = page.locator(surface === 'repository' ? '.code-rag-transcript' : '.detailed-analysis-content')
    await expect(response.locator('.rich-diagram svg')).toHaveCount(1, { timeout: 60000 })
    const endpointLabel = response.locator('.rich-diagram svg g.node').filter({ hasText: '/pool/{poolId}/troubleshoot' })
    await expect(endpointLabel).toHaveText(/^POST\s*\/pool\/\{poolId\}\/troubleshoot\s+\(stream\)$/)
    await expect(response.locator('.rich-error')).toHaveCount(0)
    const block = response.locator('app-rich-block').first()
    await block.getByRole('button', { name: 'Source', exact: true }).click()
    await expect(block.locator('pre code')).toHaveText(diagram)
    if (surface === 'repository') {
      await page.getByRole('button', { name: 'Saved rich code', exact: false }).click()
    } else {
      await page.reload()
      await page.getByRole('button', { name: 'Saved rich investigation', exact: false }).click()
    }
    for (const theme of ['light', 'dark']) {
      await page.evaluate(theme => { document.documentElement.dataset['theme'] = theme }, theme)
      await expect(response.locator('.rich-diagram svg')).toHaveCount(1)
      await expect(endpointLabel).toHaveText(/^POST\s*\/pool\/\{poolId\}\/troubleshoot\s+\(stream\)$/)
    }
    await expect(response.locator('.rich-error')).toHaveCount(0)
    expect(errors).toEqual([])
  })
}

for (const restored of [false, true]) {
  for (const length of [8000, 8001, 16000]) {
    test(`repository followup bounds ${length}-character ${restored ? 'restored' : 'live'} answer history`, async ({ page }) => {
      const longAnswer = 'Previous explanation. ' + 'x'.repeat(length - 43) + ' End of saved answer.'
      expect(longAnswer.length).toBe(length)
      const errors = await fixture(page, longAnswer)
      await page.goto(process.env['POOLSENSE_UI_TEST_URL'] ?? 'http://localhost:4200')
      await page.getByRole('button', { name: 'Open code repositories', exact: true }).click()
      if (restored) {
        await page.getByRole('button', { name: 'Saved rich code', exact: false }).click()
      } else {
        await page.locator('#code-chat-question').fill('Explain the scheduler')
        await page.locator('.code-rag-composer').getByRole('button', { name: 'Send', exact: true }).click()
      }
      const transcript = page.locator('.code-rag-transcript')
      await expect(transcript).toContainText(longAnswer)

      await page.route('**/api/code-chat/ask', async route => {
        const request = route.request().postDataJSON()
        if (request.history.some((turn: { content: string }) => turn.content.length > 8000)) {
          await route.fulfill({ status: 400, json: { message: "The field Content must be a string or array type with a maximum length of '8000'." } })
          return
        }
        await route.fulfill({ json: { projectId: 'test-project', sessionId: 'followup-code', question: request.question, answer: 'Follow-up accepted.', citations: [], generatedAt: now } })
      })
      const sent = page.waitForRequest(request => new URL(request.url()).pathname === '/api/code-chat/ask' && request.method() === 'POST')
      await page.locator('#code-chat-question').fill('Why?')
      await page.locator('.code-rag-composer').getByRole('button', { name: 'Send', exact: true }).click()
      const payload = (await sent).postDataJSON()
      const priorAnswer = payload.history.find((turn: { role: string }) => turn.role === 'assistant')
      expect(payload.question).toBe('Why?')
      expect(payload.sessionId).toBe(restored ? 'saved-code' : 'new-code')
      expect(priorAnswer.content.length).toBeLessThanOrEqual(8000)
      if (length <= 8000) expect(priorAnswer.content).toBe(longAnswer)
      else expect(priorAnswer.content).toBe(longAnswer.slice(0, 8000 - '\n[Earlier message truncated]'.length) + '\n[Earlier message truncated]')
      await expect(transcript).toContainText('Follow-up accepted.')
      await expect(transcript).toContainText(longAnswer)
      await expect(transcript.locator('.code-rag-message-error')).toHaveCount(0)
      expect(errors).toEqual([])
    })
  }
}

test('saved report and pool followup render actual visuals', async ({ page }, testInfo) => {
  test.setTimeout(120000)
  const source = '## Validation path (conceptual)\n\n' + fence('mermaid', diagrams[0]) + '\n\n' + fence('chart', chart('line'))
  const errors = await fixture(page, source)
  await page.goto('http://localhost:4200/Pool/123')
  await expect(page.locator('.detailed-analysis-content .rich-diagram svg')).toHaveCount(1, { timeout: 60000 })
  await page.locator('#pool-troubleshoot-input').fill('Explain checks')
  await page.locator('#pool-troubleshoot-input').press('Enter')
  const followup = page.locator('.pool-troubleshoot-answer')
  await expect(followup.locator('.rich-diagram svg')).toHaveCount(1, { timeout: 60000 })
  await expect(followup.locator('.rich-chart:not([hidden]) canvas')).toHaveCount(1)
  await followup.screenshot({ path: testInfo.outputPath('rich-pool-followup.png') })
  expect(errors).toEqual([])
})

test('invalid charts and unsafe/malformed diagrams show errors and original source', async ({ page }) => {
  test.setTimeout(90000)
  const source = [fence('chart', '{broken'), fence('mermaid', 'flowchart LR\n A[broken'), fence('mermaid', 'flowchart LR\n A-->B\n click A "https://evil.test"')].join('\n\n')
  const errors = await fixture(page, source)
  await page.goto('http://localhost:4200')
  await page.locator('#incident-input').fill('Invalid fixture')
  await page.locator('.composer-poolsense-button').click()
  const detail = page.locator('.detailed-analysis-content')
  await expect(detail.locator('.rich-error')).toHaveCount(3, { timeout: 60000 })
  await expect(detail.locator('pre')).toHaveCount(3)
  await expect(detail.locator('svg')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('rapid theme changes and destroying pending visuals never publish stale output', async ({ page }) => {
  test.setTimeout(90000)
  const errors = await fixture(page, fence('mermaid', diagrams[0]) + '\n\n' + fence('chart', chart('scatter')))
  await page.goto('http://localhost:4200')
  await page.locator('#incident-input').fill('Render then dismiss')
  await page.locator('.composer-poolsense-button').click()
  await expect(page.locator('.detailed-analysis-content')).toBeVisible()
  await page.evaluate(() => {
    for (const theme of ['dark', 'light', 'dark']) document.documentElement.dataset['theme'] = theme
  })
  await page.getByRole('button', { name: '+ New chat', exact: true }).click()
  await expect(page.locator('.detailed-analysis-content')).toHaveCount(0)
  await page.getByRole('button', { name: 'Saved rich investigation', exact: false }).click()
  await expect(page.locator('.detailed-analysis-content .rich-diagram svg')).toHaveCount(1, { timeout: 60000 })
  await expect(page.locator('.detailed-analysis-content .rich-error')).toHaveCount(0)
  expect(await page.locator('.detailed-analysis-content .rich-diagram').innerText()).toContain('Reported symptom')
  expect(errors).toEqual([])
})

test('ordinary Markdown/code does not load chart or diagram engines', async ({ page }) => {
  const requests: string[] = []
  page.on('request', request => {
    if (/mermaid|chart_js|chart\.js|chunk.*chart/i.test(request.url())) requests.push(request.url())
  })
  await fixture(page, '## Plain answer\n\n' + fence('text', 'A --> B\n  untouched ASCII'))
  await page.goto('http://localhost:4200')
  // Keep the reasoning rail free of charts as well.
  await page.route('**/api/ticket/process-progress', route => route.fulfill({
    contentType: 'application/x-ndjson',
    body: JSON.stringify({ type: 'result', result: { ...result, detailedAnalysisMarkdown: '## Plain answer\n\n' + fence('text', 'A --> B\n  untouched ASCII'), reasoning: 'No visual required.' } }) + '\n',
  }))
  await page.locator('#incident-input').fill('Plain answer')
  await page.locator('.composer-poolsense-button').click()
  await expect(page.locator('.detailed-analysis-content pre')).toContainText('  untouched ASCII')
  expect(requests).toEqual([])
})
