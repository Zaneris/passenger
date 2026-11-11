using Passenger.Core;
using Passenger.Lsp;

namespace Passenger.UnitTests.Lsp;

/// <summary>Usages a server answered and those Passenger found beside it, put together: one the server named already is
/// kept once however each URI spells its file (through a link, in other letter case where the file system ignores it, a
/// Windows drive as vscode-uri writes it), and one at another place or in another file is kept beside it.</summary>
public sealed class DocumentUrisTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_usage_the_server_named_by_another_spelling_of_its_file_is_kept_once()
    {
        string game = Directory.CreateDirectory(Path.Combine(root, "Game")).FullName;
        string player = Path.Combine(game, "Player.gd");
        File.WriteAllText(player, "extends Node\n");
        LspLocation served = At(new Uri(player).AbsoluteUri, 3, 4);
        List<(string Spelling, string Uri)> spellings = [("the same URI", served.Uri), ("the path's own URI", DocumentUris.FromPath(player))];
        if (!OperatingSystem.IsWindows())
        {
            string link = Path.Combine(root, "linked");
            Directory.CreateSymbolicLink(link, game);
            spellings.Add(("a link to its folder", new Uri(Path.Combine(link, "Player.gd")).AbsoluteUri));
        }
        if (PathNames.CaseIgnoredIn(game))
        {
            spellings.Add(("other letter case", new Uri(Path.Combine(root, "game", "player.gd")).AbsoluteUri));
        }
        if (OperatingSystem.IsWindows())
        {
            // vscode-uri's spelling: a lowercase drive, its colon escaped.
            spellings.Add(("a drive as vscode-uri writes it", $"file:///{char.ToLowerInvariant(player[0])}%3A{new Uri(player).AbsolutePath[(new Uri(player).AbsolutePath.IndexOf(':') + 1)..]}"));
        }
        foreach ((string spelling, string uri) in spellings)
        {
            Assert.True(DocumentUris.Besides([At(uri, 3, 4)], [served]).Count == 0,
                $"A usage the server named, given again by {spelling} ({uri} beside {served.Uri}), was kept, so Find Usages would list it twice");
        }
    }

    [Fact]
    public void A_usage_at_another_place_or_in_another_file_is_kept_beside_the_servers()
    {
        string player = Path.Combine(root, "player.gd");
        string enemy = Path.Combine(root, "enemy.gd");
        LspLocation served = At(DocumentUris.FromPath(player), 3, 4);
        IReadOnlyList<LspLocation> own = [At(DocumentUris.FromPath(player), 3, 5), At(DocumentUris.FromPath(player), 4, 4), At(DocumentUris.FromPath(enemy), 3, 4)];

        IReadOnlyList<LspLocation> kept = DocumentUris.Besides(own, [served]);
        Assert.True(kept.SequenceEqual(own), $"Usages the server did not name were dropped, so Find Usages would miss them: kept {string.Join(", ", kept)}");
    }

    /// <summary>A rename's new name differing only in letter case from the file there keeps its own spelling, its folder
    /// spelled as the disk spells it, where the file's path as the disk names it would make the rename one of the file onto
    /// itself, which moved nothing.</summary>
    [Fact]
    public void A_new_name_differing_from_the_file_there_only_in_letter_case_keeps_its_own_spelling()
    {
        string game = Directory.CreateDirectory(Path.Combine(root, "Game")).FullName;
        File.WriteAllText(Path.Combine(game, "player.cs"), "class Player;\n");
        string folder = PathNames.CaseIgnoredIn(game) ? Path.Combine(root, "game") : game;

        string renamed = DocumentUris.ToNewPath(new Uri(Path.Combine(folder, "Player.cs")).AbsoluteUri);
        Assert.True(renamed == Path.Combine(game, "Player.cs"), $"player.cs renamed to Player.cs goes to {renamed}, so the rename would move {(renamed == Path.Combine(game, "player.cs") ? "nothing" : "it elsewhere")}");
        Assert.Equal(Path.Combine(game, "Enemy.cs"), DocumentUris.ToNewPath(DocumentUris.FromPath(Path.Combine(game, "Enemy.cs"))));
    }

    [Fact]
    public void Documents_of_no_file_are_the_same_only_by_their_whole_uri()
    {
        const string Generated = "roslyn-source-generated://Spinner/Main_ScriptPath.generated.cs?hintName=Main_ScriptPath.generated.cs";

        Assert.True(DocumentUris.Same(Generated, Generated), "A generated document was not the same as itself");
        Assert.False(DocumentUris.Same(Generated, Generated.Replace("Main_", "Enemy_", StringComparison.Ordinal)), "Two generated documents were taken for one");
    }

    private static LspLocation At(string uri, int line, int character) => new(uri, new LspRange(new LspPosition(line, character), new LspPosition(line, character + 4)));
}
