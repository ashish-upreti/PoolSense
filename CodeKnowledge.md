# GitHub Code Knowledge

## Enable

Code knowledge is disabled by default. Deploy and verify the .NET 10 upgrade before enabling it. Install a supported, patched .NET 10 runtime (and the .NET 10 Hosting Bundle when using IIS); Angular remains on version 18.

1. Apply [database/sqlserver-bootstrap.sql](database/sqlserver-bootstrap.sql) for the existing PoolSense schema, then [database/coderag-bootstrap.sql](database/coderag-bootstrap.sql) to the intended PoolSense SQL Server database. The additive CodeRAG script is idempotent. The runtime does not silently modify your database schema.
2. Register a GitHub OAuth App. Set its callback to the exact public PoolSense URL ending in `/api/github/callback`. For local development through the Angular proxy, use `http://localhost:4200/api/github/callback`.
3. Provide the following configuration. Supply the client secret through .NET user secrets or a server environment variable, not a committed settings file. Existing local appsettings files are ignored by Git.
4. Use one always-running Windows API instance, with persistent storage and the same Windows service identity. Grant only that identity and administrators access to the storage directory. Restart the API after configuration.
5. Sign in to PoolSense, open Application Configuration, and edit a saved application. Connect GitHub in the application's Git repositories section, then use Add repository for each URL and optional branch. Click Update Application to save the changes and queue indexing. New repositories default to `master`; a blank branch also resolves to `master`. Enter another branch explicitly when needed.

```json
{
  "CodeRAG": {
    "Enabled": true,
    "StoragePath": "D:/PoolSenseData/code-knowledge",
    "GitHubClientId": "YOUR_OAUTH_APP_CLIENT_ID",
    "CallbackUrl": "https://poolsense.example.com/api/github/callback",
    "ReturnUrl": "/",
    "RefreshMinutes": 60,
    "MaxArchiveMegabytes": 100,
    "MaxExtractedMegabytes": 500,
    "MaxRepositoriesPerProject": 20,
    "ContextCharacters": 24000
  }
}
```

The secret configuration key is `CodeRAG:GitHubClientSecret`; its environment variable equivalent is `CodeRAG__GitHubClientSecret`. Never paste real secrets into source-controlled HTTP examples, URLs, chat, or logs.

## Connection Troubleshooting

- A `503` with `Code knowledge is not enabled.` means the API's effective `CodeRAG:Enabled` setting is false. Enable it only after completing the database and OAuth setup above, then restart the API. An environment override uses `CodeRAG__Enabled=true`.
- Both `CodeRAG:GitHubClientId` and `CodeRAG:GitHubClientSecret` must be configured for OAuth. Register a [GitHub OAuth App](https://github.com/settings/applications/new) and supply its values on the server; signing in to PoolSense does not configure these values. For local development, use `http://localhost:4200` as the homepage and store the client ID and secret in the API project's .NET user-secrets. Restart the API after configuring them.
- For local development, the OAuth App callback must be `http://localhost:4200/api/github/callback`, matching `CodeRAG:CallbackUrl`. Access PoolSense through `http://localhost:4200` so its session cookie accompanies the callback.
- Git repositories appears inside the saved application's edit form, not in the configured-applications list. Connect GitHub first to reveal repository rows. When the connection-status request fails, Connect is disabled. Use Reload GitHub connection after correcting server configuration.

## Application Editing

- Each project supports multiple repositories. GitHub authorization belongs to the signed-in user; repository registrations belong to the selected project.
- Add repository creates a local draft row. URL, branch, enabled-state changes and removals remain unsaved until Update Application. Cancel edit discards pending repository changes without making repository requests.
- Update Application saves application settings first, then applies repository changes. New or changed enabled repositories are queued for indexing; unchanged enabled repositories are queued for a branch check unless already queued or indexing. Disabled repositories are not queued.
- Indexing runs asynchronously in the existing background worker. The application editor remains open to display repository status and the queued count. Saving does not wait for indexing to complete.
- Application and repository requests are not one database transaction. If a repository request fails, the editor reports the partial save and retains pending changes. Retry Update Application to continue; successfully saved repository rows retain their IDs so they are not created again. Cancel does not roll back changes already saved to the server.

## Access Rules

- PoolSense/AD login and GitHub authorization are separate. Each user connects their own GitHub account; no email-based account matching is used.
- The configuring user must be able to access the repository. Repository metadata is also filtered for the viewing user.
- Every interactive query checks the querying user's current GitHub repository/branch access before opening a cached index. Indexing-owner credentials are never a fallback for answering another user.
- Public repositories are allowed when readable through the user's connected GitHub authorization. Private repositories require that user's own access. OAuth's `repo` scope is broader than read-only; this integration performs only read operations. Organizations may require OAuth approval/SSO authorization.
- GitHub outages, rate limits, missing authorization and denied access exclude code evidence. Wiki and PoolDB still use their existing behavior.
- Unattended ticket processing has no querying GitHub user, so its stored recommendations and emails remain Wiki/PoolDB-only.
- Code-backed answers are interactive and ephemeral: they do not enter shared interaction content, incident knowledge, failure-pattern storage, or feedback promotion. Non-code answers receive a user/answer-bound feedback permit. When CodeRAG is enabled, older clients must forward `feedbackAuthorization` when submitting feedback; regenerate old answers before submitting feedback.
- Manual Disconnect revokes the user's GitHub grant for this OAuth app, invalidating all of that user's tokens for the app, then deletes the local connection and cancels pending OAuth callbacks. A GitHub response indicating the token is already missing also permits local cleanup. Other revocation failures keep the connection available for retry and show an error. Dependent indexing pauses when next checked; repository registrations are retained.
- PoolSense does not reconnect automatically. The user must click Connect GitHub again. Every connection requests GitHub's account picker (`prompt=select_account`) and the `repo` scope. After grant revocation, GitHub requests authorization again; users can review available organization access. PoolSense cannot force a new password entry, sign users out of GitHub globally, or bypass organization approval/SSO policies. If only an individual token was already invalid, review or revoke any remaining app authorization in GitHub Settings > Applications to reset consent.
- Another authorized project-config user can take indexing ownership. Signing out of PoolSense does not cancel the separately granted unattended indexing authorization.

## Index Lifecycle

- New/updated repository registrations are durable queued work in SQL. A single hosted worker checks for queued/due work every ten seconds; all actual branch checks are scheduled hourly by default.
- The worker checks the configured branch SHA. An unchanged commit with a valid index is reused without downloading or parsing. A changed commit downloads the exact GitHub commit archive and rebuilds that repository's index. Missing/incompatible indexes are rebuilt as recovery even if the commit is unchanged.
- GitHub archive APIs avoid ambient Git credentials, repository hooks and checkout scripts; Git is not required for this integration. Submodules and Git LFS objects are not expanded. Only github.com repositories are supported in this version.
- Archives and extracted bytes are bounded; path traversal is rejected and symlinks are skipped. C# declarations and syntax-scoped graph relations are retained; Python, TypeScript/JavaScript, HTML/CSS/SCSS, Markdown and SQL are searchable source chunks. Files over 2 MB and build/dependency directories are skipped.
- Each rebuild creates a separate SQLite snapshot. Publication uses a revision check; removed or reconfigured repositories cannot publish obsolete results. Queries read a coherent snapshot and cite immutable GitHub commit/file/line links.
- Failed refreshes retain the last successful same-configuration index. Authorized users can still receive that evidence with a stale status. Rate-limit reset/Retry-After times postpone retries when later than the normal interval. Builds have a 15-minute deadline.
- Queued/interrupted work is recovered after restart. The worker cleans abandoned generations and removed registrations' files. Storage must have enough capacity for current snapshots plus a staged build; set operating-system quotas and monitor disk usage.
- The configured branch stays pinned by name; changing a repository's default branch does not silently change an existing registration. Edit its branch and click Update Application to switch branches. Updating an unchanged repository checks for new commits rather than unconditionally rebuilding.
- Hourly checking is not an exact freshness guarantee: updates become searchable after the next successful check and build. Keep IIS/app hosting active (account for idle shutdown/recycling); the worker catches up on restart.

## Storage and Operations

SQL stores repository configuration/status and encrypted OAuth tokens. Source and index snapshots live under `StoragePath`; Data Protection keys are in its `keys` directory, encrypted using Windows DPAPI. Do not publish this directory as static content. There is no plaintext-token fallback; non-Windows hosting needs a separately designed protected-key configuration and is rejected when the feature is enabled.

Back up SQL and protected key material together. DPAPI keys are tied to the Windows identity/machine; moving hosts requires an appropriate key migration or GitHub reconnection. Source/index caches can be rebuilt. A single shared local directory is not a supported multi-instance deployment.

Application HTTP client logging is disabled for GitHub OAuth/download clients. Request-start diagnostics are suppressed to avoid recording OAuth callback parameters. Configure IIS/reverse-proxy/APM logging to omit query strings on `/api/github/callback` and never capture OAuth bodies, authorization headers, or source/answer content.

New repository mutations require the `X-PoolSense-CSRF: 1` header as well as existing PoolSense authentication. The UI supplies it. There is no new global project RBAC model: existing project-configuration users retain management access, additionally constrained by GitHub access.

## Verify

```powershell
dotnet build .\PoolSense.sln --configuration Release
dotnet test .\PoolSense.Api.Tests\PoolSense.Api.Tests.csproj --filter "FullyQualifiedName~CodeRAG"
npm run build --prefix .\PoolSense.UI
npm run lint --prefix .\PoolSense.UI
```

With the UI development server running on localhost:4200:

```powershell
npm run test:code --prefix .\PoolSense.UI
```

The browser tests mock API responses; they verify desktop/mobile repository operations without using real credentials. Unit tests verify commit reuse/change, graph retrieval and deleted files, access isolation, snapshot publication races, archive traversal, OAuth state consumption and feedback permits.

Before production enablement, test against a dedicated non-production SQL database and GitHub repository: authorize two users with different access, add/index/query, verify an unchanged refresh, push changed/deleted files, refresh and verify commit-pinned citations, revoke access, then restart the host and check recovery. Real OAuth exchange, organization policies, SQL schema compatibility and production hosting cannot be certified by mocked tests alone. Do not run NYRA live smoke tests without an intended test environment.