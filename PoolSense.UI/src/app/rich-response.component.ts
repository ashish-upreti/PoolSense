import { CommonModule } from '@angular/common'
import { AfterViewInit, Component, ElementRef, Input, OnChanges, OnDestroy, ViewChild, inject } from '@angular/core'
import { DomSanitizer, type SafeHtml } from '@angular/platform-browser'
import type { Chart, ChartConfiguration } from 'chart.js'
import { type ChartSpec, type RichBlock, prepareMermaid, richBlocks, sanitizeDiagram, sanitizeMarkdown, validateChart } from './rich-response'

let diagramId = 0
let diagramQueue: Promise<void> = Promise.resolve()
const colors = ['#6366f1', '#0891b2', '#d97706', '#db2777', '#059669', '#8b5cf6']
let mermaidModule: Promise<typeof import('mermaid')> | undefined
let chartModule: Promise<typeof import('chart.js')> | undefined

@Component({
  selector: 'app-rich-block',
  standalone: true,
  imports: [CommonModule],
  styles: [':host { display: block; min-width: 0; }'],
  template: `
    <dialog #frame open class="rich-block" (cancel)="closeExpanded($event)" [attr.aria-label]="block.language || block.kind">
      <header class="rich-toolbar">
        <strong>{{ chartSpec?.title || block.language || block.kind }}</strong>
        <div>
          <button *ngIf="block.kind !== 'code'" type="button" (click)="showSource = !showSource" [attr.aria-expanded]="showSource">Source</button>
          <button type="button" (click)="copySource()">Copy</button>
          <button type="button" (click)="downloadSource()">Download source</button>
          <button *ngIf="svgSource" type="button" (click)="downloadSvg()">SVG</button>
          <button *ngIf="chart" type="button" (click)="downloadPng()">PNG</button>
          <button type="button" (click)="toggleExpanded()" [attr.aria-expanded]="expanded">{{ expanded ? 'Close expanded view' : 'Expand' }}</button>
        </div>
      </header>
      <p *ngIf="status" class="rich-status" role="status">{{ status }}</p>
      <p *ngIf="error" class="rich-error" role="alert">{{ error }}</p>
      <p *ngIf="loading" class="rich-status" role="status">Rendering visual...</p>
      <pre *ngIf="block.kind === 'code' || showSource || error"><code>{{ block.source }}</code></pre>
      <div *ngIf="svgHtml" class="rich-diagram" role="img" aria-label="Rendered diagram; source available above" [innerHTML]="svgHtml"></div>
      <div [hidden]="!chartSpec || !!error" class="rich-chart">
        <canvas #canvas role="img" [attr.aria-label]="chartSpec?.title">Chart data is available in the table below.</canvas>
      </div>
      <details *ngIf="chartSpec && !error" class="rich-data">
        <summary>Chart data (accessible table)</summary>
        <div class="rich-table-scroll"><table>
          <caption>{{ chartSpec.title }}</caption>
          <thead><tr><th scope="col">Dataset</th><th scope="col">Label / X</th><th scope="col">Value / Y</th></tr></thead>
          <tbody><ng-container *ngFor="let dataset of chartSpec.datasets">
            <tr *ngFor="let point of dataset.data; let i = index"><th scope="row">{{ dataset.label }}</th><td>{{ pointX(point, i) }}</td><td>{{ pointY(point) }}</td></tr>
          </ng-container></tbody>
        </table></div>
      </details>
    </dialog>
  `,
})
export class RichBlockComponent implements AfterViewInit, OnChanges, OnDestroy {
  @Input({ required: true }) block!: RichBlock
  @ViewChild('canvas', { static: true }) canvas!: ElementRef<HTMLCanvasElement>
  @ViewChild('frame', { static: true }) frame!: ElementRef<HTMLDialogElement>
  private readonly sanitizer = inject(DomSanitizer)
  private version = 0
  private ready = false
  private destroyed = false
  private observer?: MutationObserver
  private timer?: ReturnType<typeof setTimeout>
  chart?: Chart
  chartSpec?: ChartSpec
  svgHtml: SafeHtml | null = null
  svgSource = ''
  showSource = false
  expanded = false
  loading = false
  error = ''
  status = ''

  ngAfterViewInit() {
    this.ready = true
    this.observer = new MutationObserver(() => this.schedule())
    this.observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] })
    this.schedule()
  }
  ngOnChanges() { this.schedule() }
  private schedule() {
    this.version++
    if (!this.ready || this.destroyed) return
    clearTimeout(this.timer)
    this.timer = setTimeout(() => { void this.render(this.version) })
  }
  private async render(version: number) {
    this.chart?.destroy()
    this.chart = undefined
    this.chartSpec = undefined
    this.svgHtml = null
    this.svgSource = ''
    this.error = ''
    this.status = ''
    const block = this.block
    if (block.kind === 'code') return
    this.loading = true
    const current = () => !this.destroyed && version === this.version
    const dark = document.documentElement.dataset['theme'] === 'dark'
    try {
      if (block.kind === 'chart') {
        const spec = validateChart(block.source)
        const { Chart, registerables } = await (chartModule ??= import('chart.js'))
        if (!current()) return
        Chart.register(...registerables)
        const fontScale = Number.parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--app-font-scale')) / 100
        const chartFont = { family: 'Inter, Segoe UI, sans-serif', size: 12 * fontScale }
        this.chartSpec = spec
        const ink = dark ? '#cbd5e1' : '#334155'
        const configuration: ChartConfiguration = {
          type: spec.type === 'histogram' ? 'bar' : spec.type,
          data: {
            labels: spec.labels,
            datasets: spec.datasets.map((dataset, index) => ({
              ...dataset,
              backgroundColor: spec.type === 'pie' ? colors : colors[index],
              borderColor: colors[index],
              borderWidth: 2,
              pointStyle: ['circle', 'rect', 'triangle', 'cross', 'star', 'rectRot'][index],
              borderDash: spec.type === 'line' && index % 2 ? [6, 3] : [],
            })),
          },
          options: {
            responsive: true, maintainAspectRatio: false, animation: false,
            font: chartFont,
            plugins: {
              legend: { labels: { color: ink, usePointStyle: true, font: chartFont } },
              title: { display: true, text: spec.title, color: ink, font: { ...chartFont, size: 16 * fontScale } },
              tooltip: { titleFont: chartFont, bodyFont: chartFont, footerFont: chartFont },
            },
            scales: spec.type === 'pie' ? {} : {
              x: { ticks: { color: ink, font: chartFont }, grid: { color: dark ? '#334155' : '#e2e8f0' } },
              y: { beginAtZero: true, ticks: { color: ink, font: chartFont }, grid: { color: dark ? '#334155' : '#e2e8f0' } },
            },
          },
        }
        this.chart = new Chart(this.canvas.nativeElement, configuration)
      } else {
        const source = prepareMermaid(block.source)
        if (source !== block.source) this.status = 'Label quoting adjusted for rendering; original source is unchanged.'
        // Mermaid has global configuration/state. Serialize renders across components and themes.
        const work = diagramQueue.then(async () => {
          if (!current()) return
          const { default: mermaid } = await (mermaidModule ??= import('mermaid'))
          if (!current()) return
          const ink = dark ? '#f8fafc' : '#0f172a'
          const palette = dark
            ? ['#4f46e5', '#0e7490', '#7e22ce', '#047857', '#9d174d', '#92400e']
            : ['#e0e7ff', '#cffafe', '#f3e8ff', '#d1fae5', '#fce7f3', '#fef3c7']
          const scales = Object.fromEntries(Array.from({ length: 12 }, (_, index) => [`cScale${index}`, palette[index % palette.length]]))
          const scaleLabels = Object.fromEntries(Array.from({ length: 12 }, (_, index) => [`cScaleLabel${index}`, ink]))
          const fontScale = Number.parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--app-font-scale')) / 100
          mermaid.initialize({
            startOnLoad: false, securityLevel: 'strict', theme: 'base',
            themeVariables: {
              darkMode: dark, fontFamily: 'Inter, Segoe UI, sans-serif', fontSize: `${16 * fontScale}px`,
              primaryColor: palette[0], secondaryColor: palette[1], tertiaryColor: palette[2],
              primaryTextColor: ink, secondaryTextColor: ink, tertiaryTextColor: ink, textColor: ink,
              lineColor: dark ? '#a5b4fc' : '#4f46e5', primaryBorderColor: dark ? '#a5b4fc' : '#6366f1',
              git0: palette[0], gitBranchLabel0: ink, ...scales, ...scaleLabels,
            },
            htmlLabels: false, flowchart: { htmlLabels: false }, maxTextSize: 8000, maxEdges: 150,
            suppressErrorRendering: true,
          })
          const { svg } = await mermaid.render(`pool-diagram-${++diagramId}`, source)
          if (!current()) return
          this.svgSource = sanitizeDiagram(svg)
          this.svgHtml = this.sanitizer.bypassSecurityTrustHtml(this.svgSource)
        })
        diagramQueue = work.catch(() => { /* The owning component reports the error below; keep the queue usable. */ })
        await work
      }
    } catch (error) {
      if (current()) {
        this.error = `Unable to render ${block.kind}: ${error instanceof Error ? error.message : 'Invalid visual source.'}`
      }
    } finally {
      if (current()) this.loading = false
    }
  }
  pointX(point: number | { x: number; y: number }, index: number) {
    return typeof point === 'number' ? this.chartSpec?.labels[index] : point.x
  }
  pointY(point: number | { x: number; y: number }) { return typeof point === 'number' ? point : point.y }
  async copySource() {
    try {
      await navigator.clipboard.writeText(this.block.source)
      if (!this.destroyed) this.status = 'Source copied.'
    } catch {
      if (!this.destroyed) {
        this.status = 'Clipboard unavailable. Select and copy the source manually.'
        this.showSource = true
      }
    }
  }
  toggleExpanded() {
    const frame = this.frame.nativeElement
    frame.close()
    this.expanded = !this.expanded
    if (this.expanded) frame.showModal()
    else frame.show()
    this.chart?.resize()
  }
  closeExpanded(event: Event) { event.preventDefault(); if (this.expanded) this.toggleExpanded() }
  private download(blob: Blob, name: string) {
    const url = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = name
    anchor.click()
    setTimeout(() => URL.revokeObjectURL(url), 1000)
  }
  downloadSource() { this.download(new Blob([this.block.source], { type: 'text/plain;charset=utf-8' }), this.block.kind === 'chart' ? 'chart.json' : 'response-source.txt') }
  downloadSvg() { this.download(new Blob([this.svgSource], { type: 'image/svg+xml;charset=utf-8' }), 'diagram.svg') }
  downloadPng() {
    this.canvas.nativeElement.toBlob(blob => {
      if (this.destroyed) return
      if (blob) this.download(blob, 'chart.png')
      else if (!this.destroyed) this.status = 'PNG export unavailable. Download the source instead.'
    })
  }
  ngOnDestroy() {
    this.destroyed = true
    this.version++
    clearTimeout(this.timer)
    this.observer?.disconnect()
    this.chart?.destroy()
    if (this.expanded) this.frame.nativeElement.close()
  }
}

@Component({
  selector: 'app-rich-response',
  standalone: true,
  imports: [CommonModule, RichBlockComponent],
  template: `
    <div class="rich-response markdown-body">
      <p *ngIf="error" class="rich-error" role="alert">{{ error }}</p>
      <ng-container *ngFor="let block of blocks">
        <div *ngIf="block.kind === 'markdown'; else visual" [innerHTML]="block.html"></div>
        <ng-template #visual><app-rich-block [block]="block"></app-rich-block></ng-template>
      </ng-container>
    </div>
  `,
  styleUrls: ['./rich-response.css'],
})
export class RichResponseComponent implements OnChanges {
  @Input() content: string | null | undefined = ''
  private readonly sanitizer = inject(DomSanitizer)
  blocks: (RichBlock & { html?: SafeHtml })[] = []
  error = ''
  ngOnChanges() {
    this.error = ''
    try {
      this.blocks = richBlocks(this.content || '').map(block => block.kind === 'markdown'
        ? { ...block, html: this.sanitizer.bypassSecurityTrustHtml(sanitizeMarkdown(block.source)) }
        : block)
    } catch (error) {
      this.blocks = []
      this.error = error instanceof Error ? error.message : 'Unable to render response.'
    }
  }
}
