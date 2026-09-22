# Theme palettes

Sutty includes 36 named app and terminal palettes. Choose an app theme in Options →
Appearance; **Follow application** also applies that theme's terminal background,
foreground, cursor and 16 ANSI colors. A separate terminal selection overrides it.
Preferences retain their existing names and ids.

The app keeps Sutty's gradient buttons, navigation indicators and selection tints.
Panels, borders, control states and gradient pairs are Sutty adaptations of each
palette, not a reproduction of VS Code's editor syntax or extension behavior.
Gradient button text chooses black or white to suit the selected palette.

| Family | Included choices | Palette reference |
| --- | --- | --- |
| Sutty | Dark, Light | Original Deep Field design |
| VS Code | Dark+, Light+, Dark Modern, Light Modern | [Microsoft default themes](https://github.com/microsoft/vscode/tree/main/extensions/theme-defaults/themes) |
| Dracula | Dracula | [Dracula palette and ANSI specification](https://spec.draculatheme.com/#sec-Color-Palette) |
| Monokai | Monokai, Monokai Dimmed | Microsoft [Monokai](https://github.com/microsoft/vscode/tree/main/extensions/theme-monokai), [Monokai Dimmed](https://github.com/microsoft/vscode/tree/main/extensions/theme-monokai-dimmed) |
| Atom One | Atom One Dark, Atom One Light | [One Dark Pro](https://github.com/Binaryify/OneDark-Pro), [Atom One Light](https://github.com/akamud/vscode-theme-onelight) |
| GitHub | Dark, Light, Dark Dimmed | [GitHub's VS Code theme](https://github.com/primer/github-vscode-theme) |
| Solarized | Dark, Light | [Ethan Schoonover's Solarized](https://ethanschoonover.com/solarized/) |
| Nord | Nord | [Nord palette](https://www.nordtheme.com/docs/colors-and-palettes/) |
| Tokyo Night | Night, Storm, Light | [Tokyo Night VS Code theme](https://github.com/tokyo-night/tokyo-night-vscode-theme) |
| Catppuccin | Mocha, Macchiato, Frappé, Latte | [Catppuccin palette](https://github.com/catppuccin/palette) |
| Gruvbox | Dark, Light | [Gruvbox](https://github.com/morhetz/gruvbox) |
| Owl | Night Owl, Light Owl | [Sarah Drasner's Night Owl](https://github.com/sdras/night-owl-vscode-theme) |
| Material | Material, Material Palenight | [Material theme project / successor](https://github.com/material-theme/vsc-material-theme) |
| Ayu | Dark, Light, Mirage | [Ayu color palette](https://github.com/ayu-theme/ayu-colors) |
| Cobalt2 | Cobalt2 | [Wes Bos's Cobalt2](https://github.com/wesbos/cobalt2-vscode) |
| SynthWave | SynthWave '84 | [Robb Owen's SynthWave '84](https://github.com/robb0wen/synthwave-vscode) |
| Ubuntu | Ubuntu | Existing Sutty Ubuntu/Tango terminal palette |

Theme names identify their respective upstream projects; these are local Sutty
palette adaptations. No editor extensions, fonts, glow injection or remote theme
loading are installed. `sutty.Setting.ThemeCatalog` is the shared source for the
app picker, terminal picker and persisted terminal-id normalization.
