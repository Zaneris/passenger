# Passenger themes, format 1

A theme sets the colours and shapes of Passenger's window and code; a code colour scheme sets only the code's colours,
under any theme. Both are JSON data, never code. Passenger's own theme is the default: every theme and scheme is laid
over it, so a file gives only what it changes.

## A source

A source holds `passenger-themes.json` at its root, which lists its files by paths inside it (folders joined by `/`, no
`..`, no absolute path or address, each ending `.json`). A theme's images go beside its file. A source is one of:

- a folder on your machine, by its full path;
- a GitHub repository, as `owner/name` or its github.com address, read through its raw files at its default branch;
- a web address over https, under which `passenger-themes.json` and the files sit.

Passenger lists its own themes repository, [Zaneris/passenger-themes](https://github.com/Zaneris/passenger-themes), from
the start; add another in Settings | Appearance | Theme sources. Passenger reads a source only when you look at its
themes there, choose one or press Refresh, never as it starts; a source that cannot be read, or a file it lists that
cannot, is said in Settings and changes nothing. Add from File beside the Theme and Colour scheme lists takes a theme or
scheme file of your own.

```json
{
  "format": 1,
  "themes": ["Harbour.json", "Sakura/theme.json"],
  "codeColours": ["Dusk.json"]
}
```

## A theme

```json
{
  "format": 1,
  "name": "Harbour",
  "description": "Warm and quiet: teal on linen by day, a warm charcoal by night.",
  "tokens": { "ControlRadius": 8, "TabMargin": [1, 6, 1, 0] },
  "dark": { "Frame": "#151311", "Accent": "#2CB5A3" },
  "light": { "Frame": "#EEE8DC", "Accent": "#0F766E" }
}
```

`name` is required, at most 64 characters; `description` is optional, at most 1,000. `tokens` holds values for both
variants, `dark` and `light` for one each; a token goes in `tokens` or in `dark` and `light`, never both. Any of the
three may be left out, as may `background`, `fonts`, `languages`, `layout` and `vscode` below.

A token is named as its key in [Tokens.axaml](../src/Passenger.App/Theme/Tokens.axaml) without `Passenger.`:
`Frame`, `Editor.Background`, `Syntax.Keyword`, `SyntaxStyle.Comment`, `ControlRadius`, and the margins
`EditorTextPadding` (between the editor's gutter and its text), `ToolHeaderHeight`, `ToolTitleMargin` and
`DialogFooterPadding`. Tokens.axaml gives every token
and its default value; a token takes the kind of value its default is:

| The default | The value |
|---|---|
| a colour (`SolidColorBrush`) | `"#RRGGBB"`, `"#AARRGGBB"`, or `{"colour": "#RRGGBB", "opacity": 0.2}` (opacity 0 to 1) |
| a gradient (`LinearGradientBrush`) | a colour, or `{"start": [0, 0], "end": [1, 0], "stops": [{"offset": 0, "colour": "#1B3270"}, {"offset": 1, "colour": "#0E4552"}]}`: points as fractions of the area, 2 to 16 stops with offsets rising from 0 to 1 |
| shadows (`BoxShadows`) | `"X Y BLUR SPREAD #AARRGGBB"` (BLUR and SPREAD may be left out, `inset` may lead), up to 8 joined by commas, or `"none"` |
| a radius (`CornerRadius`) | a number for every corner, or `[top left, top right, bottom right, bottom left]`, 0 to 1000 |
| a thickness (`Thickness`) | a number for every side, or `[left, top, right, bottom]`, -1000 to 1000 |
| a number (`x:Double`) | a number, 0 to 1000 |
| a syntax style (`x:String`) | `"Normal"`, `"Bold"`, `"Italic"` or `"Bold Italic"` |

### Its background picture

```json
"background": {
  "image": "dusk.webp",
  "opacity": 0.25,
  "tint": { "colour": "#101830", "strength": 0.2 },
  "places": ["editor", "toolWindows", "frame", "welcome"],
  "fit": "cover",
  "align": "bottom right",
  "light": { "image": "day.jpg", "opacity": 0.2 }
}
```

`image` is a picture beside the theme's file (its folders joined by `/`, no `..`): a PNG, JPEG or WebP of at most 8 MB,
at most 8192 pixels along a side and 16 million pixels in all. Passenger reads its size from its
header before anything decodes it, then decodes it with its own decoders. The rest may be left out:

- `opacity`, how much of the picture shows, 0 to 0.3 (0.15 if left out). The picture is drawn over the area, its text and
  its ground alike, so every glyph keeps at least 70% of its contrast with its ground whatever the picture holds.
- `tint`, a colour washed over the picture at a `strength` from 0 (none) to 1 (the colour alone; 0.5 if left out).
- `places`, where it is drawn: one or more of `editor` (each editor, fitted to it), `toolWindows` (each tool window's
  page), `frame` (the toolbar, the stripes, the editor tabs' strip and the status bar, fitted to the whole window so they
  show one picture) and `welcome` (the Welcome screen). `["editor"]` if left out.
- `fit`: `cover` (fills the area, its overflow cut off; the default), `contain` (whole inside the area), `tile` (at its own
  size, repeated) or `centre` (at its own size, once).
- `align`: `centre` (the default), or `top`, `bottom`, `left`, `right`, or one of each, `bottom right`.
- `dark` and `light` give what differs under one variant, the rest as above, or `"none"` for no picture there.

The picture is kept beside the theme when the theme is chosen, so it draws offline too.

### Its fonts

```json
"fonts": { "code": { "family": "Fira Code", "size": 14 }, "window": { "family": "Inter" } }
```

The code's and the window's family, each by its name, and the code's `size` in points (6 to 48). A family or size you
choose in Settings wins wherever you chose one; Reset Font Size goes back to the theme's size. A family not installed
here leaves Passenger's own.

### The code's colours by language

```json
"languages": {
  "csharp": { "dark": { "Syntax.Keyword": "#FF7AB8" }, "light": { "Syntax.Keyword": "#B0306A" } },
  "json": { "tokens": { "SyntaxStyle.Comment": "Italic" } }
}
```

A language is named by its id as Passenger knows it (`csharp`, `xaml`, `msbuild`, `xml`, `json`, `aspnetcorerazor`, `gdscript`, `typescript`,
`css`, `markdown`, …), and gives the code's tokens (`Editor.*`, `Syntax.*`, `SyntaxStyle.*`, `Diff.*`) in `tokens`,
`dark` and `light` as a theme gives its own. They are laid over the file's own tokens in that language's editors. A
language this Passenger does not know is passed over and named.

### Its default layout

```json
"layout": {
  "places": { "rightTop": ["explorer", "commit"], "bottomLeft": ["build", "run"], "bottomRight": ["problems"] },
  "open": ["explorer", "build", "problems"],
  "sizes": { "left": 260, "right": 320, "bottom": 220 },
  "editorTabs": "left",
  "toolWindows": "tabs",
  "stripeIcons": "untabbed",
  "closedWindows": "leave",
  "findResults": "window"
}
```

Where the tool windows dock when nobody has arranged them: what a window opens on before any layout is kept, and what
Window | Layout Settings | Restore Default Layout goes back to. A layout you arrange yourself always wins, and is kept as
yours whatever theme you choose; choosing another theme moves the windows only while they still stand where the theme
before put them.

- `places` names, for any of `leftTop`, `leftBottom`, `rightTop`, `rightBottom`, `bottomLeft` and `bottomRight`, the tool
  windows docked there in their order, before those the theme leaves where Passenger puts them. A window docks in one
  place. The windows are named by their ids: `explorer`, `structure`, `bookmarks`, `commit`, `run`, `debug`, `tests`,
  `build`, `problems`, `find`, `git`, `nuget`, `terminal`, `localHistory`, `containers`, `profiler`, `hierarchy`,
  `endpoints`, `requests`, `liveTree`, `game`, `aspire` and `migrations`. `editor` names those shown as tabs among the
  editors instead, their icons on the stripes where Passenger puts them.
- `open` lists the windows open at the start, every other closed; left out, Passenger's own open (the Explorer). A place
  shows one window, the first in its order.
- `sizes` gives the left and right areas' widths and the bottom one's height, each 100 to 2000 pixels; one left out is
  Passenger's own.
- `editorTabs` stands each editor's tabs in a row across its `top`, or in a column down its `left` or `right` side; left
  out, across the top.
- `toolWindows` names the docked tool windows by their `icons` on the stripes alone, or by `tabs` too: titled tabs along
  the foot of each place where several dock; left out, by icons.
- `stripeIcons` keeps an icon on the stripes for `all` the windows while titled tabs name them, or only for those no
  titled tab names (`untabbed`), so a window whose place is open with its tab is named once; left out, all.
- `closedWindows` says what closing a tool window by its × does to its icon: `keep` it on the stripe, as hiding it does,
  or `leave` the stripe until the window is opened again from View | Tool Windows, Find Action or its keys; left out,
  keep.
- `findResults` keeps Find in Files' matches in its `popup` alone, or in the Find `window` as well once one is gone to,
  a tab to go back to, where the places Go to Declaration and the like find to choose from are listed too; left out, the
  popup.

What the layout arranges beyond where the windows dock (`editorTabs`, `toolWindows`, `stripeIcons`, `closedWindows`, `findResults`) is each a setting of Settings | Appearance: the
theme's is the default there, and the place you choose wins over it whatever theme you choose, until you choose Theme
default again.

A window or place this Passenger does not have is passed over and named.

## A VS Code colour theme

Settings | Appearance | VS Code themes searches [Open VSX](https://open-vsx.org), the Eclipse Foundation's open registry of
VS Code extensions, for colour themes: the most downloaded first, by rating, the newest or by name on request, or by words
naming a theme, its extension or its publisher. Passenger asks Open VSX only while the explorer is open, or as you choose
or refresh a theme. Each row is a colour theme an extension contributes (icon themes and the rest are left out), with its
extension, publisher, downloads and rating. Choosing one fetches the extension's `.vsix` and reads only its `package.json`
and the theme's files: data held to their limits (a `.vsix` of at most 32 MB and 5,000 files, each theme file 1 MB, eight
files a theme through `include`), nothing of the extension run. It shows at once, before Apply, and is kept so it draws
offline; one that cannot be read is said, changing nothing.

A VS Code theme is one variant, dark or light (its `uiTheme`), and shows in it whatever Dark or light says. Passenger reads
its JSON as VS Code does, comments and trailing commas allowed, a file it `include`s laid first. Its token colours
(`tokenColors`, matched to TextMate scopes as VS Code matches them) and semantic token colours (`semanticTokenColors`,
where it turns `semanticHighlighting` on) colour the code; its workbench colours (`colors`) colour the window where they
correspond; everything else is worked out from what it gives, its grounds kept a step apart. Every word and mark is held
to WCAG AA on what it is drawn on (text 4.5:1, icons, focus rings and the caret 3:1): a colour the theme gives too faint
is moved in lightness, its hue kept, only as far as it must. A theme whose token colours are a TextMate `.tmTheme` file is
refused, saying so.

| The code's kind | Semantic token type | TextMate scope |
|---|---|---|
| `Syntax.Keyword` | `keyword` | `keyword.other`, `storage.modifier`, `keyword` |
| `Syntax.ControlKeyword` | `controlKeyword` | `keyword.control`, else `Syntax.Keyword` |
| `Syntax.Comment` | `comment` | `comment.line.double-slash`, `comment` |
| `Syntax.DocComment` | – | `comment.block.documentation`, `comment.documentation`, else `Syntax.Comment` |
| `Syntax.String` | `string` | `string.quoted.double`, `string` |
| `Syntax.StringEscape` | `stringEscapeCharacter` | `constant.character.escape`, else `Syntax.String` |
| `Syntax.Number` | `number` | `constant.numeric` |
| `Syntax.Preprocessor` | `preprocessorKeyword` | `keyword.preprocessor`, `meta.preprocessor`, else `Syntax.Keyword` |
| `Syntax.Class` | `class` | `entity.name.type.class`, `entity.name.type`, `support.class` |
| `Syntax.Interface` | `interface` | `entity.name.type.interface`, else `Syntax.Class` |
| `Syntax.Struct` | `struct` | `entity.name.type.struct`, else `Syntax.Class` |
| `Syntax.Enum` | `enum` | `entity.name.type.enum`, else `Syntax.Class` |
| `Syntax.Delegate` | `delegate` | `entity.name.type.delegate`, else `Syntax.Class` |
| `Syntax.TypeParameter` | `typeParameter` | `entity.name.type.type-parameter`, `entity.name.type.parameter`, else `Syntax.Class` |
| `Syntax.Method` | `method`, `function` | `entity.name.function`, `support.function` |
| `Syntax.Property` | `property` | `variable.other.property`, `variable.other.object.property`, `support.variable.property` |
| `Syntax.Event` | `event` | `variable.other.event`, else `Syntax.Property` |
| `Syntax.Field` | `field` | `entity.name.variable.field`, `variable.other.object.property`, else `Syntax.Property` |
| `Syntax.Constant` | `constant` | `variable.other.constant`, `constant.other`, else `Syntax.Field` |
| `Syntax.EnumMember` | `enumMember` | `variable.other.enummember`, `constant.other.enum`, else `Syntax.Constant` |
| `Syntax.Tag` | – | `entity.name.tag.xml`, `entity.name.tag.html` |
| `Syntax.AttributeName` | – | `entity.other.attribute-name.localname.xml`, `entity.other.attribute-name.html` |
| `Syntax.AttributeValue` | – | `string.quoted.double.xml`, `string.quoted.double.html`, else `Syntax.String` |
| `Syntax.Entity` | – | `constant.character.entity.xml`, `constant.character.entity`, else `Syntax.StringEscape` |
| `Syntax.JsonKey` | – | `support.type.property-name.json`, else `Syntax.Property` |
| `Syntax.YamlKey` | – | `entity.name.tag.yaml`, else `Syntax.Tag` |
| `Syntax.Heading` | – | `entity.name.section.markdown`, `markup.heading.markdown`, else `Syntax.Keyword` |
| `Syntax.Code` | – | `markup.inline.raw.string.markdown`, `markup.fenced_code.block.markdown`, else `Syntax.String` |
| `Syntax.Quote` | – | `markup.quote.markdown`, else `Syntax.Comment` |
| `Syntax.LinkDestination` | – | `markup.underline.link.markdown` |

A kind a theme colours no way takes the one after "else", then Passenger's palette from the theme's own colours.

| Passenger's token | VS Code's colours, the first given winning |
|---|---|
| `Frame` | `editorGroupHeader.tabsBackground`, `statusBar.background`, `activityBar.background` |
| `Panel` | `sideBar.background`, `panel.background` |
| `Field` | `input.background`, `dropdown.background` |
| `FieldBorder` | `input.border`, `dropdown.border` |
| `Popup` | `menu.background`, `editorWidget.background`, `quickInput.background` |
| `PopupBorder` | `menu.border`, `editorWidget.border`, `widget.border` |
| `Separator` | `menu.separatorBackground`, `sideBarSectionHeader.border` |
| `Text` | `foreground`, `sideBar.foreground` |
| `TextMuted` | `descriptionForeground` |
| `Icon` | `icon.foreground` |
| `Link` | `textLink.foreground` |
| `LinkHover` | `textLink.activeForeground` |
| `Error` | `errorForeground`, `editorError.foreground` |
| `Warning` | `editorWarning.foreground`, `list.warningForeground` |
| `TextSelection` | `selection.background`, `editor.selectionBackground` |
| `RowSelected` | `list.activeSelectionBackground`, `list.inactiveSelectionBackground` |
| `Accent` | `button.background`, `focusBorder`, `activityBarBadge.background` |
| `AccentText` | `button.foreground`, `activityBarBadge.foreground` |
| `AccentHover` | `button.hoverBackground` |
| `Focus` | `focusBorder` |
| `ScrollThumb` | `scrollbarSlider.background` |
| `ScrollThumbHover` | `scrollbarSlider.hoverBackground` |
| `Menu.Selected` | `menu.selectionBackground`, `list.activeSelectionBackground` |
| `Menu.SelectedText` | `menu.selectionForeground`, `list.activeSelectionForeground` |
| `TitleBar` | `titleBar.activeBackground` |
| `TitleBar.Text` | `titleBar.activeForeground` |
| `Stripe.Selected` | `activityBar.activeBackground` |
| `Stripe.SelectedIcon` | `activityBar.foreground` |
| `Tab.Active` | `tab.activeBackground` |
| `Tab.ActiveText` | `tab.activeForeground` |
| `Modified` | `editorCursor.foreground` |
| `Run` | `debugIcon.startForeground`, `testing.iconPassed` |
| `Test.Passed` | `testing.iconPassed` |
| `Test.Failed` | `testing.iconFailed` |
| `Test.Skipped` | `testing.iconSkipped` |
| `Vcs.Added` | `editorGutter.addedBackground` |
| `Vcs.Modified` | `editorGutter.modifiedBackground` |
| `Vcs.Deleted` | `editorGutter.deletedBackground` |
| `Vcs.File.Modified` | `gitDecoration.modifiedResourceForeground` |
| `Vcs.File.Added` | `gitDecoration.addedResourceForeground`, `gitDecoration.untrackedResourceForeground` |
| `Vcs.File.Deleted` | `gitDecoration.deletedResourceForeground` |
| `Vcs.File.Unversioned` | `gitDecoration.untrackedResourceForeground` |
| `Vcs.File.Conflicted` | `gitDecoration.conflictingResourceForeground` |
| `Terminal.Background` | `terminal.background`, `panel.background` |
| `Terminal.Foreground` | `terminal.foreground` |
| `Terminal.Cursor` | `terminalCursor.foreground`, `editorCursor.foreground` |
| `Terminal.Selection` | `terminal.selectionBackground` |
| `Terminal.Ansi0` | `terminal.ansiBlack` |
| `Terminal.Ansi1` | `terminal.ansiRed` |
| `Terminal.Ansi2` | `terminal.ansiGreen` |
| `Terminal.Ansi3` | `terminal.ansiYellow` |
| `Terminal.Ansi4` | `terminal.ansiBlue` |
| `Terminal.Ansi5` | `terminal.ansiMagenta` |
| `Terminal.Ansi6` | `terminal.ansiCyan` |
| `Terminal.Ansi7` | `terminal.ansiWhite` |
| `Terminal.Ansi8` | `terminal.ansiBrightBlack` |
| `Terminal.Ansi9` | `terminal.ansiBrightRed` |
| `Terminal.Ansi10` | `terminal.ansiBrightGreen` |
| `Terminal.Ansi11` | `terminal.ansiBrightYellow` |
| `Terminal.Ansi12` | `terminal.ansiBrightBlue` |
| `Terminal.Ansi13` | `terminal.ansiBrightMagenta` |
| `Terminal.Ansi14` | `terminal.ansiBrightCyan` |
| `Terminal.Ansi15` | `terminal.ansiBrightWhite` |
| `Editor.Gutter` | `editorGutter.background` |
| `Editor.PopupBorder` | `editorWidget.border`, `editorHoverWidget.border` |
| `Editor.Text` | `editor.foreground` |
| `Editor.LineNumber` | `editorLineNumber.foreground` |
| `Editor.LineNumberCurrent` | `editorLineNumber.activeForeground` |
| `Editor.CurrentLine` | `editor.lineHighlightBackground` |
| `Editor.Selection` | `editor.selectionBackground` |
| `Editor.SelectionInactive` | `editor.inactiveSelectionBackground` |
| `Editor.Caret` | `editorCursor.foreground` |
| `Editor.SearchMatch` | `editor.findMatchHighlightBackground` |
| `Editor.SearchCurrent` | `editor.findMatchBackground` |
| `Editor.Error` | `editorError.foreground` |
| `Editor.Warning` | `editorWarning.foreground` |
| `Editor.Info` | `editorInfo.foreground` |
| `Editor.Breakpoint` | `debugIcon.breakpointForeground` |
| `Editor.ExecutionLine` | `editor.stackFrameHighlightBackground` |
| `Editor.InlayHint` | `editorInlayHint.foreground` |
| `Editor.InlayHintBack` | `editorInlayHint.background` |
| `Editor.UsageRead` | `editor.wordHighlightBackground` |
| `Editor.UsageWrite` | `editor.wordHighlightStrongBackground` |
| `Editor.MatchedBrace` | `editorBracketMatch.background` |
| `Editor.Whitespace` | `editorWhitespace.foreground` |
| `Editor.LinkUnderline` | `editorLink.activeForeground` |
| `Diff.Added` | `diffEditor.insertedLineBackground`, `diffEditor.insertedTextBackground` |
| `Diff.Deleted` | `diffEditor.removedLineBackground`, `diffEditor.removedTextBackground` |
| `Diff.AddedWord` | `diffEditor.insertedTextBackground` |
| `Diff.DeletedWord` | `diffEditor.removedTextBackground` |

## A theme over a VS Code theme

```json
{
  "format": 1,
  "name": "Dusk over Dracula",
  "vscode": { "id": "dracula-theme.theme-dracula", "theme": "Dracula Theme", "version": "2.25.1" },
  "tokens": { "ControlRadius": 0, "Accent": "#FF79C6" },
  "fonts": { "code": { "family": "Fira Code" } }
}
```

`vscode` names a VS Code colour theme as the theme's base: `id` is its extension's Open VSX id (`publisher.name`), `theme`
its label in that extension, and `version`, which may be left out, pins the extension's version. The VS Code theme gives
every colour as above, and the theme's own tokens, background, fonts, languages and layout are laid over it, so a theme
gives only what it changes; it shows in the base's variant. The base is fetched from Open VSX with the theme and kept
beside it (`vscode/` in its folder), so it draws offline; Refresh takes the extension's newest version unless `version`
pins one.

## A code colour scheme

The same shape as a theme, giving only the code's tokens: `Editor.*`, `Syntax.*`, `SyntaxStyle.*` and `Diff.*`, and
`languages` as a theme gives them. It is laid over the theme in force; `dark` gives its colours under a dark theme and
`light` under a light one. In order: the theme's tokens, the theme's for a language, the scheme's, then the scheme's for
the language, the last laid winning.

## How Passenger reads a file

- Plain JSON in UTF-8: no comments, no trailing commas, no member given twice, at most 1 MB, nested at most 10 deep.
- A file that is not JSON, gives no `format` or another than 1, or gives a value of the wrong kind is refused whole,
  saying the file, the token and what it takes; nothing of it is used.
- A member or token this Passenger does not know is passed over and named, so a typo shows and a file written for a
  later Passenger still reads.

## A theme chosen is kept

A theme or scheme you choose is kept as its file was read, under Passenger's state folder in `themes/` (a folder for each
source, then one for each theme), so it draws at every start and offline without its source. Passenger takes a newer
copy only when you press Refresh. A theme you chose that is not kept yet (one chosen before Passenger moved its themes
to their repository) is fetched as Passenger starts, said in the status bar while it comes; Passenger's own theme shows
until then, and one that cannot be fetched is said, never dropped from your settings.

## Passenger's themes

Passenger carries its own theme, `Tokens.axaml`, and beside it Cubicle in its built-in source
(`src/Passenger.App/Theme/BuiltIn`): square, dense and calm, a blue accent on cool greys, its own default layout and a
smaller code size, to show what a theme can set. Harbour, the ten generated themes and the Reef code colour scheme live in
Passenger's themes repository, whose copy the tests carry in `tests/Passenger.Tests/Themes`. Harbour.json and Reef.json
are written by hand; Cubicle and the ten are written by `tools/themes.py` from its palettes (`just themes FOLDER`, the ten
into a checkout of the repository or the tests' copy, Cubicle into the built-in source), so change a palette there rather
than its file.
