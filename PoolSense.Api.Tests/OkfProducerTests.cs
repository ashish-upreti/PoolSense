using System.Text;
using System.Text.RegularExpressions;
using CodeRAG.Core.Storage;
using PoolSense.Api.Services;
using PoolSense.Api.Services.CodeRAG;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class OkfProducerTests : IDisposable
{
    private const string Commit = "abcdef0123456789abcdef0123456789abcdef0123";
    private readonly string root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "okf-producer-tests", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string Snapshot => Path.Combine(root, "snapshot");
    private string Database => Path.Combine(root, "source.db");
    private readonly CodeRepository repository = new() { FullName = "owner/project" };

    public OkfProducerTests()
    {
        Directory.CreateDirectory(Source);
        using var db = GraphStore.Open(Database);
        GraphStore.Execute(db, "INSERT INTO repositories VALUES(@p0,'2026-01-01',1,1,0,@p1,@p2)",
            GraphStore.RootKey(Source), GraphStore.Version, GraphStore.Parser);
    }

    [Fact]
    public async Task DisabledLlmProducesSourcedTerminologyWorkflowsRulesSqlAndContracts()
    {
        Populate();
        var llm = new NeverLlm();
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot, llm: llm);
        Assert.Equal("prepared", result.Status);
        Assert.Equal(0, llm.Calls);
        var read = OkfLocalBundle.ReadExisting(result.BundlePath!, expectedCommit: Commit);
        Assert.Equal("ready", read.Status);
        var docs = read.Documents.Where(d => d.Kind == OkfDocumentKind.Concept).ToList();
        Assert.InRange(docs.Count, 5, 14);
        var glossary = docs.Single(d => d.Type == "Glossary");
        var aliases = Assert.IsAssignableFrom<IReadOnlyList<object?>>(glossary.Metadata["aliases"]);
        Assert.Contains("HTTP Rate DTO", aliases);
        Assert.DoesNotContain("Hypertext", string.Join(" ", aliases));
        var calculation = docs.Single(d => d.Id == "calculations");
        Assert.Contains("`amount`", calculation.Body);
        Assert.Contains("`rate`", calculation.Body);
        Assert.Contains("amount * rate", calculation.Body);
        var contract = docs.Single(d => d.Id == "contracts");
        Assert.Contains("Save", contract.Body);
        Assert.Contains("IMPLEMENTS", contract.Body);
        Assert.Contains("syntax", contract.Body);
        var config = docs.Single(d => d.Id == "configuration");
        Assert.Contains("Rates:Default", config.Body);
        Assert.DoesNotContain("Password123", string.Join("\n", docs.Select(d => d.Body)));
        Assert.All(docs, d =>
        {
            Assert.Equal("draft", d.Status);
            Assert.Equal(OkfTrust.Unverified, d.Trust);
            Assert.Equal(OkfBundle.ProducerVersion, d.Metadata["poolsense_generator"]);
            Assert.NotEmpty(d.Metadata["description"]!.ToString()!);
            Assert.False(d.Metadata.ContainsKey("verified"));
            var ids = d.Sources.Select(s => s.Id).ToHashSet();
            Assert.NotEmpty(ids);
            foreach (Match footnote in Regex.Matches(d.Body, @"\[\^([^\]]+)\]"))
                Assert.Contains(footnote.Groups[1].Value, ids);
            Assert.All(d.Sources, s =>
            {
                Assert.StartsWith($"https://github.com/owner/project/blob/{Commit}/", s.Resource);
                Assert.Matches(@"#L\d+-L\d+$", s.Resource);
                Assert.NotNull(s.Metadata["poolsense_symbol"]);
                Assert.NotNull(s.Metadata["poolsense_kind"]);
                Assert.Matches("^[a-f0-9]{64}$", s.Metadata["poolsense_source_sha256"]!.ToString()!);
            });
        });
        var index = read.Documents.Single(d => d.Kind == OkfDocumentKind.Index);
        Assert.Equal("0.2", index.Metadata["okf_version"]);
        Assert.Contains("Calculation:", index.Body);
        var log = read.Documents.Single(d => d.Kind == OkfDocumentKind.Log);
        Assert.Contains("Content identity:", log.Body);
        Assert.Contains("## " + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), log.Body);
    }

    [Fact]
    public async Task WorkflowUsesOnlySameRepositoryIndexedGraphAndPreservesResolution()
    {
        Populate();
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot);
        Assert.Equal("prepared", result.Status);
        var workflows = OkfLocalBundle.ReadExisting(result.BundlePath!).Documents.Where(d => d.Type == "Workflow").ToList();
        Assert.NotEmpty(workflows);
        var body = string.Join("\n", workflows.Select(d => d.Body));
        Assert.Contains("HandleRequest", body);
        Assert.Contains("Calculate", body);
        Assert.Contains("scoped-syntax", body);
        Assert.Contains("not proof of runtime dispatch", body);
        Assert.DoesNotContain("DynamicSave", body);
        Assert.DoesNotContain("ForeignSave", body);
        Assert.DoesNotContain("NOTIFIES", body);
        Assert.All(workflows, d => Assert.InRange(d.Sources.Count, 2, 8));
    }

    [Fact]
    public async Task SqlLineageKeepsExplicitJoinsAndDoesNotInventSameNameRelations()
    {
        Populate();
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot);
        var sql = OkfLocalBundle.ReadExisting(result.BundlePath!).Documents.Single(d => d.TagsForTest().Contains("sql"));
        Assert.Contains("dbo.Rates", sql.Body);
        Assert.Contains("dbo.Payments", sql.Body);
        Assert.Contains("r.Id = p.RateId", sql.Body);
        Assert.Contains("SUM(p.Amount * r.Value)", sql.Body);
        Assert.Contains("dbo.Totals", sql.Body);
        Assert.Contains("No cross-file or same-name lineage is inferred", sql.Body);
        Assert.DoesNotContain("dbo.Unrelated", sql.Body);
        Assert.DoesNotContain("JOIN dbo.Fake", sql.Body);
        Assert.All(sql.Sources, s => Assert.Contains("/database/rates.sql#", s.Resource));
    }

    [Fact]
    public async Task CalculationRulesKeepLiteralGuardsBranchesAndSqlAliasesAndPredicatesWithExactSources()
    {
        Add("guard", "Rules/Calculate.cs", "Member", "Calculate", """
            public decimal Calculate(decimal amount, decimal rate)
            {
                if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
                if (rate == 0) return 0;
                else return amount * rate;
            }
            """);
        Add("switch", "Rules/Classify.cs", "Member", "Classify", """
            public decimal Classify(decimal amount) => amount switch
            {
                < 0 => 0,
                var value when value > 100 => value * 0.9m,
                _ => amount
            };
            """);
        Add("sql", "database/guarded.sql", "SourceChunk", "guarded.sql",
            "-- " + new string('x', 1600) + "\n" + """
            SELECT SUM(CASE WHEN p.Active = 1 THEN p.Amount ELSE 0 END) AS NetAmount
            FROM dbo.Payments p
            JOIN dbo.Rates r ON r.Id = p.RateId AND r.Region = 'EU'
            WHERE p.Status = 'posted'
            HAVING SUM(p.Amount) > 10;
            """);
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot);
        Assert.Equal("prepared", result.Status);
        var docs = OkfLocalBundle.ReadExisting(result.BundlePath!).Documents;
        var rules = docs.Single(d => d.Id == "calculations");
        Assert.Contains("`amount`", rules.Body);
        Assert.Contains("`rate`", rules.Body);
        Assert.Contains("if (amount < 0)", rules.Body);
        Assert.Contains("throw new ArgumentOutOfRangeException(nameof(amount))", rules.Body);
        Assert.Contains("if (rate == 0) return 0", rules.Body);
        Assert.Contains("else return amount * rate", rules.Body);
        Assert.Contains("var value when value > 100 => value * 0.9m", rules.Body);
        Assert.Contains("no inferred execution dependency", rules.Body);
        var sql = docs.Single(d => d.TagsForTest().Contains("sql"));
        Assert.Contains("AS NetAmount", sql.Body);
        Assert.Contains("JOIN dbo.Rates r ON r.Id = p.RateId AND r.Region = 'EU'", sql.Body);
        Assert.Contains("WHERE p.Status = 'posted'", sql.Body);
        Assert.Contains("HAVING SUM(p.Amount) > 10", sql.Body);
        Assert.Contains("WHEN p.Active = 1 THEN p.Amount ELSE 0 END", sql.Body);
        Assert.Contains("may be partial", sql.Body);
        foreach (var document in new[] { rules, sql })
        {
            Assert.Equal(OkfTrust.Unverified, document.Trust);
            foreach (var source in document.Sources)
            {
                Assert.Contains($"[^{source.Id}]", document.Body.Split("[^" + source.Id + "]:")[0]);
                Assert.StartsWith($"https://github.com/owner/project/blob/{Commit}/", source.Resource);
            }
        }
    }

    [Fact]
    public async Task DocumentAndSymbolBoundsAreExplicitAndPublicationIsImmutable()
    {
        Populate();
        var options = new OkfOptions { MaxDocuments = 2, MaxSymbols = 4 };
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot, options);
        Assert.Equal("prepared", result.Status);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Limitations, e => e.Contains("Partial source coverage"));
        Assert.Contains(result.Limitations, e => e.Contains("Partial concept coverage"));
        var files = Directory.GetFiles(result.BundlePath!, "*.md");
        Assert.Equal(2, files.Length);
        var read = OkfLocalBundle.ReadExisting(result.BundlePath!, options);
        Assert.All(read.Documents, d =>
        {
            Assert.True(Encoding.UTF8.GetByteCount(d.RawMarkdown) <= options.MaxDocumentBytes);
            Assert.InRange(d.Sources.Count, 0, options.MaxSymbols);
        });
        var bytes = files.ToDictionary(f => f, File.ReadAllText);
        using (var db = GraphStore.Open(Database))
            GraphStore.Execute(db, "UPDATE symbols SET source='changed' WHERE id='handler'");
        var existing = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot, options);
        Assert.Equal("existing", existing.Status);
        foreach (var file in files) Assert.Equal(bytes[file], File.ReadAllText(file));
        Assert.Empty(Directory.GetDirectories(Snapshot, ".okf-*"));
    }

    [Fact]
    public async Task SmallByteBudgetNeverPublishesOversizedOrPartialBundle()
    {
        Add("x", "Models/Widget.cs", "Class", "Widget", "public class Widget {}");
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot,
            new() { MaxDocumentBytes = 256 });
        Assert.Equal("limited", result.Status);
        Assert.Contains(result.Errors, e => e.Contains("byte limit"));
        Assert.False(Directory.Exists(Path.Combine(Snapshot, "okf")));
        Assert.Empty(Directory.GetDirectories(Snapshot, ".okf-*"));
    }

    [Fact]
    public async Task TotalBundleByteBudgetIncludesConceptsIndexAndLog()
    {
        Add("x", "Models/Widget.cs", "Class", "Widget", "public class Widget {}");
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot,
            new() { MaxBundleBytes = 1024 });
        Assert.Equal("limited", result.Status);
        Assert.Contains(result.Errors, e => e.Contains("total bundle byte limit"));
        Assert.False(Directory.Exists(Path.Combine(Snapshot, "okf")));
        Assert.Empty(Directory.GetDirectories(Snapshot, ".okf-*"));
    }

    [Fact]
    public async Task LongIdentifiersFitByCompactingMetadataAndReducingWholeGroundedEntries()
    {
        var names = Enumerable.Range(0, 12).Select(i => "Rate" + new string('x', 800) + i).ToList();
        for (var i = 0; i < names.Count; i++)
            Add("long-" + i, $"Models/Rate{i}.cs", "Class", names[i], $"public class {names[i]} {{}}");
        var options = new OkfOptions { MaxDocumentBytes = 4096 };
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot, options);
        Assert.True(result.Status == "prepared", string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
        Assert.Contains(result.Limitations, l => l.StartsWith("Budget-limited concept"));
        var read = OkfLocalBundle.ReadExisting(result.BundlePath!, options, Commit);
        Assert.Equal("ready", read.Status);
        Assert.All(read.Documents, d =>
        {
            Assert.InRange(Encoding.UTF8.GetByteCount(d.RawMarkdown), 1, options.MaxDocumentBytes);
            Assert.All(d.Sources, source =>
            {
                Assert.Contains((string)source.Metadata["poolsense_symbol"]!, names);
                Assert.Equal("Class", source.Metadata["poolsense_kind"]);
                Assert.Matches("^[a-f0-9]{64}$", (string)source.Metadata["poolsense_source_sha256"]!);
                Assert.StartsWith($"https://github.com/owner/project/blob/{Commit}/", source.Resource);
                Assert.Contains($"[^{source.Id}]", d.Body);
            });
        });
        Assert.All(read.Documents.Where(d => d.Kind == OkfDocumentKind.Concept), d =>
        {
            Assert.InRange(d.Sources.Count, 1, 7);
            Assert.Contains("display shortened", d.Body);
            Assert.Equal(OkfTrust.Unverified, d.Trust);
        });
        Assert.Contains("Budget-limited concept", read.Documents.Single(d => d.Kind == OkfDocumentKind.Log).Body);
    }

    [Fact]
    public async Task DenseSourceMetadataFitsYamlBudgetWithoutWeakeningExactSourceContracts()
    {
        for (var i = 0; i < 12; i++)
            Add("dense-" + i, $"Models/HTTPRate{i}DTO.cs", "Class", $"HTTPRate{i}DTO", $"public class HTTPRate{i}DTO {{}}");
        var options = new OkfOptions { MaxYamlNodes = 120 };
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot, options);
        Assert.True(result.Status == "prepared", string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
        Assert.Contains(result.Limitations, l => l.StartsWith("Budget-limited concept"));
        var read = OkfLocalBundle.ReadExisting(result.BundlePath!, options, Commit);
        Assert.Equal("ready", read.Status);
        Assert.All(read.Documents, d => OkfParser.Parse(d.Id + ".md", d.RawMarkdown, options));
        Assert.All(read.Documents.Where(d => d.Kind == OkfDocumentKind.Concept), d =>
        {
            Assert.InRange(d.Sources.Count, 1, 7);
            Assert.True(d.Metadata.ContainsKey("description"));
            Assert.True(d.Metadata.ContainsKey("aliases"));
            Assert.True(d.Metadata.ContainsKey("tags"));
            Assert.All(d.Sources, s => Assert.Contains($"[^{s.Id}]", d.Body));
        });
    }

    [Fact]
    public async Task InvalidSourceSpansRemainErrorsRatherThanOrdinaryCoverageLimitations()
    {
        Add("valid", "Models/Valid.cs", "Class", "Valid", "public class Valid {}");
        Add("invalid", "Models/Invalid.cs", "Class", "Invalid", "public class Invalid {}");
        File.WriteAllText(Path.Combine(Source, "Models", "Invalid.cs"), "public class Changed {}");
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot);
        Assert.Equal("prepared-with-errors", result.Status);
        Assert.Contains(result.Errors, e => e.Contains("no longer matched its local source"));
        Assert.Contains(result.Limitations, l => l.StartsWith("Partial source coverage"));
        Assert.DoesNotContain(OkfLocalBundle.ReadExisting(result.BundlePath!).Documents.SelectMany(d => d.Sources),
            s => Equals(s.Metadata["poolsense_symbol"], "Invalid"));
    }

    [Fact]
    public async Task BoundedSelectionBalancesSqlMethodsInterfacesAndTypesInsteadOfFirstAlphabeticalFiles()
    {
        for (var i = 0; i < 24; i++)
            Add("alpha-" + i, $"AA/Alpha{i:00}.cs", "Class", "Alpha" + i, $"public class Alpha{i} {{}}");
        Add("sql", "ZZ/database.sql", "SourceChunk", "database.sql", "SELECT SUM(Amount) FROM dbo.Payments;");
        Add("method", "ZZ/Calculate.cs", "Member", "Calculate", "public decimal Calculate(decimal x) => x * 2;");
        Add("interface", "ZZ/IRate.cs", "Interface", "IRate", "public interface IRate { decimal Get(); }");
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot,
            new() { MaxSymbols = 4 });
        Assert.Equal("prepared", result.Status);
        Assert.Empty(result.Errors);
        Assert.NotEmpty(result.Limitations);
        var documents = OkfLocalBundle.ReadExisting(result.BundlePath!).Documents;
        var names = documents.SelectMany(d => d.Sources).Select(s => s.Metadata["poolsense_symbol"]!.ToString()).ToHashSet();
        Assert.Contains("database.sql", names);
        Assert.Contains("Calculate", names);
        Assert.Contains("IRate", names);
        Assert.Equal(4, names.Count);
        Assert.Contains(documents, d => d.Id == "calculations");
        Assert.Contains(documents, d => d.Id == "contracts");
        Assert.Contains(documents, d => d.Type == "Calculation" && d.Id.StartsWith("calculation-"));
    }

    [Fact]
    public async Task SameNamedAndDynamicCallsWithoutIndexedEdgesDoNotBecomeWorkflows()
    {
        Add("handler", "Handlers/Handle.cs", "Member", "Handle",
            "public void Handle() { dynamic x = null; x.Save(); Notify(); }");
        Add("save", "Stores/Save.cs", "Member", "Save", "public void Save() {}");
        Add("notify", "Messaging/Notify.cs", "Member", "Notify", "public void Notify() {}");
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot);
        Assert.Equal("prepared", result.Status);
        Assert.DoesNotContain(OkfLocalBundle.ReadExisting(result.BundlePath!).Documents, d => d.Type == "Workflow");
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    public async Task LateGraphConnectedTopicsSurviveDenseUnrelatedSymbolsAndTightBudgets(int maximumDocuments)
    {
        for (var i = 0; i < 96; i++)
        {
            Add("noise-type-" + i, $"AA/Types/Noise{i:000}.cs", "Class", $"NoiseType{i}", $"public class NoiseType{i} {{}}");
            Add("noise-member-" + i, $"AA/Members/Noise{i:000}.cs", "Member", $"NoiseMember{i}", $"public void NoiseMember{i}() {{}}");
            Add("noise-interface-" + i, $"AA/Interfaces/Noise{i:000}.cs", "Interface", $"INoise{i}", $"public interface INoise{i} {{ void Empty(); }}");
            Add("noise-sql-" + i, $"AA/Sql/Noise{i:000}.sql", "SourceChunk", $"Noise{i}.sql", "SELECT COUNT(*) FROM dbo.Noise;");
        }
        Add("late-handler", "ZZ/HandleLate.cs", "Member", "HandleLate", "public decimal HandleLate(decimal x) => WorkLate(x);");
        Add("late-worker", "ZZ/WorkLate.cs", "Member", "WorkLate", "public decimal WorkLate(decimal x) => x * 2;");
        Add("late-interface", "ZZ/ILateStore.cs", "Interface", "ILateStore", "public interface ILateStore { void Save(decimal x); }");
        Add("late-store", "ZZ/LateStore.cs", "Class", "LateStore", "public class LateStore : ILateStore { public void Save(decimal x) {} }");
        Add("late-config", "ZZ/ConfigureLate.cs", "Member", "ConfigureLate", "public void ConfigureLate() { var x = configuration.GetSection(\"Rates:Late\"); }");
        using (var db = GraphStore.Open(Database))
        {
            GraphStore.Execute(db, "INSERT INTO edges(repo_root,from_id,to_id,kind,site_line,resolution) VALUES(@p0,'late-handler','late-worker','CALLS',1,'scoped-syntax')",
                GraphStore.RootKey(Source));
            GraphStore.Execute(db, "INSERT INTO edges(repo_root,from_id,to_id,kind,resolution) VALUES(@p0,'late-store','late-interface','IMPLEMENTS','scoped-syntax')",
                GraphStore.RootKey(Source));
        }
        var options = new OkfOptions { MaxSymbols = 12, MaxDocuments = maximumDocuments };
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot, options);
        Assert.True(result.Status == "prepared", string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
        Assert.Contains(result.Limitations, l => l.StartsWith("Partial source coverage"));
        var read = OkfLocalBundle.ReadExisting(result.BundlePath!, options, Commit);
        Assert.Equal("ready", read.Status);
        Assert.InRange(read.Documents.Count, 1, maximumDocuments);
        var workflow = Assert.Single(read.Documents, d => d.Type == "Workflow");
        Assert.Contains("HandleLate", workflow.Body);
        Assert.Contains("WorkLate", workflow.Body);
        Assert.Contains("CALLS", workflow.Body);
        Assert.Contains("scoped-syntax", workflow.Body);
        Assert.DoesNotContain("Noise", workflow.Body);
        var contract = Assert.Single(read.Documents, d => d.Type == "Interface");
        Assert.Contains("ILateStore", contract.Body);
        Assert.Contains("LateStore", contract.Body);
        Assert.Contains("IMPLEMENTS", contract.Body);
        var configuration = Assert.Single(read.Documents, d => d.Type == "Configuration");
        Assert.Contains("Rates:Late", configuration.Body);
        Assert.All(configuration.Sources, s => Assert.Equal("ConfigureLate", s.Metadata["poolsense_symbol"]));
        Assert.Contains(read.Documents, d => d.Type == "Glossary");
        if (maximumDocuments >= 9) Assert.Contains(read.Documents, d => d.Type == "Calculation");
        Assert.InRange(read.Documents.SelectMany(d => d.Sources).Select(s => s.Id).Distinct().Count(), 5, options.MaxSymbols);
    }

    [Fact]
    public async Task ASourceBudgetTooSmallForCallEndpointsDoesNotFabricateWorkflowCoverage()
    {
        Add("handler", "ZZ/Handle.cs", "Member", "Handle", "public void Handle() { Save(); }");
        Add("save", "ZZ/Save.cs", "Member", "Save", "public void Save() {}");
        using (var db = GraphStore.Open(Database))
            GraphStore.Execute(db, "INSERT INTO edges(repo_root,from_id,to_id,kind,site_line,resolution) VALUES(@p0,'handler','save','CALLS',1,'scoped-syntax')",
                GraphStore.RootKey(Source));
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot, new() { MaxSymbols = 1 });
        Assert.Equal("prepared", result.Status);
        var documents = OkfLocalBundle.ReadExisting(result.BundlePath!).Documents;
        Assert.DoesNotContain(documents, d => d.Type == "Workflow");
        Assert.Single(documents.SelectMany(d => d.Sources).Select(s => s.Id).Distinct());
        Assert.NotEmpty(result.Limitations);
    }

    [Fact]
    public async Task CancellationDoesNotPublishAndUnsupportedSourceMetadataDoesNotBecomeKnowledge()
    {
        Add("x", "Models/HTTPRateDTO.cs", "Class", "HTTPRateDTO", "public class HTTPRateDTO {}", "Invented regulated annual rate.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot, cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(Snapshot, "okf")));
        var result = await OkfBundle.PrepareAsync(repository, Commit, Database, Source, Snapshot);
        Assert.Equal("prepared", result.Status);
        Assert.DoesNotContain("Invented regulated", string.Join("\n", Directory.GetFiles(result.BundlePath!, "*.md").Select(File.ReadAllText)));
    }

    private void Populate()
    {
        Add("model", "Models/HTTPRateDTO.cs", "Class", "HTTPRateDTO", "public class HTTPRateDTO { public decimal Rate { get; set; } }");
        Add("handler", "Handlers/HandleRequest.cs", "Member", "HandleRequest",
            "public decimal HandleRequest(decimal amount, decimal rate) { dynamic sink = null; sink.DynamicSave(); return Calculate(amount, rate); }");
        Add("calculate", "Rules/Calculate.cs", "Member", "Calculate",
            "public decimal Calculate(decimal amount, decimal rate) => amount * rate;");
        Add("dynamic", "Other/DynamicSave.cs", "Member", "DynamicSave", "public void DynamicSave() {}");
        Add("contract", "Interfaces/IRateStore.cs", "Interface", "IRateStore", "public interface IRateStore { void Save(decimal total); }");
        Add("implementation", "Stores/RateStore.cs", "Class", "RateStore", "public class RateStore : IRateStore { public void Save(decimal total) {} }");
        Add("config", "Config/Configure.cs", "Member", "Configure",
            "public void Configure() { var x = configuration.GetSection(\"Rates:Default\"); var password = \"Password123\"; }");
        Add("sql", "database/rates.sql", "SourceChunk", "rates.sql", """
            INSERT INTO dbo.Totals (Value)
            SELECT SUM(p.Amount * r.Value)
            FROM dbo.Payments p
            JOIN dbo.Rates r ON r.Id = p.RateId
            WHERE p.Active = 1;
            """);
        Add("other-sql", "database/notes.sql", "SourceChunk", "notes.sql", "-- JOIN dbo.Fake ON fictional = imaginary");
        Add("unrelated", "Models/Unrelated.cs", "Class", "Unrelated", "public class Unrelated {}");
        using var db = GraphStore.Open(Database);
        GraphStore.Execute(db, "INSERT INTO edges(repo_root,from_id,to_id,kind,site_line,site_col,resolution) VALUES(@p0,'handler','calculate','CALLS',1,1,'scoped-syntax')",
            GraphStore.RootKey(Source));
        GraphStore.Execute(db, "INSERT INTO edges(repo_root,from_id,to_id,kind,resolution) VALUES(@p0,'implementation','contract','IMPLEMENTS','scoped-syntax')",
            GraphStore.RootKey(Source));
        var otherRoot = GraphStore.RootKey(Path.Combine(root, "other-repo"));
        GraphStore.Execute(db, "INSERT INTO repositories VALUES(@p0,'2026-01-01',1,1,0,@p1,@p2)", otherRoot, GraphStore.Version, GraphStore.Parser);
        GraphStore.Execute(db, "INSERT INTO symbols VALUES('foreign',@p0,'Foreign.cs','Member','ForeignSave','ForeignSave',1,1,NULL,'public void ForeignSave() {}','')", otherRoot);
        GraphStore.Execute(db, "INSERT INTO edges(repo_root,from_id,to_id,kind,resolution) VALUES(@p0,'handler','foreign','CALLS','syntax')", GraphStore.RootKey(Source));
    }

    private void Add(string id, string path, string kind, string name, string text, string docs = "")
    {
        var full = Path.Combine(Source, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Length;
        using var db = GraphStore.Open(Database);
        GraphStore.Execute(db, "INSERT INTO symbols VALUES(@p0,@p1,@p2,@p3,@p4,@p4,1,@p5,NULL,@p6,@p7)",
            id, GraphStore.RootKey(Source), path, kind, name, lines, text, docs);
    }

    public void Dispose()
    {
        for (var attempt = 0; Directory.Exists(root); attempt++)
        {
            try { Directory.Delete(root, true); return; }
            catch (IOException exception) when (OperatingSystem.IsWindows() && attempt < 7 &&
                (exception.HResult & 0xffff) is 5 or 32 or 33)
            {
                Thread.Sleep(Math.Min(500, 25 << attempt));
            }
        }
    }

    private sealed class NeverLlm : ILLMService
    {
        public int Calls { get; private set; }
        public Task<string> GetResponseAsync(string prompt)
        {
            Calls++;
            throw new InvalidOperationException("Deterministic production must not call an LLM.");
        }
    }
}

internal static class OkfProducerTestExtensions
{
    internal static IReadOnlyList<object?> TagsForTest(this OkfDocument document) =>
        document.Metadata.GetValueOrDefault("tags") as IReadOnlyList<object?> ?? [];
}
