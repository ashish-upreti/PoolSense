import { CommonModule } from '@angular/common'
import { Component, OnInit, inject } from '@angular/core'
import { FormsModule } from '@angular/forms'
import {
  ApplicationFeedbackRequest,
  ApiService,
  IngestionStatus,
  ProjectConfig,
  ProjectConfigInput,
  ProjectGroup,
  SimilarIncident,
  TicketWorkflowResult,
} from './api.service'
import { environment } from '../environments/environment'

type UserMessage = {
  id: number
  role: 'user'
  text: string
}

type AssistantMessage = {
  id: number
  role: 'assistant'
  text: string
  query: string
  result: TicketWorkflowResult
}

type ChatMessage = UserMessage | AssistantMessage

type FeedbackState = {
  comment: string
  isSubmitting: boolean
  submitted: boolean
  error: string
  selectedFeedbackType: number | null
  wasUsed: boolean
  selectedTicketId: string
}

type TelemetryDatum = {
  name: string
  value: number
  color: string
}

type AppSection = 'main' | 'projectConfig' | 'applicationFeedback'

type ApplicationFeedbackForm = ApplicationFeedbackRequest

const quickPrompts = ['VG item missing', 'Data load job failed', 'UI error']

const defaultProjectForm: ProjectConfigInput = {
  projectId: '',
  projectName: '',
  knowledgeLookbackYears: environment.projectDefaults.knowledgeLookbackYears,
  similaritySearchLimit: environment.projectDefaults.similaritySearchLimit,
  sendEmail: environment.projectDefaults.sendEmail,
  poolingEnabled: environment.projectDefaults.poolingEnabled,
  emailRecipients: environment.projectDefaults.emailRecipients,
  applicationFilter: '',
}

function createDefaultProjectForm(): ProjectConfigInput {
  return { ...defaultProjectForm }
}

function createDefaultApplicationFeedbackForm(): ApplicationFeedbackForm {
  return {
    userName: '',
    userEmail: '',
    feedbackType: 'Suggestion',
    message: '',
  }
}

function createFeedbackState(selectedTicketId = ''): FeedbackState {
  return {
    comment: '',
    isSubmitting: false,
    submitted: false,
    error: '',
    selectedFeedbackType: null,
    wasUsed: false,
    selectedTicketId,
  }
}

function getDefaultFeedbackTicketId(similarIncidents: SimilarIncident[]) {
  return similarIncidents.find((incident) => incident.ticketId.trim().length > 0)?.ticketId ?? ''
}

function buildProjectIdPreview(projectName: string) {
  return Array.from(projectName.trim().toLowerCase())
    .map((character) => (/[a-z0-9]/.test(character) ? character : '-'))
    .join('')
    .replace(/^-+|-+$/g, '')
}

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './app.component.html',
})
export class AppComponent implements OnInit {
  private readonly api = inject(ApiService)

  readonly quickPrompts = quickPrompts
  readonly allGroupValue = '__all__'
  readonly appSettings = environment

  messages: ChatMessage[] = []
  insights: TicketWorkflowResult | null = null
  isLoading = false
  error = ''
  input = ''
  groups: ProjectGroup[] = []
  selectedGroupIds: string[] = []
  activeSection: AppSection = 'main'
  isSidebarCollapsed = true
  isDark = localStorage.getItem('theme') !== 'light'
  projects: ProjectConfig[] = []
  ingestionStatuses: IngestionStatus[] = []
  projectForm = createDefaultProjectForm()
  editingProjectId: string | null = null
  isProjectLoading = true
  isProjectSaving = false
  projectError = ''
  projectNotice = ''
  applicationFeedbackForm = createDefaultApplicationFeedbackForm()
  applicationFeedbackError = ''
  applicationFeedbackNotice = ''
  isApplicationFeedbackSaving = false
  feedbackStateByMessageId: Record<number, FeedbackState> = {}

  ngOnInit() {
    this.applyTheme()
    void this.loadProjectGroups()
    void this.loadProjectWorkspace()
  }

  get isAllGroupsSelected() {
    return this.selectedGroupIds.length === 0
  }

  get generatedProjectId() {
    return buildProjectIdPreview(this.projectForm.projectName)
  }

  get statusByProjectId() {
    return new Map(this.ingestionStatuses.map((status) => [status.projectId, status]))
  }

  get confidence() {
    return this.insights ? Math.round(this.insights.confidence * 100) : 0
  }

  get patterns() {
    if (!this.insights) {
      return []
    }

    return Array.from(
      new Set(
        [
          this.insights.failurePattern.failureType,
          this.insights.failurePattern.resolutionCategory,
        ].filter((pattern) => pattern && pattern.trim().length > 0),
      ),
    )
  }

  get telemetry(): TelemetryDatum[] {
    if (!this.insights) {
      return []
    }

    const avgSimilarity =
      this.insights.similarIncidents.length > 0
        ? Math.round(
            (this.insights.similarIncidents.reduce((sum, incident) => sum + incident.similarity, 0) /
              this.insights.similarIncidents.length) *
              100,
          )
        : 0

    return [
      { name: 'Confidence', value: this.confidence, color: '#6366f1' },
      { name: 'Incidents', value: avgSimilarity, color: '#818cf8' },
      { name: 'Pattern fit', value: Math.min(this.insights.failurePatternFrequency * 10, 100), color: '#a5b4fc' },
    ]
  }

  async handleSend(rawMessage = this.input) {
    const message = rawMessage.trim()

    if (!message || this.isLoading) {
      return
    }

    const userMessage: UserMessage = { id: Date.now(), role: 'user', text: message }
    this.messages = [...this.messages, userMessage]
    this.error = ''
    this.isLoading = true

    try {
      const result = await this.api.askPoolSense(message, this.selectedGroupIds)
      const assistantMessage: AssistantMessage = {
        id: userMessage.id + 1,
        role: 'assistant',
        text: result.suggestedResolution,
        query: message,
        result,
      }

      this.feedbackStateByMessageId[assistantMessage.id] = createFeedbackState(
        getDefaultFeedbackTicketId(result.similarIncidents),
      )

      this.messages = [...this.messages, assistantMessage]
      this.insights = result
      this.input = ''
    } catch (requestError) {
      this.error = requestError instanceof Error ? requestError.message : 'Unable to reach PoolSense.'
    } finally {
      this.isLoading = false
    }
  }

  handleComposerKeydown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault()
      void this.handleSend()
    }
  }

  toggleTheme() {
    this.isDark = !this.isDark
    this.applyTheme()
  }

  setActiveSection(section: AppSection) {
    this.activeSection = section
  }

  toggleSidebar() {
    this.isSidebarCollapsed = !this.isSidebarCollapsed
  }

  handleAllGroupChange(checked: boolean) {
    this.selectedGroupIds = checked ? [] : this.groups.map((group) => group.groupId)
  }

  handleGroupChange(groupId: string, checked: boolean) {
    const next = checked
      ? [...this.selectedGroupIds, groupId]
      : this.selectedGroupIds.filter((selectedGroupId) => selectedGroupId !== groupId)

    this.selectedGroupIds = next.length === 0 ? [] : next
  }

  isGroupChecked(groupId: string) {
    return !this.isAllGroupsSelected && this.selectedGroupIds.includes(groupId)
  }

  async loadProjectWorkspace(refreshIngestionTotals = false) {
    this.isProjectLoading = true

    try {
      const [loadedProjects, loadedStatuses] = await Promise.all([
        this.api.getProjects(),
        this.api.getIngestionStatuses(refreshIngestionTotals),
      ])

      this.projects = loadedProjects
      this.ingestionStatuses = loadedStatuses
    } catch (requestError) {
      this.projectError = requestError instanceof Error ? requestError.message : 'Unable to load application configuration data.'
    } finally {
      this.isProjectLoading = false
    }
  }

  handleEditProject(project: ProjectConfig) {
    this.editingProjectId = project.projectId
    this.projectForm = {
      projectId: project.projectId,
      projectName: project.projectName,
      knowledgeLookbackYears: project.knowledgeLookbackYears,
      similaritySearchLimit: project.similaritySearchLimit,
      sendEmail: project.sendEmail,
      poolingEnabled: project.poolingEnabled,
      emailRecipients: project.emailRecipients,
      applicationFilter: project.applicationFilter,
    }
    this.projectNotice = `Editing ${project.projectName}.`
    this.projectError = ''
  }

  cancelProjectEdit() {
    this.resetProjectForm()
    this.projectNotice = ''
    this.projectError = ''
  }

  async handleProjectSubmit() {
    const payload: ProjectConfigInput = {
      ...this.projectForm,
      projectId: this.editingProjectId ? this.projectForm.projectId.trim() : '',
      projectName: this.projectForm.projectName.trim(),
      emailRecipients: this.projectForm.emailRecipients.trim(),
      applicationFilter: this.projectForm.applicationFilter.trim(),
    }

    if (!payload.projectName) {
      this.projectError = 'Application name is required.'
      return
    }

    this.projectError = ''
    this.projectNotice = ''
    this.isProjectSaving = true

    try {
      if (this.editingProjectId) {
        await this.api.updateProject(this.editingProjectId, payload)
        this.projectNotice = `Updated ${payload.projectName}.`
      } else {
        await this.api.createProject(payload)
        this.projectNotice = `Created ${payload.projectName}.`
      }

      this.resetProjectForm()
      await this.loadProjectWorkspace(true)
    } catch (requestError) {
      this.projectError = requestError instanceof Error ? requestError.message : 'Unable to save the application configuration.'
    } finally {
      this.isProjectSaving = false
    }
  }

  async handleApplicationFeedbackSubmit() {
    const payload: ApplicationFeedbackRequest = {
      userName: this.applicationFeedbackForm.userName.trim(),
      userEmail: this.applicationFeedbackForm.userEmail.trim(),
      feedbackType: this.applicationFeedbackForm.feedbackType.trim(),
      message: this.applicationFeedbackForm.message.trim(),
    }

    if (!payload.userName || !payload.userEmail || !payload.message) {
      this.applicationFeedbackError = 'Name, email, and feedback details are required.'
      return
    }

    this.applicationFeedbackError = ''
    this.applicationFeedbackNotice = ''
    this.isApplicationFeedbackSaving = true

    try {
      await this.api.submitApplicationFeedback(payload)
      this.applicationFeedbackNotice = 'Feedback submitted successfully.'
      this.applicationFeedbackForm = createDefaultApplicationFeedbackForm()
    } catch (requestError) {
      this.applicationFeedbackError = requestError instanceof Error ? requestError.message : 'Unable to submit application feedback.'
    } finally {
      this.isApplicationFeedbackSaving = false
    }
  }

  getProjectStatus(project: ProjectConfig): IngestionStatus {
    return this.statusByProjectId.get(project.projectId) ?? {
      projectId: project.projectId,
      ingested: 0,
      total: 0,
      progressPercentage: 0,
    }
  }

  clampPercentage(value: number) {
    return Math.max(0, Math.min(100, value))
  }

  truncateResolution(resolution: string) {
    if (!resolution) return 'No resolution'
    return resolution.length > 60 ? `${resolution.slice(0, 60).trimEnd()}...` : resolution
  }

  getRetrievedTicketIds(message: AssistantMessage) {
    return message.result.similarIncidents.map((incident) => incident.ticketId).filter((ticketId) => ticketId.trim().length > 0)
  }

  getFeedbackState(messageId: number) {
    this.feedbackStateByMessageId[messageId] ??= createFeedbackState()

    return this.feedbackStateByMessageId[messageId]
  }

  isFeedbackDisabled(message: AssistantMessage) {
    const state = this.getFeedbackState(message.id)
    return state.submitted || state.isSubmitting || this.getRetrievedTicketIds(message).length === 0
  }

  isFeedbackSubmitDisabled(message: AssistantMessage) {
    return this.isFeedbackDisabled(message) || this.getFeedbackState(message.id).selectedTicketId.trim().length === 0
  }

  getFeedbackSubmittedLabel(messageId: number) {
    const state = this.getFeedbackState(messageId)
    const targetLabel = state.selectedTicketId ? ` for ${state.selectedTicketId}` : ''

    if (!state.submitted) {
      return ''
    }

    if (state.selectedFeedbackType === 1) {
      return state.wasUsed
        ? `Feedback submitted${targetLabel}: marked helpful and used in resolution.`
        : `Feedback submitted${targetLabel}: marked helpful.`
    }

    return `Feedback submitted${targetLabel}: marked not helpful.`
  }

  async submitMessageFeedback(message: AssistantMessage, feedbackType: number) {
    if (this.isFeedbackSubmitDisabled(message)) {
      if (!this.isFeedbackDisabled(message)) {
        this.getFeedbackState(message.id).error = 'Select the primary incident for this feedback.'
      }

      return
    }

    const state = this.getFeedbackState(message.id)
    state.isSubmitting = true
    state.error = ''

    try {
      const wasUsed = feedbackType === 1 && state.wasUsed

      await this.api.submitFeedback({
        query: message.query,
        suggestedResolution: message.result.suggestedResolution,
        feedbackType,
        wasUsed,
        comment: state.comment.trim() || undefined,
        selectedTicketId: state.selectedTicketId,
        retrievedTicketIds: this.getRetrievedTicketIds(message),
      })

      state.wasUsed = wasUsed
      state.selectedFeedbackType = feedbackType
      state.submitted = true
    } catch (requestError) {
      state.error = requestError instanceof Error ? requestError.message : 'Unable to submit feedback.'
    } finally {
      state.isSubmitting = false
    }
  }

  trackMessage(_index: number, message: ChatMessage) {
    return message.id
  }

  trackIncident(_index: number, incident: SimilarIncident) {
    return incident.ticketId
  }

  private async loadProjectGroups() {
    try {
      this.groups = await this.api.getProjectGroups()
    } catch {
      this.groups = []
    }
  }

  private resetProjectForm() {
    this.editingProjectId = null
    this.projectForm = createDefaultProjectForm()
  }

  private applyTheme() {
    document.documentElement.dataset['theme'] = this.isDark ? 'dark' : 'light'
    localStorage.setItem('theme', this.isDark ? 'dark' : 'light')
  }
}