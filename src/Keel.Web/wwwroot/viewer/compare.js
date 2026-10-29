// Visual comparison of two versions of a model (increment 9.1, ADR-0046): overlay (A tinted blue, B orange, with a transparency slider), side by side with one
// shared camera, and a swipe divider. Both meshes arrive from the server's access-checked mesh route as plain float32 triangles in the coordinates of their
// files; nothing is moved, rotated (beyond standing a Z-up pair upright) or scaled to line them up: there is no automatic registration. The marking banner,
// the distribution statement and the overlay are protected by the same banner guard as the file viewer. No inline script, no eval, same-origin requests only.
import * as T from './vendor/three-bundle.js';
import { guardBanners } from './banner-guard.js';
import { addButton, addRange, addSelect, addSeparator, createOverlay, setPressed } from './viewer-ui.js';

const body = document.body;
const statusEl = document.getElementById('viewer-status');
const progressEl = document.getElementById('viewer-progress');
const stage = document.getElementById('viewer-stage');
const toolbar = document.getElementById('viewer-toolbar');

const COLOR_A = 0x3b82f6;
const COLOR_B = 0xf59e0b;

class CompareError extends Error {}

let markings = null;

function fail(message) {
  markings?.releaseOverlays();
  progressEl?.setAttribute('hidden', '');
  statusEl?.remove();
  const panel = document.createElement('section');
  panel.className = 'viewer-panel viewer-failure';
  panel.setAttribute('role', 'alert');
  const h = document.createElement('h2');
  h.textContent = 'The comparison cannot be shown';
  const p = document.createElement('p');
  p.textContent = message;
  panel.append(h, p);
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

async function fetchMesh(url, label) {
  const response = await fetch(url, { credentials: 'same-origin', redirect: 'manual' });
  if (response.type === 'opaqueredirect') throw new CompareError('Your session has ended. Reload the page to sign in again.');
  if (!response.ok) throw new CompareError(`Version ${label}: ${await problemText(response)}`);
  const buffer = await response.arrayBuffer();
  if (buffer.byteLength === 0 || buffer.byteLength % 36 !== 0) throw new CompareError(`Version ${label} did not arrive as a valid model.`);
  return {
    positions: new Float32Array(buffer),
    triangles: buffer.byteLength / 36,
    up: response.headers.get('X-Keel-Up') === 'y' ? 'y' : 'z',
    unit: response.headers.get('X-Keel-Unit') || 'units',
  };
}

function makeMesh(data, color) {
  const geometry = new T.BufferGeometry();
  geometry.setAttribute('position', new T.Float32BufferAttribute(data.positions, 3));
  geometry.computeVertexNormals(); // non-indexed: flat shading, which shows the facets of a mesh honestly
  const material = new T.MeshStandardMaterial({ color, metalness: 0.1, roughness: 0.6, side: T.DoubleSide, transparent: false, opacity: 1 });
  return new T.Mesh(geometry, material);
}

async function main() {
  const labelA = body.dataset.labelA;
  const labelB = body.dataset.labelB;
  markings = guardBanners({ banner: document.querySelector('[data-viewer-banner][data-position="top"]')?.textContent ?? '' });
  const maxTriangles = Number(body.dataset.maxTriangles) || 1_000_000;
  progressEl?.removeAttribute('hidden');
  progressEl?.removeAttribute('value');
  const [a, b] = await Promise.all([fetchMesh(body.dataset.meshA, labelA), fetchMesh(body.dataset.meshB, labelB)]);
  progressEl?.setAttribute('hidden', '');
  if (a.triangles > maxTriangles || b.triangles > maxTriangles) throw new CompareError(`A version has more than ${maxTriangles.toLocaleString('en-US')} triangles, more than the comparison shows.`);

  const meshA = makeMesh(a, COLOR_A);
  const meshB = makeMesh(b, COLOR_B);
  // Models of Z-up formats (STL, 3MF) are stood upright together; a mixed pair is left exactly as the files place it.
  const upright = a.up === 'z' && b.up === 'z';
  const root = new T.Group();
  root.add(meshA, meshB);
  root.rotation.x = upright ? -Math.PI / 2 : 0;

  const host = document.createElement('div');
  host.className = 'scene-host';
  stage.replaceChildren(host);
  statusEl?.remove();

  let renderer;
  try {
    renderer = new T.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
  } catch {
    fail('3D graphics (WebGL) is not available in this browser, so the comparison cannot be shown. The numbers on the Versions tab still apply.');
    return;
  }
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
  const canvas = renderer.domElement;
  canvas.setAttribute('role', 'img');
  canvas.setAttribute('aria-label', `3D comparison of version ${labelA} (blue) and version ${labelB} (orange). Drag to orbit, right-drag or arrow keys to pan, wheel to zoom.`);
  canvas.tabIndex = 0;
  host.append(canvas);

  const scene = new T.Scene();
  scene.background = new T.Color(getComputedStyle(stage).getPropertyValue('--viewer-stage-bg').trim() || '#1b2330');
  scene.add(root);
  const camera = new T.PerspectiveCamera(40, 1, 0.1, 1000);
  scene.add(camera);
  scene.add(new T.HemisphereLight(0xffffff, 0x3a4150, 1.6));
  const key = new T.DirectionalLight(0xffffff, 2.2);
  key.position.set(1, 1.5, 2);
  camera.add(key);
  const fill = new T.DirectionalLight(0xffffff, 0.8);
  fill.position.set(-2, -1, 1);
  camera.add(fill);

  root.updateMatrixWorld(true);
  const box = new T.Box3().setFromObject(root);
  if (box.isEmpty()) { fail('The models have no measurable size.'); return; }
  const center = box.getCenter(new T.Vector3());
  const radius = Math.max(box.getSize(new T.Vector3()).length() / 2, 1e-6);
  camera.near = radius / 200;
  camera.far = radius * 200;
  camera.updateProjectionMatrix();

  const controls = new T.OrbitControls(camera, canvas);
  controls.screenSpacePanning = true;
  controls.enableDamping = false;
  controls.listenToKeyEvents(canvas);
  controls.addEventListener('change', scheduleRender);
  const overlay = createOverlay(markings, stage);

  // ---- state ---------------------------------------------------------------------------------------------------------------------------
  let mode = 'overlay';
  let transparency = 35; // percent
  let swipe = 50; // percent of the width that shows A
  let frame = 0;
  let disposed = false;

  const dividerEl = document.createElement('div');
  dividerEl.className = 'compare-divider';
  dividerEl.setAttribute('aria-hidden', 'true');
  dividerEl.hidden = true;
  const tagA = document.createElement('div');
  tagA.className = 'compare-tag compare-tag-a';
  tagA.textContent = `A · ${labelA}`;
  const tagB = document.createElement('div');
  tagB.className = 'compare-tag compare-tag-b';
  tagB.textContent = `B · ${labelB}`;
  for (const t of [tagA, tagB]) { t.hidden = true; t.setAttribute('aria-hidden', 'true'); }
  stage.append(dividerEl, tagA, tagB);

  function scheduleRender() {
    if (frame || disposed) return;
    frame = requestAnimationFrame(() => { frame = 0; renderNow(); });
  }
  function requestRender() {
    if (disposed) return;
    cancelAnimationFrame(frame);
    frame = 0;
    renderNow();
  }

  function applyMaterials() {
    const translucent = mode === 'overlay' && transparency > 0;
    const opacity = translucent ? 1 - (transparency / 100) * 0.85 : 1;
    for (const m of [meshA, meshB]) {
      m.material.transparent = translucent;
      m.material.opacity = opacity;
      m.material.depthWrite = !translucent;
      m.material.needsUpdate = true;
    }
  }

  function renderNow() {
    if (disposed) return;
    const w = Math.max(1, host.clientWidth);
    const h = Math.max(1, host.clientHeight);
    renderer.setScissorTest(true);
    const pass = (viewport, scissor, showA, showB, aspect) => {
      meshA.visible = showA;
      meshB.visible = showB;
      camera.aspect = aspect;
      camera.updateProjectionMatrix();
      renderer.setViewport(...viewport);
      renderer.setScissor(...scissor);
      renderer.render(scene, camera);
    };
    if (mode === 'overlay') {
      pass([0, 0, w, h], [0, 0, w, h], true, true, w / h);
    } else if (mode === 'side') {
      const half = Math.floor(w / 2);
      pass([0, 0, half, h], [0, 0, half, h], true, false, half / h);
      pass([half, 0, w - half, h], [half, 0, w - half, h], false, true, (w - half) / h);
    } else {
      const split = Math.round((w * swipe) / 100);
      pass([0, 0, w, h], [0, 0, split, h], true, false, w / h);
      pass([0, 0, w, h], [split, 0, w - split, h], false, true, w / h);
    }
    meshA.visible = true;
    meshB.visible = true;
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    layoutMarks(w);
  }

  function layoutMarks(w) {
    const split = mode === 'side' ? Math.floor(w / 2) : mode === 'swipe' ? Math.round((w * swipe) / 100) : null;
    dividerEl.hidden = split === null;
    tagA.hidden = tagB.hidden = mode === 'overlay';
    if (split !== null) {
      dividerEl.style.left = `${split - 1}px`;
      tagA.style.left = '8px';
      tagB.style.left = `${Math.min(w - 8 - tagB.offsetWidth, split + 8)}px`;
    }
  }

  function resize() {
    const w = Math.max(1, host.clientWidth);
    const h = Math.max(1, host.clientHeight);
    renderer.setSize(w, h, false);
    requestRender();
  }
  const resizeObserver = new ResizeObserver(resize);
  resizeObserver.observe(host);

  // ---- views ---------------------------------------------------------------------------------------------------------------------------
  const DIRECTIONS = { iso: [1, 0.8, 1], front: [0, 0, 1], back: [0, 0, -1], right: [1, 0, 0], left: [-1, 0, 0], top: [0, 1, 0.001], bottom: [0, -1, 0.001] };
  function fitDistance() {
    const w = Math.max(1, host.clientWidth);
    const h = Math.max(1, host.clientHeight);
    const aspect = (mode === 'side' ? w / 2 : w) / h;
    const fov = (camera.fov * Math.PI) / 180;
    const fitH = radius / Math.sin(fov / 2);
    const fitW = radius / Math.sin(Math.atan(Math.tan(fov / 2) * aspect));
    return Math.max(fitH, fitW) * 1.05;
  }
  function setView(name) {
    const d = new T.Vector3(...DIRECTIONS[name]).normalize();
    camera.position.copy(center).addScaledVector(d, fitDistance());
    controls.target.copy(center);
    camera.lookAt(center);
    controls.update();
    requestRender();
  }
  const fitView = () => setView('iso');

  // ---- toolbar -------------------------------------------------------------------------------------------------------------------------
  const modeButtons = {};
  function setMode(next) {
    mode = next;
    for (const [id, button] of Object.entries(modeButtons)) setPressed(button, id === mode);
    transparencyGroup.hidden = mode !== 'overlay';
    swipeGroup.hidden = mode !== 'swipe';
    stage.dataset.mode = mode;
    applyMaterials();
    setView('iso');
  }
  modeButtons.overlay = addButton(toolbar, 'Overlay', () => setMode('overlay'), { key: '1', pressed: true, title: 'Both versions in one view: A blue, B orange (1)' });
  modeButtons.side = addButton(toolbar, 'Side by side', () => setMode('side'), { key: '2', pressed: false, title: 'A on the left, B on the right, with one shared camera (2)' });
  modeButtons.swipe = addButton(toolbar, 'Swipe', () => setMode('swipe'), { key: '3', pressed: false, title: 'A on the left of a divider, B on the right (3)' });
  addSeparator(toolbar);
  const transparencyGroup = document.createElement('span');
  transparencyGroup.className = 'viewer-group';
  toolbar.append(transparencyGroup);
  addRange(transparencyGroup, 'Transparency', { min: 0, max: 100, value: transparency }, (v) => { transparency = v; applyMaterials(); requestRender(); });
  const swipeGroup = document.createElement('span');
  swipeGroup.className = 'viewer-group';
  swipeGroup.hidden = true;
  toolbar.append(swipeGroup);
  addRange(swipeGroup, 'Divider', { min: 0, max: 100, value: swipe }, (v) => { swipe = v; requestRender(); });
  addButton(toolbar, 'Fit', fitView, { key: 'F' });
  addSelect(toolbar, 'View', [['', 'Standard views…'], ['iso', 'Isometric'], ['front', 'Front'], ['back', 'Back'], ['left', 'Left'], ['right', 'Right'], ['top', 'Top'], ['bottom', 'Bottom']],
    (v, select) => { if (v) setView(v); select.value = ''; });
  const readout = document.createElement('div');
  readout.className = 'viewer-readout viewer-readout-block';
  readout.setAttribute('role', 'status');
  readout.setAttribute('aria-live', 'polite');
  readout.textContent = `A (${labelA}) is blue, B (${labelB}) is orange. ${a.triangles.toLocaleString('en-US')} and ${b.triangles.toLocaleString('en-US')} triangles.${upright ? '' : ' The two files use different up axes and are shown as stored.'}`;
  toolbar.append(readout);
  toolbar.hidden = false;

  canvas.addEventListener('keydown', (e) => {
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    const k = e.key.toLowerCase();
    if (k === 'f') { fitView(); e.preventDefault(); }
    else if (k === '1') { setMode('overlay'); e.preventDefault(); }
    else if (k === '2') { setMode('side'); e.preventDefault(); }
    else if (k === '3') { setMode('swipe'); e.preventDefault(); }
  });

  function dispose() {
    if (disposed) return;
    disposed = true;
    cancelAnimationFrame(frame);
    resizeObserver.disconnect();
    controls.dispose();
    for (const m of [meshA, meshB]) { m.geometry.dispose(); m.material.dispose(); }
    renderer.dispose();
    renderer.forceContextLoss();
  }
  window.addEventListener('pagehide', dispose);

  resize();
  setMode('overlay');
  body.dataset.viewerState = 'ready';
  stage.dataset.ready = 'true';
  stage.dataset.trianglesA = String(a.triangles);
  stage.dataset.trianglesB = String(b.triangles);
  // A narrow hook for the automated tests: nothing here reaches the file's bytes, and it does not bypass the overlay.
  window.__keelViewer = {
    screenshotDataUrl: () => { renderNow(); return canvas.toDataURL('image/png'); },
    dispose, canvas, overlay, setMode, mode: () => mode,
  };
}

main().catch((e) => {
  if (e instanceof CompareError) fail(e.message);
  else fail(e && e.message ? e.message : 'The comparison could not be loaded.');
});
