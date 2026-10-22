import {
  ARCH_LABELS, REFERRER_LABELS, ROUTE_LABELS, TRACK_LABELS, addDays, daily, featureUsage, githubDownloads,
  homebrewInstalls, jobHealth, merge, niceMax, ranked, retentionRows, segments, smoothPath, total, utcDay,
} from "/admin/admin-model.js";

const $ = (id) => document.getElementById(id);
const SVG = "http://www.w3.org/2000/svg";
const number = new Intl.NumberFormat();
const percent = new Intl.NumberFormat(undefined, { style: "percent", maximumFractionDigits: 0 });
const shortDate = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", timeZone: "UTC" });
const longDate = new Intl.DateTimeFormat(undefined, { weekday: "short", month: "short", day: "numeric", timeZone: "UTC" });
const time = new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit" });
const dateTime = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
const dayLabel = (day) => shortDate.format(new Date(`${day}T00:00:00Z`));
const format = (value) => (value === null || value === undefined ? "–" : number.format(value));
const plural = (count, one, many) => `${number.format(count)} ${count === 1 ? one : many}`;

let lastData = null;

function view(name) {
  document.body.dataset.view = name;
  $("boot").hidden = name !== "loading";
  $("gate").hidden = name !== "gate";
  $("dashboard").hidden = name !== "dashboard";
  if (name === "gate") $("password").focus();
}

function el(tag, options = {}, children = []) {
  const node = document.createElement(tag);
  if (options.className) node.className = options.className;
  if (options.text !== undefined) node.textContent = options.text;
  for (const [key, value] of Object.entries(options.attrs ?? {})) node.setAttribute(key, value);
  node.append(...children);
  return node;
}

function svg(tag, attrs = {}, parent) {
  const node = document.createElementNS(SVG, tag);
  for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, String(value));
  parent?.append(node);
  return node;
}

function setNumber(node, value) {
  const text = format(value);
  node.classList.toggle("is-empty", value === null || value === undefined);
  if (node.textContent !== text && node.textContent !== "–" && document.body.dataset.drawn !== undefined) {
    node.classList.remove("changed");
    void node.offsetWidth;
    node.classList.add("changed");
  }
  node.textContent = text;
}

function status(message, tone = "info") {
  const line = $("status");
  line.hidden = !message;
  line.textContent = message ?? "";
  line.dataset.tone = tone;
}

/** A labelled bar list. Bars are decorative; the label and value carry the meaning. */
function bars(list, rows, { empty, value = (row) => format(row.value), max } = {}) {
  list.replaceChildren();
  if (!rows.length) {
    list.append(el("li", { className: "empty", text: empty ?? "Nothing yet." }));
    return;
  }
  const peak = max ?? Math.max(...rows.map((row) => row.value), 1);
  rows.forEach((row, index) => {
    const fill = el("span", { className: "bar-fill" });
    fill.style.setProperty("--share", `${Math.max(2, (row.value / peak) * 100)}%`);
    fill.style.setProperty("--order", String(index));
    const valueNode = el("span", { className: "bar-value" });
    const shown = value(row);
    if (Array.isArray(shown)) valueNode.append(shown[0], el("small", { text: shown[1] }));
    else valueNode.textContent = shown;
    list.append(el("li", {}, [
      el("span", { className: "bar-label", text: row.label, attrs: { title: row.label } }),
      el("span", { className: "bar-track", attrs: { "aria-hidden": "true" } }, [fill]),
      valueNode,
    ]));
  });
}

/* ---------- The 30-day ink chart ---------- */

const chart = { points: [], focus: -1, geometry: null };

function drawChart(points) {
  const box = $("active-chart").getBoundingClientRect();
  const width = Math.max(320, Math.round(box.width)), height = Math.max(200, Math.round(box.height));
  const pad = { top: 16, right: 14, bottom: 30, left: 34 };
  const plotW = width - pad.left - pad.right, plotH = height - pad.top - pad.bottom;
  const values = points.map((point) => point.value).filter((value) => value !== null);
  const max = niceMax(Math.max(0, ...values));
  const x = (index) => pad.left + (points.length > 1 ? (index / (points.length - 1)) * plotW : plotW / 2);
  const y = (value) => pad.top + plotH - (value / max) * plotH;
  chart.geometry = { x, y, pad, width, height, plotH };

  const root = $("active-svg");
  root.replaceChildren();
  root.setAttribute("viewBox", `0 0 ${width} ${height}`);
  const defs = svg("defs", {}, root);
  const gradient = svg("linearGradient", { id: "area-fill", x1: 0, y1: 0, x2: 0, y2: 1 }, defs);
  svg("stop", { offset: "0%", "stop-color": "#ffb58c", "stop-opacity": .22 }, gradient);
  svg("stop", { offset: "100%", "stop-color": "#ffb58c", "stop-opacity": 0 }, gradient);

  for (const tick of [0, max / 2, max]) {
    svg("line", { class: "grid-line", x1: pad.left, x2: width - pad.right, y1: y(tick), y2: y(tick) }, root);
    const label = svg("text", { class: "axis-label", x: pad.left - 10, y: y(tick) + 4, "text-anchor": "end" }, root);
    label.textContent = number.format(tick);
  }
  const labelDays = [0, Math.floor((points.length - 1) / 2), points.length - 1];
  for (const index of new Set(labelDays)) {
    const anchor = index === 0 ? "start" : index === points.length - 1 ? "end" : "middle";
    const label = svg("text", { class: "axis-label", x: x(index), y: height - 8, "text-anchor": anchor }, root);
    label.textContent = dayLabel(points[index].day);
  }

  if (!values.length) {
    const note = svg("text", { class: "no-data", x: pad.left + plotW / 2, y: pad.top + plotH * 0.3, "text-anchor": "middle" }, root);
    note.textContent = "No opted-in copies have sent counts in these 30 days.";
  }
  for (const run of segments(points.map((point, index) => ({ ...point, index })))) {
    const coords = run.map((point) => ({ x: x(point.index), y: y(point.value) }));
    const line = smoothPath(coords);
    if (coords.length > 1) {
      const area = `${line}L${coords.at(-1).x},${y(0)}L${coords[0].x},${y(0)}Z`;
      svg("path", { class: "area", d: area }, root);
    }
    svg("path", { class: "ink-line", d: coords.length > 1 ? line : `${line}h0.01`, pathLength: 1 }, root);
  }
  points.forEach((point, index) => {
    if (point.newInstalls > 0) svg("circle", { class: "new-mark", cx: x(index), cy: y(point.value ?? 0), r: 4.5 }, root);
  });
  chart.guide = svg("line", { class: "guide", y1: pad.top, y2: pad.top + plotH, visibility: "hidden" }, root);
  chart.dot = svg("circle", { class: "focus-dot", r: 6, visibility: "hidden" }, root);
  if (chart.focus >= 0) focusDay(chart.focus, false);
}

function focusDay(index, announce = true) {
  const { points, geometry } = chart;
  const tip = $("chart-tip");
  if (index < 0 || !points[index] || !geometry) {
    chart.guide?.setAttribute("visibility", "hidden");
    chart.dot?.setAttribute("visibility", "hidden");
    tip.hidden = true;
    return;
  }
  chart.focus = index;
  const point = points[index];
  const px = geometry.x(index), py = geometry.y(point.value ?? 0);
  chart.guide.setAttribute("x1", px);
  chart.guide.setAttribute("x2", px);
  chart.guide.setAttribute("visibility", "visible");
  chart.dot.setAttribute("cx", px);
  chart.dot.setAttribute("cy", py);
  chart.dot.setAttribute("visibility", point.value === null ? "hidden" : "visible");
  const detail = point.value === null ? "No count stored for this day"
    : `${plural(point.value, "copy", "copies")} active${point.newInstalls ? ` · ${number.format(point.newInstalls)} new` : ""}`;
  const when = longDate.format(new Date(`${point.day}T00:00:00Z`));
  tip.replaceChildren(el("strong", { text: point.value === null ? "–" : plural(point.value, "copy", "copies") }),
    el("span", { text: point.value === null ? `${when} · no count stored` : point.newInstalls ? `${when} · ${number.format(point.newInstalls)} new` : when }));
  const box = $("active-chart").getBoundingClientRect();
  const scale = box.width / geometry.width;
  tip.style.left = `${Math.min(Math.max(px * scale, 80), box.width - 80)}px`;
  tip.style.top = `${py * (box.height / geometry.height)}px`;
  tip.hidden = false;
  if (announce) $("chart-live").textContent = `${longDate.format(new Date(`${point.day}T00:00:00Z`))}: ${detail}.`;
}

function setUpChartInput() {
  const area = $("active-chart");
  area.addEventListener("pointermove", (event) => {
    if (!chart.geometry || !chart.points.length) return;
    const box = area.getBoundingClientRect();
    const local = (event.clientX - box.left) * (chart.geometry.width / box.width);
    const step = (chart.geometry.width - chart.geometry.pad.left - chart.geometry.pad.right) / Math.max(1, chart.points.length - 1);
    focusDay(Math.round(Math.min(Math.max((local - chart.geometry.pad.left) / step, 0), chart.points.length - 1)), false);
  });
  area.addEventListener("pointerleave", () => { if (document.activeElement !== area) focusDay(-1); });
  area.addEventListener("focus", () => focusDay(chart.focus >= 0 ? chart.focus : chart.points.length - 1));
  area.addEventListener("blur", () => focusDay(-1));
  area.addEventListener("keydown", (event) => {
    const moves = { ArrowLeft: -1, ArrowRight: 1, Home: -Infinity, End: Infinity };
    if (!(event.key in moves) || !chart.points.length) return;
    event.preventDefault();
    const next = Math.min(Math.max((chart.focus < 0 ? chart.points.length - 1 : chart.focus) + moves[event.key], 0), chart.points.length - 1);
    focusDay(next);
  });
  new ResizeObserver(() => { if (chart.points.length) drawChart(chart.points); }).observe(area);
}

/** Spans the days that have data. Daily counts scale from zero; a running total scales to its own range. */
function sparkline(node, values, { runningTotal = false } = {}) {
  node.replaceChildren();
  const known = values.map((value, index) => ({ value, index })).filter((point) => point.value !== null);
  if (known.length < 2) return;
  const width = 220, height = 34;
  const numbers = known.map((point) => point.value);
  const low = runningTotal ? Math.min(...numbers) : 0, high = Math.max(...numbers);
  const first = known[0].index, span = Math.max(1, known.at(-1).index - first);
  node.setAttribute("viewBox", `0 0 ${width} ${height}`);
  node.setAttribute("preserveAspectRatio", "none");
  const flat = high === low;
  const coords = known.map((point) => ({
    x: ((point.index - first) / span) * width,
    y: flat ? (runningTotal ? height / 2 : height - 3) : height - 3 - ((point.value - low) / (high - low)) * (height - 6),
  }));
  const line = smoothPath(coords);
  if (!flat) svg("path", { class: "spark-area", d: `${line}L${coords.at(-1).x},${height}L${coords[0].x},${height}Z` }, node);
  svg("path", { d: line, "vector-effect": "non-scaling-stroke" }, node);
}

/* ---------- Sections ---------- */

function renderPeople(summaries, end) {
  const latest = summaries.at(-1);
  const week = summaries.filter((summary) => summary.day > addDays(end, -7));
  const points = daily(summaries, end, 30, (summary) => summary.app.active)
    .map((point) => ({ ...point, newInstalls: summaries.find((summary) => summary.day === point.day)?.app.newInstalls ?? 0 }));
  setNumber($("mau"), latest?.app.mau ?? null);
  $("mau-caption").textContent = `${latest?.app.mau === 1 ? "opted-in copy" : "opted-in copies"} used Annotator in the last 30 days`;
  if (!$("mau").querySelector("svg")) {
    const ring = svg("svg", { class: "ink-ellipse", viewBox: "0 0 300 110", preserveAspectRatio: "none", "aria-hidden": "true", focusable: "false" });
    svg("path", { pathLength: 1, d: "M62 20C124 4 232 6 272 28C299 45 290 82 236 95C170 110 72 106 30 86C3 72 9 38 52 24C92 12 152 8 206 14" }, ring);
    $("mau").append(ring);
  }
  setNumber($("wau"), latest?.app.wau ?? null);
  setNumber($("active"), latest?.app.active ?? null);
  $("active-label").textContent = latest ? `Active on ${dayLabel(latest.day)}` : "Latest day";
  setNumber($("new-installs"), latest ? week.reduce((sum, summary) => sum + summary.app.newInstalls, 0) : null);
  $("chart-range").textContent = `${dayLabel(points[0].day)} – ${dayLabel(points.at(-1).day)} (UTC days)`;
  chart.points = points;
  drawChart(points);
  const table = $("chart-table");
  table.replaceChildren(el("caption", { text: "Opted-in copies active each day" }),
    el("tr", {}, [el("th", { text: "Day" }), el("th", { text: "Active" }), el("th", { text: "New" })]),
    ...points.map((point) => el("tr", {}, [el("td", { text: point.day }), el("td", { text: format(point.value) }),
      el("td", { text: point.value === null ? "–" : number.format(point.newInstalls) })])));
}

function renderTiles(summaries, end, github, githubSnapshots) {
  const week = summaries.filter((summary) => summary.day > addDays(end, -7));
  const month = (pick) => daily(summaries, end, 30, pick).map((point) => point.value);
  const clicks = (summary) => total(summary.web.downloads);
  const has = summaries.length > 0;
  const tile = (id) => ({ value: $(id).querySelector("[data-value]"), spark: $(id).querySelector(".spark") });
  const tiles = { newest: tile("tile-new"), visits: tile("tile-visits"), clicks: tile("tile-clicks"), github: tile("tile-github") };
  setNumber(tiles.newest.value, has ? week.reduce((sum, s) => sum + s.app.newInstalls, 0) : null);
  sparkline(tiles.newest.spark, month((s) => s.app.newInstalls));
  setNumber(tiles.visits.value, has ? week.reduce((sum, s) => sum + s.web.sessions, 0) : null);
  sparkline(tiles.visits.spark, month((s) => s.web.sessions));
  setNumber(tiles.clicks.value, has ? week.reduce((sum, s) => sum + clicks(s), 0) : null);
  sparkline(tiles.clicks.spark, month(clicks));
  setNumber(tiles.github.value, github?.total ?? null);
  const change = github?.change;
  $("tile-github").querySelector("[data-detail]").textContent = github
    ? `All release files, all time${change === null ? "" : change === 0 ? `. No change since ${dayLabel(github.since)}`
      : `. ${change > 0 ? "+" : ""}${number.format(change)} since ${dayLabel(github.since)}`}.`
    : "No download count yet. Press Refresh.";
  const byDay = new Map(githubSnapshots.map((snapshot) => [snapshot.day, snapshot.total]));
  sparkline(tiles.github.spark, Array.from({ length: 30 }, (_, index) => byDay.get(addDays(end, index - 29)) ?? null), { runningTotal: true });
}

function renderDownloads(github, homebrew) {
  $("downloads-note").textContent = github
    ? `GitHub counted ${github.capturedAt ? dateTime.format(new Date(github.capturedAt)) : `on ${dayLabel(github.day)}`}`
    : "Not counted yet";
  const kinds = $("download-kinds");
  kinds.replaceChildren();
  if (github) {
    kinds.append(
      el("div", { className: "kind" }, [el("strong", { text: number.format(github.kinds.dmg) }), el("span", { text: "Disk image" }),
        el("p", { text: "Downloads from the website or the GitHub release page." })]),
      el("div", { className: "kind" }, [el("strong", { text: number.format(github.kinds.zip) }), el("span", { text: "ZIP" }),
        el("p", { text: "Also counts Homebrew installs and in-app updates, which download the same file." })]),
    );
  } else kinds.append(el("p", { className: "empty", text: "The nightly count or Refresh reads GitHub's release numbers." }));
  bars($("releases"), github?.releases ?? [], { empty: "No release downloads counted yet." });

  const box = $("homebrew");
  box.replaceChildren(el("h3", { text: "Homebrew installs" }));
  if (homebrew.status === "reported") {
    box.append(el("div", { className: "counts" }, ["30d", "90d", "365d"].map((period) =>
      el("div", {}, [el("strong", { text: format(homebrew.counts[period] ?? null) }), el("span", { text: `${period.replace("d", "")} days` })]))));
  } else if (homebrew.status === "unreported") {
    box.append(
      el("p", { text: "Not reported yet. Homebrew sends install counts for the homebrew-annotator tap. Macs that added the tap with the older two-line command used a custom address, which Homebrew treats as private, so their installs aren't counted." }),
      el("p", {}, ["Homebrew installs still download the ZIP from GitHub, so they are inside the ZIP count above. Checked ",
        dayLabel(homebrew.day), " in Homebrew's public report for ", el("code", { text: "dorlugasigal/annotator/annotator" }), "."]),
    );
  } else box.append(el("p", { text: "The nightly count hasn't checked Homebrew yet." }));
}

function renderWebsite(week) {
  const routes = {}, referrers = {}, tracks = {};
  for (const summary of week) {
    merge(routes, summary.web.routes);
    merge(referrers, summary.web.referrers);
    merge(tracks, summary.web.downloads);
  }
  bars($("routes"), ranked(routes, ROUTE_LABELS), { empty: "No visits in the last 7 days." });
  bars($("referrers"), ranked(referrers, REFERRER_LABELS), { empty: "No visits in the last 7 days." });
  bars($("tracks"), ranked(tracks, TRACK_LABELS), { empty: "No download clicks in the last 7 days." });
}

function renderApp(latest) {
  $("app-note").textContent = latest ? `${dayLabel(latest.day)} (UTC)` : "Latest day";
  const empty = "No opted-in copy reported that day.";
  bars($("versions"), ranked(latest?.app.versions), { empty });
  bars($("os"), ranked(latest?.app.os), { empty });
  bars($("chips"), ranked(latest?.app.architectures, ARCH_LABELS), { empty });
}

function renderFeatures(week) {
  const usage = featureUsage(week);
  bars($("features"), usage.rows, {
    max: Math.max(usage.activeDays, 1),
    value: (row) => [percent.format(row.share), `${number.format(row.value)} of ${number.format(usage.activeDays)}`],
    empty: usage.activeDays ? "No feature use reported in the last 7 days." : "No opted-in copy was active in the last 7 days.",
  });
}

function renderRetention(summaries) {
  const list = $("retention");
  list.replaceChildren();
  const rows = retentionRows(summaries);
  if (!rows.length) {
    list.append(el("li", { className: "empty", text: "Not enough history yet. The first day-7 result appears a week after a copy first shares counts." }));
    return;
  }
  rows.forEach((row, index) => {
    const fill = el("span", { className: "bar-fill" });
    fill.style.setProperty("--share", `${Math.max(2, row.rate * 100)}%`);
    fill.style.setProperty("--order", String(index));
    list.append(el("li", {}, [
      el("span", { className: "cohort-period" }, [`Day ${row.period}`, el("span", { className: "cohort-when", text: `from ${dayLabel(row.cohort)}` })]),
      el("span", { className: "bar-track", attrs: { "aria-hidden": "true" } }, [fill]),
      el("span", { className: "bar-value" }, [percent.format(row.rate), el("small", { text: `${row.retained} of ${row.size}` })]),
    ]));
  });
}

function renderHealth(record, generatedAt) {
  const health = jobHealth(record, new Date(generatedAt));
  const box = $("health");
  const labels = {
    ok: "Last night's count finished", errors: "Finished with source errors", stale: "The nightly count is late",
    running: "The nightly count is running", stalled: "The nightly count didn't finish", missing: "The nightly count hasn't run yet",
  };
  const facts = el("p", { className: "health-facts" });
  if (health.level !== "missing") {
    facts.append(el("span", {}, [el("b", { text: dateTime.format(new Date(health.at)) }), ` (${Math.round(health.hours)} h ago)`]));
    if (health.aggregated.length) facts.append(el("span", {}, ["Recounted ", el("b", { text: `${dayLabel(health.aggregated[0])} – ${dayLabel(health.aggregated.at(-1))}` })]));
    facts.append(el("span", {}, ["Removed ", el("b", { text: number.format(health.removed) }), " raw records older than 45 days"]));
  }
  box.replaceChildren(el("div", { className: "health-line" }, [
    el("span", { className: "health-state", text: labels[health.level], attrs: { "data-level": health.level } }), facts,
  ]));
  if (health.level === "stale") box.append(el("p", { className: "definition", text: "More than 26 hours since the last finished run. Check the aggregate-daily scheduled function in Netlify." }));
  if (health.level === "stalled") box.append(el("p", { className: "definition", text: "The run started but never finished, often a timeout. The next run recounts the last 9 days." }));
  if (health.errors?.length) box.append(el("ul", { className: "health-errors" }, health.errors.map((error) => el("li", { text: error }))));
  $("health-raw").textContent = record ? JSON.stringify(record, null, 2) : "No run recorded.";
}

function render(data) {
  lastData = data;
  const summaries = data.summaries ?? [];
  const now = new Date(data.generatedAt);
  const end = summaries.at(-1)?.day ?? utcDay(now);
  const latest = summaries.at(-1);
  const week = summaries.filter((summary) => summary.day > addDays(end, -7));
  const github = githubDownloads(data.github ?? []);
  renderPeople(summaries, end);
  renderTiles(summaries, end, github, data.github ?? []);
  renderDownloads(github, homebrewInstalls(data.homebrew ?? []));
  renderWebsite(week);
  renderApp(latest);
  renderFeatures(week);
  renderRetention(summaries);
  renderHealth(data.health, data.generatedAt);
  $("freshness").replaceChildren("Loaded ", el("strong", { text: time.format(now) }),
    latest ? ` · counts to ${dayLabel(latest.day)}` : " · no counts yet");
  document.querySelectorAll(".reveal").forEach((node, index) => node.style.setProperty("--order", String(index)));
}

/* ---------- Loading, sign-in and actions ---------- */

async function load(method = "GET") {
  const refresh = $("refresh");
  if (method === "POST") {
    refresh.disabled = true;
    refresh.dataset.busy = "";
    $("refresh-label").textContent = "Refreshing…";
  }
  try {
    const response = await fetch("/api/admin/summary", { method, credentials: "same-origin" });
    if (response.status === 401) {
      view("gate");
      if (lastData) $("login-error").textContent = "Your session ended. Sign in again.";
      return;
    }
    const body = await response.json();
    if (!response.ok) {
      if ($("dashboard").hidden) { view("gate"); $("login-error").textContent = body.error ?? "Metrics could not load."; }
      else status(body.error ?? "Metrics could not load. Try again.", "warn");
      return;
    }
    const first = $("dashboard").hidden;
    view("dashboard");
    render(body);
    if (first) window.setTimeout(() => { document.body.dataset.drawn = ""; }, 2600);
    if (method === "POST") reportRefresh(body);
    else status("");
  } catch {
    if ($("dashboard").hidden) { view("gate"); $("login-error").textContent = "The service could not be reached. Check the connection and try again."; }
    else status("Metrics could not load. Check the connection and try again.", "warn");
  } finally {
    refresh.disabled = false;
    delete refresh.dataset.busy;
    $("refresh-label").textContent = "Refresh";
  }
}

function reportRefresh(body) {
  const at = time.format(new Date(body.generatedAt));
  const github = body.refresh?.github;
  if (github === "failed") status(`Recounted today at ${at}. GitHub didn't answer (${body.refresh.error}), so downloads are from the last count.`, "warn");
  else if (github === "recent") status(`Recounted today at ${at}. GitHub downloads were read less than 10 minutes ago.`);
  else status(`Recounted today and read GitHub downloads at ${at}.`);
}

$("login").addEventListener("submit", async (event) => {
  event.preventDefault();
  const error = $("login-error");
  error.textContent = "";
  if (!$("password").value) { error.textContent = "Type your passphrase."; return; }
  const button = $("login-button");
  button.disabled = true;
  try {
    const response = await fetch("/api/admin/login", {
      method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ password: $("password").value }),
    });
    if (!response.ok) { error.textContent = (await response.json().catch(() => ({}))).error ?? "Sign-in failed."; return; }
    await load();
  } catch {
    error.textContent = "Sign-in could not connect. Try again.";
  } finally {
    button.disabled = false;
    $("password").value = "";
  }
});

$("refresh").addEventListener("click", () => load("POST"));
$("logout").addEventListener("click", async () => {
  try {
    const response = await fetch("/api/admin/logout", { method: "POST", credentials: "same-origin" });
    if (!response.ok) throw new Error("Sign-out failed.");
    lastData = null;
    delete document.body.dataset.drawn;
    status("");
    view("gate");
  } catch { status("Sign-out could not be confirmed. Try again.", "warn"); }
});

setUpChartInput();
load();
