using System.Diagnostics.CodeAnalysis;
using LayoutSharp.Recognition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LayoutSharp.Services;

/// <summary>
/// Dependency-injection helpers for registering LayoutSharp.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ILayoutService"/> (implemented by <see cref="LayoutService"/>) as a
    /// singleton. If an <see cref="ITextRecognizer"/> is registered in the container it is picked
    /// up automatically and text-bearing regions are recognized; otherwise the service runs
    /// layout-only. ONNX sessions are expensive to create and thread-safe to reuse, so a singleton
    /// is the right lifetime.
    /// </summary>
    public static IServiceCollection AddLayoutSharp(
        this IServiceCollection services,
        Action<LayoutServiceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LayoutServiceOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton<ILayoutService>(sp =>
            new LayoutService(
                options,
                sp.GetService<ITextRecognizer>(),
                sp.GetService<ILogger<LayoutService>>(),
                sp.GetService<ITableRecognizer>(),
                sp.GetService<IFormulaRecognizer>()));

        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="TRecognizer"/> as the <see cref="ITextRecognizer"/> (if none is
    /// registered yet) and then <see cref="ILayoutService"/> as a singleton that uses it.
    /// </summary>
    public static IServiceCollection AddLayoutSharp<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TRecognizer>(
        this IServiceCollection services,
        Action<LayoutServiceOptions>? configure = null)
        where TRecognizer : class, ITextRecognizer
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ITextRecognizer, TRecognizer>();
        return services.AddLayoutSharp(configure);
    }

    /// <summary>
    /// Registers <typeparamref name="TRecognizer"/> as the <see cref="ITableRecognizer"/> (if none is
    /// registered yet). <see cref="AddLayoutSharp"/> picks it up — in either order — so table blocks
    /// carry a <see cref="Models.LayoutBlock.Table"/>.
    /// </summary>
    public static IServiceCollection AddLayoutSharpTableRecognizer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TRecognizer>(
        this IServiceCollection services)
        where TRecognizer : class, ITableRecognizer
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ITableRecognizer, TRecognizer>();
        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="TRecognizer"/> as the <see cref="IFormulaRecognizer"/> (if none is
    /// registered yet). <see cref="AddLayoutSharp"/> picks it up — in either order — so formula blocks
    /// carry a <see cref="Models.LayoutBlock.Latex"/>.
    /// </summary>
    public static IServiceCollection AddLayoutSharpFormulaRecognizer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TRecognizer>(
        this IServiceCollection services)
        where TRecognizer : class, IFormulaRecognizer
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IFormulaRecognizer, TRecognizer>();
        return services;
    }
}
