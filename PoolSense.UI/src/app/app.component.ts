import { CommonModule } from '@angular/common'
import { Component, OnInit, inject } from '@angular/core'
import { FormsModule } from '@angular/forms'
import {
  ApplicationFeedbackRequest,
  AuthenticatedUser,
  ApiService,
  DeploymentInfo,
  IngestionStatus,
  PoolReport,
  PoolRecommendationReportListItem,
  PoolRecommendationReportListResponse,
  PoolTroubleshootResponse,
  ProjectConfig,
  ProjectConfigInput,
  ProjectGroup,
  SimilarIncident,
  TicketAutomationSettings,
  TicketAutomationSettingsInput,
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

type PoolTroubleshootEntry = PoolTroubleshootResponse & {
  id: number
}

type FeedbackState = {
  currentPoolResolutionNote: string
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

type AppSection = 'main' | 'poolRecommendations' | 'projectConfig' | 'applicationFeedback'

type ApplicationFeedbackForm = ApplicationFeedbackRequest
type TicketAutomationSettingsForm = TicketAutomationSettingsInput
type PoolRecommendationEmailFilter = 'all' | 'sent' | 'notSent'
type PoolRecommendationDateRange = '7' | '30' | '90' | 'all'

const quickPrompts = ['VG item missing', 'Data load job failed', 'UI error']
const poolTroubleshootPrompts = [
  'First checks',
  'Validation checklist',
  'Compare similar incidents',
  'Draft user update',
  'Escalation summary',
]

const defaultTicketAutomationSettings: TicketAutomationSettings = {
  pollingEnabled: environment.ticketAutomation.pollingEnabled,
  pollIntervalSeconds: environment.ticketAutomation.pollIntervalSeconds,
  poolSenseEmail: environment.ticketAutomation.poolSenseEmail,
  closedStatusName: environment.ticketAutomation.closedStatusName,
  newStatusName: environment.ticketAutomation.newStatusName,
  similaritySearchLimit: environment.ticketAutomation.similaritySearchLimit,
  email: {
    recipient: '',
    fromAddress: environment.ticketAutomation.email.fromAddress,
    deliveryMode: environment.ticketAutomation.email.deliveryMode,
    smtpHost: environment.ticketAutomation.email.smtpHost,
    port: environment.ticketAutomation.email.port,
    timeoutMs: environment.ticketAutomation.email.timeoutMs,
    databaseMailProfile: environment.ticketAutomation.email.databaseMailProfile,
  },
}

const defaultProjectForm: ProjectConfigInput = {
  projectId: '',
  projectName: '',
  knowledgeLookbackYears: environment.projectDefaults.knowledgeLookbackYears,
  similaritySearchLimit: environment.projectDefaults.similaritySearchLimit,
  sendEmail: environment.projectDefaults.sendEmail,
  poolingEnabled: environment.projectDefaults.poolingEnabled,
  emailRecipients: environment.projectDefaults.emailRecipients,
  applicationFilter: '',
  nyraKbNames: '',
}

function createDefaultProjectForm(): ProjectConfigInput {
  return { ...defaultProjectForm }
}

function createTicketAutomationSettingsForm(settings: TicketAutomationSettings = defaultTicketAutomationSettings): TicketAutomationSettingsForm {
  return {
    pollingEnabled: settings.pollingEnabled,
    pollIntervalSeconds: settings.pollIntervalSeconds,
    poolSenseEmail: settings.poolSenseEmail,
  }
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
    currentPoolResolutionNote: '',
    isSubmitting: false,
    submitted: false,
    error: '',
    selectedFeedbackType: null,
    wasUsed: false,
    selectedTicketId,
  }
}

function trimUserValue(value: string | null | undefined) {
  return value?.trim() ?? ''
}

function getAuthenticatedUserName(user: AuthenticatedUser) {
  return trimUserValue(user.displayName) || trimUserValue(user.username) || trimUserValue(user.authPrincipal)
}

function getAuthenticatedUserEmail(user: AuthenticatedUser) {
  const explicitEmail = trimUserValue(user.email)
  if (explicitEmail) {
    return explicitEmail
  }

  const principal = trimUserValue(user.authPrincipal)
  if (principal.includes('@')) {
    return principal
  }

  const username = trimUserValue(user.username)
  if (!username) {
    return ''
  }

  if (username.includes('@')) {
    return username
  }

  const cleanUsername = username.split('\\').filter(Boolean).pop() ?? username
  return `${cleanUsername}@intel.com`
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

function getInitialPoolReportId() {
  const match = window.location.pathname.match(/^\/Pool\/([^/?#]+)/i)
  if (!match?.[1]) {
    return ''
  }

  try {
    return decodeURIComponent(match[1]).trim()
  } catch {
    return match[1].trim()
  }
}

function createDefaultPoolRecommendationResponse(): PoolRecommendationReportListResponse {
  return {
    items: [],
    totalCount: 0,
    page: 1,
    pageSize: 25,
  }
}

function formatDateInput(date: Date) {
  return date.toISOString().slice(0, 10)
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
  readonly poolTroubleshootPrompts = poolTroubleshootPrompts
  readonly allGroupValue = '__all__'
  readonly appVersion = environment.appVersion
  readonly appSettings = environment

  currentUser: AuthenticatedUser | null = null
  isSessionLoading = true
  loginForm = {
    username: '',
    password: '',
    rememberMe: false,
  }
  isLoginSubmitting = false
  loginError = ''
  messages: ChatMessage[] = []
  insights: TicketWorkflowResult | null = null
  isLoading = false
  error = ''
  input = ''
  groups: ProjectGroup[] = []
  selectedGroupIds: string[] = []
  selectedGroupScopeValues: string[] = [this.allGroupValue]
  activeSection: AppSection = 'main'
  isSidebarCollapsed = true
  isDark = localStorage.getItem('theme') === 'dark'
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
  ticketAutomationSettings = defaultTicketAutomationSettings
  ticketAutomationForm = createTicketAutomationSettingsForm()
  deploymentInfo: DeploymentInfo | null = null
  isTicketAutomationSaving = false
  poolReport: PoolReport | null = null
  poolReportSourceEventId = getInitialPoolReportId()
  isPoolReportLoading = false
  poolReportError = ''
  poolTroubleshootQuestion = ''
  poolTroubleshootEntries: PoolTroubleshootEntry[] = []
  isPoolTroubleshootLoading = false
  poolTroubleshootError = ''
  poolRecommendationReports = createDefaultPoolRecommendationResponse()
  poolRecommendationFilters = {
    projectId: '',
    searchTerm: '',
    emailSent: 'all' as PoolRecommendationEmailFilter,
    dateRange: '30' as PoolRecommendationDateRange,
    pageSize: 25,
  }
  isPoolRecommendationLoading = false
  poolRecommendationError = ''
  feedbackStateByMessageId: Record<number, FeedbackState> = {}

  ngOnInit() {
    this.applyTheme()
    void this.initializeSession()
  }

  get currentUserDisplayName() {
    return this.currentUser?.displayName || this.currentUser?.username || 'Signed in'
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

  get deploymentLabel() {
    return this.resolvedDeploymentInfo?.environmentLabel || (this.appSettings.production ? 'PROD' : 'DEV')
  }

  get deploymentContextLabel() {
    const deployment = this.resolvedDeploymentInfo
    if (!deployment) {
      return this.appSettings.ticketAutomation.sourceDatabaseName
    }

    return [deployment.poolSenseDatabaseName || deployment.ticketSourceDatabaseName]
      .filter((value) => value && value.trim().length > 0)
      .join(' · ')
  }

  get ticketSourceDatabaseName() {
    return this.resolvedDeploymentInfo?.ticketSourceDatabaseName || this.appSettings.ticketAutomation.sourceDatabaseName
  }

  private get resolvedDeploymentInfo() {
    return this.ticketAutomationSettings.deployment ?? this.deploymentInfo
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

  get poolRecommendationTotalPages() {
    return Math.max(1, Math.ceil(this.poolRecommendationReports.totalCount / this.poolRecommendationReports.pageSize))
  }

  get poolRecommendationRangeLabel() {
    if (this.poolRecommendationReports.totalCount === 0) {
      return '0 reports'
    }

    const start = (this.poolRecommendationReports.page - 1) * this.poolRecommendationReports.pageSize + 1
    const end = Math.min(this.poolRecommendationReports.totalCount, start + this.poolRecommendationReports.items.length - 1)
    return `${start}-${end} of ${this.poolRecommendationReports.totalCount}`
  }

  get poolRecommendationProjectOptions() {
    return this.projects.filter((project) => project.applicationFilter.trim().length > 0)
  }

  get isPoolReportWorkspace() {
    return this.poolReportSourceEventId.trim().length > 0 || this.poolReport !== null || this.isPoolReportLoading
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

  async handleLoginSubmit() {
    const username = this.loginForm.username.trim()
    const password = this.loginForm.password

    if (!username || !password || this.isLoginSubmitting) {
      this.loginError = 'Username and password are required.'
      return
    }

    this.loginError = ''
    this.isLoginSubmitting = true

    try {
      const loginResult = await this.api.login(username, password, this.loginForm.rememberMe)
      this.currentUser = loginResult.user
      this.loginForm = {
        username: '',
        password: '',
        rememberMe: false,
      }
      this.applyAuthenticatedUserToFeedbackForm(loginResult.user)
      this.startAuthenticatedWorkspaceLoad()
    } catch (requestError) {
      this.loginError = requestError instanceof Error ? requestError.message : 'Unable to sign in.'
    } finally {
      this.isLoginSubmitting = false
    }
  }

  async handleLogout() {
    await this.api.logout()
    this.currentUser = null
    this.messages = []
    this.insights = null
    this.projects = []
    this.ingestionStatuses = []
    this.groups = []
    this.setSelectedGroupIds([])
    this.error = ''
    this.projectError = ''
    this.projectNotice = ''
    this.poolReport = null
    this.poolReportSourceEventId = getInitialPoolReportId()
    this.poolReportError = ''
    this.isPoolReportLoading = false
    this.poolTroubleshootQuestion = ''
    this.poolTroubleshootEntries = []
    this.poolTroubleshootError = ''
    this.isPoolTroubleshootLoading = false
    this.poolRecommendationReports = createDefaultPoolRecommendationResponse()
    this.poolRecommendationError = ''
    this.isPoolRecommendationLoading = false
    this.feedbackStateByMessageId = {}
  }

  handleComposerKeydown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault()
      void this.handleSend()
    }
  }

  handlePoolTroubleshootKeydown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault()
      void this.handlePoolTroubleshootSubmit()
    }
  }

  toggleTheme() {
    this.isDark = !this.isDark
    this.applyTheme()
  }

  setActiveSection(section: AppSection) {
    this.activeSection = section
    if (section === 'main') {
      if (/^\/Pool\//i.test(window.location.pathname)) {
        window.history.pushState({}, '', '/')
      }

      if (this.poolReportSourceEventId || this.poolReport || this.isPoolReportLoading) {
        this.clearPoolReportWorkspace()
      }
    }

    if (section === 'poolRecommendations' && this.currentUser) {
      void this.loadPoolRecommendations()
    }
  }

  toggleSidebar() {
    this.isSidebarCollapsed = !this.isSidebarCollapsed
  }

  handleAllGroupChange(checked: boolean) {
    this.setSelectedGroupIds(checked ? [] : this.groups.map((group) => group.groupId))
  }

  handleGroupChange(groupId: string, checked: boolean) {
    const next = checked
      ? [...this.selectedGroupIds, groupId]
      : this.selectedGroupIds.filter((selectedGroupId) => selectedGroupId !== groupId)

    this.setSelectedGroupIds(next.length === 0 ? [] : next)
  }

  isGroupChecked(groupId: string) {
    return !this.isAllGroupsSelected && this.selectedGroupIds.includes(groupId)
  }

  handleGroupMultiSelectChange(selectedValues: string[] | string) {
    const values = Array.isArray(selectedValues) ? selectedValues : [selectedValues]
    const normalizedValues = values
      .map((value) => value.trim())
      .filter((value) => value.length > 0)

    if (normalizedValues.length === 0 || normalizedValues.includes(this.allGroupValue)) {
      this.setSelectedGroupIds([])
      return
    }

    const validGroupIds = new Set(this.groups.map((group) => group.groupId))
    this.setSelectedGroupIds(Array.from(new Set(normalizedValues.filter((value) => validGroupIds.has(value)))))
  }

  private setSelectedGroupIds(groupIds: string[]) {
    this.selectedGroupIds = groupIds
    this.selectedGroupScopeValues = groupIds.length === 0 ? [this.allGroupValue] : [...groupIds]
  }

  private clearPoolReportWorkspace() {
    this.poolReport = null
    this.poolReportSourceEventId = ''
    this.isPoolReportLoading = false
    this.poolReportError = ''
    this.poolTroubleshootQuestion = ''
    this.poolTroubleshootEntries = []
    this.poolTroubleshootError = ''
    this.isPoolTroubleshootLoading = false
    this.messages = []
    this.insights = null
  }

  async loadProjectWorkspace(refreshIngestionTotals = false) {
    if (!this.currentUser) {
      this.isProjectLoading = false
      return
    }

    this.isProjectLoading = true

    try {
      const [loadedProjects, loadedStatuses, loadedTicketAutomationSettings] = await Promise.all([
        this.api.getProjects(),
        this.api.getIngestionStatuses(refreshIngestionTotals),
        this.api.getTicketAutomationSettings(),
      ])

      this.projects = loadedProjects
      this.ingestionStatuses = loadedStatuses
      this.ticketAutomationSettings = loadedTicketAutomationSettings
      this.deploymentInfo = loadedTicketAutomationSettings.deployment ?? this.deploymentInfo
      this.ticketAutomationForm = createTicketAutomationSettingsForm(loadedTicketAutomationSettings)
    } catch (requestError) {
      this.projectError = requestError instanceof Error ? requestError.message : 'Unable to load application configuration data.'
    } finally {
      this.isProjectLoading = false
    }
  }

  async handleTicketAutomationSubmit() {
    const payload: TicketAutomationSettingsInput = {
      pollingEnabled: this.ticketAutomationForm.pollingEnabled,
      pollIntervalSeconds: Number(this.ticketAutomationForm.pollIntervalSeconds),
      poolSenseEmail: this.ticketAutomationForm.poolSenseEmail,
    }

    if (!Number.isFinite(payload.pollIntervalSeconds) || payload.pollIntervalSeconds < 10 || payload.pollIntervalSeconds > 3600) {
      this.projectError = 'Poll interval must be between 10 and 3600 seconds.'
      this.projectNotice = ''
      return
    }

    this.projectError = ''
    this.projectNotice = ''
    this.isTicketAutomationSaving = true

    try {
      this.ticketAutomationSettings = await this.api.updateTicketAutomationSettings(payload)
      this.ticketAutomationForm = createTicketAutomationSettingsForm(this.ticketAutomationSettings)
      this.projectNotice = 'Updated application-level polling settings.'
    } catch (requestError) {
      this.projectError = requestError instanceof Error ? requestError.message : 'Unable to save the application-level polling settings.'
    } finally {
      this.isTicketAutomationSaving = false
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
      nyraKbNames: project.nyraKbNames,
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
      projectId: this.editingProjectId ? this.editingProjectId.trim() : '',
      projectName: this.projectForm.projectName.trim(),
      emailRecipients: this.projectForm.emailRecipients.trim(),
      applicationFilter: this.projectForm.applicationFilter.trim(),
      nyraKbNames: this.projectForm.nyraKbNames.trim(),
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
      userName: (this.applicationFeedbackForm.userName || this.currentUser?.displayName || this.currentUser?.username || '').trim(),
      userEmail: (this.applicationFeedbackForm.userEmail || this.currentUser?.email || '').trim(),
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
      if (this.currentUser) {
        this.applyAuthenticatedUserToFeedbackForm(this.currentUser)
      }
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

  getCurrentIssueId(message: AssistantMessage) {
    const poolIssueId = (this.poolReport?.sourceEventId || this.poolReportSourceEventId || '').trim()
    if (poolIssueId) {
      return `pool:${poolIssueId}`
    }

    return `chat:${message.id}`
  }

  async submitMessageFeedback(message: AssistantMessage, feedbackType: number) {
    if (this.isFeedbackSubmitDisabled(message)) {
      if (!this.isFeedbackDisabled(message)) {
        this.getFeedbackState(message.id).error = 'Select the incident this feedback applies to.'
      }

      return
    }

    const state = this.getFeedbackState(message.id)
    const note = state.currentPoolResolutionNote.trim()

    if (feedbackType === -1 && !note) {
      state.error = 'Add the current pool cause or what went wrong — helps PoolSense avoid this path next time.'
      return
    }

    if (feedbackType === 1 && state.wasUsed && !note) {
      state.error = 'Add the confirmed fix you used — gives PoolSense stronger evidence for future similar issues.'
      return
    }

    state.isSubmitting = true
    state.error = ''

    try {
      const wasUsed = feedbackType === 1 && state.wasUsed

      await this.api.submitFeedback({
        query: message.query,
        suggestedResolution: message.result.suggestedResolution,
        feedbackType,
        wasUsed,
        currentPoolResolutionNote: state.currentPoolResolutionNote.trim() || undefined,
        currentIssueId: this.getCurrentIssueId(message),
        applyToTargetIncident: true,
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

  trackNyraDocument(_index: number, document: { documentId: string; sourceUrl: string; citation: string }) {
    return document.documentId || document.sourceUrl || document.citation
  }

  trackPoolRecommendation(_index: number, report: PoolRecommendationReportListItem) {
    return report.sourceEventId
  }

  async handlePoolRecommendationFilterChange() {
    await this.loadPoolRecommendations(1)
  }

  async refreshPoolRecommendations() {
    await this.loadPoolRecommendations(this.poolRecommendationReports.page)
  }

  async goToPoolRecommendationPage(page: number) {
    await this.loadPoolRecommendations(Math.max(1, Math.min(page, this.poolRecommendationTotalPages)))
  }

  openPoolRecommendation(report: PoolRecommendationReportListItem) {
    const reportUrl = report.reportUrl || `/Pool/${encodeURIComponent(report.sourceEventId)}`
    window.history.pushState({}, '', reportUrl)
    this.poolReportSourceEventId = report.sourceEventId
    void this.loadPoolReport(report.sourceEventId)
  }

  formatPercent(value: number) {
    return `${Math.round((Number.isFinite(value) ? value : 0) * 100)}%`
  }

  async handlePoolTroubleshootSubmit(rawQuestion = this.poolTroubleshootQuestion) {
    const question = this.resolvePoolTroubleshootQuestion(rawQuestion)
    const poolId = this.poolReport?.sourceEventId || this.poolReportSourceEventId

    if (!poolId || !question || this.isPoolTroubleshootLoading) {
      return
    }

    this.poolTroubleshootError = ''
    this.isPoolTroubleshootLoading = true

    try {
      const response = await this.api.troubleshootPool(poolId, question)
      this.poolTroubleshootEntries = [{ ...response, id: Date.now() }, ...this.poolTroubleshootEntries]
      this.poolTroubleshootQuestion = ''
    } catch (requestError) {
      this.poolTroubleshootError = requestError instanceof Error ? requestError.message : 'Unable to troubleshoot this pool.'
    } finally {
      this.isPoolTroubleshootLoading = false
    }
  }

  retryPoolReportLoad() {
    const poolId = this.poolReport?.sourceEventId || this.poolReportSourceEventId
    if (poolId) {
      void this.loadPoolReport(poolId)
    }
  }

  private async loadProjectGroups() {
    if (!this.currentUser) {
      this.groups = []
      return
    }

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

  private async initializeSession() {
    this.isSessionLoading = true

    try {
      this.deploymentInfo = await this.api.getDeploymentInfo()
    } catch {
      this.deploymentInfo = null
    }

    try {
      this.currentUser = await this.api.getSession()
      if (this.currentUser) {
        this.applyAuthenticatedUserToFeedbackForm(this.currentUser)
      }
    } catch (requestError) {
      this.loginError = requestError instanceof Error ? requestError.message : 'Unable to validate your session.'
      this.currentUser = null
    } finally {
      this.isSessionLoading = false
    }

    if (this.currentUser) {
      this.startAuthenticatedWorkspaceLoad()
    }
  }

  private startAuthenticatedWorkspaceLoad() {
    void (async () => {
      await this.loadAuthenticatedWorkspace()
      await this.loadInitialPoolReport()
    })()
  }

  private async loadAuthenticatedWorkspace() {
    await Promise.all([this.loadProjectGroups(), this.loadProjectWorkspace()])
  }

  private async loadPoolRecommendations(page = 1) {
    if (!this.currentUser) {
      return
    }

    this.isPoolRecommendationLoading = true
    this.poolRecommendationError = ''

    try {
      this.poolRecommendationReports = await this.api.getPoolRecommendations(this.buildPoolRecommendationFilters(page))
    } catch (requestError) {
      this.poolRecommendationError = requestError instanceof Error ? requestError.message : 'Unable to load Pool Recommendations.'
    } finally {
      this.isPoolRecommendationLoading = false
    }
  }

  private buildPoolRecommendationFilters(page: number) {
    const dateRange = this.poolRecommendationFilters.dateRange
    let from = ''
    if (dateRange !== 'all') {
      const fromDate = new Date()
      fromDate.setDate(fromDate.getDate() - Number(dateRange))
      from = formatDateInput(fromDate)
    }

    return {
      projectId: this.poolRecommendationFilters.projectId || undefined,
      searchTerm: this.poolRecommendationFilters.searchTerm.trim() || undefined,
      emailSent:
        this.poolRecommendationFilters.emailSent === 'sent'
          ? true
          : this.poolRecommendationFilters.emailSent === 'notSent'
            ? false
            : null,
      from: from || undefined,
      page,
      pageSize: Number(this.poolRecommendationFilters.pageSize) || 25,
    }
  }

  private async loadInitialPoolReport() {
    const poolReportId = getInitialPoolReportId()
    if (!poolReportId || !this.currentUser) {
      return
    }

    this.poolReportSourceEventId = poolReportId
    await this.loadPoolReport(poolReportId)
  }

  private async loadPoolReport(poolReportId: string) {
    this.activeSection = 'main'
    this.poolReport = null
    this.poolReportError = ''
    this.poolTroubleshootQuestion = ''
    this.poolTroubleshootEntries = []
    this.poolTroubleshootError = ''
    this.isPoolReportLoading = true

    try {
      const report = await this.api.getPoolReport(poolReportId)
      this.poolReport = report
      this.poolReportSourceEventId = report.sourceEventId || poolReportId
      this.applyPoolReportScope(report)
      if (report.isReady && report.workflowResult) {
        this.applyPoolReportToWorkspace(report)
      } else {
        this.messages = []
        this.insights = null
      }
    } catch (requestError) {
      this.poolReportError = requestError instanceof Error ? requestError.message : 'Unable to load the PoolSense report.'
    } finally {
      this.isPoolReportLoading = false
    }
  }

  private applyPoolReportToWorkspace(report: PoolReport) {
    const result = report.workflowResult
    if (!result) {
      return
    }

    const sourceEventId = report.sourceEventId || this.poolReportSourceEventId
    const userMessage: UserMessage = {
      id: Date.now(),
      role: 'user',
      text: `Pool ${sourceEventId}`,
    }
    const assistantMessage: AssistantMessage = {
      id: userMessage.id + 1,
      role: 'assistant',
      text: result.suggestedResolution,
      query: userMessage.text,
      result,
    }

    this.feedbackStateByMessageId[assistantMessage.id] = createFeedbackState(
      getDefaultFeedbackTicketId(result.similarIncidents),
    )
    this.messages = [userMessage, assistantMessage]
    this.insights = result
    this.input = ''
  }

  private applyPoolReportScope(report: PoolReport) {
    if (!this.poolReportSourceEventId || this.groups.length === 0) {
      return
    }

    const exactProjectMatch = report.projectId
      ? this.groups.find((group) => group.groupId.localeCompare(report.projectId, undefined, { sensitivity: 'accent' }) === 0)
      : null

    const targetNames = [report.projectName, report.application]
      .map((value) => value?.trim().toLowerCase() ?? '')
      .filter((value) => value.length > 0)

    const nameMatch =
      exactProjectMatch ||
      this.groups.find((group) => {
        const displayName = group.displayName.trim().toLowerCase()
        return targetNames.some((targetName) => displayName === targetName)
      })

    this.setSelectedGroupIds(nameMatch ? [nameMatch.groupId] : [])
  }

  private resolvePoolTroubleshootQuestion(rawQuestion: string) {
    const question = rawQuestion.trim()
    if (!question) {
      return ''
    }

    if (question === 'First checks') {
      return 'What should I validate first for this pool?'
    }

    if (question === 'Validation checklist') {
      return 'Create a step-by-step validation checklist for this pool.'
    }

    if (question === 'Compare similar incidents') {
      return 'Which similar incidents are most relevant, and what should I reuse from them?'
    }

    if (question === 'Draft user update') {
      return 'Draft a concise update I can send to the user for this pool.'
    }

    if (question === 'Escalation summary') {
      return 'Create an escalation summary with symptoms, likely cause, evidence, and next asks.'
    }

    return question
  }

  private applyAuthenticatedUserToFeedbackForm(user: AuthenticatedUser) {
    this.applicationFeedbackForm = {
      ...this.applicationFeedbackForm,
      userName: this.applicationFeedbackForm.userName.trim() || getAuthenticatedUserName(user),
      userEmail: this.applicationFeedbackForm.userEmail.trim() || getAuthenticatedUserEmail(user),
    }
  }

  private applyTheme() {
    document.documentElement.dataset['theme'] = this.isDark ? 'dark' : 'light'
    localStorage.setItem('theme', this.isDark ? 'dark' : 'light')
  }
}