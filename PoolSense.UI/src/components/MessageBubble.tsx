import { useMemo, useState } from 'react'
import { submitFeedback } from '../services/api'
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

  return <AssistantMessageBubble message={message} />
}

interface AssistantMessageBubbleProps {
  message: Extract<ChatMessage, { role: 'assistant' }>
}

function AssistantMessageBubble({ message }: AssistantMessageBubbleProps) {
  const [comment, setComment] = useState('')
  const [isSubmittingFeedback, setIsSubmittingFeedback] = useState(false)
  const [feedbackSubmitted, setFeedbackSubmitted] = useState(false)
  const [feedbackError, setFeedbackError] = useState('')
  const [selectedFeedbackType, setSelectedFeedbackType] = useState<number | null>(null)

  const retrievedTicketIds = useMemo(
    () => message.result.similarIncidents.map((incident) => incident.ticketId).filter((ticketId) => ticketId.trim().length > 0),
    [message.result.similarIncidents],
  )

  const isFeedbackDisabled = feedbackSubmitted || isSubmittingFeedback || retrievedTicketIds.length === 0

  async function handleFeedback(feedbackType: number) {
    if (isFeedbackDisabled) {
      return
    }

    setIsSubmittingFeedback(true)
    setFeedbackError('')

    try {
      await submitFeedback({
        query: message.query,
        suggestedResolution: message.result.suggestedResolution,
        feedbackType,
        comment: comment.trim() || undefined,
        retrievedTicketIds,
      })

      setSelectedFeedbackType(feedbackType)
      setFeedbackSubmitted(true)
    } catch (error) {
      setFeedbackError(error instanceof Error ? error.message : 'Unable to submit feedback.')
    } finally {
      setIsSubmittingFeedback(false)
    }
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
      <div className="feedback-panel">
        <label className="feedback-label" htmlFor={`feedback-comment-${message.id}`}>
          Optional comment
        </label>
        <textarea
          id={`feedback-comment-${message.id}`}
          className="feedback-comment-input"
          value={comment}
          onChange={(event) => setComment(event.target.value)}
          placeholder="Add context about this response"
          rows={2}
          disabled={isFeedbackDisabled}
        />
        <div className="feedback-actions">
          <button
            type="button"
            className="feedback-button"
            onClick={() => void handleFeedback(1)}
            disabled={isFeedbackDisabled}
          >
            Helpful
          </button>
          <button
            type="button"
            className="feedback-button feedback-button-secondary"
            onClick={() => void handleFeedback(-1)}
            disabled={isFeedbackDisabled}
          >
            Not Helpful
          </button>
        </div>
        {!feedbackSubmitted && retrievedTicketIds.length === 0 ? (
          <p className="feedback-status">Feedback is unavailable because no similar incident ids were returned.</p>
        ) : null}
        {feedbackSubmitted ? (
          <p className="feedback-status">
            Feedback submitted{selectedFeedbackType === 1 ? ': marked helpful.' : ': marked not helpful.'}
          </p>
        ) : null}
        {feedbackError ? <p className="error-banner">{feedbackError}</p> : null}
      </div>
    </article>
  )
}