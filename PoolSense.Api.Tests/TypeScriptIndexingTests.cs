using CodeRAG.Core.Indexing;
using CodeRAG.Core.Storage;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class TypeScriptIndexingTests : IDisposable
{
    private readonly string directory = Path.Combine(Directory.GetCurrentDirectory(), ".typescript-tests", Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(directory, "source");
    private string Cache(string name) => Path.Combine(directory, name + ".json");

    [Fact]
    public async Task CompilerLinksImportsFunctionsAndEstablishedServiceMethods_WithSitesAndFileParents()
    {
        Write("service.ts", """
            export function helper() { return 1; }
            export class Service {
              send() { return helper(); }
              static create() { return 1; }
            }
            export const service = new Service();
            """);
        Write("caller.tsx", """
            import { helper as help, Service, service } from './service.js';
            import * as api from './service';
            export class Component {
              constructor(private service: Service) {}
              local() { return 1; }
              run() { this.local(); this.service.send(); help(); service.send(); Service.create(); api.helper(); unknown(); }
            }
            export const render = () => <div>{help()}</div>;
            """);
        Write("plain.js", """
            import { helper } from './service';
            function local() { helper(); }
            local();
            """);
        Write("view.jsx", """
            import { helper } from './service';
            export function View() { return <span>{helper()}</span>; }
            """);
        var result = await new TypeScriptIndexer().IndexAsync(Root, ["service.ts", "caller.tsx", "plain.js", "view.jsx"], Cache("first"));
        Assert.Equal(4, result.ParsedFiles);
        Assert.Equal(0, result.ReusedFiles);
        Assert.Empty(result.Warnings);
        Assert.DoesNotContain(result.Symbols, symbol => symbol.Kind == "File");
        Assert.Contains(result.Symbols, symbol => symbol.Name == "View" && symbol.Kind == "Function");
        Assert.All(result.Symbols.Where(symbol => !symbol.QualifiedName.Contains('.')), symbol =>
            Assert.Equal(SourceText.Hash($"{GraphStore.RootKey(Root)}\0{symbol.FilePath}\0File"), symbol.ParentId));
        AssertCall(result, "Component.run", "Component.local");
        AssertCall(result, "Component.run", "Service.send");
        AssertCall(result, "Component.run", "Service.create");
        AssertCall(result, "Component.run", "helper");
        AssertCall(result, "Service.send", "helper");
        AssertCall(result, "render", "helper");
        AssertCall(result, "local", "helper");
        AssertCall(result, "View", "helper");
        var run = Assert.Single(result.Symbols, symbol => symbol.QualifiedName == "Component.run");
        Assert.Equal(6, result.Edges.Count(edge => edge.FromId == run.Id && edge.Kind == "CALLS"));
        Assert.Equal(4, result.Edges.Count(edge => edge.Kind == "IMPORTS"));
        Assert.All(result.Edges, edge => Assert.Equal("typescript-syntax", edge.Resolution));
        Assert.All(result.Edges.Where(edge => edge.Kind == "CALLS"), edge =>
        {
            Assert.True(edge.SiteLine > 0);
            Assert.True(edge.SiteColumn > 0);
        });
    }

    [Fact]
    public async Task ContentCacheReusesAcrossRoots_RelinksChangedImportsAndDeletedDeclarations()
    {
        Write("a.ts", "export function invoke() { return 1; }");
        Write("b.ts", "export function invoke() { return 2; }");
        Write("caller.ts", "import { invoke } from './a'; export function run() { invoke(); }");
        var indexer = new TypeScriptIndexer();
        var first = await indexer.IndexAsync(Root, ["a.ts", "b.ts", "caller.ts"], Cache("first"));
        Assert.Equal(3, first.ParsedFiles);
        var unchanged = await indexer.IndexAsync(Root, ["a.ts", "b.ts", "caller.ts"], Cache("unchanged"), Cache("first"));
        Assert.Equal(0, unchanged.ParsedFiles);
        Assert.Equal(3, unchanged.ReusedFiles);

        Write("caller.ts", "import { invoke } from './b'; export function run() { invoke(); }");
        var changed = await indexer.IndexAsync(Root, ["a.ts", "b.ts", "caller.ts"], Cache("changed"), Cache("unchanged"));
        Assert.Equal(1, changed.ParsedFiles);
        Assert.Equal(2, changed.ReusedFiles);
        var target = Assert.Single(changed.Symbols, symbol => symbol.FilePath == "b.ts");
        Assert.Contains(changed.Edges, edge => edge.Kind == "CALLS" && edge.ToId == target.Id);
        Assert.DoesNotContain(changed.Edges, edge => edge.Kind == "CALLS" &&
            edge.ToId == first.Symbols.Single(symbol => symbol.FilePath == "a.ts").Id);

        Write("b.ts", "export const removed = 1;");
        var removedDeclaration = await indexer.IndexAsync(Root, ["a.ts", "b.ts", "caller.ts"], Cache("removed-declaration"), Cache("changed"));
        Assert.Equal(2, removedDeclaration.ReusedFiles);
        Assert.DoesNotContain(removedDeclaration.Edges, edge => edge.Kind == "CALLS");
        File.Delete(Path.Combine(Root, "b.ts"));
        var removedFile = await indexer.IndexAsync(Root, ["a.ts", "caller.ts"], Cache("removed-file"), Cache("removed-declaration"));
        Assert.Equal(0, removedFile.ParsedFiles);
        Assert.Equal(2, removedFile.ReusedFiles);
        Assert.DoesNotContain(removedFile.Edges, edge => edge.Kind is "CALLS" or "IMPORTS");
        Assert.DoesNotContain("\"b.ts\":", File.ReadAllText(Cache("removed-file")));

        var movedRoot = Path.Combine(directory, "moved-source");
        Directory.Move(Root, movedRoot);
        var moved = await indexer.IndexAsync(movedRoot, ["a.ts", "caller.ts"], Cache("moved"), Cache("removed-file"));
        Assert.Equal(2, moved.ReusedFiles);
        Assert.Equal(File.ReadAllText(Cache("removed-file")), File.ReadAllText(Cache("moved")));
        Assert.NotEqual(removedFile.Symbols[0].Id, moved.Symbols[0].Id);
    }

    [Fact]
    public async Task SameCachePathRefresh_ReusesAndAtomicallyReplacesCache_AfterChangesAndDeletion()
    {
        Write("service.ts", "export function invoke() { return 1; }");
        Write("caller.ts", "import { invoke } from './service'; export function run() { invoke(); }");
        var indexer = new TypeScriptIndexer();
        var cache = Cache("current");
        var first = await indexer.IndexAsync(Root, ["service.ts", "caller.ts"], cache);
        Assert.Equal(2, first.ParsedFiles);
        AssertCall(first, "run", "invoke");
        var unchanged = await indexer.IndexAsync(Root, ["service.ts", "caller.ts"], cache, cache);
        Assert.Equal(0, unchanged.ParsedFiles);
        Assert.Equal(2, unchanged.ReusedFiles);
        AssertCall(unchanged, "run", "invoke");

        Write("service.ts", "export function replacement() { return 2; }");
        var changed = await indexer.IndexAsync(Root, ["service.ts", "caller.ts"], cache, cache);
        Assert.Equal(1, changed.ParsedFiles);
        Assert.Equal(1, changed.ReusedFiles);
        Assert.DoesNotContain(changed.Edges, edge => edge.Kind == "CALLS");

        File.Delete(Path.Combine(Root, "service.ts"));
        var removed = await indexer.IndexAsync(Root, ["caller.ts"], cache, cache);
        Assert.Equal(0, removed.ParsedFiles);
        Assert.Equal(1, removed.ReusedFiles);
        Assert.DoesNotContain(removed.Edges, edge => edge.Kind is "CALLS" or "IMPORTS");
        var caller = Assert.Single(removed.Symbols);
        Assert.Equal(SourceText.Hash($"{GraphStore.RootKey(Root)}\0caller.ts\0File"), caller.ParentId);
        Assert.DoesNotContain("\"service.ts\":", File.ReadAllText(cache));
    }

    [Fact]
    public async Task DoesNotExecuteRepositoryCodeOrConfiguration_AndNeverReadsUnlistedImports()
    {
        var marker = Path.Combine(directory, "executed.txt").Replace("\\", "\\\\");
        var execute = $"require('node:fs').writeFileSync('{marker}', 'executed');";
        Write("entry.js", execute + "\nimport { hidden } from './hidden'; export function run() { hidden(); }");
        Write("hidden.ts", "export function hidden() { return 1; }");
        Write("tsconfig.json", """{"compilerOptions":{"plugins":[{"name":"./plugin.cjs"}]},"extends":"./plugin.cjs"}""");
        Write("plugin.cjs", execute);
        Write("package.json", """{"scripts":{"prepare":"node plugin.cjs"},"type":"module"}""");
        Write(Path.Combine("node_modules", "typescript", "lib", "typescript.js"), execute);
        var result = await new TypeScriptIndexer().IndexAsync(Root, ["entry.js"], Cache("first"));
        Assert.False(File.Exists(Path.Combine(directory, "executed.txt")));
        Assert.Single(result.Symbols);
        Assert.DoesNotContain(result.Edges, edge => edge.Kind is "CALLS" or "IMPORTS");
    }

    [Fact]
    public async Task JavaScriptObjectMethodsAndThisCallsResolveAcrossImports()
    {
        Write("service.js", """
            export const service = {
              send() { this.finish(); },
              finish: function () { return 1; }
            };
            """);
        Write("entry.jsx", """
            import { service } from './service';
            export function View() { service.send(); return <span />; }
            """);
        var result = await new TypeScriptIndexer().IndexAsync(Root, ["service.js", "entry.jsx"], Cache("first"));
        AssertCall(result, "View", "service.send");
        AssertCall(result, "service.send", "service.finish");
        Assert.All(result.Symbols.Where(symbol => symbol.FilePath == "service.js"), symbol => Assert.Equal("Method", symbol.Kind));
    }

    [Fact]
    public async Task DefaultFunctionAndNamedReexportsResolve_WhileMutatedImportedMethodsDoNot()
    {
        Write("service.ts", """
            export default () => 1;
            export class Service { send() {} }
            export const service = new Service();
            """);
        Write("barrel.ts", "export { default as action, service } from './service';");
        Write("entry.ts", """
            import { action, service } from './barrel';
            export function run() { action(); service.send(); }
            service.send = unknown;
            """);
        var result = await new TypeScriptIndexer().IndexAsync(Root, ["service.ts", "barrel.ts", "entry.ts"], Cache("first"));
        AssertCall(result, "run", "default");
        Assert.Single(result.Edges, edge => edge.Kind == "CALLS");
    }

    [Fact]
    public async Task UnknownShadowedAndReassignedTargetsDoNotProduceCallEdges()
    {
        Write("entry.ts", """
            function known() {}
            function shadow(known: unknown) { known(); }
            function reassigned() { let alias = known; alias = unknown; alias(); }
            function destructured() { let alias = known; [alias] = unknown; alias(); }
            class Example {
              method() {}
              run() { method(); unknown.method(); this.method(); }
            }
            function local() { known(); }
            """);
        var result = await new TypeScriptIndexer().IndexAsync(Root, ["entry.ts"], Cache("first"));
        Assert.Equal(2, result.Edges.Count(edge => edge.Kind == "CALLS"));
        AssertCall(result, "Example.run", "Example.method");
        AssertCall(result, "local", "known");
    }

    [Theory]
    [InlineData("..\\outside.ts")]
    [InlineData("../outside.ts")]
    [InlineData("folder\\..\\entry.ts")]
    [InlineData("C:\\outside.ts")]
    [InlineData("entry.ts:stream")]
    public async Task RejectsUnsafeSourcePaths(string relative)
    {
        Write("entry.ts", "export function ok() {}");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new TypeScriptIndexer().IndexAsync(Root, [relative], Cache("first")));
        Assert.False(File.Exists(Cache("first")));
    }

    [Fact]
    public async Task RejectsCacheInsideSource_AndLinkedSourceDirectory()
    {
        Write("entry.ts", "export function ok() {}");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new TypeScriptIndexer().IndexAsync(Root, ["entry.ts"], Path.Combine(Root, "cache.json")));
        var outside = Path.Combine(directory, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "escape.ts"), "export function escaped() {}");
        var linked = Path.Combine(Root, "linked");
        if (OperatingSystem.IsWindows())
        {
            var result = await new ProcessRunner().RunAsync("powershell",
                ["-NoProfile", "-NonInteractive", "-Command",
                    $"New-Item -ItemType Junction -Path '{linked.Replace("'", "''")}' -Target '{outside.Replace("'", "''")}' | Out-Null"],
                directory, TimeSpan.FromSeconds(30));
            Assert.True(result.ExitCode == 0, result.Error);
        }
        else Directory.CreateSymbolicLink(linked, outside);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new TypeScriptIndexer().IndexAsync(Root, ["linked\\escape.ts"], Cache("first")));
        }
        finally { Directory.Delete(linked); }
    }

    private static void AssertCall(TypeScriptIndexResult result, string from, string to)
    {
        var caller = Assert.Single(result.Symbols, symbol => symbol.QualifiedName == from);
        var target = Assert.Single(result.Symbols, symbol => symbol.QualifiedName == to);
        Assert.Contains(result.Edges, edge => edge.Kind == "CALLS" && edge.FromId == caller.Id && edge.ToId == target.Id);
    }

    private void Write(string relative, string source)
    {
        var file = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, source);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
