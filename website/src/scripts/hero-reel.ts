import {mountPlayer} from "./media-player";

const root=document.querySelector<HTMLElement>("[data-hero-reel]");
if(root){
  const video=root.querySelector<HTMLVideoElement>("[data-reel-video]");
  const poster=root.querySelector<HTMLElement>("[data-reel-poster]");
  const frame=root.querySelector<HTMLElement>("[data-reel-frame]");
  const toggle=root.querySelector<HTMLButtonElement>("[data-reel-toggle]");
  const label=toggle?.querySelector<HTMLElement>("[data-reel-label]");
  const symbol=toggle?.querySelector<HTMLElement>("[data-reel-symbol]");
  const status=root.querySelector<HTMLElement>("[data-reel-status]");
  const error=root.querySelector<HTMLElement>("[data-reel-error]");
  const reason=root.querySelector<HTMLElement>("[data-reel-error-reason]");
  const link=root.querySelector<HTMLAnchorElement>("[data-reel-link]");
  const list=root.querySelector<HTMLElement>("[data-reel-chapters]");
  const now=root.querySelector<HTMLElement>("[data-reel-now]");
  if(!video||!poster||!frame||!toggle||!label||!symbol||!status||!error||!reason||!link||!list||!now)
    throw new Error("The hero reel is incomplete.");
  const reel=video, box=frame, fallback=link, chapterList=list, current=now;
  const player=mountPlayer(root,{video,poster,toggle,label,symbol,status,error,reason},{title:"the reel",rate:1});

  // Sound: the reel always starts muted. The button only changes muted, so playback, pausing and looping
  // keep their own rules, and pausing the reel for any reason also silences it. It shows the real state
  // of the video, so a browser that mutes the reel again (see media-player.ts) is reflected at once.
  const sound=root.querySelector<HTMLButtonElement>("[data-reel-sound]");
  if(sound){
    const icons=[...sound.querySelectorAll<HTMLElement>("[data-sound]")];
    const showSound=()=>{
      const on=!reel.muted;
      sound.setAttribute("aria-pressed",String(on));
      for(const icon of icons)icon.hidden=icon.dataset.sound!==(on?"on":"off");
      root.toggleAttribute("data-sound-on",on);
    };
    sound.addEventListener("click",()=>{ reel.muted=!reel.muted; showSound(); });
    reel.addEventListener("volumechange",showSound);
    sound.hidden=false;
    showSound();
  }

  // Parts of the reel: each bar fills while its part plays. One browser-run animation per bar is started,
  // paused and re-aligned by media events only, so no script runs per frame.
  const reduced=matchMedia("(prefers-reduced-motion: reduce)");
  const parts=[...chapterList.querySelectorAll<HTMLElement>("[data-chapter]")].map(item=>({
    item,
    start:Number(item.dataset.start),
    end:Number(item.dataset.end),
    button:item.querySelector<HTMLButtonElement>("[data-chapter-start]"),
    fill:item.querySelector<HTMLElement>(".reel-chapter-fill"),
    label:item.querySelector<HTMLElement>(".reel-chapter-label")?.textContent?.trim()??"",
  }));
  if(!parts.length||parts.some((part,index)=>!(part.end>part.start)||!part.fill||(index>0&&part.start!==parts[index-1]!.end)))
    throw new Error("The reel chapters are incomplete.");
  let fills: Animation[]=[];
  let boundary: number|undefined;
  let active=-1;

  function time() { return reel.ended?parts.at(-1)!.end:reel.currentTime; }
  function highlight() {
    const at=time();
    const index=parts.findIndex(part=>at>=part.start&&at<part.end);
    const chosen=index===-1?parts.length-1:index;
    if(chosen!==active){
      active=chosen;
      parts.forEach((part,position)=>{
        part.item.toggleAttribute("data-active",position===chosen);
        part.item.toggleAttribute("data-done",position<chosen);
        if(position===chosen)part.button?.setAttribute("aria-current","time");
        else part.button?.removeAttribute("aria-current");
      });
      const name=parts[chosen]!.label;
      current.textContent=name;
      current.hidden=!name;
    }
    // Switch the highlighted part exactly on the next cut while the reel plays.
    window.clearTimeout(boundary);
    if(!reel.paused&&!reel.seeking){
      const next=parts[chosen]!.end;
      const wait=(next-at)/(reel.playbackRate||1)*1000;
      boundary=window.setTimeout(highlight,Math.max(16,wait+8));
    }
  }
  function syncFills() {
    if(reduced.matches){ for(const fill of fills)fill.cancel(); fills=[]; return; }
    if(!fills.length){
      // The id marks these as playback progress: they follow the reel instead of running to an end.
      fills=parts.map(part=>part.fill!.animate([{transform:"scaleX(0)"},{transform:"scaleX(1)"}],
        {id:"reel-chapter",duration:(part.end-part.start)*1000,delay:part.start*1000,fill:"both",easing:"linear"}));
    }
    const at=time()*1000;
    const playing=!reel.paused&&!reel.seeking&&reel.readyState>=HTMLMediaElement.HAVE_FUTURE_DATA;
    fills.forEach((fill,index)=>{
      fill.currentTime=at;
      fill.playbackRate=reel.playbackRate||1;
      // play() would rewind a part that has already ended, so a finished part is held full instead.
      if(playing&&at<parts[index]!.end*1000)fill.play();
      else fill.pause();
    });
  }
  function sync() { syncFills(); highlight(); }
  // A native loop seeks back to the start, which fires seeking and seeked.
  for(const type of ["playing","pause","seeking","seeked","waiting","ended","emptied","ratechange","loadedmetadata"])
    reel.addEventListener(type,sync);
  reel.addEventListener("timeupdate",()=>{
    // The last part's animation runs from the start of the reel to its end, so it tracks the whole loop.
    const shown=Number(fills.at(-1)?.currentTime??0)/1000;
    if(fills.length&&Math.abs(shown-reel.currentTime)>.25)syncFills();
    if(parts[active]&&!(reel.currentTime>=parts[active]!.start&&reel.currentTime<parts[active]!.end))highlight();
  });
  reduced.addEventListener("change",sync);
  for(const part of parts){
    part.button?.addEventListener("click",()=>{
      player.seek(part.start);
      sync();
    });
  }
  chapterList.hidden=false;
  sync();

  // Keep the frame true to the decoded video if a browser ignored the <source media> choice.
  reel.addEventListener("loadedmetadata",()=>{
    const actual=reel.videoWidth/reel.videoHeight;
    const expected=box.clientWidth/box.clientHeight;
    if(Number.isFinite(actual)&&actual>0&&Math.abs(actual-expected)/expected>.02)
      box.style.setProperty("--reel-actual-ratio",String(actual));
    else box.style.removeProperty("--reel-actual-ratio");
  });
  const sources=[...reel.querySelectorAll("source")];
  const wanted=()=>sources.find(source=>!source.media||matchMedia(source.media).matches);
  // The direct link offers the encoding made for this screen, even if a fallback source was tried last.
  reel.addEventListener("error",()=>{const source=wanted();if(source)fallback.href=source.src;},true);

  // Crossing the phone breakpoint (for example rotating a phone) selects the matching encoding.
  const query=root.dataset.phoneMedia;
  if(query){
    matchMedia(query).addEventListener("change",()=>{
      const source=wanted();
      if(!source||!reel.currentSrc||reel.currentSrc===source.src)return;
      reel.load();
      player.sync();
    });
  }
}
export {};
