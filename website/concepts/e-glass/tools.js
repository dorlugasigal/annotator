import { $, $$, reduced, phone, onScreen, startHeader } from "./page.js";
import { APP_ICONS } from "/concepts/shared/app-icons.js";

startHeader();

// The app's own icons beside the names.
for (const item of $$(".toollist li[data-icon]")) {
  const parts = APP_ICONS[item.dataset.icon];
  if (!parts) continue;
  item.insertAdjacentHTML("afterbegin", `<svg viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${parts.map((part) => `<path d="${part.d}"${part.fill ? ' fill="currentColor"' : ""}/>`).join("")}</svg>`);
}

// A rail of panes; the one in the middle is clear and plays, the others wait behind a sheen.
{
  const rail = $("#rail"), track = $("#rail-track"), panes = $$(".pane", track), names = $("#rail-names"), status = $("#rail-status");
  let active = -1, visible = false, loaded = false, settle = 0;
  const buttons = panes.map((pane, i) => {
    const button = document.createElement("button");
    button.type = "button"; button.textContent = $("h3", pane).textContent;
    button.addEventListener("click", () => go(i));
    names.append(button);
    pane.addEventListener("click", () => { if (i !== active) go(i); });
    return button;
  });
  const file = (pane, type) => `/public/demo/stage-${pane.dataset.clip}${phone.matches ? "-phone" : ""}.${type}`;
  function media() {
    panes.forEach((pane, i) => {
      const video = $("video", pane);
      if (loaded && video.dataset.poster !== file(pane, "jpg")) { video.dataset.poster = video.poster = file(pane, "jpg"); video.removeAttribute("src"); video.load(); }
      if (i === active && loaded) {
        if (!video.getAttribute("src")) video.src = file(pane, "mp4");
        video.controls = reduced;
        if (visible && !reduced) video.play().catch(() => {});
        else if (!reduced) video.pause();
      } else { video.controls = false; if (!video.paused) video.pause(); }
    });
  }
  function mark(i, announce) {
    if (i === active) return;
    active = i;
    panes.forEach((pane, n) => { pane.classList.toggle("is-active", n === i); buttons[n].setAttribute("aria-pressed", String(n === i)); });
    media();
    if (announce) status.textContent = `${$("h3", panes[i]).textContent}, ${i + 1} of ${panes.length}. ${$("p", panes[i]).textContent}`;
  }
  const leftOf = (pane) => pane.offsetLeft - (track.clientWidth - pane.offsetWidth) / 2;
  function go(i) {
    i = (i + panes.length) % panes.length;
    track.scrollTo({ left: leftOf(panes[i]), behavior: reduced ? "auto" : "smooth" });
    mark(i, true);
  }
  // The pane nearest the middle is the active one, also when the visitor scrolls the rail by hand.
  track.addEventListener("scroll", () => {
    clearTimeout(settle);
    settle = setTimeout(() => {
      const middle = track.scrollLeft + track.clientWidth / 2;
      let best = 0, far = Infinity;
      panes.forEach((pane, i) => { const d = Math.abs(pane.offsetLeft + pane.offsetWidth / 2 - middle); if (d < far) { far = d; best = i; } });
      mark(best, true);
    }, 90);
  }, { passive: true });
  rail.addEventListener("keydown", (event) => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    event.preventDefault();
    go(active + (event.key === "ArrowRight" ? 1 : -1));
    if (names.contains(document.activeElement)) buttons[active].focus();
  });
  $("#rail-prev").addEventListener("click", () => go(active - 1));
  $("#rail-next").addEventListener("click", () => go(active + 1));
  mark(0, false);
  onScreen(rail, (near) => { if (near && !loaded) { loaded = true; media(); } }, { rootMargin: "900px 0px" });
  onScreen(track, (shown) => { visible = shown; media(); }, { threshold: 0.3 });
  phone.addEventListener("change", media);
}
