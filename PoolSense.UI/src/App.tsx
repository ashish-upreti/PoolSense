import { useState, useEffect } from 'react'
import ChatPanel, { type AssistantMessage, type ChatMessage, type UserMessage } from './components/ChatPanel'
import GroupSelector from './components/GroupSelector'
import InsightPanel from './components/InsightPanel'
import { askPoolSense, getProjectGroups, type ProjectGroup, type TicketWorkflowResult } from './services/api'
import './App.css'

function App() {
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [insights, setInsights] = useState<TicketWorkflowResult | null>(null)
  const [isLoading, setIsLoading] = useState(false)
  const [error, setError] = useState('')
  const [groups, setGroups] = useState<ProjectGroup[]>([])
  // empty = All; non-empty = selected group IDs
  const [selectedGroupIds, setSelectedGroupIds] = useState<string[]>([])
  const [isDark, setIsDark] = useState(() => localStorage.getItem('theme') === 'dark')

  useEffect(() => {
    document.documentElement.dataset.theme = isDark ? 'dark' : 'light'
    localStorage.setItem('theme', isDark ? 'dark' : 'light')
  }, [isDark])

  useEffect(() => {
    getProjectGroups().then(setGroups).catch(() => setGroups([]))
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

  return (
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
  )
}

export default App
