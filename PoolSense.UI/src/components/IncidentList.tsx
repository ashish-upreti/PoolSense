import type { SimilarIncident } from '../services/api'

interface IncidentListProps {
  incidents: SimilarIncident[]
}

export default function IncidentList({ incidents }: IncidentListProps) {
  if (incidents.length === 0) {
    return <p className="empty-copy">No related incidents were returned.</p>
  }

  return (
    <ul className="insight-list">
      {incidents.map((incident) => (
        <li key={incident.ticketId} className="insight-list-item">
          <div>
            <p className="insight-item-title data-mono">
              {incident.ticketId}
              {incident.similarity > 0 ? (
                <span className="similarity-badge">
                  {Math.round(incident.similarity * 100)}% match
                </span>
              ) : null}
            </p>
            <p className="insight-item-copy">{incident.problem}</p>
          </div>
          <div className="incident-meta">
            <span className="incident-tag" title={incident.resolution}>
              {incident.resolution
                ? incident.resolution.length > 60
                  ? incident.resolution.slice(0, 60).trimEnd() + '…'
                  : incident.resolution
                : 'No resolution'}
            </span>
            <a
              className="incident-link data-mono"
              href={`https://pool.intel.com/Edit/${incident.ticketId}`}
              target="_blank"
              rel="noopener noreferrer"
            >
              View ticket {incident.ticketId}
            </a>
          </div>
        </li>
      ))}
    </ul>
  )
}