using Passenger.App.Navigation;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Syntax;

namespace Passenger.App.Xaml;

/// <summary>A XAML file's outline, the window's and an agent's: its elements as Passenger's XAML reader reads them
/// (<see cref="XmlTree"/>), each under the one holding it and spanning its lines; one by what names it, its type after
/// (<c>Save: Button</c>): its <c>x:Name</c> or <c>Name</c>, else a resource's <c>x:Key</c>, a style's selector or target
/// type, the root's <c>x:Class</c>, any other of those and an <c>x:DataType</c> written after its type; another by its type
/// as written, and a property element (<c>Grid.RowDefinitions</c>) as written, a property. Read from the open file's text as
/// the editor has it, off the window's thread.</summary>
internal sealed class XamlOutline(TextDocument document) : FileOutline
{
    public override Task<Outline<SymbolRow>> ReadAsync(string path, Outline<SymbolRow>? before, CancellationToken cancel)
    {
        TextSnapshot text = document.Snapshot;
        return BackgroundWork.Run(() => Of(text, path, before, cancel), cancel);
    }

    /// <summary>The outline of a XAML text, its rows leading to <paramref name="path"/>.</summary>
    public static Outline<SymbolRow> Of(TextSnapshot text, string path, Outline<SymbolRow>? before = null, CancellationToken cancel = default) =>
        Of(XmlTree.Parse(text.ToString()), text, path, before, cancel);

    /// <summary>The outline of a XAML text as <paramref name="tree"/> read it.</summary>
    public static Outline<SymbolRow> Of(XmlTree tree, TextSnapshot text, string path, Outline<SymbolRow>? before = null, CancellationToken cancel = default)
    {
        return Outline<SymbolRow>.Of<XmlElement>(tree.Roots, element => element.Children, (element, depth) =>
            (RowOf(element, text, path, depth), text.GetLineFromOffset(element.Start), text.GetLineFromOffset(Math.Max(element.Start, element.End - 1))), before, cancel);
    }

    private static SymbolRow RowOf(XmlElement element, TextSnapshot text, string path, int depth)
    {
        TextPosition at = text.GetPosition(element.NameStart);
        if (element.IsPropertyElement)
        {
            return new SymbolRow(element.Name, null, null, SymbolKind.Property, path, at, depth);
        }
        XmlAttribute[] naming = [.. ((XmlAttribute?[])[XamlFile.NameAttribute(element), XamlFile.Directive(element, "Key"), element.Attribute("Selector"),
            element.Attribute("TargetType"), XamlFile.Directive(element, "Class")]).OfType<XmlAttribute>().Where(a => !string.IsNullOrWhiteSpace(a.Value))];
        string besides = string.Concat(naming.Skip(1).Append(XamlFile.Directive(element, "DataType")).OfType<XmlAttribute>().Select(a => $" {a.Name}=\"{a.Value}\""));
        return naming is [var named, ..]
            ? new SymbolRow(named.Value, $": {element.Name}{besides}", null, SymbolRow.ElementKind, path, at, depth)
            : new SymbolRow(element.Name, besides.Length == 0 ? null : besides, null, SymbolRow.ElementKind, path, at, depth);
    }
}
