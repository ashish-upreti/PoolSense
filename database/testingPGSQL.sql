-- Run from a shell first:
-- docker exec -it pgvector-db psql -U postgres -d poolsense

-- Then connect inside psql:
-- \c poolsense

-- PoolSense PostgreSQL testing script
-- Notes:
-- 1. Replace every 'replace-me-*' placeholder before executing targeted queries.
-- 2. DELETE examples are wrapped in BEGIN/ROLLBACK so they are safe by default.
-- 3. Switch ROLLBACK to COMMIT only when you intentionally want to persist the delete.

-- -----------------------------------------------------------------------------
-- 0. Connectivity and schema inventory
-- -----------------------------------------------------------------------------
SELECT current_database() AS database_name,
		 current_user AS database_user,
		 now() AS connected_at;

SELECT table_name
FROM information_schema.tables
WHERE table_schema = 'public'
  AND table_name IN (
		'ticket_knowledge',
		'failure_patterns',
		'processed_source_events',
		'project_configs',
		'ingestion_status',
		'feedback_logs',
		'interaction_logs')
ORDER BY table_name;

-- -----------------------------------------------------------------------------
-- 1. Quick row counts for every PoolSense table
-- -----------------------------------------------------------------------------
SELECT 'failure_patterns' AS table_name, COUNT(*) AS row_count FROM failure_patterns
UNION ALL
SELECT 'feedback_logs' AS table_name, COUNT(*) AS row_count FROM feedback_logs
UNION ALL
SELECT 'ingestion_status' AS table_name, COUNT(*) AS row_count FROM ingestion_status
UNION ALL
SELECT 'interaction_logs' AS table_name, COUNT(*) AS row_count FROM interaction_logs
UNION ALL
SELECT 'processed_source_events' AS table_name, COUNT(*) AS row_count FROM processed_source_events
UNION ALL
SELECT 'project_configs' AS table_name, COUNT(*) AS row_count FROM project_configs
UNION ALL
SELECT 'ticket_knowledge' AS table_name, COUNT(*) AS row_count FROM ticket_knowledge
ORDER BY table_name;

-- -----------------------------------------------------------------------------
-- 2. General SELECT examples for every table
-- -----------------------------------------------------------------------------

-- ticket_knowledge: avoid selecting the full embedding column unless needed
SELECT id,
		 ticket_id,
		 source_event_id,
		 application,
		 problem,
		 root_cause,
		 resolution,
		 knowledge_year,
		 created_at
FROM ticket_knowledge
ORDER BY created_at DESC
LIMIT 10;

SELECT application,
		 COUNT(*) AS ticket_count
FROM ticket_knowledge
GROUP BY application
ORDER BY ticket_count DESC, application ASC;

SELECT source_event_id,
		 ticket_id,
		 application,
		 created_at
FROM ticket_knowledge
WHERE application = 'AT MPS Capacity Response'
ORDER BY created_at DESC;

-- failure_patterns
SELECT id,
		 system,
		 component,
		 failure_type,
		 resolution_category,
		 ticket_id,
		 application,
		 created_at
FROM failure_patterns
ORDER BY created_at DESC
LIMIT 10;

SELECT application,
		 COUNT(*) AS pattern_count
FROM failure_patterns
GROUP BY application
ORDER BY pattern_count DESC, application ASC;

-- processed_source_events
SELECT source_event_id,
		 processing_kind,
		 processed_at,
		 email_sent,
		 email_recipient
FROM processed_source_events
ORDER BY processed_at DESC
LIMIT 20;

SELECT processing_kind,
		 COUNT(*) AS event_count
FROM processed_source_events
GROUP BY processing_kind
ORDER BY processing_kind ASC;

SELECT source_event_id,
		 processed_at,
		 email_sent,
		 email_recipient
FROM processed_source_events
WHERE processing_kind = 'NewRecommendation'
ORDER BY processed_at DESC;

-- project_configs
SELECT id,
		 project_id,
		 project_name,
		 knowledge_lookback_years,
		 similarity_search_limit,
		 send_email,
		 pooling_enabled,
		 email_recipients,
		 created_at
FROM project_configs
ORDER BY created_at DESC, project_name ASC;

SELECT project_id,
		 project_name,
		 ticket_source_type,
		 application_filter,
		 array_length(knowledge_sources, 1) AS knowledge_source_count
FROM project_configs
ORDER BY project_name ASC;

-- ingestion_status
SELECT id,
		 project_id,
		 ingested_tickets,
		 total_tickets,
		 CASE
			  WHEN total_tickets = 0 THEN 0
			  ELSE ROUND((ingested_tickets::numeric / total_tickets::numeric) * 100, 2)
		 END AS progress_percentage,
		 last_updated
FROM ingestion_status
ORDER BY last_updated DESC, project_id ASC;

-- feedback_logs
SELECT id,
		 ticket_query,
		 feedback_type,
		 was_used,
		 comment,
		 retrieved_ticket_ids,
		 created_at
FROM feedback_logs
ORDER BY created_at DESC
LIMIT 20;

SELECT feedback_type,
		 COUNT(*) AS feedback_count
FROM feedback_logs
GROUP BY feedback_type
ORDER BY feedback_type ASC;

-- interaction_logs
SELECT id,
		 LEFT(query, 120) AS query_preview,
		 generated_embedding_length,
		 confidence,
		 processing_time_ms,
		 created_at
FROM interaction_logs
ORDER BY created_at DESC
LIMIT 20;

SELECT COUNT(*) AS interaction_count,
		 ROUND(AVG(confidence)::numeric, 4) AS average_confidence,
		 MAX(processing_time_ms) AS max_processing_time_ms
FROM interaction_logs;

-- -----------------------------------------------------------------------------
-- 3. Cross-table checks that are useful during debugging
-- -----------------------------------------------------------------------------

-- Project configuration paired with current ingestion progress
SELECT pc.project_id,
		 pc.project_name,
		 pc.pooling_enabled,
		 pc.send_email,
		 COALESCE(is1.ingested_tickets, 0) AS ingested_tickets,
		 COALESCE(is1.total_tickets, 0) AS total_tickets,
		 CASE
			  WHEN COALESCE(is1.total_tickets, 0) = 0 THEN 0
			  ELSE ROUND((COALESCE(is1.ingested_tickets, 0)::numeric / is1.total_tickets::numeric) * 100, 2)
		 END AS progress_percentage,
		 is1.last_updated
FROM project_configs pc
LEFT JOIN ingestion_status is1
	 ON is1.project_id = pc.project_id
ORDER BY pc.project_name ASC;

-- Source events that have knowledge rows but no processed-source-event record
SELECT tk.source_event_id,
		 tk.ticket_id,
		 tk.application,
		 tk.created_at
FROM ticket_knowledge tk
LEFT JOIN processed_source_events pse
	 ON pse.source_event_id = tk.source_event_id
WHERE COALESCE(tk.source_event_id, '') <> ''
  AND pse.source_event_id IS NULL
ORDER BY tk.created_at DESC
LIMIT 25;

-- -----------------------------------------------------------------------------
-- 4. Targeted lookup templates
-- -----------------------------------------------------------------------------

SELECT *
FROM project_configs
WHERE project_id = 'replace-me-project-id';

SELECT *
FROM ingestion_status
WHERE project_id = 'replace-me-project-id';

SELECT id,
		 ticket_id,
		 source_event_id,
		 application,
		 created_at
FROM ticket_knowledge
WHERE ticket_id = 'replace-me-ticket-id'
	OR source_event_id = 'replace-me-source-event-id';

SELECT *
FROM failure_patterns
WHERE ticket_id = 'replace-me-ticket-id'
	OR source_event_id = 'replace-me-source-event-id';

SELECT *
FROM processed_source_events
WHERE source_event_id = 'replace-me-source-event-id';

-- -----------------------------------------------------------------------------
-- 5. Safe DELETE examples for every table
-- -----------------------------------------------------------------------------

-- ticket_knowledge: delete a single ticket or a single source event
BEGIN;
DELETE FROM ticket_knowledge
WHERE ticket_id = 'replace-me-ticket-id'
	OR source_event_id = 'replace-me-source-event-id';
SELECT COUNT(*) AS remaining_ticket_knowledge_rows
FROM ticket_knowledge
WHERE ticket_id = 'replace-me-ticket-id'
	OR source_event_id = 'replace-me-source-event-id';
ROLLBACK;

-- failure_patterns: delete only the rows tied to a known ticket/source event
BEGIN;
DELETE FROM failure_patterns
WHERE ticket_id = 'replace-me-ticket-id'
	OR source_event_id = 'replace-me-source-event-id';
SELECT COUNT(*) AS remaining_failure_pattern_rows
FROM failure_patterns
WHERE ticket_id = 'replace-me-ticket-id'
	OR source_event_id = 'replace-me-source-event-id';
ROLLBACK;

-- processed_source_events: retry a specific event
BEGIN;
DELETE FROM processed_source_events
WHERE source_event_id = 'replace-me-source-event-id'
  AND processing_kind = 'NewRecommendation';
SELECT COUNT(*) AS remaining_processed_source_event_rows
FROM processed_source_events
WHERE source_event_id = 'replace-me-source-event-id'
  AND processing_kind = 'NewRecommendation';
ROLLBACK;

-- processed_source_events: bulk retry all NewRecommendation events
BEGIN;
DELETE FROM processed_source_events
WHERE processing_kind = 'NewRecommendation';
SELECT COUNT(*) AS remaining_new_recommendation_rows
FROM processed_source_events
WHERE processing_kind = 'NewRecommendation';
ROLLBACK;

-- project_configs: delete one project configuration by project_id
BEGIN;
DELETE FROM project_configs
WHERE project_id = 'replace-me-project-id';
SELECT COUNT(*) AS remaining_project_config_rows
FROM project_configs
WHERE project_id = 'replace-me-project-id';
ROLLBACK;

-- ingestion_status: delete one status row by project_id
BEGIN;
DELETE FROM ingestion_status
WHERE project_id = 'replace-me-project-id';
SELECT COUNT(*) AS remaining_ingestion_status_rows
FROM ingestion_status
WHERE project_id = 'replace-me-project-id';
ROLLBACK;

-- feedback_logs: delete a specific feedback row by id
BEGIN;
DELETE FROM feedback_logs
WHERE id = -1;
SELECT COUNT(*) AS remaining_feedback_rows
FROM feedback_logs
WHERE id = -1;
ROLLBACK;

-- interaction_logs: delete a specific interaction row by id
BEGIN;
DELETE FROM interaction_logs
WHERE id = -1;
SELECT COUNT(*) AS remaining_interaction_rows
FROM interaction_logs
WHERE id = -1;
ROLLBACK;

-- project cleanup sequence: remove ingestion status and config together
BEGIN;
DELETE FROM ingestion_status
WHERE project_id = 'replace-me-project-id';
DELETE FROM project_configs
WHERE project_id = 'replace-me-project-id';
SELECT COUNT(*) AS remaining_project_rows
FROM project_configs
WHERE project_id = 'replace-me-project-id';
SELECT COUNT(*) AS remaining_ingestion_rows
FROM ingestion_status
WHERE project_id = 'replace-me-project-id';
ROLLBACK;
