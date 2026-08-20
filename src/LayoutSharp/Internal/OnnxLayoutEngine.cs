using LayoutSharp.Models;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LayoutSharp.Internal;

/// <summary>
/// Owns the layout-detector ONNX session: lazily ensures the model is downloaded, builds the
/// session (CPU or CUDA), and runs detection. Thread-safe and reusable; create one per process.
/// </summary>
internal sealed class OnnxLayoutEngine : ILayoutDetector
{
    private readonly LayoutModelSpec _spec;
    private readonly string? _cachePath;
    private readonly bool _useGpu;
    private readonly bool _offline;
    private readonly ILogger? _logger;
    private readonly int? _intraOpThreads;
    private readonly IProgress<Models.ModelDownloadProgress>? _progress;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private IDetectionSession? _detector;
    private bool _disposed;

    public OnnxLayoutEngine(LayoutModelSpec spec, string? cachePath, bool useGpu, bool offline, ILogger? logger, int? intraOpThreads = null, IProgress<Models.ModelDownloadProgress>? progress = null)
    {
        _spec = spec;
        _cachePath = cachePath;
        _useGpu = useGpu;
        _offline = offline;
        _logger = logger;
        _intraOpThreads = intraOpThreads;
        _progress = progress;
    }

    /// <inheritdoc />
    public LayoutModel Model => _spec.Model;

    /// <inheritdoc />
    public bool IsGpu { get; private set; }

    /// <inheritdoc />
    public Task WarmUpAsync(CancellationToken cancellationToken) => EnsureDetectorAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RawDetection>> DetectAsync(
        Image<Rgb24> image, float scoreThreshold, CancellationToken cancellationToken)
    {
        var detector = await EnsureDetectorAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return detector.Detect(image, scoreThreshold);
        }
        catch (OnnxRuntimeException ex)
        {
            throw new LayoutInferenceException(
                $"Layout inference failed for model {_spec.FileName} ({_spec.Family}): {ex.Message}", ex);
        }
    }

    private async Task<IDetectionSession> EnsureDetectorAsync(CancellationToken cancellationToken)
    {
        if (_detector is not null) return _detector;
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_detector is not null) return _detector;
            ObjectDisposedException.ThrowIf(_disposed, this);

            var modelPath = await ModelDownloadManager
                .EnsureModelAsync(_spec, _cachePath, _offline, _logger, cancellationToken, _progress)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var (sessionOptions, gpu) = CreateSessionOptions();
            try
            {
                _detector = CreateSession(modelPath, _spec, sessionOptions);
            }
            catch (OnnxRuntimeException ex)
            {
                throw new LayoutInferenceException(
                    $"Failed to load layout model {_spec.FileName} from '{modelPath}': {ex.Message} " +
                    (_spec.LocalPath is null
                        ? "If the cached file is corrupt or truncated, delete it and it will be re-downloaded."
                        : "Check that the custom model is a valid ONNX file and that CustomLayoutModel.OutputContract matches its export."), ex);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                throw new LayoutInferenceException(
                    "ONNX Runtime native library could not be loaded. Ensure the Microsoft.ML.OnnxRuntime " +
                    "(or Microsoft.ML.OnnxRuntime.Gpu) package's native assets are deployed for this platform.", ex);
            }

            IsGpu = gpu;
            _logger?.LogInformation("Layout detector ready: {Model} ({Family}, {Size}px, {Classes} classes) on {Provider}.",
                _spec.FileName, _spec.Family, _spec.InputSize, _spec.ClassCount, gpu ? "CUDA" : "CPU");
            return _detector;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Instantiates the decoder that matches the spec's graph contract. The shipped detector is a raw
    /// DETR head; the other arms exist for bring-your-own models (see <see cref="Models.CustomLayoutModel"/>).
    /// </summary>
    internal static IDetectionSession CreateSession(string modelPath, LayoutModelSpec spec, SessionOptions sessionOptions)
        => spec.Kind switch
        {
            DetectorKind.Detr => new DetrLayoutDetector(modelPath, spec, sessionOptions),
            DetectorKind.PaddleDetection => new PaddleLayoutDetector(modelPath, spec, sessionOptions),
            DetectorKind.YoloEndToEnd => new YoloLayoutDetector(modelPath, spec, sessionOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Kind, "Unknown detector kind."),
        };

    private (SessionOptions Options, bool Gpu) CreateSessionOptions()
        => OnnxSessionFactory.CreateSessionOptions(_useGpu, _logger, _intraOpThreads);

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _detector?.Dispose();
        _detector = null;
        _initLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
