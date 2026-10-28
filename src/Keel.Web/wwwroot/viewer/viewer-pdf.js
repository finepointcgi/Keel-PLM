// PDF viewer on pdf.js (Apache-2.0, vendored). The bytes are the server's MARKED pipeline output: every page already carries the banner, notices
// and traceability line stamped into it, so the viewer adds no overlay (what is on screen is what a download contains). PDF JavaScript is never
// executed (enableScripting false, isEvalSupported false) and links/forms are not activated: pages are drawn to canvas with a selectable text layer.
import { addButton, addSeparator, setPressed } from './viewer-ui.js';

const MIN_SCALE = 0.25;
const MAX_SCALE = 5;

export async function mount({ stage, toolbar, meta, bytes, assetBase, setStatus, fail }) {
  const pdfjs = await import(`${assetBase}vendor/pdf.min.mjs`);
  pdfjs.GlobalWorkerOptions.workerSrc = `${assetBase}vendor/pdf.worker.min.mjs`;

  let doc;
  try {
    doc = await pdfjs.getDocument({
      data: bytes,
      isEvalSupported: false,
      enableScripting: false,
      verbosity: 0,
      stopAtErrors: false,
    }).promise;
  } catch (e) {
    const name = e && e.name;
    fail(name === 'PasswordException' ? 'This PDF is password protected and cannot be shown.'
      : name === 'InvalidPDFException' ? 'This PDF is damaged and cannot be shown.'
        : `This PDF cannot be shown: ${e && e.message ? e.message : 'unknown error'}`);
    return;
  }

  const scroll = document.createElement('div');
  scroll.className = 'pdf-scroll';
  scroll.tabIndex = 0;
  scroll.setAttribute('role', 'document');
  scroll.setAttribute('aria-label', `${meta.fileName}, ${doc.numPages} pages. Plus and minus zoom, 0 fits the width, Page Up and Page Down move between pages.`);
  stage.replaceChildren(scroll);
  document.getElementById('viewer-status')?.remove();

  const first = await doc.getPage(1);
  const base = first.getViewport({ scale: 1 });
  let mode = 'fit-width'; // 'fit-width' | 'fit-page' | 'custom'
  let scale = 1;
  const pages = [];
  for (let n = 1; n <= doc.numPages; n++) {
    const holder = document.createElement('div');
    holder.className = 'pdf-page';
    holder.dataset.page = String(n);
    holder.setAttribute('role', 'region');
    holder.setAttribute('aria-label', `Page ${n} of ${doc.numPages}`);
    scroll.append(holder);
    pages.push({ n, holder, size: { w: base.width, h: base.height }, rendered: false, task: null, version: 0 });
  }

  const pageInput = document.createElement('input');
  pageInput.type = 'number';
  pageInput.min = '1';
  pageInput.max = String(doc.numPages);
  pageInput.value = '1';
  pageInput.className = 'viewer-page-input';
  pageInput.setAttribute('aria-label', 'Page number');
  const zoomReadout = document.createElement('span');
  zoomReadout.className = 'viewer-readout';
  zoomReadout.setAttribute('role', 'status');

  function goTo(n) {
    const page = pages[Math.min(doc.numPages, Math.max(1, n)) - 1];
    scroll.scrollTo({ top: page.holder.offsetTop - scroll.offsetTop - 8 });
  }
  let current = 1;
  function currentPage() {
    const mid = scroll.scrollTop + scroll.clientHeight / 3;
    let best = 1;
    for (const p of pages) { if (p.holder.offsetTop - scroll.offsetTop <= mid) best = p.n; else break; }
    return best;
  }

  addButton(toolbar, 'Previous page', () => goTo(current - 1), { key: 'PageUp' });
  toolbar.append(pageInput, Object.assign(document.createElement('span'), { className: 'viewer-readout', textContent: `of ${doc.numPages}` }));
  addButton(toolbar, 'Next page', () => goTo(current + 1), { key: 'PageDown' });
  addSeparator(toolbar);
  addButton(toolbar, 'Zoom out', () => zoom(scale / 1.2), { key: '-' });
  addButton(toolbar, 'Zoom in', () => zoom(scale * 1.2), { key: '+' });
  addButton(toolbar, 'Fit width', () => fit('fit-width'), { key: '0' });
  addButton(toolbar, 'Fit page', () => fit('fit-page'), { key: '9' });
  toolbar.append(zoomReadout);
  addSeparator(toolbar);
  addButton(toolbar, 'Dark pages', (b) => {
    const on = !scroll.classList.contains('pdf-inverted');
    scroll.classList.toggle('pdf-inverted', on);
    setPressed(b, on);
  }, { pressed: false, title: 'Invert page colours for low light' });
  toolbar.hidden = false;

  function layout() {
    const avail = scroll.clientWidth - 32;
    if (mode === 'fit-width') scale = avail / base.width;
    else if (mode === 'fit-page') scale = Math.min(avail / base.width, (scroll.clientHeight - 24) / base.height);
    scale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale || 1));
    for (const p of pages) {
      p.holder.style.width = `${Math.floor(p.size.w * scale)}px`;
      p.holder.style.height = `${Math.floor(p.size.h * scale)}px`;
      if (p.scaleUsed !== scale) unrender(p); // a page drawn at the current zoom stays as it is
    }
    zoomReadout.textContent = `${Math.round(scale * 100)}%`;
    observeAll();
  }
  function zoom(next) { mode = 'custom'; scale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, next)); layoutKeepingPosition(); }
  function fit(m) { mode = m; layoutKeepingPosition(); }
  function layoutKeepingPosition() {
    const n = currentPage();
    layout();
    goTo(n);
  }

  async function render(p) {
    if (p.rendered || p.task) return;
    const version = ++p.version;
    try {
      const page = await doc.getPage(p.n);
      if (version !== p.version) return;
      const viewport = page.getViewport({ scale });
      p.scaleUsed = scale;
      // Pages may differ in size; correct the placeholder as soon as the real size is known.
      const real = page.getViewport({ scale: 1 });
      if (Math.abs(real.width - p.size.w) > 0.5 || Math.abs(real.height - p.size.h) > 0.5) {
        p.size = { w: real.width, h: real.height };
        p.holder.style.width = `${Math.floor(viewport.width)}px`;
        p.holder.style.height = `${Math.floor(viewport.height)}px`;
      }
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      const canvas = document.createElement('canvas');
      canvas.width = Math.floor(viewport.width * dpr);
      canvas.height = Math.floor(viewport.height * dpr);
      canvas.style.width = `${Math.floor(viewport.width)}px`;
      canvas.style.height = `${Math.floor(viewport.height)}px`;
      canvas.setAttribute('aria-hidden', 'true');
      const text = document.createElement('div');
      text.className = 'pdf-text';
      text.style.setProperty('--scale-factor', String(viewport.scale));
      text.style.setProperty('--total-scale-factor', String(viewport.scale));
      p.holder.replaceChildren(canvas, text);
      const ctx = canvas.getContext('2d', { alpha: false });
      p.task = page.render({ canvasContext: ctx, viewport, transform: dpr === 1 ? null : [dpr, 0, 0, dpr, 0, 0] });
      await p.task.promise;
      if (version !== p.version) return;
      const layer = new pdfjs.TextLayer({ textContentSource: page.streamTextContent(), container: text, viewport });
      await layer.render();
      p.rendered = true;
      p.holder.dataset.rendered = 'true';
      document.body.dataset.pdfRenderedPages = String(pages.filter((x) => x.rendered).length);
      document.body.dataset.viewerState = 'ready';
      stage.dataset.ready = 'true';
      page.cleanup();
    } catch (e) {
      if (!(e && e.name === 'RenderingCancelledException')) {
        p.holder.textContent = `Page ${p.n} could not be drawn.`;
      }
    } finally {
      p.task = null;
    }
  }
  function unrender(p) {
    p.version++;
    if (p.task) { try { p.task.cancel(); } catch { /* already finished */ } p.task = null; }
    if (p.rendered || p.holder.firstChild) { p.holder.replaceChildren(); }
    p.rendered = false;
    p.scaleUsed = undefined;
    delete p.holder.dataset.rendered;
  }

  const observer = new IntersectionObserver((entries) => {
    for (const e of entries) {
      const p = pages[Number(e.target.dataset.page) - 1];
      if (e.isIntersecting) render(p); else unrender(p);
    }
  }, { root: scroll, rootMargin: '600px 0px' });
  function observeAll() { observer.disconnect(); for (const p of pages) observer.observe(p.holder); }

  scroll.addEventListener('scroll', () => {
    const n = currentPage();
    if (n !== current) { current = n; pageInput.value = String(n); }
  }, { passive: true });
  pageInput.addEventListener('change', () => goTo(Number(pageInput.value) || 1));
  scroll.addEventListener('keydown', (e) => {
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    const k = {
      '+': () => zoom(scale * 1.2), '=': () => zoom(scale * 1.2), '-': () => zoom(scale / 1.2), 0: () => fit('fit-width'), 9: () => fit('fit-page'),
      PageDown: () => goTo(current + 1), PageUp: () => goTo(current - 1), Home: () => goTo(1), End: () => goTo(doc.numPages),
    }[e.key];
    if (k) { e.preventDefault(); k(); }
  });
  let resizeTimer = 0;
  let lastWidth = scroll.clientWidth;
  new ResizeObserver(() => {
    if (mode === 'custom' || scroll.clientWidth === lastWidth) return;
    lastWidth = scroll.clientWidth;
    window.clearTimeout(resizeTimer);
    resizeTimer = window.setTimeout(layoutKeepingPosition, 150);
  }).observe(scroll);
  window.addEventListener('pagehide', () => { doc.destroy(); });

  layout();
  scroll.focus();
  setStatus('');
  document.body.dataset.pdfPages = String(doc.numPages);
}
