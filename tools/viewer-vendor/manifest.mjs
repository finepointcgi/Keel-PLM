// Rewrites src/Keel.Web/wwwroot/viewer/manifest.json: the SHA-256 of every file the viewer serves (own modules and vendored libraries).
// ViewerAssetManifestTests recomputes these hashes and fails when a file changed without this being re-run; for files under vendor/ it also
// requires the hash to appear in docs/third-party/viewer.md. Prints the vendored rows as a Markdown table for that document.
import { createHash } from 'node:crypto';
import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..', 'src', 'Keel.Web', 'wwwroot', 'viewer');

const components = {
  'vendor/three-bundle.js': { component: 'three.js + OrbitControls, STLLoader, OBJLoader, GLTFLoader (bundled with esbuild)', license: 'MIT' },
  'vendor/occt-import-js.js': { component: 'occt-import-js (JavaScript glue)', license: 'LGPL-2.1' },
  'vendor/occt-import-js.wasm': { component: 'occt-import-js (OpenCascade Technology compiled to WebAssembly)', license: 'LGPL-2.1' },
  'vendor/pdf.min.mjs': { component: 'pdf.js (pdfjs-dist) main library', license: 'Apache-2.0' },
  'vendor/pdf.worker.min.mjs': { component: 'pdf.js (pdfjs-dist) worker', license: 'Apache-2.0' },
};

function* walk(dir) {
  for (const name of readdirSync(dir).sort()) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) yield* walk(full);
    else yield full;
  }
}

const files = [];
for (const full of walk(root)) {
  const path = relative(root, full).split('\\').join('/');
  if (path === 'manifest.json') continue;
  const bytes = readFileSync(full);
  files.push({ path, sha256: createHash('sha256').update(bytes).digest('hex'), size: bytes.length, ...(components[path] ? { vendored: true, ...components[path] } : { vendored: false }) });
}
writeFileSync(join(root, 'manifest.json'), `${JSON.stringify({ description: 'SHA-256 of every file served by the viewer (see docs/third-party/viewer.md). Regenerate with node tools/viewer-vendor/manifest.mjs.', files }, null, 2)}\n`);
console.log('| File | Size (bytes) | SHA-256 | License |\n|---|---|---|---|');
for (const f of files.filter((x) => x.vendored)) console.log(`| \`${f.path}\` | ${f.size} | \`${f.sha256}\` | ${f.license} |`);
