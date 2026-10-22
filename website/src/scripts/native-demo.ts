import { sitePath } from "../lib/paths";

const video = document.querySelector<HTMLVideoElement>("#native-demo-video");
const chapterControls = document.querySelector<HTMLElement>("[data-demo-chapters]");
const status = document.querySelector<HTMLElement>("[data-video-status]");
if (!video || !chapterControls || !status) throw new Error("The native demo player is incomplete.");
const player = video, message = status;
const duration = Number(player.dataset.duration);
if (!Number.isFinite(duration) || duration <= 0) throw new Error("The native demo duration is invalid.");
const phone = matchMedia("(max-width: 600px)");
function updatePoster() {
  player.poster = sitePath(phone.matches ? "/demo/annotator-ad-phone-poster.jpg" : "/demo/annotator-ad-poster.jpg");
}
updatePoster();
phone.addEventListener("change", updatePoster);
function revealLinkedFilm() {
  if (location.hash === "#native-demo") {
    const disclosure = player.closest("details");
    if (disclosure) disclosure.open = true;
  }
}
revealLinkedFilm();
window.addEventListener("hashchange", revealLinkedFilm);

async function play() {
  try {
    await player.play();
    message.textContent = "Native app footage with music. Use fullscreen to inspect the controls.";
  } catch (error) {
    message.textContent = player.error ? "The video could not be loaded. Read the description below." :
      "Playback did not start. Use the video’s Play control to try again.";
    console.info("Native demo playback did not start.", error);
  }
}

for (const link of document.querySelectorAll<HTMLAnchorElement>("[data-watch-native-demo]")) {
  link.addEventListener("click", (event) => {
    event.preventDefault();
    const section = document.querySelector("#native-demo");
    const disclosure = section?.closest("details");
    if (disclosure) disclosure.open = true;
    section?.scrollIntoView({ block: "start" });
    if (location.hash !== "#native-demo") history.pushState(null, "", "#native-demo");
    void play();
  });
}
for (const button of document.querySelectorAll<HTMLButtonElement>("[data-video-time]")) {
  button.addEventListener("click", () => {
    const time = Number(button.dataset.videoTime);
    if (!Number.isFinite(time) || time < 0 || time > duration) throw new Error("Invalid native demo chapter.");
    player.scrollIntoView({block: "center"});
    player.currentTime = time;
    void play();
  });
}
player.addEventListener("error", () => {
  message.textContent = "The video could not be loaded. The description below lists the recorded actions.";
});
player.addEventListener("play", () => {
  const bounds = player.getBoundingClientRect();
  if (document.hidden || player.closest("details")?.open === false ||
      bounds.bottom <= 0 || bounds.top >= innerHeight) player.pause();
});
player.closest("details")?.addEventListener("toggle", () => {
  if (player.closest("details")?.open === false) player.pause();
});
const visibility = new IntersectionObserver(([entry]) => {
  if (!entry.isIntersecting && !player.paused) player.pause();
}, { threshold: 0.05 });
visibility.observe(player);
document.addEventListener("visibilitychange", () => { if (document.hidden) player.pause(); });
window.addEventListener("pagehide", () => {
  player.pause();
  visibility.disconnect();
});
window.addEventListener("pageshow", () => { visibility.observe(player); });
chapterControls.hidden = false;
