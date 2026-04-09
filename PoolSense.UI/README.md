# PoolSense.UI

PoolSense.UI is the React and Vite frontend for PoolSense, wrapped in an SDK-style .NET project so it can live in the solution as a first-class project.

## What the UI Does

The current workspace is a two-panel operator surface:

- The left panel is the incident conversation workspace with a project group scope selector.
- The right panel shows structured operational context returned by the API.
- Quick prompts seed common incident descriptions for faster smoke testing.
- A `GroupSelector` above the chat input lets operators narrow the similarity search to one or more configured project groups (e.g., ATCR, FSCO-FAB, DxCR). Leaving all groups unchecked searches across all available knowledge.
- The UI calls the main workflow endpoint and renders root cause, resolution, confidence, similar incidents, and failure-pattern metadata from the response.

The main interaction starts in [src/App.tsx](src/App.tsx), which fetches available project groups on mount, sends incident text and selected group IDs to the API, and stores the returned `TicketWorkflowResult` for both the chat transcript and the insights panel.

## Development

- `npm run dev` starts the Vite development server on `http://localhost:5173`.
- `dotnet build PoolSense.UI.csproj` runs the frontend production build through MSBuild.
- `npm run build` performs the standalone TypeScript and Vite production build.
- `npm run lint` runs ESLint against the UI codebase.

## Runtime Flow

1. On load, the UI calls `GET /api/projects/groups` to populate the project group selector.
2. A user types an incident summary or clicks a suggested prompt.
3. The UI posts the message to `POST /api/ticket/process` with the selected group IDs.
4. The same text is used for both `title` and `description` in the request body.
5. The assistant response is rendered in the chat area.
6. The insights sidebar renders confidence, failure pattern details, related incidents, and a lightweight telemetry visualization.

Key UI modules:

- [src/components/ChatPanel.tsx](src/components/ChatPanel.tsx)
	Conversation area, prompt chips, composer, and loading/error states.

- [src/components/GroupSelector.tsx](src/components/GroupSelector.tsx)
	Project group scope selector. Fetched groups are rendered as toggle chips. An empty selection means "All groups".

- [src/components/InsightPanel.tsx](src/components/InsightPanel.tsx)
	Summary cards for confidence, routing metadata, reasoning, telemetry, and historical matches.

- [src/components/IncidentList.tsx](src/components/IncidentList.tsx)
	Renders the list of similar historical incidents returned by the workflow.

- [src/components/PatternList.tsx](src/components/PatternList.tsx)
	Renders failure pattern details.

- [src/components/TelemetryChart.tsx](src/components/TelemetryChart.tsx)
	Lightweight incident-count chart using Recharts.

- [src/services/api.ts](src/services/api.ts)
	Typed API contract and fetch wrappers for the workflow request and group listing.

## API Integration

- `VITE_API_PROXY_TARGET` defaults to `http://localhost:5217` for local proxying.
- `VITE_API_BASE_URL` can be used when the frontend needs to call an external API base URL directly.

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

- If the browser cannot reach the API during local development, confirm the backend is listening on `http://localhost:5217` or adjust `VITE_API_PROXY_TARGET`.
- If the UI loads but workflow requests fail with `500`, check the API configuration for Azure OpenAI and PostgreSQL.
- If `dotnet build` fails inside `PoolSense.UI`, run `npm ci` manually once in this folder to confirm Node and npm are available on the machine.
