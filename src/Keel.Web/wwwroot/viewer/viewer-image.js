// Image viewer (PNG, JPEG, GIF, WebP, SVG). The bytes are shown through an <img> element only: SVG is never inlined into the document, so
// scripts, links and external references inside an SVG cannot run (an image-context SVG is inert). Zoom and pan use the CSSOM (no inline style
// attributes, which the CSP forbids). The marking overlay is drawn over the image and protected by the banner guard.
import { addButton, addSeparator, createOverlay } from './viewer-ui.js';

const MIN = 0.05;
const MAX = 40;

export async function mount({ stage, toolbar, meta, bytes, markings, setStatus, fail }) {
  const mediaType = { png: 'image/png', jpeg: 'image/jpeg', gif: 'image/gif', webp: 'image/webp', svg: 'image/svg+xml' }[meta.format];
  if (!mediaType) { fail('This image format is not supported.'); return; }
  const url = URL.createObjectURL(new Blob([bytes], { type: mediaType }));

  const frame = document.createElement('div');
  frame.className = 'image-frame';
  frame.tabIndex = 0;
  frame.setAttribute('role', 'img');
  frame.setAttribute('aria-label', `${meta.fileName}. Arrow keys pan, plus and minus zoom, 0 fits the window.`);
  const img = document.createElement('img');
  img.className = 'image-content';
  img.alt = meta.fileName;
  img.draggable = false;
  frame.append(img);
  stage.replaceChildren(frame);
  document.getElementById('viewer-status')?.remove();

  await new Promise((resolve, reject) => {
    img.onload = resolve;
    img.onerror = () => reject(new Error('The image could not be decoded. The file may be damaged.'));
    img.src = url;
  });

  createOverlay(markings, stage);
  const natural = { w: img.naturalWidth || 300, h: img.naturalHeight || 150 };
  let scale = 1;
  let x = 0;
  let y = 0;

  const readout = document.createElement('span');
  readout.className = 'viewer-readout';
  readout.setAttribute('role', 'status');

  function apply() {
    img.style.width = `${natural.w}px`;
    img.style.height = `${natural.h}px`;
    img.style.transform = `translate(${x}px, ${y}px) scale(${scale})`;
    readout.textContent = `${natural.w} × ${natural.h} px · ${Math.round(scale * 100)}%`;
  }
  function fit() {
    const r = frame.getBoundingClientRect();
    scale = Math.min(r.width / natural.w, r.height / natural.h, 1) || 1;
    x = (r.width - natural.w * scale) / 2;
    y = (r.height - natural.h * scale) / 2;
    apply();
  }
  function zoomAt(factor, cx, cy) {
    const next = Math.min(MAX, Math.max(MIN, scale * factor));
    const k = next / scale;
    x = cx - (cx - x) * k;
    y = cy - (cy - y) * k;
    scale = next;
    apply();
  }
  const center = () => { const r = frame.getBoundingClientRect(); return [r.width / 2, r.height / 2]; };

  addButton(toolbar, 'Zoom in', () => zoomAt(1.25, ...center()), { key: '+' });
  addButton(toolbar, 'Zoom out', () => zoomAt(0.8, ...center()), { key: '-' });
  addButton(toolbar, 'Fit', fit, { key: '0' });
  addButton(toolbar, '100%', () => { const [cx, cy] = center(); zoomAt(1 / scale, cx, cy); });
  addSeparator(toolbar);
  toolbar.append(readout);
  toolbar.hidden = false;

  frame.addEventListener('wheel', (e) => {
    e.preventDefault();
    const r = frame.getBoundingClientRect();
    zoomAt(e.deltaY < 0 ? 1.1 : 1 / 1.1, e.clientX - r.left, e.clientY - r.top);
  }, { passive: false });
  let drag = null;
  frame.addEventListener('pointerdown', (e) => { drag = { px: e.clientX, py: e.clientY, x, y }; frame.setPointerCapture(e.pointerId); });
  frame.addEventListener('pointermove', (e) => { if (drag) { x = drag.x + e.clientX - drag.px; y = drag.y + e.clientY - drag.py; apply(); } });
  frame.addEventListener('pointerup', () => { drag = null; });
  frame.addEventListener('keydown', (e) => {
    const step = 40;
    const handled = {
      ArrowLeft: () => { x += step; }, ArrowRight: () => { x -= step; }, ArrowUp: () => { y += step; }, ArrowDown: () => { y -= step; },
      '+': () => zoomAt(1.25, ...center()), '=': () => zoomAt(1.25, ...center()), '-': () => zoomAt(0.8, ...center()), 0: fit,
    }[e.key];
    if (handled) { e.preventDefault(); handled(); apply(); }
  });
  fit();
  frame.focus();
  document.body.dataset.viewerState = 'ready';
  stage.dataset.ready = 'true';
  window.addEventListener('pagehide', () => URL.revokeObjectURL(url));
  setStatus('');
}
