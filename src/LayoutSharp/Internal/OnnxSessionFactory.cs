using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LayoutSharp.Internal;

/// <summary>
/// Shared ONNX Runtime session plumbing used by every model the library loads (layout detector,
/// document-orientation classifier): execution-provider selection with CPU fallback, and the
/// mapping of native load failures to <see cref="LayoutInferenceException"/>.
/// </summary>
internal static class OnnxSessionFactory
{
    /// <summary>
    /// Builds session options for the requested provider. When <paramref name="useGpu"/> is set and
    /// the CUDA provider cannot be appended (no GPU package / no CUDA runtime), logs a warning and
    /// falls back to CPU rather than failing.
    /// </summary>
    /// <returns>The options and whether the CUDA provider was actually enabled.</returns>
    public static (SessionOptions Options, bool Gpu) CreateSessionOptions(bool useGpu, ILogger? logger, int? intraOpThreads = null)
    {
        if (!useGpu) return (Tuned(intraOpThreads), false);

        try
        {
            var options = Tuned(intraOpThreads);
            options.AppendExecutionProvider_CUDA();
            logger?.LogInformation("CUDA execution provider enabled.");
            return (options, true);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "CUDA execution provider unavailable; falling back to CPU. " +
                "Reference the Microsoft.ML.OnnxRuntime.Gpu package in your application (and install the matching CUDA/cuDNN runtime) for GPU acceleration.");
            return (Tuned(intraOpThreads), false);
        }
    }

    /// <summary>
    /// Session options tuned for one-image-at-a-time detection. Full graph optimization plus a
    /// sequential outer loop measured ~17 % faster than the defaults on a 12-core CPU (486 → 404 ms
    /// per page with the shipped detector): the graph is one deep chain, so parallelising across
    /// nodes only costs synchronisation, while the per-node kernels still use every intra-op thread.
    /// <paramref name="intraOpThreads"/> caps those threads — leave it null for one page at a time,
    /// set it low (2–4) when many pages are analyzed concurrently so the sessions stop fighting.
    /// </summary>
    private static SessionOptions Tuned(int? intraOpThreads)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        if (intraOpThreads is > 0) options.IntraOpNumThreads = intraOpThreads.Value;
        return options;
    }

    /// <summary>
    /// Runs <paramref name="create"/> and translates ONNX Runtime / native-library load failures
    /// into <see cref="LayoutInferenceException"/> with an actionable message.
    /// </summary>
    public static T Load<T>(Func<T> create, string fileName, string modelPath, string what = "model")
    {
        try
        {
            return create();
        }
        catch (OnnxRuntimeException ex)
        {
            throw new LayoutInferenceException(
                $"Failed to load {what} {fileName} from '{modelPath}': {ex.Message} " +
                "If the cached file is corrupt or truncated, delete it and it will be re-downloaded.", ex);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new LayoutInferenceException(
                "ONNX Runtime native library could not be loaded. Ensure the Microsoft.ML.OnnxRuntime " +
                "(or Microsoft.ML.OnnxRuntime.Gpu) package's native assets are deployed for this platform.", ex);
        }
    }
}
