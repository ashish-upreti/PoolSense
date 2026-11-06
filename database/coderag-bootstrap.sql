IF OBJECT_ID(N'dbo.github_connections', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.github_connections (
        username nvarchar(128) NOT NULL PRIMARY KEY REFERENCES dbo.auth_users(username),
        github_id bigint NOT NULL,
        login nvarchar(256) NOT NULL,
        protected_token nvarchar(max) NOT NULL
    );
END;

IF OBJECT_ID(N'dbo.project_code_repositories', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.project_code_repositories (
        id nvarchar(32) NOT NULL PRIMARY KEY,
        project_id nvarchar(128) NOT NULL REFERENCES dbo.project_configs(project_id),
        github_id bigint NOT NULL,
        branch nvarchar(255) NOT NULL,
        revision int NOT NULL,
        state_json nvarchar(max) NOT NULL CHECK (ISJSON(state_json)=1),
        CONSTRAINT UQ_project_code_repository UNIQUE(project_id,github_id,branch)
    );
END;

IF OBJECT_ID(N'dbo.code_chat_sessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.code_chat_sessions (
        id nvarchar(32) NOT NULL PRIMARY KEY,
        username nvarchar(128) NOT NULL,
        project_id nvarchar(128) NOT NULL REFERENCES dbo.project_configs(project_id) ON DELETE CASCADE,
        title nvarchar(100) NOT NULL,
        created_at datetimeoffset(3) NOT NULL,
        updated_at datetimeoffset(3) NOT NULL
    );
    CREATE INDEX IX_code_chat_sessions_user ON dbo.code_chat_sessions(username, project_id, updated_at DESC);
END;

IF OBJECT_ID(N'dbo.code_chat_messages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.code_chat_messages (
        id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        session_id nvarchar(32) NOT NULL REFERENCES dbo.code_chat_sessions(id) ON DELETE CASCADE,
        role nvarchar(16) NOT NULL,
        content nvarchar(max) NOT NULL,
        citations_json nvarchar(max) NULL CHECK (citations_json IS NULL OR ISJSON(citations_json)=1),
        created_at datetimeoffset(3) NOT NULL
    );
    CREATE INDEX IX_code_chat_messages_session ON dbo.code_chat_messages(session_id, id);
END;