// Entry point of the three.js bundle (tools/viewer-vendor/build.mjs). Only what the 3D viewer uses is exported, so esbuild can drop the rest.
export {
  Box3, BufferGeometry, Color, DirectionalLight, DoubleSide, Float32BufferAttribute, Group, HemisphereLight, Line, LineBasicMaterial, Mesh,
  MeshBasicMaterial, MeshStandardMaterial, PerspectiveCamera, Plane, Raycaster, SRGBColorSpace, Scene, SphereGeometry, Vector2, Vector3,
  WebGLRenderer,
} from 'three';
export { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
export { STLLoader } from 'three/examples/jsm/loaders/STLLoader.js';
export { OBJLoader } from 'three/examples/jsm/loaders/OBJLoader.js';
export { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
