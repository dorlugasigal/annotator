// Runs before the first paint, so the page never shows the other platform or the other theme first.
// ?platform=windows|mac and ?theme=light|dark are for looking at the page; a visitor gets their own.
(function () {
  var root = document.documentElement, query = new URLSearchParams(location.search);
  var system = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || "";
  var platform = query.get("platform") || (/win/i.test(system) ? "windows" : "mac");
  root.dataset.platform = platform === "windows" ? "windows" : "mac";
  var theme = query.get("theme");
  if (theme === "light" || theme === "dark") root.dataset.theme = theme;
  root.classList.add("js");
})();
