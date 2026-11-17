import { CommonModule } from '@angular/common'
import { ChangeDetectorRef, Component, ElementRef, Input, OnChanges, OnDestroy, SimpleChanges, inject } from '@angular/core'
import { FormsModule } from '@angular/forms'
import { createElement, GitBranch, Plus, RefreshCw, Pencil, Trash2 } from 'lucide'
import { ApiService, CodeRepository } from './api.service'

const repositoryIcons = { branch: GitBranch, add: Plus, refresh: RefreshCw, edit: Pencil, remove: Trash2 }

type RepositoryDraft = {
  key: string
  url: string
  branch: string
  enabled: boolean
  saved: CodeRepository | null
  branches: string[]
  branchesUrl: string
  branchesTruncated: boolean
  loadingBranches: boolean
}

type GitHubBranches = { fullName: string; defaultBranch: string; branches: string[]; truncated: boolean }

@Component({ selector: 'app-repo-icon', standalone: true, template: '', styles: [':host { display: inline-flex; width: 18px; height: 18px; flex: 0 0 18px; }'] })
export class RepositoryIconComponent implements OnChanges {
  @Input() name: keyof typeof repositoryIcons = 'branch'
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef)
  ngOnChanges() { this.host.nativeElement.replaceChildren(createElement(repositoryIcons[this.name], { width: '18', height: '18', 'aria-hidden': 'true' })) }
}

@Component({
  selector: 'app-code-repositories', standalone: true,
  imports: [CommonModule, FormsModule, RepositoryIconComponent],
  template: `
    <section class="repositories" aria-label="GitHub repositories">
      <header>
        <h3><app-repo-icon name="branch"></app-repo-icon> Git repositories</h3>
        <div class="actions">
          <span *ngIf="accessAvailable" class="login" title="GitHub access is managed by the administrator">Managed access · {{ login }}</span>
          <span *ngIf="accessConfigured && !accessAvailable" class="login">Managed access unavailable</span>
          <span *ngIf="!accessConfigured" class="login">Managed access not configured</span>
          <button type="button" title="Reload managed GitHub access" aria-label="Reload managed GitHub access" (click)="load()" [disabled]="disabled"><app-repo-icon name="refresh"></app-repo-icon></button>
        </div>
      </header>
      <p *ngIf="error" role="alert" class="error">{{ error }}</p>
      <p *ngIf="accessMessage" role="status" class="warning">{{ accessMessage }}</p>
      <ng-container *ngIf="projectId && accessAvailable">
        <p *ngIf="!loading && !repositories.length">No repositories configured.</p>
        <article *ngFor="let repo of repositories; trackBy: trackRepository" class="repository">
          <div class="repo-fields">
            <label class="repository-url">Repository URL<input type="url" required maxlength="300" [ngModel]="repo.url" (ngModelChange)="setUrl(repo, $event)" [ngModelOptions]="{ standalone: true }" placeholder="https://github.com/owner/repository" [disabled]="disabled" /></label>
            <label *ngIf="!hasBranches(repo)">Branch<input maxlength="255" [(ngModel)]="repo.branch" [ngModelOptions]="{ standalone: true }" placeholder="master" [disabled]="disabled || repo.loadingBranches" /></label>
            <label *ngIf="hasBranches(repo)">Branch<select [(ngModel)]="repo.branch" [ngModelOptions]="{ standalone: true }" [disabled]="disabled"><option *ngFor="let branch of repo.branches" [value]="branch">{{ branch }}</option></select></label>
            <button type="button" class="branches" (click)="loadBranches(repo)" [disabled]="disabled || repo.loadingBranches || !repo.url.trim()" [attr.aria-label]="(hasBranches(repo) ? 'Reload branches for ' : 'Load branches for ') + (repo.url || 'repository')"><app-repo-icon name="branch"></app-repo-icon>{{ repo.loadingBranches ? 'Loading...' : hasBranches(repo) ? 'Reload branches' : 'Load branches' }}</button>
            <label class="enabled"><input type="checkbox" [(ngModel)]="repo.enabled" [ngModelOptions]="{ standalone: true }" [disabled]="disabled" /> Enabled</label>
            <button type="button" title="Remove repository" aria-label="Remove repository" (click)="remove(repo)" [disabled]="disabled"><app-repo-icon name="remove"></app-repo-icon></button>
          </div>
          <p *ngIf="hasBranches(repo)" class="hint">{{ repo.branches.length }} branch{{ repo.branches.length === 1 ? '' : 'es' }} found{{ repo.branchesTruncated ? ' (first 500 shown)' : '' }}; the default branch was preselected.</p>
          <div class="repo-status"><strong>{{ isChanged(repo) ? 'Unsaved' : repo.saved?.status }}</strong><ng-container *ngIf="repo.saved as saved"><span *ngIf="!isChanged(repo)">{{ statusLabel(saved) }}</span><code>{{ saved.branch }}&#64;{{ saved.indexedCommit.slice(0, 10) || 'not indexed' }}</code><span>{{ saved.fileCount }} files · {{ saved.symbolCount }} symbols</span></ng-container></div>
          <p *ngIf="repo.saved?.message as message" class="error">{{ message }}</p>
        </article>
        <div class="list-footer">
          <button type="button" (click)="addRepository()" [disabled]="disabled"><app-repo-icon name="add"></app-repo-icon>Add repository</button>
          <span *ngIf="hasChanges" role="status">Unsaved repository changes</span>
        </div>
      </ng-container>
    </section>`,
  styles: [`
    :host { display: block; min-width: 0; grid-column: 1 / -1; container-type: inline-size; }
    .repositories { padding: 18px 0; margin-top: 16px; border-block: 1px solid #89929e55; color: inherit; }
    header, .actions, .list-footer, .repo-status { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    header, .list-footer { justify-content: space-between; }
    h3 { display: flex; gap: 10px; align-items: center; font-size: 15px; margin: 0; }
    svg { width: 18px; height: 18px; flex: 0 0 18px; }
    button { display: inline-flex; gap: 6px; align-items: center; justify-content: center; min-height: 36px; min-width: 36px; border: 1px solid #89929e88; border-radius: 4px; padding: 6px 10px; color: inherit; background: transparent; cursor: pointer; }
    button:disabled { opacity: .5; cursor: default; }
    button:hover:not(:disabled) { background: #89929e22; }
    button:focus-visible, input:focus-visible { outline: 2px solid #168782; outline-offset: 2px; }
    .repo-fields { display: grid; grid-template-columns: minmax(0, 1fr) auto auto auto; gap: 12px; align-items: end; }
    .repository-url { grid-column: 1 / -1; }
    .login { max-width: 160px; overflow-wrap: anywhere; font-size: 13px; }
    label { display: grid; gap: 6px; font-size: 13px; min-width: 0; }
    input:not([type=checkbox]), select { box-sizing: border-box; width: 100%; min-width: 0; padding: 9px; border: 1px solid #89929e88; border-radius: 4px; color: inherit; background: transparent; }
    select option { color: initial; }
    label.enabled { display: flex; align-items: center; min-height: 36px; }
    .repository { border-top: 1px solid #89929e44; margin-top: 16px; padding: 16px 0; display: grid; gap: 10px; }
    .repo-status, .list-footer span, .hint { font-size: 12px; overflow-wrap: anywhere; }
    .hint { margin: 0; opacity: .8; }
    .list-footer { margin-top: 16px; }
    .error { color: #ba493e; overflow-wrap: anywhere; }
    .warning { color: #946200; overflow-wrap: anywhere; }
    a { color: inherit; }
    p { font-size: 13px; margin: 8px 0; }
    @container(max-width: 480px) { .repo-fields { grid-template-columns: minmax(0, 1fr) auto; } .repo-fields > label:not(.enabled), .repo-fields > .branches { grid-column: 1 / -1; } }
  `],
})
export class CodeRepositoriesComponent implements OnChanges, OnDestroy {
  @Input() projectId = ''
  @Input() saving = false
  private readonly api = inject(ApiService)
  private readonly changes = inject(ChangeDetectorRef)
  accessConfigured = false
  accessAvailable = false
  login = ''
  accessMessage = ''
  repositories: RepositoryDraft[] = []
  error = ''
  busy = false
  loading = false
  private removed: CodeRepository[] = []
  private version = 0
  private destroyed = false
  private readonly timer = window.setInterval(() => { if (!this.disabled && !this.hasChanges && !document.hidden) void this.load() }, 30000)

  get disabled() { return this.busy || this.loading || this.saving }
  get hasChanges() { return this.removed.length > 0 || this.repositories.some(repo => this.isChanged(repo)) }
  ngOnChanges(changes: SimpleChanges) {
    if (changes['projectId']) { this.repositories = []; this.removed = []; this.accessAvailable = false; void this.load() }
  }
  ngOnDestroy() { this.destroyed = true; this.version++; window.clearInterval(this.timer) }
  private path(id = '') { return '/projects/' + encodeURIComponent(this.projectId) + '/repositories' + (id ? '/' + encodeURIComponent(id) : '') }
  trackRepository(_index: number, repo: RepositoryDraft) { return repo.key }
  isChanged(repo: RepositoryDraft) { return !repo.saved || repo.url.trim() !== repo.saved.url || repo.branch.trim() !== repo.saved.branch || repo.enabled !== repo.saved.enabled }
  hasBranches(repo: RepositoryDraft) { return repo.branches.length > 0 && repo.branchesUrl === repo.url.trim() }
  statusLabel(saved: CodeRepository) {
    switch (saved.status) {
      case 'queued': return 'Queued for indexing'
      case 'indexing': return 'Indexing...'
      case 'ready': return 'Index ready'
      case 'error': return 'Indexing failed'
      case 'access-required': return 'GitHub access required'
      case 'disabled': return 'Disabled'
      default: return ''
    }
  }
  private draft(saved: CodeRepository): RepositoryDraft { return { key: saved.id, url: saved.url, branch: saved.branch, enabled: saved.enabled, saved, branches: [], branchesUrl: '', branchesTruncated: false, loadingBranches: false } }
  setUrl(repo: RepositoryDraft, value: string) {
    repo.url = value
    if (repo.branchesUrl && repo.branchesUrl !== value.trim()) { repo.branches = []; repo.branchesUrl = ''; repo.branchesTruncated = false }
  }
  private parseRepositoryUrl(value: string): URL | null {
    let url: URL
    try { url = new URL(value.trim()) } catch { return null }
    const segments = url.pathname.split('/').filter(Boolean)
    if (url.protocol !== 'https:' || url.host !== 'github.com' || url.username || url.password || url.search || url.hash || segments.length !== 2) return null
    return url
  }
  async loadBranches(repo: RepositoryDraft) {
    const url = repo.url.trim()
    if (this.disabled || repo.loadingBranches || !url) return
    if (!this.parseRepositoryUrl(url)) { this.error = 'Use a repository URL in the form https://github.com/owner/repository.'; return }
    repo.loadingBranches = true
    this.error = ''
    try {
      const result = await this.api.codeRequest<GitHubBranches>(this.path() + '/branches?url=' + encodeURIComponent(url))
      if (this.destroyed) return
      repo.branches = result.branches
      repo.branchesUrl = url
      repo.branchesTruncated = result.truncated
      repo.branch = result.branches.includes(repo.branch.trim()) ? repo.branch.trim() : result.defaultBranch
      if (result.branches.length === 0) this.error = 'This repository has no branches yet.'
    } catch (error) {
      repo.branches = []; repo.branchesUrl = ''; repo.branchesTruncated = false
      this.error = error instanceof Error ? error.message : 'Unable to load branches for this repository.'
    } finally { repo.loadingBranches = false; if (!this.destroyed) this.changes.markForCheck() }
  }
  async load() {
    const version = ++this.version
    let connectionLoaded = false
    this.loading = true
    try {
      const connection = await this.api.codeRequest<{ configured: boolean; available: boolean; login: string | null; message: string }>('/github/access')
      if (version !== this.version || this.destroyed) return
      connectionLoaded = true
      this.accessConfigured = connection.configured
      this.accessAvailable = connection.available
      this.login = connection.login ?? ''
      this.accessMessage = connection.message
      const records = this.projectId && this.accessAvailable ? await this.api.codeRequest<CodeRepository[]>(this.path()) : []
      if (version !== this.version || this.destroyed) return
      if (!this.hasChanges) this.repositories = records.map(record => this.draft(record))
      this.error = ''
    } catch (error) {
      if (version === this.version && !this.destroyed) {
        if (!connectionLoaded) { this.accessConfigured = false; this.accessAvailable = false; this.login = ''; this.accessMessage = '' }
        this.error = error instanceof Error ? error.message : 'Code knowledge is unavailable.'
      }
    } finally { if (version === this.version) this.loading = false; if (!this.destroyed) this.changes.markForCheck() }
  }
  private async run(action: () => Promise<unknown>) {
    this.busy = true
    this.error = ''
    try { await action(); if (!this.destroyed) await this.load() }
    catch (error) { this.error = error instanceof Error ? error.message : 'Repository operation failed.' }
    finally { this.busy = false; if (!this.destroyed) this.changes.markForCheck() }
  }
  addRepository() {
    if (this.disabled || !this.accessAvailable) return
    this.repositories = [...this.repositories, { key: crypto.randomUUID(), url: '', branch: 'master', enabled: true, saved: null, branches: [], branchesUrl: '', branchesTruncated: false, loadingBranches: false }]
  }
  remove(repo: RepositoryDraft) {
    if (this.disabled) return
    if (repo.saved) this.removed.push(repo.saved)
    this.repositories = this.repositories.filter(entry => entry !== repo)
  }
  validate() {
    if (this.busy || this.loading) throw new Error('Wait for repository status to finish loading.')
    if (this.hasChanges && !this.accessAvailable) throw new Error('Server-managed GitHub access must be available before saving repository changes.')
    const entries = new Set<string>()
    for (const repo of this.repositories) {
      let parsed: URL
      try { parsed = new URL(repo.url.trim()) } catch { throw new Error('Each repository needs a valid GitHub URL.') }
      const url = this.parseRepositoryUrl(parsed.href)
      if (!url) throw new Error('Use a repository URL in the form https://github.com/owner/repository.')
      const segments = url.pathname.split('/').filter(Boolean)
      const key = segments.join('/').replace(/\.git$/i, '').toLowerCase() + ':' + (repo.branch.trim() || 'master')
      if (entries.has(key)) throw new Error('Each repository and branch can only be added once.')
      entries.add(key)
    }
  }
  async saveChanges(): Promise<number> {
    if (!this.accessAvailable) return 0
    this.busy = true
    this.version++
    let queued = 0
    const path = this.path()
    try {
      for (const repo of [...this.removed]) {
        await this.api.codeRequest(path + '/' + encodeURIComponent(repo.id), 'DELETE')
        this.removed = this.removed.filter(entry => entry.id !== repo.id)
      }
      for (const repo of this.repositories) {
        let saved = repo.saved
        if (this.isChanged(repo)) {
          saved = await this.api.codeRequest<CodeRepository>(path + (saved ? '/' + encodeURIComponent(saved.id) : ''), saved ? 'PUT' : 'POST', { url: repo.url.trim(), branch: repo.branch.trim() || 'master', enabled: repo.enabled })
          if (saved.enabled) queued++
        } else if (saved?.enabled && saved.status !== 'indexing' && saved.status !== 'queued') {
          saved = await this.api.codeRequest<CodeRepository>(path + '/' + encodeURIComponent(saved.id) + '/refresh', 'POST', {})
          queued++
        }
        if (saved) { repo.saved = saved; repo.url = saved.url; repo.branch = saved.branch; repo.enabled = saved.enabled }
      }
      this.error = ''
      return queued
    } catch (error) {
      this.error = error instanceof Error ? error.message : 'Unable to save repository changes.'
      throw error
    } finally { this.busy = false; if (!this.destroyed) this.changes.markForCheck() }
  }
}