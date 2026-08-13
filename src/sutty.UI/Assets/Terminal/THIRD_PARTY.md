# Package-local terminal renderer

Sutty ships these files inside the application package. Runtime loading from a CDN or
another network origin is not permitted.

| Component | Version | Shipped file | SHA-256 | License |
|---|---:|---|---|---|
| `@xterm/xterm` | 6.0.0 | `xterm-6.0.0.js` | `14903579FF54664CD72F8E8699E6961A6272C21863EC1C3B118CDC8AF5D4A972` | MIT |
| `@xterm/xterm` | 6.0.0 | `xterm-6.0.0.css` | `854A7C0FB70E8B1A083C16797AB827299FB18744F5AD34F227B48337E33293C6` | MIT |
| `@xterm/addon-fit` | 0.11.0 | `addon-fit-0.11.0.js` | `BA3EA256CE0620A0992A197D6C9BAEA64823FC93D8DA07A9E366CA9943C18527` | MIT |
| `@xterm/addon-search` | 0.16.0 | `addon-search-0.16.0.js` | `7BC1B8C7B3549411F6F6F779524C4DED6CA621FD80D64B40A70AE7C78AEFBF55` | MIT |

Upstream source: <https://github.com/xtermjs/xterm.js>. The license text is preserved in
`LICENSE.xterm.txt`. The JavaScript bundles were taken from the exact npm package versions
above after npm SHA-512 integrity verification; this table pins the bytes shipped by Sutty.
