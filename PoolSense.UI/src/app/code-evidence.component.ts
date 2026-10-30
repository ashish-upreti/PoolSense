import { CommonModule } from '@angular/common'
import { Component, Input } from '@angular/core'
import { CodeEvidence } from './api.service'

@Component({
  selector: 'app-code-evidence', standalone: true, imports: [CommonModule],
  template: `
    <p *ngIf="status === 'stale'" class="status">Code index is awaiting an update.</p>
    <p *ngIf="!documents.length" class="status">{{ status === 'not-connected' ? 'GitHub is not connected.' : 'No accessible code evidence.' }}</p>
    <details *ngFor="let document of documents">
      <summary>{{ document.repository }} / {{ document.filePath }}:{{ document.startLine }}-{{ document.endLine }}</summary>
      <p><a [href]="document.sourceUrl" target="_blank" rel="noopener noreferrer">{{ document.symbol }} / {{ document.commit.slice(0, 10) }}</a></p>
      <pre><code>{{ document.content }}</code></pre>
    </details>`,
  styles: [`
    :host { display: block; min-width: 0; }
    details { border-top: 1px solid #89929e55; padding: 12px 0; font-size: 12px; }
    summary { cursor: pointer; overflow-wrap: anywhere; }
    a { color: inherit; overflow-wrap: anywhere; }
    pre { max-height: 350px; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; padding: 10px; background: #89929e15; }
    .status { font-size: 12px; opacity: .8; }
  `],
})
export class CodeEvidenceComponent {
  @Input() documents: CodeEvidence[] = []
  @Input() status = ''
}