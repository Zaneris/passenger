// What the terminal's tests run in it, the same bytes on every system: on Windows the console is first set to read and
// write VT sequences and UTF-8, as a program drawing in a terminal does there. The first word is the case.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

Terminal.Prepare();
// The bytes as they are, on macOS and Linux past Console, which edits an input line itself and asks for the keypad's
// application keys as it first writes.
Stream output = OperatingSystem.IsWindows() ? Console.OpenStandardOutput() : Descriptor(1, FileAccess.Write);
Stream input = OperatingSystem.IsWindows() ? Console.OpenStandardInput() : Descriptor(0, FileAccess.Read);
switch (args)
{
    case ["draw", string columns]:
        // As vim or less draw: the alternate screen, cursor moves, partial erases, a scroll region, colours, a wide
        // character at the right edge; then the shell's screen back once a line is typed.
        Write("\e[?1049h\e[H\e[2J"
            + "1111111111\r\n2222222222\r\n3333333333\r\n4444444444\e[2;3H\e[K\e[3;5H\e[1K\e[4;1H\e[2K!"
            + "\e[5;1Htop\r\na\r\nb\r\nc\r\nbottom\e[6;8r\e[8;1H\nd\e[r"
            + "\e[10;1H\e[1;31mA\e[38;5;208mB\e[38;2;10;20;30mC\e[38:2::40:50:60mD\e[0;44mE\e[0mF"
            + $"\e[11;{int.Parse(columns) - 3}Habcd漢");
        Console.ReadLine();
        Write("\e[?1049l");
        break;
    case ["ask"]:
        // Where the cursor is, as a program asks before drawing, and what the terminal is, each answer read back.
        using (Terminal.Raw())
        {
            Write("\e[5;10H\e[6n");
            string at = ReadTo('R');
            Write("\e[c");
            string kind = ReadTo('c');
            Terminal.Restored();
            Write($"\r\nat {at[2..^1]} is {kind[2..^1]}\r\n");
        }
        break;
    case ["keys", .. string[] modes]:
        // The keys as read, as cat -v shows them, a line at Enter, until Ctrl+C; "application" first asks for the
        // application cursor keys.
        if (modes is ["application"])
        {
            Write("\e[?1h");
        }
        using (Terminal.Raw())
        {
            Write("keys?\r\n");
            StringBuilder line = new();
            for (int b = input.ReadByte(); b is >= 0 and not 3; b = input.ReadByte())
            {
                if (b == '\r')
                {
                    Write($"{line}\r\n");
                    line.Clear();
                }
                else
                {
                    line.Append(b < 32 ? $"^{(char)(b + 64)}" : ((char)b).ToString());
                }
            }
        }
        if (modes is ["application"])
        {
            Write("\e[?1l");
        }
        break;
    case ["lines", string from, string to]:
        Lines(long.Parse(from), long.Parse(to));
        break;
    case ["long", string width]:
        // A line one wider than the screen, then 200 numbered lines past it.
        Write(new string('0', int.Parse(width)) + "7\n");
        Lines(1, 200);
        break;
    case ["size"]:
        Write($"{Console.WindowHeight} {Console.WindowWidth}\n");
        break;
    case ["hundred"]:
        Write(string.Concat(Enumerable.Range(10, 50)) + "\n");
        break;
    case ["accent"]:
        // Résumé.pdf as macOS's file system spells it, each é an e and a combining acute.
        Write("Résumé.pdf\n");
        break;
    case ["emoji"]:
        // A rocket, then a check mark and a lightning bolt, each two columns wide.
        Write("\U0001F680x\n✅⚡y\n");
        break;
    case ["wait", string file, string from, string to]:
        // Lines that come once the test has made the file.
        while (!File.Exists(file))
        {
            Thread.Sleep(50);
        }
        Lines(long.Parse(from), long.Parse(to));
        break;
    default:
        Console.Error.WriteLine($"No case '{string.Join(' ', args)}'");
        return 2;
}
return 0;

static Stream Descriptor(int descriptor, FileAccess access) => new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(descriptor, ownsHandle: false), access, 1);

void Write(string text)
{
    output.Write(Encoding.UTF8.GetBytes(text));
    output.Flush();
}

void Lines(long from, long to)
{
    StringBuilder lines = new();
    for (long i = from; i <= to; i++)
    {
        lines.Append(i).Append('\n');
        if (lines.Length > 16 * 1024)
        {
            Write(lines.ToString());
            lines.Clear();
        }
    }
    Write(lines.ToString());
}

string ReadTo(char last)
{
    StringBuilder read = new();
    for (int b = input.ReadByte(); b >= 0; b = input.ReadByte())
    {
        read.Append((char)b);
        if (b == last)
        {
            break;
        }
    }
    return read.ToString();
}

/// <summary>The console as a program drawing in a terminal sets it: UTF-8 and VT sequences both ways on Windows, and a raw
/// mode (no echo, no line editing, no signals from keys) while it reads keys itself, put back after.</summary>
internal static class Terminal
{
    private const int StdInput = -10;
    private const int StdOutput = -11;

    public static void Prepare()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
            nint output = GetStdHandle(StdOutput);
            GetConsoleMode(output, out uint mode);
            // Processed output, wrap at the line's end, and VT processing.
            SetConsoleMode(output, mode | 0x1 | 0x2 | 0x4);
        }
    }

    private static string? kept;
    private static uint keptMode;

    public static IDisposable Raw()
    {
        if (OperatingSystem.IsWindows())
        {
            nint input = GetStdHandle(StdInput);
            GetConsoleMode(input, out keptMode);
            // VT input alone: no line input, no echo, no Ctrl+C as a signal.
            SetConsoleMode(input, 0x200);
        }
        else
        {
            kept = Stty("-g").Trim();
            Stty("raw", "-echo");
        }
        return new Restore();
    }

    public static void Restored()
    {
        if (OperatingSystem.IsWindows())
        {
            SetConsoleMode(GetStdHandle(StdInput), keptMode);
        }
        else if (kept is { } settings)
        {
            Stty(settings);
            kept = null;
        }
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => Restored();
    }

    private static string Stty(params string[] words)
    {
        ProcessStartInfo start = new("stty", words) { UseShellExecute = false, RedirectStandardOutput = true };
        using Process stty = Process.Start(start)!;
        string said = stty.StandardOutput.ReadToEnd();
        stty.WaitForExit();
        return said;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(nint handle, uint mode);
}
