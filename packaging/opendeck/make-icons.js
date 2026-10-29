#!/usr/bin/env node
// Writes the plugin's static images (action list icons, default key images) from the same drawing code the
// plugin uses at run time. Needs rsvg-convert for the PNGs. Run after changing draw.js:
//   node packaging/opendeck/make-icons.js
"use strict";

const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");
const { keySvg, infoSvg, cap, G, BLUE, RED, AMBER, GREY } = require("./com.zamarec.deck.sdPlugin/draw");

const out = path.join(__dirname, "com.zamarec.deck.sdPlugin", "icons");

const svgOf = dataUrl => Buffer.from(dataUrl.split(",")[1], "base64").toString();
const capOnly = (sticker, glyph) =>
  `<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144" viewBox="0 0 144 144">${cap(72, 72, 70, sticker, glyph)}</svg>`;

const images = {
  // The plugin and category icon, and one icon per action for the action list.
  plugin: capOnly(RED, G.rec),
  recording: capOnly(RED, G.rec),
  mark: capOnly(RED, G.flag),
  save: capOnly(RED, G.clip),
  pause: capOnly(AMBER, G.power),
  ui: capOnly(BLUE, G.window),
  info: capOnly(BLUE, `<rect x="45" y="46" width="30" height="28" rx="3" fill="none" stroke="#f8f4eb" stroke-width="4"/><path d="M51 55 H69 M51 64 H63" stroke="#f8f4eb" stroke-width="4" stroke-linecap="round"/>`),
  // What a key shows before the plugin has drawn it.
  "key-recording": svgOf(keySvg({ sticker: GREY, glyph: G.rec, opacity: 0.55, big: "ZamaREC", small: "connecting" })),
  "key-mark": svgOf(keySvg({ sticker: GREY, glyph: G.flag, opacity: 0.55, big: "MARK" })),
  "key-save": svgOf(keySvg({ sticker: GREY, glyph: G.clip, opacity: 0.55, big: "SAVE" })),
  "key-pause": svgOf(keySvg({ sticker: GREY, glyph: G.power, opacity: 0.55, big: "AUTO" })),
  "key-ui": svgOf(keySvg({ sticker: BLUE, glyph: G.window, big: "ZamaREC", small: "open window" })),
  "key-info": svgOf(infoSvg({ label: "ZAMAREC", accent: GREY, big: "…", small: "connecting" })),
};

for (const [name, svg] of Object.entries(images)) {
  const file = path.join(out, name + ".svg");
  fs.writeFileSync(file, svg);
  // Action list and plugin icons at 1x/2x as the Stream Deck SDK names them; keys at 72/144.
  const [one, two] = name.startsWith("key-") ? [72, 144] : [28, 56];
  execFileSync("rsvg-convert", ["-w", one, "-h", one, "-o", path.join(out, name + ".png"), file]);
  execFileSync("rsvg-convert", ["-w", two, "-h", two, "-o", path.join(out, name + "@2x.png"), file]);
}
console.log(`wrote ${Object.keys(images).length} images to ${out}`);
