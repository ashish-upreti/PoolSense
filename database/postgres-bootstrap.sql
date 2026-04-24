-- Run this file directly with psql, for example:
-- Get-Content -Raw .\database\postgres-bootstrap.sql | docker exec -i pgvector-db psql -U postgres -d postgres
-- The script will create the target database if needed and then connect to it.

\set ON_ERROR_STOP on
\connect postgres

SELECT 'CREATE DATABASE poolsense'
WHERE NOT EXISTS (
    SELECT 1
    FROM pg_database
    WHERE datname = 'poolsense')
\gexec

\connect poolsense

CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS ticket_knowledge (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ticket_id text NOT NULL,
    source_event_id text NOT NULL DEFAULT '',
    problem text NOT NULL,
    root_cause text NOT NULL,
    resolution text NOT NULL,
    keywords text[] NOT NULL DEFAULT '{}',
    embedding vector(1536) NOT NULL,
    application text NOT NULL DEFAULT '',
    knowledge_year integer NOT NULL DEFAULT EXTRACT(YEAR FROM now()),
    source_status text NOT NULL DEFAULT '',
    source_submitted_at timestamptz,
    source_closed_at timestamptz,
    submitter_id text NOT NULL DEFAULT '',
    lifeguard_id text NOT NULL DEFAULT '',
    source_project text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS failure_patterns (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    system text NOT NULL,
    component text NOT NULL,
    failure_type text NOT NULL,
    resolution_category text NOT NULL,
    ticket_id text NOT NULL,
    source_event_id text NOT NULL DEFAULT '',
    application text NOT NULL DEFAULT '',
    knowledge_year integer NOT NULL DEFAULT EXTRACT(YEAR FROM now()),
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS processed_source_events (
    source_event_id text NOT NULL,
    processing_kind text NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT now(),
    email_sent boolean NOT NULL DEFAULT FALSE,
    email_recipient text NOT NULL DEFAULT '',
    workflow_result text NOT NULL DEFAULT '',
    PRIMARY KEY (source_event_id, processing_kind)
);

ALTER TABLE IF EXISTS ticket_knowledge ADD COLUMN IF NOT EXISTS source_event_id text NOT NULL DEFAULT '';
ALTER TABLE IF EXISTS ticket_knowledge ADD COLUMN IF NOT EXISTS problem text NOT NULL DEFAULT '';
ALTER TABLE IF EXISTS ticket_knowledge ADD COLUMN IF NOT EXISTS root_cause text NOT NULL DEFAULT '';
ALTER TABLE IF EXISTS ticket_knowledge ADD COLUMN IF NOT EXISTS resolution text NOT NULL DEFAULT '';
ALTER TABLE IF EXISTS ticket_knowledge ADD COLUMN IF NOT EXISTS keywords text[] NOT NULL DEFAULT '{}';
ALTER TABLE IF EXISTS ticket_knowledge ADD COLUMN IF NOT EXISTS application text NOT NULL DEFAULT '';
ALTER TABLE IF EXISTS ticket_knowledge ADD COLUMN IF NOT EXISTS knowledge_year integer NOT NULL DEFAULT EXTRACT(YEAR FROM now());
ALTER TABLE IF EXISTS ticket_knowledge ADD COLUMN IF NOT EXISTS source_status text NOT NULL DEFAULT '';
ALTER TABLE IF EXISTS ticket_knowledge ADD COLUMN IF NOT EXISTS source_submitted_at timestamptz;
ALTER TABLE IF EXISTS ticket_knowledge ADD COLUMN IF NOT EXISTS source_closed_at timestamptz;
ALTER TABLE IF EXISTS ticket_knowledge ADD COLUMN IF NOT EXISTS submitter_id text NOT NULL DEFAULT '';
ALTER TABLE IF EXISTS ticket_knowledge ADD COLUMN IF NOT EXISTS lifeguard_id text NOT NULL DEFAULT '';
ALTER TABLE IF EXISTS ticket_knowledge ADD COLUMN IF NOT EXISTS source_project text NOT NULL DEFAULT '';
ALTER TABLE IF EXISTS ticket_knowledge ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now();

DO $bootstrap$
BEGIN
        IF EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_name = 'ticket_knowledge'
                    AND column_name = 'content') THEN
        EXECUTE $sql$
                        UPDATE ticket_knowledge
                        SET problem = COALESCE(NULLIF(problem, ''), NULLIF(content, ''), 'Legacy ticket content')
                        WHERE COALESCE(problem, '') = '';
        $sql$;
        END IF;
END $bootstrap$;

UPDATE ticket_knowledge
SET root_cause = COALESCE(NULLIF(root_cause, ''), 'Legacy root cause unavailable')
WHERE COALESCE(root_cause, '') = '';

UPDATE ticket_knowledge
SET resolution = COALESCE(NULLIF(resolution, ''), 'Legacy resolution unavailable')
WHERE COALESCE(resolution, '') = '';

ALTER TABLE IF EXISTS failure_patterns ADD COLUMN IF NOT EXISTS source_event_id text NOT NULL DEFAULT '';
ALTER TABLE IF EXISTS failure_patterns ADD COLUMN IF NOT EXISTS application text NOT NULL DEFAULT '';
ALTER TABLE IF EXISTS failure_patterns ADD COLUMN IF NOT EXISTS knowledge_year integer NOT NULL DEFAULT EXTRACT(YEAR FROM now());
ALTER TABLE IF EXISTS failure_patterns ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now();

CREATE TABLE IF NOT EXISTS project_configs (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id text NOT NULL UNIQUE,
    project_name text NOT NULL,
    knowledge_lookback_years integer NOT NULL DEFAULT 2,
    similarity_search_limit integer NOT NULL DEFAULT 5,
    send_email boolean NOT NULL DEFAULT TRUE,
    pooling_enabled boolean NOT NULL DEFAULT TRUE,
    email_recipients text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now(),
    ticket_source_type text NOT NULL DEFAULT 'sql',
    connection_string text NOT NULL DEFAULT '',
    knowledge_sources text[] NOT NULL DEFAULT '{}',
    application_filter text NOT NULL DEFAULT ''
);

ALTER TABLE IF EXISTS project_configs ADD COLUMN IF NOT EXISTS id integer;
DO $project_configs_id$
DECLARE
    id_is_identity boolean := FALSE;
BEGIN
    SELECT COALESCE(is_identity = 'YES', FALSE)
    INTO id_is_identity
    FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = 'project_configs'
      AND column_name = 'id';

    IF NOT id_is_identity THEN
        EXECUTE 'CREATE SEQUENCE IF NOT EXISTS project_configs_id_seq OWNED BY project_configs.id';
        EXECUTE 'ALTER TABLE project_configs ALTER COLUMN id SET DEFAULT nextval(''project_configs_id_seq'')';

        UPDATE project_configs
        SET id = nextval('project_configs_id_seq')
        WHERE id IS NULL;

        PERFORM setval(
            'project_configs_id_seq',
            COALESCE((SELECT MAX(id) FROM project_configs), 1),
            COALESCE((SELECT MAX(id) IS NOT NULL FROM project_configs), FALSE));

        EXECUTE 'ALTER TABLE project_configs ALTER COLUMN id SET NOT NULL';
    END IF;
END $project_configs_id$;

DO $project_configs_pk$
DECLARE
    current_pk_name text;
    current_pk_columns text;
BEGIN
    SELECT c.conname,
           string_agg(a.attname, ',' ORDER BY key_columns.ordinality)
    INTO current_pk_name, current_pk_columns
    FROM pg_constraint c
    JOIN unnest(c.conkey) WITH ORDINALITY AS key_columns(attnum, ordinality) ON TRUE
    JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = key_columns.attnum
    WHERE c.contype = 'p'
      AND c.conrelid = 'project_configs'::regclass
    GROUP BY c.conname;

    IF current_pk_name IS NULL THEN
        EXECUTE 'ALTER TABLE project_configs ADD CONSTRAINT project_configs_pkey PRIMARY KEY (id)';
    ELSIF current_pk_columns <> 'id' THEN
        EXECUTE format('ALTER TABLE project_configs DROP CONSTRAINT %I', current_pk_name);
        EXECUTE 'ALTER TABLE project_configs ADD CONSTRAINT project_configs_pkey PRIMARY KEY (id)';
    END IF;
END $project_configs_pk$;

CREATE UNIQUE INDEX IF NOT EXISTS project_configs_project_id_uidx
    ON project_configs (project_id);

ALTER TABLE IF EXISTS project_configs ADD COLUMN IF NOT EXISTS knowledge_lookback_years integer NOT NULL DEFAULT 2;
ALTER TABLE IF EXISTS project_configs ADD COLUMN IF NOT EXISTS similarity_search_limit integer NOT NULL DEFAULT 5;
ALTER TABLE IF EXISTS project_configs ADD COLUMN IF NOT EXISTS send_email boolean NOT NULL DEFAULT TRUE;
ALTER TABLE IF EXISTS project_configs ADD COLUMN IF NOT EXISTS pooling_enabled boolean NOT NULL DEFAULT TRUE;
ALTER TABLE IF EXISTS project_configs ADD COLUMN IF NOT EXISTS email_recipients text NOT NULL DEFAULT '';
ALTER TABLE IF EXISTS project_configs ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE IF EXISTS project_configs ADD COLUMN IF NOT EXISTS ticket_source_type text NOT NULL DEFAULT 'sql';
ALTER TABLE IF EXISTS project_configs ADD COLUMN IF NOT EXISTS connection_string text NOT NULL DEFAULT '';
ALTER TABLE IF EXISTS project_configs ADD COLUMN IF NOT EXISTS knowledge_sources text[] NOT NULL DEFAULT '{}';
ALTER TABLE IF EXISTS project_configs ADD COLUMN IF NOT EXISTS application_filter text NOT NULL DEFAULT '';

UPDATE project_configs
SET pooling_enabled = COALESCE(pooling_enabled, TRUE),
    send_email = COALESCE(send_email, TRUE),
    knowledge_lookback_years = COALESCE(knowledge_lookback_years, 2),
    similarity_search_limit = COALESCE(similarity_search_limit, 5),
    email_recipients = COALESCE(email_recipients, ''),
    ticket_source_type = COALESCE(NULLIF(ticket_source_type, ''), 'sql'),
    connection_string = COALESCE(connection_string, ''),
    application_filter = COALESCE(NULLIF(application_filter, ''), project_name),
    created_at = COALESCE(created_at, now());

DO $project_configs_similarity_constraint$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'project_configs_similarity_search_limit_chk') THEN
        ALTER TABLE project_configs
        ADD CONSTRAINT project_configs_similarity_search_limit_chk
        CHECK (similarity_search_limit BETWEEN 1 AND 20);
    END IF;
END $project_configs_similarity_constraint$;

CREATE TABLE IF NOT EXISTS ingestion_status (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id text NOT NULL UNIQUE,
    total_tickets integer NOT NULL DEFAULT 0,
    ingested_tickets integer NOT NULL DEFAULT 0,
    last_updated timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS ingestion_status_project_id_uidx
    ON ingestion_status (project_id);

CREATE INDEX IF NOT EXISTS ingestion_status_last_updated_idx
    ON ingestion_status (last_updated DESC);

CREATE TABLE IF NOT EXISTS feedback_logs (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ticket_query text NOT NULL,
    suggested_resolution text NOT NULL,
    feedback_type integer NOT NULL,
    was_used boolean NOT NULL DEFAULT FALSE,
    comment text NOT NULL DEFAULT '',
    retrieved_ticket_ids text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE IF EXISTS feedback_logs ADD COLUMN IF NOT EXISTS was_used boolean NOT NULL DEFAULT FALSE;

CREATE TABLE IF NOT EXISTS interaction_logs (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    query text NOT NULL,
    generated_embedding_length integer NOT NULL DEFAULT 0,
    retrieved_ticket_ids text NOT NULL DEFAULT '',
    retrieved_contents text NOT NULL DEFAULT '',
    suggested_resolution text NOT NULL DEFAULT '',
    confidence real NOT NULL DEFAULT 0,
    processing_time_ms integer NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ticket_knowledge_created_at_idx
    ON ticket_knowledge (created_at DESC);

CREATE INDEX IF NOT EXISTS ticket_knowledge_application_year_idx
    ON ticket_knowledge (application, knowledge_year, created_at DESC);

CREATE INDEX IF NOT EXISTS ticket_knowledge_source_event_idx
    ON ticket_knowledge (source_event_id);

CREATE INDEX IF NOT EXISTS failure_patterns_created_at_idx
    ON failure_patterns (created_at DESC);

CREATE INDEX IF NOT EXISTS failure_patterns_application_year_idx
    ON failure_patterns (application, knowledge_year, created_at DESC);

CREATE INDEX IF NOT EXISTS failure_patterns_system_idx
    ON failure_patterns (system);

CREATE INDEX IF NOT EXISTS failure_patterns_component_idx
    ON failure_patterns (component);

CREATE INDEX IF NOT EXISTS ticket_knowledge_embedding_cosine_idx
    ON ticket_knowledge
    USING hnsw (embedding vector_cosine_ops);

CREATE INDEX IF NOT EXISTS processed_source_events_processed_at_idx
    ON processed_source_events (processed_at DESC);

CREATE INDEX IF NOT EXISTS feedback_logs_created_at_idx
    ON feedback_logs (created_at DESC);

CREATE INDEX IF NOT EXISTS interaction_logs_created_at_idx
    ON interaction_logs (created_at DESC);

ANALYZE ticket_knowledge;
ANALYZE failure_patterns;
ANALYZE project_configs;
ANALYZE ingestion_status;
ANALYZE processed_source_events;
ANALYZE feedback_logs;
ANALYZE interaction_logs;