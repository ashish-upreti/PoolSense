import { useState, useEffect } from 'react'
import ChatPanel, { type AssistantMessage, type ChatMessage, type UserMessage } from './components/ChatPanel'
import GroupSelector from './components/GroupSelector'
import InsightPanel from './components/InsightPanel'
import {
  askPoolSense,
  createProject,
  getIngestionStatuses,
  getProjectGroups,
  getProjects,
  updateProject,
  type IngestionStatus,
  type ProjectConfig,
  type ProjectConfigInput,
  type ProjectGroup,
  type TicketWorkflowResult,
} from './services/api'
import './App.css'

const defaultProjectForm: ProjectConfigInput = {
  projectId: '',
  projectName: '',
  knowledgeLookbackYears: 2,
  similaritySearchLimit: 5,
  sendEmail: true,
  poolingEnabled: true,
  emailRecipients: '',
  applicationFilter: '',
}

function buildProjectIdPreview(projectName: string) {
  return Array.from(projectName.trim().toLowerCase())
    .map((character) => (/[a-z0-9]/.test(character) ? character : '-'))
    .join('')
    .replace(/^-+|-+$/g, '')
}

function App() {
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [insights, setInsights] = useState<TicketWorkflowResult | null>(null)
  const [isLoading, setIsLoading] = useState(false)
  const [error, setError] = useState('')
  const [groups, setGroups] = useState<ProjectGroup[]>([])
  // empty = All; non-empty = selected group IDs
  const [selectedGroupIds, setSelectedGroupIds] = useState<string[]>([])
  const [isDark, setIsDark] = useState(() => localStorage.getItem('theme') === 'dark')
  const [projects, setProjects] = useState<ProjectConfig[]>([])
  const [ingestionStatuses, setIngestionStatuses] = useState<IngestionStatus[]>([])
  const [projectForm, setProjectForm] = useState<ProjectConfigInput>(defaultProjectForm)
  const [editingProjectId, setEditingProjectId] = useState<string | null>(null)
  const [isProjectLoading, setIsProjectLoading] = useState(true)
  const [isProjectSaving, setIsProjectSaving] = useState(false)
  const [projectError, setProjectError] = useState('')
  const [projectNotice, setProjectNotice] = useState('')

  useEffect(() => {
    document.documentElement.dataset.theme = isDark ? 'dark' : 'light'
    localStorage.setItem('theme', isDark ? 'dark' : 'light')
  }, [isDark])

  useEffect(() => {
    getProjectGroups().then(setGroups).catch(() => setGroups([]))
  }, [])

  useEffect(() => {
    void loadProjectWorkspace()
  }, [])

  async function handleSend(message: string) {
    const userMessage: UserMessage = {
      id: Date.now(),
      role: 'user',
      text: message,
    }

    setMessages((current) => [...current, userMessage])
    setError('')
    setIsLoading(true)

    try {
      const result = await askPoolSense(message, selectedGroupIds)

      const assistantMessage: AssistantMessage = {
        id: userMessage.id + 1,
        role: 'assistant',
        text: result.suggestedResolution,
        query: message,
        result,
      }

      setMessages((current) => [...current, assistantMessage])
      setInsights(result)
    } catch (requestError) {
      const errorMessage =
        requestError instanceof Error
          ? requestError.message
          : 'Unable to reach PoolSense.'

      setError(errorMessage)
    } finally {
      setIsLoading(false)
    }
  }

  async function loadProjectWorkspace(refreshIngestionTotals = false) {
    setIsProjectLoading(true)

    try {
      const [loadedProjects, loadedStatuses] = await Promise.all([
        getProjects(),
        getIngestionStatuses(refreshIngestionTotals),
      ])

      setProjects(loadedProjects)
      setIngestionStatuses(loadedStatuses)
    } catch (requestError) {
      const errorMessage =
        requestError instanceof Error
          ? requestError.message
          : 'Unable to load project configuration data.'

      setProjectError(errorMessage)
    } finally {
      setIsProjectLoading(false)
    }
  }

  function handleProjectFieldChange<Key extends keyof ProjectConfigInput>(
    field: Key,
    value: ProjectConfigInput[Key],
  ) {
    setProjectForm((current) => ({
      ...current,
      [field]: value,
    }))
  }

  function handleEditProject(project: ProjectConfig) {
    setEditingProjectId(project.projectId)
    setProjectForm({
      projectId: project.projectId,
      projectName: project.projectName,
      knowledgeLookbackYears: project.knowledgeLookbackYears,
      similaritySearchLimit: project.similaritySearchLimit,
      sendEmail: project.sendEmail,
      poolingEnabled: project.poolingEnabled,
      emailRecipients: project.emailRecipients,
      applicationFilter: project.applicationFilter,
    })
    setProjectNotice(`Editing ${project.projectName}.`)
    setProjectError('')
  }

  function resetProjectForm() {
    setEditingProjectId(null)
    setProjectForm(defaultProjectForm)
  }

  async function handleProjectSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()

    const payload: ProjectConfigInput = {
      ...projectForm,
      projectId: editingProjectId ? projectForm.projectId.trim() : '',
      projectName: projectForm.projectName.trim(),
      emailRecipients: projectForm.emailRecipients.trim(),
      applicationFilter: projectForm.applicationFilter.trim(),
    }

    if (!payload.projectName) {
      setProjectError('Project name is required.')
      return
    }

    setProjectError('')
    setProjectNotice('')
    setIsProjectSaving(true)

    try {
      if (editingProjectId) {
        await updateProject(editingProjectId, payload)
        setProjectNotice(`Updated ${payload.projectName}.`)
      } else {
        await createProject(payload)
        setProjectNotice(`Created ${payload.projectName}.`)
      }

      resetProjectForm()
      await loadProjectWorkspace(true)
    } catch (requestError) {
      const errorMessage =
        requestError instanceof Error
          ? requestError.message
          : 'Unable to save the project configuration.'

      setProjectError(errorMessage)
    } finally {
      setIsProjectSaving(false)
    }
  }

  const statusByProjectId = new Map(ingestionStatuses.map((status) => [status.projectId, status]))
  const generatedProjectId = buildProjectIdPreview(projectForm.projectName)

  return (
    <div className="workspace-layout">
      <div className="app-shell">
        <ChatPanel
          messages={messages}
          isLoading={isLoading}
          error={error}
          onSend={handleSend}
          isDark={isDark}
          onToggleDark={() => setIsDark((prev) => !prev)}
          groupSelector={
            <GroupSelector
              groups={groups}
              selectedGroupIds={selectedGroupIds}
              onChange={setSelectedGroupIds}
              disabled={isLoading}
            />
          }
        />
        <InsightPanel insights={insights} isLoading={isLoading} />
      </div>

      <section className="project-studio">
        <div className="project-studio-header">
          <div>
            <p className="panel-kicker">Configuration</p>
            <h2>Project Configuration Studio</h2>
            <p className="workspace-copy">
              Persist project defaults in PostgreSQL and monitor ingestion progress per project.
            </p>
          </div>
          <div className="workspace-banner">
            <span className="workspace-dot" />
            <span>{projects.length} configured project{projects.length === 1 ? '' : 's'}</span>
          </div>
        </div>

        {projectError ? <p className="project-feedback-banner">{projectError}</p> : null}
        {projectNotice ? <p className="project-feedback-banner project-feedback-banner-success">{projectNotice}</p> : null}

        <div className="project-studio-grid">
          <form className="project-form-card" onSubmit={handleProjectSubmit}>
            <div className="card-heading-row project-toolbar">
              <div>
                <p className="panel-kicker">Form</p>
                <h3>{editingProjectId ? 'Edit project configuration' : 'Add project configuration'}</h3>
              </div>
              {editingProjectId ? (
                <button
                  type="button"
                  className="feedback-button feedback-button-secondary"
                  onClick={() => {
                    resetProjectForm()
                    setProjectNotice('')
                    setProjectError('')
                  }}
                >
                  Cancel edit
                </button>
              ) : null}
            </div>

            <div className="project-form-grid">
              <label className="field-group">
                <span className="field-label">Project Name</span>
                <input
                  className="field-input"
                  type="text"
                  value={projectForm.projectName}
                  onChange={(event) => handleProjectFieldChange('projectName', event.target.value)}
                  placeholder="FSCO-Fab"
                  disabled={isProjectSaving}
                />
              </label>

              <label className="field-group field-group-full">
                <span className="field-label">Application Filter(Pool Application)</span>
                <input
                  className="field-input"
                  type="text"
                  value={projectForm.applicationFilter}
                  onChange={(event) => handleProjectFieldChange('applicationFilter', event.target.value)}
                  placeholder="%FSCO-FAB%"
                  disabled={isProjectSaving}
                />
                <span className="field-note">
                  Optional. Use an exact application name for one app, or use `%` wildcards to group multiple apps together.
                </span>
              </label>

              <label className="field-group">
                <span className="field-label">Project Id</span>
                <input
                  className="field-input"
                  type="text"
                  value={editingProjectId ? projectForm.projectId : generatedProjectId}
                  placeholder="Auto-generated"
                  readOnly
                  disabled={isProjectSaving}
                />
                <span className="field-note">
                  {editingProjectId
                    ? 'Stable identifier used for scoping, ingestion status, and updates.'
                    : generatedProjectId
                      ? 'Generated automatically from Project Name when you create the project.'
                      : 'Will be auto-generated from Project Name when you create the project.'}
                </span>
              </label>

              <label className="field-group">
                <span className="field-label">Knowledge Lookback Years</span>
                <input
                  className="field-input"
                  type="number"
                  min={0}
                  value={projectForm.knowledgeLookbackYears}
                  onChange={(event) => handleProjectFieldChange('knowledgeLookbackYears', Number(event.target.value))}
                  disabled={isProjectSaving}
                />
              </label>

              <label className="field-group">
                <span className="field-label">Similarity Search Limit</span>
                <input
                  className="field-input"
                  type="number"
                  min={1}
                  max={20}
                  value={projectForm.similaritySearchLimit}
                  onChange={(event) => handleProjectFieldChange('similaritySearchLimit', Number(event.target.value))}
                  disabled={isProjectSaving}
                />
                <span className="field-note">Allowed range: 1 to 20</span>
              </label>

              <label className="field-group field-group-full">
                <span className="field-label">Email Recipients</span>
                <input
                  className="field-input"
                  type="text"
                  value={projectForm.emailRecipients}
                  onChange={(event) => handleProjectFieldChange('emailRecipients', event.target.value)}
                  placeholder="ashish.upreti@intel.com;"
                  disabled={isProjectSaving}
                />
                <span className="field-note">Optional, comma-separated email addresses.</span>
              </label>
            </div>

            <div className="toggle-row">
              <label className="toggle-card">
                <input
                  type="checkbox"
                  checked={projectForm.sendEmail}
                  onChange={(event) => handleProjectFieldChange('sendEmail', event.target.checked)}
                  disabled={isProjectSaving}
                />
                <span>
                  <strong>Send Email</strong>
                  <small>Keep notification delivery enabled for this project.</small>
                </span>
              </label>

              <label className="toggle-card">
                <input
                  type="checkbox"
                  checked={projectForm.poolingEnabled}
                  onChange={(event) => handleProjectFieldChange('poolingEnabled', event.target.checked)}
                  disabled={isProjectSaving}
                />
                <span>
                  <strong>Pooling Enabled</strong>
                  <small>Allow ingestion to process this project during scheduled runs.</small>
                </span>
              </label>
            </div>

            <div className="composer-footer">
              <p className="helper-text">
                Project ID is auto-generated on create and remains immutable to keep project scoping and ingestion status stable.
              </p>
              <button type="submit" className="send-button" disabled={isProjectSaving}>
                {isProjectSaving ? 'Saving...' : editingProjectId ? 'Update Project' : 'Create Project'}
              </button>
            </div>
          </form>

          <div className="project-list-card">
            <div className="card-heading-row project-toolbar">
              <div>
                <p className="panel-kicker">Status</p>
                <h3>Configured projects</h3>
              </div>
              <button
                type="button"
                className="feedback-button feedback-button-secondary"
                onClick={() => void loadProjectWorkspace(true)}
                disabled={isProjectLoading}
              >
                Refresh
              </button>
            </div>

            {isProjectLoading ? (
              <div className="project-empty">
                <p className="empty-copy">Loading project configuration and ingestion status...</p>
              </div>
            ) : projects.length === 0 ? (
              <div className="project-empty">
                <p className="empty-copy">No projects are configured yet. Create the first project from the form.</p>
              </div>
            ) : (
              <div className="project-list">
                {projects.map((project) => {
                  const status = statusByProjectId.get(project.projectId) ?? {
                    projectId: project.projectId,
                    ingested: 0,
                    total: 0,
                    progressPercentage: 0,
                  }

                  return (
                    <article key={project.projectId} className="project-card">
                      <div className="project-card-header">
                        <div>
                          <p className="project-card-title">{project.projectName}</p>
                          <p className="project-card-meta">{project.projectId}</p>
                        </div>
                        <button
                          type="button"
                          className="feedback-button feedback-button-secondary project-edit-button"
                          onClick={() => handleEditProject(project)}
                        >
                          Edit
                        </button>
                      </div>

                      <div className="project-status-row">
                        <span className="project-chip">Ingestion Status</span>
                        <span className="data-mono">{status.ingested} / {status.total}</span>
                      </div>

                      <div className="project-progress-track" aria-hidden="true">
                        <span
                          className="project-progress-fill"
                          style={{ width: `${Math.max(0, Math.min(100, status.progressPercentage))}%` }}
                        />
                      </div>

                      <div className="project-status-row">
                        <span className="helper-text">Progress</span>
                        <span className="confidence-value">{status.progressPercentage}%</span>
                      </div>

                      <p className="project-card-copy">
                        Lookback: {project.knowledgeLookbackYears} year{project.knowledgeLookbackYears === 1 ? '' : 's'}
                        {' • '}
                        Similarity limit: {project.similaritySearchLimit}
                        {' • '}
                        Email: {project.sendEmail ? 'On' : 'Off'}
                        {' • '}
                        Pooling: {project.poolingEnabled ? 'On' : 'Off'}
                      </p>

                      <p className="project-card-meta">
                        Filter: {project.applicationFilter || project.projectName}
                      </p>
                    </article>
                  )
                })}
              </div>
            )}
          </div>
        </div>
      </section>
    </div>
  )
}

export default App
