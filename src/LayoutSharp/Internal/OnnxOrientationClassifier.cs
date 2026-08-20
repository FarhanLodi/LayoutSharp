using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LayoutSharp.Internal;

/// <summary>
/// Runs PP-LCNet_x1_0_doc_ori (<see cref="ModelRegistry.DocOrientation"/>): lazily downloads and
/// verifies the model through <see cref="ModelDownloadManager"/>, builds the session (CPU, or CUDA
/// with CPU fallback) and classifies a page as rotated 0/90/180/270 degrees clockwise. Thread-safe
/// and reusable; ~4-5 ms per page on CPU.
/// </summary>
/// <remarks>
/// Contract (verified empirically, 12/12 on the test fixtures): input <c>x float32 [1,3,224,224]</c>
/// preprocessed with <see cref="ImageProcessing.PreprocessCenterCrop"/>; single output
/// <c>float32 [1,4]</c> that already went through Softmax; index <c>i</c> means the content is
/// rotated <c>i*90</c> degrees clockwise, so the page is made upright by rotating it
/// <c>(360 - i*90) % 360</c> degrees clockwise.
/// </remarks>
internal sealed class OnnxOrientationClassifier : IOrientationClassifier
{
    /// <summary>Short-side size the page is resized to before the centre crop.</summary>
    public const int ResizeShortSide = 256;

    /// <summary>Network input size (square centre crop).</summary>
    public const int InputSize = 224;

    private readonly string? _cachePath;
    private readonly bool _useGpu;
    private readonly bool _offline;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private InferenceSession? _session;
    private string _inputName = "x";
    private bool _disposed;

    public OnnxOrientationClassifier(string? cachePath, bool useGpu, bool offline, ILogger? logger)
    {
        _cachePath = cachePath;
        _useGpu = useGpu;
        _offline = offline;
        _logger = logger;
    }

    /// <summary>True once the session was created on a GPU execution provider.</summary>
    public bool IsGpu { get; private set; }

    /// <inheritdoc />
    public Task WarmUpAsync(CancellationToken cancellationToken) => EnsureSessionAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<OrientationPrediction> ClassifyAsync(Image<Rgb24> image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var pixels = ImageProcessing.PreprocessCenterCrop(image, ResizeShortSide, InputSize);
        var inputs = new List<NamedOnnxValue>(1)
        {
            NamedOnnxValue.CreateFromTensor(_inputName, new DenseTensor<float>(pixels, new[] { 1, 3, InputSize, InputSize })),
        };

        float[] probs;
        try
        {
            using var results = session.Run(inputs);
            probs = ReadProbabilities(results);
        }
        catch (OnnxRuntimeException ex)
        {
            throw new LayoutInferenceException(
                $"Orientation inference failed for model {ModelRegistry.DocOrientation.FileName}: {ex.Message}", ex);
        }

        return Decode(probs);
    }

    /// <summary>Argmax over the 4 class probabilities; softmax is applied only if the graph did not already do so.</summary>
    internal static OrientationPrediction Decode(ReadOnlySpan<float> logitsOrProbs)
    {
        if (logitsOrProbs.Length < 4)
            throw new LayoutSharpException($"Orientation model produced {logitsOrProbs.Length} values; expected 4 (0/90/180/270).");

        Span<float> p = stackalloc float[4];
        logitsOrProbs[..4].CopyTo(p);

        float sum = 0f;
        bool looksLikeProbs = true;
        for (int i = 0; i < 4; i++)
        {
            if (p[i] < 0f || p[i] > 1f || float.IsNaN(p[i])) looksLikeProbs = false;
            sum += p[i];
        }
        if (!looksLikeProbs || Math.Abs(sum - 1f) > 1e-2f)
        {
            float max = float.NegativeInfinity;
            for (int i = 0; i < 4; i++) max = Math.Max(max, p[i]);
            float denom = 0f;
            for (int i = 0; i < 4; i++) { p[i] = MathF.Exp(p[i] - max); denom += p[i]; }
            for (int i = 0; i < 4; i++) p[i] /= denom;
        }

        int best = 0;
        for (int i = 1; i < 4; i++) if (p[i] > p[best]) best = i;
        return new OrientationPrediction(best * 90, p[best], p[0], p[1], p[2], p[3]);
    }

    private static float[] ReadProbabilities(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        foreach (var r in results)
        {
            if (r.ElementType != TensorElementType.Float) continue;
            var t = r.AsTensor<float>();
            if (t.Length >= 4) return t.ToArray();
        }
        throw new LayoutSharpException(
            "Orientation model did not produce the expected [1, 4] probability tensor. Verify the exported ONNX graph's outputs.");
    }

    private async Task<InferenceSession> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_session is not null) return _session;
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null) return _session;
            ObjectDisposedException.ThrowIf(_disposed, this);

            var asset = ModelRegistry.DocOrientation;
            var modelPath = await ModelDownloadManager
                .EnsureModelAsync(asset, _cachePath, _offline, _logger, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var (sessionOptions, gpu) = OnnxSessionFactory.CreateSessionOptions(_useGpu, _logger);
            var session = OnnxSessionFactory.Load(
                () => new InferenceSession(modelPath, sessionOptions), asset.FileName, modelPath, "orientation model");

            // Bind the image input by name when the export uses PaddleClas' "x", else the first rank-4 input.
            string? input = null;
            foreach (var (name, meta) in session.InputMetadata)
            {
                if (name == "x" || (input is null && meta.Dimensions.Length == 4)) input = name;
            }
            _inputName = input ?? session.InputMetadata.Keys.First();

            _session = session;
            IsGpu = gpu;
            _logger?.LogInformation("Orientation classifier ready: {Model} on {Provider}.", asset.FileName, gpu ? "CUDA" : "CPU");
            return _session;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _session?.Dispose();
        _session = null;
        _initLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
