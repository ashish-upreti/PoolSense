# PoolSense.UI

PoolSense.UI is the Angular 18 frontend for PoolSense, wrapped in an SDK-style .NET project so it can live in the solution as a first-class project.

## What the UI Does

The current workspace is a two-panel operator surface:

- The left panel is the incident conversation workspace with a project group scope selector.
- The right panel shows structured operational context returned by the API.
- Quick prompts seed common incident descriptions for faster smoke testing.
- The search scope selector above the chat input lets operators narrow the similarity search to one or more configured project groups (e.g., ATCR, FSCO-FAB, DxCR). Leaving all groups unchecked searches across all available knowledge.
- The UI calls the main workflow endpoint and renders root cause, resolution, confidence, similar incidents, and failure-pattern metadata from the response.

The main interaction starts in [src/app/app.component.ts](src/app/app.component.ts), which fetches available project groups on mount, sends incident text and selected group IDs to the API, and stores the returned `TicketWorkflowResult` for both the chat transcript and the insights panel.

## Development

- `npm run dev` starts the Angular development server with the local API proxy.
- `dotnet build PoolSense.UI.csproj` runs the frontend production build through MSBuild.
- `npm run build` performs the Angular production build.
- `npm run lint` runs ESLint against the UI codebase.

## Runtime Flow

1. On load, the UI calls `GET /api/projects/groups` to populate the project group selector.
2. A user types an incident summary or clicks a suggested prompt.
3. The UI posts the message to `POST /api/ticket/process` with the selected group IDs.
4. The same text is used for both `title` and `description` in the request body.
5. The assistant response is rendered in the chat area.
6. The insights sidebar renders confidence, failure pattern details, related incidents, and a lightweight telemetry visualization.

Key UI modules:

- [src/app/app.component.ts](src/app/app.component.ts)
	Standalone Angular component for the chat workspace, insights sidebar, project configuration, and UI state.

- [src/app/app.component.html](src/app/app.component.html)
	Angular template for the workspace layout, scope selector, assistant feedback, insights, and project forms.

- [src/app/api.service.ts](src/app/api.service.ts)
	Typed API contract and fetch wrappers for workflow, feedback, project configuration, groups, and ingestion status.

- [src/App.css](src/App.css)
	Shared workspace styling used by the Angular template.

## API Integration

- [proxy.conf.json](proxy.conf.json) forwards `/api` requests to `http://localhost:5217` during local Angular development.
- In production, the UI uses same-origin `/api` paths.
- [src/environments/environment.ts](src/environments/environment.ts) contains frontend-safe defaults derived from backend appsettings, including API base path, polling labels, similarity limit defaults, and email delivery mode.
- Backend-only values such as Azure OpenAI API keys and database connection strings must stay in API configuration, user secrets, environment variables, or a deployment secret store.

Endpoints called by the UI:

- `GET /api/projects/groups` — fetches available project groups for the scope selector.
- `POST /api/ticket/process` — main workflow endpoint; returns the `TicketWorkflowResult`.

Current request shape:

```json
{
  "title": "VG item missing",
  "description": "VG item missing",
  "selectedGroupIds": ["atcr", "fsco-fab"]
}
```

Pass `null` or omit `selectedGroupIds` to search across all project groups.

The response is expected to include:

- `suggestedRootCause`
- `suggestedResolution`
- `confidence`
- `similarIncidents`
- `failurePattern`
- `reasoning`

## Working Inside the Solution

`PoolSense.UI.csproj` keeps the frontend in the `.sln` without turning it into Razor or Blazor. The project file exists so that:

- the UI appears as a first-class project in Visual Studio and `dotnet build`
- frontend dependencies can be restored during MSBuild when `node_modules` is missing
- production assets are built before the overall solution build completes

In local frontend work, prefer `npm run dev`. In integrated validation or CI, prefer `dotnet build .\PoolSense.sln` from the repository root.

## Troubleshooting

- If the browser cannot reach the API during local development, confirm the backend is listening on `http://localhost:5217` or adjust [proxy.conf.json](proxy.conf.json).
- If the UI loads but workflow requests fail with `500`, check the API configuration for Azure OpenAI and PostgreSQL.
- If `dotnet build` fails inside `PoolSense.UI`, run `npm ci` manually once in this folder to confirm Node and npm are available on the machine.
