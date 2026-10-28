// Keeps the marking banners and the distribution statement of the viewer document in place (ADR-0027, VWR-6).
//
// This is a handling aid, not DRM: a user with Download rights can obviously save the file, and anyone with developer tools can defeat any
// script running in their own browser. What it does is make accidental or casual removal visible: if a banner (or a registered overlay) is
// removed, hidden, made transparent or its text changed, it is restored and the viewer hides the file until the page is reloaded.

const TEXT_ATTR = 'data-viewer-banner';

/** @returns {{ info: () => object, protect: (el: HTMLElement, parent: HTMLElement) => void, intact: () => boolean }} */
export function guardBanners(meta) {
  const q = (sel) => document.querySelector(sel);
  const watched = [
    { key: 'top banner', selector: `[${TEXT_ATTR}][data-position="top"]` },
    { key: 'bottom banner', selector: `[${TEXT_ATTR}][data-position="bottom"]` },
    { key: 'distribution statement', selector: '[data-viewer-distribution]', optional: true },
  ].map((w) => {
    const node = q(w.selector);
    return node ? { ...w, node, parent: node.parentElement, next: node.nextSibling, text: node.textContent, copy: node.cloneNode(true) } : null;
  }).filter(Boolean);
  const overlays = []; // { el, parent, key }
  let intact = true;

  const banner = (q(`[${TEXT_ATTR}][data-position="top"]`)?.textContent ?? meta?.banner ?? '').trim();
  const distribution = (q('[data-viewer-distribution]')?.textContent ?? '').trim();
  const notices = meta?.notices ?? [];

  function problems() {
    const found = [];
    for (const w of watched) {
      if (!w.node.isConnected) { found.push(`${w.key} removed`); continue; }
      if (w.node.textContent !== w.text) found.push(`${w.key} text changed`);
      if (visiblyHidden(w.node)) found.push(`${w.key} hidden`);
    }
    for (const o of overlays) {
      if (!o.el.isConnected) found.push(`${o.key} removed`);
      else if (visiblyHidden(o.el)) found.push(`${o.key} hidden`);
    }
    return found;
  }

  function visiblyHidden(node) {
    const cs = getComputedStyle(node);
    if (cs.display === 'none' || cs.visibility !== 'visible' || Number.parseFloat(cs.opacity) < 0.9) return true;
    const r = node.getBoundingClientRect();
    return r.height < 6 || r.width < 20;
  }

  function restore() {
    for (const w of watched) {
      if (!w.node.isConnected) {
        w.node = w.copy.cloneNode(true);
        w.parent.insertBefore(w.node, w.next && w.next.isConnected ? w.next : null);
      }
      w.node.textContent = w.text;
      w.node.removeAttribute('style');
      w.node.removeAttribute('hidden');
    }
    for (const o of overlays) {
      if (!o.el.isConnected) o.parent.appendChild(o.el);
      o.el.removeAttribute('style');
      o.el.removeAttribute('hidden');
    }
  }

  function check() {
    const found = problems();
    if (found.length === 0) return;
    restore();
    if (intact) {
      intact = false;
      document.body.classList.add('banner-tampered');
      document.body.dataset.markingsIntact = 'false';
      const warn = document.createElement('p');
      warn.className = 'viewer-tamper';
      warn.setAttribute('role', 'alert');
      warn.textContent = 'The marking banner or distribution statement was changed on this page, so the file is hidden. Reload the page to view it.';
      document.getElementById('viewer-root')?.prepend(warn);
    }
  }

  document.body.dataset.markingsIntact = 'true';
  let scheduled = false;
  const schedule = () => {
    if (scheduled) return;
    scheduled = true;
    requestAnimationFrame(() => { scheduled = false; check(); });
  };
  new MutationObserver(schedule).observe(document.body, { subtree: true, childList: true, attributes: true, characterData: true });
  window.setInterval(check, 2000);

  return {
    info: () => ({ banner, distribution, notices }),
    /** Registers an overlay element that must stay in the document and visible. */
    protect: (el, parent) => { overlays.push({ el, parent, key: 'marking overlay' }); },
    intact: () => intact,
    /** Forgets the overlays (the viewer replaced its stage with an error panel on purpose). */
    releaseOverlays: () => { overlays.length = 0; },
  };
}
