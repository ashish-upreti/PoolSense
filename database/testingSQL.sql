-- Run this script in SQL Server Management Studio, Azure Data Studio, or with sqlcmd.
-- Example with Windows auth:
-- sqlcmd -S atcrsqldev.intel.com,3180 -d PoolSense -E -i .\database\testingSQL.sql

-- PoolSense SQL Server testing script
-- Notes:
-- 1. Replace every 'replace-me-*' placeholder before executing targeted queries.
-- 2. DELETE examples are wrapped in BEGIN TRAN/ROLLBACK so they are safe by default.
-- 3. Switch ROLLBACK to COMMIT only when you intentionally want to persist the delete.

-- -----------------------------------------------------------------------------
-- 0. Connectivity and schema inventory
-- -----------------------------------------------------------------------------
SELECT DB_NAME() AS database_name,
		 SUSER_SNAME() AS database_user,
		 SYSDATETIMEOFFSET() AS connected_at;

SELECT t.name AS table_name
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = 'dbo'
  AND t.name IN (
		'ticket_knowledge',
		'failure_patterns',
		'processed_source_events',
		'project_configs',
		'ingestion_status',
		'feedback_logs',
		'application_feedback_logs',
		'interaction_logs',
		'application_run_logs',
		'llm_token_usage')
ORDER BY t.name;

SELECT c.name AS column_name,
		 ty.name AS type_name,
		 c.max_length,
		 c.is_nullable
FROM sys.columns c
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.ticket_knowledge')
  AND c.name = 'embedding';

SELECT i.name AS index_name,
		 OBJECT_SCHEMA_NAME(i.object_id) AS schema_name,
		 OBJECT_NAME(i.object_id) AS table_name,
		 i.type_desc AS index_type,
		 STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) AS indexed_columns
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id IN (
		OBJECT_ID(N'dbo.ticket_knowledge'),
		OBJECT_ID(N'dbo.failure_patterns'),
		OBJECT_ID(N'dbo.processed_source_events'))
  AND i.name IN (
		'IX_ticket_knowledge_created_at',
		'IX_ticket_knowledge_application_year',
		'IX_failure_patterns_application_year',
		'IX_processed_source_events_processed_at')
GROUP BY i.name, i.object_id, i.type_desc
ORDER BY i.name;

-- -----------------------------------------------------------------------------
-- 1. Quick row counts for every PoolSense table
-- -----------------------------------------------------------------------------
SELECT 'application_run_logs' AS table_name, COUNT(*) AS row_count FROM dbo.application_run_logs
UNION ALL
SELECT 'application_feedback_logs' AS table_name, COUNT(*) AS row_count FROM dbo.application_feedback_logs
UNION ALL
SELECT 'failure_patterns' AS table_name, COUNT(*) AS row_count FROM dbo.failure_patterns
UNION ALL
SELECT 'feedback_logs' AS table_name, COUNT(*) AS row_count FROM dbo.feedback_logs
UNION ALL
SELECT 'ingestion_status' AS table_name, COUNT(*) AS row_count FROM dbo.ingestion_status
UNION ALL
SELECT 'interaction_logs' AS table_name, COUNT(*) AS row_count FROM dbo.interaction_logs
UNION ALL
SELECT 'llm_token_usage' AS table_name, COUNT(*) AS row_count FROM dbo.llm_token_usage
UNION ALL
SELECT 'processed_source_events' AS table_name, COUNT(*) AS row_count FROM dbo.processed_source_events
UNION ALL
SELECT 'project_configs' AS table_name, COUNT(*) AS row_count FROM dbo.project_configs
UNION ALL
SELECT 'ticket_knowledge' AS table_name, COUNT(*) AS row_count FROM dbo.ticket_knowledge
ORDER BY table_name;

-- -----------------------------------------------------------------------------
-- 2. General SELECT examples for every table
-- -----------------------------------------------------------------------------

-- ticket_knowledge: avoid selecting the full embedding column unless needed
SELECT TOP (10) id,
		 ticket_id,
		 source_event_id,
		 application,
		 problem,
		 root_cause,
		 resolution,
		 knowledge_year,
		 created_at
FROM dbo.ticket_knowledge
ORDER BY created_at DESC;

SELECT application,
		 COUNT(*) AS ticket_count
FROM dbo.ticket_knowledge
GROUP BY application
ORDER BY ticket_count DESC, application ASC;

SELECT source_event_id,
		 ticket_id,
		 application,
		 created_at
FROM dbo.ticket_knowledge
WHERE application = 'AT MPS Capacity Response'
ORDER BY created_at DESC;

-- failure_patterns
SELECT TOP (10) id,
		 system,
		 component,
		 failure_type,
		 resolution_category,
		 ticket_id,
		 application,
		 created_at
FROM dbo.failure_patterns
ORDER BY created_at DESC;

SELECT application,
		 COUNT(*) AS pattern_count
FROM dbo.failure_patterns
GROUP BY application
ORDER BY pattern_count DESC, application ASC;

-- processed_source_events
SELECT TOP (20) source_event_id,
		 processing_kind,
		 processed_at,
		 email_sent,
		 email_recipient
FROM dbo.processed_source_events
ORDER BY processed_at DESC;

SELECT processing_kind,
		 COUNT(*) AS event_count
FROM dbo.processed_source_events
GROUP BY processing_kind
ORDER BY processing_kind ASC;

SELECT source_event_id,
		 processed_at,
		 email_sent,
		 email_recipient
FROM dbo.processed_source_events
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
FROM dbo.project_configs
ORDER BY created_at DESC, project_name ASC;

SELECT pc.project_id,
		 project_name,
		 ticket_source_type,
		 application_filter,
		 ISNULL((SELECT COUNT(*) FROM OPENJSON(pc.knowledge_sources)), 0) AS knowledge_source_count
FROM dbo.project_configs pc
ORDER BY project_name ASC;

-- ingestion_status
SELECT id,
		 project_id,
		 ingested_tickets,
		 total_tickets,
		 CASE
			  WHEN total_tickets = 0 THEN CAST(0 AS decimal(10, 2))
			  ELSE ROUND((CAST(ingested_tickets AS decimal(18, 4)) / CAST(total_tickets AS decimal(18, 4))) * 100, 2)
		 END AS progress_percentage,
		 last_updated
FROM dbo.ingestion_status
ORDER BY last_updated DESC, project_id ASC;

-- feedback_logs
SELECT id,
		 ticket_query,
		 feedback_type,
		 was_used,
		 comment,
		 retrieved_ticket_ids,
		 created_at
FROM dbo.feedback_logs
ORDER BY created_at DESC;

SELECT feedback_type,
		 COUNT(*) AS feedback_count
FROM dbo.feedback_logs
GROUP BY feedback_type
ORDER BY feedback_type ASC;

-- application_feedback_logs
SELECT TOP (20) id,
		 user_name,
		 user_email,
		 feedback_type,
		 LEFT(message, 200) AS message_preview,
		 created_at
FROM dbo.application_feedback_logs
ORDER BY created_at DESC;

SELECT feedback_type,
		 COUNT(*) AS feedback_count
FROM dbo.application_feedback_logs
GROUP BY feedback_type
ORDER BY feedback_type ASC;

-- interaction_logs
SELECT TOP (20) id,
		 LEFT(query, 120) AS query_preview,
		 generated_embedding_length,
		 confidence,
		 processing_time_ms,
		 created_at
FROM dbo.interaction_logs
ORDER BY created_at DESC;

SELECT COUNT(*) AS interaction_count,
		 CAST(ROUND(AVG(CAST(confidence AS decimal(18, 6))), 4) AS decimal(18, 4)) AS average_confidence,
		 MAX(processing_time_ms) AS max_processing_time_ms
FROM dbo.interaction_logs;

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
			  WHEN COALESCE(is1.total_tickets, 0) = 0 THEN CAST(0 AS decimal(10, 2))
			  ELSE ROUND((CAST(COALESCE(is1.ingested_tickets, 0) AS decimal(18, 4)) / CAST(is1.total_tickets AS decimal(18, 4))) * 100, 2)
		 END AS progress_percentage,
		 is1.last_updated
FROM dbo.project_configs pc
LEFT JOIN dbo.ingestion_status is1
	 ON is1.project_id = pc.project_id
ORDER BY pc.project_name ASC;

-- Source events that have knowledge rows but no processed-source-event record
SELECT TOP (25) tk.source_event_id,
		 tk.ticket_id,
		 tk.application,
		 tk.created_at
FROM dbo.ticket_knowledge tk
LEFT JOIN dbo.processed_source_events pse
	 ON pse.source_event_id = tk.source_event_id
WHERE COALESCE(tk.source_event_id, '') <> ''
  AND pse.source_event_id IS NULL
ORDER BY tk.created_at DESC;

-- -----------------------------------------------------------------------------
-- 4. Targeted lookup templates
-- -----------------------------------------------------------------------------

SELECT *
FROM dbo.project_configs
WHERE project_id = 'replace-me-project-id';

SELECT *
FROM dbo.application_feedback_logs
WHERE user_email = 'replace-me-user-email@example.com';

SELECT *
FROM dbo.ingestion_status
WHERE project_id = 'replace-me-project-id';

SELECT id,
		 ticket_id,
		 source_event_id,
		 application,
		 created_at
FROM dbo.ticket_knowledge
WHERE ticket_id = 'replace-me-ticket-id'
	OR source_event_id = 'replace-me-source-event-id';

SELECT *
FROM dbo.failure_patterns
WHERE ticket_id = 'replace-me-ticket-id'
	OR source_event_id = 'replace-me-source-event-id';

SELECT *
FROM dbo.processed_source_events
WHERE source_event_id = 'replace-me-source-event-id';

-- -----------------------------------------------------------------------------
-- 5. Safe DELETE examples for every table
-- -----------------------------------------------------------------------------

-- ticket_knowledge: delete a single ticket or a single source event
BEGIN TRAN;
DELETE FROM dbo.ticket_knowledge
WHERE ticket_id = 'replace-me-ticket-id'
	OR source_event_id = 'replace-me-source-event-id';
SELECT COUNT(*) AS remaining_ticket_knowledge_rows
FROM dbo.ticket_knowledge
WHERE ticket_id = 'replace-me-ticket-id'
	OR source_event_id = 'replace-me-source-event-id';
ROLLBACK;

-- failure_patterns: delete only the rows tied to a known ticket/source event
BEGIN TRAN;
DELETE FROM dbo.failure_patterns
WHERE ticket_id = 'replace-me-ticket-id'
	OR source_event_id = 'replace-me-source-event-id';
SELECT COUNT(*) AS remaining_failure_pattern_rows
FROM dbo.failure_patterns
WHERE ticket_id = 'replace-me-ticket-id'
	OR source_event_id = 'replace-me-source-event-id';
ROLLBACK;

-- processed_source_events: retry a specific event
BEGIN TRAN;
DELETE FROM dbo.processed_source_events
WHERE source_event_id = 'replace-me-source-event-id'
  AND processing_kind = 'NewRecommendation';
SELECT COUNT(*) AS remaining_processed_source_event_rows
FROM dbo.processed_source_events
WHERE source_event_id = 'replace-me-source-event-id'
  AND processing_kind = 'NewRecommendation';
ROLLBACK;

-- processed_source_events: bulk retry all NewRecommendation events
BEGIN TRAN;
DELETE FROM dbo.processed_source_events
WHERE processing_kind = 'NewRecommendation';
SELECT COUNT(*) AS remaining_new_recommendation_rows
FROM dbo.processed_source_events
WHERE processing_kind = 'NewRecommendation';
ROLLBACK;

-- project_configs: delete one project configuration by project_id
BEGIN TRAN;
DELETE FROM dbo.project_configs
WHERE project_id = 'replace-me-project-id';
SELECT COUNT(*) AS remaining_project_config_rows
FROM dbo.project_configs
WHERE project_id = 'replace-me-project-id';
ROLLBACK;

-- ingestion_status: delete one status row by project_id
BEGIN TRAN;
DELETE FROM dbo.ingestion_status
WHERE project_id = 'replace-me-project-id';
SELECT COUNT(*) AS remaining_ingestion_status_rows
FROM dbo.ingestion_status
WHERE project_id = 'replace-me-project-id';
ROLLBACK;

-- feedback_logs: delete a specific feedback row by id
BEGIN TRAN;
DELETE FROM dbo.feedback_logs
WHERE id = -1;
SELECT COUNT(*) AS remaining_feedback_rows
FROM dbo.feedback_logs
WHERE id = -1;
ROLLBACK;

-- application_feedback_logs: delete a specific application feedback row by id
BEGIN TRAN;
DELETE FROM dbo.application_feedback_logs
WHERE id = -1;
SELECT COUNT(*) AS remaining_application_feedback_rows
FROM dbo.application_feedback_logs
WHERE id = -1;
ROLLBACK;

-- interaction_logs: delete a specific interaction row by id
BEGIN TRAN;
DELETE FROM dbo.interaction_logs
WHERE id = -1;
SELECT COUNT(*) AS remaining_interaction_rows
FROM dbo.interaction_logs
WHERE id = -1;
ROLLBACK;

-- project cleanup sequence: remove ingestion status and config together
BEGIN TRAN;
DELETE FROM dbo.ingestion_status
WHERE project_id = 'replace-me-project-id';
DELETE FROM dbo.project_configs
WHERE project_id = 'replace-me-project-id';
SELECT COUNT(*) AS remaining_project_rows
FROM dbo.project_configs
WHERE project_id = 'replace-me-project-id';
SELECT COUNT(*) AS remaining_ingestion_rows
FROM dbo.ingestion_status
WHERE project_id = 'replace-me-project-id';
ROLLBACK;

--Table dumps for quick reference during testing/debugging - review before executing
SELECT * FROM [dbo].[application_run_logs]
SELECT * FROM [dbo].[application_feedback_logs]
SELECT * FROM  [dbo].[failure_patterns]
SELECT * FROM  [dbo].[feedback_logs]
SELECT * FROM  [dbo].[ingestion_status]
SELECT * FROM  [dbo].[interaction_logs]
SELECT * FROM  [dbo].[processed_source_events]
SELECT * FROM  [dbo].[project_configs]
SELECT * FROM  [dbo].[ticket_knowledge]


-- Created by GitHub Copilot in SSMS - review carefully before executing
SELECT 
    service_type,
    COUNT(*)                    AS [Call Count],
    SUM(prompt_tokens)          AS [Prompt Tokens],
    SUM(completion_tokens)      AS [Completion Tokens],
    SUM(total_tokens)           AS [Total Tokens]
FROM [dbo].[llm_token_usage]
GROUP BY service_type
ORDER BY SUM(total_tokens) DESC

-- Pricing: text-embedding-3-large $0.13/1M | gpt-5.4 input $2.50/1M | gpt-5.4 output $15.00/1M
SELECT 
    service_type,
    model,
    COUNT(*)                                                                AS [Call Count],
    SUM(prompt_tokens)                                                      AS [Total Input Tokens],
    SUM(completion_tokens)                                                  AS [Total Output Tokens],
    CAST(SUM(prompt_tokens) / 1000000.0 * 
        CASE model
            WHEN 'text-embedding-3-large' THEN 0.13 
            WHEN 'gpt-5.4'               THEN 2.50 
            ELSE 0.00
        END AS DECIMAL(10,6))                                               AS [Est. Input Price (USD)],
    CAST(SUM(completion_tokens) / 1000000.0 * 
        CASE model
            WHEN 'text-embedding-3-large' THEN 0.00 
            WHEN 'gpt-5.4'               THEN 15.00 
            ELSE 0.00
        END AS DECIMAL(10,6))                                               AS [Est. Output Price (USD)],
    CAST(
        (SUM(prompt_tokens) / 1000000.0 * 
            CASE model WHEN 'text-embedding-3-large' THEN 0.13 WHEN 'gpt-5.4' THEN 2.50 ELSE 0.00 END) +
        (SUM(completion_tokens) / 1000000.0 * 
            CASE model WHEN 'text-embedding-3-large' THEN 0.00 WHEN 'gpt-5.4' THEN 15.00 ELSE 0.00 END)
    AS DECIMAL(10,6))                                                       AS [Est. Total Price (USD)]
FROM [dbo].[llm_token_usage]
GROUP BY service_type, model
ORDER BY SUM(prompt_tokens) DESC