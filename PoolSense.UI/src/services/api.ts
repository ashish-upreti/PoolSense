export interface SimilarIncident {
  ticketId: string
  problem: string
  rootCause: string
  resolution: string
  similarity: number
}

export interface FailurePattern {
  id: number
  system: string
  component: string
  failureType: string
  resolutionCategory: string
  ticketId: string
  createdAt: string
}

export interface TicketWorkflowResult {
  suggestedRootCause: string
  suggestedResolution: string
  confidence: number
  similarIncidents: SimilarIncident[]
  failurePattern: FailurePattern
  reasoning: string
  failurePatternFrequency: number
}

export interface ProjectGroup {
  groupId: string
  displayName: string
}

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? ''

export async function getProjectGroups(): Promise<ProjectGroup[]> {
  const response = await fetch(`${API_BASE_URL}/api/projects/groups`)
  if (!response.ok) return []
  const data = (await response.json()) as { groups: ProjectGroup[] }
  return data.groups ?? []
}

export async function askPoolSense(
  message: string,
  selectedGroupIds?: string[],
): Promise<TicketWorkflowResult> {
  const response = await fetch(`${API_BASE_URL}/api/ticket/process`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({
      title: message,
      description: message,
      selectedGroupIds: selectedGroupIds ?? null,
    }),
  })

  if (!response.ok) {
    const errorText = await response.text()
    throw new Error(errorText || 'PoolSense request failed.')
  }

  return (await response.json()) as TicketWorkflowResult
}