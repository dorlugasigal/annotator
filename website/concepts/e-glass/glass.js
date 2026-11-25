// The glass: a lens, a toolbar slab and two small marks, drawn over the page on a clear canvas.
// The page itself stays HTML. Every frame the things behind the glass (a painted copy of the page text, the
// footage as a video texture) are drawn into a hidden picture, and each glass object bends that picture:
// refraction per wavelength for the spectral edge, a mirror of a small studio for the highlights.
// The lens gets its own, much sharper picture of just the spot behind it, so what it magnifies stays crisp.
import * as THREE from "three";
import { RoundedBoxGeometry } from "three/addons/geometries/RoundedBoxGeometry.js";
import { mergeVertices, toCreasedNormals } from "three/addons/utils/BufferGeometryUtils.js";
import { APP_ICONS } from "/concepts/shared/app-icons.js";
import { mark as brandMark } from "/concepts/shared/toolbar-glyphs.js";

// Colors here are the page's own sRGB numbers, passed through untouched, so the glass shows exactly the page.
THREE.ColorManagement.enabled = false;

export function webglAvailable() {
  try { return Boolean(document.createElement("canvas").getContext("webgl2")); } catch { return false; }
}

const FOV = 24;
export const SLAB_ASPECT = 11.2, PILL_ASPECT = 2.5;
export const SLAB_GLYPHS = ["cursorarrow", "|", "rectangle", "circle", "arrow.up.right", "|", "pencil", "textformat", "highlighter", "|", "eraser"];

const PLANE_VERT = `varying vec2 vUv; void main() { vUv = uv; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`;
const PLANE_FRAG = `uniform sampler2D map; uniform float opacity; varying vec2 vUv;
void main() { gl_FragColor = texture2D(map, vUv) * opacity; }`;
const REEL_FRAG = `uniform sampler2D map; uniform vec2 uSize; uniform float uRadius; uniform float opacity; varying vec2 vUv;
void main() {
  vec2 q = abs((vUv - 0.5) * uSize) - uSize * 0.5 + uRadius;
  float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - uRadius;
  float a = (1.0 - smoothstep(-0.75, 0.75, d / max(fwidth(d), 1e-4))) * opacity;
  gl_FragColor = vec4(texture2D(map, vUv).rgb * a, a);
}`;
const BACKDROP_FRAG = `uniform vec3 c0; uniform vec3 c1; uniform vec3 c2; uniform float mid; uniform vec2 uView; uniform vec3 uGrid; uniform vec4 uLine; varying vec2 vUv;
float rule(float at, float gap) {
  float m = mod(at, gap), w = max(fwidth(at), 1e-4);
  if (m > gap * 0.5) m -= gap;
  return clamp((0.5 - abs(m - 0.5)) / w + 0.5, 0.0, 1.0);
}
void main() {
  float v = 1.0 - vUv.y;
  vec3 c = v < mid ? mix(c0, c1, v / mid) : mix(c1, c2, (v - mid) / (1.0 - mid));
  // The same ruled paper as the page's CSS: one-pixel lines, drawn sharp at any magnification.
  vec2 p = vec2(vUv.x, v) * uView - uGrid.xy;
  c = mix(c, uLine.rgb, uLine.a * rule(p.x, uGrid.z));
  c = mix(c, uLine.rgb, uLine.a * rule(p.y, uGrid.z));
  gl_FragColor = vec4(c, 1.0);
}`;

const GLASS_VERT = `
uniform float uStretch;
varying vec3 vWorld; varying vec3 vNormal; varying vec3 vLocal; varying vec3 vLocalN; varying vec3 vTx; varying vec3 vTy;
void main() {
  vec3 p = position;
  p.x += sign(p.x) * uStretch;
  vLocal = p; vLocalN = normal;
  vec4 w = modelMatrix * vec4(p, 1.0);
  vWorld = w.xyz;
  mat3 m = mat3(modelMatrix);
  vNormal = normalize(m * normal);
  vTx = normalize(m[0]); vTy = normalize(m[1]);
  gl_Position = projectionMatrix * viewMatrix * w;
}`;
const GLASS_FRAG = `
uniform sampler2D uBack; uniform mat4 uBackVP; uniform sampler2D uEnv;
uniform float uIor; uniform float uThick; uniform float uDisp; uniform vec3 uTint; uniform vec3 uEdge;
#ifdef USE_BUMP
uniform sampler2D uBump; uniform sampler2D uBumpPill; uniform vec2 uBumpSize; uniform vec2 uPillSize; uniform float uPill;
float heightAt(vec2 local) {
  float a = textureLod(uBump, local / uBumpSize + 0.5, 1.0).r;
  float b = textureLod(uBumpPill, local / uPillSize + 0.5, 1.0).r;
  return mix(a, b, uPill);
}
#endif
varying vec3 vWorld; varying vec3 vNormal; varying vec3 vLocal; varying vec3 vLocalN; varying vec3 vTx; varying vec3 vTy;

vec3 envAt(vec3 d) {
  vec2 uv = vec2(atan(d.x, d.z) / 6.2831853 + 0.5, asin(clamp(d.y, -1.0, 1.0)) / 3.14159265 + 0.5);
  vec3 e = textureLod(uEnv, uv, 0.0).rgb;
  float lum = dot(e, vec3(0.3, 0.59, 0.11));
  // The lights are far brighter than the room: even the few percent that clear glass mirrors show as a glint.
  return e * (1.0 + 13.0 * smoothstep(0.9, 1.0, lum));
}
vec2 backUV(vec3 p) { vec4 c = uBackVP * vec4(p, 1.0); return c.xy / c.w * 0.5 + 0.5; }

void main() {
  vec3 N = normalize(vNormal);
  vec3 V = normalize(cameraPosition - vWorld);
  vec3 I = -V;
  float frost = 0.0, emboss = 0.0;
  #ifdef USE_BUMP
    float face = smoothstep(0.6, 0.97, vLocalN.z);
    float e = 0.012;
    float h = heightAt(vLocal.xy);
    vec2 g = vec2(heightAt(vLocal.xy + vec2(e, 0.0)) - heightAt(vLocal.xy - vec2(e, 0.0)), heightAt(vLocal.xy + vec2(0.0, e)) - heightAt(vLocal.xy - vec2(0.0, e))) * face;
    N = normalize(N + vTy * vLocal.y * 0.5 * face - (vTx * g.x + vTy * g.y) * 0.85);
    frost = h * face; emboss = dot(g, vec2(0.7, -0.7));
  #endif

  // Seven wavelengths, red bends least and violet most: white stays white, edges split into a spectrum.
  vec3 sum = vec3(0.0), weight = vec3(0.0);
  for (int i = 0; i < 7; i++) {
    float s = float(i) / 6.0;
    vec3 r = refract(I, N, 1.0 / (uIor + (s - 0.5) * uDisp));
    vec3 side = r - I * dot(r, I);
    vec3 w = vec3(smoothstep(0.58, 0.0, s), 1.0 - abs(2.0 * s - 1.0), smoothstep(0.42, 1.0, s));
    sum += texture2D(uBack, backUV(vWorld + side * uThick)).rgb * w;
    weight += w;
  }
  vec3 through = sum / weight * uTint;

  float nv = clamp(dot(N, V), 0.0, 1.0);
  float fresnel = 0.045 + 0.955 * pow(1.0 - nv, 5.0);
  vec3 mirror = envAt(reflect(I, N));
  // Thick glass is a little denser where you look along it.
  through *= mix(uEdge, vec3(1.0), smoothstep(0.0, 0.7, nv));
  vec3 col = through * (1.0 - fresnel) + mirror * fresnel;
  col += emboss * 0.55;
  col = mix(col, vec3(1.0), frost * 0.1);
  gl_FragColor = vec4(col, 1.0);
}`;

// A small studio to mirror: soft boxes for the highlights, a dark floor and dark flags so that the rim of clear
// glass has something darker than the page to show, and one warm and one cool card for color.
function studio() {
  const c = document.createElement("canvas");
  c.width = 1024; c.height = 512;
  const g = c.getContext("2d");
  const sky = g.createLinearGradient(0, 0, 0, 512);
  sky.addColorStop(0, "#f6f3ee"); sky.addColorStop(0.4, "#dedad3"); sky.addColorStop(0.56, "#9a9289"); sky.addColorStop(1, "#57504a");
  g.fillStyle = sky; g.fillRect(0, 0, 1024, 512);
  const soft = (x, y, w, h, color, blur) => {
    g.save(); g.shadowColor = color; g.shadowBlur = blur; g.shadowOffsetX = 3000; g.fillStyle = color;
    g.fillRect(x - 3000, y, w, h); g.restore();
  };
  // The middle of this picture is behind the viewer, its two outer edges are behind the page. Behind the page it
  // is dim: that is what the rim of the glass mirrors, and why the rim shows against a light page.
  soft(-80, 0, 190, 512, "#2e2b28", 70); soft(914, 0, 190, 512, "#2e2b28", 70);
  soft(392, 118, 86, 78, "#ffffff", 5);       // a window, up and to the left
  soft(392, 204, 86, 12, "#ffffff", 3);
  soft(590, 80, 13, 230, "#ffffff", 3);       // a strip light on the right
  soft(300, 30, 420, 12, "#ffffff", 4);       // a long light overhead, for the top edges
  soft(150, 60, 120, 120, "#e6e3de", 30);     // a pale wall far left, not bright enough to glare
  soft(110, 380, 240, 80, "#ffb58c", 50);     // a warm card, low left
  soft(690, 340, 220, 100, "#a9c2ff", 50);    // a cool card, low right
  const t = new THREE.CanvasTexture(c);
  t.wrapS = THREE.RepeatWrapping; t.generateMipmaps = false; t.minFilter = THREE.LinearFilter;
  return t;
}

function softSprite(w, h, blur, draw) {
  const c = document.createElement("canvas");
  c.width = w; c.height = h;
  const g = c.getContext("2d");
  draw(g, (color, amount, shape) => {
    g.save(); g.shadowColor = color; g.shadowBlur = amount; g.shadowOffsetX = w * 3; g.fillStyle = "#000"; g.strokeStyle = "#000";
    g.translate(-w * 3, 0); shape(); g.restore();
  });
  const t = new THREE.CanvasTexture(c);
  t.premultiplyAlpha = true;
  return t;
}

function iconPaths(g, name, cx, cy, size, line) {
  const k = size / 24;
  g.save(); g.translate(cx - size / 2, cy - size / 2); g.scale(k, k);
  g.lineWidth = line / k; g.lineCap = "round"; g.lineJoin = "round";
  for (const part of APP_ICONS[name]) { const p = new Path2D(part.d); if (part.fill) g.fill(p); g.stroke(p); }
  g.restore();
}

// Height maps for the pictograms pressed into the slab: the toolbar's tools, and the one button it shrinks to.
function bumpMaps() {
  const strip = document.createElement("canvas"), pill = document.createElement("canvas");
  const unit = 176;
  strip.width = Math.round(unit * SLAB_ASPECT); strip.height = unit;
  let g = strip.getContext("2d");
  g.fillStyle = "#000"; g.fillRect(0, 0, strip.width, unit);
  g.fillStyle = g.strokeStyle = "#fff";
  const step = unit * 0.98, gap = unit * 0.36, total = SLAB_GLYPHS.reduce((n, name) => n + (name === "|" ? gap : step), 0);
  let x = (strip.width - total) / 2;
  for (const name of SLAB_GLYPHS) {
    if (name === "|") { g.fillRect(x + gap / 2 - 2, unit * 0.3, 4, unit * 0.4); x += gap; continue; }
    iconPaths(g, name, x + step / 2, unit / 2, unit * 0.46, 8.5);
    x += step;
  }
  pill.width = Math.round(unit * PILL_ASPECT); pill.height = unit;
  g = pill.getContext("2d");
  g.fillStyle = "#000"; g.fillRect(0, 0, pill.width, unit);
  g.fillStyle = "#fff";
  const svg = brandMark(100), box = /viewBox="([^"]+)"/.exec(svg)[1].split(" ").map(Number), d = /<path[^>]* d="([^"]+)"/.exec(svg)[1];
  const k = (unit * 0.5) / box[3];
  g.save(); g.translate(pill.width / 2 - (box[2] * k) / 2, unit / 2 - (box[3] * k) / 2); g.scale(k, k); g.translate(-box[0], -box[1]); g.fill(new Path2D(d)); g.restore();
  return [strip, pill].map((c) => {
    const t = new THREE.CanvasTexture(c);
    t.minFilter = THREE.LinearMipmapLinearFilter; t.generateMipmaps = true;
    return t;
  });
}

// A solid of revolution around the z axis, from a profile of [radius, z, normal radius, normal z].
function revolve(profile, around) {
  const position = [], normal = [], index = [];
  for (let j = 0; j <= around; j++) {
    const a = (j / around) * Math.PI * 2, c = Math.cos(a), s = Math.sin(a);
    for (const [r, z, nr, nz] of profile) { position.push(r * c, r * s, z); normal.push(nr * c, nr * s, nz); }
  }
  const n = profile.length;
  for (let j = 0; j < around; j++) for (let i = 0; i < n - 1; i++) {
    const a = j * n + i, b = (j + 1) * n + i;
    index.push(a, a + 1, b, b, a + 1, b + 1);
  }
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.Float32BufferAttribute(position, 3));
  geometry.setAttribute("normal", new THREE.Float32BufferAttribute(normal, 3));
  geometry.setIndex(index);
  return geometry;
}

// A thick magnifying lens of radius 1: a domed front, a rounded rim, a flatter back.
export const LENS_CURVE = 1.85;
function lensGeometry() {
  const rim = 0.075, profile = [];
  const cap = (curve, sign, steps, from) => {
    const reach = Math.asin((1 - rim) / (curve - rim));
    for (let i = from; i <= steps; i++) {
      const t = (i / steps) * reach;
      profile.push([curve * Math.sin(t), sign * ((rim - curve) * Math.cos(reach) + curve * Math.cos(t)), Math.sin(t), sign * Math.cos(t)]);
    }
    return reach;
  };
  const front = cap(LENS_CURVE, 1, 40, 0);
  const back = Math.asin((1 - rim) / (4 - rim)), center = 1 - rim;
  for (let i = 1; i < 16; i++) {
    const t = front + (i / 16) * (Math.PI - back - front);
    profile.push([center + rim * Math.sin(t), rim * Math.cos(t), Math.sin(t), Math.cos(t)]);
  }
  const tail = profile.length;
  cap(4, -1, 12, 0);
  profile.splice(tail, profile.length - tail, ...profile.slice(tail).reverse());
  return revolve(profile, 128);
}

function arrowGeometry() {
  const pts = [[-0.52, 0.1], [0.1, 0.1], [0.1, 0.3], [0.52, 0], [0.1, -0.3], [0.1, -0.1], [-0.52, -0.1]];
  const shape = new THREE.Shape(), round = 0.045, n = pts.length;
  pts.forEach((p, i) => {
    const prev = pts[(i + n - 1) % n], next = pts[(i + 1) % n];
    const toward = (q) => { const d = Math.hypot(q[0] - p[0], q[1] - p[1]); return [p[0] + ((q[0] - p[0]) / d) * round, p[1] + ((q[1] - p[1]) / d) * round]; };
    const a = toward(prev), b = toward(next);
    if (i) shape.lineTo(a[0], a[1]); else shape.moveTo(a[0], a[1]);
    shape.quadraticCurveTo(p[0], p[1], b[0], b[1]);
  });
  shape.closePath();
  let geometry = new THREE.ExtrudeGeometry(shape, { depth: 0.07, bevelEnabled: true, bevelThickness: 0.065, bevelSize: 0.06, bevelSegments: 7, curveSegments: 8 });
  geometry.translate(0, 0, -0.035);
  geometry.deleteAttribute("uv");
  geometry = mergeVertices(geometry, 1e-4);
  // The faces stay flat like plate glass; only the bevel is rounded.
  return toCreasedNormals(geometry, 0.3);
}

export function createGlass(canvas, { onLost } = {}) {
  const renderer = new THREE.WebGLRenderer({ canvas, alpha: true, antialias: true, powerPreference: "high-performance" });
  renderer.outputColorSpace = THREE.LinearSRGBColorSpace;
  renderer.setClearColor(0x000000, 0);
  canvas.addEventListener("webglcontextlost", (event) => { event.preventDefault(); onLost?.(); });

  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(FOV, 1, 20, 6000), lensCamera = new THREE.PerspectiveCamera(FOV, 1, 20, 6000);
  const PAGE = 0, SHADE = 1, GLASS = 2, LENS = 3, LENS_SHADE = 4;
  let vw = 1, vh = 1, ratio = 1, distance = 1000;
  const targetOptions = { depthBuffer: true, generateMipmaps: false, minFilter: THREE.LinearFilter, magFilter: THREE.LinearFilter };
  const pageTarget = new THREE.WebGLRenderTarget(4, 4, { ...targetOptions, depthBuffer: false });
  const lensTarget = new THREE.WebGLRenderTarget(4, 4, { ...targetOptions, samples: 4 });
  const pageVP = new THREE.Matrix4(), lensVP = new THREE.Matrix4();
  const env = studio(), [bumpStrip, bumpPill] = bumpMaps();
  const unit = new THREE.PlaneGeometry(1, 1);
  const premultiplied = { transparent: true, depthWrite: false, blending: THREE.CustomBlending, blendSrc: THREE.OneFactor, blendDst: THREE.OneMinusSrcAlphaFactor, blendSrcAlpha: THREE.OneFactor, blendDstAlpha: THREE.OneMinusSrcAlphaFactor };

  const on = (mesh, layer, order = 0) => { mesh.layers.set(layer); mesh.renderOrder = order; mesh.frustumCulled = false; scene.add(mesh); return mesh; };
  const backdrop = on(new THREE.Mesh(unit, new THREE.ShaderMaterial({
    uniforms: {
      c0: { value: new THREE.Color() }, c1: { value: new THREE.Color() }, c2: { value: new THREE.Color() }, mid: { value: 0.5 },
      uView: { value: new THREE.Vector2(1, 1) }, uGrid: { value: new THREE.Vector3(0, 0, 40) }, uLine: { value: new THREE.Vector4(0, 0, 0, 0) },
    },
    vertexShader: PLANE_VERT, fragmentShader: BACKDROP_FRAG, depthTest: false, depthWrite: false,
  })), PAGE, -10);
  const reel = on(new THREE.Mesh(unit, new THREE.ShaderMaterial({
    uniforms: { map: { value: null }, uSize: { value: new THREE.Vector2(1, 1) }, uRadius: { value: 20 }, opacity: { value: 1 } },
    vertexShader: PLANE_VERT, fragmentShader: REEL_FRAG, ...premultiplied,
  })), PAGE, 5);
  reel.visible = false;
  const planes = new Map();

  const glass = (back, vp, extra = {}, defines = {}) => new THREE.ShaderMaterial({
    uniforms: {
      uBack: { value: back.texture }, uBackVP: { value: vp }, uEnv: { value: env }, uStretch: { value: 0 },
      uIor: { value: 1.5 }, uThick: { value: 20 }, uDisp: { value: 0.08 }, uTint: { value: new THREE.Color(0.99, 0.995, 1) }, uEdge: { value: new THREE.Color(0.66, 0.74, 0.9) }, ...extra,
    },
    defines, vertexShader: GLASS_VERT, fragmentShader: GLASS_FRAG,
  });
  const lens = on(new THREE.Mesh(lensGeometry(), glass(lensTarget, lensVP)), LENS);
  const slab = on(new THREE.Mesh(new RoundedBoxGeometry(SLAB_ASPECT, 1, 0.4, 8, 0.2), glass(pageTarget, pageVP, {
    uBump: { value: bumpStrip }, uBumpPill: { value: bumpPill }, uBumpSize: { value: new THREE.Vector2(SLAB_ASPECT, 1) }, uPillSize: { value: new THREE.Vector2(PILL_ASPECT, 1) }, uPill: { value: 0 },
  }, { USE_BUMP: "" })), GLASS);
  const ring = on(new THREE.Mesh(new THREE.TorusGeometry(1, 0.23, 28, 112), glass(pageTarget, pageVP)), GLASS);
  const arrow = on(new THREE.Mesh(arrowGeometry(), glass(pageTarget, pageVP)), GLASS);

  // Soft shadows on the page, each with the brighter patch where the glass gathers light.
  const shadowOf = (texture) => on(new THREE.Mesh(unit, new THREE.ShaderMaterial({ uniforms: { map: { value: texture }, opacity: { value: 1 } }, vertexShader: PLANE_VERT, fragmentShader: PLANE_FRAG, ...premultiplied })), SHADE, 20);
  const disc = (g, x, y, r) => { g.beginPath(); g.arc(x, y, r, 0, Math.PI * 2); };
  // Glass throws a pale shadow with bright lines in it, where it gathers the light.
  const shadows = {
    lens: shadowOf(softSprite(256, 256, 26, (g, soft) => {
      soft("#15161a38", 22, () => { disc(g, 128, 128, 84); g.fill(); });
      soft("#fffaf2e6", 16, () => { disc(g, 118, 114, 40); g.fill(); });
    })),
    slab: shadowOf(softSprite(1024, 160, 22, (g, soft) => {
      soft("#15161a33", 20, () => { g.beginPath(); g.roundRect(60, 50, 904, 60, 18); g.fill(); });
      soft("#fffaf2e6", 6, () => { g.beginPath(); g.roundRect(90, 100, 844, 5, 2); g.fill(); });
    })),
    ring: shadowOf(softSprite(256, 256, 14, (g, soft) => {
      soft("#15161a33", 12, () => { g.lineWidth = 34; disc(g, 128, 128, 82); g.stroke(); });
      soft("#fffaf2f2", 5, () => { g.lineWidth = 6; g.beginPath(); g.arc(128, 128, 82, 0.3, 2.5); g.stroke(); });
    })),
    arrow: shadowOf(softSprite(256, 256, 14, (g, soft) => {
      soft("#15161a33", 12, () => { g.beginPath(); g.moveTo(22, 108); g.lineTo(140, 108); g.lineTo(140, 66); g.lineTo(232, 128); g.lineTo(140, 190); g.lineTo(140, 148); g.lineTo(22, 148); g.closePath(); g.fill(); });
      soft("#fffaf2f2", 4, () => { g.lineWidth = 5; g.beginPath(); g.moveTo(34, 142); g.lineTo(146, 142); g.lineTo(146, 176); g.lineTo(214, 132); g.stroke(); });
    })),
  };
  shadows.lens.layers.set(LENS_SHADE);

  function resize(width, height, pixelRatio) {
    vw = width; vh = height; ratio = pixelRatio;
    renderer.setPixelRatio(ratio);
    renderer.setSize(vw, vh, false);
    distance = vh / 2 / Math.tan(THREE.MathUtils.degToRad(FOV / 2));
    for (const c of [camera, lensCamera]) { c.aspect = vw / vh; c.near = distance * 0.2; c.far = distance * 2.5; c.position.set(0, 0, distance); c.updateMatrixWorld(); }
    camera.updateProjectionMatrix();
    pageTarget.setSize(Math.round(vw * ratio), Math.round(vh * ratio));
    backdrop.scale.set(vw, vh, 1);
    backdrop.material.uniforms.uView.value.set(vw, vh);
  }
  const at = (mesh, x, y, z, size) => {
    const k = (distance - z) / distance;
    mesh.position.set((x - vw / 2) * k, (vh / 2 - y) * k, z);
    if (size !== undefined) mesh.scale.setScalar(size * k);
    return k;
  };
  const flat = (mesh, x, y, w, h) => { mesh.position.set(x + w / 2 - vw / 2, vh / 2 - y - h / 2, 0); mesh.scale.set(w, h, 1); };
  const shade = (mesh, x, y, w, h, turn, alpha) => {
    mesh.visible = alpha > 0.01;
    mesh.position.set(x - vw / 2, vh / 2 - y, 0); mesh.scale.set(w, h, 1); mesh.rotation.z = turn; mesh.material.uniforms.opacity.value = alpha;
  };

  return {
    resize,
    get ratio() { return ratio; },
    setBackdrop(top, middle, bottom, mid) {
      const u = backdrop.material.uniforms;
      u.c0.value.set(top); u.c1.value.set(middle); u.c2.value.set(bottom); u.mid.value = mid;
    },
    // The ruled lines of the paper: where they start, how far apart, their color and strength.
    setGrid(x, y, step, color, alpha) {
      const u = backdrop.material.uniforms, c = new THREE.Color(color);
      u.uGrid.value.set(x, y, step); u.uLine.value.set(c.r, c.g, c.b, alpha);
    },
    // A painted copy of a piece of the page (see copy.js).
    setPlane(id, source) {
      let plane = planes.get(id);
      if (!plane) {
        const texture = new THREE.CanvasTexture(source);
        texture.premultiplyAlpha = true; texture.anisotropy = 4;
        plane = on(new THREE.Mesh(unit, new THREE.ShaderMaterial({ uniforms: { map: { value: texture }, opacity: { value: 1 } }, vertexShader: PLANE_VERT, fragmentShader: PLANE_FRAG, ...premultiplied })), PAGE, planes.size);
        planes.set(id, plane);
      } else {
        const texture = plane.material.uniforms.map.value;
        // A canvas that changed size needs new storage on the graphics card.
        if (texture.userData.w !== source.width || texture.userData.h !== source.height) texture.dispose();
        texture.image = source; texture.needsUpdate = true;
      }
      const texture = plane.material.uniforms.map.value;
      texture.userData.w = source.width; texture.userData.h = source.height;
    },
    setReel(video, posterUrl) {
      const poster = new THREE.TextureLoader().load(posterUrl);
      const film = new THREE.VideoTexture(video);
      reel.userData = { video, poster, film };
      reel.material.uniforms.map.value = poster;
    },
    setPoster(url) { reel.userData.poster?.dispose(); reel.userData.poster = new THREE.TextureLoader().load(url); },
    frame(s) {
      for (const [id, plane] of planes) {
        const p = s.planes[id];
        plane.visible = Boolean(p) && p.o > 0.004;
        if (plane.visible) { flat(plane, p.x, p.y, p.w, p.h); plane.material.uniforms.opacity.value = p.o; }
      }
      reel.visible = Boolean(reel.userData.video) && s.reel.o > 0.004;
      if (reel.visible) {
        const u = reel.material.uniforms, { video, film, poster } = reel.userData;
        flat(reel, s.reel.x, s.reel.y, s.reel.w, s.reel.h);
        u.uSize.value.set(s.reel.w, s.reel.h); u.uRadius.value = s.reel.r; u.opacity.value = s.reel.o;
        u.map.value = video.readyState >= 2 && (video.currentTime > 0 || !video.paused) ? film : poster;
      }

      let k = at(lens, s.lens.x, s.lens.y, 170, s.lens.r);
      lens.rotation.set(s.lens.rx, s.lens.ry, 0);
      lens.material.uniforms.uThick.value = s.lens.r * k * LENS_CURVE * s.lens.power;
      lens.material.uniforms.uDisp.value = 0.075;
      shade(shadows.lens, s.lens.x + s.lens.r * 0.16, s.lens.y + s.lens.r * 0.36, s.lens.r * 3, s.lens.r * 3, 0, 1);

      k = at(slab, s.slab.x, s.slab.y, s.slab.z, s.slab.h);
      slab.rotation.set(s.slab.rx, s.slab.ry, s.slab.rz);
      const u = slab.material.uniforms;
      u.uStretch.value = (s.slab.w / s.slab.h - SLAB_ASPECT) / 2; u.uPill.value = s.slab.pill; u.uThick.value = s.slab.h * 0.52; u.uDisp.value = 0.1;
      shade(shadows.slab, s.slab.x + s.slab.h * 0.1, s.slab.y + s.slab.h * (0.32 + s.slab.z * 0.004), s.slab.w * 1.13, s.slab.h * 2.67, s.slab.rz, 1);

      at(ring, s.ring.x, s.ring.y, s.ring.z, s.ring.r);
      ring.rotation.set(s.ring.rx, s.ring.ry, 0);
      ring.material.uniforms.uThick.value = s.ring.r * 0.34; ring.material.uniforms.uDisp.value = 0.11;
      shade(shadows.ring, s.ring.x + s.ring.r * 0.14, s.ring.y + s.ring.r * 0.4, s.ring.r * 3.12 * Math.abs(Math.cos(s.ring.ry)), s.ring.r * 3.12 * Math.abs(Math.cos(s.ring.rx)), 0, s.ring.r > 1 ? 1 : 0);

      at(arrow, s.arrow.x, s.arrow.y, s.arrow.z, s.arrow.size);
      arrow.rotation.set(s.arrow.rx, s.arrow.ry, s.arrow.rz);
      arrow.material.uniforms.uThick.value = s.arrow.size * 0.15; arrow.material.uniforms.uDisp.value = 0.11;
      shade(shadows.arrow, s.arrow.x + s.arrow.size * 0.05, s.arrow.y + s.arrow.size * 0.16, s.arrow.size * 1.4 * Math.abs(Math.cos(s.arrow.ry)), s.arrow.size * 1.4 * Math.abs(Math.cos(s.arrow.rx)), s.arrow.rz, s.arrow.size > 1 ? 1 : 0);
      ring.visible = s.ring.r > 1; arrow.visible = s.arrow.size > 1;

      scene.updateMatrixWorld();
      pageVP.multiplyMatrices(camera.projectionMatrix, camera.matrixWorldInverse);
      // The page as the glass sees it. An object's own shadow lies under it, so shadows stay out of this picture.
      camera.layers.set(PAGE);
      renderer.setRenderTarget(pageTarget);
      renderer.render(scene, camera);

      // The lens looks at a small square around itself, at several times the page's sharpness.
      const side = s.lens.r * 2.5, size = Math.min(2048, Math.ceil((side * ratio * 1.9) / 64) * 64);
      if (lensTarget.width !== size) lensTarget.setSize(size, size);
      lensCamera.setViewOffset(vw, vh, s.lens.x - side / 2, s.lens.y - side / 2, side, side);
      lensCamera.updateProjectionMatrix();
      lensVP.multiplyMatrices(lensCamera.projectionMatrix, lensCamera.matrixWorldInverse);
      lensCamera.layers.set(PAGE); lensCamera.layers.enable(SHADE); lensCamera.layers.enable(GLASS);
      renderer.setRenderTarget(lensTarget);
      renderer.render(scene, lensCamera);

      camera.layers.set(SHADE); camera.layers.enable(LENS_SHADE); camera.layers.enable(GLASS); camera.layers.enable(LENS);
      renderer.setRenderTarget(null);
      renderer.render(scene, camera);
    },
    dispose() { renderer.dispose(); pageTarget.dispose(); lensTarget.dispose(); },
  };
}
