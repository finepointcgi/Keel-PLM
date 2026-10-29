// Diagram behaviour for the relationship pictures (UX-3, ADR-0029): pan and zoom (mouse wheel with Ctrl or Cmd, drag, two-finger pinch, + - 0 and arrow keys),
// roving focus between nodes with the arrow keys, Enter / Ctrl+Enter to open, a tooltip that follows focus as well as the pointer, and "fit". An ES module served from
// the application's own origin: no inline script, no eval, no external request. It decides nothing about access: every node it can focus was rendered by the server for
// a viewer who may see it; restricted nodes carry no data-href and no text, so there is nothing for it to open or reveal.
//
// Markup contract (see Components/Visuals/DiagramViewport.razor):
//   root           .vis-viewport[data-vis-w][data-vis-h]   the content size in SVG units
//   svg            .vis-svg > g[data-vis-content]          the layer this module moves
//   toolbar        [data-vis-action="in|out|fit|reset"]    buttons
//   nodes          .vis-node[id][data-x][data-y][data-w][data-h]  optional data-left / data-right / data-up / data-down (id lists), data-href, data-tip
//   tooltip        .vis-tip                                 text only, set with textContent

const states = new WeakMap();
const MIN = 0.1;
const MAX = 4;

function clamp(v, lo, hi) { return Math.min(hi, Math.max(lo, v)); }

function apply(s) {
  s.content.setAttribute('transform', `translate(${s.tx.toFixed(2)} ${s.ty.toFixed(2)}) scale(${s.scale.toFixed(4)})`);
}

function size(root) {
  const r = root.querySelector('.vis-svg').getBoundingClientRect();
  return { w: Math.max(r.width, 1), h: Math.max(r.height, 1) };
}

function fit(s) {
  const { w, h } = size(s.root);
  const cw = Number(s.root.dataset.visW) || 1;
  const ch = Number(s.root.dataset.visH) || 1;
  const scale = clamp(Math.min(w / cw, h / ch), MIN, 1.25);
  s.scale = scale;
  s.tx = (w - cw * scale) / 2;
  s.ty = Math.max((h - ch * scale) / 2, 0);
  s.moved = false;
  apply(s);
}

function zoomAt(s, factor, px, py) {
  const next = clamp(s.scale * factor, MIN, MAX);
  const k = next / s.scale;
  s.tx = px - (px - s.tx) * k;
  s.ty = py - (py - s.ty) * k;
  s.scale = next;
  s.moved = true;
  apply(s);
}

function centre(s) {
  const { w, h } = size(s.root);
  return { x: w / 2, y: h / 2 };
}

function reveal(s, el) {
  const { w, h } = size(s.root);
  const x = Number(el.dataset.x) * s.scale + s.tx;
  const y = Number(el.dataset.y) * s.scale + s.ty;
  const nw = Number(el.dataset.w) * s.scale;
  const nh = Number(el.dataset.h) * s.scale;
  const pad = 24;
  if (x < pad) s.tx += pad - x; else if (x + nw > w - pad) s.tx -= x + nw - (w - pad);
  if (y < pad) s.ty += pad - y; else if (y + nh > h - pad) s.ty -= y + nh - (h - pad);
  apply(s);
}

function openHref(href, newTab) {
  if (newTab) { window.open(href, '_blank', 'noopener'); return; }
  if (window.Blazor && typeof window.Blazor.navigateTo === 'function') window.Blazor.navigateTo(href);
  else window.location.assign(new URL(href, document.baseURI).toString());
}

function firstId(list) {
  return (list || '').split(' ').filter(Boolean)[0];
}

function showTip(s, node) {
  const text = node.dataset.tip;
  if (!text) { hideTip(s); return; }
  s.tip.textContent = text;
  s.tip.hidden = false;
  const rr = s.root.getBoundingClientRect();
  const nr = node.getBoundingClientRect();
  const left = clamp(nr.left - rr.left, 4, Math.max(rr.width - 260, 4));
  const below = nr.bottom - rr.top + 6;
  s.tip.style.left = `${left}px`;
  s.tip.style.top = `${Math.min(below, Math.max(rr.height - 60, 4))}px`;
}

function hideTip(s) { s.tip.hidden = true; s.tip.textContent = ''; }

export function attach(root) {
  if (!root || states.has(root)) return;
  const content = root.querySelector('[data-vis-content]');
  const tip = root.querySelector('.vis-tip');
  if (!content || !tip) return;
  const svg = root.querySelector('.vis-svg');
  const s = { root, content, tip, scale: 1, tx: 0, ty: 0, moved: false, pointers: new Map(), pinch: 0, drag: null, off: [] };
  states.set(root, s);

  const on = (target, type, handler, opts) => { target.addEventListener(type, handler, opts); s.off.push(() => target.removeEventListener(type, handler, opts)); };

  fit(s);
  // Fit again once when the container gets its real size (tabs and panels lay out after the first render), until the person moves the picture themselves.
  const ro = typeof ResizeObserver === 'function' ? new ResizeObserver(() => { if (!s.moved) fit(s); }) : null;
  ro?.observe(svg);
  s.off.push(() => ro?.disconnect());

  on(root, 'click', (e) => {
    const btn = e.target instanceof Element ? e.target.closest('[data-vis-action]') : null;
    if (!btn || !root.contains(btn)) return;
    const c = centre(s);
    switch (btn.dataset.visAction) {
      case 'in': zoomAt(s, 1.25, c.x, c.y); break;
      case 'out': zoomAt(s, 0.8, c.x, c.y); break;
      case 'fit': fit(s); break;
      case 'reset': s.scale = 1; s.tx = 16; s.ty = 16; s.moved = true; apply(s); break;
    }
  });

  on(svg, 'wheel', (e) => {
    if (!(e.ctrlKey || e.metaKey)) return; // plain wheel scrolls the page
    e.preventDefault();
    const r = svg.getBoundingClientRect();
    zoomAt(s, e.deltaY < 0 ? 1.1 : 1 / 1.1, e.clientX - r.left, e.clientY - r.top);
  }, { passive: false });

  on(svg, 'pointerdown', (e) => {
    if (e.target instanceof Element && e.target.closest('.vis-node')) return; // clicks on nodes belong to the node
    s.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    svg.setPointerCapture?.(e.pointerId);
    if (s.pointers.size === 1) s.drag = { x: e.clientX, y: e.clientY, tx: s.tx, ty: s.ty };
    if (s.pointers.size === 2) { s.drag = null; s.pinch = distance(s); }
  });
  on(svg, 'pointermove', (e) => {
    if (!s.pointers.has(e.pointerId)) return;
    s.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (s.pointers.size === 1 && s.drag) {
      s.tx = s.drag.tx + (e.clientX - s.drag.x);
      s.ty = s.drag.ty + (e.clientY - s.drag.y);
      s.moved = true;
      apply(s);
    } else if (s.pointers.size === 2) {
      const d = distance(s);
      if (s.pinch > 0 && d > 0) {
        const m = midpoint(s);
        const r = svg.getBoundingClientRect();
        zoomAt(s, d / s.pinch, m.x - r.left, m.y - r.top);
      }
      s.pinch = d;
    }
  });
  const up = (e) => { s.pointers.delete(e.pointerId); s.drag = null; s.pinch = 0; };
  on(svg, 'pointerup', up);
  on(svg, 'pointercancel', up);

  on(root, 'keydown', (e) => {
    const node = e.target instanceof Element ? e.target.closest('.vis-node') : null;
    if (e.key === 'Escape') { hideTip(s); return; }
    if (e.altKey || (e.metaKey && !node)) return;
    if (node && root.contains(node)) {
      if (handleNodeKey(s, node, e)) return;
    }
    const c = centre(s);
    switch (e.key) {
      case '+': case '=': e.preventDefault(); zoomAt(s, 1.25, c.x, c.y); break;
      case '-': case '_': e.preventDefault(); zoomAt(s, 0.8, c.x, c.y); break;
      case '0': e.preventDefault(); fit(s); break;
      default:
        if (e.target === root || e.target === svg) {
          const step = 48;
          if (e.key === 'ArrowLeft') { e.preventDefault(); s.tx += step; s.moved = true; apply(s); }
          else if (e.key === 'ArrowRight') { e.preventDefault(); s.tx -= step; s.moved = true; apply(s); }
          else if (e.key === 'ArrowUp') { e.preventDefault(); s.ty += step; s.moved = true; apply(s); }
          else if (e.key === 'ArrowDown') { e.preventDefault(); s.ty -= step; s.moved = true; apply(s); }
        }
    }
  });

  // Roving tabindex: one node is in the tab order; focus and hover show the tooltip.
  on(root, 'focusin', (e) => {
    const node = e.target instanceof Element ? e.target.closest('.vis-node') : null;
    if (!node) return;
    root.querySelectorAll('.vis-node[tabindex="0"]').forEach((n) => { if (n !== node) n.setAttribute('tabindex', '-1'); });
    node.setAttribute('tabindex', '0');
    reveal(s, node);
    showTip(s, node);
  });
  on(root, 'focusout', (e) => { if (!(e.relatedTarget instanceof Element) || !root.contains(e.relatedTarget)) hideTip(s); });
  on(root, 'mouseover', (e) => { const node = e.target instanceof Element ? e.target.closest('.vis-node') : null; if (node) showTip(s, node); });
  on(root, 'mouseout', (e) => { const node = e.target instanceof Element ? e.target.closest('.vis-node') : null; if (node && document.activeElement !== node) hideTip(s); });
}

function distance(s) {
  const [a, b] = [...s.pointers.values()];
  return Math.hypot(a.x - b.x, a.y - b.y);
}

function midpoint(s) {
  const [a, b] = [...s.pointers.values()];
  return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
}

/** Returns true when the key was a node key and has been handled. */
function handleNodeKey(s, node, e) {
  const go = (id) => {
    const el = id ? s.root.querySelector(`[id="${CSS.escape(id)}"]`) : null;
    if (el instanceof HTMLElement || el instanceof SVGElement) { e.preventDefault(); el.focus(); return true; }
    return false;
  };
  switch (e.key) {
    case 'ArrowRight': return go(firstId(node.dataset.right));
    case 'ArrowLeft': return go(firstId(node.dataset.left));
    case 'ArrowUp': return go(firstId(node.dataset.up));
    case 'ArrowDown': return go(firstId(node.dataset.down));
    case 'Home': return go(s.root.dataset.visFirst);
    case 'End': return go(s.root.dataset.visLast);
    case 'Enter': {
      const href = node.dataset.href;
      e.preventDefault();
      if (href) openHref(href, e.ctrlKey || e.metaKey);
      else node.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
      return true;
    }
    case ' ': {
      e.preventDefault();
      node.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
      return true;
    }
    default: return false;
  }
}

export function focusFirst(root) {
  const first = root?.querySelector('.vis-node');
  if (first instanceof HTMLElement || first instanceof SVGElement) first.focus();
}

export function focusNode(root, id) {
  const el = root?.querySelector(`[id="${CSS.escape(id)}"]`);
  if (el instanceof HTMLElement || el instanceof SVGElement) el.focus();
}

export function refit(root) {
  const s = states.get(root);
  if (s) fit(s);
}

/** True on a phone-width screen: the table alternative is offered first there. */
export function isNarrow() {
  return typeof window.matchMedia === 'function' && window.matchMedia('(max-width: 599px)').matches;
}

export function detach(root) {
  const s = states.get(root);
  if (!s) return;
  s.off.forEach((f) => f());
  states.delete(root);
}
