/* ── 3D-подиум ICDK на three.js: тело героя из AssetVault, шмотки по скелету ── */
import * as THREE from './vendor/three.module.js';
import { GLTFLoader } from './vendor/addons/GLTFLoader.js';
import { OrbitControls } from './vendor/addons/OrbitControls.js';
import { RoomEnvironment } from './vendor/addons/RoomEnvironment.js';
import { MeshoptDecoder } from './vendor/meshopt_decoder.js';

const RUBY = 0xdc2626;
const STEEL = 0x101216;

const state = {
  renderer: null,
  scene: null,
  camera: null,
  controls: null,
  root: null,
  heroSkeleton: null,
  bodySkinnedMesh: null,
  bodyScene: null,
  mixer: null,
  items: new Map(),
  bones: new Map(),
  hidden: new Set(),
  folder: null,
  defaults: new Map(),
  busy: 0,
};

const loader = new GLTFLoader();
try {
  loader.setResourcePath('/api/mesh/tex/');
  loader.setMeshoptDecoder(MeshoptDecoder);
} catch (err) {
  console.warn('3D: meshopt-декодер не подключён', err);
}

function note(text) {
  const host = document.getElementById('equipped-strip');
  if (host) host.innerHTML = '<span class="strip-empty">3D: ' + text + '</span>';
  console.warn('3D:', text);
}

function setBusy(on) {
  state.busy += on ? 1 : -1;
  if (state.busy < 0) state.busy = 0;
  document.body.classList.toggle('mesh-loading', state.busy > 0);
}

function initRenderer() {
  if (state.renderer) return true;
  const canvas = document.getElementById('podium-canvas');
  if (!canvas) return false;

  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, preserveDrawingBuffer: true });
  renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.05;
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  state.renderer = renderer;

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(STEEL);
  state.scene = scene;

  const camera = new THREE.PerspectiveCamera(38, 1, 0.05, 200);
  camera.position.set(0, 1.35, 3.4);
  state.camera = camera;

  const controls = new OrbitControls(camera, canvas);
  controls.enableDamping = true;
  controls.dampingFactor = 0.08;
  controls.rotateSpeed = 0.85;
  controls.minDistance = 1.2;
  controls.maxDistance = 9;
  controls.maxPolarAngle = Math.PI * 0.52;
  controls.target.set(0, 1.0, 0);
  state.controls = controls;

  // студийное PBR-окружение
  const pmrem = new THREE.PMREMGenerator(renderer);
  pmrem.compileEquirectangularShader();
  try {
    scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
  } catch (err) {
    console.warn('3D: окружение не собрано', err);
  }

  // холодный белый ключевой свет
  const key = new THREE.DirectionalLight(0xe8eef8, 2.6);
  key.position.set(2.4, 4.2, 3.0);
  key.castShadow = false;
  scene.add(key);

  // фирменный рубиновый контровой
  const rim = new THREE.SpotLight(RUBY, 34, 9, Math.PI / 5, 0.55, 1.6);
  rim.position.set(-2.3, 2.6, -2.6);
  rim.target.position.set(0, 1.1, 0);
  scene.add(rim, rim.target);

  scene.add(new THREE.HemisphereLight(0x9fb4cc, 0x0a0c10, 0.5));

  // круглая стальная платформа с рубиновым кольцом
  const platform = new THREE.Mesh(
    new THREE.CylinderGeometry(1.45, 1.45, 0.06, 96),
    new THREE.MeshStandardMaterial({ color: 0x181c22, metalness: 0.92, roughness: 0.34 }));
  platform.position.y = -0.03;
  scene.add(platform);

  const ring = new THREE.Mesh(
    new THREE.TorusGeometry(1.46, 0.018, 12, 128),
    new THREE.MeshBasicMaterial({ color: RUBY }));
  ring.rotation.x = Math.PI / 2;
  ring.position.y = 0.005;
  scene.add(ring);

  const halo = new THREE.Mesh(
    new THREE.TorusGeometry(1.6, 0.006, 8, 128),
    new THREE.MeshBasicMaterial({ color: 0xe2e8f0, transparent: true, opacity: 0.5 }));
  halo.rotation.x = Math.PI / 2;
  halo.position.y = -0.01;
  scene.add(halo);

  const clock = new THREE.Clock();
  const loop = () => {
    const dt = clock.getDelta();
    state.mixer?.update(dt);
    state.controls?.update();
    state.renderer.render(state.scene, state.camera);
    requestAnimationFrame(loop);
  };
  loop();

  const resize = () => {
    const host = canvas.parentElement;
    const width = host?.clientWidth || canvas.clientWidth || 320;
    const height = canvas.clientHeight || 320;
    renderer.setSize(width, height, false);
    camera.aspect = width / Math.max(1, height);
    camera.updateProjectionMatrix();
  };
  window.addEventListener('resize', resize);
  new ResizeObserver(resize).observe(canvas.parentElement || canvas);
  resize();
  return true;
}

async function fetchGlb(url) {
  try { await Promise.resolve(MeshoptDecoder?.ready); } catch (err) { console.warn('3D: meshopt.ready', err); }
  const response = await fetch(url);
  if (!response.ok) return null;
  const buffer = await response.arrayBuffer();
  if (buffer.byteLength < 32) return null;
  return new Promise((resolve) => loader.parse(buffer, '/api/mesh/tex/', (gltf) => resolve(gltf), () => resolve(null)));
}

async function fetchMeta(url) {
  try {
    const response = await fetch(url);
    if (!response.ok) return {};
    return await response.json();
  } catch (err) {
    return {};
  }
}


/** Скин шмотки: тот же порядок костей, но имена → живые кости тела. */
function remappedSkeleton(itemSkeleton) {
  if (!itemSkeleton || !state.heroSkeleton) return itemSkeleton;
  const live = new Map();
  for (const bone of state.heroSkeleton.bones) {
    const name = String(bone.name || '').toLowerCase();
    if (name && !live.has(name)) live.set(name, bone);
  }
  const bones = itemSkeleton.bones.map((bone, index) => {
    const name = String(bone?.name || '').toLowerCase();
    return live.get(name) || state.heroSkeleton.bones[index] || bone;
  });
  return new THREE.Skeleton(bones, itemSkeleton.boneInverses);
}


function collectBones(root) {
  const map = new Map();
  root?.traverse?.((node) => {
    if (node.isBone || node.isObject3D) {
      const name = String(node.name || '').toLowerCase();
      if (name && !map.has(name)) map.set(name, node);
    }
  });
  return map;
}

function frameCamera(box) {
  const size = new THREE.Vector3();
  const center = new THREE.Vector3();
  box.getSize(size);
  box.getCenter(center);
  const height = Math.max(0.35, size.y);
  state.controls.target.set(center.x, box.min.y + height * 0.55, center.z);
  const distance = Math.max(1.6, height * 2.1);
  state.camera.position.set(center.x, box.min.y + height * 0.62, center.z + distance);
  state.controls.update();
}

async function loadHero(folder) {
  if (!initRenderer()) return;
  state.folder = folder;
  clearItems();

  setBusy(true);
  try {
    const gltf = await fetchGlb('/api/mesh/hero?folder=' + encodeURIComponent(folder));
    if (!gltf) { note('модель героя не найдена в AssetVault'); window.__podiumReady = true; return; }

    if (state.root) {
      state.scene.remove(state.root);
      state.root = null;
    }

    const root = gltf.scene;
    state.root = root;
    state.scene.add(root);

    // первый SkinnedMesh тела — источник скелета и bind-матрицы
    let skeleton = null;
    let skinned = null;
    root.traverse((node) => {
      if (!skeleton && node.isSkinnedMesh) {
        skeleton = node.skeleton;
        skinned = node;
      }
    });
    state.heroSkeleton = skeleton;
    state.bodySkinnedMesh = skinned;
    state.bones = collectBones(root);

    // пять шагов прогрева, чтобы кости встали в живую позу до первого кадра
    if (gltf.animations?.length) {
      state.mixer = new THREE.AnimationMixer(root);
      const idle = gltf.animations.find((clip) => /idle/i.test(clip.name)) || gltf.animations[0];
      state.mixer.clipAction(idle).play();
      for (let i = 0; i < 5; i++) state.mixer.update(1 / 60);
    } else {
      state.mixer = null;
    }

    // Source 2 отдаёт рост героя в 150–220 единиц: нормализуем группу к 2.0 и ставим стопы на диск
    const raw = new THREE.Box3().setFromObject(root);
    if (!raw.isEmpty()) {
      const height = Math.max(0.001, raw.max.y - raw.min.y);
      const scale = 2.0 / height;
      root.scale.setScalar(scale);
      root.updateMatrixWorld(true);
      const fitted = new THREE.Box3().setFromObject(root);
      const center = new THREE.Vector3();
      fitted.getCenter(center);
      root.position.x -= center.x;
      root.position.z -= center.z;
      root.position.y -= fitted.min.y;
      root.updateMatrixWorld(true);
      const placed = new THREE.Box3().setFromObject(root);
      if (!placed.isEmpty()) frameCamera(placed);
    }

    await loadDefaults(folder);
    state.renderer.render(state.scene, state.camera);
    window.__podiumReady = true;
  } catch (err) {
    console.warn('3D: тело героя не загрузилось', err);
    note('ошибка тела: ' + (err?.message || err));
    window.__podiumReady = true;
  } finally {
    setBusy(false);
  }
}

async function loadDefaults(folder) {
  state.defaults.clear();
  try {
    const response = await fetch('/api/hero?folder=' + encodeURIComponent(folder));
    if (!response.ok) return;
    const view = await response.json();
    for (const section of view.sections || []) {
      for (const card of section.cards || []) {
        const slot = String(card.slot || '').toLowerCase();
        const prefab = String(card.prefab || '').toLowerCase();
        if (!slot || slot === 'persona_selector') continue;
        if (prefab !== 'default_item' && prefab !== 'wearable') continue;
        if (!state.defaults.has(slot)) state.defaults.set(slot, card.itemId ?? card.id);
      }
    }
  } catch (err) {
    console.warn('3D: стоковые вещи не прочитаны', err);
  }
}

/** Кость под слот: head/weapon/arms/shoulder/legs/back. */
function boneFor(slot) {
  const order = {
    head: ['head', 'bip_head'],
    weapon: ['weapon_r', 'hand_r', 'weapon'],
    offhand_weapon: ['weapon_l', 'hand_l'],
    arms: ['arm_r', 'upperarm_r', 'shoulder_r', 'arms'],
    shoulder: ['shoulder_r', 'arm_r'],
    back: ['spine_2', 'spine'],
    belt: ['pelvis', 'spine'],
    legs: ['leg_r', 'thigh_r'],
  }[String(slot || '').toLowerCase()] || [];

  for (const name of order) {
    const bone = state.bones.get(name);
    if (bone) return bone;
  }

  return state.bodySkinnedMesh || state.root;
}

async function dress(slot, itemId, style) {
  if (!initRenderer() || !itemId) return;
  const key = String(slot || '').toLowerCase();
  const existing = state.items.get(key);
  if (existing && existing.itemId === itemId) return;

  setBusy(true);
  try {
    const gltf = await fetchGlb('/api/mesh/item?itemId=' + encodeURIComponent(itemId) + '&style=' + (style || 1));
    if (!gltf) { note('предмет ' + itemId + ' не найден в AssetVault'); return; }
    const meta = await fetchMeta('/api/mesh/meta?itemId=' + encodeURIComponent(itemId));

    const host = state.bodySkinnedMesh?.parent || state.root;
    const skinnedCopies = [];

    gltf.scene.updateMatrixWorld(true);
    gltf.scene.traverse((mesh) => {
      if (!mesh.isMesh) return;
      if (mesh.isSkinnedMesh && state.heroSkeleton) {
        // рабочий секрет: кости шмотки живут на скелете тела и его bind-матрице
        mesh.frustumCulled = false;
        // скин шмотки может содержать меньше костей и в другом порядке — сопоставляем по имени
        mesh.skeleton = remappedSkeleton(mesh.skeleton);
        mesh.bind(mesh.skeleton, state.bodySkinnedMesh.bindMatrix.clone());
        host.add(mesh);
        skinnedCopies.push(mesh);
        return;
      }

      const geometry = mesh.geometry.clone();
      geometry.applyMatrix4(mesh.matrixWorld);
      const merged = new THREE.Mesh(geometry, Array.isArray(mesh.material) ? mesh.material[0] : mesh.material);
      merged.frustumCulled = false;
      boneFor(key).add(merged);
      skinnedCopies.push(merged);
    });

    hideBodyParts(meta);

    if (existing) disposeEntry(existing);
    state.items.set(key, { itemId, nodes: skinnedCopies, style: style || 1 });
  } catch (err) {
    console.warn('3D: предмет не загрузился', slot, err);
  } finally {
    setBusy(false);
  }
}

/** Скрываем перекрытые части тела по mesh_groups / mesh_labels от бэкенда. */
function hideBodyParts(meta) {
  const labels = new Set((meta?.mesh_labels || []).map((v) => String(v).toLowerCase()));
  const groups = new Set((meta?.mesh_groups || []).map((v) => String(v).toLowerCase()));
  if (!labels.size && !groups.size) return;

  state.root?.traverse((node) => {
    if (!node.isMesh) return;
    const name = String(node.name || '').toLowerCase();
    if (labels.has(name) || groups.has(name)) {
      node.visible = false;
      state.hidden.add(node);
    }
  });
}

function disposeEntry(entry) {
  for (const node of entry.nodes) {
    node.parent?.remove(node);
    node.geometry?.dispose?.();
  }
}

function undress(slot) {
  const key = String(slot || '').toLowerCase();
  const entry = state.items.get(key);
  if (!entry) return;
  disposeEntry(entry);
  state.items.delete(key);
}

function clearItems() {
  for (const [, entry] of state.items) disposeEntry(entry);
  state.items.clear();
  for (const node of state.hidden) node.visible = true;
  state.hidden.clear();
}

async function sync(folder, picks, styles) {
  const steps = [];
  try {
    steps.push('init=' + initRenderer());
    if (!state.renderer) { note('нет WebGL-канваса'); return steps.join(','); }
  if (!state.folder || state.folder !== folder) await loadHero(folder);

  const chosen = new Map();
  for (const [slot, value] of Object.entries(picks || {})) {
    const id = Number(value);
    if (id) chosen.set(String(slot).toLowerCase(), { id, style: Number((styles || {})[slot] || 1) });
  }

  for (const slot of [...state.items.keys()]) {
    if (!chosen.has(slot)) undress(slot);
  }

  for (const [slot, pick] of chosen) await dress(slot, pick.id, pick.style);
  for (const [slot, id] of state.defaults) {
    if (!chosen.has(slot) && !state.items.has(slot) && id) await dress(slot, id, 1);
  }
  steps.push('bones=' + (state.heroSkeleton?.bones?.length || 0), 'items=' + state.items.size);
  return steps.join(',');
  } catch (err) {
    note('sync: ' + (err?.message || err));
    return 'error:' + (err?.message || err);
  }
}

function resetCamera() {
  if (!state.controls) return;
  state.camera.position.set(0, 1.35, 3.4);
  state.controls.target.set(0, 1.0, 0);
  state.controls.update();
}

/* ── переключатель 3D / 2D и связка с экраном героя ────────────────────── */
function applyMode(mode) {
  document.body.classList.toggle('podium-3d', mode === '3d');
  for (const button of document.querySelectorAll('[data-preview-mode]')) {
    button.classList.toggle('on', button.dataset.previewMode === mode);
  }
  if (mode === '3d') {
    const match = location.hash.match(/^#\/hero\/([\w_]+)/i);
    if (match) loadHero(decodeURIComponent(match[1]));
  }
}

document.addEventListener('click', (event) => {
  const button = event.target.closest?.('[data-preview-mode]');
  if (button) {
    const mode = button.dataset.previewMode;
    localStorage.setItem('icdk-preview-mode', mode);
    applyMode(mode);
    return;
  }

  if (event.target.closest?.('#podium-reset')) resetCamera();
});

window.addEventListener('hashchange', () => {
  if (!document.body.classList.contains('podium-3d')) return;
  const match = location.hash.match(/^#\/hero\/([\w_]+)/i);
  if (match) loadHero(decodeURIComponent(match[1]));
});

window.Podium = { loadHero, dress, undress, sync, resetCamera, get folder() { return state.folder; } };
applyMode(localStorage.getItem('icdk-preview-mode') || '2d');
