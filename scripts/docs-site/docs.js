// Small enhancements for the documentation site: mobile menu and in-page search over search.json (built with the pages). No network beyond the site itself.
(function () {
  var root = document.currentScript.getAttribute("data-root") || "";
  var menu = document.querySelector(".menu"), side = document.getElementById("side");
  if (menu && side) menu.addEventListener("click", function () {
    var open = side.classList.toggle("open");
    menu.setAttribute("aria-expanded", open ? "true" : "false");
  });
  var input = document.getElementById("q"), list = document.getElementById("results"), index = null;
  if (!input || !list) return;
  function load(cb) {
    if (index) return cb();
    fetch(root + "search.json").then(function (r) { return r.json(); }).then(function (j) { index = j; cb(); }).catch(function () { index = []; cb(); });
  }
  function esc(s) { return s.replace(/[&<>"]/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]; }); }
  function run() {
    var terms = input.value.toLowerCase().split(/\s+/).filter(Boolean);
    if (!terms.length) { list.hidden = true; return; }
    var hits = [];
    index.forEach(function (p) {
      var t = p.t.toLowerCase(), h = p.h.toLowerCase(), x = p.x.toLowerCase(), score = 0, ok = true;
      terms.forEach(function (w) {
        var s = (t.indexOf(w) >= 0 ? 8 : 0) + (h.indexOf(w) >= 0 ? 4 : 0) + (x.indexOf(w) >= 0 ? 1 : 0);
        if (!s) ok = false; score += s;
      });
      if (ok) hits.push({ p: p, score: score });
    });
    hits.sort(function (a, b) { return b.score - a.score; });
    list.innerHTML = hits.length ? hits.slice(0, 8).map(function (h) {
      var x = h.p.x, i = x.toLowerCase().indexOf(terms[0]), snip = i >= 0 ? x.slice(Math.max(0, i - 40), i + 90) : x.slice(0, 110);
      return '<li><a href="' + root + h.p.u + '">' + esc(h.p.t) + "<small>" + esc(snip) + "…</small></a></li>";
    }).join("") : "<li><a>No matches</a></li>";
    list.hidden = false;
  }
  input.addEventListener("input", function () { load(run); });
  input.addEventListener("focus", function () { if (input.value) load(run); });
  document.addEventListener("click", function (e) { if (!e.target.closest(".search")) list.hidden = true; });
  document.addEventListener("keydown", function (e) {
    if (e.key === "/" && document.activeElement.tagName !== "INPUT") { e.preventDefault(); input.focus(); }
    if (e.key === "Escape") { list.hidden = true; input.blur(); }
  });
})();
