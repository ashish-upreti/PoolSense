import { CommonModule } from '@angular/common'
import { Component, ElementRef, HostListener, OnDestroy, OnInit, ViewChild, inject } from '@angular/core'
import { FormsModule } from '@angular/forms'
import { RichResponseComponent } from './rich-response.component'
import { CodeSearchStatusComponent } from './code-search-status.component'
import {
  ApplicationFeedbackDailyCount,
  ApplicationFeedbackInsights,
  ApplicationFeedbackRequest,
  AuthenticatedUser,
  ApiService,
  CodeChatSessionSummary,
  CodeChatTurn,
  CodeEvidence,
  CodeSearchCoverage,
  CodeRepository,
  DeploymentInfo,
  IngestionStatus,
  NyraDocumentResult,
  PoolReport,
  PoolRecommendationReportListItem,
  PoolRecommendationReportListResponse,
  PoolChatSessionSummary,
  PoolTroubleshootResponse,
  ProjectConfig,
  ProjectConfigInput,
  ProjectGroup,
  SimilarIncident,
  TicketWorkflowProgress,
  TicketAutomationSettings,
  TicketAutomationSettingsInput,
  TicketWorkflowResult,
} from './api.service'
import { environment } from '../environments/environment'
import { CodeEvidenceComponent } from './code-evidence.component'
import { CodeRepositoriesComponent } from './code-repositories.component'
import { formatAnswerDuration } from './answer-duration'

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
  workflowProgress: TicketWorkflowProgress[]
  completedInMs?: number
}

type ChatMessage = UserMessage | AssistantMessage

type MainChatWorkspace = {
  key: string
  sessionId: string | null
  title: string
  scopeIds: string[]
  messages: ChatMessage[]
  insights: TicketWorkflowResult | null
  input: string
  error: string
  historyNotice: string
  contextNotice: string
  feedback: Record<number, FeedbackState>
  detailExpanded: Record<number, boolean>
  workflowExpanded: number | null
  evidenceTab: 'incidents' | 'nyra' | 'code'
  hydrated: boolean
  unlisted: boolean
  createdAt: string
  updatedAt: string
  pending?: { id: string; controller: AbortController; query: string; scopeIds: string[]; progress: TicketWorkflowProgress[] }
}

type PoolTroubleshootEntry = PoolTroubleshootResponse & {
  id: number
  workflowProgress: TicketWorkflowProgress[]
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

type AppSection = 'main' | 'poolRecommendations' | 'projectConfig' | 'codeRepositories' | 'applicationFeedback'

type GitHubConnectionStatus = { connected: boolean; login: string; shared?: boolean }

type CodeChatMessage = {
  id: number
  role: 'user' | 'assistant'
  text: string
  citations: CodeEvidence[]
  generatedAt: string
  isError?: boolean
  codeKnowledgeStatus?: string
  codeSearchCoverage?: CodeSearchCoverage
}

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
  githubUrls: '',
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

function createPoolTroubleshootEntry(response: PoolTroubleshootResponse, workflowProgress: TicketWorkflowProgress[] = []): PoolTroubleshootEntry {
  return {
    ...response,
    id: Date.now(),
    workflowProgress,
  }
}

function findLastStepIndex(steps: TicketWorkflowProgress[], predicate: (step: TicketWorkflowProgress) => boolean) {
  for (let index = steps.length - 1; index >= 0; index -= 1) {
    if (predicate(steps[index])) {
      return index
    }
  }

  return -1
}

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule, CodeEvidenceComponent, CodeRepositoriesComponent, RichResponseComponent, CodeSearchStatusComponent],
  templateUrl: './app.component.html',
})
export class AppComponent implements OnInit, OnDestroy {
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
  detailedAnalysisExpandedByMessageId: Record<number, boolean> = {}
  activeEvidenceTab: 'incidents' | 'nyra' | 'code' = 'incidents'
  isEvidencePanelCollapsed = false
  isOperationalContextCollapsed = false
  isTroubleshootPanelCollapsed = false
  expandedWorkflowActivityMessageId: number | null = null
  isLoading = false
  loadingQuery = ''
  loadingScopeGroupIds: string[] = []
  loadingProgressEvents: TicketWorkflowProgress[] = []
  error = ''
  input = ''
  groups: ProjectGroup[] = []
  selectedGroupIds: string[] = []
  scopeAddSelectValue = ''
  activeSection: AppSection = 'main'
  isSidebarCollapsed = true
  isDark = localStorage.getItem('theme') === 'dark'
  projects: ProjectConfig[] = []
  ingestionStatuses: IngestionStatus[] = []
  projectForm = createDefaultProjectForm()
  @ViewChild(CodeRepositoriesComponent) codeRepositories?: CodeRepositoriesComponent
  editingProjectId: string | null = null
  isProjectLoading = true
  isProjectSaving = false
  projectError = ''
  projectNotice = ''
  codeRepoProjectId = ''
  codeRepoUrl = ''
  codeRepoBranch = ''
  codeRepoBranches: string[] = []
  codeRepoBranchesUrl = ''
  codeRepoBranchesTruncated = false
  isCodeRepoBranchesLoading = false
  codeRepoList: CodeRepository[] = []
  codeRepoConnection: GitHubConnectionStatus | null = null
  codeRepoError = ''
  codeRepoNotice = ''
  isCodeRepoLoading = false
  isCodeRepoAdding = false
  codeRepoBusyId = ''
  private codeRepoTimer: number | null = null
  private codeRepoLoadVersion = 0
  @ViewChild('codeChatTranscript') codeChatTranscript?: ElementRef<HTMLElement>
  codeChatMessages: CodeChatMessage[] = []
  codeChatSessionId: string | null = null
  codeChatHistory: CodeChatSessionSummary[] = []
  codeChatQuestion = ''
  codeChatError = ''
  isCodeChatAsking = false
  isCodeChatHistoryLoading = false
  applicationFeedbackForm = createDefaultApplicationFeedbackForm()
  applicationFeedbackError = ''
  applicationFeedbackNotice = ''
  isApplicationFeedbackSaving = false
  applicationFeedbackInsights: ApplicationFeedbackInsights | null = null
  applicationFeedbackInsightsError = ''
  isApplicationFeedbackInsightsLoading = false
  applicationFeedbackTimelineDays = 30
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
  poolTroubleshootProgressEvents: TicketWorkflowProgress[] = []
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
  openFeedbackTargetMessageId: number | null = null
  chatHistorySummaries: PoolChatSessionSummary[] = []
  activeChatSessionId: string | null = null
  isChatHistoryLoading = false
  chatHistoryError = ''
  isChatHistoryCollapsed = false
  isChatHistoryDrawerOpen = false
  isNarrowViewport = false
  historyPersistenceNotice = ''
  chatContextNotice = ''
  isChatSessionOpening = false
  private readonly chatWorkspaces = new Map<string, MainChatWorkspace>()
  private activeChatKey = ''
  private chatAuthEpoch = 0
  private historySelectionVersion = 0
  private historyListVersion = 0
  private historyReadController?: AbortController
  private poolReportReadVersion = 0
  private nextChatMessageId = Date.now()
  private readonly removedChatSessionIds = new Set<string>()
  isSigningOut = false
  renamingChatSessionId: string | null = null
  renamingChatSessionTitle = ''
  private readonly narrowViewportQuery = window.matchMedia('(max-width: 1056px)')
  private readonly handleNarrowViewportChange = (event: MediaQueryListEvent) => {
    this.isNarrowViewport = event.matches
    if (!event.matches) {
      this.isChatHistoryDrawerOpen = false
    }
  }

  ngOnInit() {
    this.applyTheme()
    this.isNarrowViewport = this.narrowViewportQuery.matches
    this.narrowViewportQuery.addEventListener('change', this.handleNarrowViewportChange)
    void this.initializeSession()
  }

  ngOnDestroy() {
    this.invalidateChatRequests()
    this.narrowViewportQuery.removeEventListener('change', this.handleNarrowViewportChange)
  }

  get currentUserDisplayName() {
    return this.currentUser?.displayName || this.currentUser?.username || 'Signed in'
  }

  get isAllGroupsSelected() {
    return this.selectedGroupIds.length === 0
  }

  get scopeModeSelectValue() {
    if (this.isAllGroupsSelected) {
      return this.allGroupValue
    }

    // Only an exact single selection maps to a real <option>; otherwise fall back to a value
    // that matches no option so the native select doesn't default to highlighting "All Applications".
    return this.selectedGroupIds.length === 1 ? this.selectedGroupIds[0] : ''
  }

  get unselectedGroups() {
    return this.groups.filter((group) => !this.selectedGroupIds.includes(group.groupId))
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

  get usersToday() {
    return this.applicationFeedbackInsights?.usersToday ?? 0
  }

  get usersTodayDeltaLabel() {
    const insights = this.applicationFeedbackInsights
    if (!insights) {
      return 'Loading usage'
    }

    const yesterday = insights.usersYesterday
    if (yesterday === 0) {
      return this.usersToday === 0 ? '0%' : 'New'
    }

    const delta = Math.round(((this.usersToday - yesterday) / yesterday) * 100)
    if (delta === 0) {
      return '0%'
    }

    return `${delta > 0 ? '↑' : '↓'} ${Math.abs(delta)}%`
  }

  get isUsersTodayDeltaNegative() {
    return (this.applicationFeedbackInsights?.usersToday ?? 0) < (this.applicationFeedbackInsights?.usersYesterday ?? 0)
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

  get applicationFeedbackActiveApplications() {
    return this.projects.filter((project) => project.poolingEnabled).length
  }

  get applicationFeedbackIngestionProgress() {
    if (this.ingestionStatuses.length === 0) {
      return 0
    }

    const totalProgress = this.ingestionStatuses.reduce((sum, status) => sum + this.clampPercentage(status.progressPercentage), 0)
    return Math.round(totalProgress / this.ingestionStatuses.length)
  }

  get applicationFeedbackTimelineLabel() {
    return this.applicationFeedbackTimelineDays === 0 ? 'All time' : `Last ${this.applicationFeedbackTimelineDays} days`
  }

  get poolRecommendationProjectOptions() {
    return this.projects.filter((project) => project.applicationFilter.trim().length > 0)
  }

  get loadingStatusSteps() {
    return [...this.loadingProgressEvents].sort((first, second) => first.order - second.order)
  }

  get loadingActiveStep() {
    return [...this.loadingStatusSteps].reverse().find((step) => step.state === 'active') ?? this.loadingStatusSteps.at(-1) ?? null
  }

  get loadingStatusTitle() {
    return this.loadingActiveStep?.title ? `${this.loadingActiveStep.title}...` : 'Preparing request...'
  }

  get loadingStatusCopy() {
    return this.loadingActiveStep?.detail || `Submitting query for ${this.loadingScopeLabel}.`
  }

  get loadingScopeLabel() {
    if (this.loadingScopeGroupIds.length === 0) {
      return 'all applications'
    }

    return this.loadingScopeGroupIds.map((groupId) => this.getGroupDisplayName(groupId)).join(', ')
  }

  get loadingProgressPercent() {
    const steps = this.loadingStatusSteps
    if (steps.length <= 1) {
      return 0
    }

    const activeIndex = Math.max(0, findLastStepIndex(steps, (step) => step.state === 'active'))
    const completedIndex = Math.max(0, findLastStepIndex(steps, (step) => step.state === 'completed' || step.state === 'skipped'))
    return Math.round((Math.max(activeIndex, completedIndex) / (steps.length - 1)) * 100)
  }

  get poolTroubleshootStatusSteps() {
    return [...this.poolTroubleshootProgressEvents].sort((first, second) => first.order - second.order)
  }

  get poolTroubleshootActiveStep() {
    return [...this.poolTroubleshootStatusSteps].reverse().find((step) => step.state === 'active') ?? this.poolTroubleshootStatusSteps.at(-1) ?? null
  }

  get poolTroubleshootStatusTitle() {
    return this.poolTroubleshootActiveStep?.title ? `${this.poolTroubleshootActiveStep.title}...` : 'Preparing troubleshoot request...'
  }

  get poolTroubleshootStatusCopy() {
    return this.poolTroubleshootActiveStep?.detail || 'Working through the saved report and fresh evidence.'
  }

  get poolTroubleshootProgressPercent() {
    const steps = this.poolTroubleshootStatusSteps
    if (steps.length <= 1) {
      return 0
    }

    const activeIndex = Math.max(0, findLastStepIndex(steps, (step) => step.state === 'active'))
    const completedIndex = Math.max(0, findLastStepIndex(steps, (step) => step.state === 'completed' || step.state === 'skipped'))
    return Math.round((Math.max(activeIndex, completedIndex) / (steps.length - 1)) * 100)
  }

  get isPoolReportWorkspace() {
    return this.poolReportSourceEventId.trim().length > 0 || this.poolReport !== null || this.isPoolReportLoading
  }

  private createChatWorkspace(sessionId: string | null = null, title = 'New chat', scopeIds = this.selectedGroupIds): MainChatWorkspace {
    const now = new Date().toISOString()
    const workspace: MainChatWorkspace = {
      key: 'local-' + crypto.randomUUID(), sessionId, title, scopeIds: [...scopeIds],
      messages: [], insights: null, input: '', error: '', historyNotice: '', contextNotice: '',
      feedback: {}, detailExpanded: {}, workflowExpanded: null, evidenceTab: 'incidents',
      hydrated: sessionId === null, unlisted: false, createdAt: now, updatedAt: now,
    }
    this.chatWorkspaces.set(workspace.key, workspace)
    return workspace
  }

  private findChatWorkspace(id: string) {
    return this.chatWorkspaces.get(id) ?? [...this.chatWorkspaces.values()].find(workspace => workspace.sessionId === id)
  }

  private captureChatWorkspace(): MainChatWorkspace {
    let workspace = this.chatWorkspaces.get(this.activeChatKey)
    if (!workspace) {
      workspace = this.createChatWorkspace(this.activeChatSessionId)
      this.activeChatKey = workspace.key
    }
    if (!this.isPoolReportWorkspace) {
      Object.assign(workspace, {
        messages: this.messages, insights: this.insights, input: this.input, error: this.error,
        scopeIds: [...this.selectedGroupIds], historyNotice: this.historyPersistenceNotice,
        contextNotice: this.chatContextNotice, feedback: this.feedbackStateByMessageId,
        detailExpanded: this.detailedAnalysisExpandedByMessageId, workflowExpanded: this.expandedWorkflowActivityMessageId,
        evidenceTab: this.activeEvidenceTab,
      })
    }
    return workspace
  }

  private projectChatWorkspace(workspace: MainChatWorkspace) {
    this.activeChatKey = workspace.key
    this.activeChatSessionId = workspace.sessionId || (workspace.messages.length > 0 ? workspace.key : null)
    this.messages = workspace.messages
    this.insights = workspace.insights
    this.input = workspace.input
    this.error = workspace.error
    this.setSelectedGroupIds(workspace.scopeIds)
    this.historyPersistenceNotice = workspace.historyNotice
    this.chatContextNotice = workspace.contextNotice
    this.feedbackStateByMessageId = workspace.feedback
    this.detailedAnalysisExpandedByMessageId = workspace.detailExpanded
    this.expandedWorkflowActivityMessageId = workspace.workflowExpanded
    this.activeEvidenceTab = workspace.evidenceTab
    this.openFeedbackTargetMessageId = null
    this.isLoading = !!workspace.pending
    this.loadingQuery = workspace.pending?.query || ''
    this.loadingScopeGroupIds = workspace.pending?.scopeIds || []
    this.loadingProgressEvents = workspace.pending?.progress || []
  }

  private cancelHistoryRead() {
    this.historySelectionVersion++
    this.historyReadController?.abort()
    this.historyReadController = undefined
    this.isChatSessionOpening = false
  }

  private invalidateChatRequests() {
    this.chatAuthEpoch++
    this.poolReportReadVersion++
    this.historyListVersion++
    this.cancelHistoryRead()
    for (const workspace of this.chatWorkspaces.values()) workspace.pending?.controller.abort()
    this.chatWorkspaces.clear()
    this.removedChatSessionIds.clear()
    this.activeChatKey = ''
    this.activeChatSessionId = null
    this.messages = []
    this.insights = null
    this.input = ''
    this.error = ''
    this.isLoading = false
    this.loadingQuery = ''
    this.loadingScopeGroupIds = []
    this.loadingProgressEvents = []
    this.chatHistorySummaries = []
    this.isChatHistoryLoading = false
    this.feedbackStateByMessageId = {}
    this.detailedAnalysisExpandedByMessageId = {}
    this.historyPersistenceNotice = ''
    this.chatContextNotice = ''
  }

  private workspaceSummary(workspace: MainChatWorkspace): PoolChatSessionSummary {
    const previous = this.chatHistorySummaries.find(summary => summary.id === workspace.sessionId)
    return {
      id: workspace.sessionId || workspace.key, title: workspace.title,
      selectedGroupIds: workspace.scopeIds, isPinned: previous?.isPinned ?? false,
      createdAt: previous?.createdAt || workspace.createdAt, updatedAt: workspace.updatedAt, messageCount: workspace.messages.length,
    }
  }

  isChatSessionPending(id: string) { return !!this.findChatWorkspace(id)?.pending }
  isLocalChatSession(id: string) { return id.startsWith('local-') }
  trackChatSessionId(_index: number, session: PoolChatSessionSummary) { return session.id }

  async handleSend(rawMessage = this.input) {
    const message = rawMessage.trim()
    if (!message || this.isLoading || this.isChatSessionOpening || !this.currentUser || this.isSigningOut) return
    const origin = this.captureChatWorkspace()
    if ([...this.chatWorkspaces.values()].filter(workspace => workspace.pending).length >= 3) {
      origin.error = 'Three chats are already investigating. You can keep switching conversations; wait for one to finish before starting another request.'
      this.projectChatWorkspace(origin)
      return
    }
    const owner = this.currentUser.username
    const epoch = this.chatAuthEpoch
    const requestId = crypto.randomUUID()
    const controller = new AbortController()
    const scope = [...origin.scopeIds]
    const sessionId = origin.sessionId
    const userId = ++this.nextChatMessageId
    const assistantId = ++this.nextChatMessageId
    const requestStartedAt = performance.now()
    if (!origin.messages.length) origin.title = message.replace(/\s+/g, ' ').slice(0, 80)
    origin.messages = [...origin.messages, { id: userId, role: 'user', text: message }]
    origin.error = ''
    origin.updatedAt = new Date().toISOString()
    origin.pending = {
      id: requestId, controller, query: message, scopeIds: scope,
      progress: [{ stage: 'request', title: 'Submitting request', detail: 'Sending query for the captured application scope.', state: 'completed', order: 0, timestampUtc: origin.updatedAt }],
    }
    this.projectChatWorkspace(origin)
    const valid = () => this.chatAuthEpoch === epoch && this.currentUser?.username === owner &&
      this.chatWorkspaces.get(origin.key) === origin && origin.pending?.id === requestId
    const visible = () => this.activeChatKey === origin.key && !this.isPoolReportWorkspace
    try {
      const result = await this.api.askPoolSenseWithProgress(message, scope, progress => {
        if (!valid()) return
        origin.pending!.progress = [...origin.pending!.progress.filter(event => event.stage !== progress.stage), progress]
          .sort((a, b) => a.order - b.order)
        if (visible()) this.loadingProgressEvents = origin.pending!.progress
      }, sessionId, controller.signal)
      if (!valid()) return
      const completedInMs = performance.now() - requestStartedAt
      if (visible()) this.captureChatWorkspace()
      const assistant: AssistantMessage = {
        id: assistantId, role: 'assistant', text: result.suggestedResolution, query: message, result,
        workflowProgress: [...origin.pending!.progress].sort((a, b) => a.order - b.order),
        completedInMs,
      }
      origin.messages = [...origin.messages, assistant]
      origin.feedback[assistantId] = createFeedbackState(getDefaultFeedbackTicketId(result.similarIncidents))
      origin.insights = result
      origin.sessionId = result.chatSessionId || sessionId
      origin.historyNotice = result.historyPersistenceError || ''
      origin.contextNotice = ''
      origin.input = ''
      origin.updatedAt = new Date().toISOString()
      if (origin.sessionId) {
        origin.unlisted = true
        const summary = this.workspaceSummary(origin)
        this.chatHistorySummaries = [...this.chatHistorySummaries.filter(item => item.id !== summary.id), summary]
      }
      if (visible()) this.projectChatWorkspace(origin)
      void this.loadChatHistory()
    } catch (requestError) {
      if (!valid() || controller.signal.aborted) return
      origin.error = requestError instanceof Error ? requestError.message : 'Unable to reach PoolSense.'
      if (visible()) this.error = origin.error
    } finally {
      if (valid()) {
        origin.pending = undefined
        if (visible()) this.projectChatWorkspace(origin)
      }
    }
  }

  private upsertPoolTroubleshootProgress(progress: TicketWorkflowProgress) {
    const nextEvents = this.poolTroubleshootProgressEvents.filter((event) => event.stage !== progress.stage)
    this.poolTroubleshootProgressEvents = [...nextEvents, progress]
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
      this.invalidateChatRequests()
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
    if (this.isSigningOut) return
    this.isSigningOut = true
    this.invalidateChatRequests()
    try { await this.api.logout() }
    catch (requestError) {
      this.error = requestError instanceof Error ? requestError.message : 'Unable to sign out.'
      this.isSigningOut = false
      return
    }
    this.isSigningOut = false
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
    this.poolTroubleshootProgressEvents = []
    this.poolRecommendationReports = createDefaultPoolRecommendationResponse()
    this.poolRecommendationError = ''
    this.isPoolRecommendationLoading = false
    this.feedbackStateByMessageId = {}
    this.applicationFeedbackInsights = null
    this.applicationFeedbackInsightsError = ''
    this.chatHistorySummaries = []
    this.chatContextNotice = ''
    this.activeChatSessionId = null
    this.chatHistoryError = ''
    this.historyPersistenceNotice = ''
    this.isChatHistoryDrawerOpen = false
    this.renamingChatSessionId = null
    this.renamingChatSessionTitle = ''
  }

  async loadChatHistory() {
    if (!this.currentUser) {
      return
    }

    this.isChatHistoryLoading = true
    this.chatHistoryError = ''
    const epoch = this.chatAuthEpoch
    const owner = this.currentUser.username
    const version = ++this.historyListVersion

    try {
      const summaries = await this.api.listPoolChatSessions()
      if (epoch !== this.chatAuthEpoch || this.currentUser?.username !== owner || version !== this.historyListVersion) return
      this.chatHistorySummaries = summaries.filter(summary => !this.removedChatSessionIds.has(summary.id))
      for (const workspace of this.chatWorkspaces.values()) {
        const summary = this.chatHistorySummaries.find(item => item.id === workspace.sessionId)
        if (summary) {
          workspace.title = summary.title
          workspace.unlisted = false
        } else if (workspace.sessionId && workspace.unlisted && !this.removedChatSessionIds.has(workspace.sessionId)) {
          this.chatHistorySummaries.push(this.workspaceSummary(workspace))
        }
      }
    } catch (requestError) {
      if (epoch !== this.chatAuthEpoch || this.currentUser?.username !== owner || version !== this.historyListVersion) {
        return
      }
      this.chatHistoryError = requestError instanceof Error ? requestError.message : 'Unable to load your chat history.'
    } finally {
      if (epoch === this.chatAuthEpoch && version === this.historyListVersion) this.isChatHistoryLoading = false
    }
  }

  get pinnedChatSessions() {
    return this.chatHistorySummaries.filter((session) => session.isPinned)
  }

  get recentChatSessions() {
    const local = [...this.chatWorkspaces.values()].filter(workspace => !workspace.sessionId && workspace.messages.length > 0)
      .map(workspace => this.workspaceSummary(workspace))
    return [...local, ...this.chatHistorySummaries.filter(session => !session.isPinned)]
      .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
  }

  get isHistoryRailVisible() {
    return this.activeSection === 'main' && (this.isNarrowViewport ? this.isChatHistoryDrawerOpen : !this.isChatHistoryCollapsed)
  }

  toggleChatHistory() {
    if (this.isNarrowViewport) {
      this.isChatHistoryDrawerOpen = !this.isChatHistoryDrawerOpen
      return
    }

    this.isChatHistoryCollapsed = !this.isChatHistoryCollapsed
  }

  toggleOperationalContext() {
    this.isOperationalContextCollapsed = !this.isOperationalContextCollapsed
  }

  closeChatHistoryDrawer() {
    this.isChatHistoryDrawerOpen = false
  }

  startNewChat() {
    const previous = this.captureChatWorkspace()
    this.cancelHistoryRead()
    if (!previous.sessionId && !previous.messages.length && !previous.pending) this.chatWorkspaces.delete(previous.key)
    const workspace = this.createChatWorkspace()
    if (this.isPoolReportWorkspace) this.clearPoolReportWorkspace()
    this.projectChatWorkspace(workspace)
    this.isChatHistoryDrawerOpen = false
    this.cancelChatSessionRename()
    this.activeSection = 'main'
  }

  async openChatSession(summary: PoolChatSessionSummary) {
    if (this.isSigningOut) return
    this.captureChatWorkspace()
    this.cancelHistoryRead()
    const cached = this.findChatWorkspace(summary.id)
    const workspace = cached ?? this.createChatWorkspace(summary.id, summary.title, summary.selectedGroupIds ?? [])
    if (this.isPoolReportWorkspace) this.clearPoolReportWorkspace()
    if (/^\/Pool\//i.test(window.location.pathname)) window.history.pushState({}, '', '/')
    this.activeSection = 'main'
    this.isChatHistoryDrawerOpen = false
    this.chatHistoryError = ''
    this.projectChatWorkspace(workspace)
    if (workspace.hydrated || workspace.pending || this.isLocalChatSession(summary.id)) return
    const epoch = this.chatAuthEpoch
    const owner = this.currentUser?.username
    const version = this.historySelectionVersion
    const controller = new AbortController()
    this.historyReadController = controller
    this.isChatSessionOpening = true
    const valid = () => epoch === this.chatAuthEpoch && this.currentUser?.username === owner &&
      version === this.historySelectionVersion && this.activeChatKey === workspace.key
    try {
      const session = await this.api.getPoolChatSession(summary.id, controller.signal)
      if (!valid() || this.removedChatSessionIds.has(summary.id)) return
      const restoredMessages: ChatMessage[] = []
      const restoredFeedbackState: Record<number, FeedbackState> = {}
      let pendingQuery = ''

      for (const stored of session.messages) {
        if (stored.role === 'user') {
          pendingQuery = stored.content
          restoredMessages.push({ id: ++this.nextChatMessageId, role: 'user', text: stored.content })
          continue
        }

        const result = stored.result ?? this.createFallbackWorkflowResult(stored.content)
        const assistantMessage: AssistantMessage = {
          id: ++this.nextChatMessageId,
          role: 'assistant',
          text: stored.content || result.suggestedResolution,
          query: pendingQuery,
          result,
          workflowProgress: [],
        }
        restoredFeedbackState[assistantMessage.id] = createFeedbackState(
          getDefaultFeedbackTicketId(result.similarIncidents),
        )
        restoredMessages.push(assistantMessage)
        pendingQuery = ''
      }

      workspace.messages = restoredMessages
      workspace.feedback = restoredFeedbackState
      workspace.insights = [...restoredMessages].reverse().find(message => message.role === 'assistant')?.result ?? null
      workspace.scopeIds = session.session.selectedGroupIds ?? []
      workspace.sessionId = session.session.id
      workspace.title = session.session.title
      workspace.hydrated = true
      this.projectChatWorkspace(workspace)
    } catch (requestError) {
      if (!valid() || controller.signal.aborted) return
      this.chatHistoryError = requestError instanceof Error ? requestError.message : 'Unable to open this conversation.'
    } finally {
      if (valid()) {
        this.isChatSessionOpening = false
        this.historyReadController = undefined
      }
    }
  }

  beginChatSessionRename(summary: PoolChatSessionSummary) {
    if (this.isLocalChatSession(summary.id)) return
    this.renamingChatSessionId = summary.id
    this.renamingChatSessionTitle = summary.title
  }

  cancelChatSessionRename() {
    this.renamingChatSessionId = null
    this.renamingChatSessionTitle = ''
  }

  async confirmChatSessionRename(summary: PoolChatSessionSummary) {
    if (this.isLocalChatSession(summary.id)) return
    const epoch = this.chatAuthEpoch
    const title = this.renamingChatSessionTitle.trim()
    if (!title || title === summary.title) {
      this.cancelChatSessionRename()
      return
    }

    try {
      await this.api.patchPoolChatSession(summary.id, { title })
      if (epoch !== this.chatAuthEpoch) return
      const workspace = this.findChatWorkspace(summary.id)
      if (workspace) workspace.title = title
      await this.loadChatHistory()
    } catch (requestError) {
      if (epoch === this.chatAuthEpoch) this.chatHistoryError = requestError instanceof Error ? requestError.message : 'Unable to rename this conversation.'
    } finally {
      if (epoch === this.chatAuthEpoch) this.cancelChatSessionRename()
    }
  }

  async handleChatSessionTogglePin(summary: PoolChatSessionSummary) {
    if (this.isLocalChatSession(summary.id)) return
    const epoch = this.chatAuthEpoch
    try {
      await this.api.patchPoolChatSession(summary.id, { isPinned: !summary.isPinned })
      if (epoch !== this.chatAuthEpoch) return
      await this.loadChatHistory()
    } catch (requestError) {
      if (epoch === this.chatAuthEpoch) this.chatHistoryError = requestError instanceof Error ? requestError.message : 'Unable to update this conversation.'
    }
  }

  async handleChatSessionDelete(summary: PoolChatSessionSummary) {
    if (this.isChatSessionPending(summary.id)) {
      this.chatHistoryError = 'Wait for this conversation to finish before deleting it. You can continue switching chats.'
      return
    }
    if (!window.confirm(`Delete "${summary.title}"? This cannot be undone.`)) {
      return
    }

    const epoch = this.chatAuthEpoch
    try {
      if (!this.isLocalChatSession(summary.id)) await this.api.deletePoolChatSession(summary.id)
      if (epoch !== this.chatAuthEpoch) return
      this.removedChatSessionIds.add(summary.id)
      if (this.activeChatSessionId === summary.id) {
        this.startNewChat()
      }
      const workspace = this.findChatWorkspace(summary.id)
      if (workspace) this.chatWorkspaces.delete(workspace.key)
      this.chatHistorySummaries = this.chatHistorySummaries.filter(item => item.id !== summary.id)
      await this.loadChatHistory()
    } catch (requestError) {
      if (epoch === this.chatAuthEpoch) this.chatHistoryError = requestError instanceof Error ? requestError.message : 'Unable to delete this conversation.'
    }
  }

  chatHistoryScopeLabel(summary: PoolChatSessionSummary) {
    const groupIds = summary.selectedGroupIds ?? []
    if (groupIds.length === 0) {
      return 'All applications'
    }

    const names = groupIds.map((groupId) => this.getGroupDisplayName(groupId))
    if (names.length <= 2) {
      return names.join(', ')
    }

    return `${names.slice(0, 2).join(', ')} +${names.length - 2}`
  }

  chatHistoryRelativeTime(isoTimestamp: string) {
    const timestamp = new Date(isoTimestamp)
    if (Number.isNaN(timestamp.getTime())) {
      return ''
    }

    const minutes = Math.round((Date.now() - timestamp.getTime()) / 60000)
    if (minutes < 1) {
      return 'Just now'
    }
    if (minutes < 60) {
      return `${minutes}m ago`
    }
    const hours = Math.round(minutes / 60)
    if (hours < 24) {
      return `${hours}h ago`
    }
    const days = Math.round(hours / 24)
    if (days < 7) {
      return `${days}d ago`
    }

    return timestamp.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
  }

  private createFallbackWorkflowResult(content: string): TicketWorkflowResult {
    return {
      suggestedRootCause: '',
      suggestedResolution: content,
      confidence: 0,
      similarIncidents: [],
      failurePattern: {
        id: 0,
        system: '',
        component: '',
        failureType: '',
        resolutionCategory: '',
        ticketId: '',
        createdAt: '',
      },
      reasoning: '',
      failurePatternFrequency: 0,
    }
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
        const workspace = this.chatWorkspaces.get(this.activeChatKey)
        if (workspace) this.projectChatWorkspace(workspace)
      }
    }

    if (section === 'poolRecommendations' && this.currentUser) {
      void this.loadPoolRecommendations()
    }

    if (section === 'applicationFeedback' && this.currentUser && !this.applicationFeedbackInsights) {
      void this.loadApplicationFeedbackInsights()
    }

    if (section === 'codeRepositories' && this.currentUser) {
      if (!this.codeRepoProjectId || !this.projects.some(project => project.projectId === this.codeRepoProjectId)) {
        this.codeRepoProjectId = this.projects[0]?.projectId ?? ''
      }
      void this.loadCodeRepositories()
      void this.loadCodeChatHistory()
      this.startCodeRepoPolling()
    } else {
      this.stopCodeRepoPolling()
    }
  }

  private startCodeRepoPolling() {
    this.stopCodeRepoPolling()
    this.codeRepoTimer = window.setInterval(() => {
      if (!document.hidden && !this.isCodeRepoAdding && !this.codeRepoBusyId) void this.loadCodeRepositories(true)
    }, 30000)
  }

  private stopCodeRepoPolling() {
    if (this.codeRepoTimer !== null) {
      window.clearInterval(this.codeRepoTimer)
      this.codeRepoTimer = null
    }
  }

  get isCodeRepoBusy() {
    return this.isCodeRepoLoading || this.isCodeRepoAdding || this.isCodeRepoBranchesLoading || this.codeRepoBusyId !== ''
  }

  get codeRepoBranchesLoaded() {
    return this.codeRepoBranches.length > 0 && this.codeRepoBranchesUrl === this.codeRepoUrl.trim()
  }

  get codeRepoProjectName() {
    return this.projects.find(project => project.projectId === this.codeRepoProjectId)?.projectName ?? ''
  }

  private codeRepoPath(id = '') {
    return '/projects/' + encodeURIComponent(this.codeRepoProjectId) + '/repositories' + (id ? '/' + encodeURIComponent(id) : '')
  }

  trackCodeRepository(_index: number, repo: CodeRepository) {
    return repo.id
  }

  codeRepoName(repo: CodeRepository) {
    return repo.url.replace(/^https:\/\/github\.com\//i, '')
  }

  codeRepoStatusLabel(repo: CodeRepository) {
    switch (repo.status) {
      case 'queued': return 'Queued for indexing'
      case 'indexing': return 'Indexing...'
      case 'ready': return 'Index ready'
      case 'error': return 'Indexing failed'
      case 'access-required': return 'GitHub access required'
      case 'disabled': return 'Disabled'
      default: return repo.status
    }
  }

  async loadCodeRepositories(silent = false) {
    if (!this.currentUser) return
    const version = ++this.codeRepoLoadVersion
    if (!silent) this.isCodeRepoLoading = true
    try {
      this.codeRepoConnection = await this.api.codeRequest<GitHubConnectionStatus>('/github/connection')
      const records = this.codeRepoProjectId && this.codeRepoConnection.connected
        ? await this.api.codeRequest<CodeRepository[]>(this.codeRepoPath())
        : []
      if (version !== this.codeRepoLoadVersion) return
      this.codeRepoList = records
      if (!silent) this.codeRepoError = ''
    } catch (requestError) {
      if (version !== this.codeRepoLoadVersion) return
      if (!this.codeRepoConnection) this.codeRepoConnection = { connected: false, login: '' }
      this.codeRepoError = requestError instanceof Error ? requestError.message : 'Unable to load code repositories.'
    } finally {
      if (version === this.codeRepoLoadVersion) this.isCodeRepoLoading = false
    }
  }

  async handleCodeRepoProjectChange(projectId: string) {
    this.codeRepoProjectId = projectId
    this.codeRepoList = []
    this.codeRepoNotice = ''
    this.resetCodeChat()
    await Promise.all([this.loadCodeRepositories(), this.loadCodeChatHistory()])
  }

  async handleCodeRepoAdd() {
    const url = this.codeRepoUrl.trim()
    const branch = this.codeRepoBranch.trim() || 'master'
    if (!url || !this.codeRepoProjectId || this.isCodeRepoBusy) return
    if (!this.validateCodeRepoUrl(url)) return

    this.isCodeRepoAdding = true
    this.codeRepoError = ''
    this.codeRepoNotice = ''
    try {
      const saved = await this.api.codeRequest<CodeRepository>(this.codeRepoPath(), 'POST', { url, branch, enabled: true })
      this.codeRepoUrl = ''
      this.codeRepoBranch = ''
      this.codeRepoBranches = []
      this.codeRepoBranchesUrl = ''
      this.codeRepoNotice = `${this.codeRepoName(saved)} (${saved.branch}) was added and queued for indexing.`
      await this.loadCodeRepositories(true)
    } catch (requestError) {
      this.codeRepoError = requestError instanceof Error ? requestError.message : 'Unable to add this repository.'
    } finally {
      this.isCodeRepoAdding = false
    }
  }

  handleCodeRepoUrlChange(value: string) {
    this.codeRepoUrl = value
    if (this.codeRepoBranchesUrl && this.codeRepoBranchesUrl !== value.trim()) {
      this.codeRepoBranches = []
      this.codeRepoBranchesUrl = ''
      this.codeRepoBranchesTruncated = false
      this.codeRepoBranch = ''
    }
  }

  async handleCodeRepoLoadBranches() {
    const url = this.codeRepoUrl.trim()
    if (!url || !this.codeRepoProjectId || this.isCodeRepoBusy) return
    if (!this.validateCodeRepoUrl(url)) return

    this.isCodeRepoBranchesLoading = true
    this.codeRepoError = ''
    try {
      const result = await this.api.codeRequest<{ fullName: string; defaultBranch: string; branches: string[]; truncated: boolean }>(
        this.codeRepoPath() + '/branches?url=' + encodeURIComponent(url))
      this.codeRepoBranches = result.branches
      this.codeRepoBranchesUrl = url
      this.codeRepoBranchesTruncated = result.truncated
      this.codeRepoBranch = result.branches.includes(this.codeRepoBranch) ? this.codeRepoBranch : result.defaultBranch
      if (result.branches.length === 0) this.codeRepoError = 'This repository has no branches yet.'
    } catch (requestError) {
      this.codeRepoBranches = []
      this.codeRepoBranchesUrl = ''
      this.codeRepoError = requestError instanceof Error ? requestError.message : 'Unable to load branches for this repository.'
    } finally {
      this.isCodeRepoBranchesLoading = false
    }
  }

  private validateCodeRepoUrl(url: string) {
    let parsed: URL
    try { parsed = new URL(url) } catch { this.codeRepoError = 'Enter a valid GitHub repository URL.'; return false }
    const segments = parsed.pathname.split('/').filter(Boolean)
    if (parsed.protocol !== 'https:' || parsed.host !== 'github.com' || parsed.username || parsed.password || parsed.search || parsed.hash || segments.length !== 2) {
      this.codeRepoError = 'Use a repository URL in the form https://github.com/owner/repository.'
      return false
    }
    return true
  }

  async handleCodeRepoRefresh(repo: CodeRepository) {
    if (this.isCodeRepoBusy || !repo.enabled || repo.status === 'indexing' || repo.status === 'queued') return
    this.codeRepoBusyId = repo.id
    this.codeRepoError = ''
    this.codeRepoNotice = ''
    try {
      const path = repo.status === 'access-required' ? '/take-ownership' : '/refresh'
      await this.api.codeRequest<CodeRepository>(this.codeRepoPath(repo.id) + path, 'POST', {})
      this.codeRepoNotice = `${this.codeRepoName(repo)} queued for re-indexing.`
      await this.loadCodeRepositories(true)
    } catch (requestError) {
      this.codeRepoError = requestError instanceof Error ? requestError.message : 'Unable to refresh this repository.'
    } finally {
      this.codeRepoBusyId = ''
    }
  }

  async handleCodeRepoRemove(repo: CodeRepository) {
    if (this.isCodeRepoBusy || !window.confirm(`Remove ${this.codeRepoName(repo)} from ${this.codeRepoProjectName || 'this application'}?`)) return
    this.codeRepoBusyId = repo.id
    this.codeRepoError = ''
    this.codeRepoNotice = ''
    try {
      await this.api.codeRequest(this.codeRepoPath(repo.id), 'DELETE')
      this.codeRepoNotice = `${this.codeRepoName(repo)} was removed.`
      this.clearCodeAnswers()
      await this.loadCodeRepositories(true)
    } catch (requestError) {
      this.codeRepoError = requestError instanceof Error ? requestError.message : 'Unable to remove this repository.'
    } finally {
      this.codeRepoBusyId = ''
    }
  }

  get hasReadyCodeRepository() {
    return this.codeRepoList.some(repo => repo.enabled && repo.indexedCommit)
  }

  async loadCodeChatHistory() {
    if (!this.currentUser || !this.codeRepoProjectId) {
      this.codeChatHistory = []
      return
    }
    this.isCodeChatHistoryLoading = true
    try {
      this.codeChatHistory = await this.api.getCodeChatHistory(this.codeRepoProjectId)
    } catch (requestError) {
      this.codeChatError = requestError instanceof Error ? requestError.message : 'Unable to load conversation history.'
    } finally {
      this.isCodeChatHistoryLoading = false
    }
  }

  async openCodeChatSession(sessionId: string) {
    if (this.isCodeChatAsking || sessionId === this.codeChatSessionId) return
    this.isCodeChatHistoryLoading = true
    this.codeChatError = ''
    try {
      const session = await this.api.getCodeChatSession(this.codeRepoProjectId, sessionId)
      this.codeChatSessionId = session.session.id
      this.codeChatMessages = session.messages.map(message =>
        this.createCodeChatMessage(message.role, message.content, message.citations, message.createdAt))
      this.scrollCodeChatToBottom()
    } catch (requestError) {
      this.codeChatError = requestError instanceof Error ? requestError.message : 'Unable to load this conversation.'
    } finally {
      this.isCodeChatHistoryLoading = false
    }
  }

  async deleteCodeChatSession(sessionId: string, event?: Event) {
    event?.stopPropagation()
    if (this.isCodeChatAsking) return
    try {
      await this.api.deleteCodeChatSession(this.codeRepoProjectId, sessionId)
      this.codeChatHistory = this.codeChatHistory.filter(session => session.id !== sessionId)
      if (this.codeChatSessionId === sessionId) this.resetCodeChat()
    } catch (requestError) {
      this.codeChatError = requestError instanceof Error ? requestError.message : 'Unable to delete this conversation.'
    }
  }

  async handleCodeChatAsk() {
    const question = this.codeChatQuestion.trim()
    if (!question || !this.codeRepoProjectId || this.isCodeChatAsking) return

    const history: CodeChatTurn[] = this.codeChatMessages
      .filter(message => !message.isError)
      .slice(-8)
      .map(message => ({ role: message.role, content: message.text }))

    this.codeChatMessages = [...this.codeChatMessages, this.createCodeChatMessage('user', question)]
    this.codeChatQuestion = ''
    this.isCodeChatAsking = true
    this.codeChatError = ''
    this.scrollCodeChatToBottom()

    try {
      const response = await this.api.askCodeChat(this.codeRepoProjectId, question, history, this.codeChatSessionId)
      this.codeChatSessionId = response.sessionId
      this.codeChatMessages = [
        ...this.codeChatMessages,
        this.createCodeChatMessage('assistant', response.answer, response.citations, response.generatedAt, response.codeKnowledgeStatus, response.codeSearchCoverage),
      ]
      void this.loadCodeChatHistory()
    } catch (requestError) {
      const message = requestError instanceof Error ? requestError.message : 'Unable to answer from repository source.'
      this.codeChatMessages = [...this.codeChatMessages, { ...this.createCodeChatMessage('assistant', message), isError: true }]
    } finally {
      this.isCodeChatAsking = false
      this.scrollCodeChatToBottom()
    }
  }

  handleCodeChatKeydown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault()
      void this.handleCodeChatAsk()
    }
  }

  resetCodeChat() {
    this.codeChatMessages = []
    this.codeChatSessionId = null
    this.codeChatError = ''
  }

  trackCodeChatMessage(_index: number, message: CodeChatMessage) {
    return message.id
  }

  trackCodeChatSession(_index: number, session: CodeChatSessionSummary) {
    return session.id
  }

  private createCodeChatMessage(role: 'user' | 'assistant', text: string, citations: CodeEvidence[] = [], generatedAt = new Date().toISOString(), codeKnowledgeStatus = '', codeSearchCoverage?: CodeSearchCoverage): CodeChatMessage {
    return {
      id: Date.now() + Math.random(),
      role,
      text,
      citations,
      generatedAt,
      codeKnowledgeStatus,
      codeSearchCoverage,
    }
  }

  private scrollCodeChatToBottom() {
    setTimeout(() => {
      const element = this.codeChatTranscript?.nativeElement
      if (element) element.scrollTop = element.scrollHeight
    })
  }

  toggleSidebar() {
    this.isSidebarCollapsed = !this.isSidebarCollapsed
  }

  handleGroupChange(groupId: string, checked: boolean) {
    const next = checked
      ? [...this.selectedGroupIds, groupId]
      : this.selectedGroupIds.filter((selectedGroupId) => selectedGroupId !== groupId)

    this.changeChatScope(next.length === 0 ? [] : next)
  }

  handleScopeSelectChange(value: string) {
    if (!value || value === this.allGroupValue) {
      this.changeChatScope([])
      return
    }

    this.changeChatScope([value])
  }

  handleAddGroupSelect(groupId: string) {
    if (!groupId) {
      return
    }

    this.handleGroupChange(groupId, true)
  }

  getGroupDisplayName(groupId: string) {
    return this.groups.find((group) => group.groupId === groupId)?.displayName ?? groupId
  }

  private setSelectedGroupIds(groupIds: string[]) {
    this.selectedGroupIds = groupIds
    this.scopeAddSelectValue = ''
  }

  private changeChatScope(groupIds: string[]) {
    if (this.isLoading || this.isChatSessionOpening) return
    const sameScope = groupIds.length === this.selectedGroupIds.length &&
      groupIds.every(groupId => this.selectedGroupIds.includes(groupId))
    if (!sameScope && (this.messages.length > 0 || this.activeChatSessionId)) {
      this.startNewChat()
      this.chatContextNotice = 'Application scope changed. Started a new chat to keep conversation context separate.'
    }
    this.setSelectedGroupIds(groupIds)
  }

  trackGroupId(_index: number, groupId: string) {
    return groupId
  }

  trackGroupById(_index: number, group: { groupId: string }) {
    return group.groupId
  }

  private clearPoolReportWorkspace() {
    this.poolReportReadVersion++
    this.poolReport = null
    this.poolReportSourceEventId = ''
    this.isPoolReportLoading = false
    this.poolReportError = ''
    this.poolTroubleshootQuestion = ''
    this.poolTroubleshootEntries = []
    this.poolTroubleshootError = ''
    this.isPoolTroubleshootLoading = false
    this.poolTroubleshootProgressEvents = []
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
    if (this.isProjectSaving) return
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
      githubUrls: project.githubUrls ?? '',
    }
    this.projectNotice = `Editing ${project.projectName}.`
    this.projectError = ''
  }

  cancelProjectEdit() {
    if (this.isProjectSaving) return
    this.resetProjectForm()
    this.projectNotice = ''
    this.projectError = ''
  }

  async handleProjectSubmit() {
    if (this.isProjectSaving) return

    const payload: ProjectConfigInput = {
      ...this.projectForm,
      projectId: this.editingProjectId ? this.editingProjectId.trim() : '',
      projectName: this.projectForm.projectName.trim(),
      emailRecipients: this.projectForm.emailRecipients.trim(),
      applicationFilter: this.projectForm.applicationFilter.trim(),
      nyraKbNames: this.projectForm.nyraKbNames.trim(),
      githubUrls: this.projectForm.githubUrls,
    }

    if (!payload.projectName) {
      this.projectError = 'Application name is required.'
      return
    }

    this.projectError = ''
    this.projectNotice = ''
    this.isProjectSaving = true
    const repositoryEditor = this.codeRepositories
    let applicationSaved = false

    try {
      repositoryEditor?.validate()
      if (this.editingProjectId) {
        await this.api.updateProject(this.editingProjectId, payload)
        applicationSaved = true
        const queued = await repositoryEditor?.saveChanges() ?? 0
        this.projectNotice = `Updated ${payload.projectName}.` + (queued ? ` Queued ${queued} ${queued === 1 ? 'repository' : 'repositories'} for indexing.` : '')
      } else {
        await this.api.createProject(payload)
        this.projectNotice = `Created ${payload.projectName}.`
        this.resetProjectForm()
      }

      await this.loadProjectWorkspace(true)
    } catch (requestError) {
      const message = requestError instanceof Error ? requestError.message : 'Unable to save the application configuration.'
      this.projectError = applicationSaved ? `Application settings were saved, but repository changes could not be completed. ${message}` : message
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
      await this.loadApplicationFeedbackInsights()
    } catch (requestError) {
      this.applicationFeedbackError = requestError instanceof Error ? requestError.message : 'Unable to submit application feedback.'
    } finally {
      this.isApplicationFeedbackSaving = false
    }
  }

  formatApplicationFeedbackDelta(current: number, previous: number) {
    if (this.applicationFeedbackTimelineDays === 0) {
      return 'All-time total'
    }

    if (previous === 0) {
      return current > 0 ? `+ ${current} new in ${this.applicationFeedbackTimelineDays} days` : `0 vs previous ${this.applicationFeedbackTimelineDays} days`
    }

    const delta = Math.round(((current - previous) / previous) * 100)
    const sign = delta >= 0 ? '+' : '-'
    return `${sign} ${Math.abs(delta)}% vs previous ${this.applicationFeedbackTimelineDays} days`
  }

  handleApplicationFeedbackTimelineChange(days: string | number) {
    this.applicationFeedbackTimelineDays = Number(days)
    void this.loadApplicationFeedbackInsights()
  }

  isApplicationFeedbackDeltaNegative(current: number, previous: number) {
    return previous > 0 && current < previous
  }

  getApplicationFeedbackSparklineHeight(count: number, source: 'feedback' | 'ai') {
    const dailyCounts = source === 'ai'
      ? this.applicationFeedbackInsights?.dailyAiFeedbackCounts ?? []
      : this.applicationFeedbackInsights?.dailyFeedbackCounts ?? []
    const maxCount = Math.max(1, ...dailyCounts.map((dailyCount) => dailyCount.count))
    return Math.max(14, Math.round((count / maxCount) * 100))
  }

  trackApplicationFeedbackDay(_index: number, day: ApplicationFeedbackDailyCount) {
    return day.date
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

  truncateNyraContent(content: string) {
    if (!content) return ''
    return content.length > 200 ? `${content.slice(0, 200).trimEnd()}...` : content
  }

  isLatestAssistantMessage(message: AssistantMessage): boolean {
    for (let index = this.messages.length - 1; index >= 0; index -= 1) {
      const candidate = this.messages[index]
      if (candidate.role === 'assistant') {
        return candidate.id === message.id
      }
    }

    return false
  }

  isDetailedAnalysisExpanded(message: AssistantMessage): boolean {
    const state = this.detailedAnalysisExpandedByMessageId[message.id]
    return state ?? this.isLatestAssistantMessage(message)
  }

  toggleDetailedAnalysis(message: AssistantMessage) {
    this.detailedAnalysisExpandedByMessageId = {
      ...this.detailedAnalysisExpandedByMessageId,
      [message.id]: !this.isDetailedAnalysisExpanded(message),
    }
  }

  getResultConfidencePercent(result: TicketWorkflowResult): number {
    return Math.max(0, Math.min(100, Math.round((result.confidence || 0) * 100)))
  }

  getEvidenceSourceCounts(result: TicketWorkflowResult): { pool: number; nyra: number; code: number } {
    return {
      pool: result.similarIncidents?.length ?? 0,
      nyra: result.nyraDocuments?.length ?? 0,
      code: result.codeDocuments?.length ?? 0,
    }
  }

  getTopIncidentMatches(result: TicketWorkflowResult, limit = 5) {
    return (result.similarIncidents ?? [])
      .filter((incident) => incident.ticketId.trim().length > 0)
      .slice(0, limit)
      .map((incident) => ({
        ticketId: incident.ticketId,
        percent: Math.max(0, Math.min(100, Math.round((incident.similarity || 0) * 100))),
      }))
  }

  getTroubleshootingSteps(resolution: string): string[] {
    const text = (resolution || '').trim()
    if (/```|~~~/.test(text)) return []
    if (!text) {
      return []
    }

    const numberedSteps = text
      .split(/\s*\d+[.)]\s+/)
      .map((step) => step.trim())
      .filter((step) => step.length > 0)

    if (numberedSteps.length > 1) {
      return numberedSteps.slice(0, 8)
    }

    const sentenceSteps = text
      .split(/(?<=[.!?])\s+(?=[A-Z])/)
      .map((step) => step.trim())
      .filter((step) => step.length > 0)

    return sentenceSteps.length > 1 ? sentenceSteps.slice(0, 6) : []
  }

  getReasoningSegments(reasoning: string | undefined | null): string[] {
    const text = (reasoning || '').replace(/\s+/g, ' ').trim()
    if (!text) {
      return ['No reasoning details available.']
    }

    const sentenceSegments = text
      .split(/(?<=[.!?])\s+(?=[A-Z(])/)
      .map((segment) => segment.trim())
      .filter((segment) => segment.length > 0)

    return sentenceSegments.length > 1 ? sentenceSegments.slice(0, 10) : [text]
  }

  getRetrievedTicketIds(message: AssistantMessage) {
    return message.result.similarIncidents.map((incident) => incident.ticketId).filter((ticketId) => ticketId.trim().length > 0)
  }

  getFeedbackState(messageId: number) {
    this.feedbackStateByMessageId[messageId] ??= createFeedbackState()

    return this.feedbackStateByMessageId[messageId]
  }

  getFeedbackTargetOptionLabel(incident: SimilarIncident) {
    const ticketId = incident.ticketId.trim()
    if (!ticketId) {
      return 'Historical incident'
    }

    const matchLabel = incident.similarity > 0 ? `${Math.round(incident.similarity * 100)}% match` : 'Historical incident'
    return `${ticketId} - ${matchLabel}`
  }

  getFeedbackTargetSelectedLabel(message: AssistantMessage) {
    const state = this.getFeedbackState(message.id)
    const selected = message.result.similarIncidents.find((incident) => incident.ticketId === state.selectedTicketId)
    if (selected) {
      return this.getFeedbackTargetOptionLabel(selected)
    }

    if (state.selectedTicketId.trim()) {
      return `${state.selectedTicketId} - Historical incident`
    }

    return 'Select an incident'
  }

  isFeedbackTargetSelected(messageId: number, ticketId: string) {
    return this.getFeedbackState(messageId).selectedTicketId === ticketId
  }

  isFeedbackTargetOpen(messageId: number) {
    return this.openFeedbackTargetMessageId === messageId
  }

  toggleFeedbackTargetMenu(messageId: number) {
    this.openFeedbackTargetMessageId = this.openFeedbackTargetMessageId === messageId ? null : messageId
  }

  selectFeedbackTarget(messageId: number, ticketId: string) {
    this.getFeedbackState(messageId).selectedTicketId = ticketId
    this.openFeedbackTargetMessageId = null
  }

  @HostListener('document:click', ['$event'])
  handleDocumentClick(event: MouseEvent) {
    const target = event.target
    if (!(target instanceof Element)) {
      return
    }

    if (!target.closest('.feedback-target-control')) {
      this.openFeedbackTargetMessageId = null
    }
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
    this.openFeedbackTargetMessageId = null
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

  trackWorkflowProgress(_index: number, progress: TicketWorkflowProgress) {
    return progress.stage
  }

  isWorkflowActivityExpanded(messageId: number) {
    return this.expandedWorkflowActivityMessageId === messageId
  }

  toggleWorkflowActivity(messageId: number) {
    this.expandedWorkflowActivityMessageId = this.isWorkflowActivityExpanded(messageId) ? null : messageId
  }

  answerDurationLabel(message: ChatMessage): string | null {
    if (message.role !== 'assistant') return null
    const clientDuration = formatAnswerDuration(message.completedInMs)
    if (clientDuration) return `Completed in ${clientDuration}`
    const serverDuration = formatAnswerDuration(message.result?.processingDurationMs)
    return serverDuration ? `Generated in ${serverDuration}` : null
  }

  answerDurationTitle(message: ChatMessage): string | null {
    if (message.role !== 'assistant') return null
    const clientDuration = formatAnswerDuration(message.completedInMs)
    if (clientDuration) return 'Elapsed time from sending your question to receiving this completed response.'
    const serverDuration = formatAnswerDuration(message.result?.processingDurationMs)
    return serverDuration ? 'Server-measured generation time recorded when this answer was saved.' : null
  }

  trackIncident(_index: number, incident: SimilarIncident) {
    return incident.ticketId
  }

  trackNyraDocument(_index: number, document: { documentId: string; sourceUrl: string; citation: string }) {
    return document.documentId || document.sourceUrl || document.citation
  }

  clearCodeAnswers() {
    const removedIds = new Set(this.messages
      .filter(message => message.role === 'assistant' && (message.result.codeDocuments?.length ?? 0) > 0)
      .map(message => message.id))
    this.messages = this.messages.filter(message => !removedIds.has(message.id))
    if (this.insights?.codeDocuments?.length) this.insights = null
    this.poolTroubleshootEntries = this.poolTroubleshootEntries.filter(entry => !entry.codeDocuments?.length)
    for (const messageId of removedIds) delete this.feedbackStateByMessageId[messageId]
    if (this.openFeedbackTargetMessageId !== null && removedIds.has(this.openFeedbackTargetMessageId)) this.openFeedbackTargetMessageId = null
    if (this.expandedWorkflowActivityMessageId !== null && removedIds.has(this.expandedWorkflowActivityMessageId)) this.expandedWorkflowActivityMessageId = null
    this.activeEvidenceTab = 'incidents'
  }

  setEvidenceTab(tab: 'incidents' | 'nyra' | 'code') {
    this.activeEvidenceTab = tab
  }

  toggleEvidencePanel() {
    this.isEvidencePanelCollapsed = !this.isEvidencePanelCollapsed
  }

  toggleTroubleshootPanel() {
    this.isTroubleshootPanelCollapsed = !this.isTroubleshootPanelCollapsed
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
    this.captureChatWorkspace()
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
    this.poolTroubleshootProgressEvents = [
      {
        stage: 'request',
        title: 'Submitting follow-up',
        detail: `Sending troubleshooting question for pool ${poolId}.`,
        state: 'completed',
        order: 0,
        timestampUtc: new Date().toISOString(),
      },
    ]

    try {
      const response = await this.api.troubleshootPoolWithProgress(poolId, question, (progress) => {
        this.upsertPoolTroubleshootProgress(progress)
      })
      this.poolTroubleshootEntries = [createPoolTroubleshootEntry(response, this.poolTroubleshootStatusSteps), ...this.poolTroubleshootEntries]
      this.mergeTroubleshootNyraEvidence(response)
      this.poolTroubleshootQuestion = ''
    } catch (requestError) {
      this.poolTroubleshootError = requestError instanceof Error ? requestError.message : 'Unable to troubleshoot this pool.'
    } finally {
      this.isPoolTroubleshootLoading = false
      this.poolTroubleshootProgressEvents = []
    }
  }

  private mergeTroubleshootNyraEvidence(response: PoolTroubleshootResponse) {
    if (!this.insights) {
      return
    }

    const newDocuments = response.nyraDocuments ?? []
    if (newDocuments.length === 0) {
      return
    }

    const documentKey = (document: NyraDocumentResult) => document.documentId || document.sourceUrl || document.citation
    const existingDocuments = this.insights.nyraDocuments ?? []
    const existingKeys = new Set(existingDocuments.map(documentKey))
    const mergedDocuments = [
      ...existingDocuments,
      ...newDocuments.filter((document) => !existingKeys.has(documentKey(document))),
    ]

    this.insights = {
      ...this.insights,
      nyraDocuments: mergedDocuments,
      nyraKnowledgeBaseUsed: this.insights.nyraKnowledgeBaseUsed || !!response.nyraKnowledgeBaseUsed,
      nyraKnowledgeBaseNames: Array.from(
        new Set([...(this.insights.nyraKnowledgeBaseNames ?? []), ...(response.nyraKnowledgeBaseNames ?? [])]),
      ),
    }

    this.activeEvidenceTab = 'nyra'
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
    void this.loadChatHistory()
  }

  private async loadAuthenticatedWorkspace() {
    await Promise.all([this.loadProjectGroups(), this.loadProjectWorkspace(), this.loadApplicationFeedbackInsights()])
  }

  private async loadApplicationFeedbackInsights() {
    if (!this.currentUser) {
      return
    }

    this.isApplicationFeedbackInsightsLoading = true
    this.applicationFeedbackInsightsError = ''

    try {
      this.applicationFeedbackInsights = await this.api.getApplicationFeedbackInsights(this.applicationFeedbackTimelineDays)
    } catch (requestError) {
      this.applicationFeedbackInsightsError = requestError instanceof Error ? requestError.message : 'Unable to load application feedback insights.'
    } finally {
      this.isApplicationFeedbackInsightsLoading = false
    }
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
    this.cancelHistoryRead()
    const epoch = this.chatAuthEpoch
    const version = ++this.poolReportReadVersion
    const valid = () => epoch === this.chatAuthEpoch && version === this.poolReportReadVersion
    this.activeSection = 'main'
    this.activeChatSessionId = null
    this.isLoading = false
    this.loadingQuery = ''
    this.loadingScopeGroupIds = []
    this.loadingProgressEvents = []
    this.messages = []
    this.insights = null
    this.error = ''
    this.historyPersistenceNotice = ''
    this.poolReport = null
    this.poolReportError = ''
    this.poolTroubleshootQuestion = ''
    this.poolTroubleshootEntries = []
    this.poolTroubleshootError = ''
    this.poolTroubleshootProgressEvents = []
    this.isPoolReportLoading = true

    try {
      const report = await this.api.getPoolReport(poolReportId)
      if (!valid()) return
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
      if (valid()) this.poolReportError = requestError instanceof Error ? requestError.message : 'Unable to load the PoolSense report.'
    } finally {
      if (valid()) this.isPoolReportLoading = false
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
      workflowProgress: [],
    }

    this.feedbackStateByMessageId[assistantMessage.id] = createFeedbackState(
      getDefaultFeedbackTicketId(result.similarIncidents),
    )
    this.messages = [userMessage, assistantMessage]
    this.insights = result
    this.activeChatSessionId = null
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