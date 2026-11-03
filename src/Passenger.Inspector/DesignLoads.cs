using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Markup.Xaml;

namespace Passenger.Inspector;

/// <summary>Avalonia's designer, a XAML file's previewer, made to record where in the file each load makes each element, so
/// the inspector can say where the frame shows it: the runtime XAML loader it takes from Avalonia's locator for each load
/// wrapped, each load's configuration asked for source information, its document named as the file. Avalonia keeps the loader's interface its own, so the
/// wrap is a proxy of it, bound by reflection. On the designer's UI thread, once; a designer that loads otherwise keeps its
/// own loader, and one whose wrap fails runs on as it was.</summary>
internal static class DesignLoads
{
    /// <summary>The file the designer previews, which each load is recorded as made from.</summary>
    private static string? file;

    /// <summary>What the designer's last load made, while it lives.</summary>
    private static WeakReference<object>? last;

    /// <summary>What the designer's last load made, while it lives: the design view the frame shows.</summary>
    public static object? Last => last is { } made && made.TryGetTarget(out object? root) ? root : null;

    /// <param name="previewed">The file the designer previews (<see cref="Wire.PreviewVariable"/>).</param>
    public static void Wrap(string previewed)
    {
        file = previewed;
        try
        {
            Type loaders = typeof(AvaloniaXamlLoader).GetNestedType("IRuntimeXamlLoader", BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException("Avalonia's AvaloniaXamlLoader no longer has its IRuntimeXamlLoader");
            if (AvaloniaLocator.Current.GetService(loaders) is not { } loader || loader is Sourced)
            {
                InspectorTrace.Step("the designer has no runtime XAML loader to wrap");
                return;
            }
            object wrapped = DispatchProxy.Create(loaders, typeof(Sourced));
            ((Sourced)wrapped).Inner = loader;
            object binding = typeof(AvaloniaLocator).GetMethod(nameof(AvaloniaLocator.Bind))!.MakeGenericMethod(loaders).Invoke(AvaloniaLocator.CurrentMutable, null)!;
            binding.GetType().GetMethod("ToConstant")!.MakeGenericMethod(loaders).Invoke(binding, [wrapped]);
            InspectorTrace.Step($"wrapped the designer's {loader.GetType().Name}, so its loads record where they make each element");
        }
        catch (Exception error) when (error is InvalidOperationException or MemberAccessException or TypeLoadException or TargetInvocationException
            or ArgumentException or NotSupportedException)
        {
            InspectorTrace.Step($"the designer's loader could not be wrapped: {error}");
        }
    }

    /// <summary>The designer's loader, each load asked to record where it made each element; what it throws (a XAML error the
    /// designer shows with its line) thrown as it was.</summary>
    internal class Sourced : DispatchProxy
    {
        public object? Inner { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is [RuntimeXamlLoaderDocument document, RuntimeXamlLoaderConfiguration configuration])
            {
                configuration.CreateSourceInfo = true;
                document.Document ??= file;
            }
            try
            {
                object? made = targetMethod!.Invoke(Inner, args);
                last = made is null ? null : new WeakReference<object>(made);
                return made;
            }
            catch (TargetInvocationException thrown) when (thrown.InnerException is { } inner)
            {
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }
        }
    }
}
