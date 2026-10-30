// Scrolls the current change of a redline into view. Scrolling and focus are plain DOM calls (CSP-safe); nothing else is touched.
export function showChange(ids) {
  for (const id of ids) {
    const el = document.getElementById(id);
    if (el) {
      el.scrollIntoView({ block: 'center', inline: 'nearest' });
      return true;
    }
  }
  return false;
}

export function focusId(id) {
  const el = document.getElementById(id);
  if (el instanceof HTMLElement) el.focus({ preventScroll: true });
}
