import { test, expect } from '@playwright/test'

test('pinned/recent long titles stay ellipsized and expose full hover names after load and rename', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  const now = '2026-11-10T12:00:00Z'
  const sessions = [
    { id: 'pinned', title: 'Pinned investigation of the publication workflow and its cross-system validation dependencies', isPinned: true },
    { id: 'recent', title: 'Recent investigation of application mapping and downstream publication notification behavior', isPinned: false },
  ].map(session => ({ ...session, selectedGroupIds: [], createdAt: now, updatedAt: now, messageCount: 2 }))
  await page.route('**/api/**', async route => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    let body: unknown = []
    if (path === '/api/auth/session') body = { success: true, user: { username: 'test', displayName: 'Test operator', isAdmin: true } }
    else if (path === '/api/projects/groups') body = { groups: [] }
    else if (path === '/api/settings/deployment') body = { environmentLabel: 'Tooltip fixture', environmentName: 'Test' }
    else if (path === '/api/settings/ticket-automation') body = { pollingEnabled: false, pollIntervalSeconds: 60, email: { deliveryMode: 'Smtp' } }
    else if (path === '/api/feedback/application/insights') { await route.fulfill({ status: 503, json: { message: 'No fixture usage' } }); return }
    else if (path === '/api/ticket/history') body = sessions
    else if (path.startsWith('/api/ticket/history/') && request.method() === 'PATCH') {
      sessions.find(session => session.id === path.split('/').pop())!.title = request.postDataJSON().title
      await route.fulfill({ status: 204 }); return
    }
    await route.fulfill({ json: body })
  })
  await page.goto('http://localhost:4200')
  const items = page.locator('.chat-history-item')
  await expect(items).toHaveCount(2)
  for (let index = 0; index < sessions.length; index++) {
    const item = items.nth(index)
    const button = item.locator('.chat-history-open')
    const title = item.locator('.chat-history-item-title')
    await button.hover()
    await expect(button).toHaveAttribute('title', sessions[index].title)
    await expect(button).toHaveAccessibleName(sessions[index].title)
    expect(await title.evaluate(element => getComputedStyle(element).textOverflow)).toBe('ellipsis')
    expect(await title.evaluate(element => element.scrollWidth > element.clientWidth)).toBe(true)
    const renamed = `Renamed ${sessions[index].title}`.slice(0, 100)
    await item.getByRole('button', { name: 'Rename conversation', exact: true }).click()
    await item.getByRole('textbox', { name: 'Conversation title' }).fill(renamed)
    await item.getByRole('button', { name: 'Save title', exact: true }).click()
    await expect(button).toHaveAttribute('title', renamed)
    await expect(button).toHaveAccessibleName(renamed)
    await item.hover()
    await expect(item.getByRole('button', { name: 'Rename conversation', exact: true })).toHaveAttribute('title', 'Rename')
    await expect(item).not.toHaveAttribute('title')
  }
})
