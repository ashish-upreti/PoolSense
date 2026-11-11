import { Marked, type Token } from 'marked'
import DOMPurify from 'dompurify'

export type ChartKind = 'bar' | 'line' | 'pie' | 'scatter' | 'histogram'
export type ChartPoint = number | { x: number; y: number }
export interface ChartSpec {
  type: ChartKind
  title: string
  labels: string[]
  datasets: { label: string; data: ChartPoint[] }[]
}
export type RichBlock = { kind: 'markdown' | 'code' | 'mermaid' | 'chart'; source: string; language?: string }
const MAX_SOURCE = 100_000
const markdown = new Marked({
  gfm: true, breaks: true, async: false,
  renderer: {
    html: ({ text }) => escapeHtml(text),
    image: ({ text }) => escapeHtml(text),
  },
})

export function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}

export function richBlocks(source: string): RichBlock[] {
  if (source.length > MAX_SOURCE) throw new Error('Response exceeds the 100,000 character rendering limit. Download or shorten the response.')
  const blocks: RichBlock[] = []
  let pending: Token[] = []
  const flush = () => {
    if (pending.length) blocks.push({ kind: 'markdown', source: markdown.parser(pending) })
    pending = []
  }
  for (const token of markdown.lexer(source)) {
    if (token.type !== 'code') { pending.push(token); continue }
    flush()
    const language = (token.lang || '').trim().toLowerCase()
    blocks.push({ kind: language === 'mermaid' || language === 'chart' ? language : 'code', source: token.text, language })
  }
  flush()
  if (blocks.filter(block => block.kind !== 'markdown').length > 20) throw new Error('Use at most 20 fenced blocks per response.')
  return blocks
}

export function sanitizeMarkdown(html: string): string {
  const clean = DOMPurify.sanitize(html, {
    USE_PROFILES: { html: true }, FORBID_TAGS: ['style', 'img', 'svg', 'iframe', 'object', 'embed'],
    FORBID_ATTR: ['style', 'id', 'name'],
  })
  const document = new DOMParser().parseFromString(clean, 'text/html')
  for (const link of Array.from(document.querySelectorAll('a'))) {
    const href = link.getAttribute('href') || ''
    if (!/^(https?:\/\/|mailto:|#)/i.test(href)) link.removeAttribute('href')
    link.setAttribute('target', '_blank')
    link.setAttribute('rel', 'noopener noreferrer')
  }
  return document.body.innerHTML
}

export function validateMermaid(source: string): void {
  if (source.length > 8000 || source.split(/[;\n]/).length > 100) throw new Error('Diagram exceeds the 8,000 character / 100 statement limit.')
  if ((source.match(/\[[^\]]*\]|\([^)]*\)|\{[^}]*\}/g) || []).length > 100) throw new Error('Diagram exceeds the 100 labelled node limit.')
  if (!/^\s*(flowchart|graph|sequenceDiagram|pie|timeline|mindmap)\b/.test(source)) throw new Error('Unsupported diagram. Use flowchart, sequenceDiagram, pie, timeline or mindmap.')
  if (/%%|(?:^|[\s;])(?:click|style|classDef|linkStyle)\b|<[/!a-z]|javascript:|https?:|data:|@\{|image\s*:/i.test(source)) {
    throw new Error('Unsafe diagram: directives, links, HTML, external resources and custom styling are not allowed.')
  }
  if ((source.match(/-->|---|==>|-.->|->>|-->>/g) || []).length > 150) throw new Error('Diagram exceeds the 150 edge limit.')
}

export function sanitizeDiagram(svg: string): string {
  return DOMPurify.sanitize(svg, {
    USE_PROFILES: { svg: true, svgFilters: true },
    FORBID_TAGS: ['foreignObject', 'a', 'image', 'script', 'animate', 'set'],
    FORBID_ATTR: ['href', 'xlink:href'],
  })
}

function object(value: unknown): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new Error('Expected a JSON object.')
  return value as Record<string, unknown>
}
function keys(value: Record<string, unknown>, allowed: string[]) {
  if (Object.keys(value).some(key => !allowed.includes(key))) throw new Error('Unknown chart field.')
}
function text(value: unknown): string {
  if (typeof value !== 'string' || !value.trim() || value.length > 120 || /[<>]|https?:\/\//i.test(value)) throw new Error('Chart labels must be plain text, 1-120 characters.')
  return value
}
function number(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value) || Math.abs(value) > 1e12) throw new Error('Chart values must be finite numbers within +/- 1e12.')
  return value
}

export function validateChart(source: string): ChartSpec {
  if (source.length > 30_000) throw new Error('Chart exceeds the 30,000 character limit.')
  const raw = object(JSON.parse(source))
  keys(raw, ['type', 'title', 'labels', 'datasets', 'samples', 'bins'])
  const type = raw['type']
  if (typeof type !== 'string' || !['bar', 'line', 'pie', 'scatter', 'histogram'].includes(type)) throw new Error('Unsupported chart type.')
  const title = text(raw['title'])
  if (type === 'histogram' && raw['samples'] !== undefined) {
    if (raw['labels'] !== undefined || raw['datasets'] !== undefined) throw new Error('Use either histogram samples or explicit bins, not both.')
    if (!Array.isArray(raw['samples']) || !raw['samples'].length || raw['samples'].length > 500) throw new Error('Histogram requires 1-500 samples.')
    const samples = raw['samples'].map(number)
    const bins = raw['bins'] === undefined ? 10 : number(raw['bins'])
    if (!Number.isInteger(bins) || bins < 1 || bins > 40) throw new Error('Histogram bins must be an integer from 1 to 40.')
    const min = Math.min(...samples), max = Math.max(...samples)
    const range = max - min
    const counts = Array<number>(bins).fill(0)
    samples.forEach(sample => counts[range === 0 ? 0 : Math.min(bins - 1, Math.floor(((sample - min) / range) * bins))]++)
    const format = (n: number) => Number(n.toPrecision(5)).toString()
    return { type, title, labels: counts.map((_, i) => `${format(min + (range || bins) * (i / bins))} - ${format(min + (range || bins) * ((i + 1) / bins))}`), datasets: [{ label: 'Count', data: counts }] }
  }
  if (raw['samples'] !== undefined || raw['bins'] !== undefined) throw new Error('Samples/bins are only supported for sample-based histograms.')
  if (!Array.isArray(raw['datasets']) || !raw['datasets'].length || raw['datasets'].length > 6) throw new Error('Use 1-6 chart datasets.')
  if ((type === 'pie' || type === 'histogram') && raw['datasets'].length !== 1) throw new Error('Pie and histogram charts require one dataset.')
  const labels = raw['labels'] === undefined ? [] : Array.isArray(raw['labels']) ? raw['labels'].map(text) : (() => { throw new Error('Labels must be an array.') })()
  if (labels.length > 200) throw new Error('Use at most 200 labels.')
  const datasets = raw['datasets'].map(dataset => {
    const value = object(dataset)
    keys(value, ['label', 'data'])
    if (!Array.isArray(value['data']) || !value['data'].length || value['data'].length > 200) throw new Error('Use 1-200 points per dataset.')
    const data = value['data'].map(point => {
      if (type !== 'scatter') return number(point)
      const xy = object(point)
      keys(xy, ['x', 'y'])
      return { x: number(xy['x']), y: number(xy['y']) }
    })
    if (type !== 'scatter' && data.length !== labels.length) throw new Error('Labels and data must have equal lengths.')
    if ((type === 'pie' || type === 'histogram') && data.some(point => typeof point === 'number' && (point < 0 || (type === 'histogram' && !Number.isInteger(point))))) throw new Error('Pie values must be nonnegative; histogram counts must be nonnegative integers.')
    return { label: text(value['label']), data }
  })
  return { type: type as ChartKind, title, labels, datasets }
}
