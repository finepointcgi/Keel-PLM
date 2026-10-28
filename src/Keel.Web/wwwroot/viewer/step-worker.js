// Web Worker (classic script) that converts STEP and IGES files to triangle meshes with the OpenCascade importer (occt-import-js, LGPL-2.1,
// WebAssembly). It loads the importer and its .wasm from this directory's vendor/ folder only; it has no access to the page's DOM or session.
// The worker script is served under the viewer's Content-Security-Policy, which allows 'wasm-unsafe-eval' (and nothing else beyond 'self').
let occtPromise = null;

function loadImporter() {
  if (!occtPromise) {
    importScripts('vendor/occt-import-js.js');
    // eslint-disable-next-line no-undef
    occtPromise = occtimportjs({ locateFile: (name) => `vendor/${name}` });
  }
  return occtPromise;
}

self.onmessage = async (ev) => {
  const { format, buffer } = ev.data;
  try {
    const occt = await loadImporter();
    const bytes = new Uint8Array(buffer);
    const result = format === 'iges' ? occt.ReadIgesFile(bytes, null) : occt.ReadStepFile(bytes, null);
    if (!result || !result.success) {
      self.postMessage({ error: `The ${format === 'iges' ? 'IGES' : 'STEP'} file could not be converted. It may be damaged or use features the importer does not support.` });
      return;
    }
    self.postMessage({ result });
  } catch (e) {
    self.postMessage({ error: `The CAD importer failed: ${e && e.message ? e.message : e}` });
  }
};
