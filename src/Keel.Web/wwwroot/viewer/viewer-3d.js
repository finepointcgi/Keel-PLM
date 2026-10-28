// 3D viewer (STL, glTF/GLB, OBJ, STEP/IGES) on three.js (MIT, vendored and bundled). STEP and IGES are converted in a Web Worker by the
// OpenCascade importer (occt-import-js, LGPL-2.1, vendored as a separate .wasm so it stays replaceable; see docs/third-party/viewer.md).
// Tools: orbit/pan/zoom, fit, standard views, shaded/wireframe, bounding box, point-to-point measurement, single-axis section, exploded view
// when the file has an assembly structure, and screenshots with the marking banner and distribution statement burned in.
import * as T from './vendor/three-bundle.js';
import { addButton, addRange, addSelect, addSeparator, createOverlay, setPressed } from './viewer-ui.js';

const CAD_UNITS = 'mm';

export async function mount(ctx) {
  const { stage, toolbar, meta, bytes, markings, assetBase, maxTriangles, setStatus, showProgress, fail } = ctx;

  // ---- load -----------------------------------------------------------------------------------------------------------------------------
  let loaded;
  try {
    loaded = await loadModel(meta, bytes, { assetBase, maxTriangles, setStatus, showProgress });
  } catch (e) {
    fail(e && e.message ? e.message : 'The model could not be loaded.');
    return;
  }
  const { root, groups, triangles, up: initialUp, units } = loaded;
  // The camera is always Y-up. Z-up data (CAD, STL) is stood up by rotating the model about X, so the standard views are the same for every format.
  let zUp = initialUp === 'z';
  root.rotation.x = zUp ? -Math.PI / 2 : 0;

  // ---- scene ----------------------------------------------------------------------------------------------------------------------------
  const host = document.createElement('div');
  host.className = 'scene-host';
  stage.replaceChildren(host);
  document.getElementById('viewer-status')?.remove();

  let renderer;
  try {
    renderer = new T.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
  } catch {
    disposeObject(root);
    fail('3D graphics (WebGL) is not available in this browser, so the model cannot be shown. You can download the file instead.');
    return;
  }
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
  const canvas = renderer.domElement;
  canvas.setAttribute('role', 'img');
  canvas.setAttribute('aria-label', `3D view of ${meta.fileName}. Drag to orbit, right-drag or arrow keys to pan, wheel to zoom.`);
  canvas.tabIndex = 0;
  host.append(canvas);

  const bg = new T.Color(getComputedStyle(stage).getPropertyValue('--viewer-stage-bg').trim() || '#1b2330');
  const scene = new T.Scene();
  scene.background = bg;
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

  const meshes = [];
  root.traverse((o) => { if (o.isMesh) meshes.push(o); });
  const box = new T.Box3();
  const size = new T.Vector3();
  const center = new T.Vector3();
  function updateBounds() {
    root.updateMatrixWorld(true);
    box.setFromObject(root);
    box.getSize(size);
    box.getCenter(center);
  }
  updateBounds();
  const radius = Math.max(size.length() / 2, 1e-6);
  // CAD axis name -> index of the world axis it lies on (a Z-up model is rotated, so CAD Y is world -Z and CAD Z is world Y).
  const axisIndex = () => (zUp ? { x: 0, y: 2, z: 1 } : { x: 0, y: 1, z: 2 });
  const comp = (v, a) => [v.x, v.y, v.z][axisIndex()[a]];
  camera.near = radius / 200;
  camera.far = radius * 200;
  camera.updateProjectionMatrix();

  const controls = new T.OrbitControls(camera, canvas);
  controls.screenSpacePanning = true;
  controls.enableDamping = false;
  controls.listenToKeyEvents(canvas);
  controls.addEventListener('change', scheduleRender);

  const overlay = createOverlay(markings, stage);

  // ---- rendering (on demand) -------------------------------------------------------------------------------------------------------------
  let frame = 0;
  let disposed = false;
  // Controls fire `change` many times per frame while dragging, so those renders are coalesced into one animation frame; every other change
  // (a view, a toggle, a section, an explode step) renders at once, so the canvas always shows the state the toolbar says.
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
  const labelEl = document.createElement('div');
  labelEl.className = 'viewer-measure-label';
  labelEl.hidden = true;
  stage.append(labelEl);
  function renderNow() {
    if (disposed) return;
    renderer.render(scene, camera);
    if (measure.segment) {
      const mid = measure.segment.mid.clone().project(camera);
      const w = host.clientWidth;
      const h = host.clientHeight;
      labelEl.style.transform = `translate(${(mid.x * 0.5 + 0.5) * w}px, ${(-mid.y * 0.5 + 0.5) * h}px)`;
    }
  }
  function resize() {
    const w = Math.max(1, host.clientWidth);
    const h = Math.max(1, host.clientHeight);
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    requestRender();
  }
  const resizeObserver = new ResizeObserver(resize);
  resizeObserver.observe(host);

  // ---- views ----------------------------------------------------------------------------------------------------------------------------
  // World directions (the camera is Y-up). The top and bottom views are tilted by a hair so the view direction is never parallel to the up vector.
  const DIRECTIONS = {
    iso: [1, 0.8, 1], front: [0, 0, 1], back: [0, 0, -1], right: [1, 0, 0], left: [-1, 0, 0], top: [0, 1, 0.001], bottom: [0, -1, 0.001],
  };
  function setView(name) {
    const d = new T.Vector3(...DIRECTIONS[name]).normalize();
    camera.position.copy(center).addScaledVector(d, fitDistance());
    controls.target.copy(center);
    camera.lookAt(center);
    controls.update();
    requestRender();
  }
  function fitDistance() {
    const fov = (camera.fov * Math.PI) / 180;
    const fitH = radius / Math.sin(fov / 2);
    const fitW = radius / Math.sin(Math.atan(Math.tan(fov / 2) * camera.aspect));
    return Math.max(fitH, fitW) * 1.05;
  }
  const fitView = () => setView('iso');

  // ---- toolbar ---------------------------------------------------------------------------------------------------------------------------
  const readout = document.createElement('div');
  readout.className = 'viewer-readout viewer-readout-block';
  readout.setAttribute('role', 'status');
  readout.setAttribute('aria-live', 'polite');
  const baseReadout = () => `Bounding box ${fmt(comp(size, 'x'))} × ${fmt(comp(size, 'y'))} × ${fmt(comp(size, 'z'))} ${units} · ${triangles.toLocaleString('en-US')} triangles`;
  readout.textContent = baseReadout();
  const unitNote = units === CAD_UNITS ? '' : ' (units not stated by the file)';
  readout.title = `Bounding box X × Y × Z${unitNote}`;

  addButton(toolbar, 'Fit', fitView, { key: 'F' });
  addSelect(toolbar, 'View', [['', 'Standard views…'], ['iso', 'Isometric'], ['front', 'Front'], ['back', 'Back'], ['left', 'Left'], ['right', 'Right'], ['top', 'Top'], ['bottom', 'Bottom']],
    (v, select) => { if (v) setView(v); select.value = ''; });
  const upButton = addButton(toolbar, zUp ? 'Z up' : 'Y up', () => {
    zUp = !zUp;
    upButton.textContent = zUp ? 'Z up' : 'Y up';
    root.rotation.x = zUp ? -Math.PI / 2 : 0;
    updateBounds();
    clearMeasure();
    applySection();
    prepareExplode();
    fitView();
  }, { title: 'Switch the up axis (CAD files are usually Z up, glTF is Y up)' });
  addSeparator(toolbar);

  let wire = false;
  addButton(toolbar, 'Wireframe', (b) => {
    wire = !wire;
    setPressed(b, wire);
    for (const m of meshes) for (const mat of materialsOf(m)) mat.wireframe = wire;
    requestRender();
  }, { key: 'W', pressed: false });

  // measurement
  const measure = { active: false, points: [], objects: [], segment: null };
  const markerRadius = radius * 0.012;
  const raycaster = new T.Raycaster();
  const measureButton = addButton(toolbar, 'Measure', () => setMeasure(!measure.active), { key: 'M', pressed: false, title: 'Measure the distance between two points on the model (M)' });
  addButton(toolbar, 'Clear measurement', clearMeasure);
  function setMeasure(on) {
    measure.active = on;
    setPressed(measureButton, on);
    canvas.classList.toggle('measuring', on);
    if (on) readout.textContent = 'Measure: click the first point on the model.';
    else readout.textContent = baseReadout();
  }
  function clearMeasure() {
    for (const o of measure.objects) { scene.remove(o); o.geometry.dispose(); o.material.dispose(); }
    measure.objects = [];
    measure.points = [];
    measure.segment = null;
    labelEl.hidden = true;
    readout.textContent = measure.active ? 'Measure: click the first point on the model.' : baseReadout();
    requestRender();
  }
  function pick(ev) {
    const r = canvas.getBoundingClientRect();
    raycaster.setFromCamera(new T.Vector2(((ev.clientX - r.left) / r.width) * 2 - 1, -((ev.clientY - r.top) / r.height) * 2 + 1), camera);
    for (const hit of raycaster.intersectObjects(meshes, false)) {
      if (!hit.object.visible) continue;
      if (section.active && section.plane.distanceToPoint(hit.point) < 0) continue; // clipped away: not on the visible part
      return hit.point.clone();
    }
    return null;
  }
  function addPoint(p) {
    if (measure.points.length >= 2) clearMeasure();
    measure.points.push(p);
    const marker = new T.Mesh(new T.SphereGeometry(markerRadius, 16, 12), new T.MeshBasicMaterial({ color: 0xff3b30, depthTest: false }));
    marker.position.copy(p);
    marker.renderOrder = 10;
    scene.add(marker);
    measure.objects.push(marker);
    if (measure.points.length === 1) {
      readout.textContent = 'Measure: click the second point.';
    } else {
      const [a, b] = measure.points;
      const line = new T.Line(new T.BufferGeometry().setFromPoints([a, b]), new T.LineBasicMaterial({ color: 0xff3b30, depthTest: false }));
      line.renderOrder = 10;
      scene.add(line);
      measure.objects.push(line);
      const d = a.distanceTo(b);
      const text = `${fmt(d)} ${units}`;
      measure.segment = { mid: a.clone().add(b).multiplyScalar(0.5) };
      labelEl.textContent = text;
      labelEl.hidden = false;
      readout.textContent = `Distance ${text} (ΔX ${fmt(Math.abs(comp(a, 'x') - comp(b, 'x')))}, ΔY ${fmt(Math.abs(comp(a, 'y') - comp(b, 'y')))}, ΔZ ${fmt(Math.abs(comp(a, 'z') - comp(b, 'z')))})${unitNote}`;
      canvas.dataset.measured = d.toFixed(6);
    }
    requestRender();
  }
  let down = null;
  canvas.addEventListener('pointerdown', (e) => { down = { x: e.clientX, y: e.clientY }; });
  canvas.addEventListener('pointerup', (e) => {
    if (!measure.active || !down || e.button !== 0) return;
    if (Math.hypot(e.clientX - down.x, e.clientY - down.y) > 4) return; // a drag, not a click
    const p = pick(e);
    if (p) addPoint(p);
  });

  // section
  const section = { active: false, axis: 'x', flip: false, plane: new T.Plane(new T.Vector3(-1, 0, 0), 0), t: 0.5 };
  function applySection() {
    const i = axisIndex()[section.axis];
    const min = [box.min.x, box.min.y, box.min.z][i];
    const max = [box.max.x, box.max.y, box.max.z][i];
    const value = min + (max - min) * section.t;
    section.plane.normal.set(0, 0, 0);
    section.plane.normal.setComponent(i, section.flip ? 1 : -1);
    section.plane.constant = section.flip ? -value : value;
    renderer.clippingPlanes = section.active ? [section.plane] : [];
    requestRender();
  }
  const sectionButton = addButton(toolbar, 'Section', () => {
    section.active = !section.active;
    setPressed(sectionButton, section.active);
    sectionControls.hidden = !section.active;
    applySection();
  }, { pressed: false, title: 'Cut the model with a plane along one axis' });
  const sectionControls = document.createElement('span');
  sectionControls.className = 'viewer-group';
  sectionControls.hidden = true;
  toolbar.append(sectionControls);
  addSelect(sectionControls, 'Axis', [['x', 'X'], ['y', 'Y'], ['z', 'Z']], (v) => { section.axis = v; applySection(); });
  addRange(sectionControls, 'Position', { min: 0, max: 100, value: 50 }, (v) => { section.t = v / 100; applySection(); });
  addButton(sectionControls, 'Flip', (b) => { section.flip = !section.flip; setPressed(b, section.flip); applySection(); }, { pressed: false });

  // exploded view: only when the file carries an assembly structure with at least two top-level parts
  let explodeInput = null;
  function prepareExplode() {
    if (groups.length < 2) return;
    for (const g of groups) {
      if (g.userData.basePosition) g.position.copy(g.userData.basePosition);
    }
    root.updateMatrixWorld(true);
    for (const g of groups) {
      g.userData.basePosition = g.position.clone();
      g.userData.center = new T.Box3().setFromObject(g).getCenter(new T.Vector3());
    }
    if (explodeInput) explodeInput.value = '0';
    requestRender();
  }
  if (groups.length >= 2) {
    prepareExplode();
    explodeInput = addRange(toolbar, `Explode (${groups.length} parts)`, { min: 0, max: 100, value: 0 }, (v) => {
      const f = v / 100;
      const toLocal = root.quaternion.clone().invert();
      for (const g of groups) {
        const off = g.userData.center.clone().sub(center).multiplyScalar(f * 1.5).applyQuaternion(toLocal);
        g.position.copy(g.userData.basePosition).add(off);
      }
      clearMeasure();
      requestRender();
    }).input;
  }
  addSeparator(toolbar);

  addButton(toolbar, 'Screenshot', () => downloadScreenshot(), { title: 'Save the current view as a PNG with the marking banner and distribution statement burned in' });
  toolbar.append(readout);
  toolbar.hidden = false;

  // ---- screenshot with burned-in markings -------------------------------------------------------------------------------------------------
  /** Renders the view into a PNG whose top and bottom bands carry the banner, the distribution statement and a traceability line (not overprinted). */
  function screenshotCanvas() {
    renderer.render(scene, camera);
    const w = canvas.width;
    const h = canvas.height;
    const { banner, distribution } = markings.info();
    const pad = Math.max(8, Math.round(w / 120));
    const bannerPx = Math.max(16, Math.round(w / 60));
    const noticePx = Math.max(12, Math.round(w / 90));
    const trace = `Keel PLM ${meta.documentNumber} ${meta.iteration} · ${meta.fileName} · SHA-256 ${meta.sha256.slice(0, 16)}… · captured ${new Date().toISOString().slice(0, 16).replace('T', ' ')} UTC`;
    const measureCanvas = document.createElement('canvas').getContext('2d');
    measureCanvas.font = `${noticePx}px sans-serif`;
    const lines = [...wrap(measureCanvas, distribution, w - 2 * pad), ...wrap(measureCanvas, trace, w - 2 * pad)];
    const topH = bannerPx + pad;
    const bottomH = bannerPx + pad + lines.length * Math.round(noticePx * 1.3) + pad;
    const out = document.createElement('canvas');
    out.width = w;
    out.height = h + topH + bottomH;
    const g = out.getContext('2d');
    const color = banner.startsWith('CUI') ? '#502b85' : '#007a33';
    g.fillStyle = color;
    g.fillRect(0, 0, w, topH);
    g.fillRect(0, topH + h, w, bottomH);
    g.drawImage(canvas, 0, topH);
    g.fillStyle = '#ffffff';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.font = `bold ${bannerPx}px sans-serif`;
    g.fillText(banner, w / 2, topH / 2);
    g.fillText(banner, w / 2, topH + h + bannerPx / 2 + pad / 2);
    g.font = `${noticePx}px sans-serif`;
    g.textAlign = 'left';
    g.textBaseline = 'top';
    let y = topH + h + bannerPx + pad + pad / 2;
    for (const line of lines) { g.fillText(line, pad, y); y += Math.round(noticePx * 1.3); }
    return out;
  }
  function downloadScreenshot() {
    screenshotCanvas().toBlob((blob) => {
      if (!blob) return;
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `${meta.fileName.replace(/\.[^.]+$/, '')}-view.png`;
      document.body.append(a);
      a.click();
      a.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 5000);
    }, 'image/png');
  }

  // ---- keyboard ---------------------------------------------------------------------------------------------------------------------------
  canvas.addEventListener('keydown', (e) => {
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    const k = e.key.toLowerCase();
    if (k === 'f') { fitView(); e.preventDefault(); }
    else if (k === 'w') { toolbar.querySelector('[aria-keyshortcuts="W"]')?.click(); e.preventDefault(); }
    else if (k === 'm') { setMeasure(!measure.active); e.preventDefault(); }
    else if (k === 'escape') { setMeasure(false); }
    else if ('1234567'.includes(k) && k.length === 1) { setView(['iso', 'front', 'back', 'left', 'right', 'top', 'bottom'][Number(k) - 1]); e.preventDefault(); }
  });

  // ---- lifecycle --------------------------------------------------------------------------------------------------------------------------
  function dispose() {
    if (disposed) return;
    disposed = true;
    cancelAnimationFrame(frame);
    resizeObserver.disconnect();
    controls.dispose();
    clearMeasure();
    disposeObject(root);
    renderer.dispose();
    renderer.forceContextLoss();
  }
  window.addEventListener('pagehide', dispose);

  resize();
  fitView();
  renderNow();
  document.body.dataset.viewerState = 'ready';
  stage.dataset.ready = 'true';
  stage.dataset.triangles = String(triangles);
  stage.dataset.bbox = `${comp(size, 'x').toFixed(4)},${comp(size, 'y').toFixed(4)},${comp(size, 'z').toFixed(4)}`;
  setStatus('');
  // A narrow hook for the automated tests: nothing here reaches the file's bytes, and it does not bypass the overlay.
  window.__keelViewer = { screenshotDataUrl: () => screenshotCanvas().toDataURL('image/png'), dispose, canvas, overlay };
}

// ======================================================================================================================================
// loading
// ======================================================================================================================================

async function loadModel(meta, bytes, { assetBase, maxTriangles, setStatus, showProgress }) {
  const buffer = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
  const material = () => new T.MeshStandardMaterial({ color: 0xb9c4d2, metalness: 0.15, roughness: 0.55, side: T.DoubleSide });
  let root;
  let groups = [];
  let up = 'z';
  let units = 'units';
  setStatus('Reading the model…');

  switch (meta.format) {
    case 'stl': {
      if (buffer.byteLength >= 84 && 84 + 50 * new DataView(buffer).getUint32(80, true) === buffer.byteLength) {
        const count = new DataView(buffer).getUint32(80, true);
        if (count > maxTriangles) throw budget(count, maxTriangles);
      }
      const geometry = new T.STLLoader().parse(buffer);
      root = new T.Group();
      root.add(new T.Mesh(geometry, material()));
      break;
    }
    case 'obj': {
      root = new T.OBJLoader().parse(new TextDecoder('utf-8').decode(buffer));
      root.traverse((o) => {
        if (!o.isMesh) return;
        if (!o.geometry.attributes.normal) o.geometry.computeVertexNormals();
        o.material = material();
      });
      groups = root.children.length >= 2 ? [...root.children] : [];
      up = 'y';
      break;
    }
    case 'glb':
    case 'gltf': {
      const loader = new T.GLTFLoader();
      let input = buffer;
      if (meta.format === 'gltf') {
        const text = new TextDecoder('utf-8').decode(buffer);
        let json;
        try { json = JSON.parse(text); } catch { throw new Error('The glTF file is not valid JSON.'); }
        const external = [...(json.buffers ?? []), ...(json.images ?? [])].map((x) => x.uri).find((u) => typeof u === 'string' && !u.startsWith('data:'));
        if (external) throw new Error(`This glTF file refers to a separate file (${external.slice(0, 80)}). Upload a .glb, or a .gltf with its data embedded.`);
        input = text;
      }
      let gltf;
      try { gltf = await loader.parseAsync(input, ''); } catch (e) { throw new Error(`The glTF file could not be read: ${e && e.message ? e.message : e}`); }
      root = gltf.scene;
      groups = singleChain(root).children.length >= 2 ? [...singleChain(root).children] : [];
      up = 'y';
      units = 'm';
      break;
    }
    case 'step':
    case 'iges': {
      setStatus('Converting the CAD geometry. Large assemblies can take a while…');
      showProgress(-1);
      const result = await runOcct(meta.format, buffer, assetBase);
      showProgress(null);
      ({ root, groups } = buildOcct(result, material, maxTriangles));
      units = CAD_UNITS;
      break;
    }
    default:
      throw new Error('This 3D format is not supported.');
  }

  let triangles = 0;
  root.traverse((o) => {
    if (!o.isMesh) return;
    const g = o.geometry;
    triangles += (g.index ? g.index.count : g.attributes.position.count) / 3;
  });
  if (triangles === 0) { disposeObject(root); throw new Error('The file contains no surface geometry to show.'); }
  if (triangles > maxTriangles) { disposeObject(root); throw budget(triangles, maxTriangles); }
  root.updateMatrixWorld(true);
  const empty = new T.Box3().setFromObject(root).isEmpty();
  if (empty) { disposeObject(root); throw new Error('The model has no measurable size.'); }
  return { root, groups, triangles, up, units };
}

function budget(count, limit) {
  return new Error(`This model has ${Math.round(count).toLocaleString('en-US')} triangles, more than the viewer's limit of ${limit.toLocaleString('en-US')}. Download it and open it in a CAD or mesh tool.`);
}

/** Follows single-child nodes down to the first node that has several children (the assembly level). */
function singleChain(node) {
  let n = node;
  while (n.children.length === 1 && !n.isMesh) n = n.children[0];
  return n;
}

function runOcct(format, buffer, assetBase) {
  return new Promise((resolve, reject) => {
    let worker;
    try { worker = new Worker(`${assetBase}step-worker.js`); } catch (e) { reject(new Error(`The CAD importer could not start: ${e.message}`)); return; }
    worker.onmessage = (ev) => {
      worker.terminate();
      if (ev.data.error) reject(new Error(ev.data.error)); else resolve(ev.data.result);
    };
    worker.onerror = (ev) => { worker.terminate(); reject(new Error(`The CAD importer failed to run${ev.message ? `: ${ev.message}` : ''}.`)); };
    worker.postMessage({ format, buffer }, [buffer]);
  });
}

function buildOcct(result, material, maxTriangles) {
  const meshes = result.meshes ?? [];
  let total = 0;
  for (const m of meshes) total += m.index ? m.index.array.length / 3 : m.attributes.position.array.length / 9;
  if (total > maxTriangles) throw budget(total, maxTriangles);

  const make = (m) => {
    const g = new T.BufferGeometry();
    g.setAttribute('position', new T.Float32BufferAttribute(m.attributes.position.array, 3));
    if (m.attributes.normal) g.setAttribute('normal', new T.Float32BufferAttribute(m.attributes.normal.array, 3));
    if (m.index) g.setIndex(m.index.array);
    if (!m.attributes.normal) g.computeVertexNormals();
    const mat = material();
    if (m.color) mat.color.setRGB(m.color[0], m.color[1], m.color[2], T.SRGBColorSpace);
    return new T.Mesh(g, mat);
  };
  const build = (node) => {
    const group = new T.Group();
    group.name = node.name ?? '';
    for (const i of node.meshes ?? []) group.add(make(meshes[i]));
    for (const c of node.children ?? []) group.add(build(c));
    return group;
  };
  const root = build(result.root);
  // A STEP assembly: the first level that has several children is the level that can be exploded.
  let level = root;
  while (level.children.length === 1 && !level.children[0].isMesh) level = level.children[0];
  const groups = level.children.length >= 2 ? [...level.children] : [];
  return { root, groups };
}

// ======================================================================================================================================
// helpers
// ======================================================================================================================================

function materialsOf(mesh) { return Array.isArray(mesh.material) ? mesh.material : [mesh.material]; }

function disposeObject(root) {
  root.traverse((o) => {
    if (o.geometry) o.geometry.dispose();
    if (!o.material) return;
    for (const mat of Array.isArray(o.material) ? o.material : [o.material]) {
      for (const value of Object.values(mat)) {
        if (value && value.isTexture) {
          value.source?.data?.close?.();
          value.dispose();
        }
      }
      mat.dispose();
    }
  });
}

function fmt(n) {
  const a = Math.abs(n);
  return a >= 100 ? n.toFixed(1) : a >= 1 ? n.toFixed(2) : n.toFixed(3);
}

function wrap(ctx, text, maxWidth) {
  const words = text.split(/\s+/);
  const lines = [];
  let line = '';
  for (const w of words) {
    const test = line ? `${line} ${w}` : w;
    if (line && ctx.measureText(test).width > maxWidth) { lines.push(line); line = w; } else line = test;
  }
  if (line) lines.push(line);
  return lines;
}
