// The sample apps on the stage: a weekly report whose week can be changed, a chart whose points say their numbers,
// a chat with a reply box, and windows that move by their title bars. The data is made up and nothing leaves the page.
const $ = (selector, scope = document) => scope.querySelector(selector);
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));

const WEEKS = [
  { name: "Week 45", dates: "2 to 8 November", visitors: ["11,870", "−2%"], orders: ["91", "−1%"], signups: ["379", "+3%"], days: [51, 55, 54, 57, 53, 52, 57], before: [50, 52, 53, 55, 52, 51, 54] },
  { name: "Week 46", dates: "9 to 15 November", visitors: ["12,004", "+1%"], orders: ["94", "+3%"], signups: ["402", "+6%"], days: [55, 58, 57, 60, 56, 57, 59] },
  { name: "Week 47", dates: "16 to 22 November", visitors: ["12,480", "+4%"], orders: ["96", "+2%"], signups: ["318", "−21%"], days: [50, 54, 51, 22, 43, 48, 50] },
];
const DAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"], XS = [24, 86, 148, 210, 272, 334, 396];
const yOf = (value) => 136 - (value / 70) * 108;
const curve = (values) => values.map((value, i) => (i ? `C${XS[i - 1] + 21} ${yOf(values[i - 1]).toFixed(1)} ${XS[i] - 21} ${yOf(value).toFixed(1)} ${XS[i]} ${yOf(value).toFixed(1)}` : `M${XS[0]} ${yOf(value).toFixed(1)}`)).join("");

export function createApps({ board, onUse }) {
  const windows = $("#windows", board), report = $("#win-report", board), chat = $("#win-chat", board);
  const thread = $("#thread", chat), form = $("#reply", chat), input = $("#reply-text", chat), points = $("#points", report), dip = $("#dip", report);
  const firstThread = thread.innerHTML, last = WEEKS.length - 1;
  let week = last;

  // The report --------------------------------------------------------------------------------------
  const cells = { visitors: ["#m-visitors", "#d-visitors"], orders: ["#m-orders", "#d-orders"], signups: ["#ring-target", "#d-signups"] };
  const buttons = DAYS.map((day) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "point";
    button.innerHTML = '<i></i><span role="tooltip"></span>';
    button.dataset.day = day;
    points.append(button);
    return button;
  });
  function showWeek(index, quiet) {
    week = clamp(index, 0, last);
    const data = WEEKS[week], lowest = data.days.indexOf(Math.min(...data.days));
    $("#week-name", report).textContent = data.name;
    $("#week-dates", report).textContent = data.dates;
    $("#week-prev", report).disabled = week === 0;
    $("#week-next", report).disabled = week === last;
    for (const key in cells) {
      const [number, change] = cells[key].map((id) => $(id, report));
      number.textContent = data[key][0];
      change.textContent = data[key][1];
      change.className = `delta ${data[key][1].startsWith("+") ? "up" : "down"}`;
    }
    const line = curve(data.days);
    $("#chart-line", report).setAttribute("d", line);
    $("#chart-area", report).setAttribute("d", `${line}V136H${XS[0]}Z`);
    $("#chart-last", report).setAttribute("d", curve(week ? WEEKS[week - 1].days : data.before));
    buttons.forEach((button, i) => {
      const value = data.days[i];
      button.style.left = `${(XS[i] / 420) * 100}%`;
      button.style.top = `${(yOf(value) / 160) * 100}%`;
      button.classList.toggle("is-low", i === lowest && value < 40);
      button.setAttribute("aria-label", `${button.dataset.day}: ${value} sign-ups`);
      button.lastElementChild.textContent = `${button.dataset.day.slice(0, 3)} ${value}`;
    });
    dip.style.left = `${(XS[lowest] / 420) * 100}%`;
    dip.style.top = `${(yOf(data.days[lowest]) / 160) * 100}%`;
    if (!quiet) {
      // The numbers that changed blink once, so the change is seen.
      report.classList.remove("is-changed");
      void report.offsetWidth;
      report.classList.add("is-changed");
    }
  }
  $("#week-prev", report).addEventListener("click", () => showWeek(week - 1));
  $("#week-next", report).addEventListener("click", () => showWeek(week + 1));

  // The chat ----------------------------------------------------------------------------------------
  function send(text = input.value) {
    const words = text.trim();
    if (!words) return;
    const item = document.createElement("li");
    item.className = "mine";
    item.append(Object.assign(document.createElement("b"), { textContent: "You" }), Object.assign(document.createElement("span"), { textContent: words }));
    thread.append(item);
    while (thread.children.length > 12) thread.firstElementChild.remove();
    thread.scrollTop = thread.scrollHeight;
    input.value = "";
  }
  form.addEventListener("submit", (e) => { e.preventDefault(); send(); });

  // Windows that move --------------------------------------------------------------------------------
  // A window is moved with `translate`, so nothing else on the stage changes its layout. `shift` is how far.
  const shifts = new Map();
  let top = 2;
  const shiftOf = (node) => shifts.get(node) || [0, 0];
  // How far a grown stage's frame reaches past the board on each side (main.js hands this in once the stage exists).
  let room = () => ({ top: 0, right: 0, bottom: 0, left: 0 });
  function limits(win) {
    let x = 0, y = 0;
    for (let node = win; node && node !== board; node = node.offsetParent) { x += node.offsetLeft; y += node.offsetTop; }
    const r = room();
    return { x: [8 - r.left - x, board.clientWidth + r.right - win.offsetWidth - x - 8], y: [8 - r.top - y, board.clientHeight + r.bottom - win.offsetHeight - y - 8] };
  }
  function moveTo(win, dx, dy) {
    const room = limits(win), x = clamp(dx, Math.min(0, room.x[0]), Math.max(0, room.x[1])), y = clamp(dy, Math.min(0, room.y[0]), Math.max(0, room.y[1]));
    shifts.set(win, [x, y]);
    win.style.translate = x || y ? `${x}px ${y}px` : "";
    return [x, y];
  }
  const raise = (win) => { win.style.zIndex = String(++top); };
  for (const bar of windows.querySelectorAll("[data-drag]")) {
    const win = bar.closest(".window");
    let grab = null;
    bar.addEventListener("pointerdown", (e) => {
      if (e.button !== 0 || !movable()) return;
      const [x, y] = shiftOf(win);
      grab = { id: e.pointerId, x: e.clientX - x, y: e.clientY - y };
      try { bar.setPointerCapture(e.pointerId); } catch { /* a scripted press has no pointer to hold */ }
      raise(win);
      win.classList.add("is-moving");
      e.preventDefault();
    });
    bar.addEventListener("pointermove", (e) => { if (grab?.id === e.pointerId) moveTo(win, e.clientX - grab.x, e.clientY - grab.y); });
    const drop = (e) => { if (grab?.id === e.pointerId) { grab = null; win.classList.remove("is-moving"); } };
    bar.addEventListener("pointerup", drop);
    bar.addEventListener("pointercancel", drop);
  }
  // With one window on the stage there is nowhere to move it to.
  const chatShown = () => chat.offsetParent !== null;
  const movable = () => chatShown();
  addEventListener("resize", () => { for (const win of shifts.keys()) moveTo(win, ...shiftOf(win)); });

  // The visitor using an app is theirs to do: the page hears about it and steps back.
  for (const name of ["pointerdown", "keydown"]) windows.addEventListener(name, (e) => { if (e.isTrusted) onUse(e); }, true);

  showWeek(last, true);
  return {
    chat, input, shiftOf, moveTo, raise, chatShown,
    // A grown stage gives the windows the whole frame; as it shrinks, `refit` gathers them back in.
    setRoom(get) { room = get; },
    refit() { for (const win of shifts.keys()) moveTo(win, ...shiftOf(win)); },
    previous: $("#week-prev", report), next: $("#week-next", report), sendButton: $("#reply-send", chat), chatBar: $("[data-drag]", chat),
    // In Draw the apps are out of reach, for the keyboard too.
    setReachable(on) { windows.inert = !on; },
    reset() {
      showWeek(last, true);
      report.classList.remove("is-changed");
      thread.innerHTML = firstThread;
      input.value = "";
      for (const win of [...shifts.keys()]) {
        // A window that was moved glides home instead of jumping.
        win.classList.add("is-returning");
        moveTo(win, 0, 0);
        setTimeout(() => win.classList.remove("is-returning"), 260);
      }
    },
  };
}
