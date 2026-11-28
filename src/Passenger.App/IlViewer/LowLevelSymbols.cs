using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Passenger.App.ToolWindows;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.IlViewer;

internal sealed record CompiledSymbol(string Assembly, Guid Module, EntityHandle Token, string Name);

internal static class LowLevelSymbols
{
    public static string Render(SyntaxTree tree, DecompilerSettings settings, int offset, List<TextLink> links)
    {
        using StringWriter writer = new(CultureInfo.InvariantCulture);
        TokenWriter tokens = TokenWriter.InsertRequiredSpaces(new TextTokenWriter(new LinkedOutput(writer, offset, links), settings));
        tree.AcceptVisitor(new CSharpOutputVisitor(tokens, settings.CSharpFormattingOptions));
        return writer.ToString().TrimEnd();
    }

    public static FileLink? Definition(CompiledSymbol symbol)
    {
        try
        {
            using PEReader image = new(OpenShared(symbol.Assembly));
            MetadataReader metadata = image.GetMetadataReader();
            if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != symbol.Module)
            {
                return null;
            }
            return SourceOf(image, symbol) ?? Decompile(symbol);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException
            or InvalidOperationException or MetadataFileNotSupportedException or DecompilerException)
        {
            return null;
        }
    }

    private static FileLink? SourceOf(PEReader image, CompiledSymbol symbol)
    {
        if (symbol.Token.Kind != HandleKind.MethodDefinition
            || !image.TryOpenAssociatedPortablePdb(symbol.Assembly, path => File.Exists(path) ? OpenShared(path) : null,
                out MetadataReaderProvider? provider, out _) || provider is null)
        {
            return null;
        }
        using (provider)
        {
            MetadataReader symbols = provider.GetMetadataReader();
            foreach (SequencePoint point in symbols.GetMethodDebugInformation((MethodDefinitionHandle)symbol.Token).GetSequencePoints())
            {
                if (!point.IsHidden && !point.Document.IsNil)
                {
                    string path = symbols.GetString(symbols.GetDocument(point.Document).Name);
                    if (Path.IsPathFullyQualified(path) && FoundPaths.Askable(path) && File.Exists(path))
                    {
                        return new FileLink(path, new TextPosition(point.StartLine - 1, Math.Max(0, point.StartColumn - 1))) { CompiledLines = true };
                    }
                }
            }
        }
        return null;
    }

    private static FileLink? Decompile(CompiledSymbol symbol)
    {
        string assembly = Path.GetFileNameWithoutExtension(symbol.Assembly);
        if (LibrarySources.Entry("Decompiled", assembly, symbol.Assembly) is not { } folder
            || LibrarySources.Inside(folder, $"{FileNames.Safe(symbol.Name)}-{MetadataTokens.GetToken(symbol.Token):X8}.cs") is not { } path)
        {
            return null;
        }
        if (!File.Exists(path))
        {
            CSharpDecompiler decompiler = new(symbol.Assembly, new DecompilerSettings { ThrowOnAssemblyResolveErrors = false });
            LibrarySources.WriteWhole(path, Encoding.UTF8.GetBytes(decompiler.DecompileAsString(symbol.Token)));
        }
        return new FileLink(path, default);
    }

    private static FileStream OpenShared(string path) => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Records the symbol references emitted by ILSpy at the offsets where they are written.</summary>
    private sealed class LinkedOutput(StringWriter writer, int offset, List<TextLink> links) : ITextOutput
    {
        private readonly PlainTextOutput output = new(writer);

        public string IndentationString { get => output.IndentationString; set => output.IndentationString = value; }
        public void Indent() => output.Indent();
        public void Unindent() => output.Unindent();
        public void Write(char ch) => output.Write(ch);
        public void Write(string text) => output.Write(text);
        public void WriteLine() => output.WriteLine();
        public void MarkFoldStart(string collapsedText, bool defaultCollapsed, bool isDefinition) { }
        public void MarkDefinitionStart() { }
        public void MarkFoldEnd() { }
        public void WriteLocalReference(string text, object reference, bool isDefinition, bool isHoverOnly) => Write(text);
        public void WriteReference(OpCodeInfo opCode, bool omitSuffix) => output.WriteReference(opCode, omitSuffix);

        public void WriteReference(IType type, string text, bool isDefinition) => WriteEntity(type.GetDefinition(), text);

        public void WriteReference(IMember member, string text, bool isDefinition) => WriteEntity(member.MemberDefinition, text);

        public void WriteReference(MetadataFile metadata, Handle handle, string text, string protocol, bool isDefinition)
        {
            Write(text);
            if (handle.Kind is HandleKind.TypeDefinition or HandleKind.MethodDefinition or HandleKind.FieldDefinition
                or HandleKind.PropertyDefinition or HandleKind.EventDefinition)
            {
                Link(metadata, (EntityHandle)handle, text, text);
            }
        }

        private void WriteEntity(IEntity? entity, string text)
        {
            Write(text);
            if (entity is { MetadataToken.IsNil: false, ParentModule.MetadataFile: { } metadata })
            {
                Link(metadata, entity.MetadataToken, entity.Name, text);
            }
        }

        private void Link(MetadataFile metadata, EntityHandle token, string name, string text)
        {
            if (text.Length > 0 && Path.IsPathFullyQualified(metadata.FileName))
            {
                Guid mvid = metadata.Metadata.GetGuid(metadata.Metadata.GetModuleDefinition().Mvid);
                links.Add(new TextLink(new TextRange(offset + writer.GetStringBuilder().Length - text.Length, text.Length),
                    new CompiledSymbol(metadata.FileName, mvid, token, name)));
            }
        }
    }
}
