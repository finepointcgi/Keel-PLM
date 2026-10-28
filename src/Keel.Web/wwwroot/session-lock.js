// Browser side of the idle session lock (NIST SP 800-171 3.1.10, ADR-0026). An ES module served from the application's own origin: no
// inline script, no eval, so the CSP (default-src 'self') is untouched.
//
// This script only REPORTS user activity and traps keyboard focus in the lock screen. It never decides anything: the server holds the
// session's idle clock and locks it with its own timer even if this script is blocked, suspended or dead.

const ACTIVITY_EVENTS = ['pointerdown', 'pointermove', 'keydown', 'touchstart', 'wheel', 'scroll'];
let stopActivity = null;
let releaseTrap = null;

/**
 * Starts reporting real user activity (pointer, key, touch, wheel, scroll) to the circuit, at most once per throttleMs. Returning to a
 * hidden tab asks the server to refresh the screen but is not counted as activity.
 * @param {{invokeMethodAsync: (name: string) => Promise<void>}} dotnet  the circuit's reference (methods Activity and Sync)
 * @param {number} throttleMs
 */
export function start(dotnet, throttleMs) {
  stop();
  let last = 0;
  const report = () => {
    if (document.hidden) return;
    const now = Date.now();
    if (now - last < throttleMs) return;
    last = now;
    dotnet.invokeMethodAsync('Activity').catch(() => { /* the circuit is gone: nothing to report to */ });
  };
  const onVisible = () => {
    if (!document.hidden) dotnet.invokeMethodAsync('Sync').catch(() => { });
  };
  for (const name of ACTIVITY_EVENTS) window.addEventListener(name, report, { capture: true, passive: true });
  document.addEventListener('visibilitychange', onVisible);
  stopActivity = () => {
    for (const name of ACTIVITY_EVENTS) window.removeEventListener(name, report, { capture: true });
    document.removeEventListener('visibilitychange', onVisible);
  };
}

export function stop() {
  if (stopActivity) stopActivity();
  stopActivity = null;
}

/**
 * Keeps keyboard focus inside the lock screen (a modal dialog): Tab and Shift+Tab cycle through its controls, and focus that escapes is
 * pulled back. The first control gets the focus.
 * @param {HTMLElement} container
 */
export function trapFocus(container) {
  releaseFocusTrap();
  if (!container) return;
  const selector = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
  const focusables = () => Array.from(container.querySelectorAll(selector)).filter(el => !el.hasAttribute('hidden'));
  const previous = document.activeElement;
  (focusables()[0] || container).focus();

  const onKeyDown = event => {
    if (event.key !== 'Tab') return;
    const items = focusables();
    if (items.length === 0) { event.preventDefault(); container.focus(); return; }
    const first = items[0];
    const last = items[items.length - 1];
    const inside = container.contains(document.activeElement);
    if (event.shiftKey && (document.activeElement === first || !inside)) { event.preventDefault(); last.focus(); }
    else if (!event.shiftKey && (document.activeElement === last || !inside)) { event.preventDefault(); first.focus(); }
  };
  const onFocusIn = event => {
    if (!container.contains(event.target)) (focusables()[0] || container).focus();
  };
  document.addEventListener('keydown', onKeyDown, true);
  document.addEventListener('focusin', onFocusIn, true);
  releaseTrap = () => {
    document.removeEventListener('keydown', onKeyDown, true);
    document.removeEventListener('focusin', onFocusIn, true);
    if (previous && typeof previous.focus === 'function' && document.contains(previous)) previous.focus();
  };
}

export function releaseFocusTrap() {
  if (releaseTrap) releaseTrap();
  releaseTrap = null;
}
