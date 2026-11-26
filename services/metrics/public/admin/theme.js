// The dashboard's theme, as on the website: dark unless the owner chose light, kept under the same name, so the site
// and the dashboard always match. This file loads before the page is drawn (no flash of the other theme), and turns
// the switch on once the page is there. It has no part in signing in or reading counts.
(function () {
  "use strict";
  var KEY = "annotator.site.theme";
  var PAGE = { dark: "#0e0e11", light: "#f9f8fa" };
  var root = document.documentElement;
  var stored = null;
  try { stored = localStorage.getItem(KEY); } catch (error) { stored = null; }
  root.dataset.theme = stored === "light" ? "light" : "dark";
  var still = matchMedia("(prefers-reduced-motion: reduce)");

  function show(theme) {
    root.dataset.theme = theme;
    var meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.setAttribute("content", PAGE[theme]);
    document.querySelectorAll(".theme-switch").forEach(function (button) {
      button.setAttribute("aria-checked", String(theme === "dark"));
    });
  }

  var turn = 0;
  function choose(theme, from) {
    try { localStorage.setItem(KEY, theme); } catch (error) { /* storage is off: the choice lasts for this visit */ }
    var mine = ++turn;
    if (still.matches) { show(theme); return; }
    if (!document.startViewTransition || !from) {
      root.classList.add("theme-fade");
      show(theme);
      setTimeout(function () { if (mine === turn) root.classList.remove("theme-fade"); }, 340);
      return;
    }
    // The new theme opens in a circle from the switch, far enough to reach the farthest corner.
    var box = from.getBoundingClientRect(), x = box.left + box.width / 2, y = box.top + box.height / 2;
    root.style.setProperty("--vt-x", x + "px");
    root.style.setProperty("--vt-y", y + "px");
    root.style.setProperty("--vt-r", Math.ceil(Math.hypot(Math.max(x, innerWidth - x), Math.max(y, innerHeight - y))) + "px");
    root.classList.add("theme-reveal");
    var transition = document.startViewTransition(function () { show(theme); });
    var done = function () { if (mine === turn) root.classList.remove("theme-reveal"); };
    transition.finished.then(done, done);
  }

  document.addEventListener("DOMContentLoaded", function () {
    show(root.dataset.theme);
    document.querySelectorAll(".theme-switch").forEach(function (button) {
      button.addEventListener("click", function () { choose(root.dataset.theme === "dark" ? "light" : "dark", button); });
    });
  });
  // The website or another dashboard tab changed the theme: this one follows.
  addEventListener("storage", function (event) {
    if (event.key === KEY && (event.newValue === "light" || event.newValue === "dark")) show(event.newValue);
  });
})();
