/* Первый экран ICDK: объёмное ядро из three.js.
   Сцена лёгкая: один металлический кристалл, каркас, два кольца, осколки и пыль.
   Всё останавливается, когда герой уходит из виду или вкладка спрятана. */

import * as THREE from './vendor/three/three.module.min.js';

const canvas = document.getElementById('bg3d');
const hero = document.querySelector('.hero');
const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

if (canvas && hero) {
  try {
    boot();
  } catch (error) {
    /* без WebGL сайт остаётся на градиентных пятнах */
    canvas.remove();
    document.body.classList.remove('ready3d');
  }
}

function glowSprite() {
  const size = 128;
  const c = document.createElement('canvas');
  c.width = c.height = size;
  const ctx = c.getContext('2d');
  const g = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
  g.addColorStop(0, 'rgba(255,255,255,1)');
  g.addColorStop(0.25, 'rgba(255,255,255,0.75)');
  g.addColorStop(0.55, 'rgba(255,90,110,0.28)');
  g.addColorStop(1, 'rgba(255,90,110,0)');
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, size, size);
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  return tex;
}

function boot() {
  const dpr = Math.min(window.devicePixelRatio || 1, 1.75);
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: dpr < 1.5, alpha: true, powerPreference: 'high-performance' });
  renderer.setPixelRatio(dpr);
  renderer.setClearColor(0x000000, 0);

  const scene = new THREE.Scene();

  /* окружение для металла: градиентная панорама, иначе металл рендерится чёрным */
  const envCanvas = document.createElement('canvas');
  envCanvas.width = 512;
  envCanvas.height = 256;
  const ectx = envCanvas.getContext('2d');
  const sky = ectx.createLinearGradient(0, 0, 0, 256);
  sky.addColorStop(0, '#8e9aae');
  sky.addColorStop(0.4, '#3b4553');
  sky.addColorStop(0.72, '#5a1620');
  sky.addColorStop(1, '#0d0f14');
  ectx.fillStyle = sky;
  ectx.fillRect(0, 0, 512, 256);
  const hotSpot = ectx.createRadialGradient(140, 96, 6, 140, 96, 150);
  hotSpot.addColorStop(0, 'rgba(255,170,180,1)');
  hotSpot.addColorStop(0.4, 'rgba(230,30,55,0.45)');
  hotSpot.addColorStop(1, 'rgba(230,30,55,0)');
  ectx.fillStyle = hotSpot;
  ectx.fillRect(0, 0, 512, 256);
  const coolSpot = ectx.createRadialGradient(400, 190, 6, 400, 190, 130);
  coolSpot.addColorStop(0, 'rgba(200,200,255,0.85)');
  coolSpot.addColorStop(1, 'rgba(120,120,255,0)');
  ectx.fillStyle = coolSpot;
  ectx.fillRect(0, 0, 512, 256);

  const envTexture = new THREE.CanvasTexture(envCanvas);
  envTexture.mapping = THREE.EquirectangularReflectionMapping;
  envTexture.colorSpace = THREE.SRGBColorSpace;
  const pmrem = new THREE.PMREMGenerator(renderer);
  scene.environment = pmrem.fromEquirectangular(envTexture).texture;
  scene.environmentIntensity = 1.6;
  const camera = new THREE.PerspectiveCamera(42, 1, 0.1, 120);
  camera.position.set(0, 0, 7.6);

  const world = new THREE.Group();
  scene.add(world);

  /* ── ядро ─────────────────────────────────────────────────────────────── */
  const core = new THREE.Group();
  world.add(core);

  const gem = new THREE.Mesh(
    new THREE.IcosahedronGeometry(1.18, 1),
    new THREE.MeshStandardMaterial({
      color: 0xf3f6fb, metalness: 1, roughness: 0.11, flatShading: true,
      emissive: 0x4a0d15, emissiveIntensity: 0.8,
    })
  );
  core.add(gem);

  /* мягкое свечение вокруг ядра */
  const sun = new THREE.Sprite(new THREE.SpriteMaterial({
    map: glowSprite(), color: 0xff4a5c, transparent: true, opacity: 0.5,
    blending: THREE.AdditiveBlending, depthWrite: false,
  }));
  sun.scale.setScalar(7.5);
  core.add(sun);

  const shell = new THREE.Mesh(
    new THREE.IcosahedronGeometry(1.72, 1),
    new THREE.MeshBasicMaterial({ color: 0xff6b7a, wireframe: true, transparent: true, opacity: 1 })
  );
  core.add(shell);

  const heart = new THREE.Mesh(
    new THREE.IcosahedronGeometry(0.45, 1),
    new THREE.MeshBasicMaterial({ color: 0xff5566, transparent: true, opacity: 0.85, blending: THREE.AdditiveBlending })
  );
  core.add(heart);

  const halo = new THREE.Mesh(
    new THREE.SphereGeometry(0.62, 24, 24),
    new THREE.MeshBasicMaterial({ color: 0xff2640, transparent: true, opacity: 0.28, blending: THREE.AdditiveBlending })
  );
  core.add(halo);

  /* большой каркасный глобус — глубина за всем первым экраном */
  const globe = new THREE.Mesh(
    new THREE.IcosahedronGeometry(5.4, 2),
    new THREE.MeshBasicMaterial({ color: 0xd8dee9, wireframe: true, transparent: true, opacity: 0.105 })
  );
  const globeInner = new THREE.Mesh(
    new THREE.IcosahedronGeometry(3.6, 1),
    new THREE.MeshBasicMaterial({ color: 0xff3b4e, wireframe: true, transparent: true, opacity: 0.13 })
  );
  world.add(globe, globeInner);

  const ringGeo = new THREE.TorusGeometry(2.05, 0.006, 8, 220);
  const ringMat = new THREE.MeshBasicMaterial({ color: 0xff6675, transparent: true, opacity: 0.95, blending: THREE.AdditiveBlending });
  const ringA = new THREE.Mesh(ringGeo, ringMat);
  ringA.rotation.set(Math.PI / 2.4, 0.3, 0);
  const ringB = new THREE.Mesh(new THREE.TorusGeometry(2.5, 0.004, 8, 220), ringMat.clone());
  ringB.material.opacity = 0.7;
  ringB.rotation.set(Math.PI / 1.7, -0.6, 0.3);
  core.add(ringA, ringB);

  /* ── осколки вокруг ───────────────────────────────────────────────────── */
  const shards = [];
  const shardMat = new THREE.MeshStandardMaterial({ color: 0xe7ecf3, metalness: 1, roughness: 0.25, flatShading: true });
  const shardMatRed = new THREE.MeshBasicMaterial({ color: 0xff4a5c, transparent: true, opacity: 0.85 });
  for (let i = 0; i < 26; i += 1) {
    const red = i % 4 === 0;
    const size = 0.035 + Math.random() * 0.085;
    const geo = red
      ? new THREE.TetrahedronGeometry(size * 1.5, 0)
      : new THREE.OctahedronGeometry(size, 0);
    const mesh = new THREE.Mesh(geo, red ? shardMatRed : shardMat);
    const radius = 2.5 + Math.random() * 1.9;
    const angle = Math.random() * Math.PI * 2;
    const height = (Math.random() - 0.5) * 3.4;
    mesh.position.set(Math.cos(angle) * radius, height, Math.sin(angle) * radius * 0.75);
    mesh.rotation.set(Math.random() * 3, Math.random() * 3, Math.random() * 3);
    mesh.userData = {
      radius, angle, height,
      speed: 0.06 + Math.random() * 0.16,
      spin: 0.25 + Math.random() * 0.7,
      bob: Math.random() * Math.PI * 2,
    };
    shards.push(mesh);
    world.add(mesh);
  }

  /* ── пыль ─────────────────────────────────────────────────────────────── */
  const dustCount = 1400;
  const positions = new Float32Array(dustCount * 3);
  const colors = new Float32Array(dustCount * 3);
  const white = new THREE.Color(0xffffff);
  const red = new THREE.Color(0xff4a5c);
  for (let i = 0; i < dustCount; i += 1) {
    const r = 2.2 + Math.pow(Math.random(), 0.55) * 5.6;
    const theta = Math.random() * Math.PI * 2;
    const phi = Math.acos(2 * Math.random() - 1);
    positions[i * 3] = r * Math.sin(phi) * Math.cos(theta);
    positions[i * 3 + 1] = r * Math.cos(phi) * 0.72;
    positions[i * 3 + 2] = r * Math.sin(phi) * Math.sin(theta) * 0.8;
    const tint = Math.random() < 0.3 ? red : white;
    colors[i * 3] = tint.r;
    colors[i * 3 + 1] = tint.g;
    colors[i * 3 + 2] = tint.b;
  }
  const dustGeo = new THREE.BufferGeometry();
  dustGeo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
  dustGeo.setAttribute('color', new THREE.BufferAttribute(colors, 3));
  const dust = new THREE.Points(
    dustGeo,
    new THREE.PointsMaterial({
      size: 0.13, map: glowSprite(), vertexColors: true, transparent: true,
      opacity: 1, depthWrite: false, blending: THREE.AdditiveBlending, sizeAttenuation: true,
    })
  );
  world.add(dust);

  /* ── свет ─────────────────────────────────────────────────────────────── */
  scene.add(new THREE.AmbientLight(0x8892a6, 0.5));
  const key = new THREE.DirectionalLight(0xffffff, 3.2);
  key.position.set(4, 5, 6);
  scene.add(key);
  const hot = new THREE.PointLight(0xff2a3c, 90, 22, 1.6);
  hot.position.set(3.2, 1.6, 2.6);
  scene.add(hot);
  const cool = new THREE.PointLight(0x7c5cff, 45, 26, 1.8);
  cool.position.set(-4, -2.4, -1.5);
  scene.add(cool);

  /* ── камера и композиция ──────────────────────────────────────────────── */
  const pointer = { x: 0, y: 0 };
  const smooth = { x: 0, y: 0 };
  let scrollShift = 0;
  let width = 0;
  let height = 0;

  function layout() {
    const box = hero.getBoundingClientRect();
    width = box.width;
    height = box.height;
    renderer.setSize(width, height, false);
    camera.aspect = width / Math.max(height, 1);
    camera.updateProjectionMatrix();

    const wide = width > 1080;
    world.position.set(wide ? 0.72 : 0, wide ? 0.05 : 1.55, wide ? 0.4 : -0.9);
    const scale = wide ? 1.22 : Math.min(0.95, width / 560);
    world.scale.setScalar(scale);
    core.scale.setScalar(wide ? 1 : 0.8);
    dust.scale.setScalar(wide ? 1.1 : 0.9);
    camera.position.z = wide ? 8.2 : 8.6;
  }

  layout();

  const resizeObserver = new ResizeObserver(layout);
  resizeObserver.observe(hero);
  window.addEventListener('resize', layout);

  if (window.matchMedia('(hover: hover)').matches) {
    window.addEventListener('pointermove', (event) => {
      pointer.x = (event.clientX / window.innerWidth) * 2 - 1;
      pointer.y = (event.clientY / window.innerHeight) * 2 - 1;
    }, { passive: true });
  }

  window.addEventListener('scroll', () => {
    const box = hero.getBoundingClientRect();
    scrollShift = Math.min(Math.max(-box.top / Math.max(box.height, 1), 0), 1.2);
  }, { passive: true });

  /* ── кадры ────────────────────────────────────────────────────────────── */
  let frame = 0;
  let visible = true;
  let last = performance.now();
  let elapsed = 0;

  function draw(now) {
    const dt = Math.min((now - last) / 1000, 0.05);
    last = now;
    elapsed += dt;

    smooth.x += (pointer.x - smooth.x) * 0.045;
    smooth.y += (pointer.y - smooth.y) * 0.045;

    camera.position.x = smooth.x * 0.55;
    camera.position.y = -smooth.y * 0.4;
    camera.position.z = (width > 1080 ? 8.2 : 8.6) + scrollShift * 1.6;
    camera.lookAt(world.position.x * 0.3, world.position.y * 0.3, 0);

    core.rotation.y += dt * 0.22;
    core.rotation.x = Math.sin(elapsed * 0.25) * 0.16;
    gem.rotation.y -= dt * 0.1;
    shell.rotation.y -= dt * 0.34;
    shell.rotation.z += dt * 0.12;
    halo.scale.setScalar(1 + Math.sin(elapsed * 1.6) * 0.06);
    heart.rotation.y -= dt * 0.9;
    heart.scale.setScalar(1 + Math.sin(elapsed * 2.4) * 0.08);
    ringA.rotation.z += dt * 0.3;
    ringB.rotation.z -= dt * 0.22;

    for (const shard of shards) {
      const d = shard.userData;
      d.angle += dt * d.speed;
      shard.position.x = Math.cos(d.angle) * d.radius;
      shard.position.z = Math.sin(d.angle) * d.radius * 0.75;
      shard.position.y = d.height + Math.sin(elapsed * 0.6 + d.bob) * 0.16;
      shard.rotation.x += dt * d.spin;
      shard.rotation.y += dt * d.spin * 0.7;
    }

    globe.rotation.y += dt * 0.035;
    globe.rotation.x = Math.sin(elapsed * 0.08) * 0.12;
    globeInner.rotation.y -= dt * 0.06;

    dust.rotation.y += dt * 0.02;
    dust.rotation.x = Math.sin(elapsed * 0.1) * 0.04;

    renderer.render(scene, camera);
    if (!reduce && visible) frame = requestAnimationFrame(draw);
  }

  function start() {
    if (frame) return;
    visible = true;
    last = performance.now();
    if (reduce) {
      renderer.render(scene, camera);
      document.body.classList.add('ready3d');
      return;
    }
    frame = requestAnimationFrame(draw);
  }

  function stop() {
    visible = false;
    if (frame) cancelAnimationFrame(frame);
    frame = 0;
  }

  if ('IntersectionObserver' in window) {
    new IntersectionObserver((items) => {
      for (const item of items) {
        if (item.isIntersecting) start();
        else stop();
      }
    }, { threshold: 0.02 }).observe(hero);
  }

  document.addEventListener('visibilitychange', () => {
    if (document.hidden) stop();
    else start();
  });

  /* первый кадр рисуем сразу, чтобы не было вспышки пустого фона */
  const still = new URLSearchParams(location.search).has('noanim');
  if (still) {
    renderer.render(scene, camera);
    document.body.classList.add('ready3d', 'nomotion');
    return;
  }

  renderer.render(scene, camera);
  document.body.classList.add('ready3d');
  start();
}
