import { useState } from 'react'
import type { ReactNode } from 'react'
import type { TicketWorkflowResult } from '../services/api'
import MessageBubble from './MessageBubble'

export interface UserMessage {
  id: number
  role: 'user'
  text: string
}

export interface AssistantMessage {
  id: number
  role: 'assistant'
  text: string
  result: TicketWorkflowResult
}

export type ChatMessage = UserMessage | AssistantMessage

interface ChatPanelProps {
  messages: ChatMessage[]
  isLoading: boolean
  error: string
  onSend: (message: string) => Promise<void>
  groupSelector?: ReactNode
  isDark?: boolean
  onToggleDark?: () => void
}

const quickPrompts = [
  'VG item missing',
  'Data load job failed',
  'UI error',
]

export default function ChatPanel({ messages, isLoading, error, onSend, groupSelector, isDark, onToggleDark }: ChatPanelProps) {
  const [input, setInput] = useState('')

  async function handleSubmit(rawMessage: string) {
    const message = rawMessage.trim()

    if (!message || isLoading) {
      return
    }

    await onSend(message)
    setInput('')
  }

  return (
    <section className="workspace-panel workspace-chat">
      <header className="workspace-header">
        <div>
          <p className="panel-kicker">FSMS</p>
          <h1>Incident workspace</h1>
          <p className="workspace-copy">
            Detect. Diagnose. Resolve.
          </p>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: '0.6rem', flexShrink: 0 }}>
          <div className="workspace-banner">
            <span className="workspace-dot" />
            <span>{isLoading ? 'Analyzing live incident context' : 'Ready for triage'}</span>
          </div>
          <button
            type="button"
            className="dark-toggle"
            onClick={onToggleDark}
            aria-label={isDark ? 'Switch to light mode' : 'Switch to dark mode'}
            title={isDark ? 'Switch to light mode' : 'Switch to dark mode'}
          >
            {isDark ? '☀' : '🌙'}
          </button>
        </div>
      </header>

      <div className="prompt-strip" aria-label="Suggested prompts">
        {quickPrompts.map((prompt) => (
          <button
            key={prompt}
            type="button"
            className="prompt-chip"
            onClick={() => void handleSubmit(prompt)}
            disabled={isLoading}
          >
            {prompt}
          </button>
        ))}
      </div>

      {groupSelector}

      <div className="conversation-panel" role="log" aria-live="polite">
        {messages.length === 0 ? (
          <div className="empty-state">
            <p className="panel-kicker">Conversation</p>
            <h3>Describe an issue to start the investigation</h3>
            <p>
              Engineers should be able to see the conversation, suggested fix,
              similar incidents, and system insights together on one screen.
            </p>
          </div>
        ) : (
          messages.map((message) => <MessageBubble key={message.id} message={message} />)
        )}

        {isLoading ? (
          <article className="message-bubble message-bubble-loading">
            <p className="message-label">PoolSense</p>
            <p className="message-body">PoolSense is analyzing incidents...</p>
          </article>
        ) : null}
      </div>

      <form
        className="composer"
        onSubmit={(event) => {
          event.preventDefault()
          void handleSubmit(input)
        }}
      >
        <label className="sr-only" htmlFor="incident-input">
          Describe your issue
        </label>
        <textarea
          id="incident-input"
          className="composer-input"
          value={input}
          onChange={(event) => setInput(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter' && !event.shiftKey) {
              event.preventDefault()
              void handleSubmit(input)
            }
          }}
          placeholder="Describe your issue... (Enter to send, Shift+Enter for new line)"
          rows={3}
          disabled={isLoading}
        />
        <div className="composer-footer">
          <p className="helper-text">
            Add the service, symptom, and any recent deployment or dependency change.
          </p>
          <button type="submit" className="send-button" disabled={isLoading || !input.trim()}>
            Ask PoolSense
          </button>
        </div>
        {error ? <p className="error-banner">{error}</p> : null}
      </form>
    </section>
  )
}