# Licence texts

The full text of each licence a package Passenger carries names by its SPDX identifier (its nuspec's `license
type="expression"`), which `Passenger.Notices` writes into the app's notices under the packages it covers. Each file is
`<identifier>.txt`, as the SPDX License List gives it, unchanged:

- `MIT.txt` and `Apache-2.0.txt`: the SPDX License List 3.29.0, https://github.com/spdx/license-list-data at tag
  v3.29.0 (commit 31ba1a5), `text/MIT.txt` and `text/Apache-2.0.txt`.
- `ISC.txt`: the same release's `text/ISC.txt`, for an npm package under web/ that states ISC and carries no licence
  file of its own (boolbase).
- `BSD-3-Clause.txt`: the same release's `text/BSD-3-Clause.txt`, for Google.Protobuf.
- `MS-PL.txt`: the same release's `text/MS-PL.txt`, for Svg.Custom, the SVG model Svg.Skia draws from (the owner's
  choice for SVG files' pictures).

An npm package (under web/) that carries its own licence file is written with that file, which holds its copyright; one
without is written with the text here for the SPDX identifier its package.json states.

The SPDX License List is made by the SPDX project of the Linux Foundation (https://spdx.org/licenses/); each text is the
licence's own, whose authors publish it for anyone to copy and apply. The notices leave out a template's own placeholder
copyright line (MIT's `Copyright (c) <year> <copyright holders>`), since each package's copyright, as its package states
it, is written above the text.

A package under a licence not here fails the app's build naming it: add SPDX's `text/<identifier>.txt` at the same
release, and its line above.
