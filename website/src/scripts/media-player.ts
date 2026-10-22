import {motionAllowed, token} from "./motion-tokens";

export interface PlayerParts {
  video: HTMLVideoElement;
  poster: HTMLElement;
  toggle: HTMLButtonElement;
  label: HTMLElement;
  symbol: HTMLElement;
  status: HTMLElement;
  error: HTMLElement;
  reason: HTMLElement;
}

export interface PlayerOptions {
  /** Completes the toggle's accessible name, as in "Pause <title>". */
  title: string;
  rate: number;
}

export interface Player {
  /** A tab or other explicit selection made this clip current. */
  selected(): void;
  /** Pause without recording a manual choice, for example when a tab hides the clip. */
  suspend(): void;
  /** Re-apply the visibility, motion and manual-pause rules. */
  sync(): void;
  /** Jump to a time and play from there, as an explicit choice (for example a chapter). */
  seek(seconds: number): void;
}

/** A slow network gets a message; an ordinary start does not flash one. */
const LOADING_NOTICE_MS = 1200;

/**
 * Shared playback for the reel and the clips: they start muted, autoplay only while at least 65% visible and
 * motion is allowed, pause offscreen or when the page is hidden, and keep a
 * manual pause across scrolling. Scripts replace the native controls with one
 * compact Play/Pause control; a failure brings the native controls back. The
 * poster stays in front until the video has shown a frame of playback, then
 * the video fades in over it, so choosing a clip never shows an empty frame.
 */
export function mountPlayer(root: HTMLElement, parts: PlayerParts, options: PlayerOptions): Player {
  const {video, poster, toggle, label, symbol, status, error, reason} = parts;
  const {title, rate} = options;
  const waitsForAutomaticData=root.hasAttribute("data-hero-reel");
  if(!Number.isFinite(rate)||rate<1||rate>2) throw new Error("Invalid demonstration playback rate.");

  const reduced=matchMedia("(prefers-reduced-motion: reduce)");
  const fallbackReason=reason.textContent?.trim()||"The recording could not play.";
  let visible=false;
  let autoplayVisible=false;
  let manualState: "none"|"paused"|"playing"="none";
  let presented=false;
  let loadingNotice: number|undefined;
  let revealPoster: number|undefined;

  function say(message: string) {
    status.textContent=message;
    status.hidden=!message;
  }
  function update() {
    const action=video.ended?"Replay":video.paused?"Play":"Pause";
    label.textContent=action;
    symbol.dataset.takeSymbol=action;
    symbol.querySelectorAll<HTMLElement>("[data-action]").forEach(item=>{item.hidden=item.dataset.action!==action;});
    toggle.setAttribute("aria-label",`${action} ${title}`);
    root.dataset.playback=error.hidden?(video.paused?"paused":"playing"):"error";
    if(!error.hidden) poster.hidden=false;
  }
  function stopLoadingNotice() {
    window.clearTimeout(loadingNotice);
    loadingNotice=undefined;
    if(status.dataset.kind==="loading"){ delete status.dataset.kind; say(""); }
  }
  // The first presented frame of playback, not the first loaded one: a paused clip keeps its poster.
  function markPresented() {
    if(presented||video.paused)return;
    presented=true;
    root.dataset.presented="true";
    window.clearTimeout(revealPoster);
    revealPoster=window.setTimeout(()=>{ if(presented&&error.hidden) poster.hidden=true; },
      motionAllowed()?token("--beat-half"):0);
  }
  function watchForFrame() {
    if(presented)return;
    if("requestVideoFrameCallback" in video) video.requestVideoFrameCallback(()=>markPresented());
  }
  function forgetFrame() {
    presented=false;
    delete root.dataset.presented;
    window.clearTimeout(revealPoster);
    poster.hidden=false;
  }
  function play() {
    if(!video.paused)return;
    // Reload after an error, or if every source failed before this script started listening.
    if(video.error||!error.hidden||video.networkState===HTMLMediaElement.NETWORK_NO_SOURCE){
      error.hidden=true;
      video.controls=false;
      video.load();
    }
    if(video.ended)video.currentTime=0;
    video.playbackRate=rate;
    void video.play().catch((failure: unknown)=>{
      if(video.error||(failure instanceof DOMException&&failure.name==="AbortError"))return;
      // A browser may refuse to resume with sound without a fresh click (for example when the reel comes
      // back into view). Keep playing muted instead of stopping; the Sound button shows the change.
      if(!video.muted&&failure instanceof DOMException&&failure.name==="NotAllowedError"){
        video.muted=true;
        play();
        return;
      }
      manualState="paused";
      stopLoadingNotice();
      say("Tap Play to watch.");
      update();
    });
  }
  function sync() {
    const automatic=autoplayVisible&&manualState==="none"&&!reduced.matches&&!document.hidden;
    if(automatic&&waitsForAutomaticData)video.preload="auto";
    video.loop=visible&&!reduced.matches;
    if(!visible||document.hidden||manualState==="paused"||(reduced.matches&&manualState!=="playing")){
      if(!video.paused)video.pause();
    } else if(manualState==="playing"||(automatic&&
      (!waitsForAutomaticData||video.readyState>=HTMLMediaElement.HAVE_CURRENT_DATA))) play();
    update();
  }
  function togglePlayback() {
    manualState=video.paused?"playing":"paused";
    if(manualState==="playing"){ say(""); play(); }
    else video.pause();
  }
  toggle.addEventListener("click",togglePlayback);
  video.addEventListener("click",()=>{if(!video.controls)togglePlayback();});
  video.addEventListener("playing",()=>{
    if(manualState==="paused"||document.hidden){
      video.pause();
      update();
      return;
    }
    error.hidden=true;
    stopLoadingNotice();
    say("");
    video.playbackRate=rate;
    video.controls=false;
    watchForFrame();
    update();
  });
  video.addEventListener("timeupdate",()=>{ if(video.currentTime>0) markPresented(); });
  video.addEventListener("waiting",()=>{
    if(loadingNotice!==undefined)return;
    loadingNotice=window.setTimeout(()=>{
      loadingNotice=undefined;
      // No candidate could be loaded, even if the browser did not report it: show the error and link.
      if(video.networkState===HTMLMediaElement.NETWORK_NO_SOURCE&&video.readyState===HTMLMediaElement.HAVE_NOTHING){ fail(); return; }
      if(video.paused||video.readyState>=HTMLMediaElement.HAVE_FUTURE_DATA)return;
      status.dataset.kind="loading";
      say("Loading video…");
    },LOADING_NOTICE_MS);
  });
  for(const type of ["canplay","pause","seeked"]) video.addEventListener(type,stopLoadingNotice);
  if(waitsForAutomaticData)video.addEventListener("loadeddata",sync);
  video.addEventListener("emptied",()=>{ stopLoadingNotice(); forgetFrame(); });
  for(const type of ["loadeddata","pause","ended","play","timeupdate","emptied"]) video.addEventListener(type,update);
  function fail() {
    const code=video.error?.code;
    reason.textContent=code===MediaError.MEDIA_ERR_NETWORK?"The video download failed.":
      code===MediaError.MEDIA_ERR_DECODE?"The browser could not read the video.":
      code===MediaError.MEDIA_ERR_SRC_NOT_SUPPORTED?"This browser could not load the video format.":
      fallbackReason;
    error.hidden=false;
    stopLoadingNotice();
    say("");
    forgetFrame();
    video.controls=true;
    manualState="paused";
    console.info("Product recording failed.",video.error);
    update();
  }
  // A failed <source> child reports its error on the source, not the video, so listen while capturing.
  // Only the last source means that no candidate is left.
  video.addEventListener("error",event=>{
    if(event.target instanceof HTMLSourceElement&&event.target!==video.querySelector("source:last-of-type"))return;
    fail();
  },true);
  const observer=new IntersectionObserver(([entry])=>{
    visible=entry.isIntersecting&&entry.intersectionRatio>0;
    autoplayVisible=visible&&entry.intersectionRatio>=.65;
    sync();
  },{threshold:[0,.65]});
  reduced.addEventListener("change",sync);
  document.addEventListener("visibilitychange",sync);
  window.addEventListener("pagehide",()=>{video.pause();observer.disconnect();});
  window.addEventListener("pageshow",event=>{if(event.persisted)observer.observe(video);});
  video.muted=true;
  video.defaultMuted=true;
  // The compact control replaces the browser's own controls as soon as it can work.
  video.controls=false;
  toggle.hidden=false;
  root.dataset.ready="true";
  update();
  if(video.error||(waitsForAutomaticData&&video.networkState===HTMLMediaElement.NETWORK_NO_SOURCE&&
    video.readyState===HTMLMediaElement.HAVE_NOTHING))fail();
  observer.observe(video);

  return {
    selected() {
      manualState="none";
      if(!reduced.matches&&!document.hidden)play();
    },
    suspend() {
      if(!video.paused)video.pause();
    },
    sync,
    seek(seconds) {
      manualState="playing";
      say("");
      const jump=()=>{ video.currentTime=Math.min(Math.max(0,seconds),Math.max(0,(video.duration||seconds)-.05)); };
      if(video.readyState>=HTMLMediaElement.HAVE_METADATA) jump();
      else video.addEventListener("loadedmetadata",jump,{once:true});
      play();
    },
  };
}
