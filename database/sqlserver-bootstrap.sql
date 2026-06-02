SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.ticket_knowledge', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ticket_knowledge (
        id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ticket_knowledge PRIMARY KEY,
        ticket_id nvarchar(128) NOT NULL,
        source_event_id nvarchar(128) NOT NULL CONSTRAINT DF_ticket_knowledge_source_event_id DEFAULT '',
        problem nvarchar(max) NOT NULL,
        root_cause nvarchar(max) NOT NULL,
        resolution nvarchar(max) NOT NULL,
        keywords nvarchar(max) NOT NULL CONSTRAINT DF_ticket_knowledge_keywords DEFAULT '[]',
        embedding nvarchar(max) NOT NULL,
        application nvarchar(256) NOT NULL CONSTRAINT DF_ticket_knowledge_application DEFAULT '',
        knowledge_year int NOT NULL CONSTRAINT DF_ticket_knowledge_knowledge_year DEFAULT YEAR(SYSUTCDATETIME()),
        source_status nvarchar(128) NOT NULL CONSTRAINT DF_ticket_knowledge_source_status DEFAULT '',
        source_submitted_at datetime2(7) NULL,
        source_closed_at datetime2(7) NULL,
        submitter_id nvarchar(128) NOT NULL CONSTRAINT DF_ticket_knowledge_submitter_id DEFAULT '',
        lifeguard_id nvarchar(128) NOT NULL CONSTRAINT DF_ticket_knowledge_lifeguard_id DEFAULT '',
        source_project nvarchar(256) NOT NULL CONSTRAINT DF_ticket_knowledge_source_project DEFAULT '',
        created_at datetime2(7) NOT NULL CONSTRAINT DF_ticket_knowledge_created_at DEFAULT SYSUTCDATETIME()
    );
END;
GO

IF OBJECT_ID(N'dbo.failure_patterns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.failure_patterns (
        id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_failure_patterns PRIMARY KEY,
        system nvarchar(256) NOT NULL,
        component nvarchar(256) NOT NULL,
        failure_type nvarchar(256) NOT NULL,
        resolution_category nvarchar(256) NOT NULL,
        ticket_id nvarchar(128) NOT NULL,
        source_event_id nvarchar(128) NOT NULL CONSTRAINT DF_failure_patterns_source_event_id DEFAULT '',
        application nvarchar(256) NOT NULL CONSTRAINT DF_failure_patterns_application DEFAULT '',
        knowledge_year int NOT NULL CONSTRAINT DF_failure_patterns_knowledge_year DEFAULT YEAR(SYSUTCDATETIME()),
        created_at datetime2(7) NOT NULL CONSTRAINT DF_failure_patterns_created_at DEFAULT SYSUTCDATETIME()
    );
END;
GO

IF OBJECT_ID(N'dbo.processed_source_events', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.processed_source_events (
        source_event_id nvarchar(128) NOT NULL,
        processing_kind nvarchar(128) NOT NULL,
        processed_at datetime2(7) NOT NULL CONSTRAINT DF_processed_source_events_processed_at DEFAULT SYSUTCDATETIME(),
        email_sent bit NOT NULL CONSTRAINT DF_processed_source_events_email_sent DEFAULT 0,
        email_recipient nvarchar(512) NOT NULL CONSTRAINT DF_processed_source_events_email_recipient DEFAULT '',
        workflow_result nvarchar(max) NOT NULL CONSTRAINT DF_processed_source_events_workflow_result DEFAULT '',
        CONSTRAINT PK_processed_source_events PRIMARY KEY (source_event_id, processing_kind)
    );
END;
GO

IF OBJECT_ID(N'dbo.project_configs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.project_configs (
        id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_project_configs PRIMARY KEY,
        project_id nvarchar(128) NOT NULL,
        project_name nvarchar(256) NOT NULL,
        knowledge_lookback_years int NOT NULL CONSTRAINT DF_project_configs_knowledge_lookback_years DEFAULT 2,
        similarity_search_limit int NOT NULL CONSTRAINT DF_project_configs_similarity_search_limit DEFAULT 5,
        send_email bit NOT NULL CONSTRAINT DF_project_configs_send_email DEFAULT 1,
        pooling_enabled bit NOT NULL CONSTRAINT DF_project_configs_pooling_enabled DEFAULT 1,
        email_recipients nvarchar(max) NOT NULL CONSTRAINT DF_project_configs_email_recipients DEFAULT '',
        created_at datetime2(7) NOT NULL CONSTRAINT DF_project_configs_created_at DEFAULT SYSUTCDATETIME(),
        ticket_source_type nvarchar(32) NOT NULL CONSTRAINT DF_project_configs_ticket_source_type DEFAULT 'sql',
        connection_string nvarchar(max) NOT NULL CONSTRAINT DF_project_configs_connection_string DEFAULT '',
        knowledge_sources nvarchar(max) NOT NULL CONSTRAINT DF_project_configs_knowledge_sources DEFAULT '[]',
        application_filter nvarchar(256) NOT NULL CONSTRAINT DF_project_configs_application_filter DEFAULT '',
        CONSTRAINT UQ_project_configs_project_id UNIQUE (project_id),
        CONSTRAINT CK_project_configs_similarity_search_limit CHECK (similarity_search_limit BETWEEN 1 AND 20)
    );
END;
GO

IF OBJECT_ID(N'dbo.ingestion_status', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ingestion_status (
        id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ingestion_status PRIMARY KEY,
        project_id nvarchar(128) NOT NULL,
        total_tickets int NOT NULL CONSTRAINT DF_ingestion_status_total_tickets DEFAULT 0,
        ingested_tickets int NOT NULL CONSTRAINT DF_ingestion_status_ingested_tickets DEFAULT 0,
        last_updated datetime2(7) NOT NULL CONSTRAINT DF_ingestion_status_last_updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_ingestion_status_project_id UNIQUE (project_id)
    );
END;
GO

IF OBJECT_ID(N'dbo.feedback_logs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.feedback_logs (
        id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_feedback_logs PRIMARY KEY,
        ticket_query nvarchar(max) NOT NULL,
        suggested_resolution nvarchar(max) NOT NULL,
        feedback_type int NOT NULL,
        was_used bit NOT NULL CONSTRAINT DF_feedback_logs_was_used DEFAULT 0,
        comment nvarchar(max) NOT NULL CONSTRAINT DF_feedback_logs_comment DEFAULT '',
        target_ticket_id nvarchar(128) NOT NULL CONSTRAINT DF_feedback_logs_target_ticket_id DEFAULT '',
        retrieved_ticket_ids nvarchar(max) NOT NULL,
        created_at datetime2(7) NOT NULL CONSTRAINT DF_feedback_logs_created_at DEFAULT SYSUTCDATETIME()
    );
END;
GO

IF OBJECT_ID(N'dbo.interaction_logs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.interaction_logs (
        id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_interaction_logs PRIMARY KEY,
        query nvarchar(max) NOT NULL,
        generated_embedding_length int NOT NULL CONSTRAINT DF_interaction_logs_generated_embedding_length DEFAULT 0,
        retrieved_ticket_ids nvarchar(max) NOT NULL CONSTRAINT DF_interaction_logs_retrieved_ticket_ids DEFAULT '',
        retrieved_contents nvarchar(max) NOT NULL CONSTRAINT DF_interaction_logs_retrieved_contents DEFAULT '',
        suggested_resolution nvarchar(max) NOT NULL CONSTRAINT DF_interaction_logs_suggested_resolution DEFAULT '',
        confidence real NOT NULL CONSTRAINT DF_interaction_logs_confidence DEFAULT 0,
        processing_time_ms int NOT NULL CONSTRAINT DF_interaction_logs_processing_time_ms DEFAULT 0,
        created_at datetime2(7) NOT NULL CONSTRAINT DF_interaction_logs_created_at DEFAULT SYSUTCDATETIME()
    );
END;
GO

IF OBJECT_ID(N'dbo.application_run_logs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.application_run_logs (
        id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_application_run_logs PRIMARY KEY,
        created_at datetime2(7) NOT NULL CONSTRAINT DF_application_run_logs_created_at DEFAULT SYSUTCDATETIME(),
        level nvarchar(32) NOT NULL,
        category nvarchar(256) NOT NULL,
        event_id int NOT NULL CONSTRAINT DF_application_run_logs_event_id DEFAULT 0,
        event_name nvarchar(256) NOT NULL CONSTRAINT DF_application_run_logs_event_name DEFAULT '',
        message nvarchar(max) NOT NULL,
        exception_type nvarchar(512) NOT NULL CONSTRAINT DF_application_run_logs_exception_type DEFAULT '',
        exception_message nvarchar(max) NOT NULL CONSTRAINT DF_application_run_logs_exception_message DEFAULT '',
        exception_stack_trace nvarchar(max) NOT NULL CONSTRAINT DF_application_run_logs_exception_stack_trace DEFAULT '',
        state_json nvarchar(max) NOT NULL CONSTRAINT DF_application_run_logs_state_json DEFAULT '',
        scopes_json nvarchar(max) NOT NULL CONSTRAINT DF_application_run_logs_scopes_json DEFAULT '',
        machine_name nvarchar(128) NOT NULL CONSTRAINT DF_application_run_logs_machine_name DEFAULT '',
        user_name nvarchar(256) NOT NULL CONSTRAINT DF_application_run_logs_user_name DEFAULT '',
        process_id int NOT NULL CONSTRAINT DF_application_run_logs_process_id DEFAULT 0,
        thread_id int NOT NULL CONSTRAINT DF_application_run_logs_thread_id DEFAULT 0,
        environment_name nvarchar(128) NOT NULL CONSTRAINT DF_application_run_logs_environment_name DEFAULT '',
        application_name nvarchar(128) NOT NULL CONSTRAINT DF_application_run_logs_application_name DEFAULT ''
    );
END;
GO

IF OBJECT_ID(N'dbo.llm_token_usage', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.llm_token_usage (
        id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_llm_token_usage PRIMARY KEY,
        created_at datetime2(7) NOT NULL CONSTRAINT DF_llm_token_usage_created_at DEFAULT SYSUTCDATETIME(),
        service_type nvarchar(32) NOT NULL,
        operation_name nvarchar(128) NOT NULL,
        provider nvarchar(64) NOT NULL CONSTRAINT DF_llm_token_usage_provider DEFAULT '',
        model nvarchar(128) NOT NULL CONSTRAINT DF_llm_token_usage_model DEFAULT '',
        deployment_name nvarchar(128) NOT NULL CONSTRAINT DF_llm_token_usage_deployment_name DEFAULT '',
        prompt_tokens int NOT NULL CONSTRAINT DF_llm_token_usage_prompt_tokens DEFAULT 0,
        completion_tokens int NOT NULL CONSTRAINT DF_llm_token_usage_completion_tokens DEFAULT 0,
        total_tokens int NOT NULL CONSTRAINT DF_llm_token_usage_total_tokens DEFAULT 0,
        is_estimated bit NOT NULL CONSTRAINT DF_llm_token_usage_is_estimated DEFAULT 0,
        input_characters int NOT NULL CONSTRAINT DF_llm_token_usage_input_characters DEFAULT 0,
        output_characters int NOT NULL CONSTRAINT DF_llm_token_usage_output_characters DEFAULT 0,
        vector_dimensions int NULL,
        latency_ms int NOT NULL CONSTRAINT DF_llm_token_usage_latency_ms DEFAULT 0,
        success bit NOT NULL CONSTRAINT DF_llm_token_usage_success DEFAULT 1,
        error_message nvarchar(max) NOT NULL CONSTRAINT DF_llm_token_usage_error_message DEFAULT '',
        correlation_id nvarchar(128) NOT NULL CONSTRAINT DF_llm_token_usage_correlation_id DEFAULT '',
        machine_name nvarchar(128) NOT NULL CONSTRAINT DF_llm_token_usage_machine_name DEFAULT '',
        user_name nvarchar(256) NOT NULL CONSTRAINT DF_llm_token_usage_user_name DEFAULT '',
        process_id int NOT NULL CONSTRAINT DF_llm_token_usage_process_id DEFAULT 0
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ticket_knowledge_created_at' AND object_id = OBJECT_ID(N'dbo.ticket_knowledge'))
    CREATE INDEX IX_ticket_knowledge_created_at ON dbo.ticket_knowledge (created_at DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ticket_knowledge_application_year' AND object_id = OBJECT_ID(N'dbo.ticket_knowledge'))
    CREATE INDEX IX_ticket_knowledge_application_year ON dbo.ticket_knowledge (application, knowledge_year, created_at DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ticket_knowledge_source_event_id' AND object_id = OBJECT_ID(N'dbo.ticket_knowledge'))
    CREATE INDEX IX_ticket_knowledge_source_event_id ON dbo.ticket_knowledge (source_event_id);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_failure_patterns_created_at' AND object_id = OBJECT_ID(N'dbo.failure_patterns'))
    CREATE INDEX IX_failure_patterns_created_at ON dbo.failure_patterns (created_at DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_failure_patterns_application_year' AND object_id = OBJECT_ID(N'dbo.failure_patterns'))
    CREATE INDEX IX_failure_patterns_application_year ON dbo.failure_patterns (application, knowledge_year, created_at DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_failure_patterns_system' AND object_id = OBJECT_ID(N'dbo.failure_patterns'))
    CREATE INDEX IX_failure_patterns_system ON dbo.failure_patterns (system);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_failure_patterns_component' AND object_id = OBJECT_ID(N'dbo.failure_patterns'))
    CREATE INDEX IX_failure_patterns_component ON dbo.failure_patterns (component);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_processed_source_events_processed_at' AND object_id = OBJECT_ID(N'dbo.processed_source_events'))
    CREATE INDEX IX_processed_source_events_processed_at ON dbo.processed_source_events (processed_at DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ingestion_status_last_updated' AND object_id = OBJECT_ID(N'dbo.ingestion_status'))
    CREATE INDEX IX_ingestion_status_last_updated ON dbo.ingestion_status (last_updated DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_feedback_logs_created_at' AND object_id = OBJECT_ID(N'dbo.feedback_logs'))
    CREATE INDEX IX_feedback_logs_created_at ON dbo.feedback_logs (created_at DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_feedback_logs_target_ticket_id' AND object_id = OBJECT_ID(N'dbo.feedback_logs'))
    CREATE INDEX IX_feedback_logs_target_ticket_id ON dbo.feedback_logs (target_ticket_id);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_interaction_logs_created_at' AND object_id = OBJECT_ID(N'dbo.interaction_logs'))
    CREATE INDEX IX_interaction_logs_created_at ON dbo.interaction_logs (created_at DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_application_run_logs_created_at' AND object_id = OBJECT_ID(N'dbo.application_run_logs'))
    CREATE INDEX IX_application_run_logs_created_at ON dbo.application_run_logs (created_at DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_application_run_logs_level_created_at' AND object_id = OBJECT_ID(N'dbo.application_run_logs'))
    CREATE INDEX IX_application_run_logs_level_created_at ON dbo.application_run_logs (level, created_at DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_llm_token_usage_created_at' AND object_id = OBJECT_ID(N'dbo.llm_token_usage'))
    CREATE INDEX IX_llm_token_usage_created_at ON dbo.llm_token_usage (created_at DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_llm_token_usage_service_operation' AND object_id = OBJECT_ID(N'dbo.llm_token_usage'))
    CREATE INDEX IX_llm_token_usage_service_operation ON dbo.llm_token_usage (service_type, operation_name, created_at DESC);
GO