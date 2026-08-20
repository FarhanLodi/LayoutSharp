using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LayoutSharp.Models;
using Microsoft.Extensions.Logging;

namespace LayoutSharp.Internal;

/// <summary>
/// Resolves the local on-disk path for a model asset, downloading it from the configured base URL
/// when it is not already cached. Downloads are HTTPS-only, retried with back-off, written
/// atomically (<c>.part</c> then rename) and SHA-256 verified fail-closed.
/// </summary>
internal static class ModelDownloadManager
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };

    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>Environment variable that overrides the download base URL (private mirror).</summary>
    public const string BaseUrlEnvVar = "LAYOUTSHARP_MODEL_BASE_URL";

    /// <summary>Environment variable that overrides the model cache directory.</summary>
    public const string CacheEnvVar = "LAYOUTSHARP_CACHE";

    /// <summary>Environment variable that forces offline mode when set to <c>1</c> or <c>true</c>.</summary>
    public const string OfflineEnvVar = "LAYOUTSHARP_OFFLINE";

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = true,
        };
        var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromMinutes(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LayoutSharp/1.0");
        return client;
    }

    /// <summary>
    /// The directory models are cached in: <paramref name="customCachePath"/> if given, else the
    /// <c>LAYOUTSHARP_CACHE</c> environment variable, else <c>%LocalAppData%/LayoutSharp/models</c>.
    /// </summary>
    public static string ResolveCacheDirectory(string? customCachePath)
    {
        if (!string.IsNullOrWhiteSpace(customCachePath))
            return Path.GetFullPath(customCachePath);

        var envOverride = Environment.GetEnvironmentVariable(CacheEnvVar);
        if (!string.IsNullOrWhiteSpace(envOverride))
            return Path.GetFullPath(envOverride);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LayoutSharp",
            "models");
    }

    /// <summary>
    /// The on-disk path a model would occupy in the given cache (whether or not it exists yet), or
    /// the <see cref="LayoutModelSpec.LocalPath"/> of a custom model (which is never cached).
    /// </summary>
    public static string GetModelPath(LayoutModelSpec spec, string? customCachePath)
        => spec.LocalPath ?? Path.Combine(ResolveCacheDirectory(customCachePath), SafeFileName(spec.FileName));

    /// <summary>True when offline mode is forced through the environment.</summary>
    public static bool IsOfflineFromEnvironment()
    {
        var v = Environment.GetEnvironmentVariable(OfflineEnvVar);
        return v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the absolute path to a cached copy of <paramref name="asset"/>, downloading it
    /// if not already present. Safe for concurrent callers: only one download runs at a time.
    /// A custom model (<see cref="LayoutModelSpec.LocalPath"/>) is returned as-is — never downloaded,
    /// cached or deleted — after an optional SHA-256 check when the spec carries a checksum.
    /// </summary>
    public static async Task<string> EnsureModelAsync(
        ModelAsset asset,
        string? customCachePath,
        bool offline,
        ILogger? logger,
        CancellationToken cancellationToken,
        IProgress<ModelDownloadProgress>? progress = null)
    {
        var cacheDir = ResolveCacheDirectory(customCachePath);
        var finalPath = Path.Combine(cacheDir, SafeFileName(asset.FileName));
        if (File.Exists(finalPath)) return finalPath;

        if (offline || IsOfflineFromEnvironment())
        {
            throw new OfflineModelMissingException(
                $"Offline mode is enabled and the model '{asset.FileName}' is not present at '{finalPath}'. " +
                "Pre-seed the cache with the model file (see LayoutService.WarmUpAsync on a connected machine), " +
                $"or disable offline mode (LayoutServiceOptions.Offline / {OfflineEnvVar}).",
                finalPath);
        }

        await CacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(finalPath)) return finalPath;

            Directory.CreateDirectory(cacheDir);
            var url = ResolveUrl(asset);
            var tempPath = finalPath + ".part";

            // A checksum failure can mean the partial file was corrupt (a truncated resume, a proxy
            // that injected an error page). The first such failure discards the .part and starts
            // clean; a second one is a real mismatch and is fatal.
            bool retriedFromScratch = false;

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    logger?.LogInformation("Downloading model {Name} from {Url} (attempt {Attempt}/{Max})",
                        asset.FileName, url, attempt, MaxAttempts);

                    await DownloadToAsync(url, tempPath, asset.FileName, attempt, logger, progress, cancellationToken).ConfigureAwait(false);

                    var actual = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(tempPath);
                        if (!retriedFromScratch && attempt < MaxAttempts)
                        {
                            // Most likely a poisoned partial file: throw the bytes away and refetch.
                            retriedFromScratch = true;
                            logger?.LogWarning("Model {Name} failed SHA-256 verification (expected {Expected}, got {Actual}); " +
                                "discarding the partial file and downloading it again from scratch.",
                                asset.FileName, asset.Sha256, actual);
                            continue;
                        }

                        throw new ModelChecksumException(
                            $"Downloaded model '{asset.FileName}' failed SHA-256 verification " +
                            $"(expected {asset.Sha256}, got {actual}). The file was discarded. " +
                            "If you are using a mirror, make sure it serves the exact published asset.");
                    }

                    File.Move(tempPath, finalPath, overwrite: false);
                    logger?.LogInformation("Model {Name} cached at {Path}", asset.FileName, finalPath);
                    return finalPath;
                }
                catch (Exception ex) when (IsDownloadFailure(ex, cancellationToken))
                {
                    // The .part file is deliberately KEPT so the next attempt resumes from where this
                    // one stopped, rather than refetching bytes that are already on disk.
                    if (attempt >= MaxAttempts || !IsTransient(ex))
                    {
                        long onDisk = FileLength(tempPath);
                        throw new ModelDownloadException(
                            $"Failed to download model '{asset.FileName}' from {url} (attempt {attempt}/{MaxAttempts}): {ex.Message} " +
                            (onDisk > 0
                                ? $"{onDisk:N0} bytes are cached in '{tempPath}' and the next call resumes from there. "
                                : string.Empty) +
                            $"Check connectivity and the URL, or pre-seed the cache at '{finalPath}' and enable offline mode.",
                            url, ex);
                    }

                    var delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
                    logger?.LogWarning(ex, "Download of {Name} failed after {Bytes:N0} bytes (attempt {Attempt}/{Max}); resuming in {Delay}s.",
                        asset.FileName, FileLength(tempPath), attempt, MaxAttempts, delay.TotalSeconds);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The caller cancelled: keep the partial file so a later call can resume it.
                    throw;
                }
                catch
                {
                    TryDelete(tempPath);
                    throw;
                }
            }
        }
        finally
        {
            CacheLock.Release();
        }
    }

    /// <summary>Any network/IO failure of the download itself (never a caller-requested cancellation).</summary>
    private static bool IsDownloadFailure(Exception ex, CancellationToken ct)
        => !ct.IsCancellationRequested
           && ex is HttpRequestException or IOException or TaskCanceledException or TimeoutException;

    /// <summary>Failures worth retrying: anything except a definitive 4xx (other than 408/429).</summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } sc }
            when (int)sc is >= 400 and < 500
                 && sc is not (System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests) => false,
        _ => true,
    };

    /// <summary>
    /// Streams <paramref name="url"/> into <paramref name="tempPath"/>, continuing an existing
    /// partial file when there is one.
    /// </summary>
    /// <remarks>
    /// Resume is a conditional optimisation, never a correctness requirement: the request asks for
    /// <c>Range: bytes=&lt;have&gt;-</c>, and
    /// <list type="bullet">
    /// <item><description><c>206 Partial Content</c> — the server honoured it; the body is appended.</description></item>
    /// <item><description><c>200 OK</c> — the server ignored it and is sending the whole file; the partial file is truncated and rewritten.</description></item>
    /// <item><description><c>416 Range Not Satisfiable</c> — the partial file is at least as long as the asset (stale or corrupt); it is discarded and the whole file refetched.</description></item>
    /// </list>
    /// Whatever happens, the SHA-256 check downstream is what actually decides whether the bytes are good.
    /// </remarks>
    private static async Task DownloadToAsync(
        string url, string tempPath, string name, int attempt, ILogger? logger,
        IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        long have = FileLength(tempPath);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        bool append;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            append = true;
            logger?.LogInformation("  {Name}: resuming from {Have:N0} bytes", name, have);
        }
        else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            logger?.LogWarning("  {Name}: cached partial file ({Have:N0} bytes) is not a valid range for the asset; restarting the download.", name, have);
            TryDelete(tempPath);
            have = 0;
            append = false;
            using var restart = new HttpRequestMessage(HttpMethod.Get, url);
            using var full = await Http.SendAsync(restart, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            full.EnsureSuccessStatusCode();
            await CopyAsync(full, tempPath, name, attempt, have, append, logger, progress, ct).ConfigureAwait(false);
            return;
        }
        else
        {
            response.EnsureSuccessStatusCode();
            if (have > 0)
                logger?.LogInformation("  {Name}: server ignored the range request; downloading the whole file.", name);
            have = 0;
            append = false;
        }

        await CopyAsync(response, tempPath, name, attempt, have, append, logger, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Streams a response body to disk, reporting progress against the asset's full size.</summary>
    private static async Task CopyAsync(
        HttpResponseMessage response, string tempPath, string name, int attempt, long resumedFrom, bool append,
        ILogger? logger, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        // Content-Length covers only the bytes still to come; the asset's real size includes what is
        // already on disk (Content-Range reports it directly when the server sent one).
        long? remaining = response.Content.Headers.ContentLength;
        long? total = response.Content.Headers.ContentRange?.Length ?? (remaining is { } r ? resumedFrom + r : null);

        long downloaded = resumedFrom;
        var lastReport = DateTime.UtcNow;
        Report(logger, progress, name, downloaded, total, resumedFrom, attempt);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(
            tempPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;

            if ((DateTime.UtcNow - lastReport).TotalSeconds >= 2)
            {
                Report(logger, progress, name, downloaded, total, resumedFrom, attempt);
                lastReport = DateTime.UtcNow;
            }
        }

        // Flush before the length check so the on-disk size is what we just measured.
        await file.FlushAsync(ct).ConfigureAwait(false);

        if (total is > 0 && downloaded != total)
        {
            // Keep the bytes: the caller retries and resumes from here.
            throw new IOException($"Download of {name} ended early ({downloaded:N0} of {total:N0} bytes).");
        }

        Report(logger, progress, name, downloaded, total, resumedFrom, attempt);
    }

    private static void Report(
        ILogger? logger, IProgress<ModelDownloadProgress>? progress,
        string name, long downloaded, long? total, long resumedFrom, int attempt)
    {
        if (logger is not null) ReportProgress(logger, name, downloaded, total ?? -1L);
        progress?.Report(new ModelDownloadProgress(name, downloaded, total, resumedFrom, attempt));
    }

    /// <summary>Length of a file, or 0 when it does not exist or cannot be measured.</summary>
    private static long FileLength(string path)
    {
        try { var info = new FileInfo(path); return info.Exists ? info.Length : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static string ResolveUrl(ModelAsset asset)
    {
        var baseOverride = Environment.GetEnvironmentVariable(BaseUrlEnvVar);
        var url = string.IsNullOrWhiteSpace(baseOverride)
            ? asset.Url
            : $"{baseOverride.TrimEnd('/')}/{asset.FileName}";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new LayoutSharpException($"Model URL '{url}' is not a valid absolute URI.");

        bool https = uri.Scheme == Uri.UriSchemeHttps;
        bool loopbackHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        if (!https && !loopbackHttp)
            throw new LayoutSharpException(
                $"Refusing to download model over '{uri.Scheme}' from {url}: only HTTPS (or HTTP to loopback for local mirrors) is allowed.");

        return url;
    }

    /// <summary>Guards against path traversal in an asset name (they are internal constants, so this is a tripwire).</summary>
    private static string SafeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName != Path.GetFileName(fileName)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new LayoutSharpException($"Invalid model file name '{fileName}'.");
        }
        return fileName;
    }

    private static void ReportProgress(ILogger logger, string name, long downloaded, long total)
    {
        if (total > 0)
        {
            var pct = downloaded * 100.0 / total;
            logger.LogInformation("  {Name}: {Downloaded:N0} / {Total:N0} bytes ({Pct:F1}%)", name, downloaded, total, pct);
        }
        else
        {
            logger.LogInformation("  {Name}: {Downloaded:N0} bytes", name, downloaded);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Returns the absolute path to a cached copy of <paramref name="spec"/>'s detector model,
    /// downloading it if not already present (see <see cref="EnsureModelAsync(ModelAsset, string?, bool, ILogger?, CancellationToken, IProgress{ModelDownloadProgress})"/>).
    /// </summary>
    public static Task<string> EnsureModelAsync(
        LayoutModelSpec spec,
        string? customCachePath,
        bool offline,
        ILogger? logger,
        CancellationToken cancellationToken,
        IProgress<ModelDownloadProgress>? progress = null)
        => spec.LocalPath is { } local
            ? EnsureLocalModelAsync(spec, local, logger, cancellationToken)
            : EnsureModelAsync(spec.Asset, customCachePath, offline, logger, cancellationToken, progress);

    /// <summary>The on-disk path an asset would occupy in the given cache (whether or not it exists yet).</summary>
    public static string GetModelPath(ModelAsset asset, string? customCachePath)
        => Path.Combine(ResolveCacheDirectory(customCachePath), SafeFileName(asset.FileName));

    /// <summary>
    /// Resolves a bring-your-own model: the file must exist at <paramref name="localPath"/>, and
    /// when the spec carries a SHA-256 it is verified fail-closed. The user's file is never deleted
    /// on mismatch. Runs once per engine because the session is cached afterwards.
    /// </summary>
    private static async Task<string> EnsureLocalModelAsync(
        LayoutModelSpec spec, string localPath, ILogger? logger, CancellationToken cancellationToken)
    {
        if (!File.Exists(localPath))
            throw new LayoutSharpException(
                $"Custom layout model not found at '{localPath}'. CustomLayoutModel.Path must point to an existing ONNX file " +
                "(LayoutSharp never downloads custom models).");

        if (!string.IsNullOrEmpty(spec.Sha256))
        {
            var actual = await ComputeSha256Async(localPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, spec.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelChecksumException(
                    $"Custom layout model '{localPath}' failed SHA-256 verification " +
                    $"(expected {spec.Sha256}, got {actual}). The file was left in place; fix CustomLayoutModel.Sha256 " +
                    "or replace the file with the one the checksum was taken from.");
            }
            logger?.LogInformation("Custom layout model {Path} passed SHA-256 verification.", localPath);
        }

        logger?.LogInformation("Using custom layout model {Name} from {Path} (not cached, not downloaded).", spec.Name, localPath);
        return localPath;
    }
}
