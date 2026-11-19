using PoolSense.Api.Services.CodeRAG;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class OkfParserTests
{
    [Fact]
    public void TypeOnlyUnknownConceptIsConformantAndRoundtripsExactly()
    {
        const string markdown = "---\r\ntype: Future Widget\r\ncustom:\r\n  nested: [a, {b: 42}]\r\n---\r\nbody";
        var document = OkfParser.Parse("future/widget.md", markdown);
        Assert.Equal("future/widget", document.Id);
        Assert.Equal("Future Widget", document.Type);
        Assert.Equal("stable", document.Status);
        Assert.Equal(OkfTrust.Unverified, document.Trust);
        Assert.Equal(markdown, document.ToMarkdown());
        var custom = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(document.Metadata["custom"]);
        Assert.IsAssignableFrom<IReadOnlyList<object?>>(custom["nested"]);
    }

    [Theory]
    [InlineData("verified: {by: 'human:reviewer', at: '2026-01-01T00:00:00Z'}", OkfTrust.HumanReviewed)]
    [InlineData("verified: [{by: 'process:check', at: '2026-01-01T00:00:00Z'}]", OkfTrust.MachineConfirmed)]
    [InlineData("generated: {by: 'human:author', at: '2026-01-01T00:00:00Z'}", OkfTrust.Unverified)]
    public void VerificationMapAndListHaveSemanticTrust(string metadata, OkfTrust trust)
    {
        var document = OkfParser.Parse("a.md", $"---\ntype: Reference\n{metadata}\n---\n");
        Assert.Equal(trust, document.Trust);
        if (trust != OkfTrust.Unverified) Assert.Single(document.Verified);
    }

    [Fact]
    public void SafeCoreYamlTagsAreDataAndNeverObjectConstruction()
    {
        var document = OkfParser.Parse("a.md", "---\ntype: !!str Reference\ncustom: !!int 42\n---\n");
        Assert.Equal("Reference", document.Type);
        Assert.Equal("42", document.Metadata["custom"]);
    }

    [Fact]
    public void StalenessUsesAnAbsoluteInstantAndAttestationIsOnlyMetadata()
    {
        var document = OkfParser.Parse("a.md", """
            ---
            type: Attested Computation
            stale_after: 2026-01-01T00:00:00+02:00
            executor: {resource: "https://not-fetched.invalid/run"}
            attester: {resource: "../never-execute.ps1"}
            ---
            # Computation
            """);
        Assert.False(document.IsStale(DateTimeOffset.Parse("2025-12-31T21:59:59Z")));
        Assert.True(document.IsStale(DateTimeOffset.Parse("2025-12-31T22:00:00Z")));
        Assert.True(document.Metadata.ContainsKey("attester"));
    }

    [Theory]
    [InlineData("---\ntitle: missing type\n---\n")]
    [InlineData("---\ntype: [not, a, string]\n---\n")]
    [InlineData("---\ntype: A\ntype: B\n---\n")]
    [InlineData("---\ntype: A\nsources: [{id: missing-resource}]\n---\n")]
    [InlineData("---\ntype: A\nbroken: [unterminated\n---\n")]
    [InlineData("---\ntype: A\nx: &anchor [one]\ny: *anchor\n---\n")]
    [InlineData("---\ntype: A\nx: !unsafe payload\n---\n")]
    public void RejectsMalformedOrUnsafeYaml(string markdown) =>
        Assert.Throws<InvalidDataException>(() => OkfParser.Parse("a.md", markdown));

    [Fact]
    public void RejectsOversizedDeepOrExcessiveYaml()
    {
        Assert.Throws<InvalidDataException>(() => OkfParser.Parse("a.md", "---\ntype: Reference\n---\n",
            new() { MaxDocumentBytes = 8 }));
        Assert.Throws<InvalidDataException>(() => OkfParser.Parse("a.md", "---\ntype: A\nx: [[[[[deep]]]]]\n---\n",
            new() { MaxYamlDepth = 3 }));
        Assert.Throws<InvalidDataException>(() => OkfParser.Parse("a.md", "---\ntype: A\nx: [1,2,3,4,5]\n---\n",
            new() { MaxYamlNodes = 8 }));
    }

    [Fact]
    public void ReservedDocumentsDoNotBecomeConcepts()
    {
        Assert.Equal(OkfDocumentKind.Index, OkfParser.Parse("index.md", "---\nokf_version: '0.2'\n---\n# Listing").Kind);
        Assert.Equal(OkfDocumentKind.Index, OkfParser.Parse("sub/index.md", "# Listing").Kind);
        Assert.Equal(OkfDocumentKind.Log, OkfParser.Parse("log.md", "# Updates\n## 2026-01-01\n* Creation").Kind);
        Assert.Throws<InvalidDataException>(() => OkfParser.Parse("sub/index.md", "---\nokf_version: '0.2'\n---\n"));
        Assert.Throws<InvalidDataException>(() => OkfParser.Parse("index.md", "---\ntype: Reference\n---\n"));
        Assert.Throws<InvalidDataException>(() => OkfParser.Parse("log.md", "## yesterday\n* Update"));
    }
}
