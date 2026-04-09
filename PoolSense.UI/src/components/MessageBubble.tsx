import type { ChatMessage } from './ChatPanel'

interface MessageBubbleProps {
  message: ChatMessage
}

export default function MessageBubble({ message }: MessageBubbleProps) {
  if (message.role === 'user') {
    return (
      <article className="message-bubble message-bubble-user">
        <p className="message-label">You</p>
        <p className="message-body">{message.text}</p>
      </article>
    )
  }

  return (
    <article className="message-bubble message-bubble-assistant">
      <p className="message-label">PoolSense</p>
      <div className="assistant-summary">
        <section>
          <h3>Root Cause</h3>
          <p>{message.result.suggestedRootCause || 'Not provided'}</p>
        </section>
        <section>
          <h3>Resolution</h3>
          <p>{message.result.suggestedResolution || 'Not provided'}</p>
        </section>
      </div>
    </article>
  )
}