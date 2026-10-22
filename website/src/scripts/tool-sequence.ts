type Cue = {
  element: Element;
  keyframes: Keyframe[];
  start: number;
  duration: number;
  easing?: string;
};

export class ToolSequence {
  private animations: Animation[] = [];
  private generation = 0;

  constructor(private readonly state: HTMLElement) {}

  stop() {
    this.generation++;
    this.animations.forEach(animation => animation.cancel());
    this.animations = [];
    this.state.dataset.motionRunning = "false";
  }

  play(cues: Cue[]) {
    this.stop();
    const generation = this.generation;
    this.state.dataset.motionRunning = "true";
    this.animations = cues.map(cue => cue.element.animate(cue.keyframes, {
      delay: cue.start, duration: cue.duration, easing: cue.easing ?? "cubic-bezier(.16,1,.3,1)",
      fill: "both",
    }));
    // Only owned cancellations are expected. Other failures remain visible.
    void Promise.all(this.animations.map(animation => animation.finished)).then(() => {
      if (generation === this.generation) this.stop();
    }).catch(error => {
      if (generation !== this.generation) return;
      this.stop();
      console.error("The tool reveal could not finish.", error);
    });
  }
}

export function trace(element: SVGGeometryElement, start: number, duration: number): Cue {
  const length = element.getTotalLength();
  return {
    element, start, duration, easing: "cubic-bezier(.35,0,.4,1)",
    keyframes: [
      { strokeDasharray: `${length} ${length}`, strokeDashoffset: String(length) },
      { strokeDasharray: `${length} ${length}`, strokeDashoffset: "0" },
    ],
  };
}

export function arrive(element: Element, start: number, duration = 220, distance = 14): Cue {
  return { element, start, duration, keyframes: [
    { transform: `translateY(${distance}px)`, opacity: 0 },
    { transform: "translateY(0)", opacity: 1 },
  ] };
}
