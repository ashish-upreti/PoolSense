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

export interface ProjectConfig {
  id: number
  projectId: string
  projectName: string
  knowledgeLookbackYears: number
  similaritySearchLimit: number
  sendEmail: boolean
  poolingEnabled: boolean
  emailRecipients: string
  applicationFilter: string
  createdAt: string
}

export interface ProjectConfigInput {
  projectId: string
  projectName: string
  knowledgeLookbackYears: number
  similaritySearchLimit: number
  sendEmail: boolean
  poolingEnabled: boolean
  emailRecipients: string
  applicationFilter: string
}

export interface IngestionStatus {
  projectId: string
  ingested: number
  total: number
  progressPercentage: number
}

export interface FeedbackRequest {
  query: string
  suggestedResolution: string
  feedbackType: number
  wasUsed?: boolean
  comment?: string
  retrievedTicketIds: string[]
}

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? ''

async function readErrorMessage(response: Response, fallbackMessage: string): Promise<string> {
  const contentType = response.headers.get('content-type') ?? ''

  if (contentType.includes('application/json')) {
    const payload = (await response.json()) as {
      title?: string
      errors?: Record<string, string[]>
      detail?: string
    }

    if (payload.errors) {
      const validationMessages = Object.values(payload.errors).flat().join(' ')
      if (validationMessages) {
        return validationMessages
      }
    }

    if (payload.detail) {
      return payload.detail
    }

    if (payload.title) {
      return payload.title
    }
  }

  const errorText = await response.text()
  return errorText || fallbackMessage
}

export async function getProjectGroups(): Promise<ProjectGroup[]> {
  const response = await fetch(`${API_BASE_URL}/api/projects/groups`)
  if (!response.ok) return []
  const data = (await response.json()) as { groups: ProjectGroup[] }
  return data.groups ?? []
}

export async function getProjects(): Promise<ProjectConfig[]> {
  const response = await fetch(`${API_BASE_URL}/api/projects`)

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, 'Unable to load projects.'))
  }

  return (await response.json()) as ProjectConfig[]
}

export async function createProject(project: ProjectConfigInput): Promise<ProjectConfig> {
  const response = await fetch(`${API_BASE_URL}/api/projects`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(project),
  })

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, 'Unable to create the project.'))
  }

  return (await response.json()) as ProjectConfig
}

export async function updateProject(projectId: string, project: ProjectConfigInput): Promise<ProjectConfig> {
  const response = await fetch(`${API_BASE_URL}/api/projects/${encodeURIComponent(projectId)}`, {
    method: 'PUT',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(project),
  })

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, 'Unable to update the project.'))
  }

  return (await response.json()) as ProjectConfig
}

export async function getIngestionStatuses(refresh = false): Promise<IngestionStatus[]> {
  const query = refresh ? '?refresh=true' : ''
  const response = await fetch(`${API_BASE_URL}/api/ingestion/status${query}`)

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, 'Unable to load ingestion status.'))
  }

  return (await response.json()) as IngestionStatus[]
}

export async function submitFeedback(request: FeedbackRequest): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/api/feedback`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({
      ...request,
      wasUsed: request.wasUsed ?? false,
    }),
  })

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, 'Unable to submit feedback.'))
  }
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