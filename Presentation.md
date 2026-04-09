# PoolSense Presentation Prompts

Use the following prompts to generate a focused 10-slide presentation for the `PoolSense` proof of concept. These prompts are aligned to the actual implemented POC scope:

1. Historical closed tickets are polled directly from a SQL source.
2. New tickets trigger AI-based recommendations and email notifications.
3. A UI allows users to enter a problem statement and get possible resolutions from the same knowledge base.

Each prompt can be pasted into a presentation-capable AI model to generate one slide at a time.

## Slide 1 - Title and POC Objective

**Prompt**

Create a clean enterprise title slide for a presentation about `PoolSense`.

Include:
- Title: `PoolSense`
- Subtitle: `AI-Powered Incident Assistance Proof of Concept`
- Supporting line: `SQL Ticket Polling + AI Normalization + Similarity Search + Email Recommendations + Query UI`
- Presenter placeholder: `Prepared by: Ashish Upreti`

Also show a short objective statement:
- `Objective: Validate whether AI can transform historical ticket data into a reusable support knowledge base and assist both automated and user-driven incident resolution workflows.`

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
   - a user enters a problem statement in the UI
   - the system searches the same knowledge base
   - returns possible resolution and related historical incidents in the UI

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
- `SQL Ticket Source`
- `Background Ticket Polling Service`
- `ASP.NET Core API (.NET 9)`
- `AI Agents / Semantic Kernel orchestration`
- `Azure OpenAI chat + embeddings`
- `PostgreSQL + pgvector knowledge base`
- `Email recommendation service`
- `React UI (PoolSense.UI)`

Show these main flows:
- SQL source feeds the background polling service
- polling service sends tickets into the API workflow
- AI agents analyze and enrich tickets
- embeddings and normalized knowledge are stored in PostgreSQL
- similarity search reads from PostgreSQL
- new ticket recommendations are emailed
- UI users can query the same API and knowledge base

Make the diagram simple, clean, and executive-readable.

## Slide 5 - Flow 1: Closed Ticket Knowledge Creation

**Prompt**

Create a slide called `Flow 1 - Closed Ticket Knowledge Creation` for `PoolSense`.

Explain this implemented workflow step by step:
1. Background service polls closed tickets from the SQL source
2. Ticket data is normalized into a consistent format
3. AI analyzes the ticket and extracts structured issue understanding
4. Embeddings are generated from enriched ticket content
5. Similar historical context can be referenced
6. Structured knowledge is stored in PostgreSQL with pgvector
7. Failure patterns are extracted and stored for future insight reporting

Include a visual flow from:
`SQL Source -> Polling Service -> AI Analysis -> Embedding -> PostgreSQL Knowledge Base`

Emphasize:
- this creates the reusable historical knowledge layer for the system
- this is the foundation for all later recommendation scenarios

## Slide 6 - Flow 2: New Ticket Email Recommendation

**Prompt**

Create a slide called `Flow 2 - New Ticket Recommendation by Email` for `PoolSense`.

Explain the implemented workflow:
1. Background service polls new tickets from the SQL source
2. The API processes the incoming new ticket
3. The system generates an embedding for the ticket content
4. Similar incidents are retrieved from the knowledge base
5. AI produces a likely root cause and suggested resolution
6. Recommendation details are emailed to the lifeguard or configured recipient

Include what the email contains:
- suggested root cause
- suggested resolution
- confidence
- reasoning
- similar incidents and their resolutions

Add a value statement:
- `This flow demonstrates proactive support assistance using knowledge built from historical closed tickets.`

## Slide 7 - Flow 3: User Query UI Experience

**Prompt**

Create a slide called `Flow 3 - User Query Through UI` for `PoolSense`.

Explain the implemented user-driven workflow:
1. A user enters a problem statement in the React UI
2. The UI calls the ASP.NET Core API
3. The API analyzes the problem using AI agents
4. The system performs similarity search against the stored knowledge base
5. A likely root cause and possible resolution are returned
6. Similar incidents and reasoning are shown in the UI

Include the returned outputs:
- suggested root cause
- suggested resolution
- confidence score
- similar incidents
- failure pattern details
- reasoning

Emphasize:
- this uses the same historical knowledge generated in Flow 1
- it demonstrates human-in-the-loop support assistance

## Slide 8 - Current POC Features Implemented

**Prompt**

Create a slide listing the features currently implemented in the `PoolSense` POC.

Group them into four categories.

### `Historical Knowledge Flow`
- SQL ticket polling for closed tickets
- AI-based ticket analysis
- knowledge enrichment
- embedding generation
- PostgreSQL + pgvector storage
- failure pattern extraction and persistence

### `Recommendation Flow`
- polling for new tickets
- similarity search against historical knowledge
- AI-generated root cause and resolution recommendations
- email recommendation delivery

### `User Experience`
- React UI for incident query
- API endpoints for process, analyze, similar, and insights
- Swagger support for testing

### `Platform / Technical Foundation`
- .NET 9 ASP.NET Core API
- Semantic Kernel orchestration
- PostgreSQL bootstrap script and local development setup
- integrated frontend project in the .NET solution

Make the slide look like a mature and credible proof of concept.

## Slide 9 - Gaps, Risks, and Recommended Next Steps

**Prompt**

Create a slide called `Current Gaps and Recommended Next Steps` for `PoolSense`.

Include realistic POC gaps:
- production hardening is still needed
- database availability and environment setup need stabilization
- schema migrations and versioning are not yet production-grade
- AI resilience and output validation are still being improved
- observability, alerting, and monitoring need to be expanded
- security and secret management should be hardened
- broader pilot measurement and governance are still required

Then include a `Recommended Next Steps` section:
- stabilize infrastructure and connectivity
- productionize configuration and secrets
- define measurable pilot KPIs
- pilot with one or two operational teams
- expand dashboards, analytics, and evaluation loops

End with:
- `Recommendation: move from POC to a controlled pilot with measurable operational outcomes.`

## Slide 10 - Business Value and Closing

**Prompt**

Create a final closing slide for `PoolSense` focused on business value and leadership takeaway.

Include a short summary of value:
- faster triage support
- better reuse of organizational knowledge
- more consistent recommendation quality
- reduced dependence on individual expert memory
- foundation for future support automation and operational intelligence

Then include a conclusion statement:
- `PoolSense has demonstrated the 3 intended POC workflows: building a knowledge base from closed SQL tickets, recommending resolutions for new tickets via email, and supporting user queries through a UI backed by the same knowledge base.`

End with a clear leadership message:
- `Next step: approve a controlled pilot and production-readiness backlog.`

Style:
- polished
- executive
- simple and strong

## Optional Prompt - Generate the Full 10-Slide Deck

**Prompt**

Create a complete 10-slide internal presentation for `PoolSense`, an AI-powered incident assistance proof of concept.

The presentation must be aligned to these actual implemented POC workflows:
1. closed tickets are continuously polled from a SQL source and converted into a reusable knowledge base
2. new tickets are processed against that knowledge base and recommendation emails are sent
3. users can submit a problem statement through a React UI and receive possible resolutions from the same knowledge base

The system includes:
- SQL ticket polling
- ASP.NET Core API on .NET 9
- Semantic Kernel AI agents
- Azure OpenAI chat and embeddings
- PostgreSQL with pgvector
- similarity search
- failure pattern extraction
- email recommendation workflow
- React/Vite UI

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
