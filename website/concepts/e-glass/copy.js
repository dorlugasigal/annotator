// Paints a copy of a piece of the page into a canvas, so the glass has something real to bend.
// The page's own text stays HTML; this copy is only ever seen through glass. Each character is drawn where the
// browser laid it out, so the copy lines up with the page whatever the font, kerning or line breaks.
const range = document.createRange();
const metrics = new Map();
const visible = (color) => color && color !== "transparent" && !/,\s*0\)$/.test(color) && !/\/\s*0\)$/.test(color);

function ascentShare(ctx, font) {
  if (!metrics.has(font)) {
    ctx.font = font;
    const m = ctx.measureText("Hg");
    const up = m.fontBoundingBoxAscent ?? m.actualBoundingBoxAscent, down = m.fontBoundingBoxDescent ?? m.actualBoundingBoxDescent;
    metrics.set(font, up / (up + down));
  }
  return metrics.get(font);
}

function radius(style, w, h) {
  const value = style.borderTopLeftRadius || "0";
  const n = parseFloat(value) || 0;
  return Math.min(value.includes("%") ? (n / 100) * Math.min(w, h) : n, w / 2, h / 2);
}

function roundRect(ctx, x, y, w, h, r) {
  ctx.beginPath();
  if (ctx.roundRect) ctx.roundRect(x, y, w, h, Math.max(0, r));
  else ctx.rect(x, y, w, h);
}

// Returns the canvas and the box it covers, in CSS pixels relative to the group's own box.
export function paintCopy(group, scale, canvas = document.createElement("canvas")) {
  const base = group.getBoundingClientRect();
  const walker = document.createTreeWalker(group, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
  const boxes = [], texts = [], alpha = new Map();
  let biggest = 16;
  const styleOf = new Map();
  const style = (el) => { if (!styleOf.has(el)) styleOf.set(el, getComputedStyle(el)); return styleOf.get(el); };
  const opacity = (el) => {
    if (el === group || !el) return 1;
    if (!alpha.has(el)) alpha.set(el, parseFloat(style(el).opacity) * opacity(el.parentElement));
    return alpha.get(el);
  };
  const gone = new Set(), pictures = [];
  for (let node = group; node; node = walker.nextNode()) {
    const parent = node.parentElement;
    if (node !== group && gone.has(parent)) { if (node.nodeType === 1) gone.add(node); continue; }
    if (node.nodeType === 1) {
      const s = style(node);
      // A part with its own copy (it moves on its own) is left out of this one.
      if (s.display === "none" || (node !== group && node.hasAttribute("data-own-copy"))) { gone.add(node); continue; }
      if (node.namespaceURI !== "http://www.w3.org/1999/xhtml") { gone.add(node); if (node.localName === "svg" && s.visibility !== "hidden") pictures.push(node); continue; }
      if (s.visibility !== "hidden") boxes.push(node);
    } else if (node.data.trim()) {
      const s = style(parent);
      if (s.visibility === "hidden") continue;
      texts.push(node);
      biggest = Math.max(biggest, parseFloat(s.fontSize));
    }
  }
  const bleed = Math.ceil(Math.max(14, biggest * 0.32));
  const w = base.width + bleed * 2, h = base.height + bleed * 2;
  const k = Math.min(scale, 4096 / Math.max(w, h));
  canvas.width = Math.ceil(w * k); canvas.height = Math.ceil(h * k);
  const ctx = canvas.getContext("2d");
  ctx.setTransform(k, 0, 0, k, (bleed - base.left) * k, (bleed - base.top) * k);
  ctx.clearRect(base.left - bleed, base.top - bleed, w, h);

  for (const el of boxes) {
    if (el === group) continue;
    const s = style(el), r = el.getBoundingClientRect();
    if (!r.width || !r.height) continue;
    const round = radius(s, r.width, r.height), line = parseFloat(s.borderTopWidth) || 0;
    ctx.globalAlpha = opacity(el);
    if (visible(s.backgroundColor)) { roundRect(ctx, r.left, r.top, r.width, r.height, round); ctx.fillStyle = s.backgroundColor; ctx.fill(); }
    if (line && s.borderTopStyle !== "none" && visible(s.borderTopColor)) {
      roundRect(ctx, r.left + line / 2, r.top + line / 2, r.width - line, r.height - line, round - line / 2);
      ctx.strokeStyle = s.borderTopColor; ctx.lineWidth = line; ctx.stroke();
    }
    if (el.matches(":focus-visible")) {
      const ring = parseFloat(s.outlineWidth) || 3, gap = parseFloat(s.outlineOffset) || 0, out = gap + ring / 2;
      roundRect(ctx, r.left - out, r.top - out, r.width + out * 2, r.height + out * 2, round + out);
      ctx.strokeStyle = s.outlineColor; ctx.lineWidth = ring; ctx.stroke();
    }
  }

  ctx.textBaseline = "alphabetic";
  for (const node of texts) {
    const el = node.parentElement, s = style(el);
    const font = `${s.fontStyle} ${s.fontWeight} ${s.fontSize} ${s.fontFamily}`;
    const share = ascentShare(ctx, font), upper = s.textTransform === "uppercase";
    ctx.font = font; ctx.fillStyle = s.color; ctx.globalAlpha = opacity(el);
    const text = node.data;
    for (let i = 0; i < text.length;) {
      const code = text.codePointAt(i), size = code > 0xffff ? 2 : 1, ch = text.slice(i, i + size);
      if (ch.trim()) {
        range.setStart(node, i); range.setEnd(node, i + size);
        const r = range.getClientRects()[0];
        if (r && r.width) ctx.fillText(upper ? ch.toUpperCase() : ch, r.left, r.top + r.height * share);
      }
      i += size;
    }
  }
  // Simple pictures: the paths of an inline SVG, placed by its viewBox.
  for (const svg of pictures) {
    const r = svg.getBoundingClientRect(), box = svg.viewBox.baseVal;
    if (!r.width || !box || !box.width) continue;
    const fit = Math.min(r.width / box.width, r.height / box.height);
    ctx.save();
    ctx.globalAlpha = opacity(svg.parentElement);
    ctx.translate(r.left + (r.width - box.width * fit) / 2, r.top + (r.height - box.height * fit) / 2);
    ctx.scale(fit, fit); ctx.translate(-box.x, -box.y);
    for (const path of svg.querySelectorAll("path")) {
      const s = getComputedStyle(path), shape = new Path2D(path.getAttribute("d"));
      ctx.lineJoin = s.strokeLinejoin; ctx.lineCap = s.strokeLinecap; ctx.lineWidth = parseFloat(s.strokeWidth) || 1;
      if (s.fill && s.fill !== "none") { ctx.fillStyle = s.fill; ctx.fill(shape); }
      if (s.stroke && s.stroke !== "none") { ctx.strokeStyle = s.stroke; ctx.stroke(shape); }
    }
    ctx.restore();
  }
  ctx.globalAlpha = 1;
  return { canvas, x: -bleed, y: -bleed, w, h };
}

export async function fontsReady() {
  if (!document.fonts) return;
  const display = '"Bricolage Grotesque"';
  await Promise.all([
    document.fonts.load(`800 100px ${display}`), document.fonts.load(`760 40px ${display}`), document.fonts.load(`650 17px ${display}`),
    document.fonts.load(`400 20px ${display}`), document.fonts.load('500 13px "Geist Mono"'), document.fonts.load('600 27px "Caveat"'),
  ]).catch(() => {});
  await document.fonts.ready;
}
