// The room: a dark stage, one big screen that plays the recordings, a glossy floor, and the tools as real light.
// Everything here is drawn in display colors and written to the canvas as is, so the video is never tone-mapped
// or converted: what the recording contains is what the screen shows.
import * as THREE from "three";

const BG = [7 / 255, 9 / 255, 13 / 255];
const FOV = 34;
const rad = (deg) => (deg * Math.PI) / 180;
const lerp = (a, b, t) => a + (b - a) * t;

export function webglAvailable() {
  try { return Boolean(window.WebGL2RenderingContext && document.createElement("canvas").getContext("webgl2")); } catch { return false; }
}

const HELPERS = /* glsl */ `
float hash(vec2 p) { vec3 q = fract(vec3(p.xyx) * 0.1031); q += dot(q, q.yzx + 33.33); return fract((q.x + q.y) * q.z); }
float noise(vec2 p) {
  vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
  return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}
float fbm(vec2 p) { return noise(p) * 0.55 + noise(p * 2.03 + 7.1) * 0.3 + noise(p * 4.1 + 3.3) * 0.15; }
float sdBox(vec2 p, vec2 b, float r) { vec2 q = abs(p) - b + r; return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r; }
`;

// What the screen shows at a point: the clip (or two, while they cross-fade), the spotlight's dimming and the ink.
// The floor reads the same picture at a blurrier level, so the reflection always matches the screen.
const picture = (name, read) => /* glsl */ `
vec3 ${name}Clip(sampler2D t, vec2 uv, vec2 fit, float lod) {
  vec2 p = (uv - 0.5) * fit + 0.5;
  vec2 inside = step(vec2(0.0), p) * step(p, vec2(1.0));
  return ${read("t", "p")}.rgb * inside.x * inside.y;
}
vec3 ${name}(vec2 uv, float lod) {
  vec3 c = uMix < 0.001 ? ${name}Clip(tA, uv, uFitA, lod) : uMix > 0.999 ? ${name}Clip(tB, uv, uFitB, lod) : mix(${name}Clip(tA, uv, uFitA, lod), ${name}Clip(tB, uv, uFitB, lod), uMix);
  float d = distance(uv * vec2(uAspect, 1.0), uSpot.xy);
  float lit = 1.0 - smoothstep(uSpot.z * 0.84, uSpot.z * 1.12, d);
  c *= mix(1.0, 0.1 + 0.9 * lit, uSpotOn) * uDim;
  c += vec3(1.0, 0.9, 0.76) * lit * uSpotOn * 0.035;
  if (uInkOn > 0.5) { vec4 ink = ${read("tInk", "uv")}; c = c * (1.0 - ink.a) + ink.rgb; }
  return c;
}
`;
const PICTURE_UNIFORMS = /* glsl */ `
uniform sampler2D tA, tB, tInk;
uniform float uMix, uDim, uSpotOn, uAspect, uInkOn;
uniform vec2 uFitA, uFitB;
uniform vec3 uSpot;
`;

export function createStage(canvas, { reduced = false } = {}) {
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: false, powerPreference: "high-performance" });
  renderer.setPixelRatio(Math.min(devicePixelRatio || 1, 2));
  renderer.setClearColor(new THREE.Color().setRGB(...BG, THREE.SRGBColorSpace), 1);
  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(FOV, 1.6, 0.1, 160);
  const anisotropy = renderer.capabilities.getMaxAnisotropy();

  // Textures are kept as the browser decodes them (no color conversion on upload), see the note at the top.
  const raw = (texture, mips = true) => {
    texture.colorSpace = THREE.NoColorSpace;
    texture.generateMipmaps = mips;
    texture.minFilter = mips ? THREE.LinearMipmapLinearFilter : THREE.LinearFilter;
    texture.magFilter = THREE.LinearFilter;
    texture.anisotropy = anisotropy;
    return texture;
  };
  const black = raw(new THREE.DataTexture(new Uint8Array([0, 0, 0, 255]), 1, 1), false);
  black.needsUpdate = true;
  const clear = raw(new THREE.DataTexture(new Uint8Array([0, 0, 0, 0]), 1, 1), false);
  clear.needsUpdate = true;

  const panel = { w: 8, h: 5, cy: 3.45, bezel: 0.075 };
  const shared = {
    tA: { value: black }, tB: { value: black }, tInk: { value: clear },
    uMix: { value: 0 }, uDim: { value: 1 }, uSpotOn: { value: 0 }, uAspect: { value: 1.6 }, uInkOn: { value: 0 },
    uFitA: { value: new THREE.Vector2(1, 1) }, uFitB: { value: new THREE.Vector2(1, 1) },
    uSpot: { value: new THREE.Vector3(0.8, 0.5, 0.3) },
    uScreenC: { value: new THREE.Vector2(0, panel.cy) }, uScreenS: { value: new THREE.Vector2(panel.w, panel.h) },
    uOuter: { value: new THREE.Vector2(panel.w + panel.bezel * 2, panel.h + panel.bezel * 2) },
    uGlow: { value: new THREE.Vector3(0.5, 0.56, 0.7) }, uLight: { value: 1 }, uTime: { value: 0 },
    uBg: { value: new THREE.Vector3(...BG) }, uHaze: { value: new THREE.Vector3(0.03, 0.036, 0.05) }, uTexW: { value: 1728 },
  };
  const plane = new THREE.PlaneGeometry(1, 1);
  const PLANE_VERTEX = /* glsl */ `
    varying vec2 vP; uniform vec2 uPlane;
    void main() { vP = (uv - 0.5) * uPlane; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`;

  // Floor: polished and dark. Each point mirrors the screen, blurrier the further the mirrored ray travels.
  const floor = new THREE.Mesh(new THREE.PlaneGeometry(140, 140), new THREE.ShaderMaterial({
    uniforms: {
      ...shared,
      uPool: { value: [new THREE.Vector4(-7.4, 3.2, 3, 0.085), new THREE.Vector4(7.2, -2.6, 3.5, 0.06)] },
      uPoolColor: { value: [new THREE.Vector3(1, 0.71, 0.55), new THREE.Vector3(0.66, 0.76, 1)] },
    },
    vertexShader: /* glsl */ `
      varying vec3 vWorld;
      void main() { vec4 w = modelMatrix * vec4(position, 1.0); vWorld = w.xyz; gl_Position = projectionMatrix * viewMatrix * w; }`,
    fragmentShader: /* glsl */ `
      #define TAPS 9
      varying vec3 vWorld;
      uniform vec2 uScreenC, uScreenS, uOuter;
      uniform vec3 uGlow, uBg, uHaze;
      uniform float uLight, uTexW;
      uniform vec4 uPool[2];
      uniform vec3 uPoolColor[2];
      ${PICTURE_UNIFORMS}
      ${HELPERS}
      ${picture("mirrored", (t, p) => `textureLod(${t}, ${p}, lod)`)}
      void main() {
        vec3 ray = vWorld - cameraPosition;
        float dist = length(ray);
        vec3 V = ray / dist;
        float n = fbm(vWorld.xz * 0.42);
        float grain = noise(vWorld.xz * 7.0);
        vec3 col = vec3(0.018, 0.021, 0.028) * (0.7 + 0.6 * n);

        // Light the screen throws on the floor in front of it.
        vec2 q = vec2((vWorld.x - uScreenC.x) / (uScreenS.x * 0.7), (vWorld.z - 0.6) / 4.6);
        col += uGlow * uLight * 0.075 * exp(-dot(q, q) * 1.5) * smoothstep(-1.2, 1.4, vWorld.z);
        for (int i = 0; i < 2; i++) {
          vec2 d = (vWorld.xz - uPool[i].xy) / uPool[i].z;
          col += uPoolColor[i] * uPool[i].w * exp(-dot(d, d) * 2.2) * (0.75 + 0.5 * n);
        }

        vec3 R = reflect(V, vec3(0.0, 1.0, 0.0));
        if (R.z < -0.0005 && vWorld.z > 0.0) {
          float t = -vWorld.z / R.z;
          vec3 hit = vWorld + R * t;
          vec2 uv = (hit.xy - uScreenC) / uScreenS + 0.5;
          // A polished floor seen at a low angle smears what it mirrors downwards, more with distance.
          float rough = (0.012 + 0.016 * n) * t;
          vec2 r = vec2(rough / uScreenS.x, rough * 3.4 / uScreenS.y);
          float lod = log2(max(r.x * uTexW * 1.7, 1.0));
          float start = hash(gl_FragCoord.xy);
          vec3 sum = vec3(0.0);
          for (int i = 0; i < TAPS; i++) {
            float k = (float(i) + start) / float(TAPS) * 2.0 - 1.0;
            vec2 p = uv + vec2((hash(gl_FragCoord.yx + float(i)) - 0.5) * r.x, k * r.y);
            vec2 inside = smoothstep(vec2(0.0), r * 0.5 + 0.002, p) * smoothstep(vec2(0.0), r * 0.5 + 0.002, 1.0 - p);
            sum += mirrored(p, lod) * inside.x * inside.y * (1.0 - 0.5 * k * k);
          }
          sum /= float(TAPS) * 0.83;
          float away = max(sdBox((uv - 0.5) * uScreenS, uOuter * 0.5, 0.17), 0.0);
          sum += uGlow * uLight * (0.16 * exp(-away * 4.0) + 0.06 * exp(-away * 0.9));
          float fresnel = 0.05 + 0.95 * pow(1.0 - max(-V.y, 0.0), 3.4);
          col += sum * fresnel * 0.9 * exp(-t * 0.075) * (0.9 + 0.2 * grain);
        }
        // Far away the floor melts into the haze at the horizon.
        col = mix(col, uBg + uHaze, smoothstep(9.0, 44.0, dist));
        col += (hash(gl_FragCoord.xy + 3.7) - 0.5) / 255.0;
        gl_FragColor = vec4(col, 1.0);
      }`,
  }));
  floor.rotation.x = -Math.PI / 2;
  floor.renderOrder = 0;
  scene.add(floor);

  // The far wall is lost in haze: a faint band of light at the horizon, so the room has an end.
  const backdrop = new THREE.Mesh(new THREE.SphereGeometry(120, 32, 16), new THREE.ShaderMaterial({
    uniforms: shared, side: THREE.BackSide, depthWrite: false, depthTest: false,
    vertexShader: /* glsl */ `
      varying vec3 vDir;
      void main() { vec4 w = modelMatrix * vec4(position, 1.0); vDir = w.xyz - cameraPosition; gl_Position = projectionMatrix * viewMatrix * w; }`,
    fragmentShader: /* glsl */ `
      varying vec3 vDir;
      uniform vec3 uBg, uHaze;
      ${HELPERS}
      void main() {
        float up = max(normalize(vDir).y, 0.0);
        vec3 col = uBg + uHaze * (exp(-up * 9.0) + 0.25 * exp(-up * 2.2));
        gl_FragColor = vec4(col + (hash(gl_FragCoord.xy + 9.1) - 0.5) / 255.0, 1.0);
      }`,
  }));
  backdrop.renderOrder = -1;
  backdrop.frustumCulled = false;
  scene.add(backdrop);

  // Glow in the haze around the screen.
  const halo = new THREE.Mesh(plane, new THREE.ShaderMaterial({
    uniforms: { ...shared, uPlane: { value: new THREE.Vector2(1, 1) } },
    transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
    vertexShader: PLANE_VERTEX,
    fragmentShader: /* glsl */ `
      varying vec2 vP;
      uniform vec2 uOuter; uniform vec3 uGlow; uniform float uLight;
      ${HELPERS}
      void main() {
        float d = max(sdBox(vP, uOuter * 0.5, 0.17), 0.0);
        vec3 col = uGlow * uLight * (0.3 * exp(-d * 5.5) + 0.13 * exp(-d * 1.15) + 0.05 * exp(-d * 0.3));
        col *= 0.9 + 0.2 * fbm(vP * 0.35);
        col += (hash(gl_FragCoord.xy) - 0.5) / 255.0;
        gl_FragColor = vec4(col, 1.0);
      }`,
  }));
  halo.position.z = -0.3;
  halo.renderOrder = 1;
  scene.add(halo);

  const screen = new THREE.Mesh(plane, new THREE.ShaderMaterial({
    uniforms: { ...shared, uPlane: shared.uOuter },
    transparent: true,
    vertexShader: PLANE_VERTEX,
    fragmentShader: /* glsl */ `
      varying vec2 vP;
      uniform vec2 uOuter, uScreenS; uniform vec3 uGlow; uniform float uLight;
      ${PICTURE_UNIFORMS}
      ${HELPERS}
      ${picture("shown", (t, p) => `texture2D(${t}, ${p})`)}
      void main() {
        float edge = sdBox(vP, uOuter * 0.5, 0.17), soft = fwidth(edge);
        float alpha = 1.0 - smoothstep(-soft, soft, edge);
        if (alpha < 0.003) discard;
        float inner = sdBox(vP, uScreenS * 0.5, 0.1), softInner = fwidth(inner);
        vec3 img = shown(vP / uScreenS + 0.5, 0.0);
        // A thin dark frame whose outer edge catches the light of the picture.
        float rim = smoothstep(-0.034, -0.006, edge);
        vec3 frame = vec3(0.022, 0.025, 0.032) + (uGlow * 0.55 + 0.05) * uLight * rim * (0.3 + 0.7 * smoothstep(-0.5, 0.5, vP.y / uOuter.y));
        gl_FragColor = vec4(mix(img, frame, smoothstep(-softInner, softInner, inner)), alpha);
      }`,
  }));
  screen.renderOrder = 2;
  scene.add(screen);

  // The screen hangs from two wires.
  const wires = [-1, 1].map((side) => {
    const wire = new THREE.Mesh(new THREE.BoxGeometry(0.014, 40, 0.014), new THREE.MeshBasicMaterial({ color: 0x1b1f27 }));
    wire.userData.side = side;
    wire.renderOrder = 1;
    scene.add(wire);
    return wire;
  });

  // Light in haze: a cone that is brightest where you look through its middle and fades to nothing at its edge.
  const coneGeometry = (() => {
    const seg = 72, angle = [], along = [], index = [];
    for (let i = 0; i <= seg; i++) for (let j = 0; j < 2; j++) { angle.push((i / seg) * Math.PI * 2); along.push(j); }
    for (let i = 0; i < seg; i++) { const a = i * 2; index.push(a, a + 1, a + 2, a + 1, a + 3, a + 2); }
    const g = new THREE.BufferGeometry();
    g.setAttribute("position", new THREE.Float32BufferAttribute(new Float32Array(angle.length * 3), 3));
    g.setAttribute("aAngle", new THREE.Float32BufferAttribute(angle, 1));
    g.setAttribute("aT", new THREE.Float32BufferAttribute(along, 1));
    g.setIndex(index);
    return g;
  })();
  function beam({ color, intensity, soft = 2.2, r0 = 0.1, r1 = 1, onFloor = false }) {
    const mesh = new THREE.Mesh(coneGeometry, new THREE.ShaderMaterial({
      uniforms: {
        uApex: { value: new THREE.Vector3() }, uBase: { value: new THREE.Vector3() },
        uU: { value: new THREE.Vector3(1, 0, 0) }, uV: { value: onFloor ? new THREE.Vector3(0, 0, 1) : new THREE.Vector3(0, 1, 0) },
        uR0: { value: r0 }, uR1: { value: r1 }, uColor: { value: new THREE.Vector3(...color) }, uIntensity: { value: intensity },
        uSoft: { value: soft }, uTime: shared.uTime,
      },
      transparent: true, depthWrite: false, side: THREE.DoubleSide, blending: THREE.AdditiveBlending,
      vertexShader: /* glsl */ `
        attribute float aAngle, aT;
        uniform vec3 uApex, uBase, uU, uV; uniform float uR0, uR1;
        varying float vT; varying vec3 vN, vView, vW;
        void main() {
          vec3 axis = normalize(uBase - uApex);
          vec3 out1 = cos(aAngle) * uU + sin(aAngle) * uV;
          vec3 out0 = normalize(out1 - axis * dot(out1, axis));
          vec3 p = mix(uApex + out0 * uR0, uBase + out1 * uR1, aT);
          vT = aT; vN = out0; vW = p; vView = cameraPosition - p;
          gl_Position = projectionMatrix * viewMatrix * vec4(p, 1.0);
        }`,
      fragmentShader: /* glsl */ `
        uniform vec3 uColor; uniform float uIntensity, uSoft, uTime;
        varying float vT; varying vec3 vN, vView, vW;
        ${HELPERS}
        void main() {
          float through = pow(abs(dot(normalize(vN), normalize(vView))), uSoft);
          float along = mix(1.0, 0.5, vT) * smoothstep(0.0, 0.04, vT);
          float haze = 0.74 + 0.5 * fbm(vW.xy * 0.5 + vec2(uTime * 0.03, -uTime * 0.018) + vW.z * 0.27);
          gl_FragColor = vec4(uColor * through * along * haze * uIntensity + (hash(gl_FragCoord.xy) - 0.5) / 255.0, 1.0);
        }`,
    }));
    mesh.frustumCulled = false;
    mesh.renderOrder = 3;
    scene.add(mesh);
    return mesh;
  }
  // Two faint wash lights give the room air; the third is the Spotlight tool.
  const washes = [
    { mesh: beam({ color: [1, 0.71, 0.55], intensity: 0.17, soft: 1.6, r0: 0.3, r1: 2.7, onFloor: true }), apex: [-12.5, 17, 1], base: [-7.4, 0, 3.2] },
    { mesh: beam({ color: [0.66, 0.76, 1], intensity: 0.13, soft: 1.6, r0: 0.3, r1: 3.2, onFloor: true }), apex: [11.5, 17, -9], base: [7.2, 0, -2.6] },
  ];
  const spot = beam({ color: [1, 0.92, 0.8], intensity: 0, soft: 2.4, r0: 0.07, r1: 1 });
  spot.renderOrder = 4;

  // The laser: a thin ribbon that always faces the camera, from just outside the frame to its dot on the screen.
  const laser = new THREE.Mesh((() => {
    const g = new THREE.BufferGeometry();
    g.setAttribute("position", new THREE.Float32BufferAttribute(new Float32Array(12), 3));
    g.setAttribute("aEnd", new THREE.Float32BufferAttribute([0, 0, 1, 1], 1));
    g.setAttribute("aSide", new THREE.Float32BufferAttribute([-1, 1, -1, 1], 1));
    g.setIndex([0, 1, 2, 1, 3, 2]);
    return g;
  })(), new THREE.ShaderMaterial({
    uniforms: { uFrom: { value: new THREE.Vector3() }, uTo: { value: new THREE.Vector3() }, uRes: { value: new THREE.Vector2(1, 1) }, uIntensity: { value: 0 }, uTime: shared.uTime },
    transparent: true, depthWrite: false, depthTest: false, side: THREE.DoubleSide, blending: THREE.AdditiveBlending,
    vertexShader: /* glsl */ `
      attribute float aEnd, aSide;
      uniform vec3 uFrom, uTo; uniform vec2 uRes;
      varying float vEnd, vSide;
      void main() {
        vec4 a = projectionMatrix * viewMatrix * vec4(uFrom, 1.0), b = projectionMatrix * viewMatrix * vec4(uTo, 1.0);
        vec2 dir = normalize((b.xy / b.w - a.xy / a.w) * uRes);
        vec4 c = mix(a, b, aEnd);
        c.xy += vec2(-dir.y, dir.x) * aSide * mix(26.0, 9.0, aEnd) / uRes * c.w;
        vEnd = aEnd; vSide = aSide;
        gl_Position = c;
      }`,
    fragmentShader: /* glsl */ `
      uniform float uIntensity, uTime;
      varying float vEnd, vSide;
      ${HELPERS}
      void main() {
        float core = exp(-vSide * vSide * 46.0), glow = exp(-vSide * vSide * 5.0);
        float dust = 0.7 + 0.6 * noise(vec2(vEnd * 46.0 - uTime * 0.6, 0.5));
        float along = smoothstep(0.0, 0.3, vEnd) * mix(0.55, 1.0, vEnd);
        vec3 col = (vec3(1.0, 0.45, 0.42) * core * 0.9 + vec3(1.0, 0.09, 0.13) * glow * 0.3) * along * dust * uIntensity;
        gl_FragColor = vec4(col, 1.0);
      }`,
  }));
  laser.frustumCulled = false;
  laser.renderOrder = 6;
  scene.add(laser);

  // Slow dust that only shows where it drifts through light.
  const motes = (() => {
    const count = 150, position = new Float32Array(count * 3), seed = new Float32Array(count);
    let s = 7;
    const random = () => { s = (s * 16807) % 2147483647; return s / 2147483647; };
    for (let i = 0; i < count; i++) { position.set([(random() - 0.5) * 22, 0.4 + random() * 8.5, -3 + random() * 11], i * 3); seed[i] = random(); }
    const g = new THREE.BufferGeometry();
    g.setAttribute("position", new THREE.Float32BufferAttribute(position, 3));
    g.setAttribute("aSeed", new THREE.Float32BufferAttribute(seed, 1));
    const points = new THREE.Points(g, new THREE.ShaderMaterial({
      uniforms: { uTime: shared.uTime, uGlow: shared.uGlow, uLight: shared.uLight, uScale: { value: 1 } },
      transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
      vertexShader: /* glsl */ `
        attribute float aSeed; uniform float uTime, uScale; varying float vA;
        void main() {
          vec3 p = position;
          p.x += sin(uTime * 0.05 + aSeed * 40.0) * 0.6; p.y += sin(uTime * 0.04 + aSeed * 17.0) * 0.4 + uTime * 0.004 * (aSeed - 0.5);
          vec4 mv = viewMatrix * vec4(p, 1.0);
          gl_Position = projectionMatrix * mv;
          gl_PointSize = uScale * (1.4 + aSeed * 2.4) * 9.0 / -mv.z;
          vA = (0.25 + 0.75 * aSeed) * smoothstep(1.5, 5.0, -mv.z) * (0.6 + 0.4 * sin(uTime * 0.3 + aSeed * 90.0));
        }`,
      fragmentShader: /* glsl */ `
        uniform vec3 uGlow; uniform float uLight; varying float vA;
        void main() { float d = length(gl_PointCoord - 0.5); gl_FragColor = vec4((uGlow * 0.5 + 0.25) * (0.35 + 0.65 * uLight) * vA * 0.28 * smoothstep(0.5, 0.0, d), 1.0); }`,
    }));
    points.frustumCulled = false;
    points.renderOrder = 5;
    scene.add(points);
    return points;
  })();

  // The panel is 16:10 (16:9 while the reel plays), or 4:5 on a phone where the upright recordings are used.
  let panelAspect = 0;
  function setPanel(aspect) {
    if (aspect === panelAspect) return;
    panelAspect = aspect;
    panel.w = aspect >= 1 ? 8 : 4.6;
    panel.h = panel.w / aspect;
    panel.cy = panel.h / 2 + 0.42;
    shared.uAspect.value = aspect;
    shared.uScreenC.value.set(0, panel.cy);
    shared.uScreenS.value.set(panel.w, panel.h);
    shared.uOuter.value.set(panel.w + panel.bezel * 2, panel.h + panel.bezel * 2);
    screen.scale.set(shared.uOuter.value.x, shared.uOuter.value.y, 1);
    screen.position.set(0, panel.cy, 0);
    halo.scale.set(panel.w + 16, panel.h + 16, 1);
    halo.material.uniforms.uPlane.value.set(panel.w + 16, panel.h + 16);
    halo.position.set(0, panel.cy, -0.3);
    wires.forEach((wire) => wire.position.set(wire.userData.side * (panel.w / 2 - 0.5), panel.cy + panel.h / 2 + 20, -0.06));
    washes.forEach(({ mesh, apex, base }) => { mesh.material.uniforms.uApex.value.set(...apex); mesh.material.uniforms.uBase.value.set(...base); });
  }
  setPanel(1.6);

  // Clips: the poster picture first, the live video once it has a frame.
  const textures = new Map();
  function textureOf(clip) {
    if (!clip) return black;
    let entry = textures.get(clip.key);
    if (!entry) { entry = { poster: null, video: null }; textures.set(clip.key, entry); }
    if (clip.live && clip.video.readyState >= 2) {
      if (!entry.video) { entry.video = raw(new THREE.VideoTexture(clip.video)); entry.video.needsUpdate = true; }
      return entry.video;
    }
    if (clip.image.complete && clip.image.naturalWidth) {
      if (!entry.poster) { entry.poster = raw(new THREE.Texture(clip.image)); entry.poster.needsUpdate = true; }
      return entry.poster;
    }
    return black;
  }
  const fit = (clip, target) => {
    const a = clip ? clip.aspect : shared.uAspect.value, p = shared.uAspect.value, close = Math.abs(a - p) < 0.02;
    target.set(close || a > p ? 1 : p / a, close || a < p ? 1 : a / p);
  };
  let inkTexture = null, inkVersion = -1;

  // Camera -----------------------------------------------------------------------------------------
  // Each shot looks at a part of the screen (u0, v0, u1, v1; v from the top) from an angle. The distance and the
  // lens shift are solved so that part fills the free region of the page beside the text.
  const FULL = [0, 0, 1, 1];
  const SHOTS = [
    { az: -15, el: -5.5, fill: 1 },
    // From the back row: far away, the screen small in the frame.
    { az: 5, el: -3, fill: 0.6 },
    { az: -11, el: -4.5, fill: 1 },
    { az: 12, el: -9.5, fill: 1.03 },
    { az: -23, el: -3, fill: 1 },
    { az: -8, el: -2, fill: 1, zoom: { side: [0, 0.16, 0.52, 0.86], stack: [0.18, 0.12, 0.82, 0.8] } },
  ];
  const target = new THREE.Vector3(), dir = new THREE.Vector3(), probe = new THREE.Vector3(), level = new THREE.Vector3();
  const size = { w: 1, h: 1 }, box = { left: 0, top: 0, right: 1, bottom: 1 };
  let distance = 16;
  const px = (u) => (u - 0.5) * panel.w, py = (v) => panel.cy + (0.5 - v) * panel.h;
  function measure(rect) {
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    for (const [u, v] of [[rect[0], rect[1]], [rect[2], rect[1]], [rect[0], rect[3]], [rect[2], rect[3]]]) {
      probe.set(px(u), py(v), 0).project(camera);
      minX = Math.min(minX, probe.x); maxX = Math.max(maxX, probe.x); minY = Math.min(minY, probe.y); maxY = Math.max(maxY, probe.y);
    }
    return { left: ((minX + 1) / 2) * size.w, right: ((maxX + 1) / 2) * size.w, top: ((1 - maxY) / 2) * size.h, bottom: ((1 - minY) / 2) * size.h };
  }
  function aim(state) {
    const f = Math.min(SHOTS.length - 1, Math.max(0, state.focus)), i = Math.min(SHOTS.length - 2, Math.floor(f)), k = f - i;
    const turn = state.stacked ? 0.55 : 1;
    const pose = (n) => {
      const shot = SHOTS[n], hold = state.hold[n] || 0;
      let rect = FULL;
      if (shot.zoom) { const z = shot.zoom[state.stacked ? "stack" : "side"], amount = state.zoom; rect = FULL.map((value, j) => lerp(value, z[j], amount)); }
      return { az: shot.az * turn + (hold - 0.5) * 3.5 * Math.sign(shot.az), el: shot.el, fill: shot.fill + hold * 0.025, rect };
    };
    const a = pose(i), b = pose(i + 1);
    const rect = a.rect.map((value, j) => lerp(value, b.rect[j], k));
    const az = rad(lerp(a.az, b.az, k) + state.parallax.x * 2.2), el = rad(lerp(a.el, b.el, k) - state.parallax.y * 1.3), fill = lerp(a.fill, b.fill, k);
    target.set((px(rect[0]) + px(rect[2])) / 2, (py(rect[1]) + py(rect[3])) / 2, 0);
    dir.set(Math.sin(az) * Math.cos(el), Math.sin(el), Math.cos(az) * Math.cos(el));
    camera.clearViewOffset();
    let framed = box;
    for (let pass = 0; pass < 3; pass++) {
      camera.position.copy(target).addScaledVector(dir, distance);
      if (camera.position.y < 0.5) camera.position.y = 0.5;
      // The camera stays level and the lens shifts instead, so the screen's sides stay upright as in a film still.
      camera.lookAt(level.set(target.x, camera.position.y, target.z));
      camera.updateMatrixWorld();
      framed = measure(rect);
      if (pass < 2) distance *= Math.max((framed.right - framed.left) / (state.region.w * fill), (framed.bottom - framed.top) / (state.region.h * fill));
      distance = Math.min(90, Math.max(2.5, distance));
    }
    const shiftX = state.region.x + state.region.w / 2 - (framed.left + framed.right) / 2;
    const shiftY = state.region.y + state.region.h / 2 - (framed.top + framed.bottom) / 2;
    camera.setViewOffset(size.w, size.h, -shiftX, -shiftY, size.w, size.h);
    camera.updateMatrixWorld();
    Object.assign(box, measure(FULL));
  }

  const corner = new THREE.Vector3(), near = new THREE.Vector3(), far = new THREE.Vector3();
  function render(state) {
    setPanel(state.aspect);
    shared.uTime.value = state.time;
    shared.tA.value = textureOf(state.a); fit(state.a, shared.uFitA.value);
    shared.tB.value = textureOf(state.b); fit(state.b, shared.uFitB.value);
    shared.uMix.value = state.mix;
    shared.uTexW.value = (state.mix > 0.5 ? state.b : state.a)?.width || 1728;
    shared.uDim.value = state.dim;
    shared.uSpotOn.value = state.spot.on;
    shared.uSpot.value.set(state.spot.u * shared.uAspect.value, 1 - state.spot.v, state.spot.r);
    shared.uLight.value = state.dim * lerp(1, 0.32, state.spot.on);
    shared.uGlow.value.set(...state.glow);
    if (state.overlay && !inkTexture) { inkTexture = raw(new THREE.CanvasTexture(state.overlay)); inkTexture.premultiplyAlpha = true; }
    if (inkTexture && inkVersion !== state.overlayVersion) { inkVersion = state.overlayVersion; inkTexture.needsUpdate = true; }
    shared.tInk.value = state.inkOn && inkTexture ? inkTexture : clear;
    shared.uInkOn.value = state.inkOn && inkTexture ? 1 : 0;

    aim(state);

    const light = spot.material.uniforms;
    light.uIntensity.value = 0.62 * state.spot.on;
    spot.visible = state.spot.on > 0.003;
    light.uApex.value.set(lerp(1.2, px(state.spot.u), 0.25), panel.cy + panel.h * 0.5 + 9, 8.5);
    light.uBase.value.set(px(state.spot.u), py(state.spot.v), 0.02);
    light.uR1.value = state.spot.r * panel.h;

    const ray = laser.material.uniforms;
    laser.visible = state.laser.on > 0.003;
    ray.uIntensity.value = state.laser.on;
    ray.uRes.value.set(size.w, size.h);
    ray.uTo.value.set(px(state.laser.u), py(state.laser.v), 0.02);
    // The laser starts below the right corner of the frame, as if you held it.
    near.set(state.stacked ? 0.7 : 1.08, -1.5, -1).unproject(camera);
    far.set(state.stacked ? 0.7 : 1.08, -1.5, 1).unproject(camera);
    ray.uFrom.value.copy(near).addScaledVector(far.sub(near).normalize(), 2.2);

    motes.material.uniforms.uScale.value = renderer.getPixelRatio() * (size.h / 900);
    renderer.render(scene, camera);
  }

  return {
    resize(w, h) {
      size.w = Math.max(1, w); size.h = Math.max(1, h);
      renderer.setPixelRatio(Math.min(devicePixelRatio || 1, 2));
      renderer.setSize(size.w, size.h, false);
      camera.aspect = size.w / size.h;
      camera.updateProjectionMatrix();
    },
    render,
    // Where a point of the view (in its own pixels) lands on the screen: u and v from 0 to 1, v from the top.
    pick(x, y) {
      near.set((x / size.w) * 2 - 1, 1 - (y / size.h) * 2, -1).unproject(camera);
      far.set((x / size.w) * 2 - 1, 1 - (y / size.h) * 2, 1).unproject(camera);
      far.sub(near);
      if (Math.abs(far.z) < 1e-6) return null;
      const t = -near.z / far.z;
      if (t < 0) return null;
      corner.copy(near).addScaledVector(far, t);
      return { u: corner.x / panel.w + 0.5, v: 0.5 - (corner.y - panel.cy) / panel.h };
    },
    // The screen's outline in the view, in pixels.
    box: () => box,
    // A paused video shows a new picture after a seek; make sure it reaches the screen.
    touch(clip) { const entry = textures.get(clip.key); if (entry?.video) entry.video.needsUpdate = true; },
    lost(callback) { canvas.addEventListener("webglcontextlost", (event) => { event.preventDefault(); callback(); }); },
  };
}
