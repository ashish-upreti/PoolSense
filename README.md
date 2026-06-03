# PoolSense

PoolSense is a .NET 9 incident-assistance platform with an ASP.NET Core API, an Angular 18 operator console, Azure OpenAI-backed orchestration, and SQL Server persistence for knowledge, project configuration, ingestion tracking, feedback, telemetry, and operational logging.

At a high level, PoolSense does four things:

1. Accepts an incident or ticket description from a user or integrated client.
2. Retrieves similar historical incidents and failure-pattern context.
3. Uses AI orchestration to generate a suggested root cause, resolution, confidence score, and reasoning.
4. Supports ongoing operations through project configuration, feedback capture, ingestion tracking, and email recommendation delivery.

## Core Capabilities

- Incident triage through the `POST /api/ticket/process` workflow.
- Similar-incident retrieval using stored embeddings and cosine similarity search over SQL Server-backed knowledge rows.
- AI-generated root cause, resolution, reasoning, confidence, and failure-pattern enrichment.
- Background polling of a SQL Server ticket source for new tickets and closed-ticket knowledge ingestion.
- Project-based application scoping through `project_configs` and application filters.
- UI-based application administration for project rows, search scope, ingestion progress, and email settings.
- Dedicated application feedback capture with submitter name, email, and stored comments for the overall product.
- Feedback capture with helpful / not helpful rating, selected primary incident, optional comment, and usage signal.
- Operational insight endpoints for failures, systems, components, and timelines.
- Recommendation email delivery through SMTP or SQL Server Database Mail.
- External API support for toggling `send_email` and updating semicolon-separated Lifeguard email recipients by `application_filter`.
- SQL Server-backed interaction logs, application logs, and LLM token usage tracking.

## Solution Layout

```text
PoolSense/
├── PoolSense.sln
├── README.md
├── database/
│   ├── sqlserver-bootstrap.sql
│   ├── testingSQL.sql
│   ├── postgres-bootstrap.sql
│   └── testingPGSQL.sql
├── PoolSense.Api/
├── PoolSense.Application/
├── PoolSense.Domain/
├── PoolSense.Infrastructure/
└── PoolSense.UI/
```

### Project Responsibilities

- `PoolSense.Api`
  Hosts the HTTP API, background polling service, AI orchestration, SQL Server persistence, Swagger in development, and static SPA hosting when built frontend assets are published.

- `PoolSense.UI`
  Contains the Angular 18 operator console. It is wrapped in an SDK-style .NET project so it can participate in the solution and solution builds while still being developed with Angular CLI.

- `PoolSense.Application`
  Holds application-facing models and shared request/response contracts.

- `PoolSense.Domain` and `PoolSense.Infrastructure`
  Present as solution layers and available for future domain/infrastructure expansion.

- `database`
  Contains bootstrap and smoke-test SQL scripts. The active persistence path in the application is SQL Server, and [database/sqlserver-bootstrap.sql](database/sqlserver-bootstrap.sql) is the relevant schema bootstrap script.

## Tech Stack

- .NET 9
- ASP.NET Core Web API
- Angular 18
- TypeScript 5
- Microsoft Semantic Kernel
- Azure OpenAI chat + embeddings
- SQL Server
- SMTP or SQL Server Database Mail for recommendation delivery

## Prerequisites

Install these before running locally:

- .NET 9 SDK
- Node.js 20+ and npm
- Access to an Azure OpenAI-compatible endpoint for chat and embeddings
- Access to SQL Server instances for:
  - `PoolSenseSqlServer` — PoolSense persistence, logging, project configuration, and Database Mail delivery when enabled
  - `TicketSourceSqlServer` — source ticket database used by polling and ingestion

## Configuration

The API reads settings from [PoolSense.Api/appsettings.json](PoolSense.Api/appsettings.json), [PoolSense.Api/appsettings.Development.json](PoolSense.Api/appsettings.Development.json), user secrets, and environment variables.

Use [PoolSense.Api/appsettings.example.json](PoolSense.Api/appsettings.example.json) as the checked-in baseline template.

### Important Configuration Sections

- `AiSettings`
  - `BaseUrl`
  - `ApiKey`
  - `ApiVersion`
  - `ImageApiVersion`
  - `Models.Chat`
  - `Models.Embeddings`

- `ConnectionStrings`
  - `PoolSenseSqlServer` — PoolSense persistence database
  - `TicketSourceSqlServer` — source ticket database used by the polling connector

- `Cors`
  - `AllowedOrigins` — Angular or hosted UI origins allowed to send credentialed API requests

- `Auth`
  - `JwtSecret` — strong signing secret, recommended length 32+ characters
  - `AllowInsecurePasswordFallback` — permits plaintext password fallback for local HTTP development only
  - `SessionHours`
  - `RememberMeDays`

- `ActiveDirectory`
  - `Url`
  - `BaseDn`
  - `Domain`
  - `AllowedGroups`
  - `AdminGroupNames`

- `TicketAutomation`
  - `PollingEnabled`
  - `PollIntervalSeconds`
  - `ClosedStatusName`
  - `NewStatusName`
  - `SimilaritySearchLimit`
  - `Email.Recipient`
  - `Email.FromAddress`
  - `Email.DeliveryMode` (`Smtp` or `DatabaseMail`)
  - `Email.SmtpHost`
  - `Email.Port`
  - `Email.TimeoutMs`
  - `Email.DatabaseMailProfile`

Recommended local setup uses user secrets for sensitive values:

```powershell
dotnet user-secrets set --project .\PoolSense.Api "AiSettings:BaseUrl" "https://your-endpoint.openai.azure.com"
dotnet user-secrets set --project .\PoolSense.Api "AiSettings:ApiKey" "<your-api-key>"
dotnet user-secrets set --project .\PoolSense.Api "ConnectionStrings:PoolSenseSqlServer" "Server=your-sql-server,1433;Database=PoolSense;Trusted_Connection=True;TrustServerCertificate=True"
dotnet user-secrets set --project .\PoolSense.Api "ConnectionStrings:TicketSourceSqlServer" "Server=your-ticket-source-server,1433;Database=PoolProd;Trusted_Connection=True;TrustServerCertificate=True"
dotnet user-secrets set --project .\PoolSense.Api "Auth:JwtSecret" "<at-least-32-random-characters>"
```

Do not store real API keys, passwords, or production connection strings in tracked files.

## Database Setup

PoolSense currently persists operational data in SQL Server. The checked-in bootstrap script is [database/sqlserver-bootstrap.sql](database/sqlserver-bootstrap.sql).

Run it in SQL Server Management Studio, Azure Data Studio, or `sqlcmd` against the PoolSense persistence database:

```powershell
sqlcmd -S your-sql-server,1433 -d PoolSense -E -i .\database\sqlserver-bootstrap.sql
```

Useful smoke-test and verification scripts:

- [database/testingSQL.sql](database/testingSQL.sql) — SQL Server smoke-test and validation queries
- [database/sqlserver-bootstrap.sql](database/sqlserver-bootstrap.sql) — schema bootstrap and indexes

### Main SQL Server Tables

The bootstrap script creates and/or maintains these primary tables:

- `dbo.ticket_knowledge`
- `dbo.failure_patterns`
- `dbo.processed_source_events`
- `dbo.project_configs`
- `dbo.ingestion_status`
- `dbo.feedback_logs`
- `dbo.application_feedback_logs`
- `dbo.interaction_logs`
- `dbo.application_run_logs`
- `dbo.llm_token_usage`
- `dbo.auth_users`
- `dbo.auth_login_audit`

## Run Locally

### 1. Start the API

From the repository root:

```powershell
dotnet run --project .\PoolSense.Api\PoolSense.Api.csproj --launch-profile http
```

Development defaults:

- API base URL: `http://localhost:5217`
- Swagger: `http://localhost:5217/swagger`

### 2. Start the Angular UI

From the repository root:

```powershell
cd .\PoolSense.UI
npm install
npm start
```

This runs Angular with the local proxy from [PoolSense.UI/proxy.conf.json](PoolSense.UI/proxy.conf.json), forwarding `/api` to `http://localhost:5217`.

Open the UI at:

```text
http://localhost:4200
```

### 3. Integrated Builds

Useful build commands:

```powershell
dotnet build .\PoolSense.Api\PoolSense.Api.csproj
dotnet build .\PoolSense.UI\PoolSense.UI.csproj
dotnet build .\PoolSense.sln
```

`PoolSense.UI.csproj` runs `npm ci` automatically when `node_modules` is missing, then performs an Angular production build through MSBuild.

## How To Use The App

The operator experience has two primary work areas.

### Incident Workspace

1. Open `http://localhost:4200`.
2. Sign in with an Intel Active Directory account that belongs to one of the configured `ActiveDirectory:AllowedGroups`.
3. Stay on the `PoolSense` section in the left navigation rail.
4. Choose a search scope:
   - `All` to search all configured projects
   - one or more configured project groups such as `ATCR`, `FSCO-FAB`, `DxCR`, or `ONEMPS`
5. Type an incident description or click a quick prompt such as `VG item missing`.
6. Press Enter or click `Ask PoolSense`.
7. Review:
   - suggested root cause
   - suggested resolution
   - confidence
   - reasoning
   - similar incidents
   - failure-pattern details
   - telemetry summary
8. Submit feedback as `Helpful` or `Not Helpful`, optionally selecting the primary incident, adding a comment, and marking whether the resolution was used.

### Application Configuration Workspace

Use the `Application Configuration` section in the left navigation rail to:

- create application/project configuration rows
- edit existing rows
- set application filters
- set knowledge lookback years
- set similarity search limit
- toggle `Send Email`
- toggle `Pooling Enabled`
- set semicolon-separated email recipients
- review ingestion progress and refresh status

### Application Feedback Workspace

Use the `Application Feedback` section in the left navigation rail to:

- submit overall product feedback, issues, appreciation, or feature requests
- record submitter name and email for follow-up
- store the feedback in the PoolSense SQL Server database for tracking

Recipient format example:

```text
lifeguard1@intel.com; lifeguard2@intel.com
```

Application filter examples:

```text
AT MPS Capacity Response
%FSCO-FAB%
```

## API Surface

### Main Endpoints

| Endpoint | Purpose |
| --- | --- |
| `GET /api/auth/pubkey` | Return the RSA public key used by the UI to encrypt password submission. |
| `POST /api/auth/login` | Authenticate against Intel Active Directory, set the auth cookie, and record login metadata. |
| `POST /api/auth/logout` | Clear the auth cookie and invalidate the server-side session entry. |
| `GET /api/auth/session` | Validate the current cookie or bearer token and return the signed-in user. |
| `POST /api/auth/validate-ad` | Connectivity check for the configured Active Directory endpoint. |
| `POST /api/ticket/analyze` | Analyze a ticket/incident request. |
| `POST /api/ticket/process` | Main incident workflow endpoint used by the UI. |
| `POST /api/ticket/store` | Store ticket knowledge. |
| `POST /api/ticket/similar` | Retrieve similar historical tickets. |
| `POST /api/feedback` | Submit helpful / not-helpful response feedback. |
| `POST /api/feedback/application` | Submit overall product feedback with submitter details. |
| `GET /api/projects` | List configured projects/applications. |
| `POST /api/projects` | Create a project/application configuration. |
| `POST /api/projects/register` | Alias for project registration. |
| `GET /api/projects/{projectId}` | Fetch one project by id. |
| `PUT /api/projects/{projectId}` | Update one project by id. |
| `GET /api/projects/groups` | Return project groups for UI search scoping. |
| `PATCH /api/projects/email-settings/by-application` | Update `send_email` and `email_recipients` by exact `application_filter`. |
| `GET /api/ingestion/status` | Get ingestion status for all projects. |
| `GET /api/ingestion/status/{projectId}` | Get ingestion status for one project. |
| `GET /api/insights` | General insight summary. |
| `GET /api/insights/failures` | Failure insights. |
| `GET /api/insights/components` | Component insights. |
| `GET /api/insights/systems` | System insights. |
| `GET /api/insights/timeline` | Timeline insights. |

### Example Workflow Request

```json
{
  "title": "VG item missing",
  "description": "VG item missing",
  "selectedGroupIds": ["atcr", "fsco-fab"]
}
```

Pass `null`, an empty array, or omit `selectedGroupIds` to search across all configured project groups.

### Example External Email Settings Request

```json
{
  "applicationFilter": "AT MPS Capacity Response",
  "sendEmail": true,
  "emailRecipients": "lifeguard1@intel.com; lifeguard2@intel.com"
}
```

## Similarity Logic And Workflow Algorithm

PoolSense currently performs similarity retrieval in application code over a cached SQL Server knowledge set. The current path is not a SQL-side vector index lookup. Embeddings are stored in `dbo.ticket_knowledge` as JSON arrays, loaded into memory, filtered by project scope, and then scored with cosine similarity.

### 1. Search Text Selection

For `POST /api/ticket/process`, the orchestrator first runs the ticket analyzer agent to extract a structured `Problem`, `RootCause`, `Resolution`, and `Keywords`.

The similarity search text is chosen as:

- `analysis.Problem` when the analyzer returns a non-empty problem statement
- otherwise a fallback string built from the raw title and description

This means retrieval is usually driven by the analyzer's normalized problem summary rather than the raw incident body.

### 2. Query Embedding

The selected search text is embedded through the configured Azure OpenAI embedding model using a fixed dimension of `1536`.

Current embedding behavior:

- model comes from `AiSettings.Models.Embeddings`
- embedding generation uses Semantic Kernel retry handling
- token and latency usage are logged to `dbo.llm_token_usage`

### 3. Candidate Set Construction

All knowledge rows with non-empty embeddings are loaded from `dbo.ticket_knowledge` and cached in memory.

Before scoring, candidates are filtered by project scope:

- if `selectedGroupIds` is empty or omitted, all configured projects with a non-empty `application_filter` are considered
- if `selectedGroupIds` is provided, only those matching `project_configs.project_id` rows are considered

Each candidate ticket is kept only if it matches at least one scoped project:

- `KnowledgeLookbackYears` is enforced by year
- `ApplicationFilter` is matched against the knowledge row's `application`

Application filter matching rules:

- exact case-insensitive match when no wildcard is present
- SQL `LIKE`-style wildcards are supported in config:
  - `%` becomes `.*`
  - `_` becomes `.`

Those wildcard patterns are converted to a case-insensitive regular expression in memory.

### 4. Base Similarity Score

Each candidate is scored with cosine similarity:

```text
cosine(query, candidate) = dot(query, candidate) / (|query| * |candidate|)
```

If the embedding lengths do not match, or either vector magnitude is zero, the similarity score is treated as `0`.

### 5. Feedback-Aware Reranking

PoolSense does a two-stage ranking pass.

First pass:

- sort all candidates by raw cosine similarity descending
- take `max(limit, limit * 5)` candidates as the rerank pool

Second pass:

- add a feedback-derived weight to each candidate
- sort again by `(cosine similarity + feedback weight)`
- return the final top `limit`

Current feedback weights:

- helpful + used: `+0.10`
- helpful only: `+0.05`
- not helpful: `-0.05`

Feedback decay and caps:

- exponential time decay with a `45 day` half-life
- final feedback contribution clamped to `[-0.20, +0.20]`
- final rerank adjustment also clamped to `[-0.20, +0.20]`

Feedback is associated to either:

- the explicitly selected `target_ticket_id`
- or, for older feedback without a target ticket, each ticket id in `retrieved_ticket_ids`

### 6. Similarity Limit

The final similar-ticket count comes from:

- `request.SimilaritySearchLimitOverride` if it is greater than `0`
- otherwise `TicketAutomation.SimilaritySearchLimit`

Project configuration currently constrains `SimilaritySearchLimit` to `1..20`.

### 7. How Similarity Feeds Resolution

The final ranked similar incidents are passed to the resolution agent in order, highest similarity first.

The resolution agent is instructed to:

- compare the new ticket mainly against the historical `Problem` field
- choose the best 1-2 incidents
- avoid blindly copying generic stored root-cause summaries
- produce a specific root cause and adapted resolution
- emit a confidence score based on match quality

Important distinction:

- `similarIncidents[].similarity` is the retrieval score from cosine similarity plus feedback rerank
- `result.confidence` is generated by the resolution agent and reflects how strong the AI believes the final recommendation is

Current confidence guidance in the prompt is:

- `0.8+` for close matches
- `0.5-0.79` for partial matches
- below `0.5` for weak or no relevant historical match

### 8. Persistence Flow After Retrieval

For the normal `ProcessAsync` path, retrieval is only part of the pipeline. After similarity search and resolution generation, PoolSense also:

1. logs the interaction metadata
2. builds a `TicketKnowledge` record for the current ticket
3. enriches it with AI-generated query variants
4. creates a second embedding for storage using:
   - problem
   - root cause
   - resolution
   - generated search variants
5. extracts a structured failure pattern
6. persists both knowledge and failure-pattern rows when the workflow is in persist mode

This means the search embedding and the storage embedding are related but not identical:

- search embedding uses the incoming issue text or analyzed problem
- storage embedding uses enriched knowledge text built from the final structured fields and generated search variants

## Runtime Flow

1. The UI loads project groups and application configuration from the API.
2. A user submits an incident description.
3. The API orchestrates ticket analysis, retrieval, resolution generation, and failure-pattern reasoning.
4. Similar incidents and context are pulled from persisted knowledge in SQL Server.
5. The UI renders the response and accepts operator feedback.
6. Background polling continuously checks the ticket source for:
   - closed tickets to ingest as knowledge
   - new tickets to evaluate for recommendation email delivery
7. Project configuration and ingestion status drive application scoping and operational behavior.

## Key Files

| File | Purpose |
| --- | --- |
| [PoolSense.Api/Program.cs](PoolSense.Api/Program.cs) | Service registration, Swagger, CORS, and app pipeline. |
| [PoolSense.Api/Controllers](PoolSense.Api/Controllers) | HTTP endpoints for ticket workflow, projects, ingestion, feedback, and insights. |
| [PoolSense.Api/Services/BackgroundTicketPollingService.cs](PoolSense.Api/Services/BackgroundTicketPollingService.cs) | Scheduled polling, ingestion, and recommendation-email workflow. |
| [PoolSense.Api/Services/DatabaseMailEmailService.cs](PoolSense.Api/Services/DatabaseMailEmailService.cs) | SQL Server Database Mail delivery via `PoolSenseSqlServer`. |
| [PoolSense.Api/Data/ProjectRepository.cs](PoolSense.Api/Data/ProjectRepository.cs) | SQL Server persistence for `project_configs`. |
| [PoolSense.UI/src/app/app.component.ts](PoolSense.UI/src/app/app.component.ts) | Angular UI state and interaction orchestration. |
| [PoolSense.UI/src/app/app.component.html](PoolSense.UI/src/app/app.component.html) | Angular template for the operator console and project admin. |
| [PoolSense.UI/src/app/api.service.ts](PoolSense.UI/src/app/api.service.ts) | Frontend fetch wrapper for PoolSense APIs. |
| [database/sqlserver-bootstrap.sql](database/sqlserver-bootstrap.sql) | SQL Server schema bootstrap. |
| [database/testingSQL.sql](database/testingSQL.sql) | SQL Server smoke-test and verification queries. |

## Troubleshooting

- If `npm start` fails immediately, run `npm install` or `npm ci` in [PoolSense.UI](PoolSense.UI) and retry.
- If the UI cannot reach the API, confirm the backend is listening on `http://localhost:5217` or update [PoolSense.UI/proxy.conf.json](PoolSense.UI/proxy.conf.json).
- If the API returns `500`, check API logs and verify `AiSettings`, `PoolSenseSqlServer`, and `TicketSourceSqlServer` values.
- If project configuration does not load, confirm `dbo.project_configs` exists in the PoolSense database.
- If ingestion status is empty or incorrect, confirm `dbo.ingestion_status` exists and the polling service is enabled.
- If recommendation emails are not sent, verify project-level `Send Email`, semicolon-separated recipients, and the configured email delivery mode.
- If `DeliveryMode = DatabaseMail`, verify the SQL Server Database Mail profile exists on `PoolSenseSqlServer` and is usable by the configured login.
- If `dotnet build .\PoolSense.UI\PoolSense.UI.csproj` fails, confirm Node.js/npm are installed and frontend dependencies restore correctly.

## Notes

- The active local UI dev server uses Angular, not React/Vite.
- The active persistence path is SQL Server.
- The checked-in PostgreSQL scripts are no longer the primary runtime path for the current app configuration.