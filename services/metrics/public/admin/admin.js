import {
  DOWNLOAD_LABELS, PLATFORMS, PLATFORM_LABELS, compare, jobHealth, niceMax, overview, segments, smoothPath,
} from "/admin/admin-model.js";

const $ = (id) => document.getElementById(id);
const SVG = "http://www.w3.org/2000/svg";
const number = new Intl.NumberFormat();
const decimal = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 });
const percent = new Intl.NumberFormat(undefined, { style: "percent", maximumFractionDigits: 0 });
const shortDate = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", timeZone: "UTC" });
const longDate = new Intl.DateTimeFormat(undefined, { weekday: "short", month: "short", day: "numeric", timeZone: "UTC" });
const time = new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit" });
const dateTime = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
const dayLabel = (day) => shortDate.format(new Date(`${day}T00:00:00Z`));
const rangeLabel = (days) => `${dayLabel(days[0])} – ${dayLabel(days.at(-1))}`;
const format = (value, formatter = number) => (value === null || value === undefined ? "–" : formatter.format(value));
const plural = (count, one, many) => `${number.format(count)} ${count === 1 ? one : many}`;
const DOWNLOAD_NOTES = {
  macDiskImage: "From the website or the GitHub release page.",
  macZip: "Homebrew installs and in-app updates download this file.",
  windowsZip: "winget installs and direct downloads. Microsoft Store installs aren't counted here.",
};

let lastData = null;
let periodLength = /^#7-days$/.test(location.hash) ? 7 : 30;

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

function setNumber(node, value, formatter = number) {
  const text = format(value, formatter);
  node.classList.toggle("is-empty", value === null || value === undefined);
  if (node.firstChild?.nodeType === Node.TEXT_NODE && node.firstChild.textContent !== text && node.firstChild.textContent !== "–"
    && document.body.dataset.drawn !== undefined) {
    node.classList.remove("changed");
    void node.offsetWidth;
    node.classList.add("changed");
  }
  const ring = node.querySelector(":scope > .ink-ellipse");
  node.replaceChildren(text, ...(ring ? [ring] : []));
}

function status(message, tone = "info") {
  const line = $("status");
  line.hidden = !message;
  line.textContent = message ?? "";
  line.dataset.tone = tone;
}

/**
 * "↑ 25% · was 4". The arrow is decorative and hidden from screen readers, which hear "Up" or "Down".
 * Small earlier values show the difference instead of a percentage, so one more copy never reads as +100%.
 * The direction follows the numbers as shown, so "37%" never sits next to "down, was 37%".
 */
function showChange(node, change, { formatter = number, share = false } = {}) {
  // Nothing then and nothing now isn't news.
  node.hidden = !change || (change.previous === 0 && change.diff === 0);
  if (node.hidden) {
    node.dataset.trend = "";
    node.replaceChildren();
    return;
  }
  const shown = share ? percent : formatter;
  const was = shown.format(change.previous);
  const trend = shown.format(change.previous + change.diff) === was ? "same" : change.trend;
  node.dataset.trend = trend;
  if (trend === "same") {
    node.replaceChildren(`No change · was ${was}`);
    return;
  }
  const amount = share ? "" : change.ratio !== null && change.previous >= 10
    ? `${percent.format(Math.abs(change.ratio))} · ` : `${formatter.format(Math.abs(change.diff))} · `;
  node.replaceChildren(
    el("span", { className: "arrow", text: trend === "up" ? "↑ " : "↓ ", attrs: { "aria-hidden": "true" } }),
    el("span", { className: "sr-only", text: trend === "up" ? "Up, " : "Down, " }),
    `${amount}was ${was}`,
  );
}

function changeNode(change, options, tag = "span") {
  const node = el(tag, { className: "delta" });
  showChange(node, change, options);
  return node;
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
    fill.style.setProperty("--share", `${row.value > 0 ? Math.max(2, (row.value / peak) * 100) : 0}%`);
    fill.style.setProperty("--order", String(index));
    const valueNode = el("span", { className: "bar-value" });
    const shown = value(row);
    if (Array.isArray(shown)) valueNode.append(shown[0], typeof shown[1] === "string" ? el("small", { text: shown[1] }) : shown[1]);
    else valueNode.textContent = shown;
    const label = el("span", { className: "bar-label", text: row.label, attrs: { title: row.label } });
    if (row.tag) label.append(" ", el("span", { className: "tag", text: row.tag }));
    list.append(el("li", {}, [label, el("span", { className: "bar-track", attrs: { "aria-hidden": "true" } }, [fill]), valueNode]));
  });
}

/* ---------- The daily ink chart ---------- */

const chart = { points: [], focus: -1, geometry: null };

function drawChart(points) {
  const box = $("active-chart").getBoundingClientRect();
  const width = Math.max(320, Math.round(box.width)), height = Math.max(200, Math.round(box.height));
  const pad = { top: 16, right: 14, bottom: 30, left: 34 };
  const plotW = width - pad.left - pad.right, plotH = height - pad.top - pad.bottom;
  const values = points.flatMap((point) => [point.value, point.previous]).filter((value) => value !== null);
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
  for (const index of new Set([0, Math.floor((points.length - 1) / 2), points.length - 1])) {
    const anchor = index === 0 ? "start" : index === points.length - 1 ? "end" : "middle";
    const label = svg("text", { class: "axis-label", x: x(index), y: height - 8, "text-anchor": anchor }, root);
    label.textContent = dayLabel(points[index].day);
  }

  if (!points.some((point) => point.value !== null)) {
    const note = svg("text", { class: "no-data", x: pad.left + plotW / 2, y: pad.top + plotH * 0.3, "text-anchor": "middle" }, root);
    note.textContent = `No counts in these ${points.length} days.`;
  }
  const line = (pick) => segments(points.map((point, index) => ({ value: pick(point), index })))
    .map((run) => run.map((point) => ({ x: x(point.index), y: y(point.value) })));
  for (const coords of line((point) => point.previous)) {
    svg("path", { class: "previous-line", d: coords.length > 1 ? smoothPath(coords) : `${smoothPath(coords)}h0.01` }, root);
  }
  for (const coords of line((point) => point.value)) {
    const path = smoothPath(coords);
    if (coords.length > 1) svg("path", { class: "area", d: `${path}L${coords.at(-1).x},${y(0)}L${coords[0].x},${y(0)}Z` }, root);
    svg("path", { class: "ink-line", d: coords.length > 1 ? path : `${path}h0.01`, pathLength: 1 }, root);
  }
  points.forEach((point, index) => {
    if (point.newInstalls > 0) svg("circle", { class: "new-mark", cx: x(index), cy: y(point.value ?? 0), r: 4.5 }, root);
  });
  chart.guide = svg("line", { class: "guide", y1: pad.top, y2: pad.top + plotH, visibility: "hidden" }, root);
  chart.dot = svg("circle", { class: "focus-dot", r: 6, visibility: "hidden" }, root);
  if (chart.focus >= 0) focusDay(chart.focus, false);
}

function describeDay(point) {
  const when = longDate.format(new Date(`${point.day}T00:00:00Z`));
  const extras = [point.newInstalls ? `${number.format(point.newInstalls)} new` : "", point.windows ? `${number.format(point.windows)} on Windows` : ""]
    .filter(Boolean);
  const before = `${dayLabel(point.previousDay)}: ${point.previous === null ? "no count" : plural(point.previous, "copy", "copies")}`;
  return { when, extras, before };
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
  const { when, extras, before } = describeDay(point);
  tip.replaceChildren(el("strong", { text: point.value === null ? "–" : plural(point.value, "copy", "copies") }),
    el("span", { text: [when, ...(point.value === null ? ["no count stored"] : extras)].join(" · ") }),
    el("span", { className: "tip-before", text: before }));
  const box = $("active-chart").getBoundingClientRect();
  tip.style.left = `${Math.min(Math.max(px * (box.width / geometry.width), 80), box.width - 80)}px`;
  tip.style.top = `${py * (box.height / geometry.height)}px`;
  tip.hidden = false;
  if (announce) {
    const detail = point.value === null ? "No count stored for this day"
      : [`${plural(point.value, "copy", "copies")} active`, ...extras].join(", ");
    $("chart-live").textContent = `${when}: ${detail}. ${before}.`;
  }
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

/** Spans the days that have data, scaled from zero. */
function sparkline(node, values) {
  node.replaceChildren();
  const known = values.map((value, index) => ({ value, index })).filter((point) => point.value !== null);
  if (known.length < 2) return;
  const width = 220, height = 34;
  const high = Math.max(...known.map((point) => point.value));
  const first = known[0].index, span = Math.max(1, known.at(-1).index - first);
  node.setAttribute("viewBox", `0 0 ${width} ${height}`);
  node.setAttribute("preserveAspectRatio", "none");
  const coords = known.map((point) => ({
    x: ((point.index - first) / span) * width,
    y: high === 0 ? height - 3 : height - 3 - (point.value / high) * (height - 6),
  }));
  const line = smoothPath(coords);
  if (high > 0) svg("path", { class: "spark-area", d: `${line}L${coords.at(-1).x},${height}L${coords[0].x},${height}Z` }, node);
  svg("path", { d: line, "vector-effect": "non-scaling-stroke" }, node);
}

/* ---------- Sections ---------- */

function renderRange(data) {
  const { period: span, comparable, covered, hasData, stillCounting } = data;
  $("range-text").replaceChildren(el("strong", { text: rangeLabel(span.days) }),
    comparable ? `, compared with ${rangeLabel(span.previous)}.` : ".",
    " Whole days in UTC. Today isn't over yet, so it's shown on its own.");
  const notes = [];
  if (!hasData) {
    notes.push("No counts yet. App numbers appear after a copy turns on Share usage counts and the nightly count runs at 00:15 UTC. You can also press Refresh.");
  } else if (!covered) notes.push(`No counts in these ${span.length} days.`);
  else if (covered < span.length) {
    notes.push(`Only ${covered} of these ${span.length} days have counts. Totals leave out the missing days, so nothing is compared.`);
  } else if (!comparable) notes.push(`There aren't ${span.length} counted days before this period yet, so nothing is compared.`);
  if (stillCounting) notes.push(`${dayLabel(span.end)} is still being counted. The nightly count finishes it at 00:15 UTC, or press Refresh.`);
  $("range-note").hidden = !notes.length;
  $("range-note").textContent = notes.join(" ");
  for (const node of document.querySelectorAll("[data-range]")) node.textContent = rangeLabel(span.days);
  for (const input of document.querySelectorAll('input[name="period"]')) input.checked = Number(input.value) === span.length;
}

function renderPeople(data) {
  const { people, platforms, period: span, asOf } = data;
  const copies = $("copies");
  if (!copies.querySelector(".ink-ellipse")) {
    const ring = svg("svg", { class: "ink-ellipse", viewBox: "0 0 300 110", preserveAspectRatio: "none", "aria-hidden": "true", focusable: "false" });
    svg("path", { pathLength: 1, d: "M62 20C124 4 232 6 272 28C299 45 290 82 236 95C170 110 72 106 30 86C3 72 9 38 52 24C92 12 152 8 206 14" }, ring);
    copies.append(ring);
  }
  setNumber(copies, people.copies.value);
  showChange($("copies-change"), people.copies.change);
  const days = asOf && asOf !== span.end ? `in the ${span.length} days to ${dayLabel(asOf)}` : `in these ${span.length} days`;
  $("copies-caption").textContent = `${people.copies.value === 1 ? "opted-in copy" : "opted-in copies"} used Annotator ${days}`;
  setNumber($("copies-mac"), platforms.macos.copies.value);
  setNumber($("copies-windows"), platforms.windows.copies.value);
  setNumber($("per-day"), people.perDay.value, decimal);
  showChange($("per-day-change"), people.perDay.change, { formatter: decimal });
  setNumber($("today"), people.today?.active ?? null);
  $("today-note").textContent = people.today ? `counted ${time.format(new Date(people.today.countedAt))}` : "Press Refresh";
  $("key-now").textContent = `These ${span.length} days`;
  $("key-before").textContent = `The ${span.length} days before`;
  $("active-chart").setAttribute("aria-label", `Opted-in copies active each day in these ${span.length} days, with the ${span.length} days `
    + "before as a dashed line. Use the arrow keys to read each day.");
  if (chart.points.length !== data.chart.length) chart.focus = -1;
  chart.points = data.chart;
  drawChart(data.chart);
  $("chart-table").replaceChildren(el("caption", { text: "Opted-in copies active each day" }),
    el("tr", {}, ["Day", "Active", "New", "On Windows", "Day before", "Active then"].map((text) => el("th", { text }))),
    ...data.chart.map((point) => el("tr", {}, [point.day, format(point.value), point.value === null ? "–" : number.format(point.newInstalls),
      point.value === null ? "–" : number.format(point.windows), point.previousDay, format(point.previous)].map((text) => el("td", { text })))));
}

function renderFunnel(data) {
  const { funnel, github } = data;
  const step = (id, metric, extra = []) => {
    const node = $(id);
    setNumber(node.querySelector("[data-value]"), metric.value);
    showChange(node.querySelector("[data-change]"), metric.change);
    node.querySelector("[data-extra]").replaceChildren(...extra);
    sparkline(node.querySelector(".spark"), metric.daily);
  };
  step("step-visits", funnel.visits);
  step("step-clicks", funnel.clicks, funnel.clicks.rate === null ? [] : [`${percent.format(funnel.clicks.rate)} of visits `,
    changeNode(compare(funnel.clicks.rate, funnel.clicks.previousRate), { share: true })]);
  step("step-disk", funnel.diskImages, [!github ? "Press Refresh to read GitHub"
    : github.since ? `On GitHub since ${dayLabel(github.since)}` : "Needs a second GitHub count"]);
  step("step-optins", funnel.optIns);
}

function platformColumn(data, platform) {
  const metrics = data.platforms[platform];
  const name = PLATFORM_LABELS[platform];
  const column = el("div", { className: "platform", attrs: { "data-platform": platform } }, [el("h3", { text: name })]);
  const count = metrics.copies.value;
  if (count === null) {
    column.append(el("p", { className: "empty", text: data.hasData
      ? `${name} numbers appear after the next nightly count, or press Refresh.` : "No counts yet." }));
    return column;
  }
  column.append(
    el("p", { className: "platform-number" }, [number.format(count), el("small", { text: count === 1 ? " copy" : " copies" })]),
    changeNode(metrics.copies.change, undefined, "p"),
  );
  if (count === 0) {
    column.append(el("p", { className: "empty", text: platform === "windows" && !data.everWindows
      ? "No Windows copy has shared counts yet." : `No ${name} copy shared counts in these days.` }));
    return column;
  }
  column.append(el("p", { className: "platform-facts", text: `${format(metrics.perDay.value, decimal)} a day on average · `
    + `${metrics.newInstalls.value === null ? "new opt-ins unknown" : plural(metrics.newInstalls.value, "new opt-in", "new opt-ins")}` }));
  if (!metrics.os) {
    column.append(el("p", { className: "empty", text: "Systems and chips appear after the next nightly count, or press Refresh." }));
  } else {
    const systems = el("ol", { className: "bars" }), chips = el("ol", { className: "bars" });
    bars(systems, metrics.os);
    bars(chips, metrics.architectures);
    column.append(el("h4", { text: "Systems" }), systems, el("h4", { text: "Chips" }), chips);
  }
  return column;
}

function renderPlatforms(data) {
  $("platforms").replaceChildren(...PLATFORMS.map((platform) => platformColumn(data, platform)));
}

function renderVersions(data) {
  const box = $("adoption");
  const versions = data.versions;
  box.replaceChildren();
  if (!versions?.of) {
    box.append(el("p", { className: "empty", text: versions ? "No copy reported a version in these days."
      : data.hasData ? "Versions appear after the next nightly count, or press Refresh." : "No versions reported yet." }));
    $("versions").replaceChildren();
    return;
  }
  const split = PLATFORMS.filter((platform) => versions.platforms[platform].of)
    .map((platform) => `${PLATFORM_LABELS[platform]} ${number.format(versions.platforms[platform].on)} of ${number.format(versions.platforms[platform].of)}`);
  box.append(
    el("p", { className: "adoption-number", text: percent.format(versions.share) }),
    el("p", { className: "adoption-text" }, [`of copies ran the newest ${versions.fromGitHub ? "release" : "version seen"}, `,
      el("strong", { text: versions.newest }), ` (${number.format(versions.on)} of ${number.format(versions.of)}).`]),
  );
  if (split.length > 1) box.append(el("p", { className: "adoption-detail", text: split.join(" · ") }));
  const today = versions.today?.of
    ? `${number.format(versions.today.on)} of ${number.format(versions.today.of)} (${percent.format(versions.today.share)})` : null;
  if (versions.later) {
    box.append(el("p", { className: "adoption-detail" }, [el("strong", { text: versions.later }),
      ` came out after ${dayLabel(data.period.end)}. `, today ? `Counting today so far, ${today} run it.` : "Press Refresh to count today."]));
  } else if (today) box.append(el("p", { className: "adoption-detail", text: `Counting today so far: ${today}.` }));
  bars($("versions"), versions.rows.map((row) => ({ ...row, tag: row.label === versions.newest ? "newest" : undefined })));
}

function renderDownloads(data) {
  const { github, homebrew } = data;
  $("downloads-note").textContent = github
    ? `GitHub counted ${github.capturedAt ? dateTime.format(new Date(github.capturedAt)) : `on ${dayLabel(github.day)}`}`
    : "Not counted yet";
  const kinds = $("download-kinds");
  kinds.replaceChildren();
  if (github) {
    for (const [kind, label] of Object.entries(DOWNLOAD_LABELS)) {
      const counts = github.kinds[kind];
      kinds.append(el("div", { className: "kind", attrs: { "data-kind": kind } }, [
        el("strong", { text: counts.gained === null ? "–" : `${counts.gained > 0 ? "+" : ""}${number.format(counts.gained)}` }),
        el("span", { text: label }),
        changeNode(compare(counts.gained, counts.previous), undefined, "p"),
        el("p", { text: `${number.format(counts.total)} in all. ${DOWNLOAD_NOTES[kind]}` }),
      ]));
    }
  } else kinds.append(el("p", { className: "empty", text: "The nightly count or Refresh reads GitHub's release numbers." }));
  $("downloads-since").textContent = github?.since ? `New downloads since GitHub's count on ${dayLabel(github.since)}. GitHub counts `
    + "repeats and bots too." : github ? "Only one GitHub count so far, so there are no new downloads to show yet." : "";
  const releases = github?.releases ?? [];
  const shown = releases.slice(0, 6);
  const older = releases.slice(6);
  if (older.length) shown.push({ key: "older", label: plural(older.length, "older release", "older releases"), value: older.reduce((sum, row) => sum + row.value, 0) });
  bars($("releases"), shown, { empty: "No release downloads counted yet." });

  const box = $("homebrew");
  box.replaceChildren(el("h3", { text: "Homebrew installs" }));
  if (homebrew.status === "reported") {
    box.append(el("div", { className: "counts" }, ["30d", "90d", "365d"].map((key) =>
      el("div", {}, [el("strong", { text: format(homebrew.counts[key] ?? null) }), el("span", { text: `${key.replace("d", "")} days` })]))));
  } else if (homebrew.status === "unreported") {
    box.append(
      el("p", { text: "Not reported yet. Homebrew sends install counts for the homebrew-annotator tap. Macs that added the tap with the older two-line command used a custom address, which Homebrew treats as private, so their installs aren't counted." }),
      el("p", {}, ["Homebrew installs still download the Mac ZIP from GitHub, so they are inside the Mac ZIP count above. Checked ",
        dayLabel(homebrew.day), " in Homebrew's public report for ", el("code", { text: "dorlugasigal/annotator/annotator" }), "."]),
    );
  } else box.append(el("p", { text: "The nightly count hasn't checked Homebrew yet." }));
}

function renderFeatures(data) {
  const { features } = data;
  bars($("features"), features.activeDays ? features.rows : [], {
    max: Math.max(features.activeDays, 1),
    value: (row) => [percent.format(row.share), row.previousShare === null
      ? `${number.format(row.value)} of ${number.format(features.activeDays)}`
      : changeNode(compare(row.share, row.previousShare), { share: true }, "small")],
    empty: data.hasData ? "No opted-in copy was active in these days." : "No feature use reported yet.",
  });
}

function renderWebsite(data) {
  const empty = `No visits in these ${data.period.length} days.`;
  bars($("routes"), data.website.routes, { empty });
  bars($("referrers"), data.website.referrers, { empty });
  bars($("tracks"), data.website.tracks, { empty: `No download clicks in these ${data.period.length} days.` });
}

function renderRetention(data) {
  $("retention-pooled").replaceChildren(...[["d7", 7], ["d30", 30]].map(([key, days]) => {
    const pool = data.retention[key];
    return el("div", { className: "pool" }, [
      el("strong", { className: pool ? "" : "is-empty", text: pool ? percent.format(pool.rate) : "–" }),
      el("span", { text: `came back on day ${days}` }),
      el("small", { text: pool ? `${number.format(pool.retained)} of ${plural(pool.size, "copy", "copies")}` : "No group was checked in these days." }),
      changeNode(pool?.change ?? null, { share: true }, "small"),
    ]);
  }));
  const list = $("retention");
  list.replaceChildren();
  if (!data.cohorts.length) {
    list.append(el("li", { className: "empty", text: "Not enough history yet. The first day-7 result appears a week after a copy first shares counts." }));
    return;
  }
  data.cohorts.forEach((row, index) => {
    const fill = el("span", { className: "bar-fill" });
    fill.style.setProperty("--share", `${row.rate > 0 ? Math.max(2, row.rate * 100) : 0}%`);
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
  const shown = overview(data, periodLength);
  renderRange(shown);
  renderPeople(shown);
  renderFunnel(shown);
  renderPlatforms(shown);
  renderVersions(shown);
  renderDownloads(shown);
  renderFeatures(shown);
  renderWebsite(shown);
  renderRetention(shown);
  renderHealth(data.health, data.generatedAt);
  $("freshness").replaceChildren("Loaded ", el("strong", { text: time.format(new Date(data.generatedAt)) }),
    shown.hasData ? ` · whole days to ${dayLabel(shown.period.end)}` : " · no counts yet");
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
  if (github === "failed") status(`Recounted yesterday and today at ${at}. GitHub didn't answer (${body.refresh.error}), so downloads are from the last count.`, "warn");
  else if (github === "recent") status(`Recounted yesterday and today at ${at}. GitHub downloads were read less than 10 minutes ago.`);
  else status(`Recounted yesterday and today, and read GitHub downloads, at ${at}.`);
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

for (const input of document.querySelectorAll('input[name="period"]')) {
  input.addEventListener("change", () => {
    periodLength = Number(input.value);
    history.replaceState(null, "", `#${periodLength}-days`);
    if (lastData) render(lastData);
  });
}
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
