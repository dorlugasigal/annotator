// The sample app on the hero stage: a small weekly report that works. Its numbers are made up and nothing leaves
// the page. In Use apps mode the visitor can change the week, choose what the chart shows and read each day; in
// Draw mode it does not react, because the pointer is the tool.

const DAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
// For each metric: the chart's title, what one and many are called, and the range the chart's height stands for.
const METRICS = {
  visitors: { title: "Visitors per day", names: ["visitor", "visitors"], range: [1400, 2000] },
  orders: { title: "Orders per day", names: ["order", "orders"], range: [8, 18] },
  signups: { title: "Sign-ups per day", names: ["sign-up", "sign-ups"], range: [0, 70] },
};
// Each week: its seven days for every metric, and the week before it, which is the dashed line and what the
// percentages compare with.
const WEEKS = [
  {
    name: "Week 45", dates: "2 to 8 November",
    notes: ["The autumn campaign started on Monday.", "Two blog posts went out this week."],
    visitors: [[1720, 1800, 1760, 1740, 1700, 1550, 1610], [1690, 1750, 1720, 1700, 1650, 1500, 1524]],
    orders: [[13, 13, 14, 13, 12, 12, 13], [13, 14, 13, 13, 13, 12, 13]],
    signups: [[51, 53, 55, 54, 56, 53, 57], [50, 52, 54, 53, 55, 52, 56]],
  },
  {
    name: "Week 46", dates: "9 to 15 November",
    notes: ["The pricing page got a new headline.", "Nothing new shipped this week."],
    visitors: [[1740, 1820, 1790, 1750, 1720, 1560, 1620], [1720, 1800, 1760, 1740, 1700, 1550, 1610]],
    orders: [[13, 14, 14, 15, 13, 12, 13], [13, 13, 14, 13, 12, 12, 13]],
    signups: [[54, 56, 57, 59, 58, 57, 61], [51, 53, 55, 54, 56, 53, 57]],
  },
  {
    name: "Week 47", dates: "16 to 22 November",
    notes: ["The newsletter went out on Tuesday.", "The new sign-up form shipped on Thursday."],
    visitors: [[1810, 1920, 1880, 1760, 1790, 1640, 1680], [1740, 1820, 1790, 1750, 1720, 1560, 1620]],
    orders: [[15, 16, 14, 11, 14, 13, 13], [13, 14, 14, 15, 13, 12, 13]],
    signups: [[52, 58, 54, 21, 44, 43, 46], [54, 56, 57, 59, 58, 57, 61]],
  },
];
const LAST = WEEKS.length - 1;

// The chart's drawing area in its viewBox (420 by 160).
const X0 = 24, STEP = 62, TOP = 28, BOTTOM = 136, VIEW_W = 420, VIEW_H = 160;
const sum = (list) => list.reduce((a, b) => a + b, 0);
const round = (n) => Math.round(n * 10) / 10;
const heights = (values, [lo, hi]) => values.map((v) => BOTTOM - ((v - lo) / (hi - lo)) * (BOTTOM - TOP));
// A smooth line through the seven days, level at each of them.
function curve(ys) {
  let d = `M${X0} ${round(ys[0])}`;
  for (let i = 1; i < ys.length; i += 1) {
    const x = X0 + STEP * i;
    d += `C${x - STEP + 25} ${round(ys[i - 1])} ${x - 25} ${round(ys[i])} ${x} ${round(ys[i])}`;
  }
  return d;
}
// The app's "standard" curve (0.3, 0, 0.2, 1), for a change that could be turned round.
function standard(x) {
  const at = (t, a, b) => 3 * (1 - t) * (1 - t) * t * a + 3 * (1 - t) * t * t * b + t * t * t;
  let lo = 0, hi = 1;
  for (let i = 0; i < 24; i += 1) { const mid = (lo + hi) / 2; if (at(mid, .3, .2) < x) lo = mid; else hi = mid; }
  return at((lo + hi) / 2, 0, 1);
}

export function createReport(root, { animate = true, canUse = () => true, onChange = () => {}, onUse = () => {} } = {}) {
  const $ = (selector) => root.querySelector(selector), $$ = (selector) => [...root.querySelectorAll(selector)];
  const prev = $("#week-prev"), next = $("#week-next"), name = $("#week-name"), dates = $("#week-dates"), notes = $("#notes");
  const tabs = $$(".metric"), title = $("#chart-title"), plot = $("#plot"), tip = $("#tip"), points = $$(".point");
  const line = $("#chart-line"), area = $("#chart-area"), before = $("#chart-last");
  const state = { week: LAST, metric: "signups" };
  let shown = null, frame = 0, tipFor = -1;

  const week = () => WEEKS[state.week];
  const text = (metric = state.metric) => {
    const w = week(), [days] = w[metric];
    return `${w.name}, ${w.dates}. ${sum(days).toLocaleString("en-US")} ${METRICS[metric].names[1]}.`;
  };

  function paint(ys, last) {
    line.setAttribute("d", curve(ys));
    area.setAttribute("d", `${curve(ys)}V${BOTTOM}H${X0}Z`);
    before.setAttribute("d", curve(last));
    points.forEach((point, i) => {
      point.style.left = `${round(((X0 + STEP * i) / VIEW_W) * 100)}%`;
      point.style.top = `${round((ys[i] / VIEW_H) * 100)}%`;
    });
    shown = { ys, last };
  }

  function render(moving = animate) {
    const w = week(), metric = METRICS[state.metric];
    name.textContent = w.name;
    dates.textContent = w.dates;
    // At an end of the weeks a button is marked as unavailable but keeps the keyboard's place.
    prev.setAttribute("aria-disabled", String(state.week === 0));
    next.setAttribute("aria-disabled", String(state.week === LAST));
    notes.replaceChildren(...w.notes.map((note) => Object.assign(document.createElement("li"), { textContent: note })));
    for (const tab of tabs) {
      const [days, earlier] = w[tab.dataset.metric], change = Math.round((sum(days) / sum(earlier) - 1) * 100);
      const delta = tab.querySelector(".delta");
      tab.setAttribute("aria-pressed", String(tab.dataset.metric === state.metric));
      tab.querySelector(".num").textContent = sum(days).toLocaleString("en-US");
      delta.textContent = `${change < 0 ? "\u2212" : "+"}${Math.abs(change)}%`;
      delta.className = `delta ${change < 0 ? "down" : "up"}`;
    }
    title.textContent = metric.title;
    const [days, earlier] = w[state.metric];
    points.forEach((point, i) => point.setAttribute("aria-label", `${DAYS[i]}: ${days[i].toLocaleString("en-US")} ${metric.names[days[i] === 1 ? 0 : 1]}`));
    if (tipFor >= 0) showTip(tipFor);

    const to = { ys: heights(days, metric.range), last: heights(earlier, metric.range) }, from = shown;
    cancelAnimationFrame(frame);
    if (!moving || !from) { paint(to.ys, to.last); return; }
    const start = performance.now(), blend = (a, b, k) => a.map((v, i) => v + (b[i] - v) * k);
    const tick = (now) => {
      const k = standard(Math.min(1, (now - start) / 480));
      paint(blend(from.ys, to.ys, k), blend(from.last, to.last, k));
      if (k < 1) frame = requestAnimationFrame(tick);
    };
    frame = requestAnimationFrame(tick);
  }

  // A day's number, in a label over its point.
  function showTip(i) {
    const [days] = week()[state.metric], metric = METRICS[state.metric];
    tipFor = i;
    tip.textContent = `${DAYS[i].slice(0, 3)}: ${days[i].toLocaleString("en-US")} ${metric.names[days[i] === 1 ? 0 : 1]}`;
    tip.hidden = false;
    tip.dataset.side = i < 2 ? "start" : i > 4 ? "end" : "middle";
    tip.style.left = points[i].style.left;
    tip.style.top = points[i].style.top;
    points.forEach((point, n) => point.classList.toggle("is-shown", n === i));
  }
  function hideTip() {
    tipFor = -1;
    tip.hidden = true;
    points.forEach((point) => point.classList.remove("is-shown"));
  }

  // What the page's own script calls. They change the app without counting as the visitor using it.
  function go(step) {
    const to = Math.min(LAST, Math.max(0, state.week + step));
    if (to === state.week) return false;
    state.week = to;
    render();
    onChange();
    return true;
  }
  function show(metric) {
    if (!METRICS[metric] || metric === state.metric) return false;
    state.metric = metric;
    render();
    onChange();
    return true;
  }
  function reset(moving) {
    if (state.week === LAST && state.metric === "signups") return;
    state.week = LAST;
    state.metric = "signups";
    hideTip();
    render(moving);
  }

  // What the visitor does. A press that a script made (not trusted) changes the app the same way, without being
  // reported as the visitor's. onChange hears of every change, onUse only of the visitor's.
  const used = (e, changed) => { if (changed && e.isTrusted) onUse(text()); };
  prev.addEventListener("click", (e) => { if (canUse(e)) used(e, go(-1)); });
  next.addEventListener("click", (e) => { if (canUse(e)) used(e, go(1)); });
  for (const tab of tabs) tab.addEventListener("click", (e) => { if (canUse(e)) used(e, show(tab.dataset.metric)); });
  points.forEach((point, i) => {
    point.addEventListener("pointerenter", (e) => { if (canUse(e)) showTip(i); });
    point.addEventListener("pointerleave", () => { if (document.activeElement !== point) hideTip(); });
    point.addEventListener("focus", (e) => { if (canUse(e)) showTip(i); });
    point.addEventListener("blur", hideTip);
    point.addEventListener("click", (e) => { if (canUse(e)) { showTip(i); if (e.isTrusted) onUse(""); } });
    // The seven days are one stop for Tab; the arrow keys go from day to day.
    point.addEventListener("keydown", (e) => {
      const to = e.key === "ArrowRight" ? i + 1 : e.key === "ArrowLeft" ? i - 1 : e.key === "Home" ? 0 : e.key === "End" ? points.length - 1 : null;
      if (to === null || !canUse(e)) return;
      e.preventDefault();
      const target = points[Math.min(points.length - 1, Math.max(0, to))];
      points.forEach((other) => { other.tabIndex = other === target ? 0 : -1; });
      target.focus({ preventScroll: true });
    });
  });

  render(false);
  return { state, go, show, reset, hideTip, text, controls: { prev, next, tabs, points } };
}
