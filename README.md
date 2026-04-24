# PoolSense

PoolSense is an incident-assistance application built as a .NET 9 solution with an ASP.NET Core API, a React/Vite frontend, Semantic Kernel-based AI orchestration, and PostgreSQL persistence with vector search.

The primary workflow is:

1. A user submits a ticket or incident description.
2. The API analyzes the request with Azure OpenAI-backed agents.
3. Embeddings and historical knowledge are used to find similar incidents.
4. A workflow orchestrator returns a suggested root cause, resolution, confidence score, and related failure patterns.

## Solution Layout

```text
PoolSense/
├── PoolSense.sln
├── PoolSense.Api/             # ASP.NET Core API, orchestration, controllers, services
├── PoolSense.Application/     # Shared request models and application-facing contracts
├── PoolSense.Domain/          # Domain layer placeholder
├── PoolSense.Infrastructure/  # Infrastructure layer placeholder
└── PoolSense.UI/              # React + Vite frontend wrapped as a SDK-style .NET project
```

### Project Responsibilities

- `PoolSense.Api`
    Hosts the HTTP API, registers Semantic Kernel and services, exposes Swagger in development, and serves the built SPA when frontend assets are published.

- `PoolSense.UI`
    Contains the React application. It remains a Node/Vite frontend, but it is now represented in the solution as a first-class SDK-style .NET project. `dotnet build` on this project runs the frontend production build through MSBuild.

- `PoolSense.Application`
    Holds application models shared by the API, including ticket request payloads.

- `PoolSense.Domain` and `PoolSense.Infrastructure`
    Present as solution layers but currently contain minimal implementation.

## Core Capabilities

- Ticket analysis through AI agents.
- Ticket processing that combines analysis, similarity search, and failure-pattern reasoning.
- Knowledge storage backed by embeddings.
- Similar-incident lookup using PostgreSQL + pgvector.
- Automated background polling of a SQL Server ticket source for closed-ticket knowledge ingestion.
- Email recommendation delivery for newly detected tickets.
- Multi-project support scoped by application name and knowledge year.
- User feedback capture from the React UI with helpful / not-helpful actions and optional comments.
- Feedback-weighted retrieval ranking using helpfulness and outcome-usage signals.
- Interaction logging for query text, retrieved incident metadata, confidence, and processing time.
- Insight endpoints for failure trends, repeated systems, components, and incident timelines.
- A React operator workspace for asking PoolSense and reviewing returned evidence.

## Tech Stack

- .NET 9
- ASP.NET Core Web API
- Microsoft Semantic Kernel
- Azure OpenAI chat + embeddings
- PostgreSQL
- pgvector
- React 19
- Vite 8
- TypeScript 5
- Recharts

## Prerequisites

Install these before running locally:

- .NET 9 SDK
- Node.js 20+ and npm
- PostgreSQL
- pgvector enabled in the target database
- Access to an Azure OpenAI-compatible endpoint for chat and embeddings

## Configuration

### API Settings

The API reads configuration from `PoolSense.Api/appsettings.json` and `PoolSense.Api/appsettings.Development.json`.

Important sections:

- `AiSettings`
    - `BaseUrl`
    - `ApiKey`
    - `ApiVersion`
    - `Models.Chat`
    - `Models.Embeddings`

- `ConnectionStrings.Postgres`
- `ConnectionStrings.TicketSourceSqlServer` — SQL Server connection string to the ticket source database (read-only; used by the background polling service).

- `TicketAutomation`
    - `PollingEnabled` — switch that controls the background polling service.
    - `SendEmail` — whether to send email recommendations for new tickets.
    - `ApplicationName` — default application scope for knowledge storage and queries.
    - `KnowledgeLookbackYears` — rolling-year window used for ingestion filtering and knowledge base queries. Set to `0` for no limit (ingest all historical tickets).
    - `PollIntervalSeconds` — seconds between polling iterations (minimum enforced at 10).
    - `ClosedStatusName` — status value that marks a ticket as closed in the source system.
    - `NewStatusName` — status value that marks a ticket as new in the source system.
    - `SourceDatabaseName` — source database name queried via `tbl_Application`.
    - `ProjectGroups` — list of named application groups. Each entry has a `GroupId`, `DisplayName`, and `ApplicationFilter` (supports SQL LIKE wildcards, e.g. `%FSCO-FAB%`).
    - `Email.Recipient` — recipient address for recommendation emails.
    - `Email.FromAddress` — from address on outbound recommendation emails.
    - `Email.DeliveryMode` — `Smtp` (default) or `DatabaseMail`. Use `DatabaseMail` to relay via SQL Server Database Mail on the ticket-source server.
    - `Email.SmtpHost` — SMTP relay hostname (used when `DeliveryMode = Smtp`).
    - `Email.Port` — SMTP port (default `25`), used when `DeliveryMode = Smtp`.
    - `Email.TimeoutMs` — SMTP connection and send timeout in milliseconds (default `30000`).
    - `Email.DatabaseMailProfile` — Database Mail profile name configured in `msdb` (used when `DeliveryMode = DatabaseMail`).

Recommended approach for local development:

- Keep non-secret defaults in `appsettings.Development.json`.
- Store secrets such as API keys outside source control using environment variables or `dotnet user-secrets`.

The API project now has a `UserSecretsId`, so you can configure local secrets without changing tracked files.

Recommended local setup:

```powershell
dotnet user-secrets set --project .\PoolSense.Api "AiSettings:BaseUrl" "https://your-endpoint.openai.azure.com"
dotnet user-secrets set --project .\PoolSense.Api "AiSettings:ApiKey" "<your-api-key>"
dotnet user-secrets set --project .\PoolSense.Api "ConnectionStrings:Postgres" "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=PoolSense"
```

Example shape:

```json
{
    "AiSettings": {
        "BaseUrl": "https://your-endpoint.openai.azure.com",
        "ApiKey": "<set-via-user-secrets-or-env>",
        "ApiVersion": "2024-02-15-preview",
        "Models": {
            "Chat": "gpt-4",
            "Embeddings": "text-embedding-3-large"
        }
    },
    "ConnectionStrings": {
        "Postgres": "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=PoolSense",
        "TicketSourceSqlServer": "Server=your-sql-server,1433;Database=PoolProd;Trusted_Connection=True;TrustServerCertificate=True"
    },
    "TicketAutomation": {
        "Enabled": false,
        "SendEmail": true,
        "PollingEnabled": true,
        "ApplicationName": "AT MPS Capacity Response",
        "KnowledgeLookbackYears": 3,
        "PollIntervalSeconds": 60,
        "ClosedStatusName": "Closed",
        "NewStatusName": "New",
        "SourceDatabaseName": "PoolProd",
        "ProjectGroups": [
            {
                "GroupId": "atcr",
                "DisplayName": "ATCR",
                "ApplicationFilter": "AT MPS Capacity Response"
            }
        ],
        "Email": {
            "Recipient": "<recipient@your-domain.com>",
            "FromAddress": "<from@your-domain.com>",
            "DeliveryMode": "Smtp",
            "SmtpHost": "smtp.your-domain.com",
            "Port": 25,
            "TimeoutMs": 30000,
            "DatabaseMailProfile": ""
        }
    }
}
```

### Frontend Settings

`PoolSense.UI/.env.example` contains the frontend variables used by Vite:

- `VITE_API_BASE_URL`
    Optional explicit API base URL. Leave empty when proxying through Vite in development.

- `VITE_API_PROXY_TARGET`
    The API target used by the Vite dev server. Default local value is `http://localhost:5217`.

## Database Setup

The repository currently contains runtime SQL queries but no EF Core migrations. A checked-in PostgreSQL bootstrap script is now available at [database/postgres-bootstrap.sql](database/postgres-bootstrap.sql).

### 1. Enable `pgvector`

The bootstrap script handles `pgvector` and the required tables for you. If you want to apply the steps manually, the first command is:

```sql
CREATE EXTENSION IF NOT EXISTS vector;
```

### 2. Run the Bootstrap Script

From a shell with `psql` available:

```powershell
psql "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=PoolSense" -f .\database\postgres-bootstrap.sql
```

If you are using the checked-in Docker pgvector container instead of a local `psql` install, you can run the script like this:

```powershell
Get-Content .\database\postgres-bootstrap.sql -Raw | docker exec -i pgvector-db psql -U postgres -d PoolSense
```

### 3. What the Script Creates

The API writes to six tables:

- `ticket_knowledge`
    Stores enriched incident text, extracted root cause and resolution, keywords, and the embedding used for similarity search. Scoped by `application` and `knowledge_year`.

- `failure_patterns`
    Stores structured failure classifications produced by the AI workflow. Scoped by `application` and `knowledge_year`.

- `processed_source_events`
    Tracks deduplicated polling state per source event. Prevents reprocessing the same closed ticket (`ClosedKnowledge`) or sending a repeated email for the same new ticket (`NewRecommendation`).

- `project_configs`
    Stores ticket-source registration metadata for `/api/projects`.

- `feedback_logs`
    Stores UI feedback submitted for AI responses, including the original query, suggested resolution, helpful / not-helpful rating, whether the suggestion was used, optional comment text, and retrieved ticket ids.

- `interaction_logs`
    Stores AI pipeline interaction metadata such as query text, embedding length, retrieved ticket ids and summarized content, suggested resolution, confidence, and processing time.

Key columns in `ticket_knowledge`:

```sql
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
```

Key columns in `failure_patterns`:

```sql
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
```

`processed_source_events` schema:

```sql
CREATE TABLE IF NOT EXISTS processed_source_events (
    source_event_id text NOT NULL,
    processing_kind text NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT now(),
    email_sent boolean NOT NULL DEFAULT FALSE,
    email_recipient text NOT NULL DEFAULT '',
    workflow_result text NOT NULL DEFAULT '',
    PRIMARY KEY (source_event_id, processing_kind)
);
```

`feedback_logs` schema:

```sql
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
```

`interaction_logs` schema:

```sql
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
```

The bootstrap script also creates an HNSW cosine index for fast vector similarity search:

```sql
CREATE INDEX IF NOT EXISTS ticket_knowledge_embedding_cosine_idx
    ON ticket_knowledge
    USING hnsw (embedding vector_cosine_ops);

ANALYZE ticket_knowledge;
```

Refer to [database/postgres-bootstrap.sql](database/postgres-bootstrap.sql) for the complete and authoritative schema including all indexes.

### 4. Embedding Dimension Note

The checked-in configuration uses `text-embedding-3-large`. The `ticket_knowledge.embedding` column is defined as `vector(1536)`. If you switch to a model that produces a different number of dimensions, update the column definition to match before inserting records.

### 5. Connect To The PostgreSQL Terminal

If `psql` is installed on your machine:

```powershell
psql "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=PoolSense"
```

If you want to connect through the running Docker container:

```powershell
docker exec -it pgvector-db psql -U postgres -d PoolSense
```

Useful interactive `psql` commands:

```sql
\dt
\d ticket_knowledge
\d failure_patterns
\d processed_source_events
\q
```

### 6. Verify Stored Data With SELECT Queries

Use these queries to verify that the polling workflow is writing the expected data.

Check recent ticket knowledge rows:

```sql
SELECT id, ticket_id
FROM ticket_knowledge;
```

```sql
SELECT id,
             ticket_id,
             source_event_id,
             application,
             knowledge_year,
             source_status,
             problem,
             root_cause,
             resolution,
             created_at
FROM ticket_knowledge
ORDER BY created_at DESC
LIMIT 20;
```

Check recent failure-pattern rows:

```sql
SELECT id,
             ticket_id,
             source_event_id,
             application,
             knowledge_year,
             system,
             component,
             failure_type,
             resolution_category,
             created_at
FROM failure_patterns
ORDER BY created_at DESC
LIMIT 20;
```

Check processed polling state and email delivery:

```sql
SELECT source_event_id,
             processing_kind,
             processed_at,
             email_sent,
             email_recipient
FROM processed_source_events
ORDER BY processed_at DESC
LIMIT 50;
```

Check active registered projects:

```sql
SELECT project_id,
             project_name,
             ticket_source_type,
             is_active,
             knowledge_sources
FROM project_configs
ORDER BY project_name;
```

Check only the current application and current-year knowledge scope:

```sql
SELECT ticket_id,
             source_event_id,
             application,
             knowledge_year,
             source_status,
             created_at
FROM ticket_knowledge
WHERE application = 'AT MPS Capacity Response'
    AND knowledge_year = EXTRACT(YEAR FROM CURRENT_DATE)::int
ORDER BY created_at DESC;
```

## Ticket Automation and Background Polling

The API includes a hosted `BackgroundTicketPollingService` that continuously reads from the SQL Server ticket source. It drives three flows:

### Flow 1 — Closed Ticket Knowledge Ingestion

1. The polling service queries tickets with status `ClosedStatusName` (default: `Closed`) from the SQL source.
2. Each new closed ticket is processed through the AI workflow: analyzed, enriched, embedded, and stored in `ticket_knowledge` and `failure_patterns`.
3. The event is recorded in `processed_source_events` with `processing_kind = 'ClosedKnowledge'` to prevent reprocessing.

### Flow 2 — New Ticket Recommendation and Email

1. The polling service queries tickets with status `NewStatusName` (default: `New`).
2. For each unseen new ticket, the workflow searches the knowledge base for similar incidents and generates a root-cause and resolution suggestion.
3. If `SendEmail = true`, the recommendation is emailed to the configured `Email.Recipient`.
4. The event is recorded in `processed_source_events` with `processing_kind = 'NewRecommendation'`.

### Flow 3 — User Query UI

The React UI lets operators enter a problem statement and receive suggestions from the same knowledge base without triggering automation. See [PoolSense.UI/README.md](PoolSense.UI/README.md).

### Enabling Polling

Polling is controlled by `TicketAutomation:PollingEnabled`. To enable it for local testing:

```json
"TicketAutomation": {
    "PollingEnabled": true,
    "SendEmail": false
}
```

Set `SendEmail: false` during local testing to avoid sending real emails.

### ProjectGroups

`ProjectGroups` lets you define multiple named application scopes. Each group's `ApplicationFilter` is applied as a SQL `ILIKE` query when the filter contains `%`, or as an exact match otherwise.

```json
"ProjectGroups": [
    { "GroupId": "atcr",     "DisplayName": "ATCR",     "ApplicationFilter": "AT MPS Capacity Response" },
    { "GroupId": "fsco-fab", "DisplayName": "FSCO-FAB", "ApplicationFilter": "%FSCO-FAB%" },
    { "GroupId": "dxcr",     "DisplayName": "DxCR",     "ApplicationFilter": "Die Prep / Die Sort Capacity Response" }
]
```

## Local Development

### Quick Start: Run the API and UI

1. Open a terminal at the solution root.
2. Start the API:

```powershell
dotnet run --project .\PoolSense.Api\PoolSense.Api.csproj --launch-profile http
```

3. Open a second terminal.
4. Go to the UI project:

```powershell
cd .\PoolSense.UI
```

5. Install frontend dependencies if you have not already:

```powershell
npm install
```

6. Start the UI:

```powershell
npm run dev
```

7. Open the app in your browser:
   - UI: `http://localhost:5173`
   - API: `http://localhost:5217`
   - Swagger: `http://localhost:5217/swagger`

Notes:
- The UI proxies `/api` requests to `http://localhost:5217` during development.
- To stop both apps, press `Ctrl+C` in each terminal.

### Option 1: Run API and Vite Separately

This is the best setup for frontend development.

1. Restore .NET dependencies:

```powershell
dotnet restore .\PoolSense.sln
```

2. Install frontend dependencies:

```powershell
Set-Location .\PoolSense.UI
npm ci
```

3. Start the API from the solution root in a separate terminal:

```powershell
dotnet run --project .\PoolSense.Api
```

That command uses the HTTP launch profile at `http://localhost:5217`. If you want to run with the HTTPS launch profile instead, use:

```powershell
dotnet run --project .\PoolSense.Api --launch-profile https
```

4. Start the frontend dev server:

```powershell
Set-Location .\PoolSense.UI
npm run dev
```

Default local URLs:

- API: `http://localhost:5217`
- API HTTPS profile: `https://localhost:7028`
- Frontend: `http://localhost:5173`
- Swagger: `http://localhost:5217/swagger`

### Option 2: Build the Whole Solution

This validates the backend and frontend together:

```powershell
dotnet build .\PoolSense.sln
```

Because `PoolSense.UI` is an SDK-style project with MSBuild targets, this build will:

- ensure Node modules exist using `npm ci` when needed
- run the frontend production build via `npm run build`

### Option 3: Start with a Fresh Database

If you are bringing up the app on a new machine:

1. Create the PostgreSQL database.
2. Enable `pgvector`.
3. Run [database/postgres-bootstrap.sql](database/postgres-bootstrap.sql).
4. Set `ConnectionStrings:Postgres` to that database.
5. Start the API and use `POST /api/ticket/store` or the main workflow to seed incident knowledge.

## Frontend Hosting Behavior

`PoolSense.Api` includes `PublishSpaAssets`, which copies built files from `PoolSense.UI/dist` into the API publish output under `wwwroot`.

At runtime the API:

- serves static frontend assets when `wwwroot` exists
- uses `MapFallbackToFile("index.html")` so client-side routes resolve to the SPA

This means:

- during development, use Vite for hot reload
- during publish or integrated deployments, the API can serve the built frontend directly

## API Endpoints

### Ticket Endpoints

- `POST /api/ticket/analyze`
    Runs the ticket analyzer agent and returns raw structured analysis.

- `POST /api/ticket/process`
    Main workflow endpoint used by the UI. Returns a `TicketWorkflowResult` including suggested root cause, suggested resolution, confidence, similar incidents, failure pattern, and reasoning.

- `POST /api/ticket/store`
    Analyzes a ticket, enriches it, generates embeddings, and stores knowledge for future similarity search.

- `POST /api/ticket/similar`
    Searches for similar tickets using the configured similarity search service.

### Project Endpoints

- `POST /api/projects/register`
    Registers a project configuration including ticket source type, connection string, and knowledge sources.

- `GET /api/projects`
    Lists active projects.

### Insight Endpoints

- `GET /api/insights`
    Returns top failures, components, repeated systems, and a monthly incident timeline.

- `GET /api/insights/failures`
    Returns top failure types.

- `GET /api/insights/components`
    Returns most problematic components.

- `GET /api/insights/systems`
    Returns systems with repeated incidents.

- `GET /api/insights/timeline`
    Returns incident counts by month.

## Swagger Test Prompts

Use these sample requests in `http://localhost:5217/swagger` to test each API.

### `POST /api/ticket/analyze`

Request body:

```json
{
    "title": "VG item missing from queue",
    "description": "Upstream source error caused a VG item to not appear in the work queue. Tool is idle waiting for material."
}
```

### `POST /api/ticket/process`

Request body:

```json
{
    "ticketId": "Pool-10001",
    "title": "VG item missing from queue",
    "description": "Upstream source error caused a VG item to not appear in the work queue. Tool is idle waiting for material."
}
```

### `POST /api/ticket/store`

Request body:

```json
{
    "ticketId": "Pool-20003",
    "title": "VG item missing from queue",
    "description": "Upstream source error caused a VG item to not appear in the work queue. Tool is idle waiting for material.",
    "resolution": "Manually triggered upstream source sync. Item reappeared in queue after a queue refresh."
}
```

### `POST /api/ticket/similar`

Request body:

```json
{
    "title": "VG item not showing in work queue",
    "description": "Tool waiting for material. Upstream may have dropped an item."
}
```

### `POST /api/projects/register`

Request body:

```json
{
    "projectName": "AT MPS Capacity Response",
    "ticketSourceType": "SqlServer",
    "connectionString": "Server=your-sql-server,1433;Database=PoolProd;Trusted_Connection=True;TrustServerCertificate=True",
    "knowledgeSources": []
}
```

### `GET /api/projects`

No request body.

Test URL:

```text
http://localhost:5217/api/projects
```

### `GET /api/insights`

No request body.

Test URL:

```text
http://localhost:5217/api/insights?limit=10&minimumIncidentCount=2&monthCount=6
```

### `GET /api/insights/failures`

No request body.

Test URL:

```text
http://localhost:5217/api/insights/failures?limit=10
```

### `GET /api/insights/components`

No request body.

Test URL:

```text
http://localhost:5217/api/insights/components?limit=10
```

### `GET /api/insights/systems`

No request body.

Test URL:

```text
http://localhost:5217/api/insights/systems?limit=10&minimumIncidentCount=2
```

### `GET /api/insights/timeline`

No request body.

Test URL:

```text
http://localhost:5217/api/insights/timeline?monthCount=6
```

## Example Request

Main workflow request:

```http
POST /api/ticket/process
Content-Type: application/json

{
    "ticketId": "Pool-10001",
    "title": "VG item missing",
    "description": "Upstream source error"
}
```

Representative response shape:

```json
{
    "suggestedRootCause": "Upstream synchronization failure caused the VG item to not be propagated to the work queue.",
    "suggestedResolution": "Trigger a manual queue refresh or restart the upstream sync service. Verify that the item appears in the source system before refreshing.",
    "confidence": 0.87,
    "reasoning": "Three similar incidents from the past 2 years involved the same upstream source error pattern. All were resolved by triggering a manual sync.",
    "failurePattern": {
        "system": "VG Queue System",
        "component": "Upstream Source Connector",
        "failureType": "Item Missing",
        "resolutionCategory": "Manual Sync"
    },
    "similarIncidents": [
        {
            "ticketId": "Pool-18742",
            "problem": "VG item dropped during upstream handoff",
            "resolution": "Triggered manual upstream sync, item reappeared.",
            "similarity": 0.93
        }
    ]
}
```

- The API currently allows CORS from `http://localhost:5173`.
- Swagger is enabled only in development.
- The frontend currently posts to `POST /api/ticket/process`.
- `PoolSense.UI` is included in the solution as a .NET project, but it is still fundamentally a Node/Vite application.

## Common Commands

From the solution root:

```powershell
dotnet restore .\PoolSense.sln
dotnet build .\PoolSense.sln
dotnet run --project .\PoolSense.Api
dotnet run --project .\PoolSense.Api --launch-profile https
```

From `PoolSense.UI`:

```powershell
npm ci
npm run dev
npm run build
npm run lint
```

## Current Gaps

- `PoolSense.Domain` and `PoolSense.Infrastructure` are still placeholders.
- Database bootstrap now has a checked-in SQL script, but migrations and repeatable schema automation are not yet present in this repository.

## Recommended Next Improvements

- Add automated schema migrations and schema versioning for PostgreSQL changes.
- Use deployment-specific environment variables or a secret manager outside local development.
- Add an end-to-end local bootstrap script for API, UI, and database.
