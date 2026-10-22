type Position = { x: number; y: number };

export function installEditableDemo(root: HTMLElement, stopMotion: () => void) {
  const annotation = root.querySelector<SVGGElement>("[data-editable-annotation]");
  const svg = annotation?.ownerSVGElement;
  const undo = root.querySelector<HTMLButtonElement>("[data-undo-move]");
  const status = root.querySelector<HTMLElement>("[data-move-status]");
  const controls = root.querySelector<HTMLElement>("[data-edit-controls]");
  if (!annotation || !svg || !undo || !status || !controls) throw new Error("Editable demo controls are incomplete.");
  const mark = annotation, canvas = svg, undoButton = undo, message = status;
  let position: Position = { x: 0, y: 0 };
  let previous: Position | undefined;
  let drag: { pointer: number; start: Position; origin: Position; previous?: Position } | undefined;

  function sourcePoint(event: PointerEvent): Position {
    const matrix = canvas.getScreenCTM();
    if (!matrix) throw new Error("The annotation example has no screen transform.");
    const point = new DOMPoint(event.clientX, event.clientY).matrixTransform(matrix.inverse());
    return { x: point.x, y: point.y };
  }

  function place(next: Position) {
    const box = mark.getBBox();
    const bounds = canvas.viewBox.baseVal;
    position = {
      x: Math.max(8 - box.x, Math.min(bounds.width - 8 - box.x - box.width, next.x)),
      y: Math.max(8 - box.y, Math.min(bounds.height - 8 - box.y - box.height, next.y)),
    };
    mark.setAttribute("transform", `translate(${position.x} ${position.y})`);
    mark.dataset.x = String(position.x);
    mark.dataset.y = String(position.y);
  }

  function report() {
    undoButton.disabled = !previous;
    message.textContent = previous ? "Frame moved. Undo is available." :
      position.x === 0 && position.y === 0 ? "Frame in its original position." : "Previous frame position restored.";
  }

  function undoMove() {
    if (!previous) return;
    place(previous); previous = undefined;
    report();
  }

  function cancelDrag() {
    if (!drag) return;
    const cancelled = drag;
    drag = undefined;
    place(cancelled.origin);
    previous = cancelled.previous;
    mark.classList.remove("is-dragging");
    if (mark.hasPointerCapture(cancelled.pointer)) mark.releasePointerCapture(cancelled.pointer);
    report();
  }

  mark.addEventListener("pointerdown", (event) => {
    if (event.button !== 0 || drag) return;
    stopMotion();
    event.preventDefault();
    mark.focus();
    mark.setAttribute("aria-pressed", "true");
    drag = { pointer: event.pointerId, start: sourcePoint(event), origin: { ...position }, previous };
    mark.setPointerCapture(event.pointerId);
    mark.classList.add("is-dragging");
  });
  mark.addEventListener("pointermove", (event) => {
    if (!drag || drag.pointer !== event.pointerId) return;
    const point = sourcePoint(event);
    place({ x: drag.origin.x + point.x - drag.start.x, y: drag.origin.y + point.y - drag.start.y });
  });
  mark.addEventListener("pointerup", (event) => {
    if (!drag || drag.pointer !== event.pointerId) return;
    const finished = drag;
    drag = undefined;
    if (position.x !== finished.origin.x || position.y !== finished.origin.y) previous = finished.origin;
    mark.classList.remove("is-dragging");
    mark.releasePointerCapture(event.pointerId);
    report();
  });
  mark.addEventListener("pointercancel", cancelDrag);
  mark.addEventListener("lostpointercapture", cancelDrag);
  mark.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && drag) { event.preventDefault(); cancelDrag(); return; }
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      const selected = mark.getAttribute("aria-pressed") !== "true";
      mark.setAttribute("aria-pressed", String(selected));
      message.textContent = selected ? "Frame selected. Use arrow keys to move it." : "Frame deselected.";
      return;
    }
    if (event.key === "Escape") { mark.setAttribute("aria-pressed", "false"); return; }
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "z") {
      event.preventDefault(); undoMove(); return;
    }
    if (event.altKey || event.metaKey || event.ctrlKey) return;
    const directions: Record<string, Position> = {
      ArrowLeft: { x: -1, y: 0 }, ArrowRight: { x: 1, y: 0 },
      ArrowUp: { x: 0, y: -1 }, ArrowDown: { x: 0, y: 1 },
    };
    const direction = directions[event.key];
    if (!direction) return;
    event.preventDefault();
    stopMotion();
    mark.setAttribute("aria-pressed", "true");
    previous = { ...position };
    const step = event.shiftKey ? 10 : 1;
    place({ x: position.x + direction.x * step, y: position.y + direction.y * step });
    report();
  });
  undoButton.addEventListener("click", undoMove);
  mark.setAttribute("role", "button");
  mark.setAttribute("aria-label", "Move the annotation frame");
  mark.setAttribute("aria-describedby", "demo-edit-help");
  mark.setAttribute("tabindex", "0");
  mark.setAttribute("aria-pressed", "false");
  controls.hidden = false;
  place(position);

  return {
    reset() { cancelDrag(); position = { x: 0, y: 0 }; place(position); previous = undefined; mark.setAttribute("aria-pressed", "false"); report(); },
    modeChanged(draw: boolean) {
      cancelDrag();
      mark.setAttribute("tabindex", draw ? "0" : "-1");
      mark.setAttribute("aria-hidden", String(!draw));
    },
  };
}
