interface PatternListProps {
  patterns: string[]
}

export default function PatternList({ patterns }: PatternListProps) {
  if (patterns.length === 0) {
    return <p className="empty-copy">No failure patterns detected yet.</p>
  }

  return (
    <ul className="pattern-list">
      {patterns.map((pattern) => (
        <li key={pattern} className="pattern-pill">
          {pattern}
        </li>
      ))}
    </ul>
  )
}