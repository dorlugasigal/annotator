import {motionAllowed, token} from "./motion-tokens";

/** The stage's tabs and panels: the app's clips. */
interface StageSet { tablist: string; tab: string; panel: string; tabKey: string; panelKey: string }
const sets: Record<string, StageSet> = {
  mac: {tablist: "[data-stage-tabs]", tab: "[data-stage-tab]", panel: "[data-stage-panel]", tabKey: "stageTab", panelKey: "stagePanel"},
};

const root = document.querySelector<HTMLElement>("[data-product-stage]");
// The tab list is vertical beside the stage on wide screens, and a grid above it elsewhere.
const vertical = matchMedia("(min-width: 1001px)");

function mountSet(container: HTMLElement, set: StageSet) {
  const navigation = container.querySelector<HTMLElement>(set.tablist);
  const tabs = [...container.querySelectorAll<HTMLButtonElement>(set.tab)];
  const panels = [...container.querySelectorAll<HTMLElement>(set.panel)];
  if (!navigation || tabs.length !== panels.length || tabs.length === 0 ||
      tabs.some((tab,index)=>tab.dataset[set.tabKey]!==panels[index]?.dataset[set.panelKey])) {
    throw new Error("Product-stage features and panels must match.");
  }
  const tablist = navigation;
  for (const panel of panels) {
    const details = panel.querySelector("details");
    if (details) {
      details.querySelector("summary")?.remove();
      const videos = [...details.querySelectorAll("video")];
      details.replaceWith(...details.childNodes);
      // Chromium stops reporting a failed <source> for a video moved in the page; restarting its
      // source selection keeps the error and direct link working. With preload="none" this is free.
      for (const video of videos) video.load();
    }
  }
  const columns = () => Math.max(1, Number(getComputedStyle(tablist).getPropertyValue("--tab-columns")) || tabs.length);
  function place() {
    tablist.setAttribute("aria-orientation", vertical.matches ? "vertical" : "horizontal");
    const count = vertical.matches ? 1 : columns();
    tablist.style.setProperty("--active-col", String(vertical.matches ? 0 : current % count));
    tablist.style.setProperty("--active-row", String(vertical.matches ? current : Math.floor(current / count)));
  }

  let current = 0;
  let wipe: Animation | undefined;
  const clip = (panel: HTMLElement) => panel.querySelector("[data-native-take]");
  // The new example wipes in from the side the selection moved toward, over the one it replaces,
  // so the stage never shows an empty frame. Its picture glides a little inside the wipe, like a camera.
  function reveal(incoming: HTMLElement, outgoing: HTMLElement, direction: number) {
    wipe?.finish();
    if (!direction || incoming === outgoing || !motionAllowed()) { outgoing.hidden = outgoing !== panels[current]; return; }
    outgoing.hidden = false;
    outgoing.dataset.leaving = "";
    incoming.dataset.entering = "";
    const radius = getComputedStyle(incoming.querySelector(".player") ?? incoming).borderTopLeftRadius || "0px";
    const from = vertical.matches ? (direction > 0 ? `inset(100% 0 0 0 round ${radius})` : `inset(0 0 100% 0 round ${radius})`)
      : (direction > 0 ? `inset(0 0 0 100% round ${radius})` : `inset(0 100% 0 0 round ${radius})`);
    const timing = {duration: token("--beat"), easing: token("--ease-move", "string")};
    const shift = vertical.matches ? `0 ${direction * 24}px` : `${direction * 24}px 0`;
    wipe = incoming.animate([{clipPath: from}, {clipPath: `inset(0 0 0 0 round ${radius})`}], timing);
    for (const layer of incoming.querySelectorAll(".player-screen > picture, .player-screen > video"))
      layer.animate([{translate: shift}, {translate: "0 0"}], timing);
    const settle = () => {
      delete incoming.dataset.entering;
      delete outgoing.dataset.leaving;
      outgoing.hidden = outgoing !== panels[current];
    };
    wipe.finished.then(settle, settle);
  }
  function select(index: number, focus=false, user=false) {
    const previous = current;
    const direction = user ? Math.sign(index - previous) : 0;
    tabs.forEach((tab,position)=>{
      const selected = index===position;
      tab.setAttribute("aria-selected",String(selected));
      tab.tabIndex = selected?0:-1;
      if (selected && focus) tab.focus();
    });
    panels.forEach((panel,position)=>{
      if (position!==index) clip(panel)?.dispatchEvent(new CustomEvent("take:pause"));
      if (position!==index && position!==previous) panel.hidden = true;
    });
    current = index;
    panels[index]!.hidden = false;
    reveal(panels[index]!, panels[previous]!, direction);
    tablist.style.setProperty("--active-index", String(index));
    place();
    if (user) clip(panels[index]!)?.dispatchEvent(new Event("take:selected"));
  }
  vertical.addEventListener("change", place);
  tabs.forEach((tab,index)=>{
    panels[index]!.setAttribute("role","tabpanel");
    panels[index]!.setAttribute("aria-labelledby",tab.id);
    tab.addEventListener("click",()=>select(index,false,true));
    tab.addEventListener("keydown",event=>{
      let target: number;
      // Up and Down move between tabs only when the list is vertical; otherwise they scroll the page.
      const key = vertical.matches ? event.key.replace("ArrowDown", "ArrowRight").replace("ArrowUp", "ArrowLeft") : event.key;
      switch(key) {
        case "ArrowRight": target=(index+1)%tabs.length; break;
        case "ArrowLeft": target=(index+tabs.length-1)%tabs.length; break;
        case "Home": target=0; break;
        case "End": target=tabs.length-1; break;
        default: return;
      }
      event.preventDefault();
      select(target,true,true);
    });
  });
  select(0);
  tablist.hidden=false;
  place();
  return {
    /** Lays the tab list out again once its set shows: a hidden list has no columns to measure. */
    place,
    /** Pauses the set's clips, for example when the other system's set takes the stage. */
    pause() { for (const panel of panels) clip(panel)?.dispatchEvent(new CustomEvent("take:pause")); },
  };
}

if (root) {
  const mounted = new Map<string, ReturnType<typeof mountSet>>();
  for (const container of root.querySelectorAll<HTMLElement>("[data-stage-set]")) {
    const name = container.dataset.stageSet ?? "";
    const set = sets[name];
    if (!set) throw new Error("Unknown product-stage set.");
    mounted.set(name, mountSet(container, set));
  }
  // Load and decode the other examples' posters before the stage is reached, so a new tab shows its
  // finished result at once while its video loads.
  const warm = new IntersectionObserver(([entry]) => {
    if (!entry?.isIntersecting) return;
    warm.disconnect();
    for (const poster of root.querySelectorAll<HTMLImageElement>("[data-take-poster] img")) {
      poster.loading = "eager";
      poster.decode().catch(() => undefined);
    }
  }, {rootMargin: "100% 0px"});
  warm.observe(root);
  root.dataset.enhanced="true";
}
