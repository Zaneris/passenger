using Passenger.Core;
using Passenger.Xaml.Metadata;

namespace Passenger.Xaml.Features;

/// <summary>A symbol of a project's C# as its XAML names it, from its documentation id (<c>P:App.MainViewModel.Greeting</c>)
/// and where the name of its declaration is: a type by its name (a markup extension's without <c>Extension</c> too), a
/// property, field, method or event of one by its own, and the members the MVVM Toolkit makes of it (a field's property, a
/// method's command and cancel command), which share its place. A method is named by XAML without its parameters, so every overload is it.
/// Its uses in a file are those <see cref="XamlNavigation.Uses"/> finds by its names and <see cref="Is"/>.</summary>
public sealed class CodeSymbol
{
    private readonly char kind;
    private readonly string type;
    private readonly string name;
    private readonly SourcePlace? declared;

    private CodeSymbol(char kind, string type, string name, SourcePlace? declared)
    {
        this.kind = kind;
        this.type = type;
        this.name = name;
        this.declared = declared;
    }

    /// <summary>The symbol of a documentation id, declared at <paramref name="declared"/> (Roslyn's lines and columns, from
    /// zero); null for one XAML never names: a namespace, a constructor, an operator.</summary>
    public static CodeSymbol? Of(string id, SourcePlace? declared)
    {
        if (id.Length < 3 || id[1] != ':' || "TPFME".IndexOf(id[0], StringComparison.Ordinal) < 0)
        {
            return null;
        }
        string full = id[2..];
        if (id[0] == 'T')
        {
            int last = full.LastIndexOf('.');
            return new CodeSymbol('T', full, full[(last + 1)..].Split('`')[0], declared);
        }
        int parameters = full.IndexOf('(', StringComparison.Ordinal);
        string member = parameters < 0 ? full : full[..parameters];
        member = member.Split("``")[0];
        int dot = member.LastIndexOf('.');
        string own = member[(dot + 1)..];
        return dot <= 0 || own.StartsWith('#') || own.StartsWith("op_", StringComparison.Ordinal) ? null : new CodeSymbol(id[0], member[..dot], own, declared);
    }

    /// <summary>The words a XAML file must hold to use it, known without its project's types: its name, a markup
    /// extension's without <c>Extension</c> too, and the property and the commands the MVVM Toolkit makes of a field and a
    /// method. A file holding none of them has no use of it, so its project's C# need not be read to know.</summary>
    private IReadOnlyList<string> Spellings => kind switch
    {
        'T' => name.EndsWith("Extension", StringComparison.Ordinal) && name.Length > "Extension".Length ? [name, name[..^"Extension".Length]] : [name],
        'F' when ToolkitNames.PropertyOf(name) is { } property => [name, property],
        'M' => [name, ToolkitNames.CommandOf(name), ToolkitNames.CancelCommandOf(name)],
        _ => [name],
    };

    /// <summary>Whether a XAML text may use it, known without its project's types: it holds a spelling, in any letter case, as
    /// XAML reads an enum's member. A text that does not has no use of it.</summary>
    public bool MayBeIn(string text) => Spellings.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>The names a XAML file of a project may write it as.</summary>
    public IReadOnlyList<string> NamesIn(TypeSystem types)
    {
        if (kind == 'T')
        {
            return name.EndsWith("Extension", StringComparison.Ordinal) && name.Length > "Extension".Length ? [name, name[..^"Extension".Length]] : [name];
        }
        List<string> names = [name];
        if (declared is not null && Owner(types) is { } owner)
        {
            names.AddRange(owner.Members.Where(m => IsDeclared(m.Place)).Select(m => m.Name));
        }
        return [.. names.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Whether a place a XAML name leads to is the symbol.</summary>
    public bool Is(XamlTarget target)
    {
        if (kind == 'T')
        {
            return target is { Kind: XamlTargetKind.Type, Type: { } named } && Dotted(named) == type;
        }
        return target is { Kind: XamlTargetKind.Member, Member: { } member }
            && ((member.Name == name && Kind(member.Kind) == kind && Dotted(member.DeclaringType) == type) || IsDeclared(member.Place));
    }

    /// <summary>Whether a use written as <paramref name="written"/> spells the symbol's name (its own, a markup extension's
    /// without Extension, or the property or a command the MVVM Toolkit makes of it), and how once the symbol is named
    /// <paramref name="name"/> (a C# name, an @ before a keyword): <paramref name="renamed"/>, null where the new name makes
    /// no such member.</summary>
    public bool Respells(string written, string name, out string? renamed)
    {
        string bare = name.StartsWith('@') ? name[1..] : name;
        const string Extension = "Extension";
        renamed = null;
        if (written == this.name)
        {
            renamed = bare;
        }
        else if (kind == 'T' && this.name.EndsWith(Extension, StringComparison.Ordinal) && written == this.name[..^Extension.Length])
        {
            renamed = bare.EndsWith(Extension, StringComparison.Ordinal) && bare.Length > Extension.Length ? bare[..^Extension.Length] : bare;
        }
        else if (kind == 'F' && written == ToolkitNames.PropertyOf(this.name))
        {
            renamed = ToolkitNames.PropertyOf(bare);
        }
        else if (kind == 'M' && written == ToolkitNames.CommandOf(this.name))
        {
            renamed = ToolkitNames.CommandOf(bare);
        }
        else if (kind == 'M' && written == ToolkitNames.CancelCommandOf(this.name))
        {
            renamed = ToolkitNames.CancelCommandOf(bare);
        }
        else if (string.Equals(written, this.name, StringComparison.OrdinalIgnoreCase))
        {
            // A name XAML matches in any case (an enum's member) takes the new name's.
            renamed = bare;
        }
        else
        {
            return false;
        }
        return true;
    }

    /// <summary>The name a C# symbol named <paramref name="declared"/> takes for a XAML use written <paramref name="written"/>
    /// to read <paramref name="typed"/>: the field or method the MVVM Toolkit makes that property or a command of, where the
    /// use is one (null where the toolkit makes no such name of any), else the name typed.</summary>
    public static string? NameFor(string declared, string written, string typed) =>
        written == declared ? typed
        : written == ToolkitNames.PropertyOf(declared) ? ToolkitNames.FieldFor(declared, typed)
        : written == ToolkitNames.CommandOf(declared) ? ToolkitNames.MethodFor(declared, typed)
        : written == ToolkitNames.CancelCommandOf(declared) ? ToolkitNames.MethodForCancel(declared, typed)
        : typed;

    /// <summary>Whether a member is declared where the symbol is: the symbol itself, or a member the MVVM Toolkit makes of it.</summary>
    public bool IsDeclaredAs(ClrMember member) => IsDeclared(member.Place);

    /// <summary>The type a member is declared in, its nested types' names joined with '+' as metadata joins them where a
    /// documentation id joins them with '.'.</summary>
    private ClrType? Owner(TypeSystem types)
    {
        string full = type;
        while (true)
        {
            if (types.Find(full) is { } found)
            {
                return found;
            }
            int dot = full.LastIndexOf('.');
            if (dot < 0)
            {
                return null;
            }
            full = $"{full[..dot]}+{full[(dot + 1)..]}";
        }
    }

    private bool IsDeclared(SourcePlace? place) =>
        place is not null && declared is not null && place.Line == declared.Line && place.Column == declared.Column && PathNames.Same(place.Path, declared.Path);

    private static string Dotted(ClrType type) => type.FullName.Replace('+', '.');

    private static char Kind(MemberKind kind) => kind switch
    {
        MemberKind.Property => 'P',
        MemberKind.Field => 'F',
        MemberKind.Event => 'E',
        _ => 'M',
    };
}
