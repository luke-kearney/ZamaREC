# ZamaREC brand assets

*An Inkwe innovation.*

## Colours

| Name | Hex | Use |
|---|---|---|
| Label blue | `#1e4f7a` | Primary brand colour, icon tile, "Zama" in the wordmark |
| Cap red | `#e81420` | Accent, "REC" in the wordmark, record/alert states |
| Sticker red | `#c3111b` | The cap sticker (cap red with a slight shade) |
| Paper | `#f8f4eb` | Light background |
| Cream | `#f3e6c8` | Secondary surface |
| Amber | `#b8620a` | Secondary accent |
| Brown | `#7a4206` | "An Inkwe innovation" line |
| Ink | `#2a1205` | Body text on paper |

## Fonts (Google Fonts)

- **Zilla Slab Bold** for the wordmark and headings
- **IBM Plex Mono Medium** for labels, timestamps and the "An Inkwe innovation" line
- **Pinyon Script** for the slogan only

All SVGs here have their text converted to outlines, so they render without the fonts installed.

## Files

| Path | Use |
|---|---|
| `icons/zamarec-icon.svg`, `icons/png/*` | App icon: .desktop launcher, web UI app icon (blue tile) |
| `icons/favicon.ico` | Web UI favicon |
| `icons/zamarec-icon-{cream,amber,mono}.svg` | Alternate icon colourways |
| `tray/zamarec-tray.svg`, `tray/*.png` | System tray and notification icon (cap only, no tile) |
| `tray/zamarec-cap.svg` | Cap mark on its own, with drop shadow |
| `lockup/zamarec-lockup.svg/.png` | Main logo (cap tile + wordmark), transparent background |
| `lockup/zamarec-lockup-on-paper.svg/.png` | Main logo on the paper background |
| `illustration/zamarec-bottle.*` | Bottle illustration (full size, plus a 704 px version) |
| `slogan/zamarec-slogan.svg/.png` | Slogan: "Enjoy the moment with a ZamaREC" — keep separate from the logo |

## Web UI header logo

The header shows **`lockup/zamarec-lockup.svg` by default**. Add a user setting (and/or clicking the logo) that switches it to **`illustration/zamarec-bottle-704.png`**. Persist the choice. The slogan is not part of the header; use it on a splash/about screen only.
