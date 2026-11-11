import { CommonModule } from '@angular/common'
import { Component, Input, OnChanges } from '@angular/core'
import type { CodeSearchCoverage } from './api.service'

@Component({
  selector: 'app-code-search-status',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div *ngIf="message || coverage" class="code-search-status" role="status">
      <p *ngIf="message">{{ message }}</p>
      <p *ngIf="coverage">
        Searched {{ coverage.searchedRepositories }} of {{ coverage.authorizedRepositories }} confirmed accessible repositories.
        <span *ngIf="!coverage.authorizationComplete">Access checks did not finish; unverified repositories were not used.</span>
        <span *ngIf="coverage.oldestIndexedAt">Oldest indexed snapshot: {{ coverage.oldestIndexedAt | date: 'medium' }}.</span>
      </p>
      <p *ngIf="coverage && coverage.authorizedRepositories > 0">Semantic: {{ coverage.semanticStatus }} · Project knowledge: {{ coverage.knowledgeStatus }}</p>
      <p *ngIf="coverage && (coverage.semanticTargetChunks ?? 0) > 0">
        Known semantic coverage: {{ coverage.semanticIndexedChunks ?? 0 }} / {{ coverage.semanticTargetChunks }} target vectors
        ({{ coverage.semanticEligibleChunks ?? 0 }} eligible source chunks).
        {{ coverage.semanticTargetReached ? 'Configured targets reached; this is not full-corpus coverage.' : 'Coverage grows in bounded background batches.' }}
      </p>
      <p *ngFor="let warning of coverage?.warnings ?? []">{{ warning }}</p>
    </div>`,
  styles: [`
    :host { display: block; min-width: 0; }
    .code-search-status { margin-block: .6rem; padding: .6rem .8rem; border: 1px solid var(--border); border-radius: 10px; background: var(--panel-subtle); color: var(--muted); font-size: .8rem; overflow-wrap: anywhere; }
    p { margin: .2rem 0; }
  `],
})
export class CodeSearchStatusComponent implements OnChanges {
  @Input() status = ''
  @Input() coverage?: CodeSearchCoverage
  message = ''
  ngOnChanges() {
    const messages: Record<string, string> = {
      partial: 'Code search is incomplete. Collected evidence is retained; coverage is not exhaustive.',
      'timed-out': 'Code search reached its deadline before usable evidence was available.',
      'not-ready': 'Code indexes are not ready. Indexing and refresh run in the background.',
      failed: 'Code search could not finish. This does not mean relevant code is absent.',
      'authorization-unavailable': 'Current code access could not be verified; evidence was withheld.',
      'not-connected': 'GitHub is not connected.',
      disabled: 'Code retrieval is disabled.',
      'not-authenticated': 'Sign in before searching code.',
      'not-scoped': 'No application code scope was selected or resolved.',
      'not-available': 'Code retrieval was skipped for this request.',
      'not-requested': 'Code search was excluded by the current source plan.',
      'no-accessible-repositories': 'No repository source is currently accessible for this scope.',
      'no-accessible-matches': 'No matching source was found in the accessible indexed repositories searched.',
      stale: 'Showing a previous indexed snapshot pending background freshness/refresh.',
    }
    this.message = messages[this.status] || ''
  }
}
