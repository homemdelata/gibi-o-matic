# Design

The original prototype was made in Claude Design (the author's private canvas: https://claude.ai/artifact/RfPus5Wyu69J33vCsJMzxJ).
A copy of the files lives in `docs/design/prototype/`. Each `.dc.html` is a screen in HTML with inline styles: **a visual reference, not code to reuse**. Open it in a browser to look at it. The links between screens and some interactions work in Claude Design; opened straight in the browser, the screens may not render completely, because they depend on the canvas runtime. In that case, read the markup.

## Screens
| File | Screen | Phase |
|---|---|---|
| `Main.dc.html` | Library | F1 |
| `Inbox.dc.html` | Inbox | F1 (manual), F5 (automatic) |
| `Review.dc.html` | Metadata review | F2 |
| `Editor.dc.html` | Editor (single book and batch) | F2 |
| `Destinations.dc.html` | List of destinations | F3 |
| `NewDestination.dc.html` | "Add destination" wizard (3 steps) | F3 |
| `KindleDestination.dc.html` | Kindle destination detail (Send and Settings) | F4 |
| `Settings.dc.html` | Settings (Folders, Metadata, General) | F0/F2 |

The data on the screens (titles, numbers) are examples from the user's collection. The covers are colored placeholders with a halftone.

The prototype defines the screens' **layout**. The current **look** comes from the F7 revamp (2026-10-24), specified in [`design/revamp/REVAMP.md`](design/revamp/REVAMP.md), with the redesigned Library (`design/revamp/Library.dc.html`) and the icon (`design/revamp/icon.svg`, the source of truth for the drawing). Where the prototype and this document disagree on colors, fonts or shape, this document wins.

## Identity
**"A comic printed in the dark, with misregistration"** (a "subtle Spider-Verse" look):
- night-blue background; the light theme is cream comic paper with night-blue ink;
- lemon yellow is the only accent for actions;
- the icon's cyan and magenta appear **only** as a print offset on selected things and in a few ornaments (the title's little bars, the decorative halftone);
- everything else is sober: nothing slanted, nothing in italics, titles without effects.

The icon is a speech balloon shaped like a G, yellow, with misregistration, on a night-blue square with a blue halftone. `tools/IconGen` draws it with SkiaSharp and generates `Assets/gibiomatic.ico` (16–256 px, with less detail at small sizes) and `Assets/logo.png` (256 px, the sidebar logo).

## Tokens
They live in `src/GibiOMatic.App/Themes/Tokens.axaml`, one dictionary per theme with the **same keys** (there is a test for that and for the AA contrast of text). Always use `DynamicResource`.

### Colors
| Token | Dark (default) | Light (cream paper) | Use |
|---|---|---|---|
| `Bg` | `#060B1C` | `#F4EEDD` | window background |
| `Surface` | `#0B1630` | `#FBF6E9` | side menu, details panel, inactive chips |
| `Card` | `#0F1D3D` | `#FFFBF0` | cards |
| `Input` | `#0B1630` | `#FFFBF0` | fields, secondary buttons |
| `Hover` | `#13244D` | `#EAE2CB` | hover, background of a selected item in lists |
| `Border` | `#1E3266` | `#D9CFB4` | dividers, card borders |
| `BorderStrong` | `#2A4580` | `#B9AD8E` | borders of fields, secondary buttons and chips |
| `Text` | `#F1F4FA` | `#0B1630` | main text |
| `TextSecondary` | `#CFD8EC` | `#26314F` | inactive menu items |
| `TextMuted` | `#9DB0D6` | `#4A5470` | captions, metadata, section labels |
| `TextFaint` | `#8496BE` | `#5A6380` | placeholders |
| `Accent` | `#FFF27A` | `#FFF27A` | primary button, selection, checked chip (the icon's lemon) |
| `AccentHover` | `#FFF7A8` | `#FFF7A8` | primary hover |
| `OnAccent` | `#0B1630` | `#0B1630` | text on the yellow and on the badges |
| `AccentText` | `#FFF27A` | `#145E92` | the accent as text, icon, focus and progress (on cream, the yellow has no contrast) |
| `AccentBorder` | transparent | `#0B1630` | 1.5 px border of the primary button and checked chips, so the lemon doesn't vanish on cream |
| `Ok` / `OkText` | `#19E3FF` / `#19E3FF` | `#0FB5D6` / `#0A6A85` | OK, connected (the icon's cyan) |
| `Warn` / `WarnText` | `#FFA45C` / `#FFB074` | `#D9692B` / `#A3480F` | Review, outdated (orange) |
| `Bad` / `BadText` | `#FF2D87` / `#FF6FB0` | `#E8448A` / `#A3134D` | Poor, low confidence (the icon's magenta) |
| `BadSoft` | `#FF6FB0` | `#E8448A` | fill under dark text (the Review counter) |
| `GlitchCyan` / `GlitchMagenta` | `#19E3FF` / `#FF2D87` | `#0FB5D6` / `#E0186E` | selection offset and ornaments |
| `HalftoneDot` | `#1D7FC0` | `#1D7FC0` | decorative halftone |

- Yellow means action and selection. "Review" is orange (`Warn`), never yellow.
- Status colors always come with text or an icon. As text, use the `*Text` variants; fills (badges, dots, pills) use `Ok`, `Warn` and `Bad`, with `OnAccent` text.
- Contrast adjustments relative to the revamp spec: in the light theme, `Bad` became `#E8448A` (with `#C2185B`, the POOR badge text was at 3.05:1) and `OkText` became `#0A6A85` (`#0B6E8A` gave 4.496:1 against `Hover`); in the dark theme, `BadText` exists because `#FF2D87` on `Hover` gives 4.31:1.

### Shadows (`BoxShadows`, per theme)
| Resource | Value (dark) | Use |
|---|---|---|
| `SelectionShadowCover` | cyan `-6 0 0 3`, magenta `6 0 0 3`, `Accent` `0 0 0 3` | selected cover (grid and details panel) |
| `SelectionShadowRing` | the same, with 4 and 2 px | selected item in the Review queue |
| `SelectionShadow` | cyan `-3 0 0 0`, magenta `3 0 0 0` | active menu item, chosen candidate, current reader thumbnail, wizard choice |
| `SelectionShadowSmall` | cyan `-2 0 0 0`, magenta `2 0 0 0` | checked chips, segments, tabs, active reader tool, current table of contents item |
| `CoverShadow` | `5 5 0 0 #8C000000` (light: `4 4 0 0 #380B1630`) | unselected covers: a hard, print-like shadow |

Avalonia draws the shadows in list order, the last on top (the opposite of CSS): the yellow ring comes last. A control whose selection goes beyond its own bounds needs `ClipToBounds="False"` (the cover buttons and the menu items already have it).

### Typography
| Role | Font | Size/weight |
|---|---|---|
| Page title (`h1`) | Anybody | 38 px / 800, letter-spacing −0.015em, **no shadow or offset**; ornament on the right (`TitleMark`: a 30×4 magenta bar and a 16×3 cyan one, indented 8 px) |
| Section or card title (`h2`, `h3`) | Anybody | 24 and 20 px / 800 |
| Summary card numbers | Anybody | 36 px / 800 |
| App name in the menu | Anybody | 21 px / 800 |
| Text | IBM Plex Sans | 14 px / 400; labels 12.5 px |
| Emphasis | IBM Plex Sans | 600; primary buttons and active items, 700 |
| Section label | IBM Plex Sans | 12 px / 600, uppercase (`UpperCase`), letter-spacing 0.1em, `TextMuted` |
| Paths, counts, formats, % | IBM Plex Mono | 12–13 px; badges 10.5 px / 600 |

The fonts are OFL and are embedded as static TTFs (Avalonia doesn't select axes of variable fonts). Anybody is always upright, in weights 700 and 800.

### Shape and space
| Resource | Value |
|---|---|
| `RadiusControl`, `RadiusField` (buttons, fields, menu items) | 6 |
| `RadiusCard` (cards, file row) | 8 |
| `RadiusCardLarge` (large choices, empty page icon) | 10 |
| `RadiusCover` (covers) | 4 |
| `RadiusBadge` (badges, segments) | 4 |
| `RadiusPill` (chips, pills) | 99 |
| `BorderControl` (borders of controls and cards) | 1.5 |

- Control height: 44 px (`ControlHeight`); chips, 38 px.
- 4 px base spacing; common gaps: 8, 12, 16, 22, 28.
- Layout: 248 px side menu, fluid content and a 340 px details panel (270 in narrow windows).
- Covers at a 2:3 ratio. Every grid card reserves 12 px at the top and on the right, where series draw the volumes behind.

## Components
- **Offset selection (the look's signature)**: everything selected, current or checked gets yellow plus a cyan sliver on the left and a magenta one on the right (shadows above). **Nothing else uses this effect**: not hover, not focus, not primary buttons, not titles. Keyboard focus is the usual `AccentText` outline.
- **Menu item**: 44 px, radius 6, 18 px stroke icon, label and optional counter (mono, or a pill: Inbox in `Ok`, Review in `BadSoft`, `OnAccent` 700 text). Active: `Accent` background, `OnAccent` text and icon, weight 700 and the slivers; 4 px side margin so they aren't clipped.
- **Menu status box**: `Bg` background, 1.5 `Border` border, radius 8.
- **Primary button**: `Accent` background, `OnAccent` 700 text, 1.5 `AccentBorder` border, no shadow. **Secondary**: `Input` background, 1.5 `BorderStrong` border.
- **Fields and search**: `Input` background, 1.5 `BorderStrong` border, radius 6, magnifier in `TextMuted`.
- **Filter chip**: 38 px pill. Inactive: `Surface` background, 1.5 `BorderStrong` border, mono `TextMuted` counter. Checked: `Accent` background, `AccentBorder` border, weight 700 and the slivers.
- **Segments** (Series/Books/Universes, Kindle tabs, Inbox decisions): a box with a 1.5 `BorderStrong` border, radius 6 and padding 4/6; the chosen segment is `Accent` with the slivers.
- **Summary cards**: `Card` background, 1.5 `Border` border, radius 8, no shadow. "Needs review" has a 3 px top sliver in `GlitchMagenta` and "priority" in `BadText`.
- **Cover in the grid**: hard shadow; when selected, `SelectionShadowCover`. **Badge**: top-right corner, background in the status color (OK `Ok`, REVIEW `Warn`, POOR `Bad`), `OnAccent` text, mono 10.5 px / 600, radius 4.
- **Series and universe (cover stack)**: up to two covers behind, from the next volumes in reading order (unnumbered volumes go after the numbered ones), offset by (6, −6) and (12, −12), darkened 15% and 30%, with radius `RadiusCover` and a 1.5 `Bg` border. Count badge in the bottom-left corner: 28 px, `Accent` background, radius 6, a stack icon and the number in mono 700 14 px. Bottom line: "Series · 3 books · Publisher".
- **Details panel**: `Surface` background; cover with the selection; field checks as 2.4 stroke icons in the status color; file as a `Card` with radius 8 and a format label on a `Border` background.
- **Decorative halftone** (`Border.halftone`): patches of dots fading out in a radial gradient, behind the content and not clickable. Top right of the main area (`HalftoneDot`, grid 9, opacity 0.7), bottom-left corner of the menu (`GlitchMagenta`, grid 8, 0.22) and top right of the details panel (`GlitchCyan`, grid 8, 0.2); in the light theme, half the opacity. The halftone of the placeholder covers stays the same.
- **Destination card**: icon in a 44 px square, name, type, rule chips and a status strip with a colored dot.
- **Confidence line**: "94% · title + number + publisher", in mono, in the level's color (≥ 80 `Ok`, ≥ 40 `Warn`, below that `Bad`).
- **"Advanced"**: a button that expands ("▸ Advanced" / "▾ Advanced") and reveals a section with a `Surface` background.
- **Icons**: stroke icons, 2 px, rounded corners (Lucide style), as geometry in `Themes/Icons.axaml`.
