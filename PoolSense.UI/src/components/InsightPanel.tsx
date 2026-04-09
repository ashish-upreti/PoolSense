import type { TicketWorkflowResult } from '../services/api'
import { lazy, Suspense } from 'react'
import IncidentList from './IncidentList'
import PatternList from './PatternList'

const TelemetryChart = lazy(() => import('./TelemetryChart'))

interface InsightPanelProps {
  insights: TicketWorkflowResult | null
  isLoading: boolean
}

function buildPatterns(insights: TicketWorkflowResult | null): string[] {
  if (!insights) {
    return []
  }

  return Array.from(
    new Set(
      [
        insights.failurePattern.failureType,
        insights.failurePattern.resolutionCategory,
      ].filter((pattern) => pattern && pattern.trim().length > 0),
    ),
  )
}

function buildTelemetry(insights: TicketWorkflowResult | null, confidence: number) {
  if (!insights) {
    return []
  }

  const avgSimilarity =
    insights.similarIncidents.length > 0
      ? Math.round(
          (insights.similarIncidents.reduce((sum, i) => sum + i.similarity, 0) /
            insights.similarIncidents.length) *
            100,
        )
      : 0

  return [
    {
      name: 'Confidence',
      value: confidence,
      color: '#2f79c8',
    },
    {
      name: 'Incidents',
      value: avgSimilarity,
      color: '#79a6d6',
    },
    {
      name: 'Pattern fit',
      value: Math.min(insights.failurePatternFrequency * 10, 100),
      color: '#9db9da',
    },
  ]
}

export default function InsightPanel({ insights, isLoading }: InsightPanelProps) {
  const patterns = buildPatterns(insights)
  const confidence = insights ? Math.round(insights.confidence * 100) : 0
  const telemetry = buildTelemetry(insights, confidence)

  return (
    <aside className="insight-panel">
      <div className="insight-panel-header">
        <div>
          <p className="panel-kicker">Incident Insights</p>
          <h2>Operational context</h2>
        </div>
        <span className={`panel-status ${isLoading ? 'panel-status-busy' : ''}`}>
          {isLoading ? 'PoolSense is analyzing incidents...' : 'Workspace synced'}
        </span>
      </div>

      {!insights ? (
        <section className="insight-card insight-empty">
          <h3>No insights yet</h3>
          <p>
            Ask PoolSense about an incident to populate similar cases, failure
            patterns, and system-level analysis in this workspace.
          </p>
        </section>
      ) : (
        <>
          <section className="insight-card">
            <div className="card-heading-row">
              <div>
                <p className="panel-kicker">System Insights</p>
                <h3>Confidence and routing</h3>
              </div>
              <strong className="confidence-value data-mono">{confidence}%</strong>
            </div>

            <div className="confidence-track" aria-hidden="true">
              <span className="confidence-fill" style={{ width: `${confidence}%` }} />
            </div>

            <div className="system-grid">
              <div>
                <p className="system-label">System</p>
                <p className="data-mono">{insights.failurePattern.system || 'Unknown system'}</p>
              </div>
              <div>
                <p className="system-label">Component</p>
                <p className="data-mono">{insights.failurePattern.component || 'Unknown component'}</p>
              </div>
              <div>
                <p className="system-label">Resolution Category</p>
                <p>{insights.failurePattern.resolutionCategory || 'Unclassified'}</p>
              </div>
              <div>
                <p className="system-label">Pattern Ticket</p>
                <p className="data-mono">{insights.failurePattern.ticketId || 'Not linked'}</p>
              </div>
            </div>

            <div className="reasoning-block">
              <p className="system-label">Reasoning</p>
              <p>{insights.reasoning || 'No reasoning details available.'}</p>
            </div>

            <div className="telemetry-block">
              <div className="card-heading-row">
                <div>
                  <p className="panel-kicker">System Insights</p>
                  <h3>Telemetry snapshot</h3>
                </div>
                <span className="section-count data-mono">LIVE</span>
              </div>

              <div className="telemetry-chart">
                <Suspense fallback={<div className="telemetry-fallback">Loading telemetry...</div>}>
                  <TelemetryChart data={telemetry} />
                </Suspense>
              </div>
            </div>
          </section>

          <section className="insight-card">
            <div className="card-heading-row">
              <div>
                <p className="panel-kicker">Similar Incidents</p>
                <h3>Historical matches</h3>
              </div>
              <span className="section-count data-mono">{insights.similarIncidents.length}</span>
            </div>
            <IncidentList incidents={insights.similarIncidents} />
          </section>

          <section className="insight-card">
            <p className="panel-kicker">Failure Patterns</p>
            <h3>Recurring signals</h3>
            <PatternList patterns={patterns} />
          </section>
        </>
      )}
    </aside>
  )
}