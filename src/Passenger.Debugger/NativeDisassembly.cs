using System.Runtime.InteropServices;
using Gee.External.Capstone;
using Gee.External.Capstone.Arm64;
using Gee.External.Capstone.X86;
using Passenger.Dap;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public static class NativeDisassembly
{
    public static DisassembleFrameResponse Decode(FrameNativeCode code)
    {
        // DbgShim loads the target's debugging library into this adapter. The adapter has the target's architecture,
        // even when the IDE is on a different machine or running a different architecture itself.
        Architecture architecture = RuntimeInformation.ProcessArchitecture;
        Func<byte[], long, IEnumerable<(long Address, int Size, string Mnemonic, string Operands)>> decode;
        using CapstoneDisassembler disassembler = architecture switch
        {
            Architecture.X64 or Architecture.X86 => CapstoneDisassembler.CreateX86Disassembler(
                architecture == Architecture.X64 ? X86DisassembleMode.Bit64 : X86DisassembleMode.Bit32),
            Architecture.Arm64 => CapstoneDisassembler.CreateArm64Disassembler(Arm64DisassembleMode.LittleEndian),
            _ => throw new InvalidOperationException($"Assembly is unavailable for {architecture}."),
        };
        disassembler.EnableSkipDataMode = true;
        if (disassembler is CapstoneX86Disassembler x86)
        {
            decode = (bytes, address) => x86.Disassemble(bytes, address).Select(i => (i.Address, i.Bytes.Length, i.Mnemonic, i.Operand));
        }
        else
        {
            CapstoneArm64Disassembler arm64 = (CapstoneArm64Disassembler)disassembler;
            decode = (bytes, address) => arm64.Disassemble(bytes, address).Select(i => (i.Address, i.Bytes.Length, i.Mnemonic, i.Operand));
        }

        int start = 0;
        List<DapNativeInstruction> instructions = [];
        foreach (CodeChunk chunk in code.Chunks)
        {
            int length = (int)chunk.Length;
            foreach ((long address, int size, string mnemonic, string operands) in decode(code.Bytes.AsSpan(start, length).ToArray(), (long)chunk.Address.Value))
            {
                uint offset = (uint)(start + address - (long)chunk.Address.Value);
                instructions.Add(new DapNativeInstruction(offset, (ulong)address, mnemonic, operands,
                    code.InstructionOffset is uint current && offset <= current && current < offset + size));
            }
            start += length;
        }
        return new DisassembleFrameResponse(architecture.ToString(), instructions, code.Sources);
    }
}
