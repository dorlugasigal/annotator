// What both pages share: small helpers, the header and the install switch.
export const $ = (selector, root = document) => root.querySelector(selector);
export const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
export const reduced = matchMedia("(prefers-reduced-motion: reduce)").matches;
export const phone = matchMedia("(max-width: 640px)");
export const no3d = new URLSearchParams(location.search).has("no3d");
export const onScreen = (element, callback, options) => {
  const observer = new IntersectionObserver(([entry]) => callback(entry.isIntersecting, entry), options);
  observer.observe(element);
  return observer;
};

export function startHeader() {
  const header = $("#top");
  const stick = () => header.classList.toggle("is-stuck", scrollY > 16);
  addEventListener("scroll", stick, { passive: true });
  stick();
}

export function startInstall() {
  const tabs = $$(".switch [role=tab]");
  if (!tabs.length) return;
  const pick = (os, focus) => tabs.forEach((tab) => {
    const on = tab.dataset.os === os;
    tab.setAttribute("aria-selected", String(on));
    tab.tabIndex = on ? 0 : -1;
    $(`#${tab.getAttribute("aria-controls")}`).hidden = !on;
    if (on && focus) tab.focus();
  });
  tabs.forEach((tab, i) => {
    tab.addEventListener("click", () => pick(tab.dataset.os));
    tab.addEventListener("keydown", (event) => {
      if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
      event.preventDefault();
      pick(tabs[(i + 1) % tabs.length].dataset.os, true);
    });
  });
  pick(document.documentElement.dataset.os);

  $$(".copy").forEach((button) => {
    const label = button.getAttribute("aria-label");
    let timer = 0;
    button.addEventListener("click", async () => {
      const text = $(`#${button.dataset.copy}`).textContent;
      let ok = true;
      try { await navigator.clipboard.writeText(text); }
      catch {
        const range = document.createRange();
        range.selectNodeContents($(`#${button.dataset.copy}`));
        getSelection().removeAllRanges(); getSelection().addRange(range);
        try { ok = document.execCommand("copy"); } catch { ok = false; }
      }
      button.textContent = ok ? "Copied" : "Select it";
      button.setAttribute("aria-label", ok ? "Copied" : "Could not copy. The command is selected, press copy on your keyboard.");
      button.classList.toggle("is-done", ok);
      clearTimeout(timer);
      timer = setTimeout(() => { button.textContent = "Copy"; button.setAttribute("aria-label", label); button.classList.remove("is-done"); }, 1800);
    });
  });
}
