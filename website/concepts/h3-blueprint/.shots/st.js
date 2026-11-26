(() => {
  const q = (s) => document.querySelector(s), screen = q("#screen"), bar = q(".ink-toolbar"), layer = q("#board .ink-layer");
  const off = (sel, to) => { const m = q(sel); if (!m) return null; const b = m.getBoundingClientRect(), n = q(to).getBoundingClientRect(); return [+(b.left + b.width / 2 - n.left - n.width / 2).toFixed(1), +(b.top + b.height / 2 - n.top - n.height / 2).toFixed(1)]; };
  const w = q("#report").getBoundingClientRect();
  return JSON.stringify({ stage: screen.dataset.stage, mode: layer.dataset.mode, hidden: layer.classList.contains("is-hidden"), collapsed: bar.classList.contains("is-collapsed"),
    ringCentreFrom318: off("#board .ink-ellipse:not(.is-app)", "#ring-target"), handCentreFrom96: off("#board .ink-ellipse.is-app", "#report [data-f=orders]"), arrowMarks: document.querySelectorAll("#board .ink-arrow").length,
    win: [w.left, w.top, w.right, w.bottom].map(Math.round), tbh: getComputedStyle(screen).getPropertyValue("--tb-h").trim(), overflowX: document.documentElement.scrollWidth - innerWidth, y: Math.round(scrollY), size: innerWidth + "x" + innerHeight });
})()
