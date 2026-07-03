# PoolSense Presentation Prompts

Use the following prompts to generate a focused 1 of 12-slide presentation for `PoolSense`, a deployed AI-powered incident assistance platform. These prompts are aligned to the delivered v1 scope:

1. Historical closed tickets are polled directly from a SQL source and converted into a reusable, vector-searchable knowledge base.
2. New tickets trigger AI-based recommendations and email notifications (SMTP or SQL Server Database Mail).
3. A split-screen operator workspace lets users enter a problem statement, scope searches by project group, and review resolutions alongside an analytics insight panel with telemetry charts.

v1 knowledge source is pool ticket history. Future releases will add sources such as project wikis, SharePoint, and codebases.

Each prompt can be pasted into a presentation-capable AI model to generate one slide at a time.

Note: Attached document is for pool assist icon consistency. Create 1 slide at a time based on below slide context:

## Slide 1 - Title and v1 Overview

**Prompt**

Create a clean enterprise title slide for a presentation about `PoolSense`.

Include:
- Title: `PoolSense`
- Subtitle: `AI-Powered Incident Assistance — Initial Release (v1)`
- Supporting line: `SQL Ticket Polling + AI Normalization + Similarity Search + Email Recommendations + Insights Dashboard + Operator Workspace + User Activity Audit Trail`
- Presenter placeholder: `Prepared by: Ashish Upreti`

Also show a short objective statement:
- `PoolSense is deployed and operational. v1 transforms pool ticket history into a reusable AI-powered knowledge base that assists both automated and user-driven incident resolution workflows with multi-group scoping, analytical insights, and transparent reasoning.`

Style guidance:
- modern corporate slide
- blue, teal, and white palette
- polished and executive-friendly

## Slide 2 - v1 Scope — 3 Delivered Workflows

**Prompt**

Create a slide called `v1 Scope` for `PoolSense`.

Explain that the initial release delivers 3 core workflows:

1. `Closed ticket knowledge flow`
   - old closed tickets are continuously polled from a SQL source
   - ticket data is normalized with AI
   - embeddings are generated
   - normalized knowledge is stored for future similarity search

2. `New ticket recommendation flow`
   - when a new ticket is detected from the SQL source
   - the system searches the historical knowledge base
   - generates likely root cause and suggested resolution
   - emails the recommendation to the lifeguard or configured recipient

3. `User query UI flow`
   - a user enters a problem statement in the split-screen operator workspace
   - the user can optionally scope the search to specific project groups
   - the system searches the same knowledge base
   - returns possible resolution, related historical incidents, confidence scoring, failure pattern classification, and AI reasoning in the UI
   - an insight panel displays telemetry charts, similar incident details, and system context alongside the conversation

Make it clear that these three workflows are live and operational in v1.

## Slide 3 - Problem Statement and Why It Matters

**Prompt**

Create a problem statement slide for `PoolSense`.

Explain the operational problem:
- support and operations teams spend significant time understanding incident context
- past ticket resolutions are not reused effectively
- expert knowledge is trapped in historical tickets or individual experience
- repeated incidents are difficult to recognize consistently
- manual triage slows decision-making and resolution support

Include a section called `Why this matters` with business impact:
- slower support response
- inconsistent troubleshooting quality
- knowledge silos
- reduced scalability of support operations
- missed opportunities for automation

Style:
- professional, consulting-style, management-friendly

## Slide 4 - Architecture Overview

**Prompt**

Create a technical architecture overview slide for `PoolSense` aligned to the deployed v1 implementation.

Include these components:
- `SQL Ticket Source (SQL Server)`
- `Background Ticket Polling Service (multi-group aware)`
- `ASP.NET Core API (.NET 9)`
- `5 AI Agents / Semantic Kernel orchestration`
  - Ticket Analyzer Agent
  - Resolution Agent
  - Failure Pattern Agent
  - Query Variant Generator Agent
  - AI JSON Response Sanitizer
- `Azure OpenAI chat + embeddings (text-embedding-3-large)`
- `SQL Server PoolSense knowledge store`
- `Email recommendation service (SMTP or SQL Server Database Mail)`
- `Angular 18 Operator Workspace (PoolSense.UI)`
   - conversation workspace + quick prompts
   - insights panel with confidence, similar incidents, and failure pattern details
   - project group scoping
   - project configuration and PoolSense flag controls
  - Dark/Light theme toggle

Show these main flows:
- SQL source feeds the background polling service (scoped by project groups)
- before each polling iteration, `tbl_Application.PoolSense` is synchronized into `project_configs`
- polling service sends tickets into the API workflow
- 5 AI agents analyze, enrich, classify, and generate search variants for tickets
- embeddings and normalized knowledge are stored in SQL Server
- similarity search uses cached SQL Server knowledge rows and in-memory cosine scoring (scoped by application and group)
- UI project enable/disable changes write `PoolSense = 1/0` back to `tbl_Application`
- new ticket recommendations are emailed (via SMTP or Database Mail)
- UI users can query the same API and knowledge base through the operator workspace
- insights API serves aggregated failure trends, component analytics, and incident timelines
- idempotent processing prevents duplicate ticket ingestion

Make the diagram simple, clean, and executive-readable.

## Slide 5 - Flow 1: Closed Ticket Knowledge Creation

**Prompt**

Create a slide called `Flow 1 - Closed Ticket Knowledge Creation` for `PoolSense`.

Explain this implemented workflow step by step:
1. Background service polls closed tickets from the SQL source (respects `knowledgeLookbackYears` for filtering)
2. Ticket data is normalized into a consistent format with application and year scoping
3. Ticket Analyzer Agent extracts structured knowledge: problem, root cause, resolution, and keywords
4. Query Variant Generator Agent produces 5 alternative search phrases for enriched retrieval
5. Embeddings are generated from enriched content using Azure OpenAI text-embedding-3-large
6. Similar historical context is retrieved via cached SQL Server knowledge rows and cosine similarity
7. Failure Pattern Agent classifies each ticket into system, component, failure type, and resolution category
8. Structured knowledge and failure patterns are stored in SQL Server
9. Processed source events are tracked for idempotent ingestion (prevents duplicates)

Include a visual flow from:
`SQL Source -> Application Sync -> Polling Service -> Ticket Analyzer Agent -> Query Variant Generator -> Embedding -> SQL Server Knowledge Base + Failure Patterns`

Emphasize:
- this creates the reusable historical knowledge layer for the system
- this is the foundation for all later recommendation and insight scenarios
- multi-group awareness allows scoping ingestion by project group (application filter with LIKE pattern)
- `tbl_Application.PoolSense` controls whether an application is enabled for PoolSense processing

## Slide 6 - Flow 2: New Ticket Email Recommendation

**Prompt**

Create a slide called `Flow 2 - New Ticket Recommendation by Email` for `PoolSense`.

Explain the implemented workflow:
1. Background service polls new tickets from the SQL source (configurable interval, default 60s)
2. The API processes the incoming new ticket through the full orchestration pipeline
3. Ticket Analyzer Agent extracts structured knowledge from the new ticket
4. An embedding is generated and similar incidents are retrieved via cached SQL Server knowledge rows and cosine similarity
5. Resolution Agent generates a targeted root cause and resolution by selecting the best-matching historical incident
6. Failure Pattern Agent classifies the incident
7. Recommendation details are emailed to the lifeguard or configured recipient
8. Email delivery supports two modes: direct SMTP or SQL Server Database Mail relay
9. The processed event is tracked with email status to prevent duplicate notifications

Include what the email contains:
- suggested root cause
- suggested resolution
- confidence score (0.0 to 1.0)
- reasoning (which historical incident informed the suggestion)
- similar incidents with their ticket IDs and resolutions
- failure pattern classification (system, component, failure type)

Add a value statement:
- `This flow demonstrates proactive support assistance using knowledge built from historical closed tickets, with delivery mode flexibility for different infrastructure environments.`

## Slide 7 - Flow 3: User Query UI Experience

**Prompt**

Create a slide called `Flow 3 - User Query Through UI` for `PoolSense`.

Explain the implemented user-driven workflow:
1. A user opens the split-screen operator workspace in the Angular UI
2. The user can select quick prompt chips (e.g. "VG item missing", "Data load job failed") or type a custom problem statement
3. The user can optionally scope the search to specific project groups via the GroupSelector
4. The UI calls the ASP.NET Core API with the problem and `selectedGroupIds`
5. The API runs 5 AI agents: Ticket Analyzer, Query Variant Generator, Embedding, Resolution Agent, and Failure Pattern Agent
6. The system performs similarity search against the stored knowledge base scoped by application and group
7. Results are displayed in the split-screen layout:
   - Left panel (ChatPanel): conversation thread with suggested root cause and resolution
   - Right panel (InsightPanel): confidence meter, failure pattern card, AI reasoning, similar incidents list with external ticket links, and a telemetry bar chart

Include the returned outputs:
- suggested root cause
- suggested resolution
- confidence score (displayed as a percentage meter)
- similar incidents (ranked by match %, with links to external ticket system)
- failure pattern details (system, component, failure type, resolution category)
- reasoning (transparency into which historical incident informed the suggestion)
- telemetry snapshot chart (confidence, average similarity, pattern fit in the Angular UI)

Emphasize:
- this uses the same historical knowledge generated in Flow 1
- it demonstrates human-in-the-loop support assistance
- group-based scoping lets users narrow searches to relevant application domains
- the split-screen design gives engineers conversation, evidence, and analytics on one screen
- dark/light theme toggle persists user preference via localStorage

## Slide 8 - Features Delivered in v1

**Prompt**

Create a slide listing the features delivered in `PoolSense` v1.

Group them into five categories.

### `Historical Knowledge Flow`
- SQL ticket polling for closed tickets (with lookback year filtering)
- Ticket Analyzer Agent for structured knowledge extraction (problem, root cause, resolution, keywords)
- Query Variant Generator Agent for enriched retrieval (5 alternative search phrases)
- embedding generation via Azure OpenAI text-embedding-3-large
- SQL Server storage with application and year scoping
- Failure Pattern Agent for classification (system, component, failure type, resolution category)
- idempotent processing to prevent duplicate ingestion
- application sync from `tbl_Application.PoolSense` into `project_configs`

### `Recommendation Flow`
- background polling for new tickets (configurable interval)
- similarity search against historical knowledge via cosine similarity
- Resolution Agent generates targeted recommendations from best-matching historical incident
- email recommendation delivery (SMTP or SQL Server Database Mail)
- processed event tracking with email status

### `Insights and Analytics`
- insights API with aggregated failure trends, top components, repeated systems, and monthly incident timeline
- telemetry snapshot chart (confidence, similarity, pattern fit) in the Angular UI
- confidence meter and failure pattern card in the UI
- reasoning transparency (shows which historical ticket informed the suggestion)

### `User Experience`
- split-screen operator workspace (65% ChatPanel / 35% InsightPanel)
- quick prompt chips for common issue types
- GroupSelector for project-group-scoped searches
- conversation thread with message bubbles for user and assistant
- similar incidents list with external ticket links (pool.intel.com)
- dark/light theme toggle with localStorage persistence
- responsive CSS Grid layout with accessibility (ARIA labels, semantic HTML, keyboard navigation)
- Swagger API documentation for testing

### `Platform / Technical Foundation`
- .NET 9 ASP.NET Core API
- 5 AI agents orchestrated via Microsoft Semantic Kernel
- Azure OpenAI (chat + text-embedding-3-large)
- SQL Server bootstrap script and local development setup
- integrated Angular 18 + TypeScript frontend as SDK-style .NET project
- multi-project and multi-group support (SQL LIKE pattern matching for application filters)
- PoolSense UI enable/disable writes back to `tbl_Application.PoolSense`
- configurable email delivery (SMTP or Database Mail)
- **PoolSense Email master kill switch** — single toggle in Master Settings to suppress all outbound emails across all applications
- **User activity audit trail** — all configuration changes, knowledge store writes, sign-in, and sign-out events recorded to `dbo.user_activity_logs` with user name, action, entity, details, and IP address
- user secrets support for local development

Make the slide reflect a mature, deployed v1 platform. v1 knowledge source is pool ticket history; additional sources are on the roadmap.

## Slide 9 - Current Limitations and v2 Roadmap

**Prompt**

Create a slide called `Current Limitations and v2 Roadmap` for `PoolSense`.

Include realistic v1 limitations:
- production hardening improvements still in progress (error handling, circuit breakers, retry policies)
- schema migrations and versioning are managed via bootstrap script; formal migration tooling is a v2 item
- AI resilience and output validation are being improved incrementally (JSON sanitizer handles edge cases)
- observability is partially in place (structured application run logs and user activity audit trail active; APM integration still needed)
- knowledge source is currently limited to pool ticket history — wikis, SharePoint, and codebases are planned for v2
- UI currently supports one concurrent conversation per session

Then include a `v2 Roadmap` section:
- expand knowledge sources: integrate project wikis and SharePoint via NYRA APIs
- add codebase as a knowledge source for deeper root cause context
- expand dashboards, analytics, and evaluation loops
- add APM integration and health check endpoints
- improve feedback loops to tune AI recommendation quality over time
- role-based access control for multi-team deployments

End with:
- `v1 is live. The roadmap is clear. These items are prioritized for the next release cycle.`

## Slide 10 - Business Value and Closing

**Prompt**

Create a final closing slide for `PoolSense` focused on business value and leadership takeaway.

Include a short summary of value:
- faster triage support with AI-generated root cause and resolution suggestions
- better reuse of organizational knowledge through vector-searchable historical tickets
- more consistent recommendation quality with confidence scoring and reasoning transparency
- reduced dependence on individual expert memory
- multi-group scoping enables team-specific knowledge domains
- proactive email recommendations for new tickets reduce manual monitoring
- insights dashboard surfaces failure trends, top components, and incident timelines
- foundation for future support automation and operational intelligence

Then include a conclusion statement:
- `PoolSense v1 is live and has delivered all 3 core workflows in production:`
  - `building a reusable knowledge base from pool ticket history using 5 specialized AI agents`
  - `proactively recommending resolutions for new tickets via email (SMTP or Database Mail)`
  - `supporting user queries through a split-screen operator workspace with group-scoped search, telemetry charts, and reasoning transparency`
- `Additional platform controls delivered: PoolSense Email master kill switch and a full user activity audit trail for compliance and traceability.`

End with a clear leadership message:
- `v1 is operational. See Slide 12 for what comes next.`

Style:
- polished
- executive
- simple and strong

## Slide 11 - Roadmap: Expanding the Knowledge Hub

**Prompt**

Create a slide called `Roadmap — Expanding the Knowledge Hub` for `PoolSense`.

Explain that v1 builds its knowledge base from pool ticket history. The next phase integrates additional knowledge sources to improve resolution quality and coverage.

Include these planned knowledge hub integrations:

### `Project Wiki / SharePoint Knowledge Base`
- ingest articles, runbooks, SOPs, and troubleshooting guides from project wikis and SharePoint sites via NYRA APIs
- enable the Resolution Agent to cite wiki articles as supporting evidence in its suggestions
- keep knowledge fresh with periodic re-crawl and delta-sync from SharePoint via NYRA APIs

### `Project Database as a Knowledge Source`
- connect directly to project-specific databases (schemas, stored procedures, job definitions, configuration tables)
- allow AI agents to reference actual system metadata when diagnosing data load failures, missing records, or configuration drift
- surface relevant table structures, recent job run history, or error logs as additional context during triage
- combine database context with ticket history for more precise root cause identification

### `Direct Production DBAS Integration`
- v1 sources ticket data from a SQL Server backup of the production DBAS system
- the next release will connect directly to the production DBAS system in real time
- this eliminates the lag between production events and knowledge base updates
- enables near-real-time ticket ingestion, status tracking, and incident correlation against the authoritative source

### `Combined Knowledge Graph`
- merge ticket knowledge, wiki content, and database context into a unified vector store
- enable cross-source similarity search: a user query can match a wiki article, a past ticket, and a database anomaly simultaneously
- weight and rank results across sources based on relevance and recency

Include a value statement:
- `v1 delivers pool ticket history as the knowledge foundation. By expanding to wikis, SharePoint, and live project databases, PoolSense evolves into a comprehensive operational knowledge platform.`

Style:
- forward-looking but grounded
- show a clear path from current POC to expanded capability
- keep it realistic and achievable

## Slide 12 - What's Next

**Prompt**

Create a closing slide called `What's Next` for `PoolSense`.

This slide presents the path forward now that v1 is live.

Include these sections:

### `Where We Are`
- PoolSense v1 is deployed and operational
- 3 core workflows are live: knowledge ingestion, email recommendations, and operator workspace
- v1 knowledge source is pool ticket history

### `v2 Scope`
- connect directly to the production DBAS system (replace SQL Server backup source)
- integrate SharePoint / project wiki as an additional knowledge source
- add codebase as a knowledge source for deeper root cause context
- expand insights dashboard and feedback loops
- APM integration and health check endpoints
- role-based access control for multi-team deployments

### `Proposed Timeline`
- Month 1: production DBAS integration and deployment pipeline improvements
- Month 2: SharePoint/wiki knowledge integration and expanded insights
- Month 3: codebase integration, feedback loop improvements, and broader team rollout

### `Expected Outcomes`
- higher recommendation accuracy with richer knowledge sources
- broader team adoption across operational domains
- measurable reduction in triage time with multi-source evidence
- clear evaluation data for ongoing investment decisions

End with a strong closing statement:
- `v1 is live. The foundation is proven. v2 expands the knowledge hub to make PoolSense indispensable across teams.`

Style:
- confident and forward-looking
- executive-friendly
- build on proven momentum

## Slide 12 (Alternative) - Expanding PoolSense

**Prompt**

Create a closing slide called `Expanding PoolSense` for a presentation.

Frame this as a collaborative, forward-looking conversation — building on what is already live.

Include these sections:

### `Where We Are Today`
- PoolSense v1 is deployed and operational with 3 live workflows
- pool ticket history is the current knowledge source
- the platform is functional and in active use

### `What the Next Phase Looks Like`
- expand the knowledge hub: add wikis, SharePoint, and codebase as sources
- connect to the production DBAS system directly for real-time ingestion
- broaden team access with role-based controls
- measurable outcomes defined upfront to evaluate ROI objectively

### `What We Would Need to Move Forward`
- alignment on priority for v2 knowledge source integrations
- resourcing conversation: part-time support from existing team members could accelerate progress
- agreement on which teams to onboard next
- a time-boxed commitment with a defined evaluation checkpoint

### `What Success Looks Like`
- engineers spend less time manually triaging repeat incidents
- recommendations become more accurate as knowledge sources expand
- organizational knowledge is captured and reused rather than lost between tickets
- a clear, data-driven signal on next investment

End with an open, collaborative closing statement:
- `v1 has answered the hardest question: yes, this works. The ask is simply to keep building on what is already proven.`

Style:
- calm, confident, and collaborative
- leadership-friendly
- invite a conversation rather than demand a decision

## Optional Prompt - Generate the Full 12-Slide Deck

**Prompt**

Create a complete 12-slide internal presentation for `PoolSense`, a deployed AI-powered incident assistance platform (v1).

The presentation must be aligned to these delivered v1 workflows:
1. closed tickets are continuously polled from a SQL source and converted into a reusable knowledge base
2. new tickets are processed against that knowledge base and recommendation emails are sent
3. users can submit a problem statement through an Angular UI and receive possible resolutions from the same knowledge base

v1 knowledge source is pool ticket history. v2 will add wikis, SharePoint, and codebases.

The system includes:
- SQL ticket polling with multi-group awareness and lookback year filtering
- ASP.NET Core API on .NET 9
- 5 Semantic Kernel AI agents (Ticket Analyzer, Resolution, Failure Pattern, Query Variant Generator, JSON Sanitizer)
- Azure OpenAI chat and text-embedding-3-large
- SQL Server persistence with cached cosine similarity search over stored embeddings
- failure pattern extraction and aggregated insights API
- email recommendation workflow (SMTP or SQL Server Database Mail)
- Angular 18 / TypeScript split-screen operator workspace
- Angular/CSS-based telemetry dashboard
- application sync from `tbl_Application.PoolSense` into `project_configs`
- UI project enable/disable write-back to `tbl_Application.PoolSense`
- dark/light theme, GroupSelector, quick prompt chips
- idempotent ticket processing and event tracking

Audience:
- engineering managers
- technical leadership
- delivery stakeholders

Desired tone:
- business-aware
- technically credible
- polished
- suitable for a manager presentation

For each slide include:
- slide title
- key bullet points
- suggested visual layout
- speaker notes
