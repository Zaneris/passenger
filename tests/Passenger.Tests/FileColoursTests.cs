using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Passenger.App;
using Passenger.Editor;
using Passenger.Languages.TextMate;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The files beside the C# in colour: a project file, settings with comments, a CI workflow, a README and a
/// script each open with their tokens in their kinds' colours, in either theme; typing keeps the colours right however
/// far a comment or a code fence it opens or closes reaches; Comment with Line Comment and with Block Comment write
/// each language's own comments; and a tag's name lights up with its pair's.</summary>
public sealed class FileColoursTests : FileColoursFolder
{
    private const string Project = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project Sdk="Microsoft.NET.Sdk">
          <!-- The build: an executable,
               with its analyzers. -->
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <Description>Shapes &amp; their areas</Description>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Serilog" Version="4.0.0" />
            <None Update="appsettings.json">
              <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
            </None>
          </ItemGroup>
        </Project>

        """;

    private const string Settings = """
        {
          // Warning and up, unless a category says otherwise.
          "Logging": {
            "LogLevel": {
              "Default": "Warning"
            }
          },
          /* The hosts it answers on. */
          "AllowedHosts": "*",
          "Retries": 3,
          "UseCache": true
        }

        """;

    private const string Workflow = """
        # Builds and tests every push.
        name: CI
        on:
          push:
            branches: [ main ]
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/setup-dotnet@v4
                with:
                  dotnet-version: '10.0.x'
              - name: Test
                run: dotnet test -c Release
                timeout-minutes: 30

        """;

    private const string Readme = """
        # Shapes

        A library of **shapes** and their *areas*.

        ## Getting started

        Build it with `dotnet build`, from the [.NET SDK](https://dot.net/download).

        ```bash
        dotnet test --filter "Category!=Slow"
        ```

        > Areas are in square units.

        """;

    private const string Script = """
        #!/bin/sh
        # Packs every project.
        set -eu
        output="artifacts/packages"
        for project in src/*/*.csproj; do
            if [ -f "$project" ]; then
                dotnet pack "$project" -o "$output"
            fi
        done

        """;

    private const string Sass = """
        // The theme's colours.
        @use "sass:math";
        $accent: #3b82f6 !default;

        /* A frame round a card. */
        @mixin framed($width) {
            border: $width solid $accent;
        }

        .card > h2 {
            padding: math.div(24px, 2);
            &:hover { color: darken($accent, 10%); }
            @include framed(1px);
            #{$side}-margin: 0;
        }

        """;

    private const string Less = """
        // The theme's colours.
        @accent: #3b82f6;
        @import (reference) "base.less";

        /* A frame round a card. */
        .framed(@width) {
            border: @width solid @accent;
        }

        .card > h2 {
            padding: 12px;
            &:hover { color: darken(@accent, 10%); }
            .framed(1px);
            @{side}-margin: 0;
        }

        """;

    private const string Paml = """
        <!-- The main window. -->
        <Window xmlns="https://github.com/avaloniaui"
                Title="Shapes &amp; areas">
          <TextBlock Text="Hello" />
        </Window>

        """;

    [AvaloniaFact]
    public void A_project_file_settings_a_workflow_a_readme_and_a_script_each_show_their_tokens_in_Riders_colours()
    {
        MainWindow window = Window();
        CodeEditor editor = Open(window, "Shapes.csproj", Project);
        Assert.Equal(TokenKind.Tag, KindOf(editor, "<Project"));
        Assert.Equal(TokenKind.AttributeName, KindOf(editor, "Sdk"));
        Assert.Equal(TokenKind.AttributeValue, KindOf(editor, "\"Microsoft.NET.Sdk\""));
        Assert.Equal(TokenKind.Comment, KindOf(editor, "with its analyzers. -->"));
        Assert.Equal(TokenKind.Tag, KindOf(editor, "</OutputType>"));
        Assert.Equal(TokenKind.Text, KindOf(editor, "Exe<"[..3]));
        Assert.Equal(TokenKind.Entity, KindOf(editor, "&amp;"));

        editor = Open(window, "appsettings.json", Settings);
        Assert.Equal(TokenKind.Comment, KindOf(editor, "// Warning and up, unless a category says otherwise."));
        Assert.Equal(TokenKind.Comment, KindOf(editor, "/* The hosts it answers on. */"));
        Assert.Equal(TokenKind.JsonKey, KindOf(editor, "\"Logging\""));
        Assert.Equal(TokenKind.String, KindOf(editor, "\"Warning\""));
        Assert.Equal(TokenKind.Number, KindOf(editor, "3"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "true"));

        editor = Open(window, Path.Combine(".github", "workflows", "ci.yml"), Workflow);
        Assert.Equal(TokenKind.Comment, KindOf(editor, "# Builds and tests every push."));
        Assert.Equal(TokenKind.YamlKey, KindOf(editor, "runs-on"));
        // A workflow's first key reads as YAML 1.1's boolean, but it is a key.
        Assert.Equal(TokenKind.YamlKey, KindOf(editor, "on:"[..2]));
        Assert.Equal(TokenKind.Text, KindOf(editor, "ubuntu-latest"));
        Assert.Equal(TokenKind.Text, KindOf(editor, "30"));
        Assert.Equal(TokenKind.String, KindOf(editor, "'10.0.x'"));
        Assert.Equal(TokenKind.Text, KindOf(editor, "dotnet test -c Release"));

        editor = Open(window, "README.md", Readme);
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "##"));
        Assert.Equal(TokenKind.Heading, KindOf(editor, "Getting started"));
        Assert.Equal(TokenKind.Bold, KindOf(editor, "shapes"));
        Assert.Equal(TokenKind.Italic, KindOf(editor, "areas"));
        Assert.Equal(TokenKind.Code, KindOf(editor, "`dotnet build`"));
        Assert.Equal(TokenKind.Link, KindOf(editor, ".NET SDK"));
        Assert.Equal(TokenKind.LinkDestination, KindOf(editor, "https://dot.net/download"));
        Assert.Equal(TokenKind.Quote, KindOf(editor, "Areas are in square units."));
        Assert.Equal(TokenKind.String, KindOf(editor, "\"Category!=Slow\""));

        editor = Open(window, "pack.sh", Script);
        Assert.Equal(TokenKind.Comment, KindOf(editor, "# Packs every project."));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "for"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "then"));
        Assert.Equal(TokenKind.String, KindOf(editor, "\"$project\""));
        Assert.Equal(TokenKind.Text, KindOf(editor, "dotnet pack"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_web_page_its_style_and_script_and_a_Dockerfile_show_their_tokens_too()
    {
        MainWindow window = Window();
        CodeEditor editor = Open(window, "index.html", "<!DOCTYPE html>\n<html lang=\"en\">\n<!-- The list. -->\n<ul class=\"list\">&amp;</ul>\n<script>\n    const shapes = [\"circle\"];\n</script>\n</html>\n");
        Assert.Equal(TokenKind.Tag, KindOf(editor, "<ul"));
        Assert.Equal(TokenKind.AttributeName, KindOf(editor, "class"));
        Assert.Equal(TokenKind.AttributeValue, KindOf(editor, "\"list\""));
        Assert.Equal(TokenKind.Comment, KindOf(editor, "<!-- The list. -->"));
        Assert.Equal(TokenKind.Entity, KindOf(editor, "&amp;"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "const"));
        Assert.Equal(TokenKind.String, KindOf(editor, "\"circle\""));

        editor = Open(window, "site.css", "/* The page. */\nbody > .list {\n    margin: 0 auto 12px;\n    color: var(--accent);\n}\n");
        Assert.Equal(TokenKind.Comment, KindOf(editor, "/* The page. */"));
        Assert.Equal(TokenKind.Tag, KindOf(editor, ".list"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "margin"));
        Assert.Equal(TokenKind.Number, KindOf(editor, "12px"));
        Assert.Equal(TokenKind.Method, KindOf(editor, "var"));

        editor = Open(window, "shapes.ts", "// Areas.\nexport class Circle implements Shape {\n    area(): number {\n        return Math.PI * this.radius ** 2;\n    }\n}\n");
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "export"));
        Assert.Equal(TokenKind.Class, KindOf(editor, "Circle"));
        Assert.Equal(TokenKind.Method, KindOf(editor, "area"));
        Assert.Equal(TokenKind.Field, KindOf(editor, "radius"));
        Assert.Equal(TokenKind.Number, KindOf(editor, "2"));

        editor = Open(window, "list.js", "function total(shapes) {\n    return shapes.length; // all of them\n}\n");
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "function"));
        Assert.Equal(TokenKind.Method, KindOf(editor, "total"));
        Assert.Equal(TokenKind.Comment, KindOf(editor, "// all of them"));

        editor = Open(window, "Dockerfile", "# Two stages.\nFROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\nRUN dotnet publish -o /app \"Shapes.csproj\"\n");
        Assert.Equal(TokenKind.Comment, KindOf(editor, "# Two stages."));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "FROM"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "RUN"));
        Assert.Equal(TokenKind.String, KindOf(editor, "\"Shapes.csproj\""));
        window.Close();
    }

    /// <summary>SCSS and Less coloured by their own grammars, as CSS is, with their variables, mixins and nesting, and
    /// commented with <c>//</c>; an Avalonia file of the old extension (.paml) as the XAML it is. A Less variable ends
    /// with its value.</summary>
    [AvaloniaFact]
    public void A_stylesheet_in_SCSS_or_Less_and_an_Avalonia_paml_file_show_their_tokens_as_their_kinds()
    {
        MainWindow window = Window();
        CodeEditor editor = Open(window, "theme.scss", Sass);
        Assert.Equal(TokenKind.Comment, KindOf(editor, "// The theme's colours."));
        Assert.Equal(TokenKind.Comment, KindOf(editor, "/* A frame round a card. */"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "@use"));
        Assert.Equal(TokenKind.String, KindOf(editor, "\"sass:math\""));
        Assert.Equal(TokenKind.Field, KindOf(editor, "$accent"));
        Assert.Equal(TokenKind.Number, KindOf(editor, "#3b82f6"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "!default"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "@mixin"));
        Assert.Equal(TokenKind.Method, KindOf(editor, "framed"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "border"));
        Assert.Equal(TokenKind.Tag, KindOf(editor, ".card"));
        Assert.Equal(TokenKind.Tag, KindOf(editor, "h2"));
        Assert.Equal(TokenKind.Number, KindOf(editor, "24px"));
        Assert.Equal(TokenKind.Tag, KindOf(editor, "&:hover"));
        Assert.Equal(TokenKind.Method, KindOf(editor, "darken"));
        Assert.Equal(TokenKind.Field, KindOf(editor, "$side"));
        LineComment(window, editor, "    padding: math.div(24px, 2);\n", "    // padding: math.div(24px, 2);\n");

        editor = Open(window, "theme.less", Less);
        Assert.Equal(TokenKind.Comment, KindOf(editor, "// The theme's colours."));
        Assert.Equal(TokenKind.Field, KindOf(editor, "@accent"));
        Assert.Equal(TokenKind.Number, KindOf(editor, "#3b82f6"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "@import"));
        Assert.Equal(TokenKind.String, KindOf(editor, "\"base.less\""));
        Assert.Equal(TokenKind.Comment, KindOf(editor, "/* A frame round a card. */"));
        Assert.Equal(TokenKind.Tag, KindOf(editor, ".framed"));
        Assert.Equal(TokenKind.Field, KindOf(editor, "@width"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "border"));
        Assert.Equal(TokenKind.Tag, KindOf(editor, ".card"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "padding"));
        Assert.Equal(TokenKind.Number, KindOf(editor, "12px"));
        Assert.Equal(TokenKind.Tag, KindOf(editor, "&:hover"));
        Assert.Equal(TokenKind.Method, KindOf(editor, "darken"));
        LineComment(window, editor, "    padding: 12px;\n", "    // padding: 12px;\n");

        editor = Open(window, "Main.paml", Paml);
        Assert.Equal(TokenKind.Comment, KindOf(editor, "<!-- The main window. -->"));
        Assert.Equal(TokenKind.Tag, KindOf(editor, "<Window"));
        Assert.Equal(TokenKind.AttributeName, KindOf(editor, "xmlns"));
        Assert.Equal(TokenKind.AttributeValue, KindOf(editor, "\"Hello\""));
        Assert.Equal(TokenKind.Entity, KindOf(editor, "&amp;"));
        Assert.Equal(TokenKind.Tag, KindOf(editor, "</Window>"));
        LineComment(window, editor, "  <TextBlock Text=\"Hello\" />\n", "  <!--<TextBlock Text=\"Hello\" />-->\n");
        Ui.PlaceAfter(editor, "areas\">");
        Ui.Press(window, "ENTER");
        Assert.Contains("areas\">\n  \n  <TextBlock", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();

        // Comment with Line Comment on a line writes the language's comment there, and takes it off again.
        static void LineComment(MainWindow window, CodeEditor editor, string line, string commented)
        {
            string text = editor.Document.Snapshot.ToString();
            editor.Focus();
            editor.Selections = SelectionSet.Single(text.IndexOf(line, StringComparison.Ordinal) + 6);
            Ui.Run(window, EditorActions.ToggleLineComment);
            Assert.Contains(commented, editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
            Assert.Equal(TokenKind.Comment, KindOf(editor, commented.Trim()));
            Ui.Run(window, EditorActions.ToggleLineComment);
            Assert.Equal(text, editor.Document.Snapshot.ToString());
        }
    }

    [AvaloniaFact]
    public void Each_kind_is_drawn_in_its_own_colour_in_the_dark_theme_and_the_light()
    {
        MainWindow window = Window();
        (string File, string Text, string Token, TokenKind Kind)[] shown =
        [
            ("Shapes.csproj", Project, "PropertyGroup", TokenKind.Tag),
            ("Shapes.csproj", Project, "Include", TokenKind.AttributeName),
            ("Shapes.csproj", Project, "PreserveNewest</", TokenKind.Text),
            ("appsettings.json", Settings, "\"AllowedHosts\"", TokenKind.JsonKey),
            ("ci.yml", Workflow, "branches", TokenKind.YamlKey),
            ("README.md", Readme, "Getting started", TokenKind.Heading),
            ("README.md", Readme, "`dotnet build`", TokenKind.Code),
        ];
        foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            window.RequestedThemeVariant = theme;
            foreach ((string file, string text, string token, TokenKind kind) in shown)
            {
                CodeEditor editor = Open(window, file, text);
                string named = kind == TokenKind.Text ? TokenKeys.Editor_Text : $"Passenger.Syntax.{kind}";
                Color expected = Token(theme, named);
                Color ink = Ink(window, editor, text.IndexOf(token, StringComparison.Ordinal), token.TrimEnd('<', '/').Length);
                Color plain = Token(theme, TokenKeys.Editor_Text);
                Assert.True(kind == TokenKind.Text || Distance(ink, expected) < Distance(ink, plain),
                    $"In the {theme} theme {token} in {file} is drawn {ink}, nearer the text's {plain} than its {kind}'s {expected}");
                Assert.True(Distance(ink, expected) < 90, $"In the {theme} theme {token} in {file} is drawn {ink}, not its {kind}'s {expected}");
            }
        }
        window.Close();
    }

    /// <summary>Settings' Colour scheme: Rider's colours for the code under either theme, at once, the editor keeping the
    /// theme's ground; the theme's own scheme back again.</summary>
    [AvaloniaFact]
    public void Riders_colour_scheme_draws_the_code_in_riders_colours_on_the_themes_ground_under_either_theme()
    {
        MainWindow window = Window();
        try
        {
            CodeEditor editor = Open(window, "Shapes.csproj", Project);
            int tag = Project.IndexOf("PropertyGroup", StringComparison.Ordinal);
            ThemeVariant theme = window.ActualThemeVariant;
            foreach (Look look in (Look[])[Look.Passenger, Look.Harbour])
            {
                window.ApplySettings(window.Settings with { Look = look, ColourScheme = ColourScheme.Theme });
                Color own = Token(theme, TokenKeys.Syntax_Tag);
                Color ground = Token(theme, TokenKeys.Editor_Background);
                Assert.True(Distance(Ink(window, editor, tag, "PropertyGroup".Length), own) < 90, $"Under {look}'s own scheme a tag is not drawn in its {own}");

                window.ApplySettings(window.Settings with { ColourScheme = ColourScheme.Rider });
                Color rider = Token(theme, TokenKeys.Syntax_Tag);
                Color ink = Ink(window, editor, tag, "PropertyGroup".Length);
                Assert.True(Distance(ink, rider) < Distance(ink, own) && Distance(ink, rider) < 90,
                    $"Under {look} with Rider's colour scheme a tag is drawn {ink}, not Rider's {rider} (the theme's own is {own})");
                Assert.True(Token(theme, TokenKeys.Editor_Background) == ground && Ui.Pixel(window, editor.TranslatePoint(new Point(editor.Bounds.Width - 40, editor.Bounds.Height - 20), window)!.Value) == ground,
                    $"Under {look} with Rider's colour scheme the editor is not on the theme's ground {ground}");
            }
        }
        finally
        {
            // The theme and scheme in force are the process's; the next test starts on Passenger's own.
            window.ApplySettings(new Passenger.App.Settings.AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Typing_keeps_colours_right_after_an_XML_comment_and_a_Markdown_code_fence_open_and_close()
    {
        MainWindow window = Window();
        CodeEditor editor = Open(window, "Shapes.csproj", Project);
        editor.Focus();

        int name = Project.IndexOf("OutputType", StringComparison.Ordinal) + "Output".Length;
        editor.Selections = SelectionSet.Single(name);
        window.KeyTextInput("X");
        Assert.Equal(TokenKind.Tag, KindOf(editor, "OutputXType", settle: false));
        Ui.Settle();
        Assert.Equal(TokenKind.Tag, KindOf(editor, "<OutputXType>"));

        int group = editor.Document.Snapshot.ToString().IndexOf("  <PropertyGroup>", StringComparison.Ordinal);
        editor.Selections = SelectionSet.Single(group);
        window.KeyTextInput("<!--");
        Ui.Settle();
        Assert.Equal(TokenKind.Comment, KindOf(editor, "<None Update"));
        Assert.Equal(TokenKind.Comment, KindOf(editor, "</Project>"));
        int end = editor.Document.Snapshot.ToString().IndexOf("</PropertyGroup>", StringComparison.Ordinal) + "</PropertyGroup>".Length;
        editor.Selections = SelectionSet.Single(end);
        window.KeyTextInput("-->");
        Ui.Settle();
        Assert.Equal(TokenKind.Comment, KindOf(editor, "<Description>"));
        Assert.Equal(TokenKind.Tag, KindOf(editor, "<None Update="[..5]));
        Assert.Equal(TokenKind.AttributeValue, KindOf(editor, "\"Serilog\""));
        Assert.Equal(TokenKind.Tag, KindOf(editor, "</Project>"));
        Assert.Equal(Colours(editor), Colours(Open(window, "Fresh.csproj", editor.Document.Snapshot.ToString())));

        editor = Open(window, "README.md", Readme);
        editor.Focus();
        int heading = Readme.IndexOf("## Getting started", StringComparison.Ordinal);
        editor.Selections = SelectionSet.Single(heading);
        window.KeyTextInput("```");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Equal(TokenKind.Code, KindOf(editor, "## Getting started"));
        Assert.Equal(TokenKind.Code, KindOf(editor, "`dotnet build`"));
        int afterHeading = editor.Document.Snapshot.ToString().IndexOf("## Getting started", StringComparison.Ordinal) + "## Getting started".Length;
        editor.Selections = SelectionSet.Single(afterHeading);
        Ui.Press(window, "ENTER");
        window.KeyTextInput("```");
        Ui.Settle();
        Assert.Equal(TokenKind.Code, KindOf(editor, "## Getting started"));
        Assert.Equal(TokenKind.Link, KindOf(editor, ".NET SDK"));
        Assert.Equal(TokenKind.Quote, KindOf(editor, "Areas are in square units."));
        Assert.Equal(Colours(editor), Colours(Open(window, "Fresh.md", editor.Document.Snapshot.ToString())));
        window.Close();
    }

    [AvaloniaFact]
    public void Comment_with_line_comment_writes_each_languages_own_comment_and_block_comment_wraps_XML_in_its_own()
    {
        MainWindow window = Window();
        CodeEditor editor = Open(window, "ci.yml", Workflow);
        editor.Focus();
        int build = Workflow.IndexOf("  build:", StringComparison.Ordinal);
        editor.Selections = SelectionSet.Single(new Selection(build, Workflow.IndexOf("runs-on: ubuntu-latest", StringComparison.Ordinal) + 3));
        Ui.Run(window, EditorActions.ToggleLineComment);
        Assert.Contains("  # build:\n  #   runs-on: ubuntu-latest\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal(TokenKind.Comment, KindOf(editor, "#   runs-on: ubuntu-latest"));
        Ui.Run(window, EditorActions.ToggleLineComment);
        Assert.Equal(Workflow, editor.Document.Snapshot.ToString());

        editor = Open(window, "appsettings.json", Settings);
        editor.Focus();
        editor.Selections = SelectionSet.Single(Settings.IndexOf("\"Retries\"", StringComparison.Ordinal));
        Ui.Run(window, EditorActions.ToggleLineComment);
        Assert.Contains("  // \"Retries\": 3,\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        // XML has no line comment: each line is wrapped in its block comment, and the toggle takes it off again.
        editor = Open(window, "Shapes.csproj", Project);
        editor.Focus();
        int reference = Project.IndexOf("<PackageReference", StringComparison.Ordinal);
        editor.Selections = SelectionSet.Single(reference + 3);
        Ui.Run(window, EditorActions.ToggleLineComment);
        Assert.Contains("    <!--<PackageReference Include=\"Serilog\" Version=\"4.0.0\" />-->\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal(TokenKind.Comment, KindOf(editor, "<!--<PackageReference Include=\"Serilog\" Version=\"4.0.0\" />-->"));
        Ui.Run(window, EditorActions.ToggleLineComment);
        Assert.Equal(Project, editor.Document.Snapshot.ToString());

        int output = Project.IndexOf("<OutputType>Exe</OutputType>", StringComparison.Ordinal);
        editor.Selections = SelectionSet.Single(new Selection(output, output + "<OutputType>Exe</OutputType>".Length));
        Ui.Run(window, EditorActions.ToggleBlockComment);
        Assert.Contains("    <!--<OutputType>Exe</OutputType>-->\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal("<!--<OutputType>Exe</OutputType>-->", editor.Document.Snapshot.GetText(editor.Selections.Primary.Range));
        Ui.Settle();
        Assert.Equal(TokenKind.Comment, KindOf(editor, "<!--<OutputType>Exe</OutputType>-->"));
        Ui.Run(window, EditorActions.ToggleBlockComment);
        Assert.Equal(Project, editor.Document.Snapshot.ToString());

        int text = Project.IndexOf("Shapes &amp;", StringComparison.Ordinal);
        editor.Selections = SelectionSet.Single(text);
        Ui.Run(window, EditorActions.ToggleBlockComment);
        Assert.Contains("<Description><!---->Shapes", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal(text + "<!--".Length, editor.Selections.Primary.Caret);
        window.KeyTextInput("note");
        Ui.Run(window, EditorActions.ToggleBlockComment);
        Assert.Contains("<Description>noteShapes", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_tag_name_at_the_caret_lights_up_with_its_pairs_and_brackets_pair_only_outside_strings()
    {
        MainWindow window = Window();
        CodeEditor editor = Open(window, "Shapes.csproj", Project);
        editor.Focus();
        int start = Project.IndexOf("<ItemGroup>", StringComparison.Ordinal) + 1;
        int end = Project.IndexOf("</ItemGroup>", StringComparison.Ordinal) + 2;

        Assert.Equal((start, end), PairAt(editor, start + 4));
        Assert.Equal((start, end), PairAt(editor, start));
        Assert.Equal((end, start), PairAt(editor, end + "ItemGroup".Length));
        Assert.Equal("ItemGroup", editor.Document.Snapshot.GetText(editor.MatchedBraces!.Value.Matching));
        Color lit = Token(window.ActualThemeVariant, TokenKeys.Editor_MatchedBrace);
        Assert.Equal(lit, Ui.Pixel(window, InBox(window, editor, start + 1)));
        Assert.Equal(lit, Ui.Pixel(window, InBox(window, editor, end + 1)));

        int none = Project.IndexOf("<None", StringComparison.Ordinal) + 1;
        Assert.Equal((none, Project.IndexOf("</None>", StringComparison.Ordinal) + 2), PairAt(editor, none + 2));
        Assert.Null(PairAt(editor, Project.IndexOf("PackageReference", StringComparison.Ordinal) + 3));
        Assert.Null(PairAt(editor, Project.IndexOf("build:", StringComparison.Ordinal) + 2));
        Assert.Null(PairAt(editor, Project.IndexOf("Serilog", StringComparison.Ordinal) + 2));

        editor.Selections = SelectionSet.Single(start + 4);
        Ui.Run(window, EditorActions.MatchBrace);
        Assert.Equal(end, editor.Selections.Primary.Caret);
        Ui.Run(window, EditorActions.MatchBrace);
        Assert.Equal(start, editor.Selections.Primary.Caret);

        // JSON's brackets pair across lines; one inside a string pairs with nothing.
        const string json = "{\n  \"a\": [1, 2],\n  \"b\": \"{ [ not code\",\n  \"c\": { \"d\": null }\n}\n";
        editor = Open(window, "data.json", json);
        Assert.Equal((0, json.LastIndexOf('}')), PairAt(editor, 0));
        Assert.Equal((json.IndexOf('['), json.IndexOf(']')), PairAt(editor, json.IndexOf('[')));
        Assert.Null(PairAt(editor, json.IndexOf("{ [", StringComparison.Ordinal) + 1));
        Assert.Null(PairAt(editor, json.IndexOf("{ [", StringComparison.Ordinal) + 3));
        window.Close();
    }

    /// <summary>The caret moved onto a tag's name finds its pair by reading the tags between them where they stand,
    /// near the caret only: onto the root element's start tag or its end tag in 1,900 lines of XML, a move and the pair
    /// it lights up allocate on the UI thread what they do in a hundred lines. In 20,000 lines the root's pair is too
    /// far to light up; Move Caret to Matching Brace, asked once, still goes there and back.</summary>
    [AvaloniaFact]
    public void Moving_the_caret_onto_a_tags_name_reads_only_the_tags_near_it_and_Matching_Brace_goes_as_far_as_its_pair()
    {
        MainWindow window = Window();
        (long longStart, long longEnd) = TagMoves(window, "Long.xml", Groups(95));
        (long shortStart, long shortEnd) = TagMoves(window, "Short.xml", Groups(5));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A move onto the root's start tag and its pair allocated {longStart:N0} bytes in 1,900 lines and {shortStart:N0} in 100; onto its end tag {longEnd:N0} and {shortEnd:N0}.");
        Assert.True(longStart <= shortStart + (16 * 1024),
            $"A move onto the root element's start tag in 1,900 lines allocated {longStart / 1024.0:F1} KB on the UI thread, against {shortStart / 1024.0:F1} KB in 100: it allocated for the tags between it and its end tag");
        Assert.True(longEnd <= shortEnd + (16 * 1024),
            $"A move onto the root element's end tag in 1,900 lines allocated {longEnd / 1024.0:F1} KB on the UI thread, against {shortEnd / 1024.0:F1} KB in 100: it allocated for the tags between it and its start tag");

        string longest = Groups(1000);
        CodeEditor editor = Open(window, "Longest.xml", longest);
        int start = longest.IndexOf("<Project", StringComparison.Ordinal) + 1;
        int end = longest.LastIndexOf("</Project>", StringComparison.Ordinal) + 2;
        Assert.Null(PairAt(editor, start + 3));
        int group = longest.IndexOf("<ItemGroup", StringComparison.Ordinal) + 1;
        Assert.Equal((group, longest.IndexOf("</ItemGroup>", StringComparison.Ordinal) + 2), PairAt(editor, group + 3));
        editor.Selections = SelectionSet.Single(start + 3);
        editor.Focus();
        Ui.Run(window, EditorActions.MatchBrace);
        Assert.Equal(end, editor.Selections.Primary.Caret);
        Ui.Run(window, EditorActions.MatchBrace);
        Assert.Equal(start, editor.Selections.Primary.Caret);
        window.Close();
    }

    /// <summary>The caret moved onto a bracket finds its pair among the lines near it: onto the brace that opens a JSON
    /// file of 30,000 items, or after the one that closes it, a move reads no more than the 2,000 lines beside it and
    /// allocates on the UI thread what a move onto one in 100 items does. That pair is too far to light up; an item's
    /// braces still light up together, and Move Caret to Matching Brace, asked once, still goes from the first brace to
    /// the last and back.</summary>
    [AvaloniaFact]
    public void Moving_the_caret_onto_a_bracket_reads_only_the_lines_near_it_and_Matching_Brace_goes_as_far_as_its_pair()
    {
        MainWindow window = Window();
        string large = Items(30_000);
        (long longBytes, long longLines) = BracketMoves(window, "Long.json", large, paired: false);
        (long shortBytes, long shortLines) = BracketMoves(window, "Short.json", Items(100), paired: true);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A move onto the outer brace and back read {longLines:N0} lines and allocated {longBytes:N0} bytes in 30,000 items, {shortLines:N0} lines and {shortBytes:N0} bytes in 100.");
        Assert.True(longLines <= 2 * 2_000,
            $"A move onto the brace around 30,000 items and after its pair read {longLines:N0} lines on the UI thread looking for the pairs, more than the 2,000 near each");
        Assert.True(longBytes <= shortBytes + (16 * 1024),
            $"A move onto the brace around 30,000 items allocated {longBytes / 1024.0:F1} KB on the UI thread, against {shortBytes / 1024.0:F1} KB in 100");

        CodeEditor editor = Open(window, "Long.json", large);
        int item = large.IndexOf("{ \"id\": 15000,", StringComparison.Ordinal);
        Assert.Equal((item, large.IndexOf('}', item)), PairAt(editor, item));
        int end = large.LastIndexOf('}');
        editor.Selections = SelectionSet.Single(0);
        editor.Focus();
        Ui.Run(window, EditorActions.MatchBrace);
        Assert.Equal(end, editor.Selections.Primary.Caret);
        Ui.Run(window, EditorActions.MatchBrace);
        Assert.Equal(0, editor.Selections.Primary.Caret);
        window.Close();
    }
}

/// <summary>A key in a long file of a language TextMate colours costs the UI thread what one in a short file does. Held to a
/// time, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class FileColoursResponsivenessTests : FileColoursFolder
{
    /// <summary>A key in a file of 20,000 lines of XML, or 5,000 of Markdown, has the grammar read the line it changed,
    /// off the UI thread, and the UI thread do what a key in a short file of the language costs it; a comment opened at the
    /// top is read to the end of the file and lands whole.</summary>
    [AvaloniaFact]
    public void A_key_in_twenty_thousand_lines_of_XML_or_five_thousand_of_Markdown_reads_the_line_it_changed()
    {
        string large = Groups(1000);
        Assert.True(large.Count(c => c == '\n') > 20_000);
        StringBuilder md = new("# Notes\n\n");
        for (int i = 0; md.ToString().Count(c => c == '\n') < 5000; i++)
        {
            md.Append(System.Globalization.CultureInfo.InvariantCulture, $"## Section {i}\n\nSome **bold** and *italic* text with `code` and a [link](https://example.com/{i}).\n\n");
            md.Append("- one item\n- another item\n\n```json\n{ \"a\": 1 }\n```\n\n> quoted\n\n");
        }
        string notes = md.ToString();
        MainWindow window = Window();

        foreach ((string file, string text, string at, string small, string smallAt) in new[]
        {
            ("Large.props", large, "File500_3.cs", "<Project>\n  <ItemGroup>\n    <Compile Include=\"File0.cs\" />\n  </ItemGroup>\n</Project>\n", "File0.cs"),
            ("Notes.md", notes, "Section 250", "# Notes\n\n## Section 1\n\nSome **bold** text.\n", "Section 1"),
        })
        {
            (double keyLong, double readLong, double lines, TextMateHighlighter colours) = Keys(window, file, text, at);
            (double keyShort, _, _, _) = Keys(window, "Small" + Path.GetExtension(file), small, smallAt);
            Assert.True(lines <= 2, $"A key in {file} had the grammar read {lines:F1} lines");
            Assert.True(keyLong <= Math.Max(4 * keyShort, keyShort + 2), $"A key in {file} took the UI thread {keyLong:F2} ms, against {keyShort:F2} ms in a short file");
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{file}: {text.Count(c => c == '\n'):N0} lines; a key {keyLong:F2} ms on the UI thread ({keyShort:F2} ms in a short file), "
                + $"{readLong:F2} ms off it reading {lines:F1} lines; opening it read {colours.Worked.Lines:N0} lines");
        }

        CodeEditor editor = window.EditorArea.CodeEditor;
        Open(window, "Large.props", large);
        TextMateHighlighter highlighter = (TextMateHighlighter)window.EditorArea.Active!.Highlighter!;
        editor.Selections = SelectionSet.Single(large.IndexOf('\n') + 1);
        editor.Focus();
        (long before, _, TimeSpan time) = highlighter.Worked;
        window.KeyTextInput("<!--");
        Ui.Settle();
        Assert.Equal(TokenKind.Comment, KindOf(editor, "</Project>"));
        (long after, _, TimeSpan took) = highlighter.Worked;
        TestContext.Current.TestOutputHelper?.WriteLine($"Opening a comment at the top: {after - before:N0} lines read in {(took - time).TotalMilliseconds:F0} ms");
        window.Close();
    }

    /// <summary>A key typed at the end of 20,000 <c>//</c> lines costs the UI thread about what one at the end of as
    /// many lines inside one <c>/* */</c> does, and the comments are still coloured as comments, on screen and far from
    /// it.</summary>
    [AvaloniaFact]
    public void A_key_at_the_end_of_twenty_thousand_line_comments_costs_what_it_does_in_one_block_comment()
    {
        string[] lines = [.. Enumerable.Range(0, 20_000).Select(i => $"The notes, line {i}")];
        string head = "namespace Notes;\n\npublic static class Kept\n{\n    public const int Count = 1;\n}\n\n";
        string slashed = head + string.Concat(lines.Select(l => $"// {l}\n"));
        string blocked = head + $"/*\n{string.Concat(lines.Select(l => $"{l}\n"))}*/\n";
        MainWindow window = Window();
        long block = KeyCost(window, "Block.cs", blocked);
        long slashes = KeyCost(window, "Slashes.cs", slashed);
        TestContext.Current.TestOutputHelper?.WriteLine($"A key at the end of 20,000 // lines allocated {slashes / 1024} KB on the UI thread, inside one /* */ {block / 1024} KB");
        Assert.True(slashes <= block + (256 * 1024),
            $"A key at the end of 20,000 // lines allocated {slashes / 1024} KB on the UI thread, against {block / 1024} KB inside one /* */: every comment of the run was classified");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal(TokenKind.Comment, KindOf(editor, "// The notes, line 19998"));
        Assert.Equal(TokenKind.Comment, KindOf(editor, "// The notes, line 100\n"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "const"));
        window.Close();

        // The least a key typed at the end of the file's last comment line allocated on the UI thread, with its frame.
        long KeyCost(MainWindow window, string file, string text)
        {
            CodeEditor editor = Open(window, file, text);
            editor.Selections = SelectionSet.Single(text.IndexOf("line 19999", StringComparison.Ordinal) + "line 19999".Length);
            editor.Focus();
            Ui.Settle();
            long least = long.MaxValue;
            for (int i = 0; i < 5; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                window.KeyTextInput("x");
                Dispatcher(window);
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
                Ui.Settle();
            }
            Assert.Equal(TokenKind.Comment, KindOf(editor, "line 19999xxxxx"));
            return least;
        }
    }

    /// <summary>The parse a key starts classifies the lines on screen on its worker, and its landing hands them to the
    /// screen: the frame after it classifies nothing again on the UI thread. A key classifies again at most the line it
    /// changed, counted in the characters classified there, not a time.</summary>
    [AvaloniaFact]
    public void A_parse_landing_after_a_key_hands_the_screen_the_colours_its_worker_read()
    {
        StringBuilder code = new("namespace Shapes;\n\npublic static class Areas\n{\n");
        for (int i = 0; i < 400; i++)
        {
            code.Append(System.Globalization.CultureInfo.InvariantCulture, $"    public static double Square{i}(double side) => side * side + {i} /* the {i}th square's area */;\n");
        }
        string text = code.Append("}\n").ToString();
        MainWindow window = Window();
        CodeEditor editor = Open(window, "Areas.cs", text);
        Passenger.Languages.CSharp.CSharpHighlighter syntax = window.EditorArea.Active!.Syntax!;
        int line = text.IndexOf("Square200(", StringComparison.Ordinal);
        editor.Selections = SelectionSet.Single(text.IndexOf("side + 200", line, StringComparison.Ordinal) + "side + 200".Length);
        editor.Focus();
        Ui.Settle();
        int length = text.IndexOf('\n', line) - text.LastIndexOf('\n', line);
        for (int i = 0; i < 3; i++)
        {
            long before = syntax.CharactersClassified;
            window.KeyTextInput("1");
            Ui.Settle();
            long classified = syntax.CharactersClassified - before;
            TestContext.Current.TestOutputHelper?.WriteLine($"Key {i + 1} and its parse's landing classified {classified} characters on the UI thread, its line {length}");
            Assert.True(classified <= 2 * length,
                $"A key and its parse's landing classified {classified} characters on the UI thread, more than twice its {length}-character line: the screen was classified again as the parse landed");
        }
        Assert.Equal(TokenKind.Number, KindOf(editor, "200111"));
        Assert.Equal(TokenKind.Comment, KindOf(editor, "/* the 201th square's area */"));
        window.Close();
    }
}

/// <summary>The folder a colours test writes its files to, and the helpers that open them in a window and read their colours.</summary>
public abstract class FileColoursFolder : IDisposable
{
    protected readonly string folder = TestFolders.Create("colours-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>What moving the caret onto the brace a JSON file opens with, and to just after the one it closes with, cost
    /// on the UI thread, each from a word beside it: the bytes allocated (the least of a few moves) and the lines the pair
    /// searches read (the most), each move lighting up the pair or, too far apart, none.</summary>
    protected (long Bytes, long Lines) BracketMoves(MainWindow window, string file, string text, bool paired)
    {
        CodeEditor editor = Open(window, file, text);
        TextMateHighlighter colours = (TextMateHighlighter)window.EditorArea.Active!.Highlighter!;
        int end = text.LastIndexOf('}');
        long least = long.MaxValue;
        long most = 0;
        for (int i = 0; i < 5; i++)
        {
            editor.Selections = SelectionSet.Single(text.IndexOf("items", StringComparison.Ordinal));
            Ui.Settle();
            long lines = colours.LinesPaired;
            long before = GC.GetAllocatedBytesForCurrentThread();
            editor.Selections = SelectionSet.Single(0);
            BracePair? first = editor.MatchedBraces;
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            Ui.Settle();
            editor.Selections = SelectionSet.Single(end + 1);
            BracePair? last = editor.MatchedBraces;
            most = Math.Max(most, colours.LinesPaired - lines);
            Assert.True(first?.Matching.Start == (paired ? end : null), $"The brace at the start of {file} paired with {first?.Matching.Start}");
            Assert.True(last?.Matching.Start == (paired ? 0 : null), $"The brace at the end of {file} paired with {last?.Matching.Start}");
            Ui.Settle();
        }
        return (least, most);
    }

    /// <summary>A JSON file of items, one to a line, in an array under the brace the file opens with.</summary>
    protected static string Items(int count)
    {
        StringBuilder json = new("{\n  \"items\": [\n");
        for (int i = 0; i < count; i++)
        {
            json.Append(System.Globalization.CultureInfo.InvariantCulture, $"    {{ \"id\": {i}, \"name\": \"Item {i}\" }}{(i < count - 1 ? "," : "")}\n");
        }
        return json.Append("  ]\n}\n").ToString();
    }

    /// <summary>What moving the caret onto the root element's start tag's name, and onto its end tag's, and the pair it
    /// lights up allocate on the UI thread, each from a blank line beside it: the least of a few moves, each finding its pair.</summary>
    protected (long Start, long End) TagMoves(MainWindow window, string file, string text)
    {
        CodeEditor editor = Open(window, file, text);
        int start = text.IndexOf("<Project", StringComparison.Ordinal) + 1;
        int end = text.LastIndexOf("</Project>", StringComparison.Ordinal) + 2;
        long Moves(int onName, int away, int pair)
        {
            long least = long.MaxValue;
            for (int i = 0; i < 5; i++)
            {
                editor.Selections = SelectionSet.Single(away);
                Ui.Settle();
                long before = GC.GetAllocatedBytesForCurrentThread();
                editor.Selections = SelectionSet.Single(onName);
                BracePair? found = editor.MatchedBraces;
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
                Assert.True(found?.Matching.Start == pair, $"The tag's name at {onName} in {file} paired with {found?.Matching.Start}, not {pair}");
                Ui.Settle();
            }
            return least;
        }
        long first = Moves(start + 3, text.IndexOf('\n') + 1, end);
        long last = Moves(end + 3, end - 3, start);
        return (first, last);
    }

    /// <summary>A project file of item groups, twenty lines each, each under a comment, with items that close themselves, an
    /// item with an element inside, and attributes with a bracket in their values.</summary>
    protected static string Groups(int count)
    {
        StringBuilder xml = new("<Project Sdk=\"Microsoft.NET.Sdk\">\n");
        for (int i = 0; i < count; i++)
        {
            xml.Append(System.Globalization.CultureInfo.InvariantCulture, $"  <!-- Group {i}:\n       its items. -->\n  <ItemGroup Condition=\"'$(Group)' == '{i}' &gt; 0\">\n");
            for (int j = 0; j < 10; j++)
            {
                xml.Append(System.Globalization.CultureInfo.InvariantCulture, $"    <Compile Include=\"File{i}_{j}.cs\" Label=\"a > b\" />\n");
            }
            xml.Append(System.Globalization.CultureInfo.InvariantCulture, $"    <PackageReference Include=\"Package{i}\"\n                      Version=\"1.0.{i}\" />\n");
            xml.Append("    <None Update=\"x.json\">\n      <CopyToOutputDirectory>Always</CopyToOutputDirectory>\n    </None>\n  </ItemGroup>\n\n");
        }
        return xml.Append("</Project>\n").ToString();
    }

    /// <summary>A file opened in the window and typed in at a place: the median time a key takes the UI thread, the time
    /// the grammar took for it off the UI thread, and the lines it read for it.</summary>
    protected (double Key, double Read, double Lines, TextMateHighlighter Colours) Keys(MainWindow window, string file, string text, string at)
    {
        CodeEditor editor = Open(window, file, text);
        TextMateHighlighter colours = (TextMateHighlighter)window.EditorArea.Active!.Highlighter!;
        editor.Selections = SelectionSet.Single(text.IndexOf(at, StringComparison.Ordinal) + 2);
        editor.Focus();
        Ui.Settle();
        (long lines, int passes, TimeSpan time) = colours.Worked;
        List<double> keys = [];
        const int Count = 12;
        for (int i = 0; i < Count; i++)
        {
            long began = Stopwatch.GetTimestamp();
            window.KeyTextInput(i % 2 == 0 ? "x" : "y");
            Dispatcher(window);
            keys.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
            Ui.Settle();
        }
        (long linesAfter, int passesAfter, TimeSpan timeAfter) = colours.Worked;
        Assert.True(passesAfter - passes >= Count, $"{Count} keys in {file} made {passesAfter - passes} passes");
        keys.Sort();
        return (keys[Count / 2], (timeAfter - time).TotalMilliseconds / Count, (linesAfter - lines) / (double)Count, colours);
    }

    /// <summary>What the key left for the UI thread to do (its frame, drawn with the colours carried), done.</summary>
    protected static void Dispatcher(MainWindow window)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        _ = window;
    }

    protected MainWindow Window()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        return window;
    }

    /// <summary>A file written to the folder and opened, with no folder opened, so no language server starts; once its
    /// colours have landed.</summary>
    protected CodeEditor Open(MainWindow window, string name, string text)
    {
        string path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || File.ReadAllText(path) != text)
        {
            File.WriteAllText(path, text);
        }
        Assert.Null(window.OpenFile(path));
        Ui.Settle();
        Assert.Null((window.EditorArea.Active!.Highlighter as TextMateHighlighter)?.Failure);
        return window.EditorArea.CodeEditor;
    }

    /// <summary>The kind every character of a stretch of the editor's text is coloured as (its first occurrence), or
    /// null when they are not all one.</summary>
    protected static TokenKind? KindOf(CodeEditor editor, string stretch, bool settle = true)
    {
        if (settle)
        {
            Ui.Settle();
        }
        TextSnapshot text = editor.Document.Snapshot;
        int start = text.ToString().IndexOf(stretch, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{stretch}' is not in the text");
        IReadOnlyList<SyntaxSpan> spans = editor.Highlighter!.GetSpans(text, start, stretch.Length);
        TokenKind?[] kinds = new TokenKind?[stretch.Length];
        Array.Fill(kinds, TokenKind.Text);
        foreach (SyntaxSpan span in spans)
        {
            for (int i = Math.Max(span.Start, start); i < Math.Min(span.End, start + stretch.Length); i++)
            {
                kinds[i - start] = span.Kind;
            }
        }
        // Spaces take no colour a person could see.
        TokenKind?[] seen = [.. kinds.Where((_, i) => !char.IsWhiteSpace(stretch[i])).Distinct()];
        return seen.Length == 1 ? seen[0] : null;
    }

    /// <summary>Every span of the editor's text, as text, to compare with the same text opened afresh.</summary>
    protected static string Colours(CodeEditor editor)
    {
        Ui.Settle();
        TextSnapshot text = editor.Document.Snapshot;
        return string.Join(", ", editor.Highlighter!.GetSpans(text, 0, text.Length).Select(s => $"{s.Kind} {text.GetText(s.Start, s.Length)}"));
    }

    protected static (int Touched, int Matching)? PairAt(CodeEditor editor, int caret)
    {
        editor.Selections = SelectionSet.Single(caret);
        Ui.Settle();
        return editor.MatchedBraces is { } pair ? (pair.Touched.Start, pair.Matching.Start) : null;
    }

    /// <summary>A point inside the box drawn behind the character at an offset: at its top, above the glyph.</summary>
    protected static Point InBox(MainWindow window, CodeEditor editor, int offset) =>
        editor.TranslatePoint(new Point(editor.PointOf(offset + 1).X - 2, editor.PointOf(offset).Y + 1), window)!.Value;

    /// <summary>The colour of the most ink in a stretch of text as drawn: of every pixel in its boxes, the one farthest
    /// from the editor's background, which is where a glyph's stroke covers the pixel whole.</summary>
    protected static Color Ink(MainWindow window, CodeEditor editor, int start, int length)
    {
        Ui.Settle();
        Color background = Token(window.ActualThemeVariant, TokenKeys.Editor_Background);
        Point from = editor.TranslatePoint(editor.PointOf(start), window)!.Value;
        Point to = editor.TranslatePoint(editor.PointOf(start + length), window)!.Value;
        using Avalonia.Media.Imaging.WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window has drawn no frame.");
        PixelRect box = new PixelRect(PixelPoint.FromPoint(from, window.RenderScaling), PixelPoint.FromPoint(to + new Point(0, editor.LineHeight), window.RenderScaling));
        byte[] pixels = new byte[box.Width * box.Height * 4];
        System.Runtime.InteropServices.GCHandle pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(box, pinned.AddrOfPinnedObject(), pixels.Length, box.Width * 4);
        }
        finally
        {
            pinned.Free();
        }
        bool rgba = frame.Format == Avalonia.Platform.PixelFormat.Rgba8888;
        Color inkiest = background;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            Color pixel = rgba ? Color.FromRgb(pixels[i], pixels[i + 1], pixels[i + 2]) : Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
            if (Distance(pixel, background) > Distance(inkiest, background))
            {
                inkiest = pixel;
            }
        }
        return inkiest;
    }

    protected static double Distance(Color a, Color b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    protected static Color Token(ThemeVariant theme, string key) =>
        Application.Current!.TryFindResource(key, theme, out object? brush) && brush is ISolidColorBrush solid
            ? solid.Color
            : throw new InvalidOperationException($"The {theme} theme has no {key}");
}
