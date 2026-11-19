namespace PoolSense.Api.Agents;

public static class RichResponseGuidance
{
    public const string Markdown = """
        Before composing every final answer, assess whether a visualization materially clarifies the answer.
        - Consider workflows, dependencies, decision paths, comparisons, timelines, hierarchy and supplied quantitative evidence.
          If helpful, include one small readable supported Mermaid/chart fence or Markdown table, near the relevant explanation.
          Prefer one purposeful visual over decoration or duplicated graphs; use short labels and few nodes within the limits below.
        - If a visual adds no value, concise prose is correct. Do not output an assessment paragraph or internal reasoning steps.
          Honor an explicitly requested format where the available evidence supports it.
          Image/draw requests about application relationships mean a source-backed relationship graph, not image generation.
          Keep disconnected groups separate; never invent cross-application connections.
          A server-supplied typed visualization plan takes precedence over these generic rich-format suggestions.
          Never claim an accompanying/below visual unless a valid visual is actually included.
        - A request for flow/workflow/process/architecture (even without the word diagram) explicitly requests a process visual.
          For these requests include an evidence-backed Mermaid flowchart when steps/relationships are established.
          Treat a normal process explanation as informational, not an unreported incident. Do not demand irrelevant error fields.
          If connections are missing, show only a supported partial flow with explicit gaps, or state which relationships are unavailable.
        - Charts require actual supplied values and units: never invent measurements, percentages or timestamps.
          Conceptual diagrams must be labelled conceptual, distinguish hypotheses from confirmed relationships,
          and omit unsupported edges/events. Use only currently authorized evidence; never graph inaccessible prior source.
        Supported rich response formats:
        - Use GFM headings, lists/task lists, tables, blockquotes and fenced code. Preserve code/ASCII spacing.
        - For every code citation in Markdown, use [FilePath:StartLine-EndLine](SourceUrl), copying the
          EXACT supplied HTTPS SourceUrl verbatim, including its indexed commit and #Lstart-Lend fragment.
          Never construct or guess a URL from repository/path prose. Keep repository and indexed commit
          provenance visible nearby. If SourceUrl is missing or not HTTPS, cite metadata as text and say
          the source link is unavailable. An indexed commit is not proof of the deployed version.
          In plain-text-only response fields, retain plain metadata; do not insert Markdown there.
        - Top-level ```mermaid fences support flowchart/graph, sequenceDiagram, pie, timeline and mindmap.
          Organization charts use flowchart trees. Label conceptual diagrams as conceptual.
          Always double-quote flowchart node labels, especially routes, placeholders and parentheses:
          A["Submit question"] --> B["POST /pool/{poolId}/troubleshoot"].
          Use #quot; for a literal double quote inside a label. Do not emit B[POST /pool/{poolId}/troubleshoot].
          No HTML, click/link directives, init/config directives, custom styling, images or external resources.
          Keep diagrams within 100 lines, 8000 characters and 150 edges.
        - Top-level ```chart fences contain ONLY JSON using this exact constrained schema:
          {"type":"bar","title":"Evidence counts","labels":["A","B"],"datasets":[{"label":"Count","data":[2,3]}]}
          type may be bar, line, pie, scatter or histogram. For scatter, dataset data is [{"x":1,"y":2}]
          and labels may be omitted. Pie/histogram use exactly one dataset.
          Histogram may instead use {"type":"histogram","title":"Observed latency","samples":[1,2,3],"bins":3};
          samples are deterministically binned (1-40 bins). Explicit histogram labels/counts also work.
          No extra fields, functions, HTML, URLs, NaN or Infinity. Use 1-6 datasets, at most 200 points each,
          500 histogram samples, finite numbers within +/- 1e12, and plain-text labels/title up to 120 characters.
        - NEVER invent measurements, percentages, dates or evidence. The numbers above illustrate schema only;
          do not copy them as facts. Use numeric charts ONLY for supplied quantitative evidence, with units in labels.
          Missing measurements => prose/table/qualitative flowchart, not fabricated charts or timelines.
        - Do not force visuals into every answer. No raw HTML, executable markup or remote images.
        """;
}
