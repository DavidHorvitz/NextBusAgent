# Asset Specifications

Source artwork should be original or properly licensed. Keep editable source (for example SVG/PNG and the WAV project/source) alongside exported runtime assets and record provenance.

## Tray Icons

Provide three `.ico` files. Each file should contain 16x16, 32x32, 48x48, and 256x256 representations, transparent background, crisp silhouettes at small sizes, and sufficient contrast in light and dark taskbars.

| File | State | Visual direction |
| --- | --- | --- |
| `bus_active.ico` | Monitoring | Teal bus glyph, clear neutral outline |
| `bus_muted.ico` | Muted | Same silhouette in grayscale with a legible pause/slash indicator |
| `bus_alert.ico` | Arrival or delay | Warm amber accent and distinct alert mark; do not rely on animation alone |

Keep the silhouette consistent between states so users can identify the app quickly. Provide accessible state names in the tray tooltip; color is never the only state cue.

## Audio

Provide a short `arrival.wav` (roughly 300-900 ms) with a soft, non-startling chime, no clipping, and no long silence. The UI must support mute independently of Windows volume, and audio must never play for every polling update. A separate delay sound should be optional and distinct.