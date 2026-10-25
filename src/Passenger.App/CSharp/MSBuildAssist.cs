using System.Text.Json;
using Passenger.App.Editors;
using Passenger.App.MSBuild;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Xaml.Syntax;

namespace Passenger.App.CSharp;

/// <summary>The same popups and commands over a project, .props or .targets file, answered by Passenger's own MSBuild code
/// help (<see cref="MSBuildService"/>): completion as it is typed (after a tag's <c>&lt;</c>, an end tag's <c>/</c>, a
/// value's quote, <c>$(</c>, <c>@(</c>, <c>%(</c>, a list's <c>;</c> and a path's separator), the hover card and Go to
/// Declaration. A package id chosen for a reference goes in with its version, or under central package management with
/// its version in Directory.Packages.props.</summary>
internal sealed class MSBuildAssist : LanguageAssist
{
    private readonly AssistContext context;
    private readonly PopupPlacement popups;
    private readonly CodeCompletion completion;
    /// <summary>The MSBuild items of the last list made, by its rows' items, which a commit and the card beside it look up.</summary>
    private readonly Dictionary<CompletionItem, MSBuildItem> msbuildOffered = [];

    public MSBuildAssist(AssistContext context, PopupPlacement popups, CodeCompletion completion)
    {
        this.context = context;
        this.popups = popups;
        this.completion = completion;
    }

    private CodeEditor Editor => context.Editor;

    public override bool Typed(string text)
    {
        if (context.MSBuildCurrent is null)
        {
            return false;
        }
        char last = text[^1];
        int caret = Editor.Selections.Primary.Caret;
        if (MSBuildTrigger(text) || (AssistContext.IsIdentifierChar(last) && !char.IsDigit(last) && context.WordStart(caret) == caret - 1))
        {
            IdeErrors.Observe(completion.Start(null));
        }
        return true;
    }

    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (context.MSBuildCurrent is not { } msbuild)
        {
            return false;
        }
        await StartMSBuildCompletion(msbuild);
        return true;
    }

    public override void Commit(CompletionItem item, int start)
    {
        if (msbuildOffered.TryGetValue(item, out MSBuildItem? offered))
        {
            CommitMSBuild(offered, start);
        }
    }

    /// <summary>The card beside the list: MSBuild's documentation of the item chosen, where its schemas have some.</summary>
    public override string? Describe(CompletionItem item) => msbuildOffered.GetValueOrDefault(item)?.Documentation;

    public override async Task<bool> HoverAsync(InfoCard card, int offset, CancellationToken cancel)
    {
        if (context.MSBuildCurrent is not { } msbuild)
        {
            return false;
        }
        await MSBuildHover(card, msbuild, offset, cancel);
        return true;
    }

    public override async Task<bool> DefinitionAsync(int offset)
    {
        if (context.MSBuildCurrent is not { } msbuild)
        {
            return false;
        }
        await MSBuildGoTo(msbuild, offset);
        return true;
    }

    /// <summary>Whether what was just typed in an MSBuild file opens completion.</summary>
    private bool MSBuildTrigger(string typed)
    {
        if (typed.Length != 1)
        {
            return false;
        }
        TextSnapshot text = Editor.Document.Snapshot;
        int caret = Editor.Selections.Primary.Caret;
        char before = caret >= 2 ? text[caret - 2] : '\0';
        return typed[0] switch
        {
            '<' => true,
            '/' or '\\' => before == '<' || InValue(text, caret),
            '"' or '\'' => before == '=',
            '(' => before is '$' or '@' or '%',
            ';' or '.' => InValue(text, caret),
            _ => false,
        };
    }

    /// <summary>Whether the caret is in an attribute's value or an element's text, where a list's separator or a path's
    /// starts its next part.</summary>
    private static bool InValue(TextSnapshot text, int caret)
    {
        for (int at = caret - 1; at >= 0 && at > caret - 400; at--)
        {
            switch (text[at])
            {
                case '"' or '\'':
                    return at > 0 && text[at - 1] == '=';
                case '>':
                    return true;
                case '<' or '\n':
                    return false;
            }
        }
        return false;
    }

    /// <summary>The MSBuild list for the caret.</summary>
    private async Task StartMSBuildCompletion(MSBuildDocument msbuild)
    {
        CancellationTokenSource cancel = completion.Asking();
        int caret = Editor.Selections.Primary.Caret;
        TextDocument asked = Editor.Document;
        try
        {
            MSBuildCompletionList? list = await msbuild.CompleteAsync(caret, cancel.Token);
            if (cancel.IsCancellationRequested || Editor.Document != asked)
            {
                return;
            }
            if (list is null || list.Items.Count == 0)
            {
                completion.Close();
                return;
            }
            msbuildOffered.Clear();
            List<CompletionItem> items = [];
            foreach (MSBuildItem offered in list.Items)
            {
                CompletionItem item = new() { Label = offered.Label, Kind = KindOf(offered.Kind), Detail = offered.Detail, SortText = offered.SortText };
                msbuildOffered[item] = offered;
                items.Add(item);
            }
            completion.Open(new CodeCompletion.Completion(items, Math.Min(list.Start, Editor.Selections.Primary.Caret), [], list.Incomplete)
            {
                Own = this,
                WordCharacters = list.WordCharacters,
                // An MSBuild list keeps its own order among names that match alike (the common properties first), not the shortest.
                Alike = 1000,
                DescribedAsItOpens = true,
            });
        }
        catch (OperationCanceledException)
        {
            // Typed on: the list is asked for again.
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
    }

    /// <summary>An MSBuild item in place of what was typed from <paramref name="start"/>: a snippet's caret where its
    /// <c>$0</c> is, a package with its version, and completion opened again when the item says (a property's values).</summary>
    private void CommitMSBuild(MSBuildItem item, int start)
    {
        int caret = Editor.Selections.Primary.Caret;
        TextRange replaced = TextRange.FromBounds(start, Math.Max(start, caret));
        if (item.Package is { } package && context.MSBuildCurrent is { } msbuild)
        {
            CommitPackage(msbuild, package, replaced);
            return;
        }
        if (item.IsSnippet)
        {
            Editor.InsertSnippet(replaced, Snippet.FromProtocol(item.Insert));
        }
        else
        {
            // The caret after what went in, also where nothing had been typed (an insertion at the caret leaves it before).
            Editor.ApplyEdits([new TextEdit(replaced.Start, replaced.Length, item.Insert)], SelectionSet.Single(replaced.Start + item.Insert.Length));
        }
        if (item.Reopen)
        {
            IdeErrors.Observe(completion.Start(null));
        }
    }

    /// <summary>A package id chosen for an item's Include: a reference that names no version takes the feed's latest;
    /// under central package management the reference takes none and Directory.Packages.props a PackageVersion for it
    /// (unless it has one), both one command undone together.</summary>
    private void CommitPackage(MSBuildDocument msbuild, PackageChoice package, TextRange replaced)
    {
        XmlTree tree = msbuild.Tree;
        XmlPlace place = tree.PlaceAt(replaced.Start);
        XmlElement? element = place.Element;
        XmlAttribute? attribute = place.Attribute;
        string? props = msbuild.Insight?.PackagesProps;
        bool central = props is not null && element?.LocalName == "PackageReference";
        List<TextEdit> edits = [new TextEdit(replaced.Start, replaced.Length, package.Id)];
        if (!central && element is not null && attribute is { Name: "Include", IsValueClosed: true } && element.Attribute("Version") is null
            && element.Attribute("VersionOverride") is null && !element.Children.Any(c => c.LocalName is "Version" or "VersionOverride"))
        {
            edits.Add(new TextEdit(attribute.ValueEnd + 1, 0, $" Version=\"{package.Version}\""));
        }
        string? propsText = central ? context.ReadOpenOrDisk(props!) : null;
        TextEdit? pinned = propsText is null ? null : CentralVersion(propsText, package);
        using CommandHistory.Recording? recording = pinned is null ? null : context.Editors.Commands.Record($"Add {package.Id}");
        if (pinned is not null)
        {
            recording!.Watch(props!);
        }
        Editor.ApplyEdits(edits, SelectionSet.Single(replaced.Start + package.Id.Length));
        if (pinned is not { } version)
        {
            return;
        }
        JsonElement edit = JsonSerializer.SerializeToElement(new
        {
            changes = new Dictionary<string, List<LspTextEdit>>
            {
                [DocumentUris.FromPath(props!)] = ServerPlaces.EditsOf(TextSnapshot.From(propsText!), [version]),
            },
        }, Json.Options);
        List<string> dropped = [];
        try
        {
            WorkspaceEdits.Apply(context.Editors, edit, null, dropped);
            context.Say(dropped.Count > 0 ? $"{Path.GetFileName(props)} was edited meanwhile: add {package.Id}'s version there"
                : $"{package.Id} {package.Version} added to {Path.GetFileName(props)}, as central package management has it");
        }
        catch (WorkspaceEditFailed failed)
        {
            context.Say($"{package.Id}'s version could not be added to {Path.GetFileName(props)}: {failed.Message}");
        }
    }

    /// <summary>The PackageVersion a package takes in Directory.Packages.props: beside the others, in their order, with
    /// their indent; in an ItemGroup of its own before the end when there are none. Null when it has one.</summary>
    private static TextEdit? CentralVersion(string text, PackageChoice package)
    {
        XmlTree tree = XmlTree.Parse(text);
        List<XmlElement> versions = [.. tree.Elements.Where(e => e.LocalName == "PackageVersion" && MSBuildSyntax.RoleOf(e) == MSBuildRole.Item)];
        if (versions.Any(v => string.Equals(v.Attribute("Include")?.Value.Trim(), package.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        string eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string line = $"<PackageVersion Include=\"{package.Id}\" Version=\"{package.Version}\" />";
        if (versions.Count > 0)
        {
            List<XmlElement> siblings = [.. versions.GroupBy(v => v.Parent).MaxBy(g => g.Count())!];
            XmlElement? after = siblings.LastOrDefault(s => string.Compare(s.Attribute("Include")?.Value.Trim(), package.Id, StringComparison.OrdinalIgnoreCase) < 0);
            XmlElement anchor = after ?? siblings[0];
            int lineStart = text.LastIndexOf('\n', Math.Max(0, anchor.Start - 1)) + 1;
            string indent = text[lineStart..anchor.Start];
            return after is not null
                ? new TextEdit(after.End, 0, eol + indent + line)
                : new TextEdit(lineStart, 0, indent + line + eol);
        }
        if (tree.Root is not { EndTagStart: { } end })
        {
            return null;
        }
        int closing = text.LastIndexOf('\n', Math.Max(0, end - 1)) + 1;
        return new TextEdit(closing, 0, $"  <ItemGroup>{eol}    {line}{eol}  </ItemGroup>{eol}");
    }

    /// <summary>Go to Declaration in an MSBuild file: straight to the one place a name leads, or the places to choose
    /// from, the one in force first.</summary>
    private async Task MSBuildGoTo(MSBuildDocument msbuild, int offset)
    {
        if (await msbuild.TargetsAsync(offset) is not { Targets.Count: > 0 } found)
        {
            context.Say(Refusals.NoDeclaration);
            return;
        }
        if (found.Targets.Count == 1)
        {
            ArriveAt(found.Targets[0].Place);
            return;
        }
        string root = Path.GetDirectoryName(msbuild.Project ?? msbuild.Document.Path)!;
        List<Choice> choices = [.. found.Targets.Select(t => new Choice(t.Title,
            PathNames.Shown(t.Place.Path, root), t))];
        popups.ShowModal(new ChoicePopup($"Declarations of {found.Symbol.Name}", choices), offset, "Go to Declaration", choice =>
        {
            if (choice?.Value is MSBuildTarget target)
            {
                ArriveAt(target.Place);
            }
            return Task.CompletedTask;
        });
    }

    private void ArriveAt(MSBuildPlace place) =>
        context.Navigate((place.Path, new TextPosition(Math.Max(0, place.Line - 1), Math.Max(0, place.Column - 1))));

    /// <summary>The hover card for a name in an MSBuild file: what it is (a property and its value) as code, then where it
    /// was set and MSBuild's documentation.</summary>
    private static async Task<bool> MSBuildHover(InfoCard hoverCard, MSBuildDocument msbuild, int offset, CancellationToken cancel)
    {
        if (await msbuild.DescribeAsync(offset) is not { } described || cancel.IsCancellationRequested)
        {
            return false;
        }
        string[] lines = described.Split('\n', StringSplitOptions.TrimEntries);
        hoverCard.AddCode(lines[0]);
        foreach (string line in lines.Skip(1).Where(l => l.Length > 0))
        {
            hoverCard.AddText(line);
        }
        return true;
    }

    /// <summary>The badge an MSBuild item shows, by the protocol's kinds.</summary>
    private static CompletionItemKind KindOf(MSBuildItemKind kind) => kind switch
    {
        MSBuildItemKind.Element or MSBuildItemKind.ClosingTag => CompletionItemKind.Class,
        MSBuildItemKind.Property or MSBuildItemKind.PropertyReference => CompletionItemKind.Property,
        MSBuildItemKind.Item or MSBuildItemKind.ItemReference => CompletionItemKind.Struct,
        MSBuildItemKind.Metadata or MSBuildItemKind.Attribute => CompletionItemKind.Field,
        MSBuildItemKind.Task or MSBuildItemKind.Target => CompletionItemKind.Method,
        MSBuildItemKind.Value or MSBuildItemKind.Version => CompletionItemKind.EnumMember,
        MSBuildItemKind.Package or MSBuildItemKind.Sdk => CompletionItemKind.Module,
        MSBuildItemKind.File or MSBuildItemKind.Folder => CompletionItemKind.Operator,
        _ => CompletionItemKind.Text,
    };
}
