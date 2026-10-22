export type Platform = "mac" | "windows";

/**
 * The visitor's desktop system when the browser names it: Chromium's client hint first, then the user agent. Phones,
 * tablets (an iPad can report a Mac) and other systems give null, so the page keeps its default order.
 */
export function visitorPlatform(): Platform | null {
  const hints = (navigator as Navigator & {userAgentData?: {platform?: string}}).userAgentData;
  const named = (hints?.platform || navigator.userAgent || "").toLowerCase();
  if (/android|iphone|ipad|ipod|cros/.test(named)) return null;
  if (/windows/.test(named)) return "windows";
  if (/macintosh|mac ?os/.test(named) && navigator.maxTouchPoints <= 1) return "mac";
  return null;
}

const visitor = visitorPlatform();
const remembered = (): Platform | null => {
  try {
    const value = sessionStorage.getItem("annotator-platform");
    return value === "mac" || value === "windows" ? value : null;
  } catch {
    return null;
  }
};
const remember = (platform: Platform) => {
  try {
    sessionStorage.setItem("annotator-platform", platform);
  } catch {
    // Storage can be off; the choice then lasts for this page only.
  }
};

// Download lists: the visitor's system first. Both choices always stay on the page.
for (const list of document.querySelectorAll<HTMLElement>("[data-platform-choices]")) {
  const preferred = visitor && list.querySelector<HTMLElement>(`:scope > [data-platform-choice="${visitor}"]`);
  if (preferred && preferred !== list.firstElementChild) list.prepend(preferred);
}

/**
 * A Mac and Windows switch: two buttons with aria-pressed that show the matching parts of their scope. Without scripts
 * the switch stays hidden and both systems' parts show. A scope with data-platform-default="visitor" starts on the
 * visitor's system (or the system chosen earlier in this visit), others on the Mac. A scope without its own switch shows
 * whatever every switch on the page last chose.
 */
const scopes = [...document.querySelectorAll<HTMLElement>("[data-platform-scope]")];
const show = (scope: HTMLElement, platform: Platform, user: boolean) => {
  for (const button of scope.querySelectorAll<HTMLButtonElement>("button[data-platform]")) {
    button.setAttribute("aria-pressed", String(button.dataset.platform === platform));
  }
  for (const part of scope.querySelectorAll<HTMLElement>("[data-platform-part]")) part.hidden = part.dataset.platformPart !== platform;
  scope.dataset.platform = platform;
  scope.dispatchEvent(new CustomEvent("platform:change", {detail: {platform, user}}));
};
const choose = (platform: Platform) => {
  remember(platform);
  for (const scope of scopes) show(scope, platform, true);
};
for (const scope of scopes) {
  const group = scope.querySelector<HTMLElement>("[data-platform-switch]");
  const buttons = [...(group?.querySelectorAll<HTMLButtonElement>("button[data-platform]") ?? [])];
  if (!scope.querySelector("[data-platform-part]")) throw new Error("A platform scope needs parts to show.");
  if (group && buttons.length !== 2) throw new Error("A platform switch needs one button for each system.");
  for (const button of buttons) button.addEventListener("click", () => choose(button.dataset.platform as Platform));
  const start = scope.dataset.platformDefault === "visitor" ? remembered() ?? visitor ?? "mac" : "mac";
  show(scope, start, false);
  if (group) group.hidden = false;
}
export {};