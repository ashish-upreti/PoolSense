# PoolSense — Frequently Asked Questions

This document covers the most anticipated questions from senior management, technical leadership, and delivery stakeholders regarding PoolSense — an AI-powered incident assistance proof of concept.

---

## Table of Contents

1. [Project Overview](#1-project-overview)
2. [Business Value & Problem Statement](#2-business-value--problem-statement)
3. [How It Works — Functional Q&A](#3-how-it-works--functional-qa)
4. [Technical Architecture & Stack](#4-technical-architecture--stack)
5. [AI & LLM Integration](#5-ai--llm-integration)
6. [Data & Security](#6-data--security)
7. [Current POC Scope & Limitations](#7-current-poc-scope--limitations)
8. [Vision: Project Knowledge Source & Web Crawler Integration](#8-vision-project-knowledge-source--web-crawler-integration)
9. [Production Readiness — What It Takes](#9-production-readiness--what-it-takes)
10. [Resource & Team Estimation — POC to Product](#10-resource--team-estimation--poc-to-product)
11. [Risks & Mitigations](#11-risks--mitigations)
12. [Roadmap & Milestones](#12-roadmap--milestones)

---

## 1. Project Overview

### Q: What is PoolSense?

**A:** PoolSense is an AI-powered incident assistance system that automatically builds a knowledge base from historical closed tickets and uses it to recommend root causes and resolutions for 
new incidents. It combines Azure OpenAI language models, vector-based similarity search (pgvector), and automated email recommendations to accelerate support triage and reduce dependence on 
individual expert knowledge.

### Q: What problem does PoolSense solve?

**A:** Support and operations teams spend significant time understanding incident context, repeatedly solving the same class of problems, and manually searching through historical tickets. 
PoolSense automates this by:

- Converting closed ticket history into a searchable, AI-enriched knowledge base.
- Proactively recommending resolutions for new tickets via email before a human even starts investigating.
- Providing an interactive UI where any team member can describe a problem and receive AI-assisted resolution suggestions.

### Q: Who is the intended audience for PoolSense?

**A:** Operations engineers, support lifeguards, incident responders, and any team that handles recurring incident tickets. The system is designed to be application-agnostic and multi-tenant, meaning
multiple project teams can use it simultaneously with scoped knowledge bases.

### Q: Is this a finished product or a proof of concept?

**A:** PoolSense is currently a **proof of concept (POC)**. It has successfully demonstrated all three intended workflows end-to-end. It is not yet production-hardened and requires additional 
investment in infrastructure, security, observability, and governance before enterprise-wide rollout.

---

## 2. Business Value & Problem Statement

### Q: What business outcomes does PoolSense deliver?

**A:**

| Outcome | How |
|---------|-----|
| **Faster triage** | AI suggests root cause and resolution in seconds, not hours |
| **Knowledge reuse** | Historical ticket resolutions are automatically captured and searchable |
| **Consistency** | Every recommendation is based on the same enriched knowledge base, reducing variance across responders |
| **Proactive support** | New tickets trigger email recommendations before a human starts investigating |
| **Reduced knowledge silos** | Expert knowledge trapped in closed tickets becomes organizational knowledge |
| **Scalability** | Multiple teams can onboard with separate project groups and application scopes |
| **Pattern visibility** | Failure pattern analytics expose systemic issues (repeated components, systems, failure types) |

### Q: How does PoolSense improve time-to-resolution?

**A:** When a new ticket arrives, PoolSense:

1. Automatically matches it against all historical knowledge using semantic similarity.
2. Generates a suggested root cause and resolution using AI agents.
3. Delivers these via email (background flow) or UI (interactive flow) — often within seconds of ticket creation.

This eliminates the manual investigation phase for known or similar issues and provides a starting point for novel issues.

### Q: What is the expected ROI?

**A:** The POC does not yet have formal ROI metrics. Recommended KPIs for a pilot phase include:

- **Mean Time to Resolution (MTTR)** reduction for tickets where PoolSense provided a recommendation.
- **Recommendation acceptance rate** — percentage of AI suggestions adopted by responders.
- **Ticket deflection rate** — incidents resolved without escalation using PoolSense output.
- **Knowledge base coverage** — percentage of new tickets that have at least one similar historical match.
- **Time saved per ticket** — estimated hours saved in investigation per incident.

### Q: How does this compare to just searching old tickets manually?

**A:** Manual search relies on exact keyword matches and the searcher's memory of past incidents. PoolSense uses:

- **Semantic search** — understands meaning, not just keywords. "Solver failed to converge" will match "optimization engine timeout" if they share conceptual similarity.
- **AI-enriched knowledge** — each ticket is analyzed and normalized by AI before storage, extracting structured problem/root-cause/resolution fields.
- **Search query variants** — the system generates 5 alternative search phrasings for each ticket to improve retrieval coverage.
- **Confidence scoring** — results are ranked by similarity with a confidence score, so responders know how reliable the suggestion is.

---

## 3. How It Works — Functional Q&A

### Q: What are the three core workflows?

**A:**

**Flow 1 — Closed Ticket Knowledge Creation**
```
SQL Source → Background Polling → AI Analysis → Embedding Generation → PostgreSQL Knowledge Base
```
Historical closed tickets are continuously polled, analyzed by AI agents, enriched with embeddings, and stored as structured knowledge. This builds the foundation for all downstream recommendations.

**Flow 2 — New Ticket Email Recommendation**
```
SQL Source → New Ticket Detected → Similarity Search → AI Resolution → Email to Lifeguard
```
When a new ticket appears, the system searches the knowledge base, generates a recommended root cause and resolution, and emails it to the configured recipient — before any manual investigation begins.

**Flow 3 — User Query UI**
```
User enters problem → API processes query → Similarity Search → AI Resolution → UI displays results
```
Any team member can open the React UI, describe an issue, and receive AI-generated suggestions along with similar historical incidents, confidence scores, and failure pattern metadata.

### Q: What does the email recommendation look like?

**A:** The email includes:

- **Source Event ID and Application** — links back to the source ticket.
- **Suggested Root Cause** — AI-generated analysis of the likely cause.
- **Suggested Resolution** — Specific actionable steps to resolve.
- **Confidence Score** — Percentage indicating how confident the system is.
- **Reasoning** — Explanation of why the system arrived at this conclusion.
- **Similar Incidents** — Historical tickets that matched, with links to the Pool ticket viewer.

### Q: What does the UI show the user?

**A:** The UI is a single-page two-panel layout:

- **Left panel (Chat):** Conversational interface where users describe their problem and receive root cause + resolution suggestions.
- **Right panel (Insights):** Confidence gauge, telemetry chart, similar historical incidents with similarity scores, failure pattern details, and AI reasoning.
- **Group selector:** Allows scoping the query to specific project groups (e.g., ATCR, FSCO-FAB).

### Q: How does the system learn from new tickets?

**A:** When a closed ticket is processed:

1. AI agents extract a structured problem statement, root cause, and resolution.
2. A query variant generator creates 5 alternative search phrasings.
3. An embedding is generated from the combined enriched text.
4. The knowledge entry and failure pattern are persisted to PostgreSQL.

This means the knowledge base grows automatically as more tickets are closed — no manual curation required.

### Q: Can teams use this independently?

**A:** Yes. PoolSense supports **multi-tenant project groups** via the `ProjectGroups` configuration. Each group has:

- A `GroupId` and `DisplayName` for UI selection.
- An `ApplicationFilter` that scopes knowledge and search to specific application names (supports exact match and SQL LIKE patterns).

Teams see only their own knowledge base while sharing the same platform infrastructure.

---

## 4. Technical Architecture & Stack

### Q: What is the technology stack?

**A:**

| Layer | Technology | Purpose |
|-------|-----------|---------|
| **Backend API** | ASP.NET Core (.NET 9) | REST API, orchestration, hosted services |
| **AI Orchestration** | Microsoft Semantic Kernel 1.73 | Agent coordination, prompt management |
| **LLM** | Azure OpenAI (GPT chat + text-embedding-3-large) | Ticket analysis, resolution generation, embeddings |
| **Knowledge Store** | PostgreSQL + pgvector | Vector similarity search, structured knowledge storage |
| **Ticket Source** | SQL Server (read-only polling) | Source of historical and new ticket data |
| **Frontend** | React 19 + Vite 8 + TypeScript 5 | Interactive operator workspace |
| **Visualization** | Recharts | Telemetry and confidence charts |
| **Email** | System.Net.Mail / SMTP | Automated recommendation delivery |

### Q: What does the architecture look like?

**A:**

```
┌──────────────────┐       ┌─────────────────────────────────────────┐
│  SQL Server      │       │  PoolSense.Api (.NET 9)                │
│  (Ticket Source) │──────▶│                                         │
└──────────────────┘       │  ┌─────────────────────────────┐       │
                           │  │ Background Polling Service   │       │
                           │  └──────────┬──────────────────┘       │
                           │             │                           │
                           │  ┌──────────▼──────────────────┐       │
┌──────────────────┐       │  │ Ticket Workflow Orchestrator │       │
│  React UI        │──────▶│  └──────────┬──────────────────┘       │
│  (PoolSense.UI) │       │             │                           │
└──────────────────┘       │  ┌──────────▼──────────────────┐       │
                           │  │ AI Agents (Semantic Kernel)  │       │
                           │  │  • TicketAnalyzerAgent       │       │
                           │  │  • ResolutionAgent           │       │
                           │  │  • FailurePatternAgent       │       │
                           │  │  • QueryVariantGenerator     │       │
                           │  └──────────┬──────────────────┘       │
                           │             │                           │
                           │  ┌──────────▼──────────┐               │
                           │  │ Azure OpenAI         │               │
                           │  │ (Chat + Embeddings)  │               │
                           │  └─────────────────────┘               │
                           │             │                           │
                           │  ┌──────────▼──────────┐               │
                           │  │ PostgreSQL + pgvector│               │
                           │  │ (Knowledge Base)     │               │
                           │  └─────────────────────┘               │
                           │             │                           │
                           │  ┌──────────▼──────────┐               │
                           │  │ Email Service (SMTP) │               │
                           │  └─────────────────────┘               │
                           └─────────────────────────────────────────┘
```

### Q: What databases are involved?

**A:**

| Database | Role | Access |
|----------|------|--------|
| **PostgreSQL** (PoolSense) | Knowledge base, failure patterns, deduplication tracking, project configs, feedback logs, and interaction logs | Read/Write |
| **SQL Server** (PoolProd) | Source ticket data (tbl_EventLog, tbl_Application, tbl_EventStatus, tbl_EventLifeguard) | Read-Only |

### Q: What are the REST API endpoints?

**A:**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/ticket/process` | POST | Full workflow: analyze → resolve → persist or recommend |
| `/api/ticket/analyze` | POST | Analyze a ticket without storing |
| `/api/ticket/store` | POST | Analyze, enrich, embed, and store ticket knowledge |
| `/api/ticket/similar` | POST | Find similar tickets via vector search |
| `/api/feedback` | POST | Capture user feedback for an AI response |
| `/api/insights` | GET | Combined failure insights dashboard data |
| `/api/insights/failures` | GET | Top failure types |
| `/api/insights/components` | GET | Most problematic components |
| `/api/insights/systems` | GET | Systems with repeated incidents |
| `/api/insights/timeline` | GET | Monthly incident trend |
| `/api/projects/register` | POST | Register a project configuration |
| `/api/projects` | GET | List active projects |
| `/api/projects/groups` | GET | Available project groups |

### Q: How does the solution handle deduplication?

**A:** The `processed_source_events` table tracks every processed ticket with a composite key of `(source_event_id, processing_kind)`. Processing kinds are `ClosedKnowledge` (knowledge persistence) and `NewRecommendation` (email suggestion). This prevents duplicate processing and duplicate emails.

---

## 5. AI & LLM Integration

### Q: What AI models does PoolSense use?

**A:**

| Model | Purpose | Dimensions |
|-------|---------|------------|
| **GPT chat model** (configurable, currently gpt-5.4 in dev) | Ticket analysis, resolution generation, failure pattern extraction, query variant generation | N/A |
| **text-embedding-3-large** | Converts ticket text into 1536-dimensional vectors for similarity search | 1536 |

### Q: How does the AI analyze a ticket?

**A:** Four specialized AI agents work in sequence:

1. **TicketAnalyzerAgent** — Extracts structured problem, root cause, resolution, and keywords from raw ticket text.
2. **QueryVariantGeneratorAgent** — Generates 5 alternative search phrasings to improve retrieval coverage.
3. **ResolutionAgent** — Given similar historical incidents as context, produces a targeted root cause, resolution, confidence score, and reasoning.
4. **FailurePatternAgent** — Extracts system, component, failure type, and resolution category for aggregation and trend analysis.

### Q: How does similarity search work?

**A:**

1. The incoming problem description is converted into a 1536-dimensional embedding vector.
2. PostgreSQL pgvector performs a cosine similarity search against all stored knowledge embeddings.
3. Results are ranked by similarity (0–1 scale) and the top 5 are returned.
4. Scoping filters (application name, knowledge year, project groups) narrow the search to relevant knowledge.

The HNSW index on the embedding column enables fast approximate nearest-neighbor search even as the knowledge base scales.

### Q: What happens if the AI returns invalid or garbled output?

**A:** Multiple safeguards exist:

- **AiJsonResponseSanitizer** — Strips markdown fences, control characters, and invalid JSON from LLM responses before deserialization.
- **SemanticKernelRetryHelper** — Retries up to 3 times on transient errors (e.g., deployment not found).
- **Graceful fallbacks** — If parsing fails, the system returns a structured error rather than crashing.

### Q: Can the AI model be swapped?

**A:** Yes. The model names are configuration-driven (`AiSettings:Models:Chat` and `AiSettings:Models:Embeddings`). Any Azure OpenAI-compatible endpoint can be used. Switching embedding models requires updating the PostgreSQL vector column dimension and re-embedding existing knowledge.

---

## 6. Data & Security

### Q: What data does PoolSense store?

**A:**

| Table | Data Stored |
|-------|-------------|
| `ticket_knowledge` | Enriched ticket text (problem, root cause, resolution), keywords, embedding vector, metadata (application, year, submitter, lifeguard, timestamps) |
| `failure_patterns` | Structured failure classifications (system, component, failure type, resolution category) |
| `processed_source_events` | Deduplication records with processing timestamps and email status |
| `project_configs` | Project registration metadata (source type, connection info, knowledge sources) |
| `feedback_logs` | User feedback for AI responses, including helpful / not-helpful rating, whether the suggestion was used, optional comment, and retrieved ticket ids |
| `interaction_logs` | AI pipeline interaction metadata including query text, embedding length, retrieved ticket ids and summaries, suggested resolution, confidence, and processing time |

### Q: Does PoolSense modify the source ticket system?

**A:** No. PoolSense has **read-only access** to the SQL Server ticket source. It only reads from `tbl_EventLog`, `tbl_Application`, `tbl_EventStatus`, and `tbl_EventLifeguard`. All writes go to the separate PostgreSQL knowledge database.

### Q: How are secrets managed?

**A:** The POC uses .NET User Secrets (`dotnet user-secrets`) for local development. Checked-in configuration files contain only placeholders. For production, secrets should be managed via Azure Key Vault, environment variables, or an enterprise secret manager.

### Q: Is the data sent to external AI services?

**A:** Ticket text is sent to an Azure OpenAI endpoint for analysis and embedding generation. The endpoint is configurable and should be pointed to an organization-approved, data-compliant Azure OpenAI instance. No data is sent to public OpenAI APIs.

### Q: What about PII or sensitive ticket content?

**A:** The current POC does not implement PII scrubbing or content filtering. For production, a data sanitization layer should be added before AI processing to mask or remove sensitive information (employee names, system credentials, etc.).

---

## 7. Current POC Scope & Limitations

### Q: What has been implemented in the POC?

**A:**

| Category | Features |
|----------|----------|
| **Knowledge Flow** | SQL ticket polling, AI analysis, knowledge enrichment, embedding generation, pgvector storage, failure pattern extraction |
| **Recommendation Flow** | New ticket detection, similarity search, AI-generated resolution, email delivery via SMTP, feedback-weighted ranking |
| **User Experience** | React UI for incident queries, confidence scoring, similar incident display, failure pattern visualization, project group filtering, thumbs up/down feedback, and optional comments |
| **Platform** | .NET 9 API, Semantic Kernel orchestration, PostgreSQL bootstrap, integrated frontend build, interaction logging, and feedback persistence |

### Q: What are the known limitations?

**A:**

| Gap | Description |
|-----|-------------|
| **No production infrastructure** | Runs locally or in development environments only |
| **No database migrations** | Schema is managed via a bootstrap SQL script, not EF Core migrations |
| **Limited observability** | Interaction and feedback data are persisted, but there is still no centralized structured logging, distributed tracing, dashboards, or alerting |
| **No authentication/authorization** | API endpoints are unauthenticated |
| **No PII handling** | Sensitive content is passed through without scrubbing |
| **No closed-loop learning automation** | Users can now rate suggestions and mark whether they were used, but there is no automated retraining, prompt tuning pipeline, or feedback analytics dashboard yet |
| **No CI/CD pipeline** | No automated build, test, or deployment pipeline |
| **No automated testing** | No unit tests, integration tests, or end-to-end tests |
| **No rate limiting** | API has no throttling or abuse protection |
| **Limited error recovery** | Background service has basic retry but no dead-letter queue or escalation |
| **Single knowledge source** | Only ingests from SQL Server ticket data; no wiki, documents, or external knowledge integration yet |

---

## 8. Vision: Project Knowledge Source & Web Crawler Integration

### Q: What is the Project Knowledge Source vision?

**A:** Beyond ticket history, PoolSense should be able to tap into **project wikis, documentation, runbooks, and team knowledge repositories** as additional data sources. When the LLM cannot confidently resolve an issue from ticket history alone, it should autonomously crawl and retrieve relevant wiki content to augment its response.

### Q: How would the Web Crawler API work?

**A:** The proposed architecture introduces a **Knowledge Crawler Service** that integrates with PoolSense:

```
┌──────────────────────────────────────────────────────────┐
│  PoolSense Knowledge Sources                            │
│                                                          │
│  ┌─────────────┐   ┌──────────────────┐   ┌───────────┐│
│  │ SQL Ticket   │   │ Wiki Crawler API  │   │ Document  ││
│  │ Source       │   │ (New)             │   │ Uploads   ││
│  │ (Existing)   │   │                  │   │ (Future)  ││
│  └──────┬──────┘   └────────┬─────────┘   └─────┬─────┘│
│         │                   │                     │      │
│         ▼                   ▼                     ▼      │
│  ┌──────────────────────────────────────────────────────┐│
│  │  Unified Knowledge Ingestion Pipeline                ││
│  │  (Chunk → Analyze → Embed → Store)                   ││
│  └──────────────────────────────────────────────────────┘│
│                          │                               │
│                          ▼                               │
│  ┌──────────────────────────────────────────────────────┐│
│  │  PostgreSQL + pgvector (Knowledge Base)              ││
│  │  ticket_knowledge + wiki_knowledge + doc_knowledge   ││
│  └──────────────────────────────────────────────────────┘│
└──────────────────────────────────────────────────────────┘
```

### Q: What would the Web Crawler API do specifically?

**A:**

| Capability | Description |
|-----------|-------------|
| **Scheduled crawling** | Periodically crawl configured project wiki URLs (Confluence, SharePoint, GitHub Wikis, internal wikis) |
| **On-demand crawling** | When the LLM's confidence is below a threshold, autonomously trigger a targeted crawl for relevant wiki pages |
| **Content extraction** | Parse HTML/wiki markup into clean text, respecting page structure (headings, code blocks, tables) |
| **Chunking** | Split large wiki pages into semantically meaningful chunks suitable for embedding |
| **Embedding & storage** | Generate embeddings for each chunk and store alongside ticket knowledge in pgvector |
| **Source tracking** | Record source URL, crawl timestamp, and content hash for freshness detection |
| **Incremental updates** | Only re-process pages whose content hash has changed since last crawl |
| **Authentication** | Support OAuth, API tokens, and SSO for authenticated wiki access |

### Q: How would this integrate into the existing resolution flow?

**A:**

1. **Resolution Agent** receives similar tickets from the knowledge base (existing behavior).
2. If confidence < configurable threshold (e.g., 70%), the agent triggers the **Knowledge Crawler** for targeted wiki search.
3. The crawler searches project wikis using the ticket's problem statement and extracted keywords.
4. Retrieved wiki content is added to the agent's context window.
5. The Resolution Agent re-evaluates with the augmented context and produces an improved recommendation.
6. Wiki sources are cited in the response so the user can verify.

This creates an **autonomous knowledge retrieval loop** — the LLM decides when it needs more context and fetches it on demand.

### Q: What wiki platforms would be supported?

**A:** The crawler API should be designed as a pluggable adapter system:

| Platform | Integration Method |
|----------|--------------------|
| **Confluence** | REST API v2 (content search, page retrieval) |
| **SharePoint** | Microsoft Graph API (site pages, document libraries) |
| **GitHub Wikis** | GitHub API (wiki pages as markdown) |
| **Azure DevOps Wikis** | Azure DevOps REST API |
| **Internal wikis** | Custom HTTP scraping with configurable selectors |
| **Static documentation sites** | Sitemap-based crawling |

### Q: What data model changes are needed?

**A:** A new `wiki_knowledge` table (or extending the existing knowledge schema):

```sql
CREATE TABLE IF NOT EXISTS wiki_knowledge (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source_url text NOT NULL,
    source_platform text NOT NULL,        -- 'confluence', 'sharepoint', 'github', etc.
    page_title text NOT NULL,
    chunk_index integer NOT NULL DEFAULT 0,
    content text NOT NULL,
    embedding vector(1536) NOT NULL,
    application text NOT NULL DEFAULT '',
    project_id text NOT NULL DEFAULT '',
    content_hash text NOT NULL,           -- For incremental update detection
    last_crawled_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(source_url, chunk_index)
);

CREATE INDEX IF NOT EXISTS wiki_knowledge_embedding_cosine_idx
    ON wiki_knowledge USING hnsw (embedding vector_cosine_ops);
```

### Q: How would the project configuration change?

**A:** The existing `project_configs` table already has a `knowledge_sources text[]` column. This would be populated with wiki URLs and crawler configuration per project:

```json
{
    "projectId": "atcr",
    "projectName": "AT MPS Capacity Response",
    "ticketSourceType": "sql",
    "knowledgeSources": [
        "https://wiki.internal.com/spaces/ATCR",
        "https://confluence.internal.com/display/ATCR/Runbooks",
        "https://github.com/org/atcr-docs/wiki"
    ]
}
```

---

## 9. Production Readiness — What It Takes

### Q: What must change to make PoolSense production-grade?

**A:** The following workstreams are required:

#### Infrastructure & Deployment
| Item | Description | Priority |
|------|-------------|----------|
| **Containerization** | Dockerize API and UI with multi-stage builds | High |
| **Kubernetes / App Service** | Deploy to managed container orchestration | High |
| **CI/CD Pipeline** | Automated build, test, and deployment (Azure DevOps / GitHub Actions) | High |
| **Environment management** | Dev, staging, production environment separation | High |
| **Database migrations** | Replace bootstrap SQL with EF Core migrations or Flyway | High |
| **PostgreSQL hosting** | Managed PostgreSQL (Azure Database for PostgreSQL Flexible Server with pgvector) | High |
| **Health checks** | Liveness and readiness probes for orchestration | Medium |

#### Security
| Item | Description | Priority |
|------|-------------|----------|
| **Authentication** | Azure AD / OIDC integration for API and UI | Critical |
| **Authorization** | Role-based access control (admin, operator, viewer) | Critical |
| **Secret management** | Azure Key Vault for API keys, connection strings | Critical |
| **PII scrubbing** | Content sanitization before AI processing | High |
| **Network security** | VNet integration, private endpoints for databases and AI endpoints | High |
| **Rate limiting** | API throttling to prevent abuse | Medium |
| **Audit logging** | Track who queried what, when | Medium |

#### Observability
| Item | Description | Priority |
|------|-------------|----------|
| **Structured logging** | Serilog / Application Insights with correlation IDs | High |
| **Distributed tracing** | OpenTelemetry for end-to-end request tracing | High |
| **Metrics & dashboards** | Grafana / Azure Monitor dashboards for API latency, AI response times, knowledge base growth | High |
| **Alerting** | Alerts for failed AI calls, polling errors, email failures | High |
| **AI cost tracking** | Monitor token consumption and Azure OpenAI spend | Medium |

#### Reliability
| Item | Description | Priority |
|------|-------------|----------|
| **Dead-letter queue** | Failed processing events routed to retry queue | High |
| **Circuit breakers** | Polly-based resilience for AI service calls | High |
| **Graceful degradation** | Return cached/partial results when AI is unavailable | Medium |
| **Backup & restore** | Automated database backups and recovery testing | High |
| **Horizontal scaling** | Multiple API instances behind load balancer | Medium |

#### Quality
| Item | Description | Priority |
|------|-------------|----------|
| **Unit tests** | Agent logic, service layer, embedding pipeline | High |
| **Integration tests** | API endpoint, database, AI service integration | High |
| **End-to-end tests** | Full workflow validation (ingest → search → recommend) | Medium |
| **Load testing** | Validate performance under concurrent users | Medium |
| **AI output evaluation** | Automated quality scoring of generated resolutions | Medium |

#### User Experience
| Item | Description | Priority |
|------|-------------|----------|
| **Feedback analytics and governance** | Build dashboards, review workflows, and policy around collected thumbs up/down, usage, and comment data | High |
| **Conversation history** | Persist and search past interactions | Medium |
| **Mobile responsiveness** | Ensure UI works on tablets and mobile | Low |
| **Accessibility** | WCAG 2.1 AA compliance | Medium |

### Q: What about the feedback loop?

**A:** A basic feedback loop is already implemented in the POC:

- Users can submit `Helpful` or `Not Helpful` feedback directly from the React UI.
- Users can add an optional comment.
- Users can indicate whether the suggested resolution was actually used.
- Feedback is persisted in `feedback_logs`.
- Retrieval ranking uses this signal to boost or penalize future matches.

This enables:

- **Reinforcement** — Highly-rated resolutions are prioritized in future similarity matches.
- **Correction** — Incorrect suggestions can be flagged and excluded from training.
- **Quality metrics** — Track recommendation accuracy over time.
- **Fine-tuning data** — Curated feedback can inform prompt improvements or model fine-tuning.

What is still missing for production is feedback governance: dashboards, reviewer workflows, abuse protection, and automated evaluation or retraining pipelines.

---

## 10. Resource & Team Estimation — POC to Product

### Q: What team is needed to take PoolSense to production?

**A:**

#### Phase 1: Controlled Pilot (3–4 months)

| Role | Count | Responsibilities |
|------|-------|-----------------|
| **Backend Engineer (.NET/C#)** | 1–2 | Production hardening, auth, migrations, resilience, testing |
| **AI/ML Engineer** | 1 | Prompt optimization, evaluation pipeline, embedding quality, crawler integration |
| **Frontend Engineer (React/TS)** | 1 | Feedback UI, conversation history, polish, accessibility |
| **DevOps / Platform Engineer** | 1 | CI/CD, containerization, Kubernetes/App Service, monitoring |
| **QA Engineer** | 0.5 | Test strategy, automated test suites |
| **Product Owner / Technical PM** | 0.5 | Backlog management, stakeholder alignment, KPI tracking |
| **Total** | ~5–6 people |

#### Phase 2: Multi-Team Rollout (4–6 months after pilot)

| Role | Count | Responsibilities |
|------|-------|-----------------|
| **Backend Engineers** | 2–3 | Web crawler service, multi-tenant scaling, advanced features |
| **AI/ML Engineer** | 1 | Knowledge quality, autonomous retrieval, model evaluation |
| **Frontend Engineer** | 1 | Admin dashboard, analytics, onboarding flows |
| **DevOps** | 1 | Scaling, performance tuning, cost optimization |
| **QA** | 1 | Regression, load, and AI quality testing |
| **Technical PM** | 1 | Cross-team coordination, roadmap |
| **Total** | ~7–9 people |

### Q: What infrastructure costs should we expect?

**A:**

| Component | Estimated Monthly Cost | Notes |
|-----------|----------------------|-------|
| **Azure OpenAI** | $500–$3,000 | Depends on ticket volume and token usage |
| **PostgreSQL (Managed)** | $200–$500 | Flexible Server with pgvector |
| **Kubernetes / App Service** | $300–$800 | API + UI hosting |
| **Container Registry** | $50–$100 | Image storage |
| **Azure Key Vault** | $10–$50 | Secret management |
| **Application Insights / Monitor** | $100–$300 | Observability |
| **SMTP / Email Service** | Minimal | Existing infrastructure |
| **Estimated total** | **$1,200–$5,000/month** | Scales with usage |

*Note: Azure OpenAI costs are the primary variable. Processing 1,000 tickets/month with embedding generation will cost significantly less than 50,000 tickets/month.*

### Q: What is the timeline to production?

**A:**

```
Month 1–2:   Security, auth, infra setup, CI/CD, database migrations
Month 2–3:   Testing, observability, feedback mechanism, pilot onboarding
Month 3–4:   Controlled pilot with 1–2 teams, measure KPIs
Month 4–6:   Web crawler integration, multi-team onboarding preparation
Month 6–8:   Broader rollout, admin tooling, analytics dashboards
Month 8–10:  Autonomous knowledge retrieval, advanced features, optimization
```

---

## 11. Risks & Mitigations

### Q: What are the key risks?

**A:**

| Risk | Impact | Mitigation |
|------|--------|------------|
| **AI hallucination** | Incorrect root cause or resolution suggested | Confidence scoring, source citation, human-in-the-loop review, feedback mechanism |
| **AI service availability** | Azure OpenAI downtime blocks all processing | Circuit breakers, graceful degradation, queue-based retry |
| **Knowledge base quality** | Garbage-in-garbage-out from low-quality source tickets | AI normalization during ingestion, quality scoring, manual curation tools |
| **Data privacy** | Sensitive content sent to AI services | PII scrubbing layer, data-compliant AI endpoint, audit logging |
| **Cost escalation** | Azure OpenAI costs scale with volume | Token budget monitoring, caching, batch processing, embedding reuse |
| **Adoption resistance** | Teams don't trust or use AI suggestions | Pilot with champions, demonstrate value with metrics, feedback loop |
| **Knowledge staleness** | Old resolutions become irrelevant | Knowledge year scoping (already implemented), freshness scoring, TTL policies |
| **Single point of failure** | PostgreSQL or AI service outage | High availability setup, read replicas, queue-based processing |
| **Scope creep** | Feature requests derail production readiness | Clear phase gates, prioritized backlog, PM governance |

### Q: How do we ensure AI quality?

**A:**

1. **Confidence thresholds** — Only surface recommendations above a minimum confidence (e.g., 60%).
2. **Source attribution** — Always show which historical tickets informed the recommendation.
3. **Human review** — Recommendations are suggestions, not automated actions.
4. **Feedback loop** — Users rate recommendations; low-rated responses are analyzed and prompts adjusted.
5. **Evaluation pipeline** — Automated scoring comparing AI output against known-good resolutions.
6. **Prompt versioning** — Track prompt changes and their impact on quality metrics.

---

## 12. Roadmap & Milestones

### Q: What does the full vision look like?

**A:**

```
┌─────────────────────────────────────────────────────────────────────┐
│                        PoolSense Vision Roadmap                    │
├──────────────┬──────────────────────────────────────────────────────┤
│ POC          │ ✅ SQL ticket polling                                │
│ (Completed)  │ ✅ AI analysis + enrichment                         │
│              │ ✅ pgvector similarity search                        │
│              │ ✅ Email recommendations                             │
│              │ ✅ React operator UI                                 │
│              │ ✅ Multi-tenant project groups                       │
│              │ ✅ Failure pattern insights                          │
│              │ ✅ User feedback mechanism                           │
│              │ ✅ Interaction logging                               │
├──────────────┼──────────────────────────────────────────────────────┤
│ Pilot        │ 🔲 Authentication & authorization                   │
│ (Phase 1)    │ 🔲 CI/CD pipeline                                   │
│              │ 🔲 Database migrations                               │
│              │ 🔲 Observability (logging, tracing, monitoring)      │
│              │ 🔲 Feedback analytics and governance                 │
│              │ 🔲 Automated tests                                   │
│              │ 🔲 PII scrubbing                                     │
│              │ 🔲 Containerized deployment                          │
├──────────────┼──────────────────────────────────────────────────────┤
│ Scale        │ 🔲 Web Crawler API for project wikis                │
│ (Phase 2)    │ 🔲 Autonomous knowledge retrieval                   │
│              │ 🔲 Multi-platform wiki support                      │
│              │    (Confluence, SharePoint, GitHub, ADO)             │
│              │ 🔲 Admin dashboard for knowledge management         │
│              │ 🔲 Advanced analytics & trend reporting             │
│              │ 🔲 Conversation history & search                    │
│              │ 🔲 Multi-team onboarding tooling                    │
├──────────────┼──────────────────────────────────────────────────────┤
│ Mature       │ 🔲 Self-improving prompts from feedback data        │
│ (Phase 3)    │ 🔲 Automated incident classification & routing      │
│              │ 🔲 Integration with ticketing systems (bidirectional)│
│              │ 🔲 Knowledge graph for cross-system dependencies    │
│              │ 🔲 SLA prediction and escalation triggers           │
│              │ 🔲 Custom model fine-tuning on organizational data  │
│              │ 🔲 Document upload ingestion (PDFs, runbooks)       │
└──────────────┴──────────────────────────────────────────────────────┘
```

### Q: What is the recommended next step?

**A:** **Approve a controlled pilot with measurable operational outcomes.**

Specifically:

1. **Select 1–2 champion teams** willing to pilot PoolSense alongside their existing workflow.
2. **Define success KPIs** — MTTR reduction target, recommendation acceptance rate, knowledge base coverage.
3. **Allocate a small team** (~5–6 engineers) for 3–4 months of production hardening and pilot execution.
4. **Measure and report** — Present pilot results to leadership with data-backed ROI before broader rollout.

PoolSense has demonstrated the three intended POC workflows: building a knowledge base from closed SQL tickets, recommending resolutions for new tickets via email, and supporting user queries through a UI backed by the same knowledge base. The next step is proving operational value with real teams and real metrics.

---

*Document prepared for PoolSense POC senior management review.*
*Last updated: April 2026*
