# "Subtle Spider-Verse" visual revamp

Specification for bringing to the app the look the user chose on 2026-10-24 (option **B2** of the revamp canvas, with icon **3a**).
This folder holds the references:

| File | What it is |
|---|---|
| `icon.svg` | The new icon. **Source of truth** for the drawing; open it in a browser. |
| `Library.dc.html` | The Library screen in the new look. It is a visual reference, not code to reuse: read the markup (colors, sizes, order of elements). Opened straight in the browser, it may not render completely, because it depends on the Claude Design runtime. |

Only the Library was designed. The other screens follow the same rules, without changing the layout they already have.

## Idea
Before it was "a comic printed in the dark"; now it is **"a comic printed in the dark, with misregistration"**.
- The background is night blue, no longer gray.
- Lemon yellow is the only accent for actions.
- The icon's cyan and magenta appear **only as a print offset on selected things** and in a few ornaments.
- Everything else is sober: nothing slanted, nothing in italics, titles without effects.

## 1. Icon
The icon is a speech balloon shaped like a G, yellow, with misregistration, on a night-blue square with a blue halftone. The geometry and colors are in `icon.svg` (viewBox 64).

**Rewrite `tools/IconGen`** to draw this icon with SkiaSharp. Today it draws the yellow square with the stroke.
- Draw, in order:
  1. the `#0B1630` square with radius 14/64;
  2. the `#1D7FC0` halftone: a 3-unit grid rotated 30°, dots of radius 0.85, with an opacity that fades from the top-right corner to 65% of the diagonal;
  3. the G's "ghosts" in `#19E3FF` (x −2.4) and `#FF2D87` (x +2.4), with `SKBlendMode.Screen`;
  4. the yellow `#FFF27A` G in 4 horizontal slices: y 0–21 offset 0; 21–28 offset −3.5; 28–37 offset +3; 37–64 offset 0;
  5. the little bars: magenta `x41 y23.5 13×2` and cyan `x7 y34 9×1.6`.
- **Small sizes need simplification.** Even so, check each one:

  | Size | Halftone | Slices | Bars | Ghosts |
  |---|---|---|---|---|
  | ≤ 24 px | no | no (whole G) | no | yes, offset by at least 1 px |
  | 32 px | yes | yes | no | yes |
  | ≥ 48 px | yes | yes | yes | yes |

- **Outputs:**
  - `src/GibiOMatic.App/Assets/gibiomatic.ico` (16–256 px, as today);
  - a 256 px PNG for the sidebar logo, for example in `Assets/logo.png`.
- **Sidebar (`ShellView.axaml`):** replace the yellow `Border` with the `StrokeIcon` (`Icon.Logo`) by a 40×40 `Image` with that PNG, with high-quality interpolation. Remove `Icon.Logo` if it ends up unused.
- The tray, the `.exe` and the Velopack installers already use the `.ico`. Check the tray at 16 px.

## 2. Tokens (`Themes/Tokens.axaml`)

### Dark theme (default)
| Token | New value | Before |
|---|---|---|
| `Bg` | `#060B1C` | `#121317` |
| `Surface` (side menu, details panel) | `#0B1630` | `#17181D` |
| `Card` | `#0F1D3D` | `#1A1B21` |
| `Input` | `#0B1630` | `#1D1F25` |
| `Hover` | `#13244D` | `#262931` |
| `Border` | `#1E3266` | `#262931` |
| `BorderStrong` | `#2A4580` | `#2E313A` |
| `Text` | `#F1F4FA` | |
| `TextSecondary` | `#CFD8EC` | |
| `TextMuted` | `#9DB0D6` | |
| `TextFaint` | `#8496BE` | |
| `Accent` / `AccentText` | `#FFF27A` | `#F5C542` |
| `AccentHover` / `AccentTextHover` | `#FFF7A8` | |
| `OnAccent` | `#0B1630` | |
| `Ok` / `OkText` | `#19E3FF` (the icon's cyan) | green |
| `Warn` / `WarnText` | `#FFA45C` / `#FFB074` (still orange) | |
| `Bad` | `#FF2D87`; text `#FF6FB0` (create `BadText` if needed) | |
| **new** `GlitchCyan` | `#19E3FF` | |
| **new** `GlitchMagenta` | `#FF2D87` | |
| **new** `HalftoneDot` | `#1D7FC0` | |

In the mockup, the REVIEW badge is yellow. **Don't follow that:** "Review" stays `Warn` (orange), so that yellow keeps meaning action and selection.

### Light theme: cream paper (user decision)
The light theme is cream comic paper, with night-blue ink.
| Group | Values |
|---|---|
| Surfaces | `Bg #F4EEDD` (the cream), `Surface #FBF6E9`, `Card #FFFBF0`, `Input #FFFBF0`, `Hover #EAE2CB` |
| Borders | `Border #D9CFB4`, `BorderStrong #B9AD8E` |
| Text (night-blue ink) | `Text #0B1630`, `TextSecondary #26314F`, `TextMuted #4A5470`, `TextFaint #5A6380` |
| Accent | `Accent #FFF27A`, the icon's same lemon; `OnAccent #0B1630`; `AccentText #145E92`, blue, because the yellow has no contrast as text on cream |
| **new** `AccentBorder` | `#0B1630`: a 1.5 px border on primary buttons and checked chips, so the lemon doesn't vanish on cream. In the dark theme, `Transparent`. |
| Glitch | `GlitchCyan #0FB5D6`, `GlitchMagenta #E0186E`, `HalftoneDot #1D7FC0` |
| Status | `Ok #0FB5D6` with `OkText #0B6E8A`; the current `Warn` and `WarnText`; `Bad #C2185B` |

### Rules for the tokens
- Both themes need **the same keys**. The contrast tests (AA in both themes) and the same-keys test, in `GibiOMatic.App.Tests`, must stay green. Adjust a shade if a pair fails, and say which.
- Also create, per theme, the selection `BoxShadows` (section 4) as resources, to use with `DynamicResource`.

### Shape
| Resource | Value |
|---|---|
| `RadiusControl` and `RadiusField` | 6 |
| `RadiusCard` | 8 |
| `RadiusCardLarge` | 10 |
| `RadiusCover` | 4 |
| Badges | 4 |
| Chips | a 99 pill, as today |
| Borders of controls and cards | 1.5 px |

## 3. Typography
- **Titles and large numbers: Anybody** (Google Fonts, OFL), weights 700 and 800, **always upright**. It replaces Bricolage Grotesque.
  - Use **static TTFs** (the `static/` folder of the Google Fonts package), following the "Static fonts, not variable" decision.
  - Add `OFL-Anybody.txt` and record it in `docs/THIRD-PARTY.md`.
  - If Bricolage ends up unused, remove its files and entry.
- Text stays in IBM Plex Sans; paths and small numbers stay in IBM Plex Mono.

| Role | Style |
|---|---|
| Page title | Anybody 800, 38 px, letter-spacing −0.015em, **no shadow or offset** |
| Title ornament | Right after the title, two stacked little bars: magenta 30×4 and cyan 16×3, indented 8 px. Use `GlitchMagenta` and `GlitchCyan`. |
| Section or card title | Anybody 800, 20–24 px |
| Summary card numbers | Anybody 800, 36 px |
| Section label | Plex Sans 600, 12 px, uppercase, letter-spacing 0.1em, `TextMuted` |
| App name in the menu | Anybody 800, 21 px |

## 4. Offset selection (the look's signature)
Everything **selected, current or checked** gets yellow plus a cyan sliver on the left and a magenta one on the right, like the icon's misregistration.
**Nothing else uses this effect.** Hover, focus, primary buttons and titles are left out.

| Element | How it looks |
|---|---|
| Selected cover (selected `cover-card` and the details panel cover) | `BoxShadow`: `0 0 0 3 Accent, -6 0 0 3 GlitchCyan, 6 0 0 3 GlitchMagenta` |
| Active menu item | `Accent` background, `OnAccent` text at weight 700, `OnAccent` icon. Shadows `-3 0 0 0 GlitchCyan, 3 0 0 0 GlitchMagenta`. 4 px side margin so they aren't clipped. |
| Checked chip and selected segment | `Accent` background with `-2 0 0 0` / `2 0 0 0` shadows |
| Other selection states (`queue-item.selected`, `candidate.selected`, `thumb.current`, `toc.current`, `tool.on`…) | The same rule, proportional to the size: 2–3 px slivers on controls, 6 px on covers |

- Keyboard focus stays the usual `AccentText` outline. It must not depend on the offset.
- **Unselected covers:** a hard `5 5 0 0 #8C000000` shadow instead of the soft one. In the light theme, use a lighter hard shadow.

## 5. Series and universes in the Library (cover stack)
Today the series card is a cover with a generic gray card behind it. It becomes **a stack with the covers of the next volumes**:
- **Back layers.**
  - With 2 books, there is 1 layer behind; with 3 or more, 2.
  - Offsets: (6, −6) for the 1st layer and (12, −12) for the 2nd.
  - Each layer shows the volume's thumbnail (or the placeholder color with halftone), darkened: a black overlay of ~15% on the 1st and ~30% on the 2nd.
  - The layers have radius `RadiusCover` and a 1.5 px border in the `Bg` color, to set them apart.
- **Alignment:** **every** grid card, single book or series, reserves 12 px at the top and on the right, so the covers line up.
- **Count badge:** bottom-left corner of the front cover, 28 px tall, `Accent` background, radius 6, a stack icon plus the number in Plex Mono 700 14 px, soft shadow. Keep the existing `ToolTip` and `AutomationProperties.Name` ("3 books in the series").
- **Bottom line:** "Series · 3 books · Publisher". The texts go in `Strings.resx` (pt-BR) and `Strings.en.resx`.
- **ViewModel:**
  - `SeriesCardViewModel`, and the universe equivalent, get up to 2 back covers, from the next volumes in reading order.
  - Reuse `CoverViewModel` (thumbnail or placeholder color).
  - Write unit tests for the choice of covers: 1 book, 2 books, 3 or more, and unnumbered volumes.
- The universe card uses the same stack.

## 6. Components (`Themes/Controls.axaml` and screens)

### Menu and cards
- **Side menu:**
  - `Surface` background;
  - items 44 px tall with radius 6;
  - the Inbox counter in an `Ok` (cyan) pill and the Review counter in a `Bad` pill in a text shade (`#FF6FB0`), both with `OnAccent` text;
  - the status box at the bottom gets a `Bg` background, a 1.5 `Border` border and radius 8.
- **Summary cards:** `Card` background, 1.5 `Border` border, radius 8, no shadow. "Needs review" gets a 3 px top border in `GlitchMagenta` and the "priority" text in `BadText`.

### Buttons and fields
| Control | Look |
|---|---|
| Primary | `Accent` background, `OnAccent` 700 text, radius 6, 1.5 `AccentBorder` border, **no shadow** |
| Secondary | `Input` background, 1.5 `BorderStrong` border, radius 6 |
| Search and fields | `Input` background, 1.5 `BorderStrong` border, radius 6, magnifier icon in `TextMuted` |
| Inactive chips | `Surface` background, 1.5 `BorderStrong` border, counter in mono `TextMuted` |

### Badges and details panel
- **Cover badges:** background in the status color, `OnAccent` text, Plex Mono 600 10.5 px, radius 4. OK = `Ok` (cyan), POOR = `Bad`, REVIEW = `Warn`.
- **Details panel:**
  - `Surface` background;
  - field checks in `Ok` (cyan), with a 2.4 stroke;
  - the file row as a card with a `Card` background and radius 8;
  - the format label (CBZ) with a `Border` background.

## 7. Decorative halftone
Three patches of dots that fade out in a radial gradient, all with `IsHitTestVisible="False"` and behind the content.
- Use a tiled `DrawingBrush`, like `HalftoneBrush`, with a `RadialGradientBrush` `OpacityMask`.

| Where | Dots | Opacity |
|---|---|---|
| Top right of the main area, behind the title (~520×360) | `HalftoneDot`, grid 9, radius 1.5 | 0.7 |
| Bottom-left corner of the side menu (~300×300) | `GlitchMagenta`, grid 8 | 0.22 |
| Top right of the details panel (~260×260) | `GlitchCyan`, grid 8 | 0.2 |

- In the light theme, use roughly half those opacities.
- The halftone of the placeholder covers stays as it is.

## 8. Scope and suggested order
Treat the work as a new phase in `ROADMAP.md` ("F7 · Visual revamp") and follow the `CLAUDE.md` flow: green build and tests, small commits, and a stop at the end for the user to test.

1. Icon: `IconGen`, `.ico`, sidebar logo.
2. Fonts and tokens for both themes, plus the contrast tests.
3. Shape and controls (`Controls.axaml`), including the offset selection.
4. Series and universe stack, with the ViewModel tests.
5. Decorative halftone and title ornament.
6. A pass over **every** screen: Inbox, Review, Editor, Destinations, Settings, reader, welcome and dialogs. Look for hard-coded colors in the XAML, old radii and soft shadows.
7. Regenerate the screenshots (`dotnet run --project tools/Screenshots`) and update `README.md` if needed.
8. Update `docs/DESIGN.md`: identity, tokens, typography, shape and components, pointing to this folder. Record in `docs/DECISIONS.md`:
   - the Anybody font;
   - the offset selection rule;
   - OK in cyan and Poor in magenta;
   - the light theme palette.

## What to ask the user (don't decide alone)
- The light theme: show a screenshot of it finished before the final commit.
- Any change to layout or text beyond what is described here.
