using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Rules the code editor keeps as a control of its own: it draws a text, its line numbers and its folds, and the
/// gutter's other columns through <see cref="IGutterColumn"/> without knowing what they show, so a new kind of mark
/// changes the IDE and never the editor; and it runs no action of the IDE's, which the IDE's keymap binds.</summary>
[Collection(Beside.Name)]
public sealed partial class EditorRulesTests
{
    /// <summary>Words of the debugger's, the tests' and version control's, as a name in the editor would carry them.</summary>
    private static readonly HashSet<string> Foreign = new(StringComparer.OrdinalIgnoreCase)
    {
        "Breakpoint", "Breakpoints", "Tracepoint", "Tracepoints", "Execution", "Debug", "Debugger", "Debugging", "Debuggee", "Dap",
        "Test", "Tests", "Testing", "Xunit", "Blame", "Commit", "Commits", "Vcs", "Git",
    };

    /// <summary>Words of the IDE's actions, as a name in the editor would carry one it ran.</summary>
    private static readonly HashSet<string> IdeActionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Navigate", "Navigates", "Declaration", "Implementation", "Implementations", "Documentation",
    };

    /// <summary>The editor names no type, member, parameter or assembly of the debugger, the tests or version control: its
    /// own declarations and what it references hold none of their words. Passed over: what the compiler makes (a property's
    /// accessors are the property's), a member that overrides or implements one declared outside the editor (Avalonia's
    /// HitTest), and the theme's keys, which the App's XAML names and the analyzers write into the editor for every project.</summary>
    [Fact]
    public void The_editor_names_nothing_of_the_debugger_the_tests_or_version_control()
    {
        List<string> found = Naming(Foreign);
        Assert.True(found.Count == 0, $"Passenger.Editor names {found.Count} things of the debugger, the tests or version control, so a change to "
            + $"any of them changes the editor: give that area's mark to the gutter as an IGutterColumn of the IDE's, and keep in the editor only how a thing is "
            + $"drawn:\n{string.Join("\n", found)}");
    }

    /// <summary>The editor names no action of the IDE's: no declaration of its own holds their words, and its keymaps bind
    /// the editor's own actions alone, by key and by gesture. A press of the mouse that runs one of the IDE's (Cmd+click's Go
    /// to Declaration) is the window's keymap's, which the editor reports as the gesture's action and the IDE runs.</summary>
    [Fact]
    public void The_editor_names_no_action_of_the_IDE()
    {
        List<string> found = Naming(IdeActionWords);
        HashSet<string> own = [.. typeof(EditorActions).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!)];
        foreach (Keymap keymap in (Keymap[])[DefaultKeymaps.VisualStudio(), DefaultKeymaps.VisualStudioMac(), DefaultKeymaps.VSCode(), DefaultKeymaps.VSCodeMac()])
        {
            found.AddRange(keymap.Actions.Concat(keymap.MouseActions).Where(action => !own.Contains(action)).Select(action => $"{keymap.Name}'s binding of {action}"));
        }
        Assert.True(found.Count == 0, $"Passenger.Editor names {found.Count} things of the IDE's actions, so a change to one changes the editor: bind the "
            + $"IDE's action in AppActions' keymaps, a gesture of the mouse too, and have the editor report what is pressed:\n{string.Join("\n", found)}");
    }

    /// <summary>The editor's declarations and references whose names hold any of these words, each as where it is and the
    /// word.</summary>
    private static List<string> Naming(HashSet<string> words)
    {
        Assembly editor = typeof(CodeEditor).Assembly;
        List<string> found = [];
        void Check(string name, string what)
        {
            if (Words().Matches(name).Select(m => m.Value).FirstOrDefault(words.Contains) is { } word)
            {
                found.Add($"{what} ({word})");
            }
        }
        foreach (AssemblyName reference in editor.GetReferencedAssemblies())
        {
            Check(reference.Name!, $"the reference to {reference.Name}");
        }
        foreach (Type type in editor.GetTypes())
        {
            if (Generated(type) || type.Name is nameof(TokenKeys) or nameof(IconKeys))
            {
                continue;
            }
            Check(type.Name, type.FullName!);
            HashSet<MethodInfo> outside = [.. type.GetInterfaces().Where(i => i.Assembly != editor && !type.IsInterface).SelectMany(i => type.GetInterfaceMap(i).TargetMethods)];
            const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            foreach (MemberInfo member in type.GetMembers(Declared))
            {
                if (member.Name.Contains('<', StringComparison.Ordinal) || member.IsDefined(typeof(CompilerGeneratedAttribute)) || member is MethodBase { IsSpecialName: true }
                    || Inherited(member, editor, outside))
                {
                    continue;
                }
                Check(member.Name, $"{type.FullName}.{member.Name}");
                if (member is MethodBase method)
                {
                    foreach (ParameterInfo parameter in method.GetParameters())
                    {
                        Check(parameter.Name ?? string.Empty, $"{type.FullName}.{member.Name}'s {parameter.Name}");
                    }
                }
            }
        }
        return found;
    }

    /// <summary>What the compiler makes: a lambda's closure, an iterator, a record's or an attribute's own.</summary>
    private static bool Generated(Type type) =>
        type.Name.Contains('<', StringComparison.Ordinal) || type.IsDefined(typeof(CompilerGeneratedAttribute)) || type.Namespace is "Microsoft.CodeAnalysis";

    /// <summary>Whether a member takes its name from one declared outside the editor: an override, or an interface's.</summary>
    private static bool Inherited(MemberInfo member, Assembly editor, HashSet<MethodInfo> outside)
    {
        IEnumerable<MethodInfo> methods = member switch
        {
            MethodInfo method => [method],
            PropertyInfo property => property.GetAccessors(nonPublic: true),
            EventInfo shown => [.. new[] { shown.AddMethod, shown.RemoveMethod }.OfType<MethodInfo>()],
            _ => [],
        };
        return methods.Any(m => m.GetBaseDefinition().DeclaringType?.Assembly != editor || outside.Contains(m));
    }

    /// <summary>A name's words: each capital's run of lower-case letters, a run of capitals, a run of digits.</summary>
    [GeneratedRegex("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+")]
    private static partial Regex Words();
}
