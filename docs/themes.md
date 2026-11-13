# Passenger themes, format 1

A theme sets the colours and shapes of Passenger's window and code; a code colour scheme sets only the code's colours,
under any theme. Both are JSON data, never code. Passenger's own theme is the default: every theme and scheme is laid
over it, so a file gives only what it changes.

## A source

A source is a folder holding `passenger-themes.json` at its root, which lists its files by paths inside it (folders
joined by `/`, no `..`, no absolute path or address, each ending `.json`). A theme's images go beside its file.

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
three may be left out.

A token is named as its key in [Tokens.axaml](../src/Passenger.App/Theme/Tokens.axaml) without `Passenger.`:
`Frame`, `Editor.Background`, `Syntax.Keyword`, `SyntaxStyle.Comment`, `ControlRadius`. Tokens.axaml gives every token
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

## A code colour scheme

The same shape as a theme, giving only the code's tokens: `Editor.*`, `Syntax.*`, `SyntaxStyle.*` and `Diff.*`. It is
laid over the theme in force; `dark` gives its colours under a dark theme and `light` under a light one.

## How Passenger reads a file

- Plain JSON in UTF-8: no comments, no trailing commas, no member given twice, at most 1 MB, nested at most 10 deep.
- A file that is not JSON, gives no `format` or another than 1, or gives a value of the wrong kind is refused whole,
  saying the file, the token and what it takes; nothing of it is used.
- A member or token this Passenger does not know is passed over and named, so a typo shows and a file written for a
  later Passenger still reads.

## The built-in themes

Passenger carries Harbour, the ten generated themes and the Rider code colour scheme as a source in
`src/Passenger.App/Theme/BuiltIn`. Harbour.json and Rider.json are written by hand; the ten are written by
`tools/themes.py` from its palettes (`just themes`), so change a palette there rather than its file.
