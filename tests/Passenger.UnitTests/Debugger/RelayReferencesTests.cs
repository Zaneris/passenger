using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Passenger.Debugger;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Interop.Extensions;
using Passenger.Debugger.Relay;

namespace Passenger.UnitTests.Debugger;

/// <summary>The engine's hold on an app's objects through the relay: each object let go in the app once the engine holds
/// none of it, the objects the app's debugger keeps anyway held for the session, what an object said asked of the app
/// once, for as long as it holds (a method's function until an update makes its next version, a value's size for good, its
/// bytes read and written at that size alone), the metadata an update takes to be there made in the app first, the update
/// applied to the host's copy of the module's metadata once the app took it, a module's metadata read from the app's
/// memory opened on the host only as a module's metadata is laid out, and a callback read as the relay sends one or not at
/// all.</summary>
public class RelayReferencesTests
{
    [Fact]
    public void A_stops_objects_are_let_go_in_the_app_once_the_engine_lets_go_of_them()
    {
        App app = new();
        RelaySession session = new(app, new DeviceModules(null), null);
        nint thread = session.Take(new RemotePointer(0x1000, 0x1000), RemoteInterfaces.Of(typeof(ICorDebugThread)));
        nint value = session.Take(new RemotePointer(0x2000, 0x2000), RemoteInterfaces.Of(typeof(ICorDebugValue)));
        Marshal.AddRef(thread);
        Marshal.Release(value);
        Marshal.Release(thread);
        Assert.True(app.Released.SequenceEqual([0x2000ul]), $"With a value let go and a thread still held, the app was told to let go of {string.Join(", ", app.Released)}");
        Marshal.Release(thread);
        Assert.True(app.Released.SequenceEqual([0x2000ul, 0x1000ul]), "The thread was not let go in the app once the engine let go of it");
        Assert.True(session.ObjectsHeld == 0, $"{session.ObjectsHeld} objects are still held once the engine let go of all of them");
    }

    [Fact]
    public void A_second_pointer_to_an_interface_held_lets_its_reference_go_at_once()
    {
        App app = new();
        RelaySession session = new(app, new DeviceModules(null), null);
        nint first = session.Take(new RemotePointer(0x2000, 0x2008), RemoteInterfaces.Of(typeof(ICorDebugValue)));
        nint second = session.Take(new RemotePointer(0x2000, 0x2008), RemoteInterfaces.Of(typeof(ICorDebugValue)));
        Assert.True(first == second && app.Released.SequenceEqual([0x2000ul]), "A second pointer to a value held kept its reference in the app");
        Marshal.Release(first);
        Marshal.Release(second);
        Assert.True(app.Released.SequenceEqual([0x2000ul, 0x2000ul]), "The value's own reference was not let go");
    }

    [Fact]
    public void A_class_is_held_for_the_session_and_its_token_asked_of_the_app_once()
    {
        App app = new();
        RelaySession session = new(app, new DeviceModules(null), null);
        nint block = session.Take(new RemotePointer(0x3000, 0x3000), RemoteInterfaces.Of(typeof(ICorDebugClass)));
        ICorDebugClass @class = (ICorDebugClass)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(block, CreateObjectFlags.UniqueInstance);
        Assert.Equal(0, @class.TryGetToken(out mdTypeDef first));
        session.EndStop();
        Assert.Equal(0, @class.TryGetToken(out mdTypeDef second));
        Assert.True(first == second && app.Asked(typeof(ICorDebugClass), nameof(ICorDebugClass.TryGetToken)) == 1, $"A class's token was asked of the app {app.Asked(typeof(ICorDebugClass), nameof(ICorDebugClass.TryGetToken))} times");
        Marshal.Release(block);
        Assert.True(app.Released.Count == 0, "A class the app's debugger keeps for its module was let go, losing what it said");
    }

    [Fact]
    public void What_a_value_said_holds_until_the_program_runs()
    {
        App app = new();
        RelaySession session = new(app, new DeviceModules(null), null);
        nint block = session.Take(new RemotePointer(0x4000, 0x4000), RemoteInterfaces.Of(typeof(ICorDebugValue)));
        ICorDebugValue value = (ICorDebugValue)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(block, CreateObjectFlags.UniqueInstance);
        Assert.Equal(0, value.TryGetType(out _));
        Assert.Equal(0, value.TryGetType(out _));
        Assert.True(app.Asked(typeof(ICorDebugValue), nameof(ICorDebugValue.TryGetType)) == 1, "A value's type was asked of the app again within one stop");
        session.EndStop();
        Assert.Equal(0, value.TryGetType(out _));
        Assert.True(app.Asked(typeof(ICorDebugValue), nameof(ICorDebugValue.TryGetType)) == 2, "A value's type was not asked again once the program ran");
    }

    [Fact]
    public void Two_methods_at_one_slot_of_two_interfaces_of_a_value_are_remembered_apart()
    {
        App app = new();
        RelaySession session = new(app, new DeviceModules(null), null);
        nint block = session.Take(new RemotePointer(0x4000, 0x4000), RemoteInterfaces.Of(typeof(ICorDebugValue)));
        ICorDebugValue value = (ICorDebugValue)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(block, CreateObjectFlags.UniqueInstance);
        Assert.Equal(0, value.TryGetType(out _));
        int exact = ((ICorDebugValue2)value).TryGetExactType(out ICorDebugType type);
        Assert.True(exact == 0 && type is not null, $"A value's exact type, at the slot its type takes in another interface, answered {exact:X8}");
    }

    [Fact]
    public void An_enumerators_elements_are_read_in_one_round_trip_and_each_let_go_with_the_engines_hold()
    {
        App app = new() { Frames = 3 };
        RelaySession session = new(app, new DeviceModules(null), null);
        nint block = session.Take(new RemotePointer(0x5000, 0x5000), RemoteInterfaces.Of(typeof(ICorDebugFrameEnum)));
        ICorDebugFrameEnum frames = (ICorDebugFrameEnum)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(block, CreateObjectFlags.UniqueInstance);
        List<ICorDebugFrame> walked = [];
        while (true)
        {
            ICorDebugFrame[] one = new ICorDebugFrame[1];
            int hr = frames.TryNext(1, one, out uint fetched);
            if (fetched == 0)
            {
                Assert.Equal(1, hr);
                break;
            }
            walked.Add(one[0]);
        }
        Assert.True(walked.Count == 3 && app.Asked(typeof(ICorDebugFrameEnum), nameof(ICorDebugFrameEnum.TryNext)) == 1,
            $"Walking 3 frames gave {walked.Count} in {app.Asked(typeof(ICorDebugFrameEnum), nameof(ICorDebugFrameEnum.TryNext))} round trips, where one brings them all");
    }

    [Fact]
    public void An_update_has_the_apps_debugger_make_its_own_copy_of_the_modules_metadata_first_and_lets_the_importer_go()
    {
        App app = new();
        RelaySession session = new(app, new DeviceModules(null), null);
        nint block = session.Take(new RemotePointer(0x6000, 0x6000), RemoteInterfaces.Of(typeof(ICorDebugModule)));
        ICorDebugModule2 module = (ICorDebugModule2)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(block, CreateObjectFlags.UniqueInstance);
        Assert.Equal(0, module.TryApplyChanges(2, [1, 2], 1, [3]));
        int made = app.Calls.FindIndex(c => c == (0x6000ul, RelaySession.SlotOf(typeof(ICorDebugModule), nameof(ICorDebugModule.TryGetMetaDataInterface))));
        int applied = app.Calls.FindIndex(c => c.Slot == RelaySession.SlotOf(typeof(ICorDebugModule2), nameof(ICorDebugModule2.TryApplyChanges)) && c.Object != 0x6000);
        Assert.True(made >= 0 && applied > made, $"The app's debugger was not asked for the module's metadata before the update, which its ApplyChanges takes to hold: "
            + string.Join(", ", app.Calls.Select(c => $"0x{c.Object:x} slot {c.Slot}")));
        Assert.True(app.Released.Count == 1, $"The importer the app's debugger handed back was not let go, or more was: {string.Join(", ", app.Released)}");
    }

    [Fact]
    public void A_function_found_by_its_token_is_asked_of_the_app_again_after_an_update_and_a_class_is_not()
    {
        App app = new();
        RelaySession session = new(app, new DeviceModules(null), null);
        nint block = session.Take(new RemotePointer(0x6000, 0x6000), RemoteInterfaces.Of(typeof(ICorDebugModule)));
        ICorDebugModule module = (ICorDebugModule)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(block, CreateObjectFlags.UniqueInstance);
        mdMethodDef method = new(0x06000001u);
        mdTypeDef type = new(0x02000002u);
        uint functionSlot = RelaySession.SlotOf(typeof(ICorDebugModule), nameof(ICorDebugModule.TryGetFunctionFromToken));
        uint classSlot = RelaySession.SlotOf(typeof(ICorDebugModule), nameof(ICorDebugModule.TryGetClassFromToken));
        int Asked(uint slot) => app.Calls.Count(c => c == (0x6000ul, slot));
        Assert.Equal(0, module.TryGetFunctionFromToken(method, out ICorDebugFunction built));
        Assert.Equal(0, module.TryGetClassFromToken(type, out ICorDebugClass _));
        session.EndStop();
        Assert.Equal(0, module.TryGetFunctionFromToken(method, out ICorDebugFunction stillBuilt));
        Assert.True(Asked(functionSlot) == 1 && ReferenceEquals(built, stillBuilt), $"A method's function was asked of the app {Asked(functionSlot)} times before any update, where it holds until one");

        Assert.Equal(0, ((ICorDebugModule2)module).TryApplyChanges(2, [1, 2], 1, [3]));

        Assert.Equal(0, module.TryGetFunctionFromToken(method, out ICorDebugFunction updated));
        Assert.True(Asked(functionSlot) == 2 && !ReferenceEquals(built, updated), "After an update the method's function was answered as the app gave it before, "
            + "its old version, so a breakpoint bound in it after the update sits in code that no longer runs");
        Assert.Equal(0, module.TryGetFunctionFromToken(method, out ICorDebugFunction stillUpdated));
        Assert.True(Asked(functionSlot) == 2 && ReferenceEquals(updated, stillUpdated), $"The updated method's function was asked of the app {Asked(functionSlot)} times, where it holds until the next update");
        Assert.Equal(0, module.TryGetClassFromToken(type, out ICorDebugClass _));
        Assert.True(Asked(classSlot) == 1, "A class, which an update leaves the same object, was asked of the app again after it");
    }

    [Fact]
    public void A_values_size_is_asked_of_the_app_once_and_its_bytes_are_read_at_that_size_alone()
    {
        App app = new() { Sizes = [4, 64] };
        RelaySession session = new(app, new DeviceModules(null), null);
        nint block = session.Take(new RemotePointer(0x7000, 0x7000), RemoteInterfaces.Of(typeof(ICorDebugGenericValue)));
        ICorDebugGenericValue value = (ICorDebugGenericValue)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(block, CreateObjectFlags.UniqueInstance);
        Assert.True(value.TryGetSize(out uint size) == 0 && size == 4, $"The value's size came back as {size}, not the 4 the app first said");
        session.EndStop();
        nint room = Marshal.AllocHGlobal(4 + 16);
        try
        {
            for (int i = 0; i < 4 + 16; i++)
            {
                Marshal.WriteByte(room, i, 0xAB);
            }
            Assert.Equal(0, value.TryGetValue(room));
            byte[] read = new byte[4 + 16];
            Marshal.Copy(room, read, 0, read.Length);
            Assert.True(read.AsSpan(0, 4).IndexOfAnyExcept((byte)7) < 0 && read.AsSpan(4).IndexOfAnyExcept((byte)0xAB) < 0,
                $"The value's bytes were written as {Convert.ToHexString(read)}: past the 4 the engine made room for, where the app said another size later");
        }
        finally
        {
            Marshal.FreeHGlobal(room);
        }
        Assert.True(app.Asked(typeof(ICorDebugValue), nameof(ICorDebugValue.TryGetSize)) == 1,
            $"The value's size was asked of the app {app.Asked(typeof(ICorDebugValue), nameof(ICorDebugValue.TryGetSize))} times, so it could answer the engine one size and the read another");
    }

    [Fact]
    public void A_write_of_fewer_bytes_than_the_app_says_its_value_takes_is_refused_and_nothing_is_sent()
    {
        App app = new() { Sizes = [64] };
        ICorDebugGenericValue value = GenericValue(new RelaySession(app, new DeviceModules(null), null));
        Assert.Throws<InvalidOperationException>(() => value.SetValue(BitConverter.GetBytes(42)));
        Assert.True(app.Asked(typeof(ICorDebugGenericValue), nameof(ICorDebugGenericValue.TrySetValue)) == 0 && app.Sent.Count == 0,
            $"An int written to a value the app says takes 64 bytes was sent as {string.Join(", ", app.Sent.Select(Convert.ToHexString))}: 60 bytes of the debugger's memory past the int's");
    }

    [Fact]
    public void A_write_sends_the_app_as_many_bytes_as_its_value_takes_and_no_more()
    {
        App app = new() { Sizes = [4] };
        ICorDebugGenericValue value = GenericValue(new RelaySession(app, new DeviceModules(null), null));
        value.SetValue([1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.True(app.Sent.Count == 1 && app.Sent[0].SequenceEqual(new byte[] { 1, 2, 3, 4 }),
            $"Writing a 4-byte value sent the app {string.Join(", ", app.Sent.Select(Convert.ToHexString))}, where its 4 bytes alone are its");
    }

    private static ICorDebugGenericValue GenericValue(RelaySession session) =>
        (ICorDebugGenericValue)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(
            session.Take(new RemotePointer(0x7000, 0x7000), RemoteInterfaces.Of(typeof(ICorDebugGenericValue))), CreateObjectFlags.UniqueInstance);

    [Fact]
    public void A_modules_metadata_read_from_the_apps_memory_is_opened_on_the_host()
    {
        (byte[] image, Guid mvid) = Plugin();
        App app = new() { ModuleName = "/data/user/0/com.shop/files/plugins/Plugin.dll", Image = image };
        ICorDebugModule module = Module(new RelaySession(app, new DeviceModules(null), null));
        Guid import = typeof(IMetaDataImport).GUID;
        int opened = module.TryGetMetaDataInterface(ref import, out object? importer);
        Assert.True(opened == 0 && importer is IMetaDataImport metadata && metadata.ScopeProps.pmvid == mvid,
            $"The metadata of a module the host has no copy of was not opened from the app's memory (0x{opened:X8}), so nothing is read of its types");
    }

    [Fact]
    public void A_modules_metadata_the_app_sends_malformed_is_refused_once_and_the_session_goes_on()
    {
        (byte[] image, _) = Plugin();
        int guids = image.AsSpan().IndexOf("#GUID\0\0\0"u8);
        "#JTD\0\0\0\0"u8.CopyTo(image.AsSpan(guids));
        App app = new() { ModuleName = "/data/user/0/com.shop/files/plugins/Plugin.dll", Image = image };
        List<string> said = [];
        RelaySession session = new(app, new DeviceModules(null), said.Add);
        ICorDebugModule module = Module(session);
        Guid import = typeof(IMetaDataImport).GUID;
        int first = module.TryGetMetaDataInterface(ref import, out object? _);
        int reads = app.Reads;
        int again = module.TryGetMetaDataInterface(ref import, out object? _);
        Assert.True(first < 0 && again == first, $"Metadata whose tables the host's reader would lay out past its end was opened: 0x{first:X8}, then 0x{again:X8}");
        Assert.True(app.Reads == reads && said.Count(each => each.Contains("is not opened", StringComparison.Ordinal)) == 1,
            $"The refused module's memory was read again ({app.Reads - reads} reads) or its refusal said other than once: {string.Join(" | ", said)}");
        ICorDebugGenericValue value = GenericValue(session);
        Assert.True(app.BrokenBy is null && value.TryGetType(out _) == 0, $"The session did not go on past the refused module: {app.BrokenBy}");
    }

    [Theory]
    [InlineData(false, "past the")]
    [InlineData(true, "could not be read past")]
    public void An_in_memory_module_said_to_take_2_GB_is_refused_saying_why_with_nothing_held_for_it_and_the_session_goes_on(bool laidOut, string why)
    {
        // The app's memory holds the image and 8 KB after it, then ends.
        byte[] image = [.. Plugin().Image, .. new byte[8192]];
        const uint Claimed = 0x7FFF_F000;
        if (laidOut)
        {
            // Its headers lay out the size the app claims: only its memory says otherwise.
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C)) + 24 + 56), Claimed);
        }
        App app = new() { Image = image, ImageSize = Claimed };
        RelaySession session = new(app, new DeviceModules(null), null);
        Assert.Equal(0, Module(session).TryGetSize(out uint size));
        ICorDebugProcess process = (ICorDebugProcess)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(
            session.Take(new RemotePointer(0x8000, 0x8000), RemoteInterfaces.Of(typeof(ICorDebugProcess))), CreateObjectFlags.UniqueInstance);
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[]? read = ModuleImages.Read(process, App.ImageAt, size, out string? said);
        long held = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(read is null && said?.Contains(why, StringComparison.Ordinal) == true, $"An image of {image.Length:N0} bytes said to take {Claimed:N0} was not refused as {why}: {said}");
        Assert.True(held < 1 << 20, $"Reading an image of {image.Length:N0} bytes said to take {Claimed:N0} took {held:N0} bytes, as if the app's word were its size");
        Assert.True(app.BrokenBy is null, $"The session did not go on past the refused module: {app.BrokenBy}");
    }

    private static ICorDebugModule Module(RelaySession session) =>
        (ICorDebugModule)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(
            session.Take(new RemotePointer(App.ModuleAt, App.ModuleAt), RemoteInterfaces.Of(typeof(ICorDebugModule))), CreateObjectFlags.UniqueInstance);

    /// <summary>A module's image as a compiler writes it, which an app may load from bytes, and its id.</summary>
    private static (byte[] Image, Guid Mvid) Plugin()
    {
        CSharpCompilation compilation = CSharpCompilation.Create("Plugin", [CSharpSyntaxTree.ParseText("public static class Discounts { public static int Off(int price) => price / 10; }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using MemoryStream image = new();
        Assert.True(compilation.Emit(image).Success, "The module did not build");
        byte[] bytes = image.ToArray();
        using PEReader pe = new(ImmutableArray.Create(bytes));
        MetadataReader reader = pe.GetMetadataReader();
        return (bytes, reader.GetGuid(reader.GetModuleDefinition().Mvid));
    }

    [Fact]
    public void A_callback_read_as_the_relay_sends_it_is_handed_on_and_one_running_past_its_frame_ends_the_session()
    {
        App app = new();
        RelaySession session = new(app, new DeviceModules(null), null);
        session.HandOn(LogMessage(message: "Hello"u8.Length * 2, sent: "Hello"u8.Length * 2));
        // The app domain is one the app's debugger keeps, the thread let go once handed on.
        Assert.True(app.BrokenBy is null && app.Released.SequenceEqual([0x9000ul]),
            $"A log message as the relay sends one was taken for a broken relay's ({app.BrokenBy}), or its thread not let go: {string.Join(", ", app.Released)}");

        session.HandOn(LogMessage(message: 4096, sent: 10));
        Assert.True(app.BrokenBy is not null && app.Released.Count == 1,
            "A log message naming 4096 bytes where its frame holds 10 was handed on, its message read past what the relay sent");
    }

    [Theory]
    [InlineData("ooiss", 16 + 16 + 8 + 4 + 2, "a string cut short")]
    [InlineData("oo", 16 + 8, "an object cut short")]
    public void A_callbacks_arguments_short_of_what_its_kinds_take_are_misread(string kinds, int length, string what)
    {
        byte[] body = new byte[length];
        if (kinds == "ooiss")
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(40), 6);
        }
        Assert.True(RelaySession.Misread(kinds, body, 0) is not null, $"A callback with {what} was read as the relay sends one");
    }

    [Fact]
    public void A_callbacks_block_of_bytes_is_read_only_at_the_size_its_next_argument_says()
    {
        byte[] body = new byte[16 + 16 + 4 + 8 + 8];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(32), 8);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(44), 8);
        Assert.Null(RelaySession.Misread("oobi", body, 0));
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(44), 4096);
        Assert.True(RelaySession.Misread("oobi", body, 0) is not null, "A data breakpoint's context of 8 bytes was handed on as 4096, so the engine would read past it");
    }

    /// <summary>A log message's callback as the relay sends it: its two objects, a level, an empty switch name and a message
    /// naming <paramref name="message"/> bytes of which <paramref name="sent"/> come.</summary>
    private static RelayEvent LogMessage(int message, int sent)
    {
        byte[] body = new byte[4 + 16 + 16 + 8 + 4 + 4 + sent];
        (body[0], body[1], body[2], body[3]) = (0x81, 1, 15, 5);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(4), 0x8000);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(12), 0x8000);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(20), 0x9000);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(28), 0x9000);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(48), message);
        return new RelayEvent(1, 15, body, 4);
    }

    [Fact]
    public void An_update_the_app_took_is_applied_to_the_hosts_copy_of_the_modules_metadata_so_a_method_it_added_is_read()
    {
        string folder = Directory.CreateTempSubdirectory("relay-update").FullName;
        try
        {
            (byte[] image, byte[] delta) = AddingMethod("static class Greeter { static string Greet() => \"Hello\"; }",
                "static class Greeter { static string Greet() => \"Hello\"; static string Added() => \"Added\"; }", "Added");
            File.WriteAllBytes(Path.Combine(folder, "Greeting.dll"), image);
            App app = new() { ModuleName = "/data/user/0/com.shop/files/.__override__/arm64-v8a/Greeting.dll" };
            RelaySession session = new(app, new DeviceModules(folder), null);
            nint block = session.Take(new RemotePointer(0x6000, 0x6000), RemoteInterfaces.Of(typeof(ICorDebugModule)));
            ICorDebugModule module = (ICorDebugModule)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(block, CreateObjectFlags.UniqueInstance);
            Guid import = typeof(IMetaDataImport).GUID;
            Assert.Equal(0, module.TryGetMetaDataInterface(ref import, out object? _));

            Assert.Equal(0, ((ICorDebugModule2)module).TryApplyChanges((uint)delta.Length, delta, 0, []));

            Guid emitting = typeof(IMetaDataEmit).GUID;
            Assert.Equal(0, module.TryGetMetaDataInterface(ref emitting, out object? emitter));
            IMetaDataEmit emit = (IMetaDataEmit)emitter!;
            Assert.Equal(0, emit.TryGetSaveSize(0, out uint size));
            byte[] saved = new byte[size];
            Assert.Equal(0, emit.TrySaveToMemory(saved, size));
            string[] methods = Methods(saved);
            Assert.True(methods.Contains("Added"), $"The host's copy of the module's metadata does not have the method the update added, so an expression calling it "
                + $"finds no such method: it has {string.Join(", ", methods)}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A module's image built from <paramref name="before"/>, and the metadata of the update that makes it
    /// <paramref name="after"/>, which adds the method named <paramref name="added"/> to its one class.</summary>
    private static (byte[] Image, byte[] Delta) AddingMethod(string before, string after, string added)
    {
        CSharpCompilationOptions options = new(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug);
        MetadataReference[] references = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
        CSharpCompilation first = CSharpCompilation.Create("Greeting", [CSharpSyntaxTree.ParseText(before)], references, options);
        using MemoryStream image = new();
        using MemoryStream symbols = new();
        Assert.True(first.Emit(image, symbols, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb)).Success, "The module did not build");
        EmitBaseline baseline = EmitBaseline.CreateInitialBaseline(first, ModuleMetadata.CreateFromImage(image.ToArray()), _ => default, _ => default, true);
        CSharpCompilation second = first.ReplaceSyntaxTree(first.SyntaxTrees[0], CSharpSyntaxTree.ParseText(after));
        ISymbol method = second.GetTypeByMetadataName("Greeter")!.GetMembers(added).Single();
        using MemoryStream metadata = new();
        using MemoryStream il = new();
        using MemoryStream pdb = new();
        EmitDifferenceResult update = second.EmitDifference(baseline, [new SemanticEdit(SemanticEditKind.Insert, null, method)], _ => false, metadata, il, pdb);
        Assert.True(update.Success, $"The update did not build: {string.Join("; ", update.Diagnostics)}");
        return (image.ToArray(), metadata.ToArray());
    }

    /// <summary>The names of the methods metadata saved whole defines.</summary>
    private static string[] Methods(byte[] metadata)
    {
        using MetadataReaderProvider provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.Create(metadata));
        MetadataReader reader = provider.GetMetadataReader();
        return [.. reader.MethodDefinitions.Select(handle => reader.GetString(reader.GetMethodDefinition(handle).Name))];
    }

    /// <summary>An app's debugger as the relay carries it: every call answered by its arguments' kinds, a frame
    /// enumerator's Next with <see cref="Frames"/> frames, a module's name <see cref="ModuleName"/>, the module at
    /// <see cref="ModuleAt"/> loaded from <see cref="Image"/> where one is given, every call's object and slot, the bytes
    /// sent and every release heard.</summary>
    private sealed class App : IRelayLink
    {
        /// <summary>The module the tests take, and where its image lies in the app's memory.</summary>
        public const ulong ModuleAt = 0x6000;
        public const ulong ImageAt = 0x7F000000;

        private readonly ConcurrentQueue<uint> _asked = new();
        private ulong _next = 0x10000;

        /// <summary>The image of the module at <see cref="ModuleAt"/>, loaded from bytes; null where it has none.</summary>
        public byte[]? Image { get; init; }

        /// <summary>The size the module says its image takes, where not the image's own.</summary>
        public uint? ImageSize { get; init; }

        /// <summary>How many reads of the app's memory came.</summary>
        public int Reads { get; private set; }

        /// <summary>The bytes of each argument sent to the app but a QueryInterface's interface id, in order.</summary>
        public List<byte[]> Sent { get; } = [];

        public int Frames { get; init; }

        /// <summary>The name a module answers, as the app's runtime names it; null where none is asked for.</summary>
        public string? ModuleName { get; init; }

        /// <summary>The sizes a value answers, one for each time it is asked, the last for any after; null where none is.</summary>
        public uint[]? Sizes { get; init; }

        private int _sized;

        /// <summary>Why the session took the relay for broken; null while it has not.</summary>
        public string? BrokenBy { get; private set; }

        public List<ulong> Released { get; } = [];

        /// <summary>Each call's object and slot, in order.</summary>
        public List<(ulong Object, uint Slot)> Calls { get; } = [];

        public ulong Callback => 0;

        public BlockingCollection<RelayEvent> Events { get; } = [];

        public int Asked(Type type, string method) => _asked.Count(each => each == RelaySession.SlotOf(type, method));

        public RelayReply Call(ulong obj, uint slot, IReadOnlyList<RelayArg> args)
        {
            _asked.Enqueue(slot);
            Calls.Add((obj, slot));
            if (slot != 0)
            {
                Sent.AddRange(args.Where(arg => arg.Kind == RelayArgKind.In).Select(arg => arg.Bytes!));
            }
            if (Image is { } image && Loaded(obj, slot, args, image) is { } loaded)
            {
                return loaded;
            }
            if (slot == 0)
            {
                return new RelayReply(0, [], [[new RemotePointer(obj + 8, obj)]]);
            }
            if (Sizes is { } sizes && slot == RelaySession.SlotOf(typeof(ICorDebugValue), nameof(ICorDebugValue.TryGetSize)) && args.Count == 1)
            {
                return new RelayReply(0, [BitConverter.GetBytes(sizes[Math.Min(_sized++, sizes.Length - 1)])], []);
            }
            if (ModuleName is { } name && slot == RelaySession.SlotOf(typeof(ICorDebugModule), nameof(ICorDebugModule.TryGetName)) && args.Count == 3)
            {
                byte[] chars = new byte[args[2].Size];
                Encoding.Unicode.GetBytes(name).CopyTo(chars, 0);
                return new RelayReply(0, [BitConverter.GetBytes(name.Length + 1), chars], []);
            }
            if (slot == RelaySession.SlotOf(typeof(ICorDebugFrameEnum), nameof(ICorDebugFrameEnum.TryNext)) && args.Count == 3)
            {
                int count = (int)args[0].Value;
                int given = Math.Min(count, Frames);
                RemotePointer[] frames = [.. Enumerable.Range(0, count).Select(i => i < given ? new RemotePointer(_next, _next++) : default)];
                return new RelayReply(given < count ? 1 : 0, [BitConverter.GetBytes(given)], [frames]);
            }
            List<byte[]> outs = [];
            List<RemotePointer[]> objects = [];
            foreach (RelayArg arg in args)
            {
                if (arg.Kind is RelayArgKind.Out or RelayArgKind.InOut)
                {
                    byte[] written = new byte[arg.Size];
                    written.AsSpan().Fill(7);
                    outs.Add(written);
                }
                else if (arg.Kind == RelayArgKind.Objects)
                {
                    objects.Add([.. Enumerable.Range(0, arg.Size).Select(_ => new RemotePointer(_next, _next++))]);
                }
            }
            return new RelayReply(0, outs, objects);
        }

        /// <summary>What the module loaded from <paramref name="image"/> and its process answer: it is no dynamic module, lies
        /// at <see cref="ImageAt"/> and is as long as its image, of which the process reads what lies within; null for any
        /// other call.</summary>
        private RelayReply? Loaded(ulong obj, uint slot, IReadOnlyList<RelayArg> args, byte[] image)
        {
            if (obj == ModuleAt && slot == RelaySession.SlotOf(typeof(ICorDebugModule), nameof(ICorDebugModule.TryIsDynamic)))
            {
                return new RelayReply(0, [new byte[4]], []);
            }
            if (obj == ModuleAt && slot == RelaySession.SlotOf(typeof(ICorDebugModule), nameof(ICorDebugModule.TryGetBaseAddress)))
            {
                return new RelayReply(0, [BitConverter.GetBytes(ImageAt)], []);
            }
            if (obj == ModuleAt && slot == RelaySession.SlotOf(typeof(ICorDebugModule), nameof(ICorDebugModule.TryGetSize)))
            {
                return new RelayReply(0, [BitConverter.GetBytes(ImageSize ?? (uint)image.Length)], []);
            }
            if (obj != ModuleAt && slot == RelaySession.SlotOf(typeof(ICorDebugProcess), nameof(ICorDebugProcess.TryReadMemory)) && args.Count == 4)
            {
                Reads++;
                ulong at = args[0].Value - ImageAt;
                int count = (int)args[1].Value;
                return args[0].Value >= ImageAt && at + (ulong)count <= (ulong)image.Length
                    ? new RelayReply(0, [image.AsSpan((int)at, count).ToArray(), BitConverter.GetBytes((ulong)count)], [])
                    : new RelayReply(unchecked((int)0x80004005), [new byte[count], new byte[8]], []);
            }
            return null;
        }

        public void Release(ulong pointer)
        {
            if (pointer != 0)
            {
                Released.Add(pointer);
            }
        }

        public void Broken(string why) => BrokenBy ??= why;

        public (int Trips, double WaitedMs) TakeTally() => (0, 0);
    }
}
