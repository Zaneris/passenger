using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using Passenger.Core;
using Passenger.UnitTests.Debugger;
using Passenger.UpdateHandlers;

[assembly: MetadataUpdateHandler(typeof(UpdateHandlerProbe))]

namespace Passenger.UnitTests.Debugger;

/// <summary>A handler counting its calls in its assembly's statics, which every load of the assembly holds apart.</summary>
internal static class UpdateHandlerProbe
{
    public static int Cleared { get; private set; }

    public static int Updated { get; private set; }

    public static Type[]? ClearedTypes { get; private set; }

    public static Type[]? UpdatedTypes { get; private set; }

    internal static void ClearCache(Type[]? types)
    {
        Cleared++;
        ClearedTypes = types;
    }

    internal static void UpdateApplication(Type[]? types)
    {
        Updated++;
        UpdatedTypes = types;
    }
}

/// <summary>A program's update handlers across its load contexts: a plugin's assembly loaded twice holds two sets of statics,
/// so a Hot Reload update reaches the handlers of both, in the order references ask for.</summary>
public sealed class UpdateHandlersTests
{
    private static (Assembly First, Assembly Second, Action Unload) LoadedTwice(Assembly assembly)
    {
        AssemblyLoadContext first = new("first", isCollectible: true);
        AssemblyLoadContext second = new("second", isCollectible: true);
        return (first.LoadFromAssemblyPath(assembly.Location), second.LoadFromAssemblyPath(assembly.Location), () =>
        {
            first.Unload();
            second.Unload();
        });
    }

    private static int Read(Assembly assembly, string count) =>
        (int)assembly.GetType(typeof(UpdateHandlerProbe).FullName!, throwOnError: true)!.GetProperty(count)!.GetValue(null)!;

    [Fact]
    public void An_assembly_loaded_in_two_contexts_has_the_handlers_of_both_called()
    {
        (Assembly first, Assembly second, Action unload) = LoadedTwice(typeof(UpdateHandlerProbe).Assembly);
        try
        {
            Assert.Empty(Handlers.Run([first, second]));
            foreach (Assembly each in (Assembly[])[first, second])
            {
                Assert.True((Read(each, nameof(UpdateHandlerProbe.Cleared)), Read(each, nameof(UpdateHandlerProbe.Updated))) == (1, 1),
                    $"The load in {AssemblyLoadContext.GetLoadContext(each)?.Name} kept its stale state: an update never reached its handlers");
            }
        }
        finally
        {
            unload();
        }
    }

    [Fact]
    public void Changed_types_reach_both_callbacks_in_every_load_context()
    {
        Type probe = typeof(UpdateHandlerProbe);
        (Assembly first, Assembly second, Action unload) = LoadedTwice(probe.Assembly);
        try
        {
            ChangedTypes changes = new();
            string line = Wire.TypeLine(probe.Module.ModuleVersionId, probe.MetadataToken);
            changes.Add(line);
            changes.Add(line);
            Type[] expected = [first.GetType(probe.FullName!)!, second.GetType(probe.FullName!)!];
            Type[]? resolved = changes.Take([first, second]);
            Assert.Equal(expected, resolved);
            Assert.Empty(Handlers.Run([first, second], resolved));
            foreach (Type loaded in expected)
            {
                Assert.Equal(expected, (Type[]?)loaded.GetProperty(nameof(UpdateHandlerProbe.ClearedTypes))!.GetValue(null));
                Assert.Equal(expected, (Type[]?)loaded.GetProperty(nameof(UpdateHandlerProbe.UpdatedTypes))!.GetValue(null));
            }
            Assert.Empty(changes.Take([first, second])!);
        }
        finally
        {
            unload();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unresolved_types_invalidate_all_caches_and_do_not_leak_into_the_next_batch(bool missingModule)
    {
        Type probe = typeof(UpdateHandlerProbe);
        ChangedTypes changes = new();
        changes.Add(Wire.TypeLine(missingModule ? Guid.Empty : probe.Module.ModuleVersionId, missingModule ? probe.MetadataToken : 0x020FFFFF));
        Assert.Null(changes.Take([probe.Assembly]));
        changes.Add(Wire.TypeLine(probe.Module.ModuleVersionId, probe.MetadataToken));
        Assert.Equal([probe], changes.Take([probe.Assembly]));
    }

    [Fact]
    public void Each_load_of_a_referenced_assembly_comes_before_what_references_it()
    {
        Assembly referencing = typeof(UpdateHandlerProbe).Assembly;
        (Assembly first, Assembly second, Action unload) = LoadedTwice(typeof(Product).Assembly);
        try
        {
            Assert.Equal([first, second, referencing], Handlers.Ordered([referencing, first, second]));
        }
        finally
        {
            unload();
        }
    }
}
