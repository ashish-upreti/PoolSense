# CodeRAG: This Quarter's Focus

For a beginner-friendly explanation of the implementation, see [How PoolSense CodeRAG Works](CodeRAG-Technical-Guide.md).

PoolSense is expanding beyond historical pool tickets and Wiki knowledge to help teams connect an incident to the code behind it. This quarter's focus is on two ways to use project code as evidence: in pool troubleshooting and in a dedicated DiggingIn experience.

## 1. CodeRAG for Pool Troubleshooting

**Start with a pool report.** Ask a follow-up question about a specific ticket, such as what code might explain the failure or where to validate a proposed fix. PoolSense can bring relevant repository evidence into the troubleshooting answer alongside similar past incidents and project Wiki material.

**Why it matters:** Teams can move from a suggested resolution to the implementation that may support or challenge it, without losing the ticket's context. Code is supporting evidence, not a substitute for confirming the root cause or testing a fix.

## 2. DiggingIn: Dedicated CodeRAG

**Start with a code question.** Explore an application's indexed repositories directly, without needing a pool ticket. Ask where a behavior is implemented, trace a service or method, and follow up in the same conversation. Answers point back to source files so engineers can inspect the evidence themselves.

**Why it matters:** DiggingIn gives developers and support engineers a focused place for deeper code investigation, while pool troubleshooting stays centered on the incident.

| | Pool troubleshooting | DiggingIn |
| --- | --- | --- |
| Starting point | A saved pool report and follow-up question | A question about an application's code |
| Evidence | Pool incidents, project Wiki, and relevant code | Indexed project repositories |
| Best for | Investigating a specific failure or proposed resolution | Understanding implementation and tracing behavior |

## What To Expect

- Code search is scoped to repositories configured for the application; access to private source requires the user's own GitHub authorization.
- Repository results are grounded in indexed source and linked to files and lines. When code evidence is unavailable or insufficient, teams should not treat an answer as verified.
- The two experiences have different starting points, but both use project-scoped code knowledge. A separate DiggingIn experience does **not** imply a second repository integration or a second index.

These are **quarterly feature targets**, not a claim that every team has them enabled today. Availability depends on application repository setup, indexing, user access, and rollout. See [GitHub Code Knowledge setup](CodeKnowledge.md) for configuration and operational details.