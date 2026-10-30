import { test, expect } from '@playwright/test'
import type { CodeRepository } from '../src/app/api.service'

for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
  test(`repository lifecycle at ${viewport.width}px`, async ({ page }, testInfo) => {
    test.setTimeout(60000)
    await page.setViewportSize(viewport)
    let repositories: CodeRepository[] = []
    let connected = false
    let serviceAvailable = false
    let failApplication = false
    let failDeletion = false
    let failedRepositoryUrl = ''
    let nextId = 0
    const mutations: string[] = []
    const errors: string[] = []
    page.on('pageerror', error => errors.push(error.message))
    page.on('console', message => { if (message.type() === 'error' && /ERROR|TypeError|NG0/.test(message.text())) errors.push(message.text()) })
    const project = { id: 1, projectId: 'test-project', projectName: 'Test Project', knowledgeLookbackYears: 2,
      similaritySearchLimit: 5, sendEmail: false, poolingEnabled: false, emailRecipients: '', applicationFilter: 'Test', nyraKbNames: '', createdAt: '' }
    const otherProject = { ...project, id: 2, projectId: 'other-project', projectName: 'Other Project' }
    await page.route('**/api/**', async route => {
      const request = route.request()
      const path = new URL(request.url()).pathname
      const repositoryPath = /^\/api\/projects\/([^/]+)\/repositories(?:\/([^/]+))?(?:\/(refresh|take-ownership))?$/.exec(path)
      let body: unknown = []
      if (path === '/api/auth/session') body = { success: true, user: { username: 'alice', displayName: 'Alice', isAdmin: true, email: 'alice@example.test' } }
      else if (path === '/api/projects/groups') body = [project, otherProject].map(entry => ({ groupId: entry.projectId, displayName: entry.projectName }))
      else if (path === '/api/projects') body = [project, otherProject]
      else if (path === '/api/projects/test-project' || path === '/api/projects/other-project') {
        expect(request.method()).toBe('PUT')
        mutations.push('PUT ' + path)
        if (failApplication) { await route.fulfill({ status: 503, json: { title: 'Application update failed.' } }); return }
        body = Object.assign(path.endsWith('/test-project') ? project : otherProject, request.postDataJSON())
      }
      else if (path === '/api/settings/ticket-automation') body = { pollingEnabled: false, pollIntervalSeconds: 60, poolSenseEmail: false, similaritySearchLimit: 5,
        newStatusName: 'New', closedStatusName: 'Closed', email: { recipient: '', fromAddress: '', deliveryMode: 'Smtp', smtpHost: '', port: 25, timeoutMs: 30000, databaseMailProfile: '' } }
      else if (path === '/api/settings/deployment') body = { environmentLabel: 'Test', environmentName: 'Test', machineName: 'Test', poolSenseDatabaseName: 'Test', ticketSourceDatabaseName: 'Test' }
      else if (path === '/api/github/connect') {
        expect(serviceAvailable).toBe(true)
        expect(request.method()).toBe('POST')
        expect(request.headers()['x-poolsense-csrf']).toBe('1')
        connected = true
        body = { url: 'http://localhost:4200/' }
      }
      else if (path === '/api/github/connection') {
        if (!serviceAvailable) { await route.fulfill({ status: 503, json: { title: 'Code knowledge is not enabled.', status: 503 } }); return }
        if (request.method() === 'DELETE') { connected = false; await route.fulfill({ status: 204 }); return }
        body = { connected, login: 'alice-gh' }
      } else if (repositoryPath) {
        expect(request.headers()['x-poolsense-csrf']).toBe('1')
        const projectId = repositoryPath[1]
        const repositoryId = repositoryPath[2]
        const existing = repositories.find(entry => entry.id === repositoryId && entry.projectId === projectId)
        if (request.method() !== 'GET') mutations.push(request.method() + ' ' + path)
        const payload = request.postDataJSON() as { url?: string; branch?: string; enabled?: boolean } | null
        if (request.method() === 'POST' && path.endsWith('/repositories')) {
          if (payload!.url === failedRepositoryUrl) { await route.fulfill({ status: 503, json: { title: 'Repository save failed.' } }); return }
          const record: CodeRepository = { id: 'repo-' + ++nextId, projectId, url: payload!.url!, branch: payload!.branch || 'master', enabled: payload!.enabled!,
            status: payload!.enabled ? 'queued' : 'disabled', message: '', indexedCommit: '', fileCount: 0, symbolCount: 0, lastCheckedAt: null, lastIndexedAt: null }
          repositories.push(record)
          body = record
        } else if (request.method() === 'POST') {
          expect(existing?.enabled).toBe(true)
          body = Object.assign(existing!, { status: 'queued' })
        } else if (request.method() === 'PUT') {
          body = Object.assign(existing!, payload, { branch: payload!.branch || 'master', status: payload!.enabled ? 'queued' : 'disabled' })
        } else if (request.method() === 'DELETE') {
          if (failDeletion) { await route.fulfill({ status: 503, json: { title: 'Repository deletion failed.' } }); return }
          repositories = repositories.filter(entry => entry !== existing)
          await route.fulfill({ status: 204 }); return
        }
        else body = repositories.filter(entry => entry.projectId === projectId)
      } else if (path.startsWith('/api/feedback/application/insights')) { await route.fulfill({ status: 503, json: { message: 'Not available in this fixture' } }); return }
      await route.fulfill({ json: body })
    })
    await page.goto('http://localhost:4200')
    await page.getByRole('button', { name: 'Open application configuration', exact: true }).click()
    const editProject = (name: string) => page.locator('.application-card').filter({ hasText: name }).getByRole('button', { name: 'Edit', exact: true }).click()
    const component = page.locator('app-code-repositories')
    await expect(component).toHaveCount(0)
    await editProject('Test Project')
    await expect(page.locator('form.application-config-card app-code-repositories')).toHaveCount(1)
    await expect(page.locator('.application-list-card app-code-repositories')).toHaveCount(0)
    await expect(page.locator('form form')).toHaveCount(0)
    await expect(component.getByRole('alert')).toHaveText('Code knowledge is not enabled.')
    await expect(component.getByLabel('Repository URL')).toHaveCount(0)
    await expect(component.getByRole('button', { name: 'Connect GitHub', exact: true })).toBeDisabled()
    await component.screenshot({ path: testInfo.outputPath('repository-unavailable.png') })
    serviceAvailable = true
    await component.getByRole('button', { name: 'Reload GitHub connection', exact: true }).click()
    await expect(component.getByRole('alert')).toHaveCount(0)
    await expect(component.getByRole('button', { name: 'Connect GitHub', exact: true })).toBeEnabled()
    await expect(component.getByRole('button', { name: 'Add repository', exact: true })).toHaveCount(0)
    await component.screenshot({ path: testInfo.outputPath('repository-disconnected.png') })
    await Promise.all([
      page.waitForEvent('load'),
      component.getByRole('button', { name: 'Connect GitHub', exact: true }).click(),
    ])
    await page.getByRole('button', { name: 'Open application configuration', exact: true }).click()
    await editProject('Test Project')
    await expect(component.getByText('alice-gh')).toBeVisible()
    const addRepository = component.getByRole('button', { name: 'Add repository', exact: true })
    const update = page.getByRole('button', { name: 'Update Application', exact: true })
    const rows = component.locator('.repository')
    const firstUrl = 'https://github.com/example/engineering-platform'
    const secondUrl = 'https://github.com/example/shared-library'
    await addRepository.click()
    await expect(rows.nth(0).getByLabel('Branch', { exact: true })).toHaveValue('master')
    await rows.nth(0).getByLabel('Repository URL').fill(firstUrl)
    await rows.nth(0).getByLabel('Branch', { exact: true }).fill('main')
    await addRepository.click()
    await rows.nth(1).getByLabel('Repository URL').fill(secondUrl)
    await rows.nth(1).getByLabel('Branch', { exact: true }).fill('release')
    await expect(component.getByText('Unsaved', { exact: true })).toHaveCount(2)
    await component.getByRole('button', { name: 'Reload GitHub connection', exact: true }).click()
    await expect(rows).toHaveCount(2)
    await expect(rows.nth(0).getByLabel('Repository URL')).toHaveValue(firstUrl)
    expect(repositories).toHaveLength(0)
    expect(mutations).toEqual([])
    for (const control of await component.locator('input, button').all()) {
      const bounds = await control.boundingBox()
      expect(bounds).not.toBeNull()
      expect(bounds!.x).toBeGreaterThanOrEqual(0)
      expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(viewport.width + 1)
    }
    await component.screenshot({ path: testInfo.outputPath('repository-drafts.png') })
    await update.click()
    await expect(component.getByText('queued', { exact: true })).toHaveCount(2)
    expect(mutations).toEqual(['PUT /api/projects/test-project', 'POST /api/projects/test-project/repositories', 'POST /api/projects/test-project/repositories'])
    expect(repositories).toHaveLength(2)
    await expect(page.getByText('Updated Test Project. Queued 2 repositories for indexing.', { exact: true })).toBeVisible()
    await expect(component.getByText('Unsaved repository changes')).toHaveCount(0)
    await component.scrollIntoViewIfNeeded()
    const bounds = await component.boundingBox()
    expect(bounds).not.toBeNull()
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(viewport.width + 1)
    await component.screenshot({ path: testInfo.outputPath('repository-controls.png') })
    await page.screenshot({ path: testInfo.outputPath('application-editor.png'), fullPage: true })

    mutations.length = 0
    await rows.nth(0).getByLabel('Branch', { exact: true }).fill('discarded-branch')
    await rows.nth(1).getByRole('button', { name: 'Remove repository', exact: true }).click()
    await page.getByRole('button', { name: 'Cancel edit', exact: true }).click()
    await expect(component).toHaveCount(0)
    expect(mutations).toEqual([])
    await editProject('Test Project')
    await expect(rows).toHaveCount(2)
    await expect(rows.nth(0).getByLabel('Branch', { exact: true })).toHaveValue('main')

    await rows.nth(0).getByLabel('Branch', { exact: true }).fill('release')
    await rows.nth(1).getByRole('button', { name: 'Remove repository', exact: true }).click()
    failApplication = true
    await update.click()
    await expect(page.getByText('Application update failed.', { exact: true })).toBeVisible()
    expect(mutations).toEqual(['PUT /api/projects/test-project'])
    expect(repositories).toHaveLength(2)
    expect(repositories[0].branch).toBe('main')
    failApplication = false
    mutations.length = 0
    await update.click()
    await expect(component.getByText('Unsaved repository changes')).toHaveCount(0)
    expect(mutations).toEqual(['PUT /api/projects/test-project', 'DELETE /api/projects/test-project/repositories/repo-2', 'PUT /api/projects/test-project/repositories/repo-1'])
    expect(repositories).toHaveLength(1)
    expect(repositories[0].branch).toBe('release')

    const thirdUrl = 'https://github.com/example/service-three'
    const fourthUrl = 'https://github.com/example/service-four'
    for (const url of [thirdUrl, fourthUrl]) {
      await addRepository.click()
      await expect(rows.last().getByLabel('Branch', { exact: true })).toHaveValue('master')
      await rows.last().getByLabel('Repository URL').fill(url)
    }
    await rows.last().getByLabel('Branch', { exact: true }).fill('')
    failedRepositoryUrl = fourthUrl
    mutations.length = 0
    await update.click()
    await expect(page.getByText('Application settings were saved, but repository changes could not be completed. Repository save failed.', { exact: true })).toBeVisible()
    expect(repositories).toHaveLength(2)
    await expect(rows).toHaveCount(3)
    await expect(rows.last().getByLabel('Repository URL')).toHaveValue(fourthUrl)
    failedRepositoryUrl = ''
    mutations.length = 0
    await update.click()
    await expect(component.getByText('Unsaved repository changes')).toHaveCount(0)
    expect(mutations).toEqual(['PUT /api/projects/test-project', 'POST /api/projects/test-project/repositories'])
    expect(repositories).toHaveLength(3)
    expect(repositories.filter(entry => entry.url === thirdUrl)).toHaveLength(1)
    expect(repositories.find(entry => entry.url === fourthUrl)?.branch).toBe('master')

    await rows.nth(1).getByLabel('Enabled', { exact: true }).uncheck()
    await update.click()
    await expect(component.getByText('disabled', { exact: true })).toBeVisible()
    repositories = repositories.map(entry => ({ ...entry, status: entry.enabled ? 'ready' : 'disabled', indexedCommit: 'a'.repeat(40), fileCount: 3 }))
    await component.getByRole('button', { name: 'Reload GitHub connection', exact: true }).click()
    await expect(component.getByText('ready', { exact: true })).toHaveCount(2)
    mutations.length = 0
    await update.click()
    await expect(component.getByText('queued', { exact: true })).toHaveCount(2)
    expect(mutations).toEqual(['PUT /api/projects/test-project', 'POST /api/projects/test-project/repositories/repo-1/refresh', 'POST /api/projects/test-project/repositories/repo-4/refresh'])

    await editProject('Other Project')
    await expect(rows).toHaveCount(0)
    await addRepository.click()
    await rows.last().getByLabel('Repository URL').fill(firstUrl)
    mutations.length = 0
    await update.click()
    await expect(component.getByText('queued', { exact: true })).toHaveCount(1)
    expect(mutations).toEqual(['PUT /api/projects/other-project', 'POST /api/projects/other-project/repositories'])
    expect(repositories.filter(entry => entry.projectId === 'other-project')).toHaveLength(1)

    const removedId = repositories.find(entry => entry.projectId === 'other-project')!.id
    mutations.length = 0
    await rows.last().getByRole('button', { name: 'Remove repository', exact: true }).click()
    await component.getByRole('button', { name: 'Reload GitHub connection', exact: true }).click()
    await expect(rows).toHaveCount(0)
    await expect(component.getByText('Unsaved repository changes')).toBeVisible()
    expect(repositories.filter(entry => entry.projectId === 'other-project')).toHaveLength(1)
    expect(mutations).toEqual([])
    failDeletion = true
    await update.click()
    await expect(page.getByText('Application settings were saved, but repository changes could not be completed. Repository deletion failed.', { exact: true })).toBeVisible()
    expect(repositories.filter(entry => entry.projectId === 'other-project')).toHaveLength(1)
    await expect(component.getByText('Unsaved repository changes')).toBeVisible()
    failDeletion = false
    mutations.length = 0
    await update.click()
    await expect(component.getByText('Unsaved repository changes')).toHaveCount(0)
    expect(mutations).toEqual(['PUT /api/projects/other-project', `DELETE /api/projects/other-project/repositories/${removedId}`])
    expect(repositories.filter(entry => entry.projectId === 'other-project')).toHaveLength(0)
    await component.getByRole('button', { name: 'Reload GitHub connection', exact: true }).click()
    await expect(component.getByText('No repositories configured.', { exact: true })).toBeVisible()
    await expect(rows).toHaveCount(0)
    await editProject('Test Project')
    await expect(rows).toHaveCount(3)

    mutations.length = 0
    await addRepository.click()
    await update.click()
    await expect(page.getByText('Each repository needs a valid GitHub URL.', { exact: true })).toBeVisible()
    await rows.last().getByLabel('Repository URL').fill(firstUrl)
    await rows.last().getByLabel('Branch', { exact: true }).fill('release')
    await update.click()
    await expect(page.getByText('Each repository and branch can only be added once.', { exact: true })).toBeVisible()
    expect(mutations).toEqual([])
    await rows.last().getByRole('button', { name: 'Remove repository', exact: true }).click()
    await component.getByRole('button', { name: 'Disconnect', exact: true }).click()
    await expect(component.getByRole('button', { name: 'Connect GitHub', exact: true })).toBeVisible()
    await expect(component.getByLabel('Repository URL')).toHaveCount(0)
    await expect(addRepository).toHaveCount(0)
    expect(errors).toEqual([])
  })
}