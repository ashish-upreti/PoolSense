import { expect, test, type Page } from '@playwright/test'

const appUrl = process.env.POOLSENSE_TEST_URL ?? 'http://localhost:4200'
const navName = /^What’s new(?: \(New\))?$/
const title = 'What’s new in PoolSense'

async function openWorkspace(page: Page) {
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    let json: unknown = []
    if (path === '/api/auth/session') json = { success: true, user: { username: 'test', displayName: 'Test operator', isAdmin: true } }
    if (path === '/api/projects/groups') json = { groups: [] }
    if (path === '/api/settings/deployment') json = { environmentLabel: 'Release fixture', environmentName: 'Test' }
    if (path === '/api/settings/ticket-automation') json = { pollingEnabled: false, email: {} }
    await route.fulfill({ json })
  })
  await page.goto(appUrl)
  await expect(page.getByRole('button', { name: navName })).toBeVisible()
}

test('opens from collapsed navigation by keyboard, clears New across reload, and links to workspace destinations', async ({ page }) => {
  await openWorkspace(page)
  const nav = page.getByRole('navigation', { name: 'Workspace sections' })
  await expect(nav.locator('.sidebar-nav-item').last()).toHaveAccessibleName('What’s new (New)')
  await page.getByRole('button', { name: 'Expand navigation sidebar' }).click()
  await expect(nav.locator('.sidebar-nav-item').last()).toHaveAccessibleName('What’s new (New)')
  await expect(nav.getByText('New', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Collapse navigation sidebar' }).click()
  const entry = nav.getByRole('button', { name: navName })
  await expect(entry).toHaveAccessibleName('What’s new (New)')
  await expect(entry.locator('.whats-new-dot')).toBeVisible()
  await entry.press('Enter')
  const heading = page.getByRole('heading', { name: title })
  await expect(heading).toBeFocused()
  await expect(entry).toHaveAttribute('aria-pressed', 'true')

  await expect(page.locator('.whats-new-grid h3')).toHaveText([
    'Code Repositories', 'Code evidence in main search', 'Saved chat history', 'Diagrams in answers',
  ])
  await page.getByRole('button', { name: 'Open Code Repositories', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Code Repositories', exact: true })).toBeVisible()
  await entry.click()
  await page.getByRole('button', { name: 'Go to main search' }).click()
  await expect(page.locator('#incident-input')).toBeVisible()

  await page.reload()
  await expect(entry).toHaveAccessibleName('What’s new')
  await expect(heading).toHaveCount(0)
  await expect(nav.getByText('New', { exact: true })).toHaveCount(0)
  await entry.click()
  await expect(heading).toBeVisible()
})

test('explicitly dismisses the badge without leaving main search and remembers it after reload', async ({ page }) => {
  await openWorkspace(page)
  const entry = page.getByRole('button', { name: navName })
  const dismiss = page.getByRole('button', { name: 'Dismiss What’s new badge' })
  await dismiss.press('Enter')
  await expect(entry).toBeFocused()
  await expect(page.getByRole('heading', { name: title })).toHaveCount(0)
  await expect(page.locator('#incident-input')).toBeVisible()

  await page.reload()
  await expect(entry).toHaveAccessibleName('What’s new')
  await expect(dismiss).toHaveCount(0)
})

for (const theme of ['light', 'dark']) {
  test(`keeps the release icon distinctive after viewing updates in ${theme} mode`, async ({ page }) => {
    await openWorkspace(page)
    await page.evaluate(theme => { document.documentElement.dataset['theme'] = theme }, theme)
    const entry = page.getByRole('button', { name: navName })
    const icon = entry.locator('.whats-new-nav-icon')
    const iconColor = await icon.evaluate(element => getComputedStyle(element).color)
    const ordinaryIcon = page.getByRole('button', { name: 'Open Pool Recommendations', exact: true }).locator('.sidebar-nav-icon')
    expect(iconColor).not.toBe(await ordinaryIcon.evaluate(element => getComputedStyle(element).color))
    await expect(entry.locator('.whats-new-dot')).toBeVisible()

    await entry.click()
    await expect(entry.locator('.whats-new-dot')).toHaveCount(0)
    await expect(icon).toHaveCSS('color', iconColor)
    await page.getByRole('button', { name: 'Go to main search' }).click()
    await expect(icon).toHaveCSS('color', iconColor)
  })
}

for (const width of [1440, 960]) {
  test(`opens chat history at ${width}px instead of toggling it closed`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 })
    await openWorkspace(page)
    const entry = page.getByRole('button', { name: navName })
    const history = page.getByRole('complementary', { name: 'Chat history' })
    const toggle = page.getByRole('button', { name: 'Toggle chat history' })

    await entry.click()
    await page.getByRole('button', { name: 'Open chat history' }).click()
    await expect(history).toBeVisible()
    await expect(toggle).toHaveAttribute('aria-expanded', 'true')
    if (width < 1056) await expect(history).toHaveClass(/chat-history-drawer/)
    else await expect(history).not.toHaveClass(/chat-history-drawer/)

    await (width < 1056 ? page.getByRole('button', { name: 'Close chat history' }) : toggle).click()
    await expect(history).toHaveCount(0)
    await entry.click()
    await page.getByRole('button', { name: 'Open chat history' }).click()
    await expect(history).toBeVisible()
  })
}

test('serves the real poster, video, and English captions, only loading video after opt-in', async ({ page }) => {
  const mediaRequests: string[] = []
  page.on('request', request => {
    if (new URL(request.url()).pathname.includes('/media/poolsense-walkthrough')) mediaRequests.push(request.url())
  })
  await openWorkspace(page)
  await page.getByRole('button', { name: navName }).click()
  const card = page.locator('.whats-new-tour')
  await expect(card).toContainText('illustrative demo data')
  const loadVideo = card.getByRole('button', { name: 'Load walkthrough video' })
  await loadVideo.scrollIntoViewIfNeeded()
  await expect.poll(() => card.locator('img').evaluate((image: HTMLImageElement) => image.naturalWidth)).toBeGreaterThan(0)
  expect(mediaRequests.map(url => new URL(url).pathname)).toEqual(['/media/poolsense-walkthrough-poster.jpg'])
  await expect(card.locator('video')).toHaveCount(0)

  const mp4 = page.waitForResponse(response => response.url().endsWith('/media/poolsense-walkthrough.mp4'))
  const captions = page.waitForResponse(response => response.url().endsWith('/media/poolsense-walkthrough-captions.vtt'))
  await loadVideo.click()
  const [videoResponse, captionResponse] = await Promise.all([mp4, captions])
  expect([200, 206]).toContain(videoResponse.status())
  expect(videoResponse.headers()['content-type']).toMatch(/^video\/mp4\b/)
  expect(captionResponse.ok()).toBe(true)
  expect(captionResponse.headers()['content-type']).toMatch(/^text\/vtt\b/)
  expect(await captionResponse.text()).toMatch(/^WEBVTT/)
  const video = card.locator('video')
  await expect(video).toBeFocused()
  await expect(video).toHaveJSProperty('controls', true)
  await expect(video).toHaveJSProperty('autoplay', false)
  await expect(video).toHaveJSProperty('paused', true)
  await expect.poll(() => video.evaluate((element: HTMLVideoElement) => element.readyState)).toBeGreaterThan(0)
  await expect.poll(() => video.evaluate((element: HTMLVideoElement) => element.textTracks[0].cues?.length ?? 0)).toBeGreaterThan(0)
  await expect(video.locator('track')).toHaveAttribute('srclang', 'en')
})
