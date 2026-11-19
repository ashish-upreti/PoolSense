# How PoolSense CodeRAG Works

A beginner-friendly technical guide. No previous knowledge of RAG is required.

## 1. The idea in one sentence

**CodeRAG finds relevant parts of your application's source code, gives those parts to an AI model, and asks it to explain the answer with links back to the code.**

Imagine asking a new teammate:

> "Where does this application decide whether to send an email?"

A useful teammate first finds the relevant files, reads them, and then explains
what they found. CodeRAG follows a similar pattern.

It does not send the entire repository with every question, retrain the AI on
every code change, or run the application to discover its behavior.

## 2. First, what does RAG mean?

RAG stands for **Retrieval-Augmented Generation**. The name describes three simple steps:

| Term | Plain meaning | In our example |
|---|---|---|
| Retrieval | Find useful information. | Find the code that checks whether email is enabled. |
| Augmented | Add that information to the AI's instructions. | Include the relevant code excerpts with the question. |
| Generation | Ask the AI to write an answer. | Explain the checks and cite the source. |

Think of it as an **open-book exam**:

- The AI model is the person answering.
- Your repository is the book.
- The search system finds the useful pages.
- The AI reads those pages and writes an explanation.
- Source links let you check the explanation.

Without retrieval, an AI might describe how email systems usually work.
With CodeRAG, it can explain what the **retrieved version of your code** does.
It can still make mistakes; references make the explanation inspectable, not automatically correct.

## 3. Where CodeRAG fits in PoolSense

PoolSense can use different kinds of evidence:

| Source | What it helps answer |
|---|---|
| Pool ticket history | "Have we seen a similar incident before?" |
| Wiki documents | "What do the documented process and instructions say?" |
| Repository code | "Where and how is this behavior implemented?" |

CodeRAG provides the repository-code part.

There are two useful starting points:

1. **Repository chat:** select one or more applications and ask about their code directly.
2. **Incident investigation:** use relevant code alongside the other selected evidence sources.

They share the same repository preparation and code-retrieval infrastructure.
Not every question searches all three sources; source selection depends on the
question and explicit instructions. For example, "look in the repo" selects code.

## 4. The big picture: two separate jobs

CodeRAG separates slow preparation from interactive questions.

```text
A. BACKGROUND PREPARATION

Application's registered GitHub repository + branch
                        |
                        v
Check access and identify a specific commit
                        |
                        v
Download source and build a searchable code map
                        |
                        v
Prepare source-linked notes and optional meaning-based index
                        |
                        v
Publish a ready snapshot

B. ANSWERING A QUESTION

Question + selected applications
                        |
                        v
Check current repository access
                        |
                        v
Search ready snapshots in several ways
                        |
                        v
Rank matches and select useful code excerpts
                        |
                        v
Give question + excerpts + source links to the AI
                        |
                        v
Return an explanation with citations and coverage information
```

**A question does not trigger a repository download or index rebuild.**
It searches what background preparation has already made available. Meaning-based
search may still need a model call to represent the question as numbers.

## 5. Job A: prepare the repository

### Step 1: register the code source

An administrator configures the server's GitHub personal access token, or **PAT**.
An application is then associated with one or more GitHub repositories and branches.
Saving new or changed enabled repository settings queues background indexing.

The PAT is a GitHub access credential. It is separate from the credentials used
to call the AI/embedding provider, and it is not sent to the browser.

### Step 2: take a versioned snapshot

The background worker checks the configured branch and prepares source from a
specific **commit**.

A commit is a saved version of the repository. A snapshot is the local prepared
copy of that version. Keeping the commit matters: a citation should point to the
code that was actually searched, not to a branch that may change tomorrow.

### Step 3: build an index

An **index** is a lookup structure, like the index at the back of a textbook.
It makes finding information faster than reading every file for every question.

The indexer records:

- File paths and line numbers.
- Named code elements, such as classes and methods. These are called **symbols**.
- Manageable pieces of source text, called **chunks**.
- Relationships it can identify, such as one method calling another.

For C#, the implementation uses Roslyn, Microsoft's C# parsing tools.
TypeScript/JavaScript parsing adds structured information for those languages.
Other supported text files can contribute searchable chunks.
Generated/dependency folders such as `bin`, `obj`, and `node_modules` are excluded.

Chunks let the system retrieve a useful section rather than a whole large file.
Some neighboring lines overlap so that a boundary does not immediately lose
surrounding context.

The relationship map is built from source analysis. It is not a recording of a
running application, and it cannot prove every runtime call or dependency.

### Step 4: prepare extra search aids

Two additional aids can help find relevant source:

**Source-linked project notes (OKF).** OKF means Open Knowledge Format.
Think of these notes as a small guidebook: terminology, partial workflows,
configuration concepts, and other supported relationships, linked to source.
They help locate code; their generated descriptions are not treated as proof.
Optional AI-written enrichment is separate and is off by default.

**Embeddings for meaning-based search.** An embedding is a list of numbers
representing a piece of text. Text about similar ideas can have similar number
patterns, even when it uses different words.

For example:

```text
Question:    "Where can outgoing messages be switched off?"
Code concept: "email enabled flag"

Different words, potentially related meaning.
```

When enabled, the embedding provider creates vectors for selected source excerpts
in background batches. The system reuses compatible existing vectors instead of
recreating all of them each time.

**Not every source chunk necessarily has an embedding.** Semantic coverage is
bounded and grows in batches. Keyword and relationship search remain useful
even without complete semantic coverage.

### Step 5: publish the prepared snapshot

A new generation is prepared separately, then made available as the ready version.
Existing searches can finish reading the old generation while a new one is built.

If a source build fails, the previous complete generation is retained.
If optional preparation fails, lexical code search can remain usable with an
explicit warning.

The normal branch-check interval is configurable and defaults to 60 minutes.
Changes pushed to GitHub are therefore **not instantly searchable**.

## 6. Job B: answer a question

### Step 1: decide the scope and check access

The selected applications determine which registered, enabled repositories are eligible.
PoolSense checks the server-managed PAT's current access before using repository
snapshots; a cached index does not bypass that check.

Important: this is **shared server access**, not each user's individual GitHub
permissions. A signed-in PoolSense user who can query an application may receive
its registered code evidence when the server PAT can read those repositories.
Deployment access rules must account for that.

### Step 2: search in complementary ways

| Search method | Simple explanation | Example |
|---|---|---|
| Keyword and symbol search | Look for words, paths, and exact code names. | Find a named method or an email-related setting. |
| Relationship search | Include connected code where indexed relationships support it. | Find a caller and the method it invokes. |
| Project-note search | Use the small guidebook to locate relevant source. | Follow an email-configuration note back to its code. |
| Semantic search, when available | Find related meaning using embeddings. | Connect "outgoing messages" with email-related code. |

This combination is often called **hybrid retrieval**.
It is not "just put all code into a vector database."

For semantic search, the question can be converted into a vector and compared
with cached source vectors. The service budgets at most one code-query embedding
per request-scoped instance, not one per repository.

### Step 3: choose the best evidence

The system combines candidates across repositories, ranks them, removes duplicate
or overlapping excerpts, and fits the most useful source into a size limit.

This matters because an AI has a limited amount of text it can read in one request.
The answer model receives **selected evidence**, not everything in every repository.

Repository chat offers Quick and Deep search. Deep uses larger search and evidence
budgets and bypasses repository shortlisting; it can take longer. It still has
limits and does not guarantee that every relevant line is found.

### Step 4: ask the AI to explain the evidence

In simplified form, the answer request looks like this:

```text
Instructions:
  Explain using the supplied repository evidence.
  Cite the supplied source links.
  Say what is missing instead of guessing.

Question:
  Where does the application decide whether to send an email?

Evidence:
  Selected source excerpts
  File names and line ranges
  Indexed commit and exact source URLs
  Supported indexed relationships
```

The language model's job is to explain the supplied material in useful language.
The search system's job is to supply the right material. Good explanations depend
on both jobs working well.

### Step 5: return the explanation and its limits

The response includes source references with repository, file, lines, and indexed
commit. Coverage information distinguishes cases such as:

- The index is not ready.
- No matching accessible source was found.
- Only part of the search completed before its deadline.
- A previous snapshot is being used while refresh is pending.
- Current access could not be verified, so evidence was withheld.

**"No matches found" is not the same as "the application does not implement this."**

## 7. A complete example

The following code and names are **invented for teaching**. They are not claims
about the actual PoolSense email implementation.

Suppose a repository contains:

```csharp
public async Task NotifyAsync(string message)
{
    if (!settings.EmailEnabled)
        return;

    await emailSender.SendAsync(message);
}
```

**User asks:** "Why might this notification not send an email?"

1. **Earlier preparation:** the indexer recorded this method, its file and lines,
   and any relationships it could resolve.
2. **Scope:** the question is limited to the selected application's repositories.
3. **Access:** PoolSense verifies that its managed GitHub credential can still read them.
4. **Retrieval:** terms such as "notification", "send", and "email", plus any
   available semantic matches, help find this method and related evidence.
5. **Evidence selection:** the method is selected with a commit-pinned source link.
6. **Generation:** the AI explains the condition using the retrieved code.

A useful answer would say:

> One possible reason is that email is disabled. This method returns without
> calling the sender when `EmailEnabled` is false. Check the effective setting
> in the running environment. The retrieved code alone does not establish its
> current value.

In the real response, that code claim should have a clickable source citation.

Notice the difference:

| Supported by this excerpt | Not established by this excerpt |
|---|---|
| There is an early return when the setting is false. | The setting is currently false in production. |
| The sender is called after that check. | The email server accepted or delivered a message. |
| This is behavior in the indexed source version. | This exact commit is deployed. |

This is the central rule: **explain what the evidence establishes; label what still needs checking.**

## 8. Where the information lives

| Information | Main location |
|---|---|
| Original source | GitHub repository |
| Application/repository registrations and published-generation metadata | SQL Server |
| Prepared source snapshots | API server's configured CodeRAG storage directory |
| Symbols, searchable text, and code relationships | Local SQLite index |
| Source-linked project notes | OKF files beside the prepared snapshot |
| Optional semantic vectors | Local semantic artifact beside the prepared snapshot |
| Repository conversations | SQL Server-backed chat history |

SQLite is a local database file; SQL Server is used for application-level
persistence. The semantic index does not require adding a separate hosted vector
database for this implementation.

Source is not necessarily kept entirely on the local server: selected code excerpts
are supplied to the configured answer model. With semantic indexing enabled,
redacted selected excerpts and semantic queries also go to the configured embedding
provider. Redaction reduces some secret exposure; it is not a guarantee that all
sensitive information is removed. Use approved providers and repository scope.

## 9. What CodeRAG does not do

- **It does not train a new AI model on your repository.** Retrieval adds context to requests.
- **It does not run code or validate a fix.** Build, test, logs, and runtime checks are still needed.
- **It does not guarantee complete coverage.** Parsing, selection, semantic coverage, and time limits apply.
- **It does not prove a root cause.** A code path may suggest a hypothesis to investigate.
- **It does not guarantee deployed-version accuracy.** Compare the indexed commit with the deployed version.
- **It does not make old chat answers new evidence.** Follow-up questions retrieve current accessible evidence again.

## 10. Ask questions that are easier to answer well

| Less specific | More useful |
|---|---|
| "Explain everything." | "Show the path from the notification request to the email sender." |
| "Why is it broken?" | "Where can email sending be skipped? Include the configuration checks." |
| "How does this work?" | "Explain this named method and its callers, with source references." |
| "It failed again." | "For this error message, find the code that throws it and explain its conditions." |

Select the relevant application, include exact identifiers or error text when
available, inspect coverage warnings, and open the cited source before acting.

## 11. Developer map

These are the main implementation entry points, not required reading for beginners:

| Component | Responsibility |
|---|---|
| [CodeIndexWorker.cs](PoolSense.Api/Services/CodeRAG/CodeIndexWorker.cs) | Background preparation, refresh coordination, and generation publication. |
| [RepositoryIndexer.cs](PoolSense.CodeRAG/Indexing/RepositoryIndexer.cs) | Parse supported source, create symbols/chunks, and build relationships. |
| [GraphStore.cs](PoolSense.CodeRAG/Storage/GraphStore.cs) | Store the local SQLite text and relationship index. |
| [Retriever.cs](PoolSense.CodeRAG/Retrieval/Retriever.cs) | Search source text/symbols and expand related evidence. |
| [OkfBundle.cs](PoolSense.Api/Services/CodeRAG/OkfBundle.cs) | Prepare and search source-linked project knowledge. |
| [CodeSemanticIndex.cs](PoolSense.Api/Services/CodeRAG/CodeSemanticIndex.cs) | Prepare and search optional cached semantic vectors. |
| [CodeKnowledgeService.cs](PoolSense.Api/Services/CodeRAG/CodeKnowledgeService.cs) | Resolve scope, check access, coordinate retrieval, and report coverage. |
| [CodeCandidateSelection.cs](PoolSense.Api/Services/CodeRAG/CodeCandidateSelection.cs) | Rank and select bounded, deduplicated source evidence. |
| [CodeChatService.cs](PoolSense.Api/Services/CodeRAG/CodeChatService.cs) | Assemble repository-answer prompts and save conversations. |

For deployment, configuration, limits, and troubleshooting, see
[GitHub Code Knowledge](CodeKnowledge.md). For the product-focus overview, see
[CodeRAG](CodeRAG.md).

**Remember:** find the relevant code, give it to the AI, explain it with references,
and verify the explanation against the indexed version and the running system.
