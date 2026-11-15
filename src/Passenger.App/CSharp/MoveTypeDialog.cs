using Avalonia.Controls;
using Passenger.Core;
using Passenger.Roslyn.Extension;

namespace Passenger.App.CSharp;

/// <summary>The Move to Another File and Move to Namespace dialogs for a type: the new file's name, or the namespace it
/// is to be in.</summary>
public sealed class MoveTypeDialog : RefactoringDialog
{
    private readonly bool toFile;
    private readonly TypeSource type;

    /// <summary>The folder of the type's file, which the new file goes beside; empty for Move to Namespace.</summary>
    private readonly string folder;
    private readonly Func<string, Task<string?>> make;

    private MoveTypeDialog(TypeSource type, bool toFile, string folder, Func<string, Task<string?>> make)
        : base(toFile ? "Move to Another File" : "Move to Namespace", toFile ? "Specify a name of a file" : "Specify namespace",
            toFile
                ? $"Specify the name of a new file beside {type.FileName} to move {type.Kind} '{type.Name}' to"
                : $"Specify the namespace to move {type.Kind} '{type.Name}' to{(type.Namespace.Length > 0 ? $" from '{type.Namespace}'" : string.Empty)}; the code that uses it follows",
            480)
    {
        this.type = type;
        this.toFile = toFile;
        this.folder = folder;
        this.make = make;
        TargetBox = Field(toFile ? type.TargetFileName : type.Namespace);
        TargetBox.TextChanged += (_, _) => Changed();
        Options = Row(toFile ? "Target file name:" : "Target namespace:", TargetBox, 140);
        Changed();
        FocusOnOpen(TargetBox);
    }

    public static MoveTypeDialog ToFile(TypeSource type, string folder, Func<string, Task<string?>> make) => new(type, toFile: true, folder, make);

    public static MoveTypeDialog ToNamespace(TypeSource type, Func<string, Task<string?>> make) => new(type, toFile: false, string.Empty, make);

    public TextBox TargetBox { get; }

    protected override string? Check()
    {
        string target = TargetBox.Text?.Trim() ?? string.Empty;
        if (!toFile)
        {
            return RefactoringChecks.WhyNotNamespace(target, type.Name, type.Namespace);
        }
        return RefactoringChecks.WhyNotFileName(target)
            ?? (PathNames.NamesIn(folder).Equals(target, type.FileName) ? $"'{type.Name}' is in {type.FileName} already" : null);
    }

    protected override Task<string?> MakeAsync() => make(TargetBox.Text?.Trim() ?? string.Empty);
}
