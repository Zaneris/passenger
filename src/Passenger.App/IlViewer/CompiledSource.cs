using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using AssemblyImage = ICSharpCode.Decompiler.Metadata.PEFile;

namespace Passenger.App.IlViewer;

internal enum CompiledView
{
    Il,
    LowLevelCSharp,
}

internal sealed record CompiledRequest(string Source, string Assembly, CompiledView View);

internal readonly record struct CompiledSection(int FirstLine, int LastLine, TextRange Heading);

internal readonly record struct CompiledSequencePoint(TextPosition Start, TextPosition End, TextRange Instructions);

/// <summary>A source file's methods as emitted by the build, located through the matching portable PDB.</summary>
internal sealed record CompiledSource(string Text, IReadOnlyList<CompiledSection> Sections,
    IReadOnlyList<CompiledSequencePoint> SequencePoints, InstructionColours? Colours = null, Words? Message = null,
    TextLink[]? Links = null)
{
    private static readonly Guid Sha1 = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    private static readonly Guid Sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");

    /// <summary>The output for the most specific matching statement, or the enclosing method's heading.</summary>
    public TextRange? RangeAt(TextPosition caret)
    {
        CompiledSequencePoint? nearest = null;
        foreach (CompiledSequencePoint point in SequencePoints)
        {
            if (point.Start.CompareTo(caret) <= 0 && caret.CompareTo(point.End) < 0
                && (nearest is null || point.Start.CompareTo(nearest.Value.Start) >= 0 && point.End.CompareTo(nearest.Value.End) <= 0))
            {
                nearest = point;
            }
        }
        if (nearest is { } mapped)
        {
            return mapped.Instructions;
        }

        CompiledSection? found = null;
        foreach (CompiledSection section in Sections)
        {
            if (section.FirstLine <= caret.Line && section.LastLine >= caret.Line
                && (found is null || section.LastLine - section.FirstLine < found.Value.LastLine - found.Value.FirstLine))
            {
                found = section;
            }
        }
        return found?.Heading;
    }

    public static CompiledSource Read(CompiledRequest request, CancellationToken cancel)
    {
        if (!File.Exists(request.Assembly))
        {
            return Say(IlWords.NeedsBuild(Words.Verbatim(Path.GetFileName(request.Source))));
        }
        try
        {
            using AssemblyImage image = new(request.Assembly, PEStreamOptions.PrefetchEntireImage);
            if (!image.Reader.TryOpenAssociatedPortablePdb(request.Assembly, OpenShared, out MetadataReaderProvider? provider, out _)
                || provider is null)
            {
                return Say(IlWords.NoPdb);
            }
            using (provider)
            {
                return ReadMethods(request, image, provider.GetMetadataReader(), cancel);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException
            or InvalidOperationException or MetadataFileNotSupportedException or DecompilerException)
        {
            return Say(IlWords.Unread(Words.Verbatim(error.Message)));
        }
    }

    private static Stream? OpenShared(string path) => File.Exists(path)
        ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
        : null;

    private static CompiledSource ReadMethods(CompiledRequest request, AssemblyImage image, MetadataReader symbols, CancellationToken cancel)
    {
        HashSet<DocumentHandle> documents = [.. symbols.Documents.Where(handle => Matches(symbols, handle, request.Source))];
        MethodSource[] methods = [.. Methods(symbols, documents, cancel).OrderBy(method => method.FirstLine)];
        if (methods.Length == 0)
        {
            return Say(IlWords.NoMethods(Words.Verbatim(Path.GetFileName(request.Source))));
        }

        bool sourceMatchesBuild = documents.Any(handle => MatchesChecksum(symbols, handle, request.Source));
        StringBuilder text = new();
        using StringWriter writer = new(text, CultureInfo.InvariantCulture);
        PlainTextOutput output = new(writer);
        MappedIl? body = request.View == CompiledView.Il ? new MappedIl(output, text, cancel) : null;
        ReflectionDisassembler? il = body is null ? null : new ReflectionDisassembler(output, body, cancel);
        DecompilerSettings settings = new()
        {
            AsyncAwait = false,
            YieldReturn = false,
            AnonymousMethods = false,
            LocalFunctions = false,
            ThrowOnAssemblyResolveErrors = false,
        };
        CSharpDecompiler? csharp = il is null ? Decompiler(request.Assembly, image, settings, cancel) : null;
        MetadataReader metadata = image.Metadata;
        List<CompiledSection> sections = [];
        List<CompiledSequencePoint> points = [];
        List<TextLink> links = [];
        foreach (MethodSource method in methods)
        {
            cancel.ThrowIfCancellationRequested();
            int start = text.Length;
            MethodDefinition definition = metadata.GetMethodDefinition(method.Handle);
            MethodDefinition shown = method.Kickoff.IsNil ? definition : metadata.GetMethodDefinition(method.Kickoff);
            string type = metadata.GetString(metadata.GetTypeDefinition(shown.GetDeclaringType()).Name);
            string name = metadata.GetString(shown.Name);
            writer.WriteLine($"// {type}.{name}");
            if (sourceMatchesBuild)
            {
                sections.Add(new CompiledSection(method.FirstLine - 1, method.LastLine - 1, TextRange.FromBounds(start, text.Length - writer.NewLine.Length)));
            }
            if (il is not null)
            {
                if (!method.Kickoff.IsNil)
                {
                    il.DisassembleMethod(image, method.Kickoff);
                    writer.WriteLine();
                }
                il.DisassembleMethod(image, method.Handle);
                if (sourceMatchesBuild)
                {
                    MapInstructions(body!.Instructions, method.Points, points);
                }
            }
            else
            {
                writer.WriteLine(LowLevelSymbols.Render(method.Kickoff.IsNil ? csharp!.Decompile(method.Handle)
                    : csharp!.Decompile(method.Kickoff, definition.GetDeclaringType()), settings, text.Length, links));
            }
            writer.WriteLine();
        }
        string rendered = writer.ToString().TrimEnd();
        return new CompiledSource(rendered, sections, points, il is null ? null : InstructionColours.ParseIl(rendered, cancel),
            Links: [.. links.OrderBy(link => link.Range.Start)]);
    }

    private static bool Matches(MetadataReader symbols, DocumentHandle handle, string source)
    {
        string path = symbols.GetString(symbols.GetDocument(handle).Name);
        return Path.IsPathFullyQualified(path) && PathNames.SameFile(path, source);
    }

    private static bool MatchesChecksum(MetadataReader symbols, DocumentHandle handle, string source)
    {
        Document document = symbols.GetDocument(handle);
        if (document.HashAlgorithm.IsNil || document.Hash.IsNil)
        {
            return false;
        }
        Guid algorithm = symbols.GetGuid(document.HashAlgorithm);
        if ((algorithm != Sha256 && algorithm != Sha1) || OpenShared(source) is not { } stream)
        {
            return false;
        }
        using (stream)
        {
            byte[] checksum = algorithm == Sha256 ? SHA256.HashData(stream) : SHA1.HashData(stream);
            return checksum.AsSpan().SequenceEqual(symbols.GetBlobBytes(document.Hash));
        }
    }

    private static IEnumerable<MethodSource> Methods(MetadataReader symbols, HashSet<DocumentHandle> documents, CancellationToken cancel)
    {
        foreach (MethodDebugInformationHandle handle in symbols.MethodDebugInformation)
        {
            cancel.ThrowIfCancellationRequested();
            MethodDebugInformation method = symbols.GetMethodDebugInformation(handle);
            SequencePoint? pending = null;
            List<(SequencePoint Point, int EndOffset)> points = [];
            foreach (SequencePoint point in method.GetSequencePoints())
            {
                if (pending is { } previous)
                {
                    points.Add((previous, point.Offset));
                }
                pending = !point.IsHidden && documents.Contains(point.Document) ? point : null;
            }
            if (pending is { } final)
            {
                points.Add((final, int.MaxValue));
            }
            if (points.Count > 0)
            {
                yield return new MethodSource(MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber(handle)),
                    method.GetStateMachineKickoffMethod(), points);
            }
        }
    }

    private static void MapInstructions(IReadOnlyList<(int Offset, TextRange Text)> instructions,
        IReadOnlyList<(SequencePoint Point, int EndOffset)> points, List<CompiledSequencePoint> result)
    {
        int index = 0;
        foreach ((SequencePoint point, int endOffset) in points)
        {
            while (index < instructions.Count && instructions[index].Offset < point.Offset)
            {
                index++;
            }
            int first = index;
            while (index < instructions.Count && instructions[index].Offset < endOffset)
            {
                index++;
            }
            if (index > first)
            {
                result.Add(new CompiledSequencePoint(new TextPosition(point.StartLine - 1, point.StartColumn - 1),
                    new TextPosition(point.EndLine - 1, point.EndColumn - 1),
                    TextRange.FromBounds(instructions[first].Text.Start, instructions[index - 1].Text.End)));
            }
        }
    }

    private static CSharpDecompiler Decompiler(string assembly, AssemblyImage image, DecompilerSettings settings, CancellationToken cancel)
    {
        UniversalAssemblyResolver resolver = new(assembly, false, image.DetectTargetFrameworkId());
        return new CSharpDecompiler(image, resolver, settings) { CancellationToken = cancel };
    }

    private static CompiledSource Say(Words message) => new(string.Empty, [], [], Message: message);

    private readonly record struct MethodSource(MethodDefinitionHandle Handle, MethodDefinitionHandle Kickoff,
        IReadOnlyList<(SequencePoint Point, int EndOffset)> Points)
    {
        public int FirstLine => Points.Min(entry => entry.Point.StartLine);
        public int LastLine => Points.Max(entry => entry.Point.EndLine);
    }
}
