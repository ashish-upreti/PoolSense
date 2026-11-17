# GitHub Code Knowledge

## Enable

Code knowledge is disabled by default. Deploy and verify the .NET 10 upgrade before enabling it. Install a supported, patched .NET 10 runtime (and the .NET 10 Hosting Bundle when using IIS); Angular remains on version 18.

1. Apply [database/sqlserver-bootstrap.sql](database/sqlserver-bootstrap.sql) for the existing PoolSense schema, then [database/coderag-bootstrap.sql](database/coderag-bootstrap.sql) to the intended PoolSense SQL Server database. The additive CodeRAG script is idempotent. The runtime does not silently modify your database schema.
2. Configure a server-managed GitHub personal access token (PAT) as `CodeRAG:GitHubToken` using .NET user secrets or the API host's protected environment configuration. Do not put a real token in source-controlled settings, HTTP examples, URLs, chat, or logs. Grant only the read permissions needed for repository metadata and contents, and authorize the token for organization SSO when required.
3. Provide the non-secret settings below. Existing local appsettings files are ignored by Git.
4. Use one always-running Windows API instance, with persistent storage and the same Windows service identity. Grant only that identity and administrators access to the storage directory. Restart the API after configuration changes.
5. Sign in to PoolSense, open Application Configuration, and edit a saved application. GitHub access is managed by the administrator; add repositories and optional branches in the application's Git repositories section. Click Update Application to save changes and queue indexing. New repositories default to `master`; a blank branch also resolves to `master`.
6. Open Code Repositories to chat against the indexed source. The application scope works like the main search scope: it defaults to **All Applications**, or pick one application from the dropdown and use **+ Add** to include more. Answers draw on the indexed repositories of every application in scope, and saved conversations are kept per exact scope (single-application conversations stay visible when only that application is selected). Re-run [database/coderag-bootstrap.sql](database/coderag-bootstrap.sql) on existing databases to add the `scope_json` column this relies on.

```json
{
  "CodeRAG": {
    "Enabled": true,
    "StoragePath": "artifacts/code-knowledge",
    "RefreshMinutes": 60,
    "MaxArchiveMegabytes": 100,
    "MaxExtractedMegabytes": 500,
    "MaxRepositoriesPerProject": 20,
    "ContextCharacters": 24000
  }
}
```

The PAT configuration key is `CodeRAG:GitHubToken`; its environment variable equivalent is `CodeRAG__GitHubToken`. For local development, store it in the API project's .NET user-secrets. The browser never receives the PAT. Never paste real secrets into source-controlled HTTP examples, URLs, chat, or logs.

## Connection Troubleshooting

- A `503` with `Code knowledge is not enabled.` means the API's effective `CodeRAG:Enabled` setting is false. An environment override uses `CodeRAG__Enabled=true`.
- The Git repositories section reports whether administrator-managed access is configured and available. If it is unavailable, check the API host's `CodeRAG:GitHubToken`, token validity, repository permissions and organization SSO authorization. The status endpoint never returns the token.
- If GitHub access reports that Windows Data Protection could not protect the CodeRAG key ring, check that the IIS application pool has **Load User Profile** enabled and that its identity can read/write the configured CodeRAG storage `keys` directory. Recycle the pool after correcting host configuration. Do not delete the existing key ring or disable DPAPI protection; existing protected data depends on those keys.
- Missing GitHub PAT does not prevent PoolSense startup or disable Wiki/Pool features. Code retrieval and repository-management actions report that managed access is unavailable until an administrator configures it.

## Rich repository answers

Repository chat uses the same safe rich-response renderer as triage and saved pool
follow-ups, for both new responses and restored history. In addition to GFM tables
and code blocks (copy/download/expanded view), it supports top-level `mermaid` and
constrained JSON `chart` fences. See [Rich AI responses](README.md#rich-ai-responses)
for exact schemas, examples, limits, supported diagram/chart types and export
behavior. Charts and diagrams load only when needed. Numeric charts require
supplied quantitative evidence; qualitative source relationships should be
labelled conceptual diagrams. Only Markdown source is stored; HTML/SVG is
sanitized again when displayed.
Paragraphs and headings use explicit vertical spacing in the shared renderer,
including restored history and narrow/mobile layouts.
Each live repository chat answer shows its browser-measured completion time
("Generated in 42.5 s", or "Generated in 1 min 12 s" from one minute onward).
The time is not stored, so answers reopened from saved history show no duration.
Flowchart rectangle labels containing route placeholders or parentheses (for
example `B[POST /pool/{poolId}/troubleshoot]`) are double-quoted in a validated
rendering copy so Mermaid treats them as text. A rendering notice identifies this
adjustment; saved Markdown, Source, Copy and source downloads remain unchanged.
This also applies to restored history. Existing quoted labels and other node
shapes/diagram types are not rewritten; unsafe content still fails validation.

Repository process and relationship requests (for example, "Show the scheduler
workflow" or "Draw the dependencies") use the same structured graph validation
as Issues. The model returns an explanation plus nodes and edges referencing
current code evidence IDs (`C1`, `C2`, etc.). The server checks graph structure
and source references, generates bounded Mermaid, and removes model-authored
Mermaid/chart fences from that explanation. Citations remain outside the graph;
the renderer's restrictions on directives, links, HTML and custom styling remain
unchanged. Graphs are limited to 12 nodes and 20 edges. Missing connections are
shown as explicit gaps, not invented links; invalid or unsupported graphs produce
an unavailable notice. Reference validation does not prove that an edge is
semantically correct or that indexed code is deployed.

The final graph-containing Markdown is saved with the conversation, so new
answers and restored history use the same renderer. Ordinary non-graph answers
keep their existing Markdown behavior. Existing saved answers are not rewritten:
if an old answer reports "Unsafe diagram", ask the graph question again after
deploying the updated API to generate a validated replacement.

Follow-up requests send a bounded copy of earlier messages: each history entry
is limited to the API's 8,000-character `Content` limit, with a truncation marker
when necessary. The current question and full displayed/stored answers are not
shortened. This also applies when continuing a restored conversation.

Assembled generic LLM prompts bind source/history/question text into a fixed
Semantic Kernel user-message template. Retrieved code containing `{{...}}` or XML
message markup stays literal text rather than being parsed as template functions
or new chat messages. This applies to repository answers and bounded knowledge
generation; agents with intentional prompt-template variables keep their existing
template behavior. Embedding settings do not need to be disabled for this fix.

Code citations use clickable Markdown `[path:line-range](SourceUrl)` with the
exact retrieved, commit-pinned HTTPS URL; prompts never synthesize links from
repository/path prose. Main investigations and restored history keep structured
**Code source references** in Operational context > Evidence & supporting documents >
Code, without duplicating them beneath the answer. Inline Markdown citations remain
clickable; reopen context to access legacy prose-only source references. Repository
chat and pool follow-ups retain source references beneath their answers. The shared
source component shows repository, path, lines and indexed
commit, with independently collapsible source excerpts. Missing/invalid,
non-HTTPS or credential-bearing URLs are not linked and display an explicit
unavailable message. An indexed commit is not evidence that the same version
is deployed.

Main chat continuation never carries raw earlier repository evidence or code-backed
assistant answers into the next prompt. Those assistant turns are withheld; fresh
retrieval checks the managed PAT's GitHub access for each follow-up. Previous
access, citations or a saved answer cannot substitute for current authorization.

## Application Editing

- Each project supports multiple repositories. GitHub access is shared through the administrator-managed PAT; repository registrations belong to the selected project.
- Add repository creates a local draft row. URL, branch, enabled-state changes and removals remain unsaved until Update Application. Cancel edit discards pending repository changes without making repository requests.
- Update Application saves application settings first, then applies repository changes. New or changed enabled repositories are queued for indexing; unchanged enabled repositories are queued for a branch check unless already queued or indexing. Disabled repositories are not queued.
- Indexing runs asynchronously in the existing background worker. The application editor remains open to display repository status and the queued count. Saving does not wait for indexing to complete.
- Application and repository requests are not one database transaction. If a repository request fails, the editor reports the partial save and retains pending changes. Retry Update Application to continue; successfully saved repository rows retain their IDs so they are not created again. Cancel does not roll back changes already saved to the server.

## Access Rules

- PoolSense/AD login remains required for interactive HTTP endpoints. GitHub authorization is separate and is not connected per user; all repository access uses the configured administrator-managed PAT.
- Every interactive query and trusted new-ticket polling checks the managed PAT's current access to each repository before opening a cached index. Repository IDs, project scope, and generation/configuration are revalidated before evidence is returned. Warmed or cached data does not grant authorization.
- The PAT must be able to read repository metadata and contents for every configured repository. Organizations may require token SSO authorization. This integration performs GitHub read operations only.
- GitHub outages, rate limits, missing authorization and denied access exclude code evidence. Wiki and PoolDB still use their existing behavior.
- Trusted new-ticket polling can retrieve code with the managed PAT and keeps repository evidence in its normal stored recommendation/email result. Other background ingestion paths retain their existing behavior. Missing, invalid, revoked or inaccessible PAT access produces explicit coverage/status and no code evidence; there is no alternate per-user GitHub credential path.
- Code-backed answers are interactive and ephemeral: they do not enter shared interaction content, incident knowledge, failure-pattern storage, or feedback promotion. Non-code answers receive a user/answer-bound feedback permit. When CodeRAG is enabled, older clients must forward `feedbackAuthorization` when submitting feedback; regenerate old answers before submitting feedback.

## Search and background preparation

Searches never create an index, refresh a branch, discover branch heads, generate
OKF documents or generate chunk embeddings. Each request still makes **current
GitHub repository-access checks**; permission checks are not head/freshness checks
and are not replaced with an indefinitely cached authorization verdict. OKF,
vectors and cached source are read only after access succeeds. Coverage reports
only confirmed accessible repositories, not denied repository names/counts.
All deployments use the shared-grant policy: checks use the configured PAT, not
proof of each PoolSense user's individual GitHub entitlements. A user who can
query an application can receive evidence from repositories registered to that
application whenever the server PAT can read them. Do not use this model if
individual GitHub entitlements must constrain each user's code access.

Ready generations are immutable. Each reader holds a generation lease while
using its own read-only SQLite connection; publication switches the SQL generation
pointer atomically with the existing revision check. Pruning defers deletion until
the last reader releases its lease. Searches do not hold the former global index
gate. Per-repository refresh exclusion prevents duplicate builds; it does not
block readers or refreshes of other repositories.

The background scheduler checks due/queued jobs every 10 seconds by default,
starts at most four jobs per cycle, and prepares at most two repositories
concurrently. Startup jitter is 0-5 seconds; scheduler failures back off up to
five minutes. A repository's normal head-check interval remains `RefreshMinutes`
(60 by default). Queued refreshes are coalesced by repository ID/revision. Each
build has a 15-minute cancellation deadline and stages all artifacts before
publication. Failed source builds retain the previous complete generation;
failed optional preparation keeps lexical source usable and reports the failure.
An interrupted indexing record is recovered by the next background worker cycle.
Unchanged-commit optional preparation copies the previous leased source tree
locally instead of downloading it again. A compatible SQLite graph is backed up
and its repository roots are rebased transactionally in the **new** generation;
source parsing is not repeated just to improve optional knowledge. Copying is cancellation-aware, rejects reparse points, and retains the
extracted-byte cap plus 50,000-entry/128-level bounds. If the branch advances during
a build, the completed pinned snapshot is served as queued/stale while another
background refresh is scheduled.

Preparation version 6 upgrades older ready generations through this same bounded
background queue, even before their next hourly head check. Each job still checks
current indexing access and the branch head. New-version incomplete preparations
retain their normal retry/backoff interval instead of retrying every scheduler tick.
Search continues using the old leased source until atomic publication; it never
performs the upgrade. Existing vectors can reuse content/profile-matching cached
embeddings. Paid explanation and semantic generation are not globally enabled.
Unchanged, successfully prepared knowledge also retains its original Markdown/log
and timestamps across unrelated optional retries; failed enrichment remains
explicit and eligible for the normal bounded retry.
Activating `CodeRAG:Semantic:Enabled` also schedules previously disabled or missing
ready semantic artifacts before the next hourly check. Failed/partial attempts
retain normal retry backoff; activation never starts indexing from a query.
This checkout enables semantic search and adaptive coverage in
`PoolSense.Api/appsettings.json`. Keyword/symbol/graph retrieval remains active;
semantic matches supplement it rather than replace it. Repository routing and
paid OKF explanations remain disabled. Check effective configuration (including
environment overrides); `CodeRAG__Semantic__Enabled=false` disables semantic work.
After deploying and restarting the API, the worker prepares missing vectors in
bounded background batches. This sends redacted source excerpts and semantic
queries to the configured embedding provider and can incur usage costs. Existing
lexical indexes stay searchable while vector coverage grows; restarting does not
mean all repositories have completed semantic preparation.

Code retrieval searches at most three repositories concurrently by default.
The search deadline is 20 seconds; a final access/configuration recheck has a
separate maximum two-second budget and withholds evidence if it cannot complete.
Caller cancellation propagates. A deadline preserves already collected evidence
as `partial`, rather than treating it as skipped/no matches. `not-ready`,
`no-accessible-repositories`, `no-accessible-matches`, `timed-out`, `failed`,
`authorization-unavailable`, `disabled` and `not-authenticated` have distinct
meanings. Results carry `codeSearchCoverage` with confirmed accessible/searched
counts, deadline/access-completion flags, knowledge/semantic status, indexed date
and warnings. Main result JSON persists this coverage in history; repository
chat and pool follow-ups also retain a Markdown coverage notice with saved answers.
Code source references and the main partial-status notice surface these distinctions.
An indexed snapshot is never represented as proof of the deployed version.

Candidate selection fuses within-retriever ranks rather than comparing raw BM25
scores from independent databases. Exact identifiers/paths and globally comparable
query-term coverage remain strong signals. A bounded global candidate pool keeps
late strong matches, merges lexical/semantic/knowledge references, suppresses
identical/overlapping spans and applies a soft repository-diversity penalty.
There is no equal excerpt budget per repository; weak excerpts do not reserve
space merely because their repository was searched.

Repository routing is **off by default** pending production evaluation. When
enabled, current-access-checked repository identity/OKF source references shortlist
deep retrieval. Weak term coverage, weak evidence or a cross-repository workflow
expands to the broader authorized scope. All applications remains eligible;
routing never rewrites the selected application scope. A confident shortlist
still reports its actual non-exhaustive coverage.

### OKF v0.2 project knowledge

The canonical specification is
[GoogleCloudPlatform/open-knowledge-format/SPEC.md](https://github.com/GoogleCloudPlatform/open-knowledge-format/blob/main/SPEC.md),
not the frozen knowledge-catalog copy. The independent
[OKF Builder Reference](https://openknowledgeformat.com/) and its
[retrieval guide](https://openknowledgeformat.com/guides/okf-retrieval) are useful
implementation references, not the normative specification or Google-maintained
tooling. Its example company/reference data is fictional, not business evidence.
Validation stays local; private source/bundles are never uploaded to its validator.
No Google account, cloud service or SDK is required. Bundles live under
`<repository>/<generation>/okf`, outside the downloaded source tree.

Producer **0.5** emits a bounded concept map, not a document per file:

| Concepts | Deterministic evidence and limits |
|---|---|
| Glossary and domain declarations | Exact identifiers, split-identifier aliases and explicit declaration documentation; no guessed acronym expansions |
| Workflows | Indexed caller/callee relationships, including resolution strength and partial coverage; at most 2 hops, 12 relations and 8 source spans per flow |
| Calculations and SQL lineage | Actual parameter/return expressions, SQL read/write tokens and explicit join fragments; no inferred same-name joins or claims of computed correctness |
| Interfaces and configuration | Actual interface declarations and configuration access/prerequisite fragments, only when source supports them |
| Reference overview | Small source-linked map supporting the typed concepts, not invented product behavior |

Selection reserves bounded real call/implementation endpoints and supported
configuration candidates before generic sampling, so large early-sorted declaration
sets cannot starve workflows or contracts. It does not invent a missing topic.
Generation considers at most 1,024 candidates/512 indexed edges and selects at
most 256 symbols and 14 concepts, further constrained by configured document and
byte limits. Balanced selection and meaningful source patterns replace the old
alphabetical overview/architecture first-N samples. The root index has typed
descriptions/local links and `okf_version: "0.2"`; the ISO-date generation log
records source/producer/content identity, not timestamp-only rewrites.
Expected selection/coverage caps are recorded separately as `KnowledgeLimitations`,
not mislabeled as failed preparation. Actual validation/provider failures remain
`KnowledgeErrors`. Long/dense concepts are fitted by compacting optional metadata
and removing whole cited entries, never by truncating source names, hashes or URLs.

Concepts are UTF-8 Markdown with YAML frontmatter; only nonempty `type` is universally
required. Unknown types/metadata round-trip, a bare `verified` mapping is treated
as one verification event, and optional fields are not prerequisites for use.
Concept IDs are bundle-relative paths without `.md`; `index.md` is a listing and
`log.md` is a date-grouped change log, not concepts. Broken concept links are
tolerated. Local traversal is bounded and cannot escape the bundle or load external
resources; unsafe YAML tags/aliases, oversized/deep metadata and symbolic-link
traversal are rejected. Executors/attesters are metadata only and are never run.

```markdown
---
type: Reference
title: Settlement source
generated: { by: process:poolsense-index, at: 2026-11-10T10:00:00Z }
status: draft
poolsense_commit: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
sources:
  - id: settlement
    resource: https://github.com/example/service/blob/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/Settlement.cs#L1-L20
    title: Settlement.cs, lines 1-20
---
Settlement source location.[^settlement]

[^settlement]: Indexed source; verify deployed commit separately.
```

Generated concepts are **unverified drafts**, not human-reviewed or machine-verified
because an LLM wrote them. Optional `stale_after` is an absolute offset-bearing
timestamp, not a TTL. Source commit/hash extensions invalidate changed supporting
source; search follows references to actual indexed spans and does not substitute
generated claims for code. A calculation concept is not an attested execution or
an automatically verified business rule.

After current repository authorization, retrieval stages are:

1. Inspect the index/compact metadata catalog for the immutable generation.
2. Rank relevant title/description/type/tags/aliases and bounded lexical terms;
   current/stable/trust signals break relevance ties, without excluding unverified,
   stale or deprecated concepts.
3. Load relevant sections from at most four concepts (12,000 characters total).
4. Follow only inline/cited needed concept links, at most two neighbors and one
   hop by default (hard caps: four neighbors/two hops), with cycle/path protection.
5. Resolve cited source IDs to exact commit/hash-checked indexed spans, then combine
   them with independent lexical/graph retrieval and optional semantic evidence.

The cache retains metadata and bounded term postings, not whole document bodies.
It holds at most eight catalogs, keys commit/options/file signatures, evicts older
entries and forgets retired generations. Cold loading is capped at 2 MiB per
bundle by default (hard cap 8 MiB); warm reads do not reparse all YAML or reload
all bodies. Missing/unmatched/invalid knowledge never hides lexical source.
Alias-derived relevance can promote a validated source, but exact code identifiers
remain stronger. Generated narrative is used for discovery only, not inserted as
authoritative answer evidence. Stale/invalid knowledge is explicitly reported;
current source must still corroborate every returned span.

Optional NYRA explanation generation is off by default,
uses at most one model invocation per prepared bundle, 12,000 input characters,
6,000 accepted output characters, a hard 1,500 output-token limit and a 30-second
timeout. Model errors leave deterministic knowledge/source usable with explicit
preparation status; generation is never paid for on each search.

For filesystem export, copy an `okf` directory from a completed generation without
editing it. The bounded `OkfLocalBundle.ReadExisting`/`Traverse` and
`OkfBundle.SearchBundle` APIs also consume human-authored bundles in an explicitly
supplied staging/export directory, including unknown metadata and missing indexes.
Never edit a live published generation in place. Source references must resolve
to the currently authorized repository/commit to become code evidence. There is
no network traversal or broad import/management UI.

### Optional semantic retrieval

The options-class fallback for `Semantic.Enabled` is **false**, but this checkout's
base configuration explicitly sets it to **true**, with adaptive coverage enabled.
It uses the existing configured
NYRA embedding provider/model (`text-embedding-3-small` in the current setup),
with the existing 1,536 dimensions; it does not switch models or add a new service.
Fixed legacy deployments retain their configured total `MaxChunks` (64 by default).
Opting into `AdaptiveCoverageEnabled` uses a repository-size target
bounded by an explicit total ceiling. Total coverage is separate from the budget:
each background batch embeds at most **64 new chunks**, then publishes a usable
partial generation. Matching existing vectors are reused, so later batches
advance instead of spending again on the same first 64 symbols. Raw chunks
over 64 KiB are skipped explicitly. Text inputs are capped at 2,000 characters.
Matching redacted content/profile vectors are reused from the previous leased
generation; small `MaxNewEmbeddingsPerBuild` values support incremental bounded
checkpoints across queued/periodic refreshes. The staging `semantic.json` artifact
is size/shape/profile/commit checked and atomically published with the source
generation. Old unredacted artifact versions are rejected.

Adaptive totals use **useful unique indexed source**, not raw file count or an LLM
guess about business importance: up to 512 useful chunks targets 128; up to 4,096
targets 512; larger repositories target 768. Targets are bounded by available
useful coverage and configured `MaxChunks`, which is the explicit total ceiling.
Candidate work is capped at 8,192 candidates/16 MiB; scan-capped coverage is
identified rather than presented as a complete corpus count. Selection balances
directories, languages and useful component roles, deduplicates source and avoids
generated/vendor/style noise. Valid legacy vectors are reused while sampling grows.
Cached coverage is verified **before** future noise filtering or sampling, with
root-independent source locators. A bounded same-head/profile recovery handles
early v3 migrations that excluded initial legacy spans: original prefix-64 spans
remain eligible, and missing vectors are regenerated only within the normal
batch budget. Already-retired vector bytes cannot be reconstructed; no fabricated
cache recovery is claimed. Recovery state persists until these spans are covered.

Manifest v3 stores little-endian float32 vectors as base64, keeping 768 vectors
within the validated 8 MiB file bound. Valid v2 float-array artifacts and their
original checksums remain readable for migration/cache reuse. Float dimensions,
finiteness, content hashes, provider profile, source commit and snapshot identity
remain checked; compact serialization does not weaken authorization or validation.
The maximum raw float payload is about 4.5 MiB per repository at 768 × 1,536,
plus bounded manifest/metadata overhead; decoding and cosine work are bounded by
the total cap. Larger coverage increases disk/CPU/provider cost, not guaranteed quality.

At request time, semantic search generates **at most one code-query embedding per
request-scoped service**, never one per repository/chunk. An exact same-query,
same-profile/settings Pool DB embedding is reused when available (notably main
follow-ups). Different query text is not falsely treated as reusable; a repository
chat's additional lexical fallback cannot spend a second query embedding.
Endpoint/model/dimension identity and finite vectors are checked, including
profile changes during a provider await. Missing/incompatible/failed semantic
artifacts/providers explicitly degrade to working lexical+graph search; they do
not trigger embedding-index creation from a search.

### Retrieval caches

The API keeps three ephemeral process-memory caches; restarting the process clears
them. None is a final-answer cache and none replaces request authentication or
per-repository GitHub access checks.

- NYRA client-credential tokens are keyed by a SHA-256 identity over issuer/token
  endpoint, client ID/secret, audience and scope; plaintext credentials and bearer
  tokens are never cache keys or log fields. Tokens are retained only when a real
  `expires_in` or JWT `exp` exists, with a refresh margin (up to 60 seconds).
  OIDC discovery is cached per issuer for one hour. Concurrent refreshes share one
  request while caller cancellation only cancels that caller's wait. Token HTTP uses
  a process-pooled client retaining the configured proxy, `NO_PROXY` and CA behavior.
  Embedding and read-only document-retrieval calls invalidate and retry once on
  401; chat generations use the current cached credential and invalidate on 401
  but are never replayed. User/repository permissions are always checked separately.
- Decoded semantic snapshots are keyed by full generation path, artifact size and
  timestamps, expected commit, embedding profile, dimensions and read limit.
  Validated immutable matrices and precomputed norms use an LRU capped at 8 snapshots
  and an estimated 64 MiB. Modified/replaced artifacts miss the cache and repeat the
  complete format/hash/profile/finiteness validation; failed reads are not retained.
  Retired generations evict their decoded entry before cleanup, without holding file
  handles or interfering with active snapshot leases.
- Exact CodeRAG query embeddings are keyed by the exact question hash, the full
  query-cache profile (including a hash of credential identity), dimensions and a
  hash of the authenticated user scope. This query-only identity does not alter the
  persisted semantic-index profile or invalidate existing indexed vectors. The
  10-minute LRU is capped at 256 vectors/32 MiB,
  returns defensive copies, single-flights concurrent identical queries, and
  excludes background chunk generation. Pool follow-ups may still pass an already
  computed same-query/same-profile vector directly. Failed or invalid vectors are
  never cached; actual provider-usage logging occurs only on cache misses that call
  the embedding service.

### Process-start cache warmup

`RetrievalCacheWarmup` is enabled by default. Its hosted worker yields during host
startup and begins only after `ApplicationStarted`, so API readiness does not wait
for cache reads. It selects up to eight enabled, ready published generations in
oldest-indexed-first order, validates semantic profile/commit and OKF snapshot
metadata, and uses the existing `ITicketKnowledgeEmbeddingStore.GetTicketKnowledgeAsync`
path for Pool-history vectors. At most two warm operations run concurrently. The
semantic LRU remains capped at eight entries/64 MiB, and the OKF catalog at eight
entries with the existing per-bundle traversal/read limits; larger repository
populations are not swept repeatedly at startup.

After a new generation is published, a bounded 16-item queue coalesces pending
warmups by repository and keeps only the newest generation. A worker rechecks that
the generation is still published and ready, then holds the normal snapshot lease
only while validating/loading its already-published artifacts. Queue overflow,
missing or invalid artifacts, SQL errors, and cancellation leave normal lazy
loading available; there is no provider retry loop. Pool-history invalidation keeps
its existing versioned `GetOrLoad` behavior, so the next ordinary request reloads
it lazily. Warmup never generates vectors, calls GitHub/NYRA/LLMs, creates fake
queries, warms unknown-query embeddings, exposes cached metadata, or caches an
authorization decision. Every interactive code request still performs its
current GitHub access check before reading any warmed generation. Cache warming
removes only initial file/catalog/vector loading; classification and answer-model
latency remain.

The conservative limits can be disabled or adjusted within the hard cache bounds:

```json
{
  "RetrievalCacheWarmup": {
    "Enabled": true,
    "MaxStartupSnapshots": 8,
    "MaxConcurrency": 2,
    "QueueCapacity": 16
  }
}
```

Startup logs report aggregate warmed repository/cache counts, vector/catalog bytes,
and elapsed milliseconds; per-repository failures include only the repository ID
and failure type. Warmup runs in process memory and is repeated after restart.

Progressive batches wait a configurable cooldown (300 seconds in this checkout);
missing/least-covered repositories are prioritized, with at most two jobs and four
starts per scheduler cycle. Provider failures keep normal retry backoff rather
than a paid hot loop. A reached configured target is **not** full-corpus coverage.
Coverage responses/history expose known indexed/eligible/target counts and whether
the configured targets are reached. Source/profile changes validate reuse again.

Checkpoint vectors are retained in the private repository `semantic-resume`
directory, never queried directly. They survive ordinary generation pruning and
are consumed only by current-authorized background preparation; successful atomic
publication clears the checkpoint, and repository deletion removes it. Published
code/OKF snapshots remain available while a partial batch is prepared. Unchanged
source and graph are copied/rebased locally without a download or reparse where compatible.

### Configuration and bounded rollout

The following supplements the existing `CodeRAG` configuration. Keep routing,
semantic retrieval and LLM enrichment gated until a small authorized sample has
been evaluated. Enabling a gate does not immediately run costly generation over
all repositories; background due/explicitly queued/version-upgrade jobs honor the bounded batch,
concurrency and per-build budgets. Refresh a small selection through the existing
repository refresh action to prepare artifacts incrementally.

```json
{
  "CodeRAG": {
    "SearchConcurrency": 3,
    "SearchDeadlineSeconds": 20,
    "CandidatesPerRepository": 20,
    "GlobalCandidateLimit": 256,
    "MaxEvidenceSpans": 12,
    "RepositoryRoutingEnabled": false,
    "RepositoryShortlistSize": 3,
    "BackgroundConcurrency": 2,
    "BackgroundBatchSize": 4,
    "BackgroundCheckSeconds": 10,
    "StartupJitterSeconds": 5,
    "Knowledge": {
      "Enabled": true,
      "EnableLlmEnrichment": false,
      "MaxSymbols": 80,
      "MaxBundleBytes": 2097152,
      "MaxSelectedConcepts": 4,
      "MaxNeighborConcepts": 2,
      "MaxNeighborHops": 1,
      "MaxReadCharacters": 12000,
      "MaxLlmInputCharacters": 12000,
      "MaxLlmResponseCharacters": 6000,
      "LlmTimeoutSeconds": 30
    },
    "Semantic": {
      "Enabled": true,
      "AdaptiveCoverageEnabled": true,
      "MaxChunks": 768,
      "SmallRepositoryTarget": 128,
      "MediumRepositoryTarget": 512,
      "LargeRepositoryTarget": 768,
      "SmallRepositoryThreshold": 512,
      "MediumRepositoryThreshold": 4096,
      "BatchCooldownSeconds": 300,
      "MaxNewEmbeddingsPerBuild": 64,
      "EmbeddingBatchSize": 8,
      "MaxTextCharacters": 2000,
      "Dimensions": 1536,
      "MaxManifestBytes": 8388608
    }
  }
}
```

Existing SQLite indexes remain searchable immediately; additive snapshot artifacts
and preparation-profile markers are built in the background. No destructive
SQLite schema migration, source-repository modification, new SQL Server schema or
cloud account is needed. The existing `ContextCharacters` (24,000 default),
archive/extracted-size limits and refresh interval still apply.

### Reproducible evaluation and limitations

`OkfKnowledgeEvaluationTests` compares the frozen old bounded summary producer/
reader, code-only retrieval and staged producer/reader on **identical synthetic
C#/SQL sources**. It records source recall@12/rank, bytes actually loaded, cold
latency and five-run warm medians. `POOLSENSE_OKF_EVAL_OUTPUT` writes the measured
JSON; `POOLSENSE_OKF_SAMPLE_OUTPUT` exports only that synthetic bundle.
`POOLSENSE_OKF_UPGRADE_ROOT` explicitly enables a single real local **shadow**
preparation check: copy/rebase an existing graph, prepare with no download or LLM,
validate old data unchanged, then remove only its temporary shadow directory.
`POOLSENSE_OKF_UPGRADE_OUTPUT` records counts/status without private source prose.

In the recorded four-case invoicing fixture, old summary knowledge had source
recall@12 of 0; staged knowledge reached 1 with first relevant ranks 1-2.
Code-only retrieval already had recall 1: this is improved **knowledge coverage**,
not proof that overall code recall increased. Warm knowledge reads loaded
3.9-11.5 kB versus 12.2 kB for the old summaries. Cold catalog construction and
source corroboration add work; timings do not establish a universal speedup.
The separate same-fixture routing comparison retains all current access checks
and broad fallback, but gives no production justification for enabling routing.
Routing and paid enrichment remain disabled. Semantic retrieval and adaptive
coverage are enabled in this checkout's base configuration; deployments can
explicitly disable them through configuration overrides.

`CodeRetrievalEvaluationTests` freezes the previous sequential/head-check/equal-budget
algorithm before refactoring. `CodeRetrievalImprovementsTests` compares the same
four synthetic source repositories for exact symbols, business paraphrases,
cross-repository workflows, irrelevant/scope-specific questions, plus deadline,
authorization, stale/refresh and snapshot lifecycle tests. Mock embedding vectors
are deliberately deterministic, not evidence that NYRA understands real business
terms. Provider/authentication changes, access-first artifact reads, overlapping
refreshes, generation limits/failures, cancellation, hot profile changes,
source-linked OKF conformance and YAML/path attacks are separately tested.

Initial recorded fixture latency was about 183-195 ms for four repositories with
a simulated 40 ms access delay each; bounded parallel lexical retrieval was about
89-97 ms. Exact-symbol and cross-repository expected-source recall stayed 1.0.
The lexical-only business paraphrase had recall 0; opt-in **mock-vector** hybrid
retrieval reached recall 1.0, with at most one query embedding. A scaled 200 ms
deadline previously discarded completed evidence; the new path retains it and
marks partial coverage. These are code-stage synthetic measurements, not
end-to-end production speed or real-model accuracy claims. Re-run the tests on
the same fixtures when changing routing/rank defaults. Production NYRA/GitHub
quality, latency and cost require an authenticated bounded rollout.

A subsequent same-process comparison used the median of three runs per case and
included prepared OKF bundles (the default retrieval configuration):

| Fixture | Frozen baseline | Parallel lexical | Parallel + OKF | OKF + mock vectors |
|---|---:|---:|---:|---:|
| Exact symbol | 186.5 ms | 93.0 ms | 124.4 ms | 114.1 ms |
| Business paraphrase | 188.7 ms | 93.6 ms | 123.2 ms | 126.1 ms |
| Cross-repository workflow | 187.8 ms | 94.3 ms | 120.2 ms | 123.3 ms |

Expected-source recall for the exact/workflow cases is 1.0 in all four modes;
only the mock-vector mode resolves the deliberately lexically disjoint business
paraphrase. Irrelevant/scope-restricted/no-access cases return no source. Refresh
tests require the new pinned commit, and timeout tests require retained partial
evidence. Mock vectors have no real provider/network latency, so their timings
must not be treated as production semantic-search latency. Benchmark JSON can be
written with `POOLSENSE_EVAL_OUTPUT` when running the individual evaluation test.

Run the relevant `CodeRAG*`, `CodeRetrieval*`, `CodeSemantic*` and `Okf*` API tests,
and `npx playwright test code-search-coverage.spec` from `PoolSense.UI` against
the local dev UI. The UI tests mock authentication/API responses and do not write
to production databases.

## Index Lifecycle

- Build prerequisites: install Node.js on the indexing host and run
  `npm ci --prefix PoolSense.CodeRAG\Indexing\TypeScript --ignore-scripts` from the
  repository root before building/publishing. The pinned TypeScript 5.9.3 compiler
  and trusted parser are copied into build/publish output. Indexed repositories'
  packages, plugins, configs and scripts are not executed. Missing Node/compiler
  fails indexing explicitly and retains the previous usable snapshot.
- Preparation version 6 adds content-hash parse caches, prepared SQLite writes,
  foreign-key/declaration lookup indexes and TypeScript relationships. Unchanged
  files reuse parsed declarations; changed/deleted files trigger global relinking
  against the current declarations. Graph and FTS rows are still rewritten into
  an immutable snapshot: this is incremental parsing, not an in-place row delta.
  Older C# snapshots remain searchable while background upgrades run. TypeScript
  cache files live alongside the database; direct library callers placing the
  database inside the source tree get an explicit temporary-cache warning instead.
- New/updated repository registrations are durable queued work in SQL. A single hosted worker checks for queued/due work every ten seconds; all actual branch checks are scheduled hourly by default.
- The worker checks the configured branch SHA. An unchanged commit with a valid index is reused without downloading or parsing. A changed commit downloads the exact GitHub commit archive and rebuilds that repository's index. Missing/incompatible indexes are rebuilt as recovery even if the commit is unchanged.
- GitHub archive APIs avoid ambient Git credentials, repository hooks and checkout scripts; Git is not required for this integration. Submodules and Git LFS objects are not expanded. Only github.com repositories are supported in this version.
- Archives and extracted bytes are bounded; path traversal is rejected and symlinks are skipped. C# and TypeScript/JavaScript declarations and syntax-scoped graph relations are retained; Python, HTML/CSS/SCSS, Markdown and SQL remain searchable source chunks. Files over 2 MB and build/dependency directories are skipped. TypeScript imports and resolvable calls carry `typescript-syntax` provenance; unresolved calls are not invented.
- Each rebuild creates a separate SQLite snapshot. Publication uses a revision check; removed or reconfigured repositories cannot publish obsolete results. Queries read a coherent snapshot and cite immutable GitHub commit/file/line links.
- Failed refreshes retain the last successful same-configuration index. Authorized users can still receive that evidence with a stale status. Rate-limit reset/Retry-After times postpone retries when later than the normal interval. Builds have a 15-minute deadline.
- Queued/interrupted work is recovered after restart. The worker cleans abandoned generations and removed registrations' files. Storage must have enough capacity for current snapshots plus a staged build; set operating-system quotas and monitor disk usage.
- The configured branch stays pinned by name; changing a repository's default branch does not silently change an existing registration. Edit its branch and click Update Application to switch branches. Updating an unchanged repository checks for new commits rather than unconditionally rebuilding.
- Hourly checking is not an exact freshness guarantee: updates become searchable after the next successful check and build. Keep IIS/app hosting active (account for idle shutdown/recycling); the worker catches up on restart.

## Query Planning and Performance

- Short dependent follow-ups include the previous user question before a single
  retrieval pass. Assistant hypotheses are not promoted into search evidence.
- Queries are classified as `symbol`, `concept` or `workflow`. Exact identifiers
  use indexed symbol-name lookup and skip query embeddings when an exact match is
  found. Symbol queries cap candidates at 12 per repository, evidence at 6 spans
  and context at 12,000 characters (or lower configured limits). Concept/workflow
  queries retain the configured budgets and optional semantic fallback.
- With `RepositoryRoutingEnabled` enabled, authorized repository/knowledge scores
  determine a stable shortlist, independent of request completion order. Workflow
  questions and insufficient term coverage broaden the search. Routing defaults
  off; it never bypasses authorization or final snapshot/configuration checks.
- Workflow/graph evidence includes bounded indexed call/import/inheritance
  relationships with source/target paths, lines and resolution strength. These
  count against the context budget. They are syntax-derived evidence, not proof
  of runtime behavior. Safe Mermaid generation still validates source references.
- Graph expansion looks up incoming/outgoing edges separately through the existing
  source/target indexes, avoiding repository-wide edge scans for each hop. This
  query-only fix also applies to existing snapshots; reindexing is not required.
  Authorization coverage tracks completed access checks separately from source
  search, so a later graph timeout does not imply access checks were unfinished.
  Deadline warnings log the effective limit and stage timings without query/source
  text; inspect these before increasing `SearchDeadlineSeconds` further.
- Semantic indexing batches up to `Semantic.EmbeddingBatchSize` inputs per provider
  request (default 8, range 1-16), within the existing total/new-vector/manifest
  budgets. Completed batches checkpoint for reuse after failures; failed batches
  remain explicit. Providers without batch support retain serial behavior.
- Repository answers expose `timingsMs` for scope, retrieval, generation and
  history persistence. `codeSearchCoverage` includes `queryKind` and retrieval
  stage `timingsMs`; preparation manifests/logs include download/index/knowledge/
  semantic durations and core parsed/reused file counts. The `PoolSense.CodeRAG`
  meter emits `coderag.stage.duration` in milliseconds. Parallel stage durations
  are summed work, not additive wall-clock latency; compare the `total` separately.

The synthetic 150-file/3,000-method fixture measured full indexing at 1.99s before
and 1.56s after, and a one-file refresh at 6.48s before and 1.93s after (149 files
reused). Exact-symbol lexical median was 106ms before and 93ms after; cross-repo
latency was essentially unchanged (98ms/100ms). Exact/cross-repository recall
remained 1, irrelevant/out-of-scope results stayed empty, and mock semantic search
recovered the business-paraphrase fixture. These are noisy local synthetic
measurements with mocked network/embedding providers, not production guarantees;
measure real stage latency and answer quality on representative repositories.

## Storage and Operations

SQL stores repository configuration/status. The legacy `github_connections` table may remain in an existing database for compatibility, but current code does not read or write it; no rows or schema are deleted. The configured PAT is held in host configuration and protected in memory with Data Protection before use by the GitHub client. Source and index snapshots live under `StoragePath`; Data Protection keys are in its `keys` directory, encrypted using Windows DPAPI. Do not publish this directory as static content. There is no plaintext-token fallback; non-Windows hosting needs a separately designed protected-key configuration and is rejected when the feature is enabled.

The portable default `artifacts/code-knowledge` is relative to the API **content root**,
not the shell's working directory. For example, an API content root of
`C:\CodeRAG\PoolSense\PoolSense.Api` resolves it to
`C:\CodeRAG\PoolSense\PoolSense.Api\artifacts\code-knowledge`; no D drive is required.
Production may explicitly configure an existing persistent writable volume, with
permissions for the API's Windows identity. Both index snapshots and protected
keys use the same resolved root. Startup creates/checks the directory with a temporary
write probe and logs the resolved location; an unavailable drive or denied write
access stops startup with an actionable error instead of silently using another root.

`CodeRAG__StoragePath` overrides JSON settings through the environment; development
user secrets and environment-specific appsettings may also override the base setting.
Check those settings before changing paths. Preserve existing generations and keys:
changing the root does not migrate them, and pointing at an empty root can require
background indexing or GitHub reconnection. Do not delete or rebuild a healthy
existing root just to correct a documentation example.

Back up SQL and protected key material together. DPAPI keys are tied to the Windows identity/machine; moving hosts requires an appropriate key migration. Source/index caches can be rebuilt. A single shared local directory is not a supported multi-instance deployment.

Application HTTP client logging is disabled for GitHub API/download clients. Never capture authorization headers, the configured PAT, repository source, or answer content in IIS/reverse-proxy/APM logs.

New repository mutations require the `X-PoolSense-CSRF: 1` header as well as existing PoolSense authentication. The UI supplies it. There is no new global project RBAC model: existing project-configuration users retain management access, with GitHub operations performed through the server-managed PAT.

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

The browser tests mock API responses; they verify desktop/mobile repository operations and the read-only managed-access status without using real credentials. Unit tests verify commit reuse/change, graph retrieval and deleted files, authenticated interactive retrieval, explicit PAT-only polling retrieval, project scoping, snapshot publication races, archive traversal and feedback permits.

Before production enablement, test against a dedicated non-production SQL database and GitHub repository: configure the intended PAT and organization SSO access, add/index/query, verify an unchanged refresh, push changed/deleted files, refresh and verify commit-pinned citations, revoke/rotate the PAT, then restart the host and check recovery. GitHub token permissions, organization policies, SQL schema compatibility and production hosting cannot be certified by mocked tests alone. Do not run NYRA live smoke tests without an intended test environment.