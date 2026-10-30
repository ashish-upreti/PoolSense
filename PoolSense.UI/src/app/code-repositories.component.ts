import { CommonModule } from '@angular/common'
import { ChangeDetectorRef, Component, ElementRef, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges, inject } from '@angular/core'
import { FormsModule } from '@angular/forms'
import { createElement, GitBranch, Plus, RefreshCw, Pencil, Trash2, UserCheck, X } from 'lucide'
import { ApiService, CodeRepository } from './api.service'

const repositoryIcons = { branch: GitBranch, add: Plus, refresh: RefreshCw, edit: Pencil, remove: Trash2, owner: UserCheck, cancel: X }

type RepositoryDraft = {
  key: string
  url: string
  branch: string
  enabled: boolean
  saved: CodeRepository | null
}

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
          <span *ngIf="connected" class="login">{{ login }}</span>
          <button *ngIf="!connected" type="button" (click)="connect()" [disabled]="disabled || !connectionAvailable"><app-repo-icon name="branch"></app-repo-icon>Connect GitHub</button>
          <button *ngIf="connected" type="button" (click)="disconnect()" [disabled]="disabled">Disconnect</button>
          <button type="button" title="Reload GitHub connection" aria-label="Reload GitHub connection" (click)="load()" [disabled]="disabled"><app-repo-icon name="refresh"></app-repo-icon></button>
        </div>
      </header>
      <p *ngIf="error" role="alert" class="error">{{ error }}</p>
      <ng-container *ngIf="projectId && connected">
        <p *ngIf="!loading && !repositories.length">No repositories configured.</p>
        <article *ngFor="let repo of repositories; trackBy: trackRepository" class="repository">
          <div class="repo-fields">
            <label class="repository-url">Repository URL<input type="url" required maxlength="300" [(ngModel)]="repo.url" [ngModelOptions]="{ standalone: true }" placeholder="https://github.com/owner/repository" [disabled]="disabled" /></label>
            <label>Branch<input maxlength="255" [(ngModel)]="repo.branch" [ngModelOptions]="{ standalone: true }" placeholder="master" [disabled]="disabled" /></label>
            <label class="enabled"><input type="checkbox" [(ngModel)]="repo.enabled" [ngModelOptions]="{ standalone: true }" [disabled]="disabled" /> Enabled</label>
            <button type="button" title="Remove repository" aria-label="Remove repository" (click)="remove(repo)" [disabled]="disabled"><app-repo-icon name="remove"></app-repo-icon></button>
          </div>
          <div class="repo-status"><strong>{{ isChanged(repo) ? 'Unsaved' : repo.saved?.status }}</strong><ng-container *ngIf="repo.saved as saved"><code>{{ saved.indexedCommit.slice(0, 10) || 'Not indexed' }}</code><span>{{ saved.fileCount }} files</span></ng-container></div>
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
    .repo-fields { display: grid; grid-template-columns: minmax(0, 1fr) auto auto; gap: 12px; align-items: end; }
    .repository-url { grid-column: 1 / -1; }
    .login { max-width: 160px; overflow-wrap: anywhere; font-size: 13px; }
    label { display: grid; gap: 6px; font-size: 13px; min-width: 0; }
    input:not([type=checkbox]) { box-sizing: border-box; width: 100%; min-width: 0; padding: 9px; border: 1px solid #89929e88; border-radius: 4px; color: inherit; background: transparent; }
    label.enabled { display: flex; align-items: center; min-height: 36px; }
    .repository { border-top: 1px solid #89929e44; margin-top: 16px; padding: 16px 0; display: grid; gap: 10px; }
    .repo-status, .list-footer span { font-size: 12px; overflow-wrap: anywhere; }
    .list-footer { margin-top: 16px; }
    .error { color: #ba493e; overflow-wrap: anywhere; }
    p { font-size: 13px; margin: 8px 0; }
    @container(max-width: 360px) { .repo-fields { grid-template-columns: minmax(0, 1fr) auto; } .repo-fields > label:not(.enabled) { grid-column: 1 / -1; } }
  `],
})
export class CodeRepositoriesComponent implements OnChanges, OnDestroy {
  @Input() projectId = ''
  @Input() saving = false
  @Output() disconnected = new EventEmitter<void>()
  private readonly api = inject(ApiService)
  private readonly changes = inject(ChangeDetectorRef)
  connectionAvailable = false
  connected = false
  login = ''
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
    if (changes['projectId']) { this.repositories = []; this.removed = []; this.connected = false; void this.load() }
  }
  ngOnDestroy() { this.destroyed = true; this.version++; window.clearInterval(this.timer) }
  private path(id = '') { return '/projects/' + encodeURIComponent(this.projectId) + '/repositories' + (id ? '/' + encodeURIComponent(id) : '') }
  trackRepository(_index: number, repo: RepositoryDraft) { return repo.key }
  isChanged(repo: RepositoryDraft) { return !repo.saved || repo.url.trim() !== repo.saved.url || repo.branch.trim() !== repo.saved.branch || repo.enabled !== repo.saved.enabled }
  private draft(saved: CodeRepository): RepositoryDraft { return { key: saved.id, url: saved.url, branch: saved.branch, enabled: saved.enabled, saved } }
  async load() {
    const version = ++this.version
    let connectionLoaded = false
    this.loading = true
    try {
      const connection = await this.api.codeRequest<{ connected: boolean; login: string }>('/github/connection')
      if (version !== this.version || this.destroyed) return
      connectionLoaded = true
      this.connectionAvailable = true
      this.connected = connection.connected
      this.login = connection.login
      const records = this.projectId && this.connected ? await this.api.codeRequest<CodeRepository[]>(this.path()) : []
      if (version !== this.version || this.destroyed) return
      if (!this.hasChanges) this.repositories = records.map(record => this.draft(record))
      this.error = ''
    } catch (error) {
      if (version === this.version && !this.destroyed) {
        if (!connectionLoaded) { this.connectionAvailable = false; this.connected = false; this.login = '' }
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
  async connect() { if (!this.connectionAvailable || this.disabled) return; await this.run(async () => { const result = await this.api.codeRequest<{ url: string }>('/github/connect', 'POST', {}); window.location.assign(result.url) }) }
  async disconnect() {
    if (this.disabled || (this.hasChanges && !window.confirm('Discard unsaved repository changes and disconnect GitHub?'))) return
    await this.run(async () => { await this.api.codeRequest('/github/connection', 'DELETE'); this.repositories = []; this.removed = []; this.connected = false; this.disconnected.emit() })
  }
  addRepository() {
    if (this.disabled || !this.connected) return
    this.repositories = [...this.repositories, { key: crypto.randomUUID(), url: '', branch: 'master', enabled: true, saved: null }]
  }
  remove(repo: RepositoryDraft) {
    if (this.disabled) return
    if (repo.saved) this.removed.push(repo.saved)
    this.repositories = this.repositories.filter(entry => entry !== repo)
  }
  validate() {
    if (this.busy || this.loading) throw new Error('Wait for repository status to finish loading.')
    if (this.hasChanges && !this.connected) throw new Error('Connect GitHub before saving repository changes.')
    const entries = new Set<string>()
    for (const repo of this.repositories) {
      let url: URL
      try { url = new URL(repo.url.trim()) } catch { throw new Error('Each repository needs a valid GitHub URL.') }
      const segments = url.pathname.split('/').filter(Boolean)
      if (url.protocol !== 'https:' || url.host !== 'github.com' || url.username || url.password || url.search || url.hash || segments.length !== 2)
        throw new Error('Use a repository URL in the form https://github.com/owner/repository.')
      const key = segments.join('/').replace(/\.git$/i, '').toLowerCase() + ':' + (repo.branch.trim() || 'master')
      if (entries.has(key)) throw new Error('Each repository and branch can only be added once.')
      entries.add(key)
    }
  }
  async saveChanges(): Promise<number> {
    if (!this.connected) return 0
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