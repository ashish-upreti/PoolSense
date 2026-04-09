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
    project_id text PRIMARY KEY,
    project_name text NOT NULL,
    ticket_source_type text NOT NULL,
    connection_string text NOT NULL,
    knowledge_sources text[] NOT NULL DEFAULT '{}',
    is_active boolean NOT NULL DEFAULT TRUE
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

ANALYZE ticket_knowledge;
ANALYZE failure_patterns;
ANALYZE project_configs;
ANALYZE processed_source_events;