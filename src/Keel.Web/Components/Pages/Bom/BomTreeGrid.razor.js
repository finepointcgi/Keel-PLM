// Blazor's Virtualize positions its top and bottom spacers with inline style attributes. The CSP (default-src 'self', ADR-0014)
// blocks inline styles, which would leave both spacers at zero height and make rows beyond the first window unreachable.
// Writes through the CSSOM (element.style) are exempt from CSP, so this module mirrors each style attribute Blazor sets onto
// element.style. It touches nothing else and runs only for the grid it is enabled on.
const observers = new WeakMap();

function mirror(el) {
  const css = el.getAttribute('style');
  // Skip when already applied: the CSSOM write rewrites the attribute, which must not retrigger this.
  if (css && el.style.cssText !== css) el.style.cssText = css;
}

function mirrorTree(node) {
  if (node.nodeType !== Node.ELEMENT_NODE) return;
  if (node.hasAttribute('style')) mirror(node);
  node.querySelectorAll('[style]').forEach(mirror);
}

export function enable(root) {
  if (!root || observers.has(root)) return;
  mirrorTree(root);
  const observer = new MutationObserver(records => {
    for (const record of records) {
      if (record.type === 'attributes') mirror(record.target);
      else record.addedNodes.forEach(mirrorTree);
    }
  });
  observer.observe(root, { attributes: true, attributeFilter: ['style'], subtree: true, childList: true });
  observers.set(root, observer);
}

export function disable(root) {
  observers.get(root)?.disconnect();
  observers.delete(root);
}
