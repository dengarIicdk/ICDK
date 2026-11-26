/* ── ссылки: меняются в одном месте ───────────────────────────────────────── */
const DOWNLOAD_URL = 'https://github.com/dengarIicdk/ICDK/releases/download/v1.0.0/ICDK_Setup_v1.0.0.exe';
const RELEASES_URL = 'https://github.com/dengarIicdk/ICDK/releases';
const TELEGRAM_URL = 'https://t.me/icdk_dota';
const DISCORD_URL = 'https://discord.gg/icdk';
const VERSION = '1.0.0';
const SHA256 = 'f9f72efaf390902ebbf03d0df5cf274684488b722c027de155f5da3b82179f24';

/* ── язык ────────────────────────────────────────────────────────────────── */
const STORE = 'icdk-lang';
const translated = Array.from(document.querySelectorAll('.t'));

function applyLang(lang) {
  const pick = lang === 'en' ? 'en' : 'ru';
  document.documentElement.lang = pick;
  for (const node of translated) {
    const value = node.dataset[pick];
    if (value) node.innerHTML = value;
  }
  document.querySelectorAll('.lang button').forEach((button) => {
    button.classList.toggle('on', button.dataset.lang === pick);
  });
  paintHash();
  try {
    localStorage.setItem(STORE, pick);
  } catch (_) {
    /* приватный режим — переключатель работает и без сохранения */
  }
}

document.querySelectorAll('.lang button').forEach((button) => {
  button.addEventListener('click', () => applyLang(button.dataset.lang));
});

let saved = null;
try {
  saved = localStorage.getItem(STORE);
} catch (_) {
  saved = null;
}
const fromUrl = new URLSearchParams(location.search).get('lang');
if (fromUrl === 'en' || fromUrl === 'ru') saved = fromUrl;
applyLang(saved || 'ru');

/* ?noanim — статичный кадр без анимаций: удобно для скриншотов и печати */
if (new URLSearchParams(location.search).has('noanim')) document.body.classList.add('nomotion');

/* ── ссылки ──────────────────────────────────────────────────────────────── */
document.querySelectorAll('[data-download]').forEach((a) => {
  a.href = DOWNLOAD_URL;
  a.setAttribute('rel', 'noopener');
});

const wire = (selector, url) => {
  const node = document.querySelector(selector);
  if (node) node.href = url;
};
wire('[data-releases]', RELEASES_URL);
wire('[data-telegram]', TELEGRAM_URL);
wire('[data-discord]', DISCORD_URL);

function paintHash() {
  const line = document.getElementById('hashline');
  if (!line || !SHA256) return;
  const short = SHA256.slice(0, 8) + '…' + SHA256.slice(-6);
  line.textContent = 'SHA256 · ' + (document.documentElement.lang === 'en' ? 'installer' : 'установщик') + ': ' + short;
  line.title = 'ICDK_Setup_v' + VERSION + '.exe — ' + SHA256;
}
paintHash();

/* ── шапка становится плотной при скролле ────────────────────────────────── */
const bar = document.getElementById('topbar');
const onScroll = () => {
  if (bar) bar.classList.toggle('solid', window.scrollY > 24);
};
onScroll();
window.addEventListener('scroll', onScroll, { passive: true });

/* ── курсорное пятно ─────────────────────────────────────────────────────── */
const root = document.documentElement;
if (window.matchMedia('(hover: hover)').matches) {
  window.addEventListener('pointermove', (event) => {
    root.style.setProperty('--px', event.clientX + 'px');
    root.style.setProperty('--py', event.clientY + 'px');
  }, { passive: true });
}

/* ── появление блоков при скролле ────────────────────────────────────────── */
const revealNodes = document.querySelectorAll('.reveal');
if ('IntersectionObserver' in window) {
  const watcher = new IntersectionObserver(
    (items) => {
      for (const item of items) {
        if (!item.isIntersecting) continue;
        const delay = Number(item.target.dataset.delay || 0);
        window.setTimeout(() => item.target.classList.add('in'), delay);
        watcher.unobserve(item.target);
      }
    },
    { rootMargin: '0px 0px -10% 0px', threshold: 0.06 }
  );
  revealNodes.forEach((node, index) => {
    if (!node.dataset.delay && index < 6) node.dataset.delay = String(index * 90);
    watcher.observe(node);
  });
} else {
  revealNodes.forEach((node) => node.classList.add('in'));
}

/* ── счётчики ───────────────────────────────────────────────────────────── */
function runCounter(node) {
  const target = Number(node.dataset.count || 0);
  const suffix = node.dataset.suffix || '';
  if (!target) {
    node.textContent = (node.dataset.text || '0') + suffix;
    return;
  }
  if (document.body.classList.contains('nomotion')) {
    node.textContent = target + suffix;
    return;
  }
  const start = performance.now();
  const duration = 1200;
  const tick = (now) => {
    const p = Math.min((now - start) / duration, 1);
    const eased = 1 - Math.pow(1 - p, 3);
    node.textContent = Math.round(target * eased) + suffix;
    if (p < 1) requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
}

const counters = document.querySelectorAll('[data-count]');
if ('IntersectionObserver' in window) {
  const counterWatcher = new IntersectionObserver((items) => {
    for (const item of items) {
      if (!item.isIntersecting) continue;
      runCounter(item.target);
      counterWatcher.unobserve(item.target);
    }
  }, { threshold: 0.4 });
  counters.forEach((node) => counterWatcher.observe(node));
} else {
  counters.forEach(runCounter);
}

/* ── объём карточек: наклон и подсветка под курсором ────────────────────── */
const fine = window.matchMedia('(hover: hover) and (pointer: fine)').matches;
if (fine) {
  document.querySelectorAll('.card').forEach((card) => {
    card.addEventListener('pointermove', (event) => {
      const box = card.getBoundingClientRect();
      const x = (event.clientX - box.left) / box.width;
      const y = (event.clientY - box.top) / box.height;
      card.style.setProperty('--ry', ((x - 0.5) * 9).toFixed(2) + 'deg');
      card.style.setProperty('--rx', ((0.5 - y) * 7).toFixed(2) + 'deg');
      card.style.setProperty('--mx', (x * 100).toFixed(1) + '%');
      card.style.setProperty('--my', (y * 100).toFixed(1) + '%');
    });
    card.addEventListener('pointerleave', () => {
      card.style.setProperty('--ry', '0deg');
      card.style.setProperty('--rx', '0deg');
    });
  });

  /* окно приложения в первом экране слегка поворачивается за курсором */
  const frame = document.getElementById('appFrame');
  const stage = document.querySelector('.stage');
  if (frame && stage) {
    stage.addEventListener('pointermove', (event) => {
      const box = stage.getBoundingClientRect();
      const x = (event.clientX - box.left) / box.width - 0.5;
      const y = (event.clientY - box.top) / box.height - 0.5;
      frame.style.transform =
        'translate3d(' + (x * 10).toFixed(2) + 'px, ' + (y * 8).toFixed(2) + 'px, 0) rotate(-1.4deg)';
    });
    stage.addEventListener('pointerleave', () => {
      frame.style.transform = '';
    });
  }
}
