using System.Reflection.Metadata;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using Passenger.Text;

namespace Passenger.App.IlViewer;

/// <summary>Records the output range of each instruction as ILSpy writes a method body.</summary>
internal sealed class MappedIl(ITextOutput output, StringBuilder text, CancellationToken cancel) : MethodBodyDisassembler(output, cancel)
{
    private readonly List<(int Offset, TextRange Text)> instructions = [];

    public IReadOnlyList<(int Offset, TextRange Text)> Instructions => instructions;

    public override void Disassemble(MetadataFile module, MethodDefinitionHandle handle)
    {
        instructions.Clear();
        base.Disassemble(module, handle);
    }

    protected override void WriteInstruction(ITextOutput output, MetadataFile metadataFile, MethodDefinitionHandle methodHandle,
        ref BlobReader blob, int methodRva)
    {
        int offset = blob.Offset;
        int start = text.Length;
        base.WriteInstruction(output, metadataFile, methodHandle, ref blob, methodRva);
        int end = text.Length;
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }
        if (end > start)
        {
            instructions.Add((offset, TextRange.FromBounds(start, end)));
        }
    }
}
