import "./product-stage";
import {mountPlayer} from "./media-player";

function mountTake(root: HTMLElement) {
  const video=root.querySelector<HTMLVideoElement>("[data-take-video]");
  const poster=root.querySelector<HTMLElement>("[data-take-poster]");
  const toggle=root.querySelector<HTMLButtonElement>("[data-take-toggle]");
  const label=toggle?.querySelector<HTMLElement>("[data-take-label]");
  const symbol=toggle?.querySelector<HTMLElement>("[data-take-symbol]");
  const status=root.querySelector<HTMLElement>("[data-take-status]");
  const error=root.querySelector<HTMLElement>("[data-take-error]");
  const reason=root.querySelector<HTMLElement>("[data-take-error-reason]");
  if(!video||!poster||!toggle||!label||!symbol||!status||!error||!reason)
    throw new Error("A product recording is incomplete.");
  const player=mountPlayer(root,{video,poster,toggle,label,symbol,status,error,reason},{
    title: video.getAttribute("aria-label")??"demonstration",
    rate: Number(video.dataset.playbackRate),
  });
  root.addEventListener("take:selected",()=>player.selected());
  root.addEventListener("take:pause",()=>player.suspend());
  const sources=[...video.querySelectorAll("source")];
  if(!sources.length)return;
  const wanted=()=>sources.find(item=>!item.media||matchMedia(item.media).matches);
  // The direct link offers the encoding made for this screen.
  const link=error.querySelector<HTMLAnchorElement>("a");
  if(link)video.addEventListener("error",()=>{const source=wanted();if(source)link.href=source.src;},true);
  // Crossing the phone breakpoint (for example rotating a phone) loads the encoding made for the new shape.
  for(const query of new Set(sources.map(source=>source.media).filter(Boolean))){
    matchMedia(query).addEventListener("change",()=>{
      const source=wanted();
      if(!source||!video.currentSrc||video.currentSrc===source.src)return;
      video.load();
      player.sync();
    });
  }
}

document.querySelectorAll<HTMLElement>("[data-native-take]").forEach(mountTake);
export {};
