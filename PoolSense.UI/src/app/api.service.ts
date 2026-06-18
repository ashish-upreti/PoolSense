import { Injectable } from '@angular/core'
import { environment } from '../environments/environment'

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

export interface PoolReport {
  sourceEventId: string
  status: 'Ready' | 'Pending' | 'Processing'
  isReady: boolean
  message: string
  retryAfterSeconds: number
  processingKind: string
  processedAt: string | null
  emailSent: boolean
  emailRecipient: string
  workflowResult: TicketWorkflowResult | null
}

export interface PoolRecommendationReportListItem {
  sourceEventId: string
  processingKind: string
  processedAt: string
  emailSent: boolean
  emailRecipient: string
  projectId: string
  projectName: string
  application: string
  summary: string
  confidence: number
  similarIncidentCount: number
  failureType: string
  resolutionCategory: string
  reportUrl: string
}

export interface PoolRecommendationReportListResponse {
  items: PoolRecommendationReportListItem[]
  totalCount: number
  page: number
  pageSize: number
}

export interface PoolRecommendationReportFilters {
  projectId?: string
  searchTerm?: string
  emailSent?: boolean | null
  from?: string
  to?: string
  page?: number
  pageSize?: number
}

export interface PoolTroubleshootResponse {
  sourceEventId: string
  question: string
  answer: string
  generatedAt: string
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

export interface TicketAutomationSettings {
  deployment?: DeploymentInfo
  pollingEnabled: boolean
  pollIntervalSeconds: number
  poolSenseEmail: boolean
  closedStatusName: string
  newStatusName: string
  similaritySearchLimit: number
  email: {
    recipient: string
    fromAddress: string
    deliveryMode: string
    smtpHost: string
    port: number
    timeoutMs: number
    databaseMailProfile: string
  }
}

export interface DeploymentInfo {
  environmentName: string
  environmentLabel: string
  machineName: string
  poolSenseDatabaseName: string
  ticketSourceDatabaseName: string
}

export interface TicketAutomationSettingsInput {
  pollingEnabled: boolean
  pollIntervalSeconds: number
  poolSenseEmail: boolean
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
  selectedTicketId: string
  retrievedTicketIds: string[]
}

export interface ApplicationFeedbackRequest {
  userName: string
  userEmail: string
  feedbackType: string
  message: string
}

export interface AuthenticatedUser {
  username: string
  authPrincipal: string
  displayName: string
  email: string
  groups: string[]
  isAdmin?: boolean
}

export interface LoginResponse {
  success: boolean
  message: string
  user: AuthenticatedUser
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly apiBaseUrl = environment.apiBaseUrl.replace(/\/$/, '')

  async getSession(): Promise<AuthenticatedUser | null> {
    const response = await fetch(this.apiUrl('/auth/session'), {
      credentials: 'include',
    })

    if (response.status === 401 || response.status === 410) {
      return null
    }

    if (!response.ok) {
      throw new Error(await this.readErrorMessage(response, 'Unable to validate your session.'))
    }

    const data = (await response.json()) as { success: boolean; user?: AuthenticatedUser }
    return data.success && data.user ? data.user : null
  }

  async login(username: string, password: string, rememberMe: boolean): Promise<LoginResponse> {
    const encryptedPassword = await this.encryptPassword(password)
    const response = await fetch(this.apiUrl('/auth/login'), {
      method: 'POST',
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        username,
        rememberMe,
        ...(encryptedPassword ? { encryptedPassword } : { password }),
      }),
    })

    if (!response.ok) {
      throw new Error(await this.readErrorMessage(response, 'Unable to sign in.'))
    }

    const data = (await response.json()) as LoginResponse
    if (!data.success) {
      throw new Error(data.message || 'Unable to sign in.')
    }

    return data
  }

  async logout(): Promise<void> {
    await fetch(this.apiUrl('/auth/logout'), {
      method: 'POST',
      credentials: 'include',
    })
  }

  async getProjectGroups(): Promise<ProjectGroup[]> {
    const response = await fetch(this.apiUrl('/projects/groups'), {
      credentials: 'include',
    })
    if (!response.ok) return []

    const data = (await response.json()) as { groups: ProjectGroup[] }
    return data.groups ?? []
  }

  async getProjects(): Promise<ProjectConfig[]> {
    const response = await fetch(this.apiUrl('/projects'), {
      credentials: 'include',
    })

    if (!response.ok) {
      throw new Error(await this.readErrorMessage(response, 'Unable to load projects.'))
    }

    return (await response.json()) as ProjectConfig[]
  }

  async getTicketAutomationSettings(): Promise<TicketAutomationSettings> {
    const response = await fetch(this.apiUrl('/settings/ticket-automation'), {
      credentials: 'include',
    })

    if (!response.ok) {
      throw new Error(await this.readErrorMessage(response, 'Unable to load application-level polling settings.'))
    }

    return (await response.json()) as TicketAutomationSettings
  }

  async getDeploymentInfo(): Promise<DeploymentInfo | null> {
    const response = await fetch(this.apiUrl('/settings/deployment'), {
      credentials: 'include',
    })

    if (!response.ok) {
      return null
    }

    return (await response.json()) as DeploymentInfo
  }

  async updateTicketAutomationSettings(settings: TicketAutomationSettingsInput): Promise<TicketAutomationSettings> {
    const response = await fetch(this.apiUrl('/settings/ticket-automation'), {
      method: 'PUT',
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(settings),
    })

    if (!response.ok) {
      throw new Error(await this.readErrorMessage(response, 'Unable to update application-level polling settings.'))
    }

    return (await response.json()) as TicketAutomationSettings
  }

  async createProject(project: ProjectConfigInput): Promise<ProjectConfig> {
    const response = await fetch(this.apiUrl('/projects'), {
      method: 'POST',
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(project),
    })

    if (!response.ok) {
      throw new Error(await this.readErrorMessage(response, 'Unable to create the project.'))
    }

    return (await response.json()) as ProjectConfig
  }

  async updateProject(projectId: string, project: ProjectConfigInput): Promise<ProjectConfig> {
    const response = await fetch(this.apiUrl(`/projects/${encodeURIComponent(projectId)}`), {
      method: 'PUT',
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(project),
    })

    if (!response.ok) {
      throw new Error(await this.readErrorMessage(response, 'Unable to update the project.'))
    }

    return (await response.json()) as ProjectConfig
  }

  async getIngestionStatuses(refresh = false): Promise<IngestionStatus[]> {
    const query = refresh ? '?refresh=true' : ''
    const response = await fetch(this.apiUrl(`/ingestion/status${query}`), {
      credentials: 'include',
    })

    if (!response.ok) {
      throw new Error(await this.readErrorMessage(response, 'Unable to load ingestion status.'))
    }

    return (await response.json()) as IngestionStatus[]
  }

  async submitFeedback(request: FeedbackRequest): Promise<void> {
    const response = await fetch(this.apiUrl('/feedback'), {
      method: 'POST',
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        ...request,
        wasUsed: request.wasUsed ?? false,
      }),
    })

    if (!response.ok) {
      throw new Error(await this.readErrorMessage(response, 'Unable to submit feedback.'))
    }
  }

  async submitApplicationFeedback(request: ApplicationFeedbackRequest): Promise<void> {
    const response = await fetch(this.apiUrl('/feedback/application'), {
      method: 'POST',
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(request),
    })

    if (!response.ok) {
      throw new Error(await this.readErrorMessage(response, 'Unable to submit application feedback.'))
    }
  }

  async askPoolSense(message: string, selectedGroupIds?: string[]): Promise<TicketWorkflowResult> {
    const response = await fetch(this.apiUrl('/ticket/process'), {
      method: 'POST',
      credentials: 'include',
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

  async getPoolReport(poolId: string): Promise<PoolReport> {
    const response = await fetch(this.apiUrl(`/pool/${encodeURIComponent(poolId)}/report`), {
      credentials: 'include',
    })

    if (!response.ok) {
      throw new Error(await this.readErrorMessage(response, `Unable to load PoolSense report for pool ${poolId}.`))
    }

    return (await response.json()) as PoolReport
  }

  async getPoolRecommendations(filters: PoolRecommendationReportFilters = {}): Promise<PoolRecommendationReportListResponse> {
    const query = new URLSearchParams()
    if (filters.projectId) query.set('projectId', filters.projectId)
    if (filters.searchTerm) query.set('q', filters.searchTerm)
    if (filters.emailSent !== undefined && filters.emailSent !== null) query.set('emailSent', String(filters.emailSent))
    if (filters.from) query.set('from', filters.from)
    if (filters.to) query.set('to', filters.to)
    if (filters.page) query.set('page', String(filters.page))
    if (filters.pageSize) query.set('pageSize', String(filters.pageSize))

    const path = `/pool/reports${query.toString() ? `?${query.toString()}` : ''}`
    const response = await fetch(this.apiUrl(path), {
      credentials: 'include',
    })

    if (!response.ok) {
      throw new Error(await this.readErrorMessage(response, 'Unable to load Pool Recommendations.'))
    }

    return (await response.json()) as PoolRecommendationReportListResponse
  }

  async troubleshootPool(poolId: string, question: string): Promise<PoolTroubleshootResponse> {
    const response = await fetch(this.apiUrl(`/pool/${encodeURIComponent(poolId)}/troubleshoot`), {
      method: 'POST',
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ question }),
    })

    if (!response.ok) {
      throw new Error(await this.readErrorMessage(response, `Unable to troubleshoot pool ${poolId}.`))
    }

    return (await response.json()) as PoolTroubleshootResponse
  }

  private async encryptPassword(password: string): Promise<string | null> {
    if (!globalThis.crypto?.subtle) {
      return null
    }

    try {
      const response = await fetch(this.apiUrl('/auth/pubkey'), {
        credentials: 'include',
      })

      if (!response.ok) {
        return null
      }

      const data = (await response.json()) as { publicKey?: string }
      if (!data.publicKey) {
        return null
      }

      const key = await globalThis.crypto.subtle.importKey(
        'spki',
        this.pemToArrayBuffer(data.publicKey),
        { name: 'RSA-OAEP', hash: 'SHA-256' },
        false,
        ['encrypt'],
      )
      const encrypted = await globalThis.crypto.subtle.encrypt(
        { name: 'RSA-OAEP' },
        key,
        new TextEncoder().encode(password),
      )

      return this.arrayBufferToBase64(encrypted)
    } catch {
      return null
    }
  }

  private pemToArrayBuffer(pem: string) {
    const base64 = pem
      .replace('-----BEGIN PUBLIC KEY-----', '')
      .replace('-----END PUBLIC KEY-----', '')
      .replace(/\s/g, '')
    const binary = atob(base64)
    const bytes = new Uint8Array(binary.length)

    for (let index = 0; index < binary.length; index += 1) {
      bytes[index] = binary.charCodeAt(index)
    }

    return bytes.buffer
  }

  private arrayBufferToBase64(buffer: ArrayBuffer) {
    const bytes = new Uint8Array(buffer)
    let binary = ''

    for (let index = 0; index < bytes.byteLength; index += 1) {
      binary += String.fromCharCode(bytes[index])
    }

    return btoa(binary)
  }

  private apiUrl(path: string) {
    return `${this.apiBaseUrl}${path.startsWith('/') ? path : `/${path}`}`
  }

  private async readErrorMessage(response: Response, fallbackMessage: string): Promise<string> {
    const contentType = response.headers.get('content-type') ?? ''

    if (contentType.includes('application/json')) {
      const payload = (await response.json()) as {
        title?: string
        errors?: Record<string, string[]>
        detail?: string
        message?: string
        error?: string
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

      if (payload.message) {
        return payload.message
      }

      if (payload.error) {
        return payload.error
      }
    }

    const errorText = await response.text()
    return errorText || fallbackMessage
  }
}