import { createInk } from "/concepts/shared/ink.js";

const root = document.documentElement;
const reduced = matchMedia("(prefers-reduced-motion: reduce)");
const $ = (selector) => document.querySelector(selector);
const wait = (ms) => new Promise((done) => setTimeout(done, ms));

// The sample stage: the whole of it can be drawn on, and the app's toolbar rests at its bottom edge.
// The toolbar lives in the dock, which keeps it a little away from the stage's sides.
const screen = $("#screen"), number = $("#ring-target");
const ink = createInk({
  surface: screen, toolbarParent: $("#dock"), toolbarPosition: "absolute",
  appearance: root.dataset.theme || "auto", properties: false, tool: "select", moreHref: "#install",
});
// The stage has room for the toolbar with its properties open. If the toolbar is ever taller, the stage grows downward.
let reserved = 0;
ink.on("layout", ({ height }) => {
  if (!height || ink.collapsed || height === reserved) return;
  reserved = height;
  requestAnimationFrame(() => screen.style.setProperty("--tb-h", `${height}px`));
});
ink.fit();

// Once, the pointer draws one ring around the number that dropped. With reduced motion the ring is already there.
let ringed = false;
async function ring() {
  if (ringed) return;
  ringed = true;
  const moving = !reduced.matches, padX = screen.clientWidth < 560 ? 11 : 18;
  await ink.perform([{ tool: "ellipse", around: number, padX, padY: 11, size: 3, rough: 0, duration: 900 }], { cursor: moving, showTool: moving });
  await wait(700);
  ink.hideGhost();
}
(document.fonts?.ready || Promise.resolve()).then(() => {
  if (reduced.matches || !window.IntersectionObserver) { ring(); return; }
  const seen = new IntersectionObserver(([entry]) => {
    if (!entry.isIntersecting) return;
    seen.disconnect();
    setTimeout(ring, 900);
  }, { threshold: 1 });
  seen.observe(number);
});

if (window.IntersectionObserver) {
  // Drawing ends when the sample screen is out of view, so keys and touches go back to the page.
  new IntersectionObserver(([entry]) => { if (!entry.isIntersecting) ink.setMode("use"); }).observe(screen);

  const reveal = new IntersectionObserver((entries) => {
    for (const entry of entries) if (entry.isIntersecting) { entry.target.classList.add("is-in"); reveal.unobserve(entry.target); }
  }, { rootMargin: "0px 0px -8% 0px" });
  document.querySelectorAll(".reveal").forEach((section) => reveal.observe(section));
} else document.querySelectorAll(".reveal").forEach((section) => section.classList.add("is-in"));

// The recording: the poster first, playing only while it is in view, with two plain buttons.
const reel = $("#reel"), play = $("#reel-play"), sound = $("#reel-sound");
if (matchMedia("(max-width: 599px)").matches) {
  reel.poster = "reel-poster-phone.jpg";
  reel.src = "/public/demo/annotator-reel-phone.mp4";
}
reel.controls = false;
$(".video-controls").hidden = false;
let wanted = !reduced.matches, inView = false;
const label = () => {
  play.textContent = reel.paused ? "Play" : "Pause";
  sound.textContent = reel.muted ? "Turn sound on" : "Turn sound off";
};
const toggle = () => {
  wanted = reel.paused;
  if (wanted) reel.play().catch(() => {}); else reel.pause();
};
play.addEventListener("click", toggle);
reel.addEventListener("click", toggle);
sound.addEventListener("click", () => { reel.muted = !reel.muted; });
for (const name of ["play", "pause", "volumechange"]) reel.addEventListener(name, label);
if (window.IntersectionObserver) new IntersectionObserver(([entry]) => {
  inView = entry.isIntersecting;
  if (inView && wanted) reel.play().catch(() => {}); else if (!inView) reel.pause();
}, { threshold: 0.5 }).observe(reel);

// Copy a command. Where the clipboard is not allowed, the command is selected so it can be copied by hand.
const status = $("#status");
for (const button of document.querySelectorAll("[data-copy]")) {
  button.addEventListener("click", async () => {
    const code = button.closest(".command").querySelector("code");
    let copied = true;
    try { await navigator.clipboard.writeText(code.textContent.trim()); }
    catch {
      const range = document.createRange();
      range.selectNodeContents(code);
      getSelection().removeAllRanges();
      getSelection().addRange(range);
      copied = document.execCommand("copy");
    }
    button.textContent = copied ? "Copied" : "Selected";
    status.textContent = copied ? "Command copied." : "Command selected. Copy it with your keyboard.";
    setTimeout(() => { button.textContent = "Copy"; status.textContent = ""; }, 2000);
  });
}

// The visitor's platform comes first in the page's order too, not only on screen.
if (root.dataset.platform === "windows") $(".platforms").prepend($('.platform[data-os="windows"]'));
