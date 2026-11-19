using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PoolSense.Api.Services.CodeRAG;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class CodeStoragePathTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "poolsense-storage-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RelativePath_UsesContentRootAndMatchesIndexFiles()
    {
        var expected = Path.Combine(root, "artifacts", "code-knowledge");
        var files = new CodeIndexFiles(new TestEnvironment(root),
            Microsoft.Extensions.Options.Options.Create(new CodeRAGOptions { StoragePath = "artifacts/code-knowledge" }));
        Assert.Equal(expected, CodeStoragePath.Resolve("artifacts/code-knowledge", root));
        Assert.Equal(expected, files.BasePath);
        Assert.False(Directory.Exists(expected));
    }

    [Fact]
    public void WritableCheck_PreservesExistingSnapshotsAndRemovesProbe()
    {
        var directory = Path.Combine(root, "artifacts", "code-knowledge");
        CodeStoragePath.EnsureWritable(directory);
        var snapshot = Path.Combine(directory, "existing-snapshot");
        Directory.CreateDirectory(snapshot);
        var file = Path.Combine(snapshot, "index.sqlite3");
        File.WriteAllBytes(file, [1, 2, 3]);
        CodeStoragePath.EnsureWritable(directory);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
        Assert.Equal([snapshot], Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public void FileInsteadOfDirectory_FailsWithActionableStorageError()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "not-a-directory");
        File.WriteAllText(path, "preserve");
        var exception = Assert.Throws<InvalidOperationException>(() => CodeStoragePath.EnsureWritable(path));
        Assert.Contains(path, exception.Message);
        Assert.Contains("CodeRAG__StoragePath", exception.Message);
        Assert.Contains("not writable", exception.Message);
        Assert.Equal("preserve", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\0")]
    public void InvalidSetting_FailsWithConfigurationKey(string value) =>
        Assert.Contains("CodeRAG:StoragePath",
            Assert.Throws<InvalidOperationException>(() => CodeStoragePath.Resolve(value, root)).Message);

    [Fact]
    public void UnavailableWindowsDrive_FailsWithoutFallingBack()
    {
        if (!OperatingSystem.IsWindows()) return;
        var drive = Enumerable.Range('D', 23).Select(letter => $"{(char)letter}:\\")
            .FirstOrDefault(path => !Directory.Exists(path));
        if (drive is null) return;
        var path = CodeStoragePath.Resolve(Path.Combine(drive, $"poolsense-storage-test-{Guid.NewGuid():N}"), root);
        var exception = Assert.Throws<InvalidOperationException>(() => CodeStoragePath.EnsureWritable(path));
        Assert.Contains(path, exception.Message);
        Assert.Contains("Check the drive", exception.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void EnabledRegistration_RejectsUnavailableStorageBeforeWorkerStarts()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "existing-file");
        File.WriteAllText(path, "preserve");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CodeRAG:Enabled"] = "true", ["CodeRAG:StoragePath"] = path,
            ["CodeRAG:GitHubToken"] = "test-placeholder"
        }).Build();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddCodeKnowledge(configuration, new TestEnvironment(root)));
        Assert.Contains("CodeRAG__StoragePath", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class TestEnvironment(string path) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = path;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
