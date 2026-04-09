docker exec -it pgvector-db psql -U postgres

\c poolassist

-- Confirm existing knowledge records are already in scope
SELECT application, COUNT(*) 
FROM ticket_knowledge 
GROUP BY application 
ORDER BY COUNT(*) DESC;

-- And failure patterns
SELECT application, COUNT(*) 
FROM failure_patterns 
GROUP BY application 
ORDER BY COUNT(*) DESC;

--NEW ticket test
SELECT source_event_id, processed_at, email_sent, email_recipient
FROM processed_source_events
WHERE processing_kind = 'NewRecommendation'
ORDER BY processed_at DESC;

-- Delete them so the poller retries
DELETE FROM processed_source_events
WHERE processing_kind = 'NewRecommendation';