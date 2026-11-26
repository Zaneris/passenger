using Passenger.Dap;

namespace Passenger.App.IlViewer;

/// <summary>Common JIT instructions, based on the Intel SDM volume 2 and Arm A64 instruction reference.</summary>
internal static class AsmOpcodes
{
    public static OpcodeHelp Describe(string architecture, DapNativeInstruction instruction)
    {
        string name = instruction.Mnemonic;
        string description = (architecture == "Arm64", name) switch
        {
            (_, ".byte") => "Bytes the disassembler could not decode as an instruction.",
            (_, "mov") => "Copies a value to the destination operand.",
            (_, "add" or "adds") => "Adds the operands and stores the sum.",
            (_, "sub" or "subs") => "Subtracts the second source value from the first.",
            (_, "cmp") => "Compares the operands by subtraction and updates the condition flags.",
            (_, "and" or "ands") => "Computes a bitwise AND of the operands.",
            (_, "nop") => "Performs no operation.",
            (false, "push") => "Pushes a value onto the stack.",
            (false, "pop") => "Pops the top of the stack into the destination.",
            (false, "call") => "Pushes the return address and transfers control to the target.",
            (false, "ret") => "Returns to the address popped from the stack.",
            (false, "jmp") => "Jumps to the target address.",
            (false, "je" or "jz") => "Jumps if the zero flag is set.",
            (false, "jne" or "jnz") => "Jumps if the zero flag is clear.",
            (false, "lea") => "Calculates an effective address without reading from memory.",
            (false, "movsx" or "movsxd") => "Copies a value and sign-extends it to the destination size.",
            (false, "movzx") => "Copies a value and zero-extends it to the destination size.",
            (false, "imul") => "Multiplies signed integers.",
            (false, "mul") => "Multiplies unsigned integers.",
            (false, "idiv") => "Divides signed integers, producing a quotient and a remainder.",
            (false, "div") => "Divides unsigned integers, producing a quotient and a remainder.",
            (false, "cdq" or "cqo") => "Sign-extends the value in the accumulator into the register pair used for division.",
            (false, "test") => "Updates condition flags from a bitwise AND without storing the result.",
            (false, "xor") or (true, "eor") => "Computes a bitwise exclusive OR of the operands.",
            (false, "or") or (true, "orr") => "Computes a bitwise OR of the operands.",
            (_, "shr" or "lsr") => "Shifts bits right and fills the vacated bits with zeroes.",
            (_, "sar" or "asr") => "Shifts bits right and preserves the sign in the vacated bits.",
            (_, "shl" or "sal" or "lsl") => "Shifts bits left and fills the vacated bits with zeroes.",
            (true, "ldr" or "ldrb" or "ldrh" or "ldur") => "Loads a value from the effective address into a register.",
            (true, "str" or "strb" or "strh" or "stur") => "Stores a register value to the effective address.",
            (true, "ldp") => "Loads two registers from adjacent memory locations.",
            (true, "stp") => "Stores two registers to adjacent memory locations.",
            (true, "adr" or "adrp") => "Forms an address relative to the current instruction or its page.",
            (true, "bl" or "blr") => "Branches to the target and saves the return address in the link register.",
            (true, "b" or "br") => "Branches to the target address.",
            (true, "ret") => "Branches back to the return address, using the link register by default.",
            (true, "cbz" or "cbnz") => "Tests a register for zero and branches on the result without changing the flags.",
            (true, "tbz" or "tbnz") => "Tests a single register bit and branches on the result without changing the flags.",
            (true, "movz") => "Places an optionally shifted immediate in the destination and clears the other bits.",
            (true, "movk") => "Replaces 16 bits of the destination with an immediate, preserving the other bits.",
            (true, "mul") => "Multiplies two integers and keeps the low bits of the product.",
            (true, "sdiv" or "udiv") => "Divides two integers and stores the quotient.",
            (true, _) when name.StartsWith("b.", StringComparison.Ordinal) => "Branches when the condition flags match the suffix.",
            (false, _) when name.StartsWith('j') => "Jumps when its condition is satisfied.",
            _ => "",
        };
        return new OpcodeHelp(name, description, string.IsNullOrEmpty(instruction.Operands) ? architecture : $"{architecture} · {instruction.Operands}");
    }
}
