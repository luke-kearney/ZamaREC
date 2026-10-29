// Key images for the ZamaREC deck plugin: the brand's bottle cap with the sticker in the state's colour,
// a glyph on the sticker and a line or two of text under it. Also used by make-icons.js for the static icons.
"use strict";

const fs = require("fs");
const path = require("path");

const BLUE = "#1e4f7a", RED = "#e81420", AMBER = "#b8620a", GREY = "#6b727d", GREEN = "#2f9e57";
const PAPER = "#f8f4eb", INK = "#14100c", MUTED = "#9a9184";

// The cap from the brand's tray icon (packaging/brand/tray), with the sticker in the state's colour.
const RIM = fs.readFileSync(path.join(__dirname, "icons", "rim.txt"), "utf8").trim();

const esc = s => String(s).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const fit = (text, max, size) => Math.min(size, Math.floor((126 / Math.max(1, text.length)) * 1.6), max);

function cap(cx, cy, r, sticker, glyph = "", opacity = 1) {
  const k = r / 50;   // the source cap is 100 wide on a 120 grid centred at 60,60
  return `<g opacity="${opacity}" transform="translate(${cx - 60 * k} ${cy - 60 * k}) scale(${k})">
    <polygon points="${RIM}" fill="#d8cfb9"/>
    <circle cx="60" cy="60" r="43" fill="#efe7d3" stroke="#b9ae95" stroke-width="2"/>
    <circle cx="60" cy="60" r="27" fill="${sticker}"/>
    <circle cx="60" cy="60" r="27" fill="#000" fill-opacity="0.16"/>
    <ellipse cx="51" cy="50" rx="8" ry="4" fill="#fff" fill-opacity="0.14" transform="rotate(-35 51 50)"/>
    ${glyph}
  </g>`;
}

// Glyphs drawn inside the sticker (on the cap's 120 grid).
const G = {
  rec: `<circle cx="60" cy="60" r="11" fill="${PAPER}"/>`,
  idle: `<circle cx="60" cy="60" r="10" fill="none" stroke="${PAPER}" stroke-width="4"/>`,
  paused: `<rect x="50" y="48" width="7" height="24" rx="2" fill="${PAPER}"/><rect x="63" y="48" width="7" height="24" rx="2" fill="${PAPER}"/>`,
  died: `<path d="M50 50 L70 70 M70 50 L50 70" stroke="${PAPER}" stroke-width="6" stroke-linecap="round"/>`,
  off: `<path d="M50 60 H70" stroke="${PAPER}" stroke-width="6" stroke-linecap="round"/>`,
  flag: `<path d="M53 45 V77" stroke="${PAPER}" stroke-width="5" stroke-linecap="round"/><path d="M55 46 H73 L68 54 L73 62 H55 Z" fill="${PAPER}"/>`,
  clip: `<path d="M47 66 L73 50 M47 50 L73 66" stroke="${PAPER}" stroke-width="4" stroke-linecap="round"/><circle cx="46" cy="47" r="5" fill="none" stroke="${PAPER}" stroke-width="3.5"/><circle cx="46" cy="69" r="5" fill="none" stroke="${PAPER}" stroke-width="3.5"/>`,
  power: `<path d="M52 52 A12 12 0 1 0 68 52" fill="none" stroke="${PAPER}" stroke-width="5" stroke-linecap="round"/><path d="M60 44 V60" stroke="${PAPER}" stroke-width="5" stroke-linecap="round"/>`,
  window: `<rect x="45" y="48" width="30" height="24" rx="3" fill="none" stroke="${PAPER}" stroke-width="4"/><path d="M45 55 H75" stroke="${PAPER}" stroke-width="4"/>`,
};

/** A key: the cap on top, a line of large text under it and an optional small line, plus a corner badge. */
function keySvg({ sticker, glyph, opacity = 1, big, small = "", bigColour = PAPER, badge = null, ring = null }) {
  const b = badge === null ? "" : `<circle cx="120" cy="24" r="17" fill="${PAPER}"/><text x="120" y="31" fill="${INK}" font-family="sans-serif" font-weight="bold" font-size="${String(badge).length > 2 ? 14 : 19}" text-anchor="middle">${esc(badge)}</text>`;
  const r = ring ? `<rect x="3" y="3" width="138" height="138" rx="18" fill="none" stroke="${ring}" stroke-width="6"/>` : "";
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144" viewBox="0 0 144 144">
    <rect width="144" height="144" fill="${INK}"/>
    ${cap(72, 54, 42, sticker, glyph, opacity)}
    <text x="72" y="${small ? 116 : 126}" fill="${bigColour}" font-family="sans-serif" font-weight="bold" font-size="${fit(big, 28, 26)}" text-anchor="middle">${esc(big)}</text>
    ${small ? `<text x="72" y="136" fill="${MUTED}" font-family="sans-serif" font-size="${fit(small, 15, 15)}" text-anchor="middle">${esc(small)}</text>` : ""}
    ${b}${r}
  </svg>`;
  return "data:image/svg+xml;base64," + Buffer.from(svg).toString("base64");
}

/** Wraps text into at most <max> lines of about <width> characters, the last one cut with an ellipsis. */
function wrap(text, width, max) {
  const words = String(text).split(/\s+/).filter(Boolean), lines = [];
  let line = "";
  for (const w of words) {
    if (line && (line + " " + w).length > width) { lines.push(line); line = ""; }
    line = line ? line + " " + w : w;
  }
  if (line) lines.push(line);
  if (lines.length > max) { lines.length = max; lines[max - 1] = lines[max - 1].slice(0, width - 1) + "…"; }
  return lines.map(l => (l.length > width ? l.slice(0, width - 1) + "…" : l));
}

/**
 * A key that is a small display: a label on top, a big value (one colour, or parts in their own colours),
 * then either a small line or a few wrapped lines, and an optional fill bar.
 */
function infoSvg({ label, accent = BLUE, big = "", parts = null, bigColour = PAPER, small = "", lines = null, bar = null, dim = false }) {
  const text = parts ? parts.map(p => p.text).join("") : big;
  const size = fit(text, 40, 40);
  const value = parts ? parts.map(p => `<tspan fill="${p.colour}">${esc(p.text)}</tspan>`).join("") : esc(big);
  const body = lines
    ? lines.map((l, i) => `<text x="72" y="${62 + (3 - lines.length) * 10 + i * 20}" fill="${PAPER}" font-family="sans-serif" font-size="17" text-anchor="middle">${esc(l)}</text>`).join("")
    : `<text x="72" y="${bar ? 82 : 88}" xml:space="preserve" fill="${bigColour}" font-family="sans-serif" font-weight="bold" font-size="${size}" text-anchor="middle">${value}</text>`;
  const b = bar ? `<rect x="16" y="98" width="112" height="10" rx="5" fill="#3a332b"/>
    <rect x="16" y="98" width="${Math.max(0, Math.min(1, bar.frac)) * 112}" height="10" rx="5" fill="${bar.colour}"/>` : "";
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144" viewBox="0 0 144 144">
    <rect width="144" height="144" fill="${INK}"/>
    <g opacity="${dim ? 0.55 : 1}">
    <rect x="0" y="0" width="144" height="6" fill="${accent}"/>
    <text x="72" y="32" fill="${MUTED}" font-family="sans-serif" font-weight="bold" font-size="${fit(label, 17, 17)}" letter-spacing="1" text-anchor="middle">${esc(label)}</text>
    ${body}${b}
    ${small ? `<text x="72" y="130" fill="${MUTED}" font-family="sans-serif" font-size="${fit(small, 16, 16)}" text-anchor="middle">${esc(small)}</text>` : ""}
    </g>
  </svg>`;
  return "data:image/svg+xml;base64," + Buffer.from(svg).toString("base64");
}

module.exports = { keySvg, infoSvg, wrap, cap, G, BLUE, RED, AMBER, GREY, GREEN, PAPER, INK, MUTED };
