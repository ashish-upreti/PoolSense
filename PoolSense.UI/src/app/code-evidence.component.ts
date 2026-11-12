import { CommonModule } from '@angular/common'
import { Component, Input, OnChanges } from '@angular/core'
import { CodeEvidence, CodeSearchCoverage } from './api.service'
import { CodeSearchStatusComponent } from './code-search-status.component'

@Component({
  selector: 'app-code-evidence', standalone: true, imports: [CommonModule, CodeSearchStatusComponent],
  template: `
    <app-code-search-status [status]="status" [coverage]="coverage"></app-code-search-status>
    <p *ngIf="!documents.length" class="status">{{ status === 'not-connected' ? 'Server-managed GitHub access is unavailable or the PAT was rejected.' : 'No accessible code evidence.' }}</p>
    <section *ngIf="entries.length" aria-label="Code source references">
      <h4>Code source references</h4>
      <p class="status">Indexed source; verify that this commit matches the deployed version.</p>
      <article *ngFor="let entry of entries" class="source-reference">
        <p class="repository">{{ entry.document.repository }}</p>
        <p class="source-link">
          <a *ngIf="entry.url; else unavailable" [href]="entry.url" target="_blank" rel="noopener noreferrer">Open source: {{ entry.document.filePath }}:{{ entry.document.startLine }}-{{ entry.document.endLine }}</a>
          <ng-template #unavailable>
            <span>{{ entry.document.filePath }}:{{ entry.document.startLine }}-{{ entry.document.endLine }}</span>
            <span class="source-unavailable">Source link unavailable (missing, invalid or non-HTTPS URL).</span>
          </ng-template>
        </p>
        <p class="commit">Indexed commit: <code>{{ entry.document.commit || 'Not supplied' }}</code></p>
        <details>
          <summary>View source excerpt<span *ngIf="entry.document.symbol">: {{ entry.document.symbol }}</span></summary>
          <pre><code>{{ entry.document.content }}</code></pre>
        </details>
      </article>
    </section>`,
  styles: [`
    :host { display: block; min-width: 0; }
    section { margin-block: 1rem; }
    h4 { margin: 0 0 .4rem; color: var(--heading-text); font-size: .9rem; }
    .source-reference { border-top: 1px solid var(--border); padding: .8rem 0; font-size: .85rem; overflow-wrap: anywhere; }
    .source-reference p { margin: .25rem 0; }
    .repository, .commit { color: var(--muted); font-size: .78rem; }
    .source-link a { color: var(--accent); text-decoration: underline; text-underline-offset: 3px; }
    a:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }
    .source-unavailable { display: block; color: var(--error-color); }
    details { margin-top: .6rem; }
    summary { cursor: pointer; overflow-wrap: anywhere; }
    a { color: inherit; overflow-wrap: anywhere; }
    pre { max-height: 350px; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; padding: 10px; background: #89929e15; }
    .status { font-size: .8rem; color: var(--muted); }
  `],
})
export class CodeEvidenceComponent implements OnChanges {
  @Input() documents: CodeEvidence[] = []
  @Input() status = ''
  @Input() coverage?: CodeSearchCoverage
  entries: { document: CodeEvidence; url: string | null }[] = []

  ngOnChanges() {
    this.entries = this.documents.map(document => {
      let url: string | null = null
      try {
        const parsed = new URL(document.sourceUrl)
        if (parsed.protocol === 'https:' && !parsed.username && !parsed.password && document.sourceUrl === document.sourceUrl.trim()) url = document.sourceUrl
      } catch {
        // Keep the citation visible and explicitly mark its link unavailable below.
      }
      return { document, url }
    })
  }
}