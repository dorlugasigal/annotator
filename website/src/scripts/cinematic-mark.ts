import {
  ACESFilmicToneMapping, AmbientLight, CatmullRomCurve3, Color, DirectionalLight,
  Group, Mesh, MeshBasicMaterial, MeshPhysicalMaterial, ShadowMaterial, PerspectiveCamera,
  PlaneGeometry, PMREMGenerator, Scene, SphereGeometry, TubeGeometry, Vector3,
  WebGLRenderer, PCFShadowMap,
} from "three";
import { RoomEnvironment } from "three/addons/environments/RoomEnvironment.js";

const host = document.querySelector<HTMLElement>("[data-cinematic-mark]");
if (!host) throw new Error("The annotation sculpture host is missing.");
const canvas = host.querySelector<HTMLCanvasElement>("[data-cinematic-canvas]");
const replay = host.querySelector<HTMLButtonElement>("[data-cinematic-replay]");
const status = host.querySelector<HTMLElement>("[data-cinematic-status]");
const proofTarget = host.querySelector<HTMLElement>("[data-proof-target]");
if (!canvas || !replay || !status || !proofTarget) throw new Error("The annotation sculpture controls are missing.");
const reduced = matchMedia("(prefers-reduced-motion: reduce)");
let frame = 0;
let disposed = false;
let renderer: WebGLRenderer | undefined;
let visible = true;

try {
  renderer = new WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: "low-power" });
} catch (error) {
  host.dataset.renderer = "unavailable";
  status.textContent = "Static illustration. 3D rendering is unavailable in this browser.";
  console.info("Annotation sculpture uses its static fallback.", error);
}

if (renderer) {
  const gpu = renderer;
  const root = host;
  const replayButton = replay;
  const statusText = status;
  const targetElement = proofTarget;
  gpu.setPixelRatio(Math.min(devicePixelRatio, 1.5));
  gpu.setClearColor(0x080e11, 0);
  gpu.toneMapping = ACESFilmicToneMapping;
  gpu.toneMappingExposure = 1.2;
  gpu.shadowMap.enabled = true;
  gpu.shadowMap.type = PCFShadowMap;
  const scene = new Scene();
  const camera = new PerspectiveCamera(34, 1, 0.1, 50);
  camera.position.set(0, 1.1, 9);
  camera.lookAt(0, 0, 0);
  const environment = new RoomEnvironment();
  const pmrem = new PMREMGenerator(gpu);
  const environmentMap = pmrem.fromScene(environment, .025);
  scene.environment = environmentMap.texture;
  environment.dispose();
  pmrem.dispose();
  const key = new DirectionalLight(0xfff0d0, 4);
  key.position.set(-3, 5, 4);
  key.castShadow = true;
  key.shadow.mapSize.set(1024, 1024);
  key.shadow.camera.left = -5; key.shadow.camera.right = 5;
  key.shadow.camera.top = 5; key.shadow.camera.bottom = -5;
  key.shadow.bias = -.002;
  scene.add(key);
  const rim = new DirectionalLight(0xc0e6ff, 2.2);
  rim.position.set(4, 1, -2);
  scene.add(rim, new AmbientLight(0xbec7d4, .4));
  const group = new Group();
  scene.add(group);
  const gold = new MeshPhysicalMaterial({
    color: new Color("#edb831"), metalness: .55, roughness: .26,
    clearcoat: 1, clearcoatRoughness: .18, envMapIntensity: 1.4,
  });
  const goldColor = new Color("#edb831");
  const inkColor = new Color("#dd4931");
  const ink = new MeshBasicMaterial({ color: "#dd4931", toneMapped: false });
  const paths: Vector3[][] = [
    [new Vector3(1.55, 2.2, -.25), new Vector3(2.1, 1.45, -.6),
      new Vector3(1.15, .55, -.2), new Vector3(-.2, .15, .6),
      new Vector3(-1.1, -.6, .35), new Vector3(-1.05, -1.75, .1)],
    [new Vector3(-1.05, -1.75, .1), new Vector3(-1.68, -.82, .08)],
    [new Vector3(-1.05, -1.75, .1), new Vector3(-.08, -1.05, .1)],
  ];
  const strokes = paths.map((points, index) => {
    const curve = new CatmullRomCurve3(points, false, "centripetal");
    const geometry = new TubeGeometry(curve, index === 0 ? 120 : 24, .14, 16, false);
    const mesh = new Mesh<TubeGeometry, MeshPhysicalMaterial | MeshBasicMaterial>(geometry, gold);
    mesh.castShadow = true; mesh.receiveShadow = true;
    group.add(mesh);
    return mesh;
  });
  const capGeometry = new SphereGeometry(.14, 20, 12);
  const caps = [paths[0][0], paths[0][paths[0].length - 1], paths[1][1], paths[2][1]].map((point) => {
    const cap = new Mesh<SphereGeometry, MeshPhysicalMaterial | MeshBasicMaterial>(capGeometry, gold);
    cap.position.copy(point); cap.castShadow = true;
    group.add(cap);
    return cap;
  });
  const floorGeometry = new PlaneGeometry(40, 40);
  const floorMaterial = new ShadowMaterial({ color: 0x000000, opacity: .25 });
  const floor = new Mesh(floorGeometry, floorMaterial);
  floor.rotation.x = -Math.PI / 2; floor.position.y = -2.3; floor.receiveShadow = true;
  scene.add(floor);
  const landing = new Vector3();
  const finalScale = new Vector3(.48, .48, .04);
  const arrowTip = paths[0][paths[0].length - 1];

  function locateTarget() {
    const bounds = root.getBoundingClientRect();
    const target = targetElement.getBoundingClientRect();
    if (!bounds.width || !bounds.height) return;
    const x = ((target.left + target.width / 2 - bounds.left) / bounds.width) * 2 - 1;
    const y = 1 - ((target.top + target.height / 2 - bounds.top) / bounds.height) * 2;
    const ray = new Vector3(x, y, .5).unproject(camera).sub(camera.position).normalize();
    const point = camera.position.clone().addScaledVector(ray, -camera.position.z / ray.z);
    landing.copy(point).sub(arrowTip.clone().multiply(finalScale));
  }

  function render() {
    if (!disposed && visible && !document.hidden) gpu.render(scene, camera);
  }
  function finalState() {
    group.rotation.set(0, 0, 0);
    group.scale.copy(finalScale);
    group.position.copy(landing);
    floorMaterial.opacity = 0;
    gold.roughness = .5;
    gold.metalness = .18;
    root.style.setProperty("--proof-reveal", "1");
    for (const stroke of strokes) stroke.geometry.setDrawRange(0, Infinity);
    for (const stroke of strokes) stroke.material = ink;
    for (const cap of caps) cap.material = ink;
    for (const cap of caps) cap.visible = true;
    root.dataset.playing = "false";
    group.updateMatrixWorld(true);
    const projected = arrowTip.clone().applyMatrix4(group.matrixWorld).project(camera);
    root.dataset.landingX = String((projected.x + 1) / 2);
    root.dataset.landingY = String((1 - projected.y) / 2);
    render();
  }
  function stop() {
    if (frame) cancelAnimationFrame(frame);
    frame = 0;
    finalState();
  }
  let userMotion = true;
  function play() {
    stop();
    if (disposed || reduced.matches || !userMotion || !visible || document.hidden) return;
    const start = performance.now();
    for (const stroke of strokes) stroke.material = gold;
    for (const cap of caps) cap.material = gold;
    root.dataset.playing = "true";
    const tick = (time: number) => {
      const t = Math.min(1, (time - start) / 2800);
      const draw = Math.min(1, t / .45);
      const ease = 1 - Math.pow(1 - draw, 3);
      const contact = Math.min(1, Math.max(0, (t - .42) / .58));
      const settle = contact * contact * (3 - 2 * contact);
      group.rotation.set((.1 + .18 * (1 - ease)) * (1 - settle),
                         (-.32 + 1.2 * (1 - ease)) * (1 - settle),
                         (-.06 - .16 * (1 - ease)) * (1 - settle));
      const scale = .76 + .24 * ease;
      group.scale.set(scale + (finalScale.x - scale) * settle,
                      scale + (finalScale.y - scale) * settle,
                      scale + (finalScale.z - scale) * settle);
      group.position.copy(landing).multiplyScalar(settle);
      group.position.y -= .18 * (1 - ease) * (1 - settle);
      floorMaterial.opacity = .25 * (1 - settle);
      gold.roughness = .26 + .24 * settle;
      gold.metalness = .55 - .37 * settle;
      gold.color.copy(goldColor).lerp(inkColor, Math.max(0, (settle - .6) / .4));
      root.style.setProperty("--proof-reveal", String(Math.min(1, contact * 2)));
      strokes.forEach((stroke, index) => {
        const progress = index === 0 ? Math.min(1, draw / .65) : Math.min(1, Math.max(0, (draw - .57) / .3));
        const count = stroke.geometry.index?.count ?? 0;
        stroke.geometry.setDrawRange(0, Math.floor(count * progress / 3) * 3);
      });
      caps.forEach((cap, index) => { cap.visible = index === 0 || (index === 1 ? draw > .65 : draw > .87); });
      render();
      if (t < 1) frame = requestAnimationFrame(tick);
      else { frame = 0; finalState(); }
    };
    tick(start);
  }
  function resize() {
    const bounds = root.getBoundingClientRect();
    if (bounds.width <= 0 || bounds.height <= 0) return;
    gpu.setSize(bounds.width, bounds.height, false);
    camera.aspect = bounds.width / bounds.height;
    camera.position.z = camera.aspect < .85 ? 11 : 9;
    camera.lookAt(0, 0, 0);
    camera.updateProjectionMatrix();
    camera.updateMatrixWorld();
    locateTarget();
    if (root.dataset.playing !== "true") finalState();
    else render();
  }
  const resizeObserver = new ResizeObserver(resize);
  resizeObserver.observe(root);
  const visibilityObserver = new IntersectionObserver(([entry]) => {
    visible = entry.isIntersecting;
    if (!visible) stop();
    else render();
  });
  visibilityObserver.observe(root);
  replayButton.addEventListener("click", play);
  const motionChanged = () => {
    stop();
    replayButton.disabled = reduced.matches || !userMotion;
    statusText.textContent = reduced.matches ? "Motion is off because Reduce Motion is enabled." :
      !userMotion ? "Motion is off in the example controls." : "";
  };
  const userMotionChanged = (event: Event) => {
    if (!(event instanceof CustomEvent) || typeof event.detail?.enabled !== "boolean") return;
    userMotion = event.detail.enabled;
    motionChanged();
  };
  document.addEventListener("annotator-motion-change", userMotionChanged);
  reduced.addEventListener("change", motionChanged);
  document.addEventListener("visibilitychange", () => { if (document.hidden) stop(); else render(); });
  canvas!.addEventListener("webglcontextlost", (event) => {
    event.preventDefault(); stop(); root.dataset.renderer = "unavailable";
    statusText.textContent = "3D rendering paused. The static illustration is shown.";
    replayButton.hidden = true;
  });
  canvas!.addEventListener("webglcontextrestored", () => {
    if (disposed) return;
    root.dataset.renderer = "webgl";
    replayButton.hidden = false;
    resize(); finalState(); motionChanged();
  });
  window.addEventListener("pageshow", (event) => { if (event.persisted && !disposed) { resize(); finalState(); } });
  window.addEventListener("pagehide", (event) => {
    if (frame) cancelAnimationFrame(frame);
    frame = 0;
    if (event.persisted) { finalState(); return; }
    disposed = true;
    resizeObserver.disconnect(); visibilityObserver.disconnect();
    reduced.removeEventListener("change", motionChanged);
    document.removeEventListener("annotator-motion-change", userMotionChanged);
    for (const stroke of strokes) stroke.geometry.dispose();
    capGeometry.dispose(); floorGeometry.dispose(); floorMaterial.dispose(); gold.dispose(); ink.dispose();
    environmentMap.dispose(); gpu.dispose();
  });
  resize();
  finalState();
  root.dataset.renderer = "webgl";
  replayButton.hidden = false;
  motionChanged();
  if (!reduced.matches) play();
}
