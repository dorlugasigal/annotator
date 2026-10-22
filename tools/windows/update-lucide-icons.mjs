#!/usr/bin/env node
// Writes apps/windows/src/Annotator.App/Icons/LucideIcons.g.cs from Assets/icon-map.json and the pinned
// lucide-static package (decision D-6). Lucide icons are 24-unit strokes (width 2, round caps and joins);
// this keeps only the mapped icons and turns every element into absolute path data (M, L, C, Q, A, Z)
// that the app builds into XAML geometry without parsing SVG at run time. A symbol can also map to a Lucide
// icon with its closed shapes filled ({ lucide, fill }) or to an original drawing in the map's glyphs
// ({ glyph }), for Mac symbols that no Lucide icon reads like.
//
// Fetch the package into a scratch folder outside the repository, then run:
//   npm pack lucide-static@<version from icon-map.json>
//   tar -xzf lucide-static-<version>.tgz
//   node tools/windows/update-lucide-icons.mjs --package <scratch>/package
import { readFileSync, writeFileSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const appFolder = join(root, 'apps', 'windows', 'src', 'Annotator.App');
const mapPath = join(appFolder, 'Assets', 'icon-map.json');
const outputPath = join(appFolder, 'Icons', 'LucideIcons.g.cs');

const packageIndex = process.argv.indexOf('--package');
if (packageIndex < 0 || !process.argv[packageIndex + 1]) {
  console.error('Usage: node tools/windows/update-lucide-icons.mjs --package <extracted lucide-static package folder>');
  process.exit(2);
}
const packageFolder = resolve(process.argv[packageIndex + 1]);
const map = JSON.parse(readFileSync(mapPath, 'utf8'));
const packageJson = JSON.parse(readFileSync(join(packageFolder, 'package.json'), 'utf8'));
if (packageJson.name !== map.lucide.package || packageJson.version !== map.lucide.version) {
  console.error(`Expected ${map.lucide.package} ${map.lucide.version}, found ${packageJson.name} ${packageJson.version}.`);
  process.exit(1);
}

const format = (value) => {
  const text = value.toFixed(3).replace(/\.?0+$/, '');
  return text === '-0' ? '0' : text;
};
const point = (x, y) => `${format(x)},${format(y)}`;

function parsePath(d) {
  const segments = [];
  let index = 0;
  let command = null;
  const skip = () => { while (index < d.length && /[\s,]/.test(d[index])) index++; };
  const number = () => {
    skip();
    const match = /^[+-]?(\d*\.\d+|\d+\.?)([eE][+-]?\d+)?/.exec(d.slice(index));
    if (!match) throw new Error(`Expected a number at ${index} in "${d}"`);
    index += match[0].length;
    return parseFloat(match[0]);
  };
  const flag = () => {
    skip();
    const value = d[index++];
    if (value !== '0' && value !== '1') throw new Error(`Expected an arc flag at ${index - 1} in "${d}"`);
    return value === '1' ? 1 : 0;
  };
  const counts = { m: 2, l: 2, h: 1, v: 1, c: 6, s: 4, q: 4, t: 2, z: 0 };
  for (;;) {
    skip();
    if (index >= d.length) break;
    if (/[MmLlHhVvCcSsQqTtAaZz]/.test(d[index])) {
      command = d[index++];
    } else if (command === null) {
      throw new Error(`Path data must start with a command: "${d}"`);
    }
    const lower = command.toLowerCase();
    const args = lower === 'a'
      ? [number(), number(), number(), flag(), flag(), number(), number()]
      : Array.from({ length: counts[lower] }, number);
    segments.push({ command, args });
    if (lower === 'z') command = null;
    else if (command === 'M') command = 'L';
    else if (command === 'm') command = 'l';
  }
  return segments;
}

function absolutePath(d) {
  const out = [];
  let x = 0, y = 0, startX = 0, startY = 0;
  let cubic = null, quadratic = null;
  for (const { command, args } of parsePath(d)) {
    const relative = command === command.toLowerCase();
    const ox = relative ? x : 0, oy = relative ? y : 0;
    let nextCubic = null, nextQuadratic = null;
    switch (command.toLowerCase()) {
      case 'm':
        x = ox + args[0]; y = oy + args[1]; startX = x; startY = y;
        out.push(`M ${point(x, y)}`);
        break;
      case 'l':
        x = ox + args[0]; y = oy + args[1];
        out.push(`L ${point(x, y)}`);
        break;
      case 'h':
        x = (relative ? x : 0) + args[0];
        out.push(`L ${point(x, y)}`);
        break;
      case 'v':
        y = (relative ? y : 0) + args[0];
        out.push(`L ${point(x, y)}`);
        break;
      case 'c': {
        const c1 = [ox + args[0], oy + args[1]], c2 = [ox + args[2], oy + args[3]];
        x = ox + args[4]; y = oy + args[5];
        out.push(`C ${point(...c1)} ${point(...c2)} ${point(x, y)}`);
        nextCubic = c2;
        break;
      }
      case 's': {
        const c1 = cubic ? [2 * x - cubic[0], 2 * y - cubic[1]] : [x, y];
        const c2 = [ox + args[0], oy + args[1]];
        x = ox + args[2]; y = oy + args[3];
        out.push(`C ${point(...c1)} ${point(...c2)} ${point(x, y)}`);
        nextCubic = c2;
        break;
      }
      case 'q': {
        const c = [ox + args[0], oy + args[1]];
        x = ox + args[2]; y = oy + args[3];
        out.push(`Q ${point(...c)} ${point(x, y)}`);
        nextQuadratic = c;
        break;
      }
      case 't': {
        const c = quadratic ? [2 * x - quadratic[0], 2 * y - quadratic[1]] : [x, y];
        x = ox + args[0]; y = oy + args[1];
        out.push(`Q ${point(...c)} ${point(x, y)}`);
        nextQuadratic = c;
        break;
      }
      case 'a':
        x = ox + args[5]; y = oy + args[6];
        out.push(`A ${point(args[0], args[1])} ${format(args[2])} ${args[3]} ${args[4]} ${point(x, y)}`);
        break;
      case 'z':
        out.push('Z');
        x = startX; y = startY;
        break;
      default:
        throw new Error(`Unsupported path command ${command}`);
    }
    cubic = nextCubic;
    quadratic = nextQuadratic;
  }
  return out.join(' ');
}

const ellipse = (cx, cy, rx, ry) =>
  `M ${point(cx - rx, cy)} A ${point(rx, ry)} 0 1 0 ${point(cx + rx, cy)} A ${point(rx, ry)} 0 1 0 ${point(cx - rx, cy)} Z`;

function rectangle(x, y, width, height, rx, ry) {
  if (!rx && !ry) {
    return `M ${point(x, y)} L ${point(x + width, y)} L ${point(x + width, y + height)} L ${point(x, y + height)} Z`;
  }
  rx = Math.min(rx || ry, width / 2);
  ry = Math.min(ry || rx, height / 2);
  const r = point(rx, ry);
  return [
    `M ${point(x + rx, y)}`, `L ${point(x + width - rx, y)}`, `A ${r} 0 0 1 ${point(x + width, y + ry)}`,
    `L ${point(x + width, y + height - ry)}`, `A ${r} 0 0 1 ${point(x + width - rx, y + height)}`,
    `L ${point(x + rx, y + height)}`, `A ${r} 0 0 1 ${point(x, y + height - ry)}`,
    `L ${point(x, y + ry)}`, `A ${r} 0 0 1 ${point(x + rx, y)}`, 'Z',
  ].join(' ');
}

const points = (list) => list.trim().split(/[\s,]+/).map(Number);

function convertElement(tag, attributes) {
  const n = (name) => parseFloat(attributes[name] ?? '0');
  switch (tag) {
    case 'path': return absolutePath(attributes.d);
    case 'circle': return ellipse(n('cx'), n('cy'), n('r'), n('r'));
    case 'ellipse': return ellipse(n('cx'), n('cy'), n('rx'), n('ry'));
    case 'rect': return rectangle(n('x'), n('y'), n('width'), n('height'), n('rx'), n('ry'));
    case 'line': return `M ${point(n('x1'), n('y1'))} L ${point(n('x2'), n('y2'))}`;
    case 'polyline':
    case 'polygon': {
      const values = points(attributes.points);
      const parts = [];
      for (let i = 0; i < values.length; i += 2) parts.push(`${i === 0 ? 'M' : 'L'} ${point(values[i], values[i + 1])}`);
      if (tag === 'polygon') parts.push('Z');
      return parts.join(' ');
    }
    default: throw new Error(`Unsupported element <${tag}>`);
  }
}

function convertIcon(name) {
  const svg = readFileSync(join(packageFolder, 'icons', `${name}.svg`), 'utf8');
  const parts = [];
  for (const match of svg.matchAll(/<(path|circle|ellipse|rect|line|polyline|polygon)\b([^>]*?)\/?>/g)) {
    const attributes = Object.fromEntries([...match[2].matchAll(/([\w-]+)="([^"]*)"/g)].map((m) => [m[1], m[2]]));
    const filled = attributes.fill !== undefined && attributes.fill !== 'none';
    parts.push({ data: convertElement(match[1], attributes), filled });
  }
  if (parts.length === 0) throw new Error(`${name}.svg has no drawable elements`);
  return parts;
}

// A symbol maps to a Lucide icon name, to { lucide, fill } for a Lucide icon whose closed shapes are filled with the
// stroke color (the Mac's filled symbols), or to { glyph } for one of the original drawings in map.glyphs.
function source(target) {
  if (typeof target === 'string') return { key: target, name: target, parts: () => convertIcon(target) };
  if (target.lucide) {
    const key = target.fill ? `${target.lucide}-filled` : target.lucide;
    return { key, name: key, parts: () => convertIcon(target.lucide).map((part) => ({ ...part, filled: target.fill || part.filled })) };
  }
  const drawing = map.glyphs?.[target.glyph];
  if (!drawing) throw new Error(`No glyph named "${target.glyph}" in icon-map.json`);
  return {
    key: `annotator-${target.glyph}`,
    name: `annotator/${target.glyph}`,
    parts: () => drawing.map((part) => ({ data: absolutePath(part.d), filled: part.fill === true })),
  };
}

const fieldName = (name) => 's_' + name.replace(/-(\w)/g, (_, c) => c.toUpperCase()).replace(/^(\d)/, '_$1');
const symbols = Object.entries(map.symbols).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)).map(([symbol, target]) => [symbol, source(target)]);
const icons = [...new Map(symbols.map(([, icon]) => [icon.key, icon])).values()].sort((a, b) => (a.key < b.key ? -1 : a.key > b.key ? 1 : 0));
const lines = [
  '// <auto-generated>',
  `// Generated by tools/windows/update-lucide-icons.mjs from ${map.lucide.package} ${map.lucide.version}`,
  '// (Lucide, ISC license; see THIRD_PARTY_NOTICES.md) and the original glyphs in Assets/icon-map.json.',
  '// Do not edit; change Assets/icon-map.json and run the script.',
  '// </auto-generated>',
  '#nullable enable',
  'namespace Annotator.App.Icons;',
  '',
  'internal static partial class LucideIcons',
  '{',
  `    public const string Version = "${map.lucide.version}";`,
  '',
];
for (const icon of icons) {
  const parts = icon.parts().map((part) => `new("${part.data}", ${part.filled})`).join(', ');
  lines.push(`    private static readonly LucideIcon ${fieldName(icon.key)} = new("${icon.name}", [${parts}]);`);
}
lines.push('', '    /// <summary>The icon for a Mac SF Symbol name, or null when the Mac does not use that symbol.</summary>');
lines.push('    public static LucideIcon? ForSymbol(string symbol) => symbol switch', '    {');
for (const [symbol, icon] of symbols) lines.push(`        "${symbol}" => ${fieldName(icon.key)},`);
lines.push('        _ => null,', '    };', '');
lines.push('    /// <summary>Every mapped SF Symbol name.</summary>');
lines.push(`    public static IReadOnlyList<string> Symbols { get; } = [${symbols.map(([symbol]) => `"${symbol}"`).join(', ')}];`);
lines.push('}', '');
writeFileSync(outputPath, lines.join('\n'));
console.log(`Wrote ${icons.length} icons for ${symbols.length} symbols to ${outputPath}`);
