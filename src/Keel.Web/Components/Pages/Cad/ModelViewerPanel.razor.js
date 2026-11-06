// The Viewer tab of document and part pages (increment 9.5, ADR-0052): a placed-box view of a STEP assembly (each part as its bounding box at the position the
// file places it) and a triangle view of STL, OBJ and 3MF models. Rendering uses the vendored three.js bundle of the file viewer (MIT, same file, same hash;
// nothing is loaded from the network). No inline script, no eval, no inline style (styles are set through the CSSOM). The picture has a text alternative: the
// structure list next to it. Keyboard: arrow keys orbit, Shift + arrow keys pan, + and - zoom, 0 or Home reset the view, Enter or Space is not needed (the list selects).
import * as T from '/_content/Keel.Web/viewer/vendor/three-bundle.js';

const views = new WeakMap();
const TWO_PI = Math.PI * 2;

function tokens() {
  const css = getComputedStyle(document.documentElement);
  const read = (name, fallback) => css.getPropertyValue(name).trim() || fallback;
  // Forced colours (Windows high contrast): the author's tokens are replaced by the system palette, which only a real element can resolve.
  if (window.matchMedia('(forced-colors: active)').matches) {
    const probe = document.createElement('span');
    probe.style.position = 'absolute';
    probe.style.visibility = 'hidden';
    document.body.append(probe);
    const resolve = name => { probe.style.color = name; return getComputedStyle(probe).color; };
    const result = { bg: resolve('Canvas'), text: resolve('CanvasText'), part: resolve('Canvas'), accent: resolve('Highlight'), selected: resolve('Highlight'), muted: resolve('GrayText') };
    probe.remove();
    return result;
  }
  return {
    bg: read('--c-surface-2', '#edf0f6'),
    text: read('--c-text', '#172033'),
    part: read('--c-surface', '#ffffff'),
    accent: read('--c-accent', '#2a4fd0'),
    selected: read('--c-focus', '#2a4fd0'),
    muted: read('--c-muted', '#4a5568'),
  };
}

function color(value) {
  const c = new T.Color();
  try { c.set(value); } catch { c.set('#808080'); }
  return c;
}

function transformPoint(m, x, y, z) {
  return [
    m[0] * x + m[1] * y + m[2] * z + m[3],
    m[4] * x + m[5] * y + m[6] * z + m[7],
    m[8] * x + m[9] * y + m[10] * z + m[11],
  ];
}

// The 12 triangles of a box (corner order: bit 0 = x, bit 1 = y, bit 2 = z).
const FACES = [[0, 2, 3, 0, 3, 1], [4, 5, 7, 4, 7, 6], [0, 1, 5, 0, 5, 4], [2, 6, 7, 2, 7, 3], [0, 4, 6, 0, 6, 2], [1, 3, 7, 1, 7, 5]];
// A single path that traces all 12 edges (it retraces three of them, so one Line object draws the whole box).
const EDGE_PATH = [0, 1, 3, 2, 0, 4, 5, 1, 5, 7, 3, 7, 6, 2, 6, 4];

function boxCorners(b, w) {
  const out = [];
  for (let i = 0; i < 8; i++) out.push(transformPoint(w, i & 1 ? b[3] : b[0], i & 2 ? b[4] : b[1], i & 4 ? b[5] : b[2]));
  return out;
}

function boxObjects(corners) {
  const positions = [];
  for (const face of FACES) for (const i of face) positions.push(...corners[i]);
  const faces = new T.BufferGeometry();
  faces.setAttribute('position', new T.Float32BufferAttribute(positions, 3));
  faces.computeVertexNormals();
  const edgePositions = [];
  for (const i of EDGE_PATH) edgePositions.push(...corners[i]);
  const edges = new T.BufferGeometry();
  edges.setAttribute('position', new T.Float32BufferAttribute(edgePositions, 3));
  return { faces, edges };
}

function meshFromTriangles(positions) {
  const geometry = new T.BufferGeometry();
  geometry.setAttribute('position', new T.Float32BufferAttribute(positions, 3));
  geometry.computeVertexNormals();
  return geometry;
}

export async function init(host, payload, dotnet) {
  // The panel can be gone again (the person switched tab) by the time the module loads: nothing to draw into, and not an error for the page.
  if (!host?.isConnected) return;
  dispose(host);
  const canvas = host.querySelector('canvas');
  const stage = host.closest('.mv-stage') ?? host.parentElement;
  const status = stage.querySelector('[data-mv-status]');
  const say = text => { if (status) status.textContent = text; };
  let renderer;
  try {
    renderer = new T.WebGLRenderer({ canvas, antialias: true, preserveDrawingBuffer: true });
  } catch {
    host.dataset.viewerState = 'unavailable';
    say('3D graphics (WebGL) is not available in this browser. The structure list shows everything the picture would.');
    return;
  }
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));

  const abort = new AbortController();
  const on = (el, type, fn, opts = {}) => el.addEventListener(type, fn, { ...opts, signal: abort.signal });
  const scene = new T.Scene();
  const camera = new T.PerspectiveCamera(40, 1, 0.01, 1e7);
  camera.up.set(0, 0, 1); // CAD files are Z up
  scene.add(new T.HemisphereLight(0xffffff, 0x666666, 1.3));
  const sun = new T.DirectionalLight(0xffffff, 1.6);
  scene.add(sun);
  const root = new T.Group();
  scene.add(root);

  const state = {
    theta: -Math.PI / 4, phi: Math.PI / 3, radius: 100, target: new T.Vector3(),
    home: null, disposed: false, frame: 0, items: [], selected: -1, parents: payload.instances?.map(i => i.parent) ?? [], palette: tokens(),
  };
  if (payload.kind === 'mesh' && payload.up === 'y') { camera.up.set(0, 1, 0); }

  function place() {
    const { theta, phi, radius, target } = state;
    const up = camera.up.y === 1 ? 'y' : 'z';
    if (up === 'z') camera.position.set(target.x + radius * Math.sin(phi) * Math.cos(theta), target.y + radius * Math.sin(phi) * Math.sin(theta), target.z + radius * Math.cos(phi));
    else camera.position.set(target.x + radius * Math.sin(phi) * Math.cos(theta), target.y + radius * Math.cos(phi), target.z + radius * Math.sin(phi) * Math.sin(theta));
    camera.lookAt(target);
    sun.position.copy(camera.position);
    host.dataset.camera = [state.theta, state.phi, state.radius].map(v => v.toFixed(3)).join(',');
  }

  function resize() {
    const w = Math.max(canvas.clientWidth, 100);
    const h = Math.max(canvas.clientHeight, 100);
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    draw();
  }

  function draw() {
    if (state.disposed || state.frame) return;
    state.frame = requestAnimationFrame(() => {
      state.frame = 0;
      if (state.disposed) return;
      place();
      renderer.render(scene, camera);
    });
  }

  function clamp() {
    state.phi = Math.min(Math.max(state.phi, 0.05), Math.PI - 0.05);
    state.theta = ((state.theta % TWO_PI) + TWO_PI) % TWO_PI;
    state.radius = Math.min(Math.max(state.radius, state.home.radius * 0.02), state.home.radius * 20);
  }

  function orbit(dTheta, dPhi) { state.theta += dTheta; state.phi += dPhi; clamp(); draw(); }
  function zoom(factor) { state.radius *= factor; clamp(); draw(); }
  function pan(dx, dy) {
    // move the target in the screen plane
    const k = state.radius * 0.0016;
    const right = new T.Vector3().setFromMatrixColumn(camera.matrix, 0);
    const upv = new T.Vector3().setFromMatrixColumn(camera.matrix, 1);
    state.target.addScaledVector(right, -dx * k).addScaledVector(upv, dy * k);
    draw();
  }
  function reset() {
    state.theta = state.home.theta; state.phi = state.home.phi; state.radius = state.home.radius; state.target.copy(state.home.target);
    draw();
  }

  // ---- content
  function paint() {
    state.palette = tokens();
    renderer.setClearColor(color(state.palette.bg), 1);
    for (const item of state.items) {
      if (!item) continue; // assemblies have no box of their own
      const sel = item.selected;
      item.faceMaterial.color.copy(color(sel ? state.palette.selected : item.placed ? state.palette.part : state.palette.bg));
      item.faceMaterial.opacity = sel ? 0.95 : item.placed ? 0.55 : 0.3;
      item.edgeMaterial.color.copy(color(sel ? state.palette.selected : item.placed ? state.palette.text : state.palette.muted));
    }
    if (state.meshMaterial) state.meshMaterial.color.copy(color(state.palette.accent));
    draw();
  }

  function fit(min, max) {
    const center = new T.Vector3((min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2);
    const diagonal = Math.max(Math.hypot(max[0] - min[0], max[1] - min[1], max[2] - min[2]), 1e-6);
    state.home = { theta: state.theta, phi: state.phi, radius: diagonal * 1.6, target: center.clone() };
    reset();
  }

  if (payload.kind === 'assembly') {
    let min = [Infinity, Infinity, Infinity];
    let max = [-Infinity, -Infinity, -Infinity];
    let drawn = 0;
    for (const inst of payload.instances) {
      if (inst.assembly || !inst.box) { state.items[inst.index] = null; continue; }
      const { faces, edges } = boxObjects(boxCorners(inst.box, inst.world));
      const faceMaterial = new T.MeshStandardMaterial({ color: 0xffffff, metalness: 0.05, roughness: 0.7, side: T.DoubleSide, transparent: true, opacity: 0.55 });
      const edgeMaterial = new T.LineBasicMaterial({ color: 0x000000 });
      const mesh = new T.Mesh(faces, faceMaterial);
      const line = new T.Line(edges, edgeMaterial);
      mesh.userData.index = inst.index;
      root.add(mesh, line);
      state.items[inst.index] = { mesh, line, faceMaterial, edgeMaterial, placed: inst.placed, selected: false };
      drawn++;
      const p = faces.getAttribute('position');
      for (let i = 0; i < p.count; i++) {
        min = [Math.min(min[0], p.getX(i)), Math.min(min[1], p.getY(i)), Math.min(min[2], p.getZ(i))];
        max = [Math.max(max[0], p.getX(i)), Math.max(max[1], p.getY(i)), Math.max(max[2], p.getZ(i))];
      }
    }
    host.dataset.boxes = String(drawn);
    if (drawn === 0) {
      host.dataset.viewerState = 'empty';
      say('No part of this file has a box that can be drawn. The structure list shows what the file contains.');
      renderer.dispose();
      return;
    }
    fit(min, max);
    say(`${drawn} part${drawn === 1 ? '' : 's'} drawn as bounding boxes.`);
  } else if (payload.kind === 'mesh') {
    say('Loading the model.');
    let data;
    try {
      const response = await fetch(payload.meshUrl, { credentials: 'same-origin', redirect: 'manual' });
      if (response.type === 'opaqueredirect') throw new Error('Your session has ended. Reload the page to sign in again.');
      if (!response.ok) {
        let detail = `The server answered ${response.status}.`;
        try { const problem = await response.json(); detail = problem.detail || problem.title || detail; } catch { /* keep the status text */ }
        throw new Error(detail);
      }
      const buffer = await response.arrayBuffer();
      if (buffer.byteLength === 0 || buffer.byteLength % 36 !== 0) throw new Error('The model did not arrive as a valid mesh.');
      data = { positions: new Float32Array(buffer), up: response.headers.get('X-Keel-Up') === 'y' ? 'y' : 'z' };
    } catch (error) {
      host.dataset.viewerState = 'error';
      say(error.message);
      renderer.dispose();
      return;
    }
    if (data.up === 'y') camera.up.set(0, 1, 0);
    const geometry = meshFromTriangles(data.positions);
    state.meshMaterial = new T.MeshStandardMaterial({ color: 0x808080, metalness: 0.1, roughness: 0.6, side: T.DoubleSide });
    root.add(new T.Mesh(geometry, state.meshMaterial));
    geometry.computeBoundingBox();
    const b = geometry.boundingBox;
    host.dataset.triangles = String(data.positions.length / 9);
    fit([b.min.x, b.min.y, b.min.z], [b.max.x, b.max.y, b.max.z]);
    say(`${(data.positions.length / 9).toLocaleString('en-US')} triangles.`);
  }

  // ---- interaction
  const pointers = new Map();
  let moved = 0;
  on(host, 'pointerdown', e => {
    if (e.button !== 0 && e.button !== 2) return;
    host.setPointerCapture(e.pointerId);
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY, pan: e.button === 2 || e.shiftKey });
    moved = 0;
  });
  on(host, 'pointermove', e => {
    const p = pointers.get(e.pointerId);
    if (!p) return;
    const dx = e.clientX - p.x;
    const dy = e.clientY - p.y;
    p.x = e.clientX; p.y = e.clientY;
    moved += Math.abs(dx) + Math.abs(dy);
    if (p.pan) pan(dx, dy); else orbit(-dx * 0.01, -dy * 0.01);
  });
  on(host, 'pointerup', e => {
    const had = pointers.delete(e.pointerId);
    if (had && moved < 4 && payload.kind === 'assembly') pick(e);
  });
  on(host, 'pointercancel', e => pointers.delete(e.pointerId));
  on(host, 'contextmenu', e => e.preventDefault());
  on(host, 'wheel', e => { e.preventDefault(); zoom(e.deltaY > 0 ? 1.1 : 1 / 1.1); }, { passive: false });
  on(host, 'keydown', e => {
    if (e.ctrlKey || e.altKey || e.metaKey) return;
    const step = Math.PI / 12;
    let handled = true;
    switch (e.key) {
      case 'ArrowLeft': e.shiftKey ? pan(-40, 0) : orbit(step, 0); break;
      case 'ArrowRight': e.shiftKey ? pan(40, 0) : orbit(-step, 0); break;
      case 'ArrowUp': e.shiftKey ? pan(0, -40) : orbit(0, -step); break;
      case 'ArrowDown': e.shiftKey ? pan(0, 40) : orbit(0, step); break;
      case '+': case '=': zoom(1 / 1.2); break;
      case '-': case '_': zoom(1.2); break;
      case '0': case 'Home': reset(); say('View reset.'); break;
      default: handled = false;
    }
    if (handled) e.preventDefault();
  });

  const raycaster = new T.Raycaster();
  const ndc = new T.Vector2();
  function pick(e) {
    const rect = canvas.getBoundingClientRect();
    ndc.set(((e.clientX - rect.left) / rect.width) * 2 - 1, -(((e.clientY - rect.top) / rect.height) * 2 - 1));
    place();
    camera.updateMatrixWorld();
    raycaster.setFromCamera(ndc, camera);
    const hits = raycaster.intersectObjects(root.children.filter(o => o.userData.index !== undefined), false);
    if (hits.length > 0) dotnet?.invokeMethodAsync('OnPicked', hits[0].object.userData.index);
  }

  const toolbar = stage.querySelector('[data-mv-toolbar]');
  if (toolbar) on(toolbar, 'click', e => {
    const action = e.target.closest('[data-mv-action]')?.dataset.mvAction;
    switch (action) {
      case 'reset': reset(); say('View reset.'); break;
      case 'zoom-in': zoom(1 / 1.2); break;
      case 'zoom-out': zoom(1.2); break;
      case 'left': orbit(Math.PI / 12, 0); break;
      case 'right': orbit(-Math.PI / 12, 0); break;
      case 'up': orbit(0, -Math.PI / 12); break;
      case 'down': orbit(0, Math.PI / 12); break;
      default: break;
    }
    host.focus({ preventScroll: true });
  });

  const resizeObserver = new ResizeObserver(resize);
  resizeObserver.observe(canvas);
  const themeObserver = new MutationObserver(paint);
  themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme', 'data-density'] });
  const scheme = window.matchMedia('(prefers-color-scheme: dark)');
  on(scheme, 'change', paint);
  const forced = window.matchMedia('(forced-colors: active)');
  on(forced, 'change', paint);

  const view = {
    state, renderer, root, resizeObserver, themeObserver, scheme, forced, paint,
    selectInstance(index) {
      const descendants = new Set([index]);
      // children always come after their parent in the instance list
      for (let i = 0; i < state.parents.length; i++) if (descendants.has(state.parents[i])) descendants.add(i);
      for (let i = 0; i < state.items.length; i++) if (state.items[i]) state.items[i].selected = index >= 0 && descendants.has(i);
      state.selected = index;
      paint();
    },
    dispose() {
      if (state.disposed) return;
      state.disposed = true;
      cancelAnimationFrame(state.frame);
      resizeObserver.disconnect();
      themeObserver.disconnect();
      abort.abort();
      root.traverse(o => { o.geometry?.dispose?.(); o.material?.dispose?.(); });
      renderer.dispose();
    },
  };
  views.set(host, view);
  paint();
  resize();
  host.dataset.viewerState = 'ready';
}

export function select(host, index) { views.get(host)?.selectInstance(index); }

export function dispose(host) {
  const v = views.get(host);
  if (!v) return;
  v.dispose();
  views.delete(host);
}
