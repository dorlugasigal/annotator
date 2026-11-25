// The taped prints: real clips that straighten and play on hover, focus or tap, and stop off screen.
export function setupPrints(scope = document) {
  const still = matchMedia("(prefers-reduced-motion: reduce)").matches;
  const canHover = matchMedia("(hover: hover) and (pointer: fine)").matches, narrow = matchMedia("(max-width: 700px)").matches;
  const watcher = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) entry.target.stop();
      else if (!canHover && !still && entry.intersectionRatio > 0.75) entry.target.play();
    }
  }, { threshold: [0, 0.75] });
  for (const print of scope.querySelectorAll(".print")) {
    const video = print.querySelector("video"), clip = print.dataset.clip, name = print.getAttribute("aria-label").replace("Play clip: ", "");
    if (narrow && print.dataset.phone !== "no") {
      video.poster = `/public/demo/stage-${clip}-phone.jpg`;
      video.src = `/public/demo/stage-${clip}-phone.mp4`;
      video.style.aspectRatio = "1080 / 1350";
    }
    print.play = () => { print.classList.add("is-playing"); print.setAttribute("aria-label", `Pause clip: ${name}`); video.play()?.catch(() => {}); };
    print.stop = () => { print.classList.remove("is-playing"); print.setAttribute("aria-label", `Play clip: ${name}`); video.pause(); };
    print.addEventListener("click", () => (video.paused ? print.play() : print.stop()));
    if (canHover) {
      print.addEventListener("pointerenter", print.play);
      print.addEventListener("pointerleave", print.stop);
      print.addEventListener("focus", () => { if (print.matches(":focus-visible")) print.play(); });
      print.addEventListener("blur", print.stop);
    }
    watcher.observe(print);
  }
}
