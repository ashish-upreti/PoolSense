import type { ProjectGroup } from '../services/api'

interface GroupSelectorProps {
  groups: ProjectGroup[]
  selectedGroupIds: string[]
  onChange: (selectedGroupIds: string[]) => void
  disabled?: boolean
}

const ALL_VALUE = '__all__'

export default function GroupSelector({ groups, selectedGroupIds, onChange, disabled }: GroupSelectorProps) {
  if (groups.length === 0) return null

  const isAll = selectedGroupIds.length === 0

  function handleAllChange(checked: boolean) {
    if (checked) {
      onChange([]) // empty = All
    } else {
      // un-checking All selects every individual group
      onChange(groups.map((g) => g.groupId))
    }
  }

  function handleGroupChange(groupId: string, checked: boolean) {
    let next: string[]
    if (checked) {
      next = [...selectedGroupIds, groupId]
    } else {
      next = selectedGroupIds.filter((id) => id !== groupId)
    }
    // if nothing selected, fall back to All
    onChange(next.length === 0 ? [] : next)
  }

  return (
    <div className="group-selector" role="group" aria-label="Project group filter">
      <p className="group-selector-label">Search scope</p>
      <div className="group-selector-options">
        <label className={`group-chip ${isAll ? 'group-chip-active' : ''}`}>
          <input
            type="checkbox"
            value={ALL_VALUE}
            checked={isAll}
            disabled={disabled}
            onChange={(e) => handleAllChange(e.target.checked)}
          />
          All
        </label>
        {groups.map((group) => {
          const checked = !isAll && selectedGroupIds.includes(group.groupId)
          return (
            <label
              key={group.groupId}
              className={`group-chip ${checked ? 'group-chip-active' : ''}`}
            >
              <input
                type="checkbox"
                value={group.groupId}
                checked={checked}
                disabled={disabled}
                onChange={(e) => handleGroupChange(group.groupId, e.target.checked)}
              />
              {group.displayName}
            </label>
          )
        })}
      </div>
    </div>
  )
}
