import { test, expect, type Page } from '@playwright/test'

async function setup(page: Page) {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    let body: unknown = []
    if (path === '/api/auth/session') body = { success: true, user: { username: 'test', displayName: 'Console operator', isAdmin: true } }
    else if (path === '/api/projects/groups') body = { groups: [] }
    else if (path === '/api/settings/deployment') body = { environmentLabel: 'Scroll fixture', environmentName: 'Test' }
    else if (path === '/api/settings/ticket-automation') body = { pollingEnabled: false, pollIntervalSeconds: 60, email: { deliveryMode: 'Smtp' } }
    else if (path === '/api/feedback/application/insights') { await route.fulfill({ status: 503, json: { message: 'Unavailable in fixture' } }); return }
    await route.fulfill({ json: body })
  })
  return errors
}

for (const height of [600, 420]) {
  for (const theme of ['light', 'dark']) {
    test(`console scrolls independently at 1280x${height} in ${theme}`, async ({ page }) => {
      await page.setViewportSize({ width: 1280, height })
      const errors = await setup(page)
      await page.goto('http://localhost:4200')
      await page.evaluate(theme => { document.documentElement.dataset['theme'] = theme }, theme)
      await page.getByRole('button', { name: 'Expand navigation sidebar' }).click()
      const sidebar = page.locator('.sidebar-panel')
      const nav = sidebar.getByRole('navigation', { name: 'Workspace sections' })
      const scroller = height > 520 ? nav : sidebar
      expect(await scroller.evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true)
      const before = await page.evaluate(() => ({
        page: document.scrollingElement!.scrollTop,
        main: document.querySelector('.workspace-chat')!.scrollTop,
        context: document.querySelector('.insight-panel')!.scrollTop,
      }))
      const bounds = (await scroller.boundingBox())!
      await page.mouse.move(bounds.x + bounds.width / 2, Math.min(height - 35, bounds.y + bounds.height / 2))
      await page.mouse.wheel(0, 1200)
      await expect.poll(() => scroller.evaluate(element => element.scrollTop)).toBeGreaterThan(0)
      await scroller.evaluate(element => { element.scrollTop = element.scrollHeight })
      await expect(sidebar.getByRole('button', { name: 'Open application feedback', exact: true })).toBeVisible()
      await expect(sidebar.getByRole('button', { name: 'Sign Out', exact: true })).toBeVisible()
      for (const name of ['Open application feedback', 'Sign Out']) {
        await sidebar.getByRole('button', { name, exact: true }).focus()
        const button = (await sidebar.getByRole('button', { name, exact: true }).boundingBox())!
        expect(button.y).toBeGreaterThanOrEqual(0)
        expect(button.y + button.height).toBeLessThanOrEqual(height)
      }
      const after = await page.evaluate(() => ({
        page: document.scrollingElement!.scrollTop,
        main: document.querySelector('.workspace-chat')!.scrollTop,
        context: document.querySelector('.insight-panel')!.scrollTop,
      }))
      expect(after).toEqual(before)
      if (height > 520) {
        await expect(sidebar.getByRole('button', { name: 'Collapse navigation sidebar' })).toBeVisible()
        const header = (await sidebar.locator('.sidebar-brand').boundingBox())!
        expect(header.y).toBeGreaterThanOrEqual(0)
      }
      await scroller.evaluate(element => { element.scrollTop = 0 })
      await page.getByRole('button', { name: 'Collapse navigation sidebar' }).click()
      await nav.getByRole('button', { name: 'Open application feedback' }).focus()
      const feedback = (await nav.getByRole('button', { name: 'Open application feedback' }).boundingBox())!
      expect(feedback.y + feedback.height).toBeLessThanOrEqual(height)
      expect(await nav.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true)
      expect(errors).toEqual([])
    })
  }
}

test('normal-height desktop retains usable header/footer and menu keyboard scrolling', async ({ page }) => {
  await page.setViewportSize({ width: 1680, height: 1100 })
  const errors = await setup(page)
  await page.goto('http://localhost:4200')
  await page.getByRole('button', { name: 'Expand navigation sidebar' }).click()
  const sidebar = page.locator('.sidebar-panel')
  const nav = sidebar.getByRole('navigation', { name: 'Workspace sections' })
  await nav.focus()
  await page.keyboard.press('End')
  await sidebar.getByRole('button', { name: 'Open application feedback' }).focus()
  const feedback = (await sidebar.getByRole('button', { name: 'Open application feedback' }).boundingBox())!
  expect(feedback.y + feedback.height).toBeLessThanOrEqual(1100)
  await expect(sidebar.getByRole('button', { name: 'Collapse navigation sidebar' })).toBeVisible()
  await expect(sidebar.getByRole('button', { name: 'Sign Out', exact: true })).toBeVisible()
  expect(errors).toEqual([])
})

test('narrow console stays in document flow without horizontally clipped actions', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const errors = await setup(page)
  await page.goto('http://localhost:4200')
  await page.getByRole('button', { name: 'Expand navigation sidebar' }).click()
  const sidebar = page.locator('.sidebar-panel')
  expect(await sidebar.evaluate(element => getComputedStyle(element).position)).toBe('static')
  await sidebar.getByRole('button', { name: 'Open application feedback' }).scrollIntoViewIfNeeded()
  await sidebar.getByRole('button', { name: 'Sign Out', exact: true }).scrollIntoViewIfNeeded()
  const bounds = (await sidebar.boundingBox())!
  expect(bounds.x + bounds.width).toBeLessThanOrEqual(391)
  expect(await sidebar.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true)
  await page.getByRole('button', { name: 'Collapse navigation sidebar' }).click()
  await expect(sidebar.getByRole('button', { name: 'Open application feedback' })).toBeVisible()
  expect(errors).toEqual([])
})
