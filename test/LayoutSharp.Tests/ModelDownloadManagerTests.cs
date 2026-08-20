using LayoutSharp.Internal;
using LayoutSharp.Models;
using Xunit;

namespace LayoutSharp.Tests;

/// <summary>
/// Download-manager behaviour that can be checked without a network: cache resolution, offline
/// mode, URL scheme policy, and file-name hygiene. (The happy path is covered by the integration tests.)
/// </summary>
[Collection("EnvironmentVariables")]
public class ModelDownloadManagerTests
{
    private static readonly LayoutModelSpec Spec = ModelRegistry.Get(LayoutModel.DoclingLayoutHeron);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "layoutsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ResolveCacheDirectory_PrefersExplicit_ThenEnv_ThenLocalAppData()
    {
        var explicitDir = TempDir();
        Assert.Equal(Path.GetFullPath(explicitDir), ModelDownloadManager.ResolveCacheDirectory(explicitDir));

        var envDir = TempDir();
        var previous = Environment.GetEnvironmentVariable(ModelDownloadManager.CacheEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ModelDownloadManager.CacheEnvVar, envDir);
            Assert.Equal(Path.GetFullPath(envDir), ModelDownloadManager.ResolveCacheDirectory(null));
            Assert.Equal(Path.GetFullPath(explicitDir), ModelDownloadManager.ResolveCacheDirectory(explicitDir)); // explicit still wins

            Environment.SetEnvironmentVariable(ModelDownloadManager.CacheEnvVar, null);
            var def = ModelDownloadManager.ResolveCacheDirectory(null);
            Assert.EndsWith(Path.Combine("LayoutSharp", "models"), def);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModelDownloadManager.CacheEnvVar, previous);
        }
    }

    [Fact]
    public void GetModelPath_CombinesCacheAndFileName()
    {
        var dir = TempDir();
        Assert.Equal(Path.Combine(Path.GetFullPath(dir), Spec.FileName), ModelDownloadManager.GetModelPath(Spec, dir));
    }

    [Fact]
    public async Task EnsureModel_ReturnsExistingFile_WithoutNetwork()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, Spec.FileName);
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 }); // any content: cached files are trusted

        var resolved = await ModelDownloadManager.EnsureModelAsync(Spec, dir, offline: true, logger: null, CancellationToken.None);
        Assert.Equal(path, resolved);
    }

    [Fact]
    public async Task EnsureModel_Offline_MissingFile_Throws()
    {
        var dir = TempDir();
        var ex = await Assert.ThrowsAsync<OfflineModelMissingException>(() =>
            ModelDownloadManager.EnsureModelAsync(Spec, dir, offline: true, logger: null, CancellationToken.None));
        Assert.Equal(Path.Combine(dir, Spec.FileName), ex.ExpectedPath);
        Assert.Contains("Offline", ex.Message);
    }

    [Fact]
    public async Task EnsureModel_OfflineViaEnvironment_MissingFile_Throws()
    {
        var dir = TempDir();
        var previous = Environment.GetEnvironmentVariable(ModelDownloadManager.OfflineEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ModelDownloadManager.OfflineEnvVar, "1");
            Assert.True(ModelDownloadManager.IsOfflineFromEnvironment());
            await Assert.ThrowsAsync<OfflineModelMissingException>(() =>
                ModelDownloadManager.EnsureModelAsync(Spec, dir, offline: false, logger: null, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModelDownloadManager.OfflineEnvVar, previous);
        }
    }

    [Fact]
    public async Task EnsureModel_RefusesPlainHttpMirror()
    {
        var dir = TempDir();
        var previous = Environment.GetEnvironmentVariable(ModelDownloadManager.BaseUrlEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ModelDownloadManager.BaseUrlEnvVar, "http://models.example.com/layout");
            var ex = await Assert.ThrowsAsync<LayoutSharpException>(() =>
                ModelDownloadManager.EnsureModelAsync(Spec, dir, offline: false, logger: null, CancellationToken.None));
            Assert.Contains("HTTPS", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModelDownloadManager.BaseUrlEnvVar, previous);
        }
    }

    [Fact]
    public async Task EnsureModel_UnreachableHttpsMirror_ThrowsModelDownloadException_AndLeavesNoPartFile()
    {
        var dir = TempDir();
        var previous = Environment.GetEnvironmentVariable(ModelDownloadManager.BaseUrlEnvVar);
        try
        {
            // Loopback port that nothing listens on: fails fast with a connection error, retried, then wrapped.
            Environment.SetEnvironmentVariable(ModelDownloadManager.BaseUrlEnvVar, "https://127.0.0.1:1/models");
            var ex = await Assert.ThrowsAsync<ModelDownloadException>(() =>
                ModelDownloadManager.EnsureModelAsync(Spec, dir, offline: false, logger: null, CancellationToken.None));
            Assert.StartsWith("https://127.0.0.1:1/models/", ex.Url);
            Assert.NotNull(ex.InnerException);
            Assert.Empty(Directory.GetFiles(dir, "*.part"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModelDownloadManager.BaseUrlEnvVar, previous);
        }
    }

    [Fact]
    public async Task EnsureModel_HonoursCancellation()
    {
        var dir = TempDir();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ModelDownloadManager.EnsureModelAsync(Spec, dir, offline: false, logger: null, cts.Token));
    }
}

[CollectionDefinition("EnvironmentVariables", DisableParallelization = true)]
public class EnvironmentVariablesCollection { }
