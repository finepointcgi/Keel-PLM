// Bootstrap of the standalone viewer document (ADR-0027). Reads the metadata the server describes, downloads the verified (and, for PDFs, marked)
// bytes through the cookie session, and hands them to the viewer for the detected type. No inline script, no eval, same-origin requests only.
import { guardBanners } from './banner-guard.js';

const body = document.body;
const statusEl = document.getElementById('viewer-status');
const progressEl = document.getElementById('viewer-progress');
const stage = document.getElementById('viewer-stage');
const toolbar = document.getElementById('viewer-toolbar');

/** Known-condition failure of the viewer: shown in an error panel, never logged as a console error. */
export class ViewerError extends Error {}

function setStatus(text) { if (statusEl) statusEl.textContent = text; }

function showProgress(fraction) {
  if (!progressEl) return;
  if (fraction === null) { progressEl.hidden = true; return; }
  progressEl.hidden = false;
  if (fraction < 0) progressEl.removeAttribute('value'); else progressEl.value = Math.round(fraction * 100);
}

let markingsRef = null;

function fail(message) {
  markingsRef?.releaseOverlays();
  showProgress(null);
  statusEl?.remove();
  const panel = document.createElement('section');
  panel.className = 'viewer-panel viewer-failure';
  panel.setAttribute('role', 'alert');
  const h = document.createElement('h2');
  h.textContent = 'This file cannot be shown';
  const p = document.createElement('p');
  p.textContent = message;
  panel.append(h, p);
  const download = document.querySelector('a.viewer-button[download]');
  if (download) {
    const a = download.cloneNode(true);
    panel.append(a);
  }
  stage.replaceChildren(panel);
  body.dataset.viewerState = 'error';
  body.dataset.viewerError = message;
}

async function problemText(response) {
  try {
    const problem = await response.json();
    return problem.detail || problem.title || `The server answered ${response.status}.`;
  } catch {
    return `The server answered ${response.status}.`;
  }
}

async function getJson(url) {
  const response = await fetch(url, { credentials: 'same-origin', redirect: 'manual', headers: { Accept: 'application/json' } });
  if (response.type === 'opaqueredirect') throw new ViewerError('Your session has ended. Reload the page to sign in again.');
  if (!response.ok) throw new ViewerError(await problemText(response));
  return response.json();
}

/** Downloads with progress. The server sends the whole verified file with an exact Content-Length. */
async function getBytes(url, expectedBytes) {
  const response = await fetch(url, { credentials: 'same-origin', redirect: 'manual' });
  if (response.type === 'opaqueredirect') throw new ViewerError('Your session has ended. Reload the page to sign in again.');
  if (!response.ok) throw new ViewerError(await problemText(response));
  const total = Number(response.headers.get('Content-Length')) || expectedBytes || 0;
  if (!response.body || !total) return new Uint8Array(await response.arrayBuffer());
  const out = new Uint8Array(total);
  const reader = response.body.getReader();
  let received = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    if (received + value.length > out.length) throw new ViewerError('The file is larger than the server announced.');
    out.set(value, received);
    received += value.length;
    showProgress(received / total);
  }
  if (received !== total) throw new ViewerError('The download ended early. Reload the page to try again.');
  return out;
}

async function main() {
  const meta = await getJson(body.dataset.metaUrl);
  if (meta.status !== 'ready') { fail(meta.message || 'This file cannot be previewed.'); return; }
  const markings = guardBanners(meta);
  markingsRef = markings;

  setStatus(`Loading ${meta.fileName} (${(meta.sizeBytes / 1048576).toFixed(1)} MiB)…`);
  showProgress(0);
  const bytes = await getBytes(body.dataset.contentUrl, meta.sizeBytes);
  showProgress(null);

  const context = {
    stage, toolbar, meta, bytes, markings,
    assetBase: body.dataset.assetBase,
    maxTriangles: Number(body.dataset.maxTriangles) || 5_000_000,
    setStatus, showProgress, fail, ViewerError,
  };
  const modules = { pdf: './viewer-pdf.js', image: './viewer-image.js', model3d: './viewer-3d.js' };
  const path = modules[meta.viewer];
  if (!path) { fail('There is no preview for this type of file.'); return; }
  const viewer = await import(path);
  body.dataset.viewerState = 'loading';
  await viewer.mount(context);
}

main().catch((e) => {
  if (e instanceof ViewerError) fail(e.message);
  else fail(e && e.message ? e.message : 'The viewer could not load this file.');
});
