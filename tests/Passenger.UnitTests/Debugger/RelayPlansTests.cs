using Passenger.Debugger.Interop;
using Passenger.Debugger.Relay;

namespace Passenger.UnitTests.Debugger;

/// <summary>How the relay to an app on a device reads the engine's ICorDebug interfaces: each method at its vtable slot,
/// each argument sized, so a call made on a stand-in here reaches the same method in the app.</summary>
public class RelayPlansTests
{
    [Fact]
    public void Each_ICorDebug_method_reaches_the_same_method_in_the_app_and_a_modules_image_and_metadata_come_whole_in_bounded_pieces() => Cases.All(
        ("a derived interface's own methods take the slots after its bases", () =>
        {
            InterfacePlan handle = RemoteInterfaces.Of(typeof(ICorDebugHandleValue));
            Assert.Equal(nameof(ICorDebugReferenceValue.TryIsNull), handle.At(7)?.Name);
            Assert.Equal(nameof(ICorDebugHandleValue.TryDispose), handle.At(13)?.Name);
            Assert.Null(handle.At(14));
        }),
        ("every array an ICorDebug method takes names its length", () =>
        {
            string[] unsized = [.. DebuggerPlans()
                .SelectMany(plan => plan.Methods.SelectMany(method => method.Params
                    .Where(p => p.Kind is ParamKind.ArrayIn or ParamKind.ArrayOut && p.CountIndex < 0)
                    .Select(p => $"{plan.Type.Name}.{method.Name}({p.Name})")))];
            Assert.True(unsized.Length == 0, $"The relay cannot size these arrays, so their calls would send nothing: {string.Join(", ", unsized)}");
        }),
        ("only launching passes arguments beyond the registers", () =>
        {
            // Eight registers carry the object and seven arguments everywhere; Apple's arm64 packs any more by their size.
            string[] beyond = [.. DebuggerPlans()
                .SelectMany(plan => plan.Methods.Where(method => method.Params.Length > 7).Select(method => $"{plan.Type.Name}.{method.Name}"))];
            Assert.Equal([$"{nameof(ICorDebug)}.{nameof(ICorDebug.TryCreateProcess)}"], beyond);
        }),
        ("a module the app names by a device path or bare name is the host's copy", () =>
        {
            string folder = Directory.CreateTempSubdirectory("relay-modules").FullName;
            try
            {
                string coreLib = Path.Combine(folder, "System.Private.CoreLib.dll");
                string app = Path.Combine(folder, "DroidApp.dll");
                using (File.Create(coreLib))
                using (File.Create(app))
                {
                }
                DeviceModules modules = new(folder);
                Assert.Equal(coreLib, modules.HostFileOf("System.Private.CoreLib"));
                Assert.Equal(app, modules.HostFileOf("/data/user/0/com.companyname.DroidApp/files/.__override__/arm64-v8a/DroidApp.dll"));
                Assert.Null(modules.HostFileOf("/data/user/0/com.companyname.DroidApp/files/.__override__/arm64-v8a/Other.dll"));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }),
        ("every answer kept names a method of its interface that takes only numbers", () =>
        {
            string[] wrong = [.. RelayAnswers.ForTheObject.Concat(RelayAnswers.ForTheCode).Concat(RelayAnswers.ForTheStop)
                .SelectMany(each => each.Value.Select(method => (Plan: RemoteInterfaces.Of(each.Key), Method: method)))
                .Where(each => each.Plan.Methods.FirstOrDefault(m => m.Name == each.Method) is not { } method || !RelaySession.Rememberable(method))
                .Select(each => $"{each.Plan.Type.Name}.{each.Method}")];
            Assert.True(wrong.Length == 0, $"These answers can't be kept by their inputs, or name no method, so they'd be asked every time or never: {string.Join(", ", wrong)}");
            Assert.True(RelayAnswers.Changes(nameof(ICorDebugController.TryContinue)) && RelayAnswers.Changes(nameof(ICorDebugILFrame.TrySetIP))
                && RelayAnswers.Changes(nameof(ICorDebugReferenceValue.TrySetValue)) && !RelayAnswers.Changes(nameof(ICorDebugReferenceValue.TryGetValue)),
                "Running the program or writing to it must end what it said while stopped, and only that");
        }),
        ("every raw pointer an ICorDebug method takes is answered on the host or refused", () =>
        {
            string[] answered =
            [
                "ICorDebugProcess.TryGetThreadContext", "ICorDebugProcess.TrySetThreadContext", "ICorDebugStackWalk.TryGetContext",
                "ICorDebugStackWalk.TrySetContext", "ICorDebugGenericValue.TryGetValue", "ICorDebugGenericValue.TrySetValue",
                "ICorDebugAppDomain.TryGetModuleFromMetaDataInterface",
            ];
            string[] refused =
            [
                "ICorDebug.TryCreateProcess", "ICorDebugEditAndContinueSnapshot.TryCopyMetaData", "ICorDebugEditAndContinueSnapshot.TrySetPEBytes",
                "ICorDebugEditAndContinueSnapshot.TrySetPESymbolBytes", "ICorDebugObjectValue.TrySetFromManagedCopy", "ICorDebugContext.TrySetFromManagedCopy",
            ];
            string[] found = [.. DebuggerPlans()
                .Where(plan => !plan.Type.Name.Contains("Callback", StringComparison.Ordinal))
                .SelectMany(plan => plan.Methods.Where(method => method.Params.Any(p => p.Kind == ParamKind.Pointer)).Select(method => $"{plan.Type.Name}.{method.Name}"))
                .Distinct()];
            string[] unknown = [.. found.Except(answered).Except(refused)];
            Assert.True(unknown.Length == 0, $"These take a raw pointer the relay neither answers on the host nor refuses: {string.Join(", ", unknown)}");
        }),
        ("a module's metadata is found in its image laid out as its file or mapped by section and read in pieces each no longer than what came before", () =>
        {
            byte[] file = File.ReadAllBytes(typeof(RelayPlansTests).Assembly.Location);
            using System.Reflection.PortableExecutable.PEReader reader = new(new MemoryStream(file));
            byte[] expected = [.. reader.GetMetadata().GetContent()];
            byte[] mapped = new byte[reader.PEHeaders.PEHeader!.SizeOfImage];
            file.AsSpan(0, reader.PEHeaders.PEHeader.SizeOfHeaders).CopyTo(mapped);
            foreach (System.Reflection.PortableExecutable.SectionHeader section in reader.PEHeaders.SectionHeaders)
            {
                file.AsSpan(section.PointerToRawData, Math.Min(section.SizeOfRawData, section.VirtualSize)).CopyTo(mapped.AsSpan(section.VirtualAddress));
            }
            foreach ((string layout, byte[] image) in (IEnumerable<(string, byte[])>)[("as its file", file), ("mapped", mapped)])
            {
                const ulong at = 0x7000_0000;
                const int Piece = 16 * 1024;
                ulong metadataAt = 0;
                List<int> reads = [];
                byte[]? read = ModuleImages.Metadata(at, (uint)image.Length, (address, count) =>
                {
                    if (metadataAt == 0 && count >= 4 && address >= at && address - at + (ulong)count <= (ulong)image.Length && image.AsSpan((int)(address - at), 4).SequenceEqual("BSJB"u8))
                    {
                        metadataAt = address;
                    }
                    if (metadataAt != 0 && address >= metadataAt)
                    {
                        reads.Add(count);
                    }
                    return address >= at && address - at + (ulong)count <= (ulong)image.Length ? image.AsSpan((int)(address - at), count).ToArray() : null;
                }, out string? refused, Piece);
                Assert.True(read is not null && read.SequenceEqual(expected), $"The metadata of an image laid out {layout} was not found: {refused}");
                Assert.True(reads.Count > 2 && reads.Skip(1).Select((count, i) => count <= Piece && count <= reads.Take(i + 1).Sum()).All(held => held),
                    $"The metadata of an image laid out {layout} was read in pieces of {string.Join(", ", reads)} bytes, one longer than {Piece:N0} or than what came before it");
            }
        }),
        ("a module's image is read whole in pieces each no longer than what came before it", () =>
        {
            byte[] file = File.ReadAllBytes(typeof(RelayPlansTests).Assembly.Location);
            const ulong at = 0x7000_0000;
            const int Piece = 64 * 1024;
            List<int> reads = [];
            byte[]? read = ModuleImages.Image(at, (uint)file.Length, (address, count) =>
            {
                reads.Add(count);
                return address >= at && address - at + (ulong)count <= (ulong)file.Length ? file.AsSpan((int)(address - at), count).ToArray() : null;
            }, out string? why, Piece);
            Assert.True(read is not null && read.SequenceEqual(file), $"An image of {file.Length:N0} bytes did not come whole: {why}");
            Assert.True(reads.Count > 2 && reads.Skip(1).Select((count, i) => count <= Piece && count <= reads.Take(i + 1).Sum()).All(held => held),
                $"An image was read in pieces of {string.Join(", ", reads)} bytes, one longer than {Piece:N0} or than what came before it");
        }));

    private static IEnumerable<InterfacePlan> DebuggerPlans() => typeof(ICorDebug).Assembly.GetTypes()
        .Where(type => type.IsInterface && type.Name.StartsWith("ICorDebug", StringComparison.Ordinal)
            && type.IsDefined(typeof(System.Runtime.InteropServices.Marshalling.GeneratedComInterfaceAttribute), inherit: false))
        .Select(RemoteInterfaces.Of);
}
