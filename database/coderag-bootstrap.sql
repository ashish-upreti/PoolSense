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