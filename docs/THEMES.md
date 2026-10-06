# Theme palettes

Sutty includes 63 named app and terminal palettes covering familiar VS Code
families and their distinct light/dark variants. This is a curated local catalog,
not every Marketplace extension or a live popularity ranking.
Choose an app theme in Options →
Appearance; **Follow application** also applies that theme's terminal background,
foreground, cursor and 16 ANSI colors. A separate terminal selection overrides it.
Preferences retain their existing names and ids.

The app keeps Sutty's gradient buttons, navigation indicators and selection tints.
Panels, borders, control states and gradient pairs are Sutty adaptations of each
palette, not a reproduction of VS Code's editor syntax or extension behavior.
Application text, accents and status colors are derived for readable contrast
against the app's surfaces. Terminal ANSI colors remain independent of that
derivation, so changing app chrome cannot silently rewrite shell output colors.
Gradient button text chooses black or white to suit the selected palette.

| Family | Included choices | Palette reference |
| --- | --- | --- |
| Sutty | Dark, Light | Original Deep Field design |
| VS Code | Dark+, Light+, Dark Modern, Light Modern, Quiet Light, Abyss, Kimbie Dark, Red, Tomorrow Night Blue | Microsoft's [default themes](https://github.com/microsoft/vscode/tree/main/extensions/theme-defaults/themes), [Quiet Light](https://github.com/microsoft/vscode/tree/main/extensions/theme-quietlight), [Abyss](https://github.com/microsoft/vscode/tree/main/extensions/theme-abyss), [Kimbie Dark](https://github.com/microsoft/vscode/tree/main/extensions/theme-kimbie-dark), [Red](https://github.com/microsoft/vscode/tree/main/extensions/theme-red), [Tomorrow Night Blue](https://github.com/microsoft/vscode/tree/main/extensions/theme-tomorrow-night-blue) |
| Dracula | Dracula | [Dracula palette and ANSI specification](https://spec.draculatheme.com/#sec-Color-Palette) |
| Monokai | Monokai, Monokai Dimmed | Microsoft [Monokai](https://github.com/microsoft/vscode/tree/main/extensions/theme-monokai), [Monokai Dimmed](https://github.com/microsoft/vscode/tree/main/extensions/theme-monokai-dimmed) |
| Atom One | Atom One Dark, Atom One Light | [One Dark Pro](https://github.com/Binaryify/OneDark-Pro), [Atom One Light](https://github.com/akamud/vscode-theme-onelight) |
| One Dark Pro | One Dark Pro, One Dark Pro Darker | [Binaryify's One Dark Pro palettes](https://github.com/Binaryify/OneDark-Pro/tree/master/themes) |
| GitHub | Dark, Light, Dark Dimmed | [GitHub's VS Code theme](https://github.com/primer/github-vscode-theme) |
| Solarized | Dark, Light | [Ethan Schoonover's Solarized](https://ethanschoonover.com/solarized/) |
| Nord | Nord | [Nord palette](https://www.nordtheme.com/docs/colors-and-palettes/) |
| Tokyo Night | Night, Storm, Light | [Tokyo Night VS Code theme](https://github.com/tokyo-night/tokyo-night-vscode-theme) |
| Catppuccin | Mocha, Macchiato, Frappé, Latte | [Catppuccin palette](https://github.com/catppuccin/palette) |
| Gruvbox | Dark, Light | [Gruvbox](https://github.com/morhetz/gruvbox) |
| Owl | Night Owl, Light Owl | [Sarah Drasner's Night Owl](https://github.com/sdras/night-owl-vscode-theme) |
| Material | Material, Palenight, Darker, Ocean, Lighter | [Archived Material source snapshot](https://github.com/antfu/vsc-material-theme/tree/main/scripts/generator/settings/specific) by Mattia Astorino and contributors |
| Ayu | Dark, Light, Mirage | [Ayu color palette](https://github.com/ayu-theme/ayu-colors) |
| Cobalt2 | Cobalt2 | [Wes Bos's Cobalt2](https://github.com/wesbos/cobalt2-vscode) |
| SynthWave | SynthWave '84 | [Robb Owen's SynthWave '84](https://github.com/robb0wen/synthwave-vscode) |
| Ubuntu | Ubuntu | Existing Sutty Ubuntu/Tango terminal palette |
| Rosé Pine | Rosé Pine, Moon, Dawn | [Rosé Pine's VS Code theme palettes](https://github.com/rose-pine/vscode/tree/main/themes) |
| Everforest | Dark, Light | [Sainnhe's Everforest VS Code palettes](https://github.com/sainnhe/everforest-vscode/tree/master/themes), medium contrast variants |
| Kanagawa | Wave, Dragon, Lotus | [Rebelot's original palette](https://github.com/rebelot/kanagawa.nvim/blob/master/lua/kanagawa/colors.lua) and [terminal mappings](https://github.com/rebelot/kanagawa.nvim/blob/master/lua/kanagawa/themes.lua); [VS Code port](https://marketplace.visualstudio.com/items?itemName=Huka.kanagawa-theme) |
| Horizon | Horizon, Horizon Bright | [Jonathan Olaleye's Horizon palettes](https://github.com/jolaleye/horizon-theme-vscode/tree/master/themes) |
| Andromeda | Andromeda | [Eliver Lara's Andromeda palette](https://github.com/EliverLara/Andromeda/blob/master/themes/Andromeda-color-theme.json) |
| Poimandres | Poimandres, Storm | [Poimandres VS Code palettes](https://github.com/drcmda/poimandres-theme/tree/main/themes) |
| Vesper | Vesper | [Rauno Freiberg's Vesper palette](https://github.com/raunofreiberg/vesper/blob/main/themes/Vesper-dark-color-theme.json) |
| Min | Min Dark, Min Light | [Miguel Solorio's Min palettes](https://github.com/miguelsolorio/min-theme/tree/master/themes) |
| Darcula | Darcula | [Rokoroku's VS Code Darcula palette](https://github.com/rokoroku/vscode-theme-darcula/blob/master/themes/darcula.json) |

## Adaptation details

- Each terminal palette contains all 16 ANSI slots. If an upstream VS Code theme
  omits slots, Sutty completes those slots with VS Code's light/dark defaults.
  This applies to Quiet Light, Kimbie Dark, Red, Horizon, Andromeda, Vesper,
  Min Dark and Darcula. Defined upstream ANSI values are retained.
- Themes without an editor foreground use the upstream foreground or default
  text token. Min Dark uses its neutral UI foreground rather than tinting all
  terminal text with its purple default token.
- Material's ANSI mappings are Sutty adaptations of its published color bases.
  Material Lighter uses the darker Material foreground `#546E7A` in place of the
  faint upstream `#90A4AE` for readable default terminal text.
- Italic, bold, glow and border-only editor variants are not separate Sutty
  themes because they do not provide a distinct color palette for this app.
- Existing 36 names and stable ids are unchanged. New themes only extend the
  picker and persisted-id allowlist; unknown values still fall back to Sutty Dark
  or Follow application.

Theme names identify their respective upstream projects; these are local Sutty
palette adaptations. No editor extensions, fonts, glow injection or remote theme
loading are installed. Archived themes remain static color references; no
upstream extension code is downloaded or executed. `sutty.Setting.ThemeCatalog` is the shared source for the
app picker, terminal picker and persisted terminal-id normalization.
