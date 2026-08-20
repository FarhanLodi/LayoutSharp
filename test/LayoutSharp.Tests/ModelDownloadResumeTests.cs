using System.Net;
using System.Security.Cryptography;
using System.Text;
using LayoutSharp.Internal;
using LayoutSharp.Models;
using Xunit;

namespace LayoutSharp.Tests;

/// <summary>
/// The download path against a real (loopback) HTTP server: streaming, progress reporting, retry,
/// resume-from-partial after an interrupted transfer, and every way a resume can go wrong.
/// </summary>
/// <remarks>
/// Loopback HTTP is allowed by <see cref="ModelDownloadManager"/> precisely so this is testable
/// without the network; the assets are a few KB of deterministic bytes, not real models.
/// </remarks>
[Collection("EnvironmentVariables")]
public sealed class ModelDownloadResumeTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "layoutsharp-dl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true); } catch (IOException) { }
    }

    private static byte[] Payload(int size)
    {
        var bytes = new byte[size];
        for (int i = 0; i < size; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>A one-file HTTP server that can serve ranges, truncate a response, or fail outright.</summary>
    private sealed class Server : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _body;
        private readonly Func<int, ServerBehaviour> _behaviour;
        private int _requests;

        public Server(byte[] body, Func<int, ServerBehaviour> behaviour)
        {
            _body = body;
            _behaviour = behaviour;
            int port = GetFreePort();
            Prefix = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public string Prefix { get; }
        public int Requests => Volatile.Read(ref _requests);
        public List<(long From, long? To)> Ranges { get; } = new();

        private static int GetFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }

                int n = Interlocked.Increment(ref _requests);
                var behaviour = _behaviour(n);

                long from = 0;
                var rangeHeader = ctx.Request.Headers["Range"];
                if (rangeHeader is not null && rangeHeader.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    var spec = rangeHeader["bytes=".Length..];
                    var parts = spec.Split('-');
                    from = long.Parse(parts[0]);
                    lock (Ranges) Ranges.Add((from, parts.Length > 1 && parts[1].Length > 0 ? long.Parse(parts[1]) : null));
                }

                try
                {
                    if (behaviour == ServerBehaviour.Fail500)
                    {
                        ctx.Response.StatusCode = 500;
                        ctx.Response.Close();
                        continue;
                    }

                    bool honourRange = behaviour != ServerBehaviour.IgnoreRange && from > 0;
                    if (honourRange && from >= _body.Length)
                    {
                        ctx.Response.StatusCode = 416;
                        ctx.Response.Headers["Content-Range"] = $"bytes */{_body.Length}";
                        ctx.Response.Close();
                        continue;
                    }

                    long start = honourRange ? from : 0;
                    var slice = _body.AsMemory((int)start);

                    if (honourRange)
                    {
                        ctx.Response.StatusCode = 206;
                        ctx.Response.Headers["Content-Range"] = $"bytes {start}-{_body.Length - 1}/{_body.Length}";
                    }

                    // Truncated: promise the full length, then send half and hang up.
                    int send = behaviour == ServerBehaviour.Truncate ? slice.Length / 2 : slice.Length;
                    ctx.Response.ContentLength64 = slice.Length;
                    await ctx.Response.OutputStream.WriteAsync(slice[..send]).ConfigureAwait(false);
                    await ctx.Response.OutputStream.FlushAsync().ConfigureAwait(false);
                    ctx.Response.Abort();
                }
                catch (HttpListenerException) { }
                catch (InvalidOperationException) { }
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); ((IDisposable)_listener).Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    private enum ServerBehaviour { Full, Truncate, Fail500, IgnoreRange }

    private Task<string> Ensure(Server server, string fileName, string sha, IProgress<ModelDownloadProgress>? progress = null)
    {
        Environment.SetEnvironmentVariable(ModelDownloadManager.BaseUrlEnvVar, server.Prefix.TrimEnd('/'));
        var asset = new ModelAsset(fileName, sha);
        return ModelDownloadManager.EnsureModelAsync(asset, _cache, offline: false, logger: null, CancellationToken.None, progress);
    }

    [Fact]
    public async Task Download_StreamsFileAndReportsProgressToCompletion()
    {
        var body = Payload(300_000);
        using var server = new Server(body, _ => ServerBehaviour.Full);
        var reports = new List<ModelDownloadProgress>();

        var path = await Ensure(server, "full.onnx", Sha(body), new Progress<ModelDownloadProgress>(reports.Add));

        Assert.Equal(body, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".part"));                       // .part renamed away
        await Task.Delay(50);                                            // Progress<T> posts asynchronously
        Assert.NotEmpty(reports);
        var last = reports[^1];
        Assert.Equal("full.onnx", last.FileName);
        Assert.Equal(body.Length, last.BytesDownloaded);
        Assert.Equal(body.Length, last.TotalBytes);
        Assert.Equal(100, last.PercentComplete!.Value, precision: 3);
        Assert.True(last.IsComplete);
        Assert.False(last.IsResumed);
    }

    [Fact]
    public async Task InterruptedDownload_ResumesFromTheBytesAlreadyOnDisk()
    {
        var body = Payload(400_000);
        // First response is truncated half way; the retry must ask for the rest, not the whole file.
        using var server = new Server(body, n => n == 1 ? ServerBehaviour.Truncate : ServerBehaviour.Full);
        var reports = new List<ModelDownloadProgress>();

        var path = await Ensure(server, "resume.onnx", Sha(body), new Progress<ModelDownloadProgress>(reports.Add));

        Assert.Equal(body, await File.ReadAllBytesAsync(path));           // bytes are correct end to end
        Assert.Equal(2, server.Requests);                                 // exactly one retry
        long resumedFrom;
        lock (server.Ranges)
        {
            var resumed = Assert.Single(server.Ranges);                     // exactly one range request
            // How much of the truncated response reached disk is up to socket buffering, so the
            // contract is "continue from whatever is there", not a fixed offset.
            Assert.InRange(resumed.From, 1, body.Length - 1);
            resumedFrom = resumed.From;
        }
        await Task.Delay(50);
        Assert.Contains(reports, r => r.IsResumed && r.ResumedFromBytes == resumedFrom);
        Assert.Contains(reports, r => r.Attempt == 2);
    }

    [Fact]
    public async Task ResumeIsSkipped_WhenTheServerIgnoresTheRangeRequest()
    {
        var body = Payload(200_000);
        // Truncate once, then reply 200 with the whole body even though a range was requested.
        using var server = new Server(body, n => n == 1 ? ServerBehaviour.Truncate : ServerBehaviour.IgnoreRange);

        var path = await Ensure(server, "ignored-range.onnx", Sha(body));

        Assert.Equal(body, await File.ReadAllBytesAsync(path));            // restarted cleanly, no duplicated prefix
    }

    [Fact]
    public async Task StalePartialFile_LongerThanTheAsset_IsDiscardedAndRefetched()
    {
        var body = Payload(50_000);
        using var server = new Server(body, _ => ServerBehaviour.Full);

        Directory.CreateDirectory(_cache);
        var partPath = Path.Combine(_cache, "stale.onnx.part");
        await File.WriteAllBytesAsync(partPath, Payload(80_000));          // longer than the real asset -> 416

        var path = await Ensure(server, "stale.onnx", Sha(body));

        Assert.Equal(body, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task CorruptPartialFile_FailsChecksumOnce_ThenRedownloadsFromScratch()
    {
        var body = Payload(120_000);
        using var server = new Server(body, _ => ServerBehaviour.Full);

        Directory.CreateDirectory(_cache);
        // Same length as a plausible partial, but wrong bytes: the resumed file hashes wrong, so the
        // manager must discard it and start over rather than caching corruption.
        await File.WriteAllBytesAsync(Path.Combine(_cache, "corrupt.onnx.part"), Encoding.ASCII.GetBytes(new string('x', 60_000)));

        var path = await Ensure(server, "corrupt.onnx", Sha(body));

        Assert.Equal(body, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task PermanentFailure_KeepsThePartialFileSoALaterCallCanResume()
    {
        var body = Payload(100_000);
        using var server = new Server(body, n => n == 1 ? ServerBehaviour.Truncate : ServerBehaviour.Fail500);

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() => Ensure(server, "keep.onnx", Sha(body)));

        var partPath = Path.Combine(_cache, "keep.onnx.part");
        Assert.True(File.Exists(partPath));                                // bytes retained for next time
        Assert.InRange(new FileInfo(partPath).Length, 1, body.Length - 1);  // a real partial, not empty or complete
        Assert.Contains("resumes from there", ex.Message);
        Assert.Contains(server.Prefix, ex.Url);
    }

    [Fact]
    public async Task ChecksumMismatch_DeletesThePartialFileAndThrows()
    {
        var body = Payload(30_000);
        using var server = new Server(body, _ => ServerBehaviour.Full);

        await Assert.ThrowsAsync<ModelChecksumException>(
            () => Ensure(server, "wrong-sha.onnx", Sha(Payload(30_001))));

        Assert.False(File.Exists(Path.Combine(_cache, "wrong-sha.onnx.part")));
        Assert.False(File.Exists(Path.Combine(_cache, "wrong-sha.onnx")));
    }

    [Fact]
    public async Task AlreadyCachedModel_IsNotDownloadedAgain()
    {
        var body = Payload(10_000);
        using var server = new Server(body, _ => ServerBehaviour.Full);

        var first = await Ensure(server, "cached.onnx", Sha(body));
        var second = await Ensure(server, "cached.onnx", Sha(body));

        Assert.Equal(first, second);
        Assert.Equal(1, server.Requests);                                  // second call served from disk
    }
}
