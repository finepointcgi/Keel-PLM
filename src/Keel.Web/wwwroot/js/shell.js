// Browser helpers of the app shell (ADR-0028). An ES module served from the application's own origin, like session-lock.js: no inline script, no eval,
// so the CSP (default-src 'self') is untouched. It decides nothing about access: it moves focus, traps focus in dialogs, listens for the palette
// shortcuts, applies the theme attributes the server already chose, updates the address-bar fragment of a tab and guards unsaved edits.

const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

/** Sets the theme and density attributes on <html> (the server renders the same values into the first response). */
export function applyPreferences(theme, density) {
  const root = document.documentElement;
  root.setAttribute('data-theme', theme);
  root.setAttribute('data-density', density);
}

export function focusSelector(selector) {
  const el = document.querySelector(selector);
  if (el instanceof HTMLElement) el.focus({ preventScroll: false });
}

export function focusElement(el) {
  if (el instanceof HTMLElement) el.focus();
}

/** True when the focus is in something a person types into (so "/" must not open the palette). */
function isTyping(target) {
  if (!(target instanceof HTMLElement)) return false;
  if (target.isContentEditable) return true;
  const tag = target.tagName;
  if (tag === 'TEXTAREA' || tag === 'SELECT') return true;
  if (tag === 'INPUT') {
    const type = (target.getAttribute('type') || 'text').toLowerCase();
    return !['checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'file'].includes(type);
  }
  return false;
}

let removeShortcuts = null;

/**
 * Ctrl/Cmd+K and "/" (when not typing) open the command palette. The handler only calls back into the circuit.
 * @param {{invokeMethodAsync: (name: string) => Promise<void>}} dotnet
 */
export function registerShortcuts(dotnet) {
  unregisterShortcuts();
  const onKey = (e) => {
    if (e.defaultPrevented) return;
    const isK = (e.key === 'k' || e.key === 'K') && (e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey;
    const isSlash = e.key === '/' && !e.ctrlKey && !e.metaKey && !e.altKey && !isTyping(e.target);
    if (!isK && !isSlash) return;
    e.preventDefault();
    dotnet.invokeMethodAsync('OpenPalette').catch(() => { /* the circuit is gone */ });
  };
  document.addEventListener('keydown', onKey);
  removeShortcuts = () => document.removeEventListener('keydown', onKey);
}

export function unregisterShortcuts() {
  if (removeShortcuts) removeShortcuts();
  removeShortcuts = null;
}

const traps = new Map();

/**
 * Keeps Tab inside a dialog and remembers where the focus was, so closing the dialog returns it there.
 * @param {HTMLElement} container
 * @param {string|null} initialSelector the element to focus first (default: the first focusable control)
 */
export function trapFocus(container, initialSelector) {
  if (!(container instanceof HTMLElement)) return;
  releaseTrap(container);
  const previous = document.activeElement;
  const onKey = (e) => {
    if (e.key !== 'Tab') return;
    const items = [...container.querySelectorAll(FOCUSABLE)].filter((x) => x.offsetParent !== null || x === document.activeElement);
    if (items.length === 0) { e.preventDefault(); container.focus(); return; }
    const first = items[0];
    const last = items[items.length - 1];
    if (e.shiftKey && (document.activeElement === first || !container.contains(document.activeElement))) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && (document.activeElement === last || !container.contains(document.activeElement))) { e.preventDefault(); first.focus(); }
  };
  container.addEventListener('keydown', onKey);
  traps.set(container, { onKey, previous });
  const target = (initialSelector && container.querySelector(initialSelector)) || container.querySelector(FOCUSABLE) || container;
  if (target instanceof HTMLElement) target.focus();
}

export function releaseTrap(container, restore = true) {
  const t = traps.get(container);
  if (!t) return;
  container.removeEventListener('keydown', t.onKey);
  traps.delete(container);
  if (restore && t.previous instanceof HTMLElement && document.contains(t.previous)) t.previous.focus();
}

/** Activates the first link of a table row when the row itself has the focus (Enter on a row opens it; a focused link handles its own Enter). */
export function clickFirstLink(selector) {
  const row = document.querySelector(selector);
  if (row instanceof HTMLElement && document.activeElement === row) {
    const link = row.querySelector('a[href]');
    if (link instanceof HTMLElement) link.click();
  }
}

/** Scrolls the list item with aria-selected="true" into view inside a listbox (arrow-key navigation of the palette). */
export function scrollSelectedIntoView(listSelector) {
  const list = document.querySelector(listSelector);
  const item = list?.querySelector('[aria-selected="true"]');
  if (item instanceof HTMLElement) item.scrollIntoView({ block: 'nearest' });
}

/** Opens an address in a new tab (Ctrl/Cmd+Enter in the palette). The address is always one of this application's own routes. */
export function openInNewTab(url) {
  const target = new URL(url, window.location.href);
  if (target.origin === window.location.origin) window.open(target.toString(), '_blank', 'noopener');
}

/** Submits a form of the page (the sign-out form, which carries its antiforgery token). */
export function submitForm(selector) {
  const form = document.querySelector(selector);
  if (form instanceof HTMLFormElement) form.requestSubmit();
}

/** Updates the address-bar fragment without navigating (a selected tab is deep-linkable and survives reload). */
export function setHash(hash) {
  const url = new URL(window.location.href);
  url.hash = hash ? '#' + hash.replace(/^#/, '') : '';
  window.history.replaceState(window.history.state, '', url.toString());
}

/** Sets query-string values (a list's sort) in the address bar without navigating, so the order is shareable and survives reload. Pairs: key, value, key, value. */
export function setQuery(...pairs) {
  const url = new URL(window.location.href);
  for (let i = 0; i + 1 < pairs.length; i += 2) {
    if (pairs[i + 1]) url.searchParams.set(pairs[i], pairs[i + 1]);
    else url.searchParams.delete(pairs[i]);
  }
  window.history.replaceState(window.history.state, '', url.toString());
}

/** Reads the fragment so a page can open on the tab a link names. */
export function getHash() {
  return window.location.hash.replace(/^#/, '');
}

export function scrollToId(id) {
  const el = document.getElementById(id);
  if (el) el.scrollIntoView({ block: 'start' });
}

let beforeUnload = null;

/** Asks the browser to confirm leaving the page while a form has unsaved edits. */
export function guardUnsaved(enabled) {
  if (enabled && !beforeUnload) {
    beforeUnload = (e) => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', beforeUnload);
  } else if (!enabled && beforeUnload) {
    window.removeEventListener('beforeunload', beforeUnload);
    beforeUnload = null;
  }
}

/** Closes an open disclosure (user menu, column chooser) when Escape is pressed or the focus leaves it. */
export function watchDismiss(container, dotnet) {
  if (!(container instanceof HTMLElement)) return;
  const onKey = (e) => { if (e.key === 'Escape') dotnet.invokeMethodAsync('Dismiss', 'escape').catch(() => { }); };
  const onFocusOut = (e) => {
    if (e.relatedTarget instanceof Node && !container.contains(e.relatedTarget)) dotnet.invokeMethodAsync('Dismiss', 'outside').catch(() => { });
  };
  const onPointer = (e) => { if (!container.contains(e.target)) dotnet.invokeMethodAsync('Dismiss', 'outside').catch(() => { }); };
  container.addEventListener('keydown', onKey);
  container.addEventListener('focusout', onFocusOut);
  document.addEventListener('pointerdown', onPointer, true);
  container.__dismiss = () => {
    container.removeEventListener('keydown', onKey);
    container.removeEventListener('focusout', onFocusOut);
    document.removeEventListener('pointerdown', onPointer, true);
  };
}

export function stopWatchDismiss(container) {
  if (container && container.__dismiss) { container.__dismiss(); container.__dismiss = null; }
}
