// Small DOM helpers shared by the viewers: toolbar controls and the marking overlay. DOM construction only; styles come from viewer.css.

/** Adds a toolbar button. `pressed` makes it a toggle (aria-pressed). Returns the button. */
export function addButton(toolbar, label, onClick, { key, pressed, title } = {}) {
  const b = document.createElement('button');
  b.type = 'button';
  b.textContent = label;
  b.className = 'viewer-tool';
  if (key) b.setAttribute('aria-keyshortcuts', key);
  b.title = title ?? (key ? `${label} (${key})` : label);
  if (pressed !== undefined) b.setAttribute('aria-pressed', String(pressed));
  b.addEventListener('click', () => onClick(b));
  toolbar.append(b);
  return b;
}

export function setPressed(button, value) { button.setAttribute('aria-pressed', String(value)); }

export function addSeparator(toolbar) {
  const s = document.createElement('span');
  s.className = 'viewer-sep';
  s.setAttribute('role', 'separator');
  toolbar.append(s);
}

export function addSelect(toolbar, label, options, onChange) {
  const wrap = document.createElement('label');
  wrap.className = 'viewer-field';
  wrap.append(document.createTextNode(label + ' '));
  const select = document.createElement('select');
  for (const [value, text] of options) {
    const o = document.createElement('option');
    o.value = value;
    o.textContent = text;
    select.append(o);
  }
  select.addEventListener('change', () => onChange(select.value, select));
  wrap.append(select);
  toolbar.append(wrap);
  return select;
}

export function addRange(toolbar, label, { min = 0, max = 100, value = 0, step = 1 }, onInput) {
  const wrap = document.createElement('label');
  wrap.className = 'viewer-field';
  wrap.append(document.createTextNode(label + ' '));
  const input = document.createElement('input');
  input.type = 'range';
  input.min = String(min);
  input.max = String(max);
  input.step = String(step);
  input.value = String(value);
  input.addEventListener('input', () => onInput(Number(input.value), input));
  wrap.append(input);
  toolbar.append(wrap);
  return { wrap, input };
}

/**
 * The marking overlay of a file that cannot be stamped in place (3D models, images): the banner above and the distribution statement below,
 * drawn over the stage. It is registered with the banner guard, which restores it and hides the file if it is removed or hidden.
 */
export function createOverlay(markings, stage) {
  const { banner, distribution } = markings.info();
  const overlay = document.createElement('div');
  overlay.className = 'viewer-overlay';
  overlay.dataset.viewerOverlay = '';
  const top = document.createElement('div');
  top.className = 'viewer-overlay-top';
  top.textContent = banner;
  const bottom = document.createElement('div');
  bottom.className = 'viewer-overlay-bottom';
  bottom.textContent = distribution;
  overlay.append(top, bottom);
  stage.append(overlay);
  markings.protect(overlay, stage);
  return overlay;
}
