// Produces the vendored viewer assets under src/Keel.Web/wwwroot/viewer/vendor and docs/third-party/viewer-licenses.
// Maintainer tool only: it needs npm and network access ONCE, on a build machine. The product never runs it and never needs the network.
//
//   npm ci --prefix tools/viewer-vendor && node tools/viewer-vendor/build.mjs
//
// After running it: update the hashes in src/Keel.Web/wwwroot/viewer/manifest.json (node tools/viewer-vendor/manifest.mjs) and the notices in
// docs/third-party/viewer.md. The unit test ViewerAssetManifestTests fails until both are done.
import { build } from 'esbuild';
import { copyFileSync, mkdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..', '..');
const out = join(repo, 'src', 'Keel.Web', 'wwwroot', 'viewer', 'vendor');
const licenses = join(repo, 'docs', 'third-party', 'viewer-licenses');
const nm = join(here, 'node_modules');
mkdirSync(out, { recursive: true });
mkdirSync(licenses, { recursive: true });

await build({
  entryPoints: [join(here, 'entry-three.js')],
  bundle: true,
  format: 'esm',
  target: 'es2022',
  minify: true,
  legalComments: 'eof',
  charset: 'utf8',
  outfile: join(out, 'three-bundle.js'),
  absWorkingDir: here,
});

const copy = (from, to) => copyFileSync(join(nm, from), join(out, to));
copy('occt-import-js/dist/occt-import-js.js', 'occt-import-js.js');
copy('occt-import-js/dist/occt-import-js.wasm', 'occt-import-js.wasm');
copy('pdfjs-dist/build/pdf.min.mjs', 'pdf.min.mjs');
copy('pdfjs-dist/build/pdf.worker.min.mjs', 'pdf.worker.min.mjs');

const lic = (from, to) => copyFileSync(join(nm, from), join(licenses, to));
lic('three/LICENSE', 'three-MIT.txt');
lic('pdfjs-dist/LICENSE', 'pdfjs-Apache-2.0.txt');
lic('occt-import-js/dist/license.occt-import-js.txt', 'occt-import-js-LGPL-2.1.txt');
lic('occt-import-js/dist/license.occt.txt', 'opencascade-LGPL-2.1.txt');

const versions = Object.fromEntries(['three', 'occt-import-js', 'pdfjs-dist'].map((n) => [n, JSON.parse(readFileSync(join(nm, n, 'package.json'), 'utf8')).version]));
console.log('Vendored:', versions);
