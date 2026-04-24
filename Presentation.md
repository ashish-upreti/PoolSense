# PoolSense Presentation Prompts

Use the following prompts to generate a focused 1 of 12-slide presentation for the `PoolSense` proof of concept. These prompts are aligned to the actual implemented POC scope:

1. Historical closed tickets are polled directly from a SQL source and converted into a reusable, vector-searchable knowledge base.
2. New tickets trigger AI-based recommendations and email notifications (SMTP or SQL Server Database Mail).
3. A split-screen operator workspace lets users enter a problem statement, scope searches by project group, and review resolutions alongside an analytics insight panel with telemetry charts.

Each prompt can be pasted into a presentation-capable AI model to generate one slide at a time.

Note: Attached document is for pool assist icon consistency. Create 1 slide at a time based on below slide context:

## Slide 1 - Title and POC Objective

**Prompt**

Create a clean enterprise title slide for a presentation about `PoolSense`.

Include:
- Title: `PoolSense`
- Subtitle: `AI-Powered Incident Assistance Proof of Concept`
- Supporting line: `SQL Ticket Polling + AI Normalization + Similarity Search + Email Recommendations + Insights Dashboard + Operator Workspace`
- Presenter placeholder: `Prepared by: Ashish Upreti`

Also show a short objective statement:
- `Objective: Validate whether AI can transform historical ticket data into a reusable support knowledge base and assist both automated and user-driven incident resolution workflows with multi-group scoping, analytical insights, and transparent reasoning.`

Style guidance:
- modern corporate slide
- blue, teal, and white palette
- polished and executive-friendly

## Slide 2 - POC Scope and Intended 3 Flows

**Prompt**

Create a slide called `POC Scope` for `PoolSense`.

Explain that the proof of concept was designed around 3 core flows:

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

Make it clear that these three flows are the primary POC value proposition.

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

Create a technical architecture overview slide for `PoolSense` aligned to the actual POC implementation.

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
- `PostgreSQL + pgvector knowledge base`
- `Email recommendation service (SMTP or SQL Server Database Mail)`
- `React 19 Operator Workspace (PoolSense.UI)`
  - ChatPanel (conversation + quick prompts)
  - InsightPanel (telemetry charts + similar incidents + failure pattern details)
  - GroupSelector (project group scoping)
  - Dark/Light theme toggle

Show these main flows:
- SQL source feeds the background polling service (scoped by project groups)
- polling service sends tickets into the API workflow
- 5 AI agents analyze, enrich, classify, and generate search variants for tickets
- embeddings and normalized knowledge are stored in PostgreSQL with pgvector
- similarity search reads from PostgreSQL (scoped by application and group)
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
6. Similar historical context is retrieved via pgvector cosine similarity
7. Failure Pattern Agent classifies each ticket into system, component, failure type, and resolution category
8. Structured knowledge and failure patterns are stored in PostgreSQL with pgvector
9. Processed source events are tracked for idempotent ingestion (prevents duplicates)

Include a visual flow from:
`SQL Source -> Polling Service -> Ticket Analyzer Agent -> Query Variant Generator -> Embedding -> pgvector Knowledge Base + Failure Patterns`

Emphasize:
- this creates the reusable historical knowledge layer for the system
- this is the foundation for all later recommendation and insight scenarios
- multi-group awareness allows scoping ingestion by project group (application filter with LIKE pattern)

## Slide 6 - Flow 2: New Ticket Email Recommendation

**Prompt**

Create a slide called `Flow 2 - New Ticket Recommendation by Email` for `PoolSense`.

Explain the implemented workflow:
1. Background service polls new tickets from the SQL source (configurable interval, default 60s)
2. The API processes the incoming new ticket through the full orchestration pipeline
3. Ticket Analyzer Agent extracts structured knowledge from the new ticket
4. An embedding is generated and similar incidents are retrieved via pgvector cosine similarity
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
1. A user opens the split-screen operator workspace in the React UI
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
- telemetry snapshot chart (confidence, average similarity, pattern fit via Recharts)

Emphasize:
- this uses the same historical knowledge generated in Flow 1
- it demonstrates human-in-the-loop support assistance
- group-based scoping lets users narrow searches to relevant application domains
- the split-screen design gives engineers conversation, evidence, and analytics on one screen
- dark/light theme toggle persists user preference via localStorage

## Slide 8 - Current POC Features Implemented

**Prompt**

Create a slide listing the features currently implemented in the `PoolSense` POC.

Group them into five categories.

### `Historical Knowledge Flow`
- SQL ticket polling for closed tickets (with lookback year filtering)
- Ticket Analyzer Agent for structured knowledge extraction (problem, root cause, resolution, keywords)
- Query Variant Generator Agent for enriched retrieval (5 alternative search phrases)
- embedding generation via Azure OpenAI text-embedding-3-large
- PostgreSQL + pgvector storage with application and year scoping
- Failure Pattern Agent for classification (system, component, failure type, resolution category)
- idempotent processing to prevent duplicate ingestion

### `Recommendation Flow`
- background polling for new tickets (configurable interval)
- similarity search against historical knowledge via cosine similarity
- Resolution Agent generates targeted recommendations from best-matching historical incident
- email recommendation delivery (SMTP or SQL Server Database Mail)
- processed event tracking with email status

### `Insights and Analytics`
- insights API with aggregated failure trends, top components, repeated systems, and monthly incident timeline
- telemetry snapshot chart (confidence, similarity, pattern fit) via Recharts
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
- PostgreSQL bootstrap script and local development setup
- integrated React 19 + TypeScript + Vite frontend as SDK-style .NET project
- multi-project and multi-group support (SQL LIKE pattern matching for application filters)
- configurable email delivery (SMTP or Database Mail)
- user secrets support for local development

Make the slide look like a mature and credible proof of concept.

## Slide 9 - Gaps, Risks, and Recommended Next Steps

**Prompt**

Create a slide called `Current Gaps and Recommended Next Steps` for `PoolSense`.

Include realistic POC gaps:
- production hardening is still needed (error handling, circuit breakers, retry policies)
- database availability and environment setup need stabilization
- schema migrations and versioning are not yet production-grade (bootstrap script only)
- AI resilience and output validation are still being improved (JSON sanitizer handles some edge cases)
- observability, alerting, and monitoring need to be expanded (no structured logging or APM yet)
- security and secret management should be hardened (user-secrets supported but not enforced)
- broader pilot measurement and governance are still required
- UI currently supports one concurrent conversation per session
- no user authentication or role-based access control yet

Then include a `Recommended Next Steps` section:
- stabilize infrastructure and connectivity
- productionize configuration and secrets
- add authentication and role-based access control
- define measurable pilot KPIs (triage time reduction, resolution accuracy, knowledge reuse rate)
- pilot with one or two operational teams using project group scoping
- expand dashboards, analytics, and evaluation loops
- add structured logging, health checks, and APM integration
- investigate feedback loops (thumbs up/down on suggestions) to improve AI quality over time

End with:
- `These gaps are expected at the POC stage and are addressed in the proposed pilot scope (Slide 12).`

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
- `PoolSense has successfully demonstrated all 3 intended POC workflows:`
  - `building a reusable knowledge base from closed SQL tickets using 5 specialized AI agents`
  - `proactively recommending resolutions for new tickets via email (SMTP or Database Mail)`
  - `supporting user queries through a split-screen operator workspace with group-scoped search, telemetry charts, and reasoning transparency`

End with a clear leadership message:
- `The POC has validated the core concept. See Slide 12 for the formal project ask.`

Style:
- polished
- executive
- simple and strong

## Slide 11 - Future Scope: Expanding the Knowledge Hub

**Prompt**

Create a slide called `Future Scope - Expanding the Knowledge Hub` for `PoolSense`.

Explain that the current POC builds its knowledge base exclusively from historical SQL ticket data. The next phase envisions integrating additional knowledge sources to dramatically improve resolution quality and coverage.

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
- the current POC sources ticket data from a SQL Server backup of the production DBAS system
- a future integration would connect directly to the production DBAS system in real time to collect live pool data
- this eliminates the lag between production events and knowledge base updates
- enables near-real-time ticket ingestion, status tracking, and incident correlation against the authoritative source

### `Combined Knowledge Graph`
- merge ticket knowledge, wiki content, and database context into a unified vector store
- enable cross-source similarity search: a user query can match a wiki article, a past ticket, and a database anomaly simultaneously
- weight and rank results across sources based on relevance and recency

Include a value statement:
- `By expanding beyond ticket history to include wikis, SharePoint, and live project databases, PoolSense evolves from incident assistance into a comprehensive operational knowledge platform.`

Style:
- forward-looking but grounded
- show a clear path from current POC to expanded capability
- keep it realistic and achievable

## Slide 12 - The Ask

**Prompt**

Create a closing ask slide called `The Ask` for `PoolSense`.

This slide is a direct request to leadership to convert the POC into a formally allocated project.

Include these sections:

### `What We Are Asking For`
- formal project allocation to move PoolSense from POC to a production-ready pilot
- dedicated team of 2–3 resources
- a 2–3 month timeline to deliver a scoped pilot release

### `Proposed Scope for Pilot Phase`
- production-harden the existing 3 core workflows (knowledge ingestion, email recommendations, operator workspace)
- connect directly to the production DBAS system (replace current SQL Server backup source)
- integrate SharePoint / project wiki as an additional knowledge source
- add authentication, role-based access, lifeguard support
- implement structured observability (logging, health checks, alerting)
- deploy to a shared environment accessible to pilot teams

### `Suggested Timeline`
- Month 1: infrastructure hardening, production DBAS integration, authentication, and deployment pipeline
- Month 2: SharePoint/wiki knowledge integration, expanded insights dashboard, feedback loops
- Month 3: pilot rollout with 1–2 operational teams, KPI measurement, evaluation, and iteration

### `Expected Outcomes`
- measurable reduction in triage time for participating teams
- validated AI recommendation accuracy through pilot feedback
- production-ready platform for broader organizational rollout
- clear business case data for full-scale investment decision

End with a strong closing statement:
- `We are requesting project allocation for a 2–3 month pilot with 2–3 dedicated resources to transition PoolSense from a validated POC into a production-ready operational tool.`

Style:
- confident and direct
- executive-friendly
- action-oriented — make it easy for leadership to say yes

## Slide 12 (Alternative) - What Would Be Needed to Take This Further

**Prompt**

Create a closing slide called `What Would Be Needed to Take This Further` for `PoolSense`.

Frame this as a collaborative, forward-looking conversation with leadership — not a formal demand. The tone should be exploratory and opportunity-focused.

Include these sections:

### `Where We Are Today`
- PoolSense has completed a working proof of concept with 3 validated workflows
- the system is functional but not yet production-hardened or team-deployed
- we have a clear picture of what the next phase would require

### `What the Next Phase Would Look Like`
- a small, focused team (2–3 engineers) working for 2–3 months
- scope limited to production-hardening, one additional knowledge source (SharePoint or DBAS), and a pilot with 1–2 teams
- measurable outcomes defined upfront so we can evaluate ROI objectively

### `What We Would Need to Move Forward`
- alignment on priority: is this the right time to invest in this direction?
- a conversation about resourcing: even part-time support from existing team members could accelerate progress
- agreement on a pilot team to validate against real workflows
- a low-risk, time-boxed commitment — 2–3 months with a defined evaluation checkpoint

### `What Success Looks Like`
- engineers spend less time manually triaging repeat incidents
- recommendations from PoolSense are reliable enough to be trusted in daily operations
- organizational knowledge is captured and reused rather than lost between tickets
- a clear signal on whether to scale or stop — no open-ended commitment

End with an open, collaborative closing statement:
- `We are not asking for a large commitment — just enough runway to find out if this is worth scaling. We believe the POC has already answered the hardest question: yes, this is technically feasible.`

Style:
- calm, confident, and collaborative
- leadership-friendly without pressure
- invite a conversation rather than demand a decision

## Optional Prompt - Generate the Full 12-Slide Deck

**Prompt**

Create a complete 12-slide internal presentation for `PoolSense`, an AI-powered incident assistance proof of concept.

The presentation must be aligned to these actual implemented POC workflows:
1. closed tickets are continuously polled from a SQL source and converted into a reusable knowledge base
2. new tickets are processed against that knowledge base and recommendation emails are sent
3. users can submit a problem statement through a React UI and receive possible resolutions from the same knowledge base

The system includes:
- SQL ticket polling with multi-group awareness and lookback year filtering
- ASP.NET Core API on .NET 9
- 5 Semantic Kernel AI agents (Ticket Analyzer, Resolution, Failure Pattern, Query Variant Generator, JSON Sanitizer)
- Azure OpenAI chat and text-embedding-3-large
- PostgreSQL with pgvector for cosine similarity search
- failure pattern extraction and aggregated insights API
- email recommendation workflow (SMTP or SQL Server Database Mail)
- React 19 / TypeScript / Vite split-screen operator workspace
- Recharts-based telemetry dashboard
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
