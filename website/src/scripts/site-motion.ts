import {motionAllowed} from "./motion-tokens";

const html = document.documentElement;

// Header: a sentinel at the top of the page flips a data attribute, so no scroll handler runs.
const sentinel = document.querySelector<HTMLElement>("[data-scroll-sentinel]");
if (sentinel) {
  new IntersectionObserver(([entry]) => {
    html.toggleAttribute("data-scrolled", !entry.isIntersecting);
  }).observe(sentinel);
}

// One-shot ink marks, drawn when their section arrives. Content stays visible without scripts: the
// undrawn starting state only exists under html.js-motion, and anything already on screen counts as drawn.
// Only a scroll-in adds is-arriving, which plays the install picture's demonstration.
const reveals = [...document.querySelectorAll<HTMLElement>("[data-reveal]")];
function revealAll() { for (const element of reveals) element.classList.add("is-revealed"); }
if (reveals.length) {
  if (typeof IntersectionObserver !== "function") revealAll();
  else {
    const observer = new IntersectionObserver(entries => {
      for (const entry of entries) if (entry.isIntersecting) {
        entry.target.classList.add("is-revealed", "is-arriving");
        observer.unobserve(entry.target);
      }
    }, {rootMargin: "0px 0px -10% 0px"});
    for (const element of reveals) {
      const bounds = element.getBoundingClientRect();
      if (bounds.top < innerHeight * .9 && bounds.bottom > 0) element.classList.add("is-revealed");
      else observer.observe(element);
    }
  }
  window.addEventListener("beforeprint", revealAll);
}
html.classList.add("js-motion");

// Same-page links glide to their section. The browser still performs the navigation, focus and
// :target; smoothing is limited to that one scroll so scripted scrolling stays instant.
document.addEventListener("click", event => {
  if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
  const link = event.target instanceof Element ? event.target.closest<HTMLAnchorElement>("a[href*='#']") : null;
  if (!link || !motionAllowed() || link.origin !== location.origin || link.pathname !== location.pathname) return;
  if (!link.hash || !document.getElementById(decodeURIComponent(link.hash.slice(1)))) return;
  html.dataset.smoothScroll = "";
  const settle = window.setTimeout(done, 1500);
  function done() {
    window.clearTimeout(settle);
    delete html.dataset.smoothScroll;
    window.removeEventListener("scrollend", done);
  }
  window.addEventListener("scrollend", done);
});

export {};
