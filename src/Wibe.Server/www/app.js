/* ICDK — клиент. Ванильный JS: один файл, без сборки.
   Иконки косметики приходят с локального диска (декодированы из pak01_dir.vpk), сеть не нужна. */

const state = {
  ready: false,
  session: null,
  mount: null,
  heroes: [],
  personas: [],
  collection: [],
  sections: {},
  filter: '',
  hero: null,            // текущий /api/hero
  loadout: { heroes: {} },
  world: { radiantTowers: [], direTowers: [], terrains: [], emblems: [], radiantCreeps: [], direCreeps: [], selected: {} },
  shelf: null,           // выбранная полка на экране героя
  extraOnly: true,       // при надетой Аркане: только предметы в свободные слоты
  busy: false,
};

const $ = (sel, root = document) => root.querySelector(sel);
const html = (strings, ...values) => strings.raw.reduce((acc, part, i) => acc + part + (values[i] ?? ''), '');
const esc = (value) => String(value ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const heroPortrait = (hero) => `/api/img?hero=${encodeURIComponent(hero?.folder || hero)}&kind=portrait`;
const heroCrop = (hero) => `/api/img?hero=${encodeURIComponent(hero?.folder || hero)}&kind=crop`;
const personaImage = (persona) => persona?.art
  ? `/art/personas/${persona.itemId}.jpg`
  : (itemImage(persona) || persona?.crop || FALLBACK);
// На главной личность стоит в общем ряду героев: та же официальная картинка-портрет.
const personaCardImage = (persona) => itemImage(persona) || personaImage(persona);
const itemImage = (item) => {
  if (!item?.image) return null;
  return `/api/img?inv=${encodeURIComponent(item.image)}`;
};
const invImage = (path) => `/api/img?inv=${encodeURIComponent(path)}`;
const FALLBACK = '/static/placeholder.svg';

const api = {
  async get(url) {
    const res = await fetch(url);
    if (!res.ok) throw new Error(`${res.status} ${url}`);
    return res.json();
  },
  async post(url, body) {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body ?? {}),
    });
    if (!res.ok) throw new Error(`${res.status} ${url}`);
    return res.json();
  },
};

/* ── загрузка состояния ─────────────────────────────────── */

async function boot() {
  for (let attempt = 0; attempt < 600; attempt++) {
    try {
      const info = await api.get('/api/state');
      paintChips(info);
      if (info.ready) {
        state.ready = true;
        $('#boot').style.display = 'none';
        await loadHeroes();
        await Promise.all([loadCollection(), loadLoadout(), loadPresets(), loadWorld()]);
        await refreshSession();
        wireHeader();
        route();
        return;
      }
      $('#boot-text').textContent = info.status || 'подготовка…';
      if (info.error) {
        $('#boot').style.display = 'none';
        wireDotaPath();
        await openDotaPath(info.error);
        return;
      }
    } catch (err) {
      /* сервер ещё поднимается */
    }
    await new Promise((r) => setTimeout(r, 300));
  }
}

function paintChips(info) {
  const text = $('#status-text');
  const dot = $('#status-dot');
  if (!text || !dot) return;
  if (!info.gameInfoClean) {
    text.textContent = 'Нужна проверка файлов';
    dot.className = 'dot err';
  } else if (!info.modFolderReady) {
    text.textContent = 'Нет папки мода';
    dot.className = 'dot warn';
  } else if (info.gameRunning) {
    text.textContent = 'Закрой Dota 2';
    dot.className = 'dot warn';
  } else {
    text.textContent = 'Готово к матчу';
    dot.className = 'dot ok';
  }
  dot.title = text.title = info.gameInfoClean
    ? 'gameinfo.gi не изменён; мод монтируется только из языковой папки'
    : 'gameinfo.gi изменён — нажми «Декомпилировать» в меню';
  const build = $('#build-id');
  if (build) build.textContent = info.buildId ? `build ${info.buildId}` : '';
  if (info.gameRunning) toast('Закрой Dota 2 перед сборкой мода', 'err', 5000);
}

async function loadCollection() {
  try {
    // Главная берёт карточки из /api/heroes: сервер читает папки Heroes. Статический
    // collection.json — только аварийный запас для очень старого сервера.
    if (!state.collection?.length) {
      const res = await fetch('/static/collection.json', { cache: 'no-store' });
      if (res.ok) state.collection = await res.json();
    }
    const sec = await fetch('/static/hero_sections.json', { cache: 'no-store' });
    if (sec.ok) state.sections = await sec.json();
  } catch (err) { state.collection ||= []; state.sections = {}; }
}

async function loadHeroes() {
  const data = await api.get('/api/heroes');
  state.heroes = data.heroes;
  state.personas = data.personas || [];
  if (Array.isArray(data.collection) && data.collection.length) state.collection = data.collection;
  if (data.loadout) state.loadout = { ...state.loadout, heroes: state.loadout.heroes };
}

async function loadLoadout() {
  try {
    state.loadout = await api.get('/api/loadout');
  } catch (err) {
    state.loadout = { heroes: {} };
  }
  state.loadout.heroes ??= {};
  state.loadout.styles ??= {};
  state.loadout.rareTaunt ??= {};
}

/** Выбранный вариант оформления (0/1/2) по слотам героя. */
function stylesFor(folder) {
  state.loadout.styles ??= {};
  state.loadout.styles[folder] ??= {};
  return state.loadout.styles[folder];
}

/** Насмешка играет и в витрине/редком простое, не только по кнопке. */
function rareTauntFor(folder) {
  state.loadout.rareTaunt ??= {};
  return Boolean(state.loadout.rareTaunt[folder]);
}

/* ── шапка ──────────────────────────────────────────────── */

function wireHeader() {
  wireDotaPath();

  const photos = $('#btn-photos');
  photos.onclick = async () => {
    const started = await api.post('/api/images/prefetch', {});
    toast(started.message, started.started ? 'ok' : '', 4000);
    pollIcons();
  };
  pollIcons();

  $('#btn-compile').onclick = compileAll;
  $('#btn-decompile').onclick = decompileAll;

  $('#btn-launch').onclick = async () => {
    const result = await api.post('/api/launch', {});
    toast(result.message, result.success ? 'ok' : 'err', 6000);
    if (result.success) setTimeout(() => refreshMount(true), 30000);
  };

  $('#btn-preset-save').onclick = async () => {
    const name = prompt('Название пресета', `сборка ${new Date().toLocaleDateString('ru-RU')}`);
    if (name === null) return;
    const result = await api.post('/api/presets', { action: 'save', name });
    toast(result.message, result.success ? 'ok' : 'err');
    await loadPresets();
  };

  $('#btn-preset-apply').onclick = async () => {
    const id = $('#presets').value;
    if (!id) {
      toast('сначала выбери пресет в списке', '', 3000);
      return;
    }

    const result = await api.post('/api/presets', { action: 'apply', id });
    toast(result.message, result.success ? 'ok' : 'err', 5000);
    if (result.success) {
      await Promise.all([loadLoadout(), loadHeroes()]);
      await refreshSession();
      route();
    }
  };
}

/* ── папка Dota 2 ───────────────────────────────────────── */

let dotaWired = false;
let dotaBusy = false;

function wireDotaPath() {
  if (dotaWired) return;
  dotaWired = true;
  const modal = $('#dota-modal');
  if (!modal) return;

  $('#btn-dota-path').onclick = () => openDotaPath();
  $('#btn-dota-cancel').onclick = closeDotaPath;
  $('#btn-dota-browse').onclick = browseDotaPath;
  $('#btn-dota-apply').onclick = applyDotaPath;
  $('#btn-dota-auto').onclick = resetDotaPath;
  modal.addEventListener('click', (event) => {
    if (event.target === modal && !dotaBusy) closeDotaPath();
  });
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && !modal.hidden && !dotaBusy) closeDotaPath();
  });
}

async function openDotaPath(errorText) {
  const modal = $('#dota-modal');
  if (!modal) return;
  modal.hidden = false;
  $('#dota-hint').classList.remove('err');
  $('#dota-hint').textContent = 'Можно указать и game\\dota — путь найдётся автоматически.';
  $('#dota-current').textContent = 'определяю…';
  $('#dota-source').textContent = '';
  try {
    const info = await api.get('/api/dota');
    $('#dota-current').textContent = info.gameDir || info.error || 'Dota 2 не найдена';
    $('#dota-source').textContent = info.source || '';
    $('#dota-path-input').value = info.gameDir || '';
    if (info.error) {
      $('#dota-hint').textContent = info.error;
      $('#dota-hint').classList.add('err');
    }
  } catch (err) {
    $('#dota-current').textContent = errorText || 'сервер не ответил';
  }
  setTimeout(() => $('#dota-path-input').focus(), 40);
}

function closeDotaPath() {
  const modal = $('#dota-modal');
  if (modal) modal.hidden = true;
}

function dotaSetBusy(busy, text) {
  dotaBusy = busy;
  $('#dota-modal').classList.toggle('busy', busy);
  $('#btn-dota-apply').disabled = busy;
  $('#btn-dota-browse').disabled = busy;
  $('#btn-dota-auto').disabled = busy;
  if (text) $('#dota-hint').textContent = text;
}

async function browseDotaPath() {
  if (dotaBusy) return;
  const button = $('#btn-dota-browse');
  const old = button.textContent;
  button.textContent = 'Открываю…';
  button.disabled = true;
  try {
    const result = await api.post('/api/dota/browse', {});
    if (result.success && result.path) {
      $('#dota-path-input').value = result.path;
      $('#dota-hint').classList.remove('err');
      $('#dota-hint').textContent = 'Путь выбран. Нажми «Применить».';
    } else if (result.message) {
      toast(result.message, '', 2600);
    }
  } catch (err) {
    toast('не удалось открыть выбор папки: ' + err.message, 'err');
  } finally {
    button.textContent = old;
    button.disabled = false;
  }
}

async function applyDotaPath() {
  if (dotaBusy) return;
  const path = $('#dota-path-input').value.trim();
  if (!path) {
    $('#dota-hint').textContent = 'Укажи папку Dota 2 или нажми «Авто (Steam)».';
    $('#dota-hint').classList.add('err');
    return;
  }

  dotaSetBusy(true, 'переключаю установку и перечитываю паки…');
  try {
    const result = await api.post('/api/dota/path', { path });
    if (!result.success) {
      dotaSetBusy(false, result.message || 'не удалось применить путь');
      $('#dota-hint').classList.add('err');
      return;
    }

    toast(result.message, 'ok', 3000);
    closeDotaPath();
    location.reload();
  } catch (err) {
    dotaSetBusy(false, 'сервер не ответил: ' + err.message);
    $('#dota-hint').classList.add('err');
  }
}

async function resetDotaPath() {
  if (dotaBusy) return;
  if (!confirm('Вернуть автоопределение Dota 2 через Steam?')) return;
  dotaSetBusy(true, 'ищу Dota 2 через Steam…');
  try {
    const result = await api.post('/api/dota/path', { path: '' });
    if (!result.success) {
      dotaSetBusy(false, result.message || 'Dota 2 не найдена через Steam');
      $('#dota-hint').classList.add('err');
      return;
    }
    toast(result.message, 'ok', 3000);
    closeDotaPath();
    location.reload();
  } catch (err) {
    dotaSetBusy(false, 'сервер не ответил: ' + err.message);
    $('#dota-hint').classList.add('err');
  }
}

async function loadPresets() {
  try {
    const data = await api.get('/api/presets');
    const select = $('#presets');
    if (!select) return;
    select.innerHTML = '<option value="">пресеты…</option>' + (data.presets || []).map((p) =>
      `<option value="${esc(p.id)}">${esc(p.name)} · ${p.heroes} гер.</option>`).join('');
  } catch (err) {
    /* пресеты необязательны */
  }
}

let iconsTimer = null;
async function pollIcons() {
  const btn = $('#btn-photos');
  if (!btn) return;
  clearTimeout(iconsTimer);
  try {
    const status = await api.get('/api/images/prefetch');
    if (status.running) {
      btn.disabled = true;
      btn.textContent = status.done > 0 ? `Фото ${status.done}` : 'Декодирую…';
      btn.title = status.phase;
      iconsTimer = setTimeout(pollIcons, 2500);
      return;
    }

    btn.disabled = false;
    btn.textContent = status.indexKeys > 0 ? `Обновить фото · ${status.indexKeys}` : 'Обновить фото из VPK';
    btn.title = status.message || `иконки: ${status.directory}`;
    if (status.message) toast(status.message, '', 8000);
    iconsTimer = setTimeout(pollIcons, 20000);
  } catch (err) {
    iconsTimer = setTimeout(pollIcons, 8000);
  }
}

/* ── маршрутизация ──────────────────────────────────────── */

function route() {
  if (!state.ready) return;
  const hash = location.hash || '#/';
  const world = /^#\/world/i.test(hash);
  document.body.classList.toggle('world-mode', world);
  $('#mode-heroes')?.classList.toggle('on', !world);
  $('#mode-world')?.classList.toggle('on', world);
  const match = hash.match(/^#\/hero\/([\w_]+)(?:\?([^#]*))?/i);
  if (match) {
    openHero(match[1], new URLSearchParams(match[2] || ''));
  } else if (world) {
    renderWorld();
  } else {
    renderHeroes();
  }
}

window.addEventListener('hashchange', route);

/* ── общая строка примерки ─────────────────────────────── */

function heroName(folder) {
  return state.heroes.find((h) => h.folder === folder)?.name || folder;
}

function heroOf(folder) {
  return state.heroes.find((h) => h.folder === folder) || { folder };
}

function loadoutEntries() {
  return Object.entries(state.loadout.heroes || {})
    .filter(([, picks]) => picks && Object.keys(picks).length > 0)
    .sort((a, b) => heroName(a[0]).localeCompare(heroName(b[0]), 'ru'));
}

function updateCompileLabel() {
  const button = $('#btn-compile');
  if (!button || state.busy) return;
  const count = loadoutEntries().length;
  button.textContent = count > 1 ? `Собрать мод · ${count}` : 'Собрать мод';
}

/** Активные герои примерки: один компактный дропдаун в верхней панели. */
function syncActiveHeroSelect() {
  const select = $('#active-hero');
  if (!select) return;
  const entries = loadoutEntries();
  const current = location.hash.match(/^#\/hero\/([\w_]+)/i)?.[1] || '';
  select.innerHTML = '<option value="">Примерка · ' + (entries.length ? entries.length + ' гер.' : 'пусто') + '</option>'
    + entries.map(([folder, picks]) => `<option value="${esc(folder)}" ${folder === current ? 'selected' : ''}>${esc(heroName(folder))} · ${Object.keys(picks).length}</option>`).join('')
    + (current && !entries.some(([f]) => f === current) ? `<option value="${esc(current)}" selected>${esc(heroName(current))} · без скинов</option>` : '');
  select.onchange = () => {
    if (select.value) location.hash = `#/hero/${select.value}`;
  };
}

function renderStrip() {
  updateCompileLabel();
  syncActiveHeroSelect();
}

/* ── экран героев ───────────────────────────────────────── */

function personaTitle(hero, persona) {
  const id = Number(persona.itemId ?? persona.id);
  if (id === 13783) return `${hero.name} (Wei)`;
  if (/\sAutomaton Persona$/i.test(persona.name)) return `${hero.name} (Automaton)`;
  return persona.name.replace(/\sPersona$/i, '');
}

function personasFor(folder) {
  return (state.personas || []).filter((p) => p.folder === folder);
}

function renderHeroes() {
  const screen = $('#screen');
  const total = Object.values(state.loadout.heroes || {}).filter((p) => Object.keys(p).length > 0).length;
  screen.innerHTML = html`
    <div class="heroes-screen">
      <div class="heroes-toolbar">
        <label class="search">
          <span style="color:var(--dim)">⌕</span>
          <input id="q" placeholder="поиск героя" value="${esc(state.filter)}" autocomplete="off" />
        </label>
        <span class="hint" id="count"></span>
        <div class="spacer"></div>
        <span class="hint">${total > 0 ? `${total} гер. в примерке` : 'выбери героя и собери образ'}</span>
      </div>
      <div class="heroes-grid" id="grid"></div>
    </div>`;

  const grid = $('#grid');
  const paint = () => {
    const q = state.filter.trim().toLowerCase();
    // Каждая папка Heroes — отдельная карточка. Личности отдельными карточками не
    // дублируются: они живут в переключателе «Базовый герой | Личность» на странице героя.
    const all = (state.collection || []).filter((e) => !(e.isPersona || e.personaId));
    const list = q.length === 0
      ? all
      : all.filter((e) => e.name.toLowerCase().includes(q) || (e.hero || '').includes(q));

    const selected = state.loadout.heroes || {};
    const cards = [];
    $('#count').textContent = `${list.length} из ${all.length}`;
    for (const e of list) {
      const picks = Object.keys(selected[e.hero] || {}).length;
      const active = e.personaId && Object.values(selected[e.hero] || {}).includes(e.personaId);
      cards.push(html`
      <button class="hero-card ${e.isPersona || e.personaId ? 'is-persona' : ''} ${active ? 'selected' : ''}"
              data-hero="${esc(e.hero)}" data-view="${esc(e.name)}" ${e.personaId ? `data-persona="${e.personaId}"` : ''}
              title="${esc(e.name)}" ${e.hero ? '' : 'disabled'}>
        <img loading="lazy" src="${esc(e.img)}" alt="${esc(e.name)}"
             onerror="this.onerror=null;this.src='${FALLBACK}'" />
        ${picks > 0 ? `<span class="hero-badge">${picks}</span>` : ''}
        <div class="cap"><b>${esc(e.name)}</b><span>${e.isPersona || e.personaId ? 'ЛИЧНОСТЬ' : (picks > 0 ? `${picks} в примерке` : '')}</span></div>
      </button>`);
    }
    grid.innerHTML = cards.join('');

    for (const card of grid.querySelectorAll('.hero-card')) {
      card.addEventListener('click', () => {
        if (!card.dataset.hero) return;
        const persona = card.dataset.persona;
        const view = card.dataset.view ? `view=${encodeURIComponent(card.dataset.view)}` : '';
        const plain = !persona && personasFor(card.dataset.hero).length > 0;
        const query = [view, persona ? `persona=${persona}` : (plain ? 'plain=1' : '')].filter(Boolean).join('&');
        location.hash = `#/hero/${card.dataset.hero}` + (query ? `?${query}` : '');
      });
    }
  };

  const input = $('#q');
  input.addEventListener('input', () => { state.filter = input.value; paint(); });
  paint();
  renderStrip();
}

/* ── экран героя ────────────────────────────────────────── */

async function openHero(folder, params) {
  const screen = $('#screen');
  screen.innerHTML = html`
    <div class="editor">
      <div class="editor-head">
        <button class="back" id="back" title="К героям">←</button>
        <div class="hero-title"><h1 id="hname">${esc(folder)}</h1><span id="hsub">загружаю…</span></div>
        <div class="spacer"></div>
        <button class="btn quiet" id="clear">Снять всё</button>
      </div>
      <div class="editor-body">
        <aside class="preview-pane">
          <div class="persona-tabs" id="persona-tabs"></div>
          <div class="skin-card" id="skincard">
            <img id="skinimg" alt="скин" src="${heroCrop(folder)}" onerror="this.src='${FALLBACK}'" />
            <button type="button" class="beta-3d" data-preview-mode="3d" title="3D-примерка (Beta)">3D <i>Beta</i></button>
            <div class="skin-label"><b id="skinname">${esc(folder)}</b><span id="skinslot">как в игре</span></div>
          </div>
          <div class="podium" id="podium">
            <div class="podium-switch">
              <button type="button" data-preview-mode="2d" title="Плоский вид">2D</button>
            </div>
            <canvas id="podium-canvas"></canvas>
            <button type="button" id="podium-reset" title="Сбросить камеру">⟲</button>
            <div class="equipped-strip" id="equipped-strip"></div>
          </div>
          <div class="hero-slots" id="hero-slots"></div>
          <div class="meta" id="hmeta" hidden></div>
        </aside>
        <section class="slots" id="slots"><div class="empty">загружаю полки…</div></section>
      </div>
    </div>`;

  // живой ре-рендер 3D после вставки шаблона в DOM
  requestAnimationFrame(() => {
    const live = document.getElementById('podium-canvas');
    if (live && live.clientWidth > 0) window.Podium?.sync?.(folder, picksFor(folder), stylesFor(folder));
  });

  $('#back').addEventListener('click', () => {
    // если адрес уже корневой, hashchange не придёт — рисуем сетку напрямую
    if (location.hash === '#/' || location.hash === '' || location.hash === '#') {
      state.hero = null;
      renderHeroes();
    } else {
      location.hash = '#/';
    }
  });

  let data;
  try {
    const view = params?.get('view') || '';
    data = await api.get(`/api/hero?folder=${encodeURIComponent(folder)}${view ? `&view=${encodeURIComponent(view)}` : ''}`);
    // адрес может прийти как Anti-Mage, а каталог живёт под слагом antimage
    if (!(data.shelves || []).length) {
      const slug = String(folder).toLowerCase().replace(/[^a-z0-9]/g, '');
      if (slug && slug !== String(folder).toLowerCase()) {
        const retry = await api.get(`/api/hero?folder=${encodeURIComponent(slug)}${view ? `&view=${encodeURIComponent(view)}` : ''}`);
        if ((retry.shelves || []).length) data = retry;
      }
    }
  } catch (err) {
    $('#slots').innerHTML = '<div class="empty">не удалось прочитать каталог: ' + esc(err.message) + '</div>';
    return;
  }

  state.hero = data;
  // сервер уже разрешил имя героя (страница wraith_king и записи skeleton_king — одно),
  // поэтому примерка берётся из ответа, а не по имени папки
  if (data.picks) state.loadout.heroes[folder] = { ...data.picks };
  if (data.styles) state.loadout.styles[folder] = { ...data.styles };
  if (typeof data.rareTaunt === 'boolean') state.loadout.rareTaunt[folder] = data.rareTaunt;
  data.name = data.catalogName || data.name;
  const shelf = params?.get('shelf') || '';
  const wantedPersona = Number(params?.get('persona') || data.personaId || 0);
  const wanted = shelf && data.shelves.some((s) => s.id === shelf) ? shelf : null;
  state.shelf = wanted || (state.shelf && data.shelves.some((s) => s.id === state.shelf) ? state.shelf : (data.shelves[0]?.id || null));
  state.loadout.heroes[folder] ??= {};

  if (params?.get('plain')) {
    const picks = picksFor(folder);
    let changed = false;
    for (const [slot, id] of Object.entries({ ...picks })) {
      if (isPersonaSlot(slot) || isPersonaItem(itemAt(data, id))) {
        delete picks[slot];
        changed = true;
      }
    }
    if (changed) await savePicksNow(folder);
  }

  if (wantedPersona > 0) {
    // Базовый item_id Личности всегда едет в сборку: даже если игрок выбрал
    // только один предмет из её папки, селектор персоны уже стоит.
    const picks = picksFor(folder);
    if (picks.persona_selector !== wantedPersona) {
      picks.persona_selector = wantedPersona;
      await savePicksNow(folder);
    }
  }

  applyCollectionSections(data);
  const urlShelf = params?.get('shelf');
  if (urlShelf && data.customShelves?.some((s) => s.id === urlShelf)) state.shelf = urlShelf;
  $('#hname').textContent = data.catalogName || data.name;
  const visible = (data.customShelves || data.shelves).filter((s) => (s.items || []).length);
  const total = visible.reduce((n, s) => n + s.items.length, 0);
  $('#hsub').textContent = `${total} вариантов · ${visible.length} полок · ${openShelvesFor(data).length} слотов`;

  renderShelves(data);
  renderFooter(data, folder);
  paintSelection(data);
  // Переключатель «Базовый герой | Личность» и полоска надетого рисуются сразу при
  // открытии страницы, а не только после первого клика по предмету.
  renderPersonaTabs(data);
  renderEquippedStrip(data);
  renderStrip();
}

function picksFor(folder) {
  state.loadout.heroes[folder] ??= {};
  return state.loadout.heroes[folder];
}

/** Сохраняем выбор на сервер (selected_loadout.json): переживает переходы и перезапуск. */
const saveTimers = {};

window.__icdkSaveLoadout = () => { const folder = state.hero?.folder; if (folder) savePicksNow(folder); };

async function savePicksNow(folder) {
  if (!folder) return;
  const picks = picksFor(folder);
  const styles = stylesFor(folder);
  try {
    await api.post('/api/loadout', {
      folder,
      picks: Object.entries(picks).filter(([, id]) => id).map(([slot, id]) => ({ slot, itemId: Number(id) })),
      styles: Object.entries(styles).filter(([, index]) => index > 0).map(([slot, index]) => ({ slot, index: Number(index) })),
      rareTaunt: Boolean(picks.taunt) && rareTauntFor(folder),
    });
  } catch (err) {
    toast('не удалось сохранить примерку: ' + err.message, 'err');
  }
}

function persistPicks(folder) {
  clearTimeout(saveTimers[folder]);
  saveTimers[folder] = setTimeout(() => savePicksNow(folder), 180);
  renderStrip();
  // 3D-подиум переодевается сразу, как только меняется выбор
  const view = state.hero;
  if (view && view.folder) {
    renderPersonaTabs(view);
    renderEquippedStrip(view);
  }
  window.Podium?.sync?.(folder, picksFor(folder), stylesFor(folder));
}

function rarityName(rarity) {
  const value = String(rarity || '').toLowerCase();
  const map = {
    common: 'обычный', uncommon: 'необычный', rare: 'редкий', mythical: 'мифический',
    legendary: 'легендарный', immortal: 'immortal', arcana: 'arcana', ancient: 'древний',
    seasonal: 'сезонный', exclusive: 'эксклюзив', unusual: 'необычный',
  };
  const key = value.replace('rarity_', '');
  return map[key] || key || '';
}

function rarityClass(rarity) {
  const value = String(rarity || '').toLowerCase().replace('rarity_', '');
  if (value.includes('arcana')) return 'r-arcana';
  if (value.includes('immortal')) return 'r-immortal';
  if (value.includes('legendary')) return 'r-legendary';
  if (value.includes('mythical')) return 'r-mythical';
  if (value.includes('rare')) return 'r-rare';
  if (value.includes('uncommon')) return 'r-uncommon';
  return 'r-common';
}

/** Крупная карточка слева: выбранный скин, иначе арт героя. */
function paintSelection(data) {
  const picks = picksFor(data.folder);
  const chosen = [];
  for (const shelf of data.shelves) {
    for (const item of shelf.items) {
      if (item.bundle) {
        if (item.bundle.some((p) => picks[p.slot] === p.itemId)) chosen.push({ shelf, item, bundle: true });
      } else if (picks[item.slot] === item.id) {
        chosen.push({ shelf, item, bundle: false });
      }
    }
  }

  const img = $('#skinimg');
  const name = $('#skinname');
  const sub = $('#skinslot');
  const hero = state.heroes.find((h) => h.folder === data.folder) || data.folder;
  const personaPreview = activePersona(data);
  const heroUrl = personaPreview ? personaImage(personaPreview) : heroCrop(hero);

  const top = chosen.find((c) => c.item.slot !== 'body') || chosen[0];
  if (personaPreview) {
    // Внутри личности показываем её собственный арт из папки игрока, а не выбранный сет.
    name.textContent = personaTitle(hero, personaPreview);
    sub.textContent = `ЛИЧНОСТЬ · ${personaPreview.name}`;
    img.onerror = () => { img.onerror = null; img.src = FALLBACK; };
    img.src = personaImage(personaPreview);
  } else if (top) {
    name.textContent = top.item.name;
    sub.textContent = `${shelfLabel(top.shelf.id) || top.item.slot}${top.item.rarity ? ' · ' + rarityName(top.item.rarity) : ''}`;
    if (top.item.image) {
      img.onerror = () => { img.onerror = null; img.src = heroUrl; };
      img.src = itemImage(top.item);
    } else {
      img.src = heroUrl;
    }
  } else {
    name.textContent = data.name;
    sub.textContent = 'как в игре';
    img.onerror = () => { img.onerror = null; img.src = FALLBACK; };
    img.src = heroUrl;
  }

  const rows = [];
  if (top) rows.push(`<div>заменяю: <b>${esc((top.item.target || data.bodyTarget).split('/').pop())}</b></div>`);
  rows.push(`<div>в игре: <b>${data.installed ? esc(data.installed.title) : 'мод не скомпилирован'}</b></div>`);
  const fileCount = Array.isArray(data.installed?.files)
    ? data.installed.files.length
    : (typeof data.installed?.files === 'number' ? data.installed.files : 0);
  if (fileCount > 0) {
    rows.push(`<div style="color:var(--dim)">${fileCount} файлов в pak${String(data.installed.slot).padStart(2, '0')}</div>`);
  }
  $('#hmeta').innerHTML = rows.join('');
  renderHeroSlots(data);
}


/* ── переключение «Базовый герой | Личность» и полоска надетого ───────── */
function renderPersonaTabs(data) {
  const host = $('#persona-tabs');
  if (!host) return;
  const personas = personasFor(data.folder);
  if (!personas.length) { host.innerHTML = ''; return; }
  const picks = picksFor(data.folder);
  const activeId = Number(picks.persona_selector || 0);
  const button = (id, title) =>
    `<button type="button" class="persona-tab ${Number(id) === activeId ? 'on' : ''}" data-persona-tab="${id}">${esc(title)}</button>`;
  // «Базовый герой | Личность»: одна Личность — просто подпись «Личность»,
  // если их несколько — имя приписано, чтобы было видно, что выбираешь.
  host.innerHTML = button(0, 'Базовый герой') +
    personas.map((persona) => button(
      persona.itemId ?? persona.id,
      personas.length === 1 ? 'Личность' : 'Личность · ' + personaTitle(data, persona))).join('');
  host.querySelectorAll('[data-persona-tab]').forEach((node) => {
    node.addEventListener('click', () => {
      const id = Number(node.dataset.personaTab) || 0;
      const picks = picksFor(data.folder);
      if (id) picks.persona_selector = id; else delete picks.persona_selector;
      window.__icdkSaveLoadout?.();
      openHero(data.folder);
    });
  });
}

function renderEquippedStrip(data) {
  const host = $('#equipped-strip');
  if (!host) return;
  const picks = picksFor(data.folder);
  const worn = [];
  for (const shelf of data.shelves || []) {
    for (const item of shelf.items || []) {
      const isBundle = Array.isArray(item.bundle) && item.bundle.length > 0;
      if (isBundle) continue;
      if (item.slot === 'persona_selector') continue;
      if (Number(picks[item.slot]) === Number(item.id)) worn.push({ slot: item.slot, item });
    }
  }
  if (!worn.length) { host.innerHTML = '<span class="strip-empty">стоковый образ</span>'; return; }
  host.innerHTML = worn.slice(0, 8).map(({ slot, item }) => `
    <span class="strip-item" title="${esc(item.name)}">
      <img loading="lazy" src="${itemImage(item)}" alt="${esc(item.name)}" onerror="this.style.display='none'" />
      <b>×</b>
    </span>`).join('');
  host.querySelectorAll('.strip-item').forEach((node, index) => {
    node.addEventListener('click', () => {
      const slot = worn[index].slot;
      const picks = picksFor(data.folder);
      delete picks[slot];
      window.__icdkSaveLoadout?.();
      openHero(data.folder);
    });
  });
}

/* ── база образа: Аркана / Личность и её свободные слоты ─────────────── */

function isPersonaSlot(slot) {
  return /_persona_\d+$/i.test(String(slot || ''));
}

function isPersonaItem(item) {
  return Boolean(item?.persona) || Number(item?.personaIndex) > 0 || isPersonaSlot(item?.slot) ||
    (item?.bundle || []).some((p) => isPersonaSlot(p.slot));
}

function itemAt(data, id) {
  if (!id) return null;
  for (const shelf of data.shelves) {
    const hit = shelf.items.find((i) => i.id === Number(id));
    if (hit) return hit;
  }
  return null;
}

function activePersona(data) {
  if (data?.isPersona && data.personaId) {
    return { id: Number(data.personaId), itemId: Number(data.personaId), name: data.catalogName || data.name,
      art: personaImage({ itemId: data.personaId, art: '' }), persona: true, slot: 'persona_selector' };
  }
  const picks = picksFor(data.folder);
  for (const shelf of data.shelves) {
    for (const item of shelf.items) {
      if (picks[item.slot] !== item.id) continue;
      if (isPersonaItem(item) && !item.parts?.length) return item;
    }
  }
  const selected = itemAt(data, picks.persona_selector);
  return selected && isPersonaItem(selected) ? selected : null;
}

function activeArcana(data) {
  const picks = picksFor(data.folder);
  for (const shelf of data.shelves.filter((s) => s.id === 'arcana')) {
    for (const item of shelf.items) {
      if (picks[item.slot] === item.id && !isPersonaItem(item)) return item;
    }
  }
  return null;
}

function basePartIds(item) {
  return new Set((item?.parts || []).map((p) => Number(p.itemId)));
}

function openSlotsFor(data) {
  const picks = picksFor(data.folder);
  const persona = activePersona(data);
  const arcana = persona ? null : activeArcana(data);
  const all = allSlotsFor(data);
  const own = new Set(['persona_selector']);
  if (persona) {
    for (const slot of all) if (!isPersonaSlot(slot)) own.add(slot);
    return all.filter((slot) => isPersonaSlot(slot));
  }
  if (arcana) {
    const ids = basePartIds(arcana);
    own.add(arcana.slot);
    for (const part of arcana.parts || []) {
      const cur = picks[part.slot];
      // Пользовательский предмет поверх части арканы остаётся видимым и снимаемым.
      if (cur != null && cur !== part.itemId && !ids.has(Number(cur))) continue;
      own.add(part.slot);
    }
    return all.filter((slot) => !own.has(slot) && !isPersonaSlot(slot));
  }
  // без базы: всё, что реально можно надеть, кроме селектора личности
  return all.filter((slot) => slot !== 'persona_selector' && !isPersonaSlot(slot));
}

function extraItemsFor(data) {
  const picks = picksFor(data.folder);
  const arcana = activeArcana(data);
  const open = new Set(openSlotsFor(data));
  const baseIds = basePartIds(arcana);
  const seen = new Set();
  const out = [];
  for (const shelf of data.shelves) {
    if (shelf.id === 'sets' || shelf.id === 'arcana') continue;
    for (const item of shelf.items) {
      if (Array.isArray(item.bundle) || !open.has(item.slot) || seen.has(item.id)) continue;
      if (arcana && (baseIds.has(item.id) || item.id === arcana.id)) continue;
      if (isPersonaItem(item)) continue;
      seen.add(item.id);
      out.push(item);
    }
  }
  const rank = { arcana: 9, immortal: 8, ancient: 7, legendary: 6, mythical: 5, rare: 4, uncommon: 3, common: 1 };
  return out.sort((a, b) => {
    const ra = rank[String(a.rarity || '').toLowerCase().replace('rarity_', '')] || 0;
    const rb = rank[String(b.rarity || '').toLowerCase().replace('rarity_', '')] || 0;
    return rb - ra || String(a.name).localeCompare(String(b.name), 'ru');
  });
}

const SLOT_RU = {
  body: 'Тело', weapon: 'Оружие', offhand_weapon: 'Вторая рука', off_hand: 'Вторая рука', head: 'Голова',
  armor: 'Доспех', arms: 'Руки', belt: 'Пояс', shoulder: 'Плечи', back: 'Спина',
  mount: 'Ездовое', legs: 'Ноги', tail: 'Хвост', wings: 'Крылья', neck: 'Шея',
  pet: 'Питомец', summon: 'Питомец', misc: 'Разное', taunt: 'Насмешка',
  shield: 'Щит', quiver: 'Колчан', gloves: 'Перчатки', costume: 'Костюм', banners: 'Знамена', flask: 'Фляга',
  voice: 'Голос', ability: 'Эффект', set: 'Сет', summon_persona_1: 'Питомец',
};

function allSlotsFor(data) {
  const out = new Set((data.slots || []).map((s) => s.slot));
  for (const shelf of data.customShelves || []) {
    for (const item of shelf.items || []) {
      const slot = item.slot;
      if (slot && slot !== 'set' && slot !== 'ability' && slot !== 'persona_selector') out.add(slot);
    }
  }
  return [...out];
}

function slotLabel(data, slot) {
  const plain = String(slot || '').replace(/_persona_\d+$/i, '');
  const found = (data.slots || []).find((s) => s.slot === slot || s.slot === plain);
  return SLOT_RU[String(slot)] || SLOT_RU[plain] || SECTION_RU[String(slot).replace(/_/g, ' ')] || SECTION_RU[plain.replace(/_/g, ' ')] || SECTION_RU[plain] || found?.label || plain.replace(/_/g, ' ');
}


/* ── «Кукла героя»: строгий матчинг слотов ──────────────────────────────
   Каждый квадрат куклы отвечает ровно за один реальный слот героя. Предмет
   показывается в квадрате только если его собственный слот совпадает с этим
   слотом: одна карточка (например насмешка) не может размножиться по чужим
   квадратам. Картинка берётся из настоящего кропа карточки (/api/crop) или из
   локальной иконки предмета (/api/img?inv=), а рамка красится в редкость. */

/* Порядок квадратов; в одном квадрате могут стоять родственные слоты. */
const DOLL_GROUPS = [
  { key: 'body', label: 'Тело', slots: ['body'] },
  { key: 'weapon', label: 'Оружие', slots: ['weapon'] },
  { key: 'offhand', label: 'Вторая рука', slots: ['offhand_weapon', 'off_hand', 'shield', 'quiver'] },
  { key: 'head', label: 'Голова', slots: ['head', 'hair', 'neck', 'beard', 'heads'] },
  { key: 'back', label: 'Спина', slots: ['back', 'wings', 'tail'] },
  { key: 'arms', label: 'Руки', slots: ['arms', 'gloves'] },
  { key: 'armor', label: 'Доспех', slots: ['armor', 'belt'] },
  { key: 'legs', label: 'Ноги', slots: ['legs', 'mount', 'feet'] },
  { key: 'shoulder', label: 'Плечи', slots: ['shoulder', 'shoulders'] },
  { key: 'pet', label: 'Питомец', slots: ['pet', 'summon', 'courier', 'ward', 'healing_ward', 'familiars', 'eidolons'] },
  { key: 'voice', label: 'Голос', slots: ['voice', 'announcer'] },
  { key: 'taunt', label: 'Насмешка', slots: ['taunt'] },
  { key: 'effects', label: 'Эффекты', slots: ['ability', 'ability_effects', 'ambient_effects', 'ability2', 'ability3', 'ability4'] },
];

/* Слоты, которые никогда не показываются на кукле как доп. квадрат. */
const DOLL_HIDDEN = new Set(['set', 'sets', 'persona_selector', 'misc', 'terrain', 'emblem', 'hud', 'weather']);

function dollGroupOf(slot) {
  const plain = canonicalSlot(slot);
  return DOLL_GROUPS.find((g) => g.key === plain || g.slots.includes(plain)) || null;
}

/** Родственные слоты одного квадрата в порядке приоритета. */
function dollSlotsOf(slot) {
  const group = dollGroupOf(slot);
  if (group) return group.slots;
  return [String(slot || '')];
}

/** Любой предмет экрана героя по id: полки, кастомные полки, части сетов и бандлов. */
function itemAnywhere(data, id) {
  const want = Number(id);
  if (!want) return null;
  const pools = [data.shelves || [], data.customShelves || []];
  for (const shelves of pools) {
    for (const shelf of shelves) {
      for (const item of shelf.items || []) {
        if (Number(item.id) === want) return item;
        for (const part of item.parts || []) {
          if (Number(part.itemId ?? part.id) === want) return { ...part, id: want, image: part.image };
        }
        for (const pick of item.bundle || []) {
          if (Number(pick.itemId ?? pick.id) === want) return { ...pick, id: want, image: pick.image };
        }
      }
    }
  }
  return itemAt(data, want);
}

/** Настоящая картинка предмета: кроп карточки, иначе локальная иконка из png/. */
function dollItemImage(data, item) {
  if (!item) return null;
  if (item.image) return itemImage(item);
  if (item.art) return item.art;
  return null;
}

/**
 * Квадраты куклы: реально доступные слоты героя плюс всё, что сейчас надето.
 * Аркана/Личность в базе тоже заполняет свои родные квадраты картинками.
 */
function dollCells(data) {
  const picks = picksFor(data.folder);
  const persona = activePersona(data);
  const arcana = persona ? null : activeArcana(data);
  const base = persona || arcana;
  const baseSlots = new Set();
  if (base) {
    baseSlots.add(String(base.slot || ''));
    for (const part of base.parts || []) baseSlots.add(String(part.slot || ''));
  }

  const raw = [];
  const add = (slot) => {
    const s = String(slot || '');
    if (!s || DOLL_HIDDEN.has(s) || isPersonaSlot(s)) return;
    if (persona) return;
    if (!raw.includes(s)) raw.push(s);
  };
  for (const slot of data.slots || []) add(slot.slot);
  for (const shelf of data.customShelves || []) {
    for (const item of shelf.items || []) add(item.slot);
  }
  for (const slot of Object.keys(picks)) {
    if (picks[slot]) add(slot);
  }
  if (!persona) for (const slot of baseSlots) add(slot);

  // группировка: один квадрат на родственные слоты
  const cells = [];
  const seen = new Set();
  for (const slot of raw) {
    const group = dollGroupOf(slot);
    const key = group ? group.key : slot;
    if (seen.has(key)) continue;
    seen.add(key);
    cells.push({ key, label: group?.label || slotLabel(data, slot), slots: group ? group.slots : [slot] });
  }
  const order = new Map(DOLL_GROUPS.map((g, i) => [g.key, i]));
  return cells.sort((a, b) => (order.get(a.key) ?? 99) - (order.get(b.key) ?? 99));
}

/** Что реально надето в этот квадрат: только предмет с совпадающим слотом. */
function dollPicked(data, cell) {
  const picks = picksFor(data.folder);
  for (const slot of cell.slots) {
    const id = picks[slot];
    if (!id) continue;
    const item = itemAnywhere(data, id);
    if (!item) continue;
    const own = canonicalSlot(item.slot || slot);
    // Никаких чужих квадратов: предмет обязан принадлежать этому слоту.
    if (cell.slots.includes(own) || !item.slot) {
      return { slot, item, rarity: item.rarity || '', name: item.name || '' };
    }
    // Части арканы/сета приходят без слота — берём по самому слоту выбора.
    if (Array.isArray(item.bundle) || !item.slot) return { slot, item, rarity: item.rarity || '', name: item.name || '' };
  }
  return null;
}

/** Настоящая ли аркана (только качество arcana, не вард и не личность). */
function isRealArcana(item) {
  if (!item || isPersonaItem(item)) return false;
  const rarity = String(item.rarity || '').toLowerCase().replace('rarity_', '');
  if (rarity !== 'arcana') return false;
  const slot = canonicalSlot(item.slot);
  return !['ability', 'ability2', 'ability3', 'ability4', 'ability_effects', 'ambient_effects', 'ward',
    'healing_ward', 'voice', 'taunt', 'courier', 'pet'].includes(slot);
}

function openShelvesFor(data) {
  return dollCells(data);
}

/** Панель «Кукла героя»: база (Аркана/Личность) и сетка доп-слотов. */
function renderHeroSlots(data) {
  const box = $('#hero-slots');
  if (!box) return;
  const picks = picksFor(data.folder);
  const persona = activePersona(data);
  const arcana = persona ? null : (isRealArcana(activeArcana(data)) ? activeArcana(data) : null);
  const base = persona || arcana;
  const cells = dollCells(data);
  const heroUrl = heroCrop(state.heroes.find((h) => h.folder === data.folder) || data.folder);
  const img = (url) => url || heroUrl;

  let baseHtml;
  if (base) {
    const title = base.name;
    const parts = (base.parts || []).length || 1;
    baseHtml = html`
      <button class="hbase picked ${rarityClass(base.rarity || (persona ? 'arcana' : 'arcana'))}"
              data-shelf="arcana" title="${esc(title)}">
        <span class="hbase-img"><img loading="lazy" src="${esc(img(dollItemImage(data, base)))}" alt="${esc(title)}"
          onerror="this.onerror=null;this.src='${esc(heroUrl)}'" /></span>
        <span class="hbase-copy"><em>${persona ? 'БАЗА ЛИЧНОСТИ' : 'БАЗА АРКАНЫ'}</em><b>${esc(title)}</b></span>
        <span class="hbase-count">${parts} слот.</span>
      </button>`;
  } else {
    baseHtml = html`
      <button class="hbase empty" data-shelf="arcana" title="Выбрать Аркану или Личность">
        <span class="hbase-img"><img loading="lazy" src="${esc(heroUrl)}" alt="" /></span>
        <span class="hbase-copy"><em>БАЗА</em><b>Аркана · Личность</b></span>
        <span class="hbase-plus">+</span>
      </button>`;
  }

  let filled = 0;
  const cellsHtml = cells.map((cell) => {
    const hit = dollPicked(data, cell);
    if (hit) filled += 1;
    const pickedSlot = hit?.slot || cell.slots[0];
    const url = hit ? img(dollItemImage(data, hit.item)) : heroUrl;
    const rarity = hit ? `r ${rarityClass(hit.rarity)}` : '';
    return html`
      <button class="hslot ${hit ? 'picked' : 'free'} ${rarity}"
              data-slot="${esc(pickedSlot)}" data-shelf="${esc(shelfForSlot(data, cell))}"
              title="${hit ? esc(hit.name) + (hit.rarity ? ' · ' + esc(rarityName(hit.rarity)) : '') : '+ ' + esc(cell.label) + ' свободен'}">
        <span class="hslot-img"><img loading="lazy" src="${esc(url)}" alt=""
          onerror="this.onerror=null;this.src='${esc(heroUrl)}'" /></span>
        <span class="hslot-name">${esc(cell.label)}</span>
        ${hit
          ? `<span class="hslot-x" data-x="${esc(pickedSlot)}" title="Вернуть сток">×</span>`
          : '<span class="hslot-plus">+</span>'}
      </button>`;
  }).join('');

  box.innerHTML = html`
    <div class="hero-slots-head"><b>Надето</b><span>${filled} из ${cells.length} слот.</span></div>
    ${baseHtml}
    <div class="hero-slots-sub">Доп. слоты · нажми, чтобы выбрать</div>
    <div class="hero-slots-grid">${cellsHtml || '<span class="hint">у героя нет доп. слотов</span>'}</div>`;

  for (const row of box.querySelectorAll('[data-shelf]')) {
    row.addEventListener('click', (event) => {
      const x = event.target.closest('[data-x]');
      if (x) {
        event.stopPropagation();
        delete picks[x.dataset.x];
        delete stylesFor(data.folder)[x.dataset.x];
        persistPicks(data.folder);
        renderHeroSlots(data);
        renderShelves(data);
        paintSelection(data);
        return;
      }
      if (row.dataset.shelf) state.shelf = row.dataset.shelf;
      renderShelves(data);
    });
  }
}

/** Полка, которая отвечает за этот квадрат куклы: своя папка, иначе слот. */
function shelfForSlot(data, cell) {
  const shelves = (data.customShelves || []).filter((shelf) => shelf.id !== 'sets');
  for (const shelf of shelves) {
    if ((shelf.items || []).some((item) => cell.slots.includes(canonicalSlot(item.slot)))) return shelf.id;
  }
  const fromServer = (data.shelves || []).find((shelf) => shelf.id === `slot:${cell.slots[0]}`);
  return fromServer?.id || `slot:${cell.slots[0]}`;
}

function shelfLabel(id) {
  return {
    sets: 'Готовый сет',
    arcana: 'Arcana',
    immortal: 'Immortal',
  }[id] || '';
}

/** Название вкладки раздела: те же разделы, что в игре, и без повторов. */
function tabTitle(shelf) {
  const key = canonicalSlot(shelf);
  if (key === 'arcana') return '★ Аркана';
  if (key === 'sets') return 'Сеты';
  if (key === 'extras') return 'Доп. слоты';
  if (key === 'immortal') return 'Immortal';
  return shelf.title;
}

function artCandidates(item) {
  const out = [item?.image];
  for (const pick of item?.bundle || []) out.push(pick.image);
  for (const part of item?.parts || []) out.push(part.image);
  return out.filter(Boolean);
}

function cardImage(data, item) {
  if (item?.art) return item.art;
  const first = artCandidates(item)[0];
  if (first) return itemImage({ image: first });
  const hero = state.heroes.find((h) => h.folder === data.folder) || data.folder;
  return heroCrop(hero);
}


const SECTION_RU = {
  costume: 'Костюм', voice: 'Голос', 'hero base': 'База', misc: 'Разное',
  beard: 'Борода', heads: 'Головы', 'left head': 'Левая голова', 'right head': 'Правая голова',
  'left arm': 'Левая рука', 'right arm': 'Правая рука', mane: 'Грива',
  crystals: 'Кристаллы', 'crystals (persona)': 'Кристаллы', 'mane (persona)': 'Грива',
  'pet (persona)': 'Питомец', 'tail (persona)': 'Хвост', 'legs (persona)': 'Ноги',
  'arms (persona)': 'Руки', 'offhand (persona)': 'Вторая рука',
  'armor (persona)': 'Доспех', 'head (persona)': 'Голова', 'weapon (persona)': 'Оружие',
  'armor (kid)': 'Доспех', 'arms (kid)': 'Руки', 'back (kid)': 'Спина',
  'head (kid)': 'Голова', 'shoulder (kid)': 'Плечи', 'taunt (kid)': 'Насмешка',
  'voice (kid)': 'Голос', 'forge spirit (kid)': 'Дух кузни',
  'tiny armor': 'Доспех Тайни', 'tiny head': 'Голова Тайни',
  bazooka: 'Базука', barrel: 'Бочка', boar: 'Вепрь', 'burning army': 'Горящая армия',
  cart: 'Телега', claws: 'Когти', demon: 'Демон', eidolons: 'Эйдолоны',
  'evil purse': 'Злая сумка', familiars: 'Фамильяры', fist: 'Кулак', flask: 'Фляга',
  'flesh golem': 'Мясной голем', 'forge spirit': 'Дух кузни', 'frozen sigil': 'Ледяная печать',
  gunt: 'Гант', hawk: 'Ястреб', 'healing ward': 'Целительный тотем',
  large: 'Большой', medium: 'Средний', small: 'Малый', tiny: 'Крошечный',
  'missle compartment': 'Ракетный отсек', 'nether ward': 'Тотем Пустоты',
  'plague wards': 'Чумные тотемы', 'power cogs': 'Силовые шестерни', propeller: 'Пропеллер',
  'proximity mine': 'Мина ближнего действия', 'serpent wards': 'Змеиные тотемы',
  sign: 'Знак', spleen: 'Селезёнка', spoon: 'Ложка', squee: 'Скви',
  antennae: 'Усики', 'death ward': 'Тотем смерти', golem: 'Голем', lantern: 'Фонарь',
  'rocket flare': 'Ракетная вспышка', shapeshift: 'Оборот', spiderling: 'Паучки',
  'spirit bear': 'Дух-медведь', spirits: 'Духи', totem: 'Тотем',
  'stone remnant': 'Каменный остаток', 'stringer limbs': 'Конечности Прядильщика',
  supernova: 'Сверхновая', tombstone: 'Надгробие', treants: 'Древни',
  'true form': 'Истинная форма', tusks: 'Клыки', wolves: 'Волки',
  shuolder: 'Плечи', 'hero base': 'База', 'ability effects': 'Эффекты способностей',
};

function sectionTitle(name) {
  const base = String(name).replace(/\s*\((persona|kid)\)\s*$/i, '').trim();
  return SECTION_RU[String(name).toLowerCase()] || SECTION_RU[base.toLowerCase()] || name;
}

const SECTION_SLOT = {
  'fullsets': 'sets', 'fullset': 'sets',
  'ability effects': 'ability',
  'off-hand': 'off_hand', 'offhand': 'off_hand', 'offhand (persona)': 'off_hand',
  'shuolder': 'shoulder', 'shoulders': 'shoulder', 'shoulder (persona)': 'shoulder',
  'hero base': 'body',
};

function sectionSlot(name) {
  const low = name.toLowerCase();
  const mapped = SECTION_SLOT[low];
  if (mapped === 'sets' || mapped === 'ability') return mapped;
  if (mapped) return mapped.startsWith('slot:') ? mapped : `slot:${mapped}`;
  let base = low.replace(/\s*\((persona|kid)\)\s*$/, '').trim();
  base = SECTION_SLOT[base] || base.replace(/[^a-z0-9]+/g, '_');
  return base.startsWith('slot:') ? base : `slot:${base}`;
}

const SLOT_ALIASES = {
  off_hand: 'offhand_weapon', offhand: 'offhand_weapon', shield: 'offhand_weapon',
  barrel: 'offhand_weapon', flask: 'offhand_weapon', lantern: 'offhand_weapon',
  quiver: 'offhand_weapon', wings: 'back', gloves: 'arms', fist: 'arms',
  heads: 'head', beard: 'head', antennae: 'head', gunt: 'weapon', claws: 'weapon',
  totem: 'weapon', bazooka: 'weapon', tail: 'legs', mount: 'legs',
};

function canonicalSlot(value) {
  const slot = String(value || '').replace(/_persona_\d+$/i, '');
  return SLOT_ALIASES[slot] || slot;
}

function shelfSlot(shelf) {
  const counts = {};
  for (const item of shelf.items || []) {
    const slot = canonicalSlot(item.slot);
    if (slot && slot !== 'set') counts[slot] = (counts[slot] || 0) + 1;
  }
  return Object.entries(counts).sort((a, b) => b[1] - a[1])[0]?.[0] || '';
}

const ABILITY_GROUPS = {
  offhand_weapon: ['offhand_weapon', 'weapon', 'arms'],
  weapon: ['weapon', 'arms', 'offhand_weapon'],
  head: ['head', 'body', 'neck'],
  arms: ['arms', 'shoulder', 'offhand_weapon'],
  shoulder: ['shoulder', 'arms', 'back'],
  back: ['back', 'body'],
  body: ['body', 'back', 'armor'],
  legs: ['legs', 'belt', 'body'],
  belt: ['belt', 'legs'],
  neck: ['neck', 'head'],
};

function abilityShelfFor(shelves, slot, card) {
  const exact = shelves.find((shelf) => shelf.id === `slot:${slot}`);
  if (exact) return exact;
  const same = shelves.find((shelf) => shelfSlot(shelf) === canonicalSlot(slot));
  if (same) return same;
  const group = ABILITY_GROUPS[canonicalSlot(slot)] || [slot];
  const related = shelves.find((shelf) => group.includes(shelfSlot(shelf)));
  if (related) return related;
  const words = String(card.name || '').toLowerCase().split(/[^a-z]+/).filter((w) => w.length > 4);
  const named = shelves.find((shelf) => words.some((w) => String(shelf.title || '').toLowerCase().includes(w)));
  if (named) return named;
  return shelves.filter((shelf) => shelf.id !== 'sets').sort((a, b) => (b.items || []).length - (a.items || []).length)[0] || null;
}

function applyCollectionSections(data) {
  const persona = activePersona(data);
  const picks = Object.values(picksFor(data.folder));
  const baseEntry = (state.collection || []).find((e) => (e.hero === data.folder || e.slug === data.folder) && !e.personaId);
  const pickedPersona = (state.collection || []).find((e) =>
    (e.hero === data.folder || e.slug === data.folder) && e.personaId && picks.includes(Number(e.personaId)));
  const personaEntry = pickedPersona || (persona
    ? (state.collection || []).find((e) => (e.hero === data.folder || e.slug === data.folder) && Number(e.personaId) === Number(persona.itemId))
    : null);
  // Личность всегда важнее базового героя. Если у неё ещё нет собственных
  // папок, ниже покажем только карточку её сета, но не обычные сеты героя.
  const entry = personaEntry || baseEntry;
  const src = entry && state.sections[entry.name];
  const serverSections = Array.isArray(data.folderSections) ? data.folderSections : null;
  if (!serverSections && !src) {
    if (personaEntry) {
      data.customShelves = [{
        id: 'sets', title: 'Сеты', kind: 'sets',
        items: [{ id: personaEntry.personaId, name: personaEntry.name, slot: 'set', rarity: '', art: personaEntry.img, image: '' }],
      }];
    }
    return;
  }

  const all = new Map();
  for (const shelf of data.shelves || []) for (const item of shelf.items || []) all.set(String(item.id), item);
  for (const slot of data.slots || []) for (const item of slot.items || []) all.set(String(item.id), item);

  const shelves = [];
  const sourceSections = serverSections
    ? serverSections.map((section) => [section.name, section])
    : Object.entries(src)
      .sort(([a], [b]) => Number(a.toLowerCase() === 'ability effects') - Number(b.toLowerCase() === 'ability effects'));
  for (const [name, info] of sourceSections) {
    const cards = (info.cards || []).map((card) => {
      const found = all.get(String(card.id));
      return found
        ? { ...found, slot: card.slot || found.slot, art: card.art, ability: card.ability || '', skill: card.skill || '' }
        : { id: card.id, name: card.name, slot: card.slot, rarity: card.rarity, image: '', art: card.art, ability: card.ability || '', skill: card.skill || '' };
    });
    if (!cards.length && !serverSections) continue;
    const id = sectionSlot(name);
    const same = shelves.find((shelf) => shelf.id === id);
    if (same) {
      const seen = new Set(same.items.map((item) => String(item.id)));
      for (const card of cards) if (!seen.has(String(card.id))) same.items.push(card);
      continue;
    }
    shelves.push({
      id,
      title: id === 'sets' ? 'Сеты'
        : id === 'ability' ? 'Эффекты способностей'
        : id.startsWith('slot:') ? slotLabel(data, id.slice(5))
        : sectionTitle(name),
      kind: id === 'sets' ? 'sets' : 'slot',
      items: cards,
    });
  }
  const standard = new Set(['weapon', 'offhand_weapon', 'head', 'shoulder', 'arms', 'armor',
    'belt', 'back', 'legs', 'neck', 'body', 'tail', 'wings']);
  for (const shelf of shelves) {
    if (!shelf.id.startsWith('slot:') || shelf.id === 'sets') continue;
    const folder = canonicalSlot(shelf.id.slice(5));
    const actual = shelfSlot(shelf);
    // Папка «Armor» с одними плащами — это спина, а не доспех. Показываем
    // название по реальному содержимому, иначе игрок видит вещи не в том слоте.
    if (standard.has(folder) && actual && actual !== folder && (shelf.items || []).length >= 2) {
      const same = (shelf.items || []).filter((item) => canonicalSlot(item.slot) === actual).length;
      if (same >= Math.max(2, Math.ceil(shelf.items.length * 0.6))) {
        shelf.title = slotLabel(data, actual);
      }
    }
  }
  // Личность без собственных папок сетов всё равно показывает свою карточку,
  // но никогда не подставляет под неё обычные сеты базового героя.
  if (personaEntry && !shelves.some((shelf) => shelf.id === 'sets')) {
    shelves.push({
      id: 'sets', title: 'Сеты', kind: 'sets',
      items: [{ id: personaEntry.personaId, name: personaEntry.name, slot: 'set', rarity: '', art: personaEntry.img, image: '' }],
    });
  }
  if (shelves.length) {
    // sets first, then equipment
    const order = ['weapon','off_hand','head','back','shoulder','arms','armor','belt','mount','taunt','legs','tail','wings'];
    const rank = (shelf) => shelf.id === 'sets' ? 0 : shelf.id === 'ability' ? 99 : 1 + Math.max(0, order.indexOf(shelf.id.slice(5)));
    shelves.sort((a, b) => rank(a) - rank(b));
    data.customShelves = shelves;
  }
}


/* Канонический ключ слота: русские названия кропов и английские из реестра — одна вкладка. */
const SLOT_KEYS = [
  [/оружие|weapon(?!.*offhand)|primary/i, 'weapon'],
  [/вторая рука|offhand|secondary/i, 'offhand_weapon'],
  [/голова|head|helm|hat/i, 'head'],
  [/доспех|броня|armor|body|chest/i, 'armor'],
  [/руки|наручи|arms|bracers|gloves/i, 'arms'],
  [/плечи|наплеч|shoulder|pauldron/i, 'shoulder'],
  [/пояс|belt/i, 'belt'],
  [/спина|плащ|back|cape|wings/i, 'back'],
  [/насмешк|taunt/i, 'taunt'],
  [/питом|курьер|courier|pet/i, 'courier'],
  [/ноги|обувь|legs|boots/i, 'legs'],
  [/музык|music|mega/i, 'music'],
];
function canonicalSlot(shelf) {
  const probe = `${shelf?.id || ''} ${shelf?.title || ''} ${shelf?.key || ''}`.toLowerCase();
  if (/^sets$|сет/i.test(probe)) return 'sets';
  if (/arcane|аркан/i.test(probe)) return 'arcana';
  if (/persona|личност/i.test(probe)) return 'persona_selector';
  if (/extra|доп/i.test(probe)) return 'extras';
  for (const [re, key] of SLOT_KEYS) if (re.test(probe)) return key;
  return probe.trim();
}

/** Склеивает полки с одинаковым каноническим слотом: одна вкладка на слот, без дублей. */
function mergeShelvesBySlot(shelves) {
  const order = [];
  const byKey = new Map();
  for (const shelf of shelves) {
    const key = canonicalSlot(shelf);
    const seen = byKey.get(key);
    if (!seen) {
      byKey.set(key, { ...shelf, id: shelf.id, title: shelf.title, items: [...(shelf.items || [])] });
      order.push(key);
      continue;
    }
    const ids = new Set(seen.items.map((i) => i.id));
    for (const item of shelf.items || []) if (!ids.has(item.id)) { seen.items.push(item); ids.add(item.id); }
  }
  return order.map((key) => byKey.get(key)).filter((shelf) => shelf.items.length > 0 || shelf.id === 'sets');
}

function shelfPlan(data) {
  if (data.customShelves) {
    const merged = mergeShelvesBySlot(data.customShelves);
    return { shelves: merged.some((shelf) => shelf.items.length > 0) ? merged : data.customShelves,
      persona: activePersona(data), arcana: null };
  }
  const picks = picksFor(data.folder);
  const persona = activePersona(data);
  const arcana = persona ? null : activeArcana(data);
  let shelves = data.shelves.map((shelf) => ({ ...shelf, items: [...shelf.items] }));

  if (persona) {
    // На странице личности — только её предметы и её сеты. Обычные скины героя не мешаем.
    shelves = shelves.map((shelf) => ({
      ...shelf,
      items: shelf.items.filter((item) => {
        if (shelf.id === 'sets') {
          return Boolean(item.bundle?.length) && item.bundle.some((p) => isPersonaSlot(p.slot));
        }
        return isPersonaItem(item) || isPersonaSlot(item.slot);
      }),
    }));
    shelves = shelves.filter((shelf) => shelf.id === 'sets' || shelf.items.length > 0);
  } else {
    // На странице обычного героя личности живут в отдельной карточке справа,
    // поэтому в полках остаются только обычные арканы, сеты и слоты.
    shelves = shelves.map((shelf) => ({
      ...shelf,
      items: shelf.items.filter((item) => {
        if (shelf.id === 'sets') {
          return !(item.bundle || []).some((p) => isPersonaSlot(p.slot)) && !isPersonaItem(item);
        }
        return !isPersonaItem(item);
      }),
    }));
    shelves = shelves.filter((shelf) => shelf.items.length > 0);
  }

  const merged = mergeShelvesBySlot(shelves);
  if (merged.some((shelf) => shelf.items.length > 0)) shelves = merged;
  if (arcana && state.extraOnly) {
    const open = new Set(openSlotsFor(data));
    const baseIds = basePartIds(arcana);
    shelves = shelves.map((shelf) => {
      if (shelf.id === 'sets' || shelf.id === 'arcana') {
        return { ...shelf, items: shelf.items.filter((item) => item.id === arcana.id) };
      }
      return {
        ...shelf,
        items: shelf.items.filter((item) => !Array.isArray(item.bundle) && open.has(item.slot) && !baseIds.has(item.id)),
      };
    });
    const extras = { id: 'extras', title: 'Доп. слоты к аркане', kind: 'extras', items: extraItemsFor(data) };
    shelves = [extras, ...shelves.filter((shelf) => shelf.items.length > 0)];
    if (!shelves.some((shelf) => shelf.id === state.shelf) || state.shelf === 'sets') state.shelf = 'extras';
  } else {
    shelves = shelves.filter((shelf) => shelf.id === 'sets' || shelf.items.length > 0);
  }

  // Ни одной серой дыры: у сета должен быть хоть один настоящий арт.
  // На странице личности её наборы и вещи не называем «арканами».
  shelves = shelves.map((shelf) => ({
    ...shelf,
    title: shelf.id.startsWith('slot:')
      ? slotLabel(data, shelf.id.slice(5))
      : shelf.id === 'sets' && persona
        ? 'Сеты личности'
        : shelf.id === 'arcana'
          ? (persona ? 'Предметы личности' : 'Арканы')
          : shelf.title,
    items: shelf.items.filter((item) => !Array.isArray(item.bundle) || artCandidates(item).length > 0),
  }));
  return { shelves, persona, arcana };
}

document.addEventListener('icdk:pick', (event) => {
  const folder = state.hero?.folder;
  if (folder) window.Podium?.sync?.(folder, picksFor(folder), stylesFor(folder));
});

function renderShelves(data) {
  const picks = picksFor(data.folder);
  const container = $('#slots');
  const plan = shelfPlan(data);
  const shelves = plan.shelves;

  if (!shelves.length || !shelves.some((shelf) => shelf.items.length > 0 || shelf.id === 'extras')) {
    container.innerHTML = '<div class="empty">У этого героя нет подходящих предметов. Сними базу или выбери другого героя.</div>';
    renderHeroSlots(data);
    return;
  }

  const current = shelves.find((s) => s.id === state.shelf) || shelves[0];
  state.shelf = current.id;
  // Одна вкладка на раздел, без дублей: ★ Аркана, Сеты, доп. слоты, Immortal, затем слоты.
  const tabRank = (shelf) => {
    const key = canonicalSlot(shelf);
    return key === 'arcana' ? 0 : key === 'sets' ? 1 : key === 'extras' ? 2 : key === 'immortal' ? 3 : 4;
  };
  const pills = shelves
    .filter((shelf) => canonicalSlot(shelf) !== 'persona_selector')
    .sort((a, b) => tabRank(a) - tabRank(b));
  const tabs = pills.map((shelf) => {
    const active = shelf.id === current.id;
    return `<button class="shelf-tab ${active ? 'active' : ''}" data-shelf="${esc(shelf.id)}">
      ${esc(tabTitle(shelf))}<span>${shelf.items.length}</span></button>`;
  }).join('');

  const cards = current.items.map((item) => {
    const isBundle = Array.isArray(item.bundle) && item.bundle.length > 0;
    const isBase = Array.isArray(item.parts) && item.parts.length > 0;
    const selected = isBundle
      ? item.bundle.every((p) => picks[p.slot] === p.itemId)
      : picks[item.slot] === item.id;
    const image = cardImage(data, item);
    const badge = item.rarity ? `<span class="rarity ${rarityClass(item.rarity)}">${esc(rarityName(item.rarity))}</span>` : '';
    const styleCount = Number(item.styles || 1);
    const styleSlot = isBundle ? (item.bundle.find((p) => p.slot === 'persona_selector')?.slot || item.bundle[0]?.slot || 'body') : item.slot;
    const activeStyle = stylesFor(data.folder)[styleSlot] || 0;
    const styleButtons = styleCount > 1
      ? `<div class="style-switch" data-style-slot="${esc(styleSlot)}">${Array.from({ length: Math.min(styleCount, 4) }, (_, i) =>
          `<span class="style-dot ${activeStyle === i ? 'on' : ''}" data-style="${i}" title="Вариант ${i + 1}">${['I', 'II', 'III', 'IV'][i]}</span>`).join('')}</div>`
      : '';
    return html`
      <button class="item ${selected ? 'sel' : ''} card ${isBundle ? 'set-card' : ''} ${isBase ? 'base-card' : ''} ${item.skill ? 'has-skill' : ''}"
              data-shelf-id="${esc(current.id)}" data-item="${item.id}" title="${esc(item.skill ? item.ability + ' — заменяемая способность' : item.name)}">
        <div class="card-art">
          <img loading="lazy" src="${image}" alt="${esc(item.name)}" onerror="this.onerror=null;this.src='${heroCrop(data.folder)}'" />
          ${badge}
          ${isBase ? '<span class="base-flag">БАЗА</span>' : ''}
          ${item.skill ? `<span class="skill-badge" title="${esc(item.ability)}"><img loading="lazy" src="${esc(item.skill)}" alt="${esc(item.ability)}" /><i>${esc(item.ability)}</i></span>` : ''}
          ${selected ? '<span class="equipped">НАДЕТО</span>' : ''}
        </div>
        <div class="card-name"><b>${esc(item.name)}</b>${item.ability ? `<span class="ability-tag">${esc(item.ability)}</span>` : ''}</div>
        ${styleButtons}
      </button>`;
  }).join('');

  const toggle = plan.arcana && !plan.persona
    ? `<button class="extra-filter ${state.extraOnly ? 'on' : ''}" id="extra-toggle" title="Спрятать несовместимые сеты и оставить только вещи в свободные слоты">
         ${state.extraOnly ? 'Только доп. слоты' : 'Все предметы'}</button>`
    : '';
  const onTaunt = current.id === 'slot:taunt' || shelfSlot(current) === 'taunt';
  const tauntToggle = onTaunt && picks.taunt
    ? `<button class="taunt-rare ${rareTauntFor(data.folder) ? 'on' : ''}" id="taunt-rare" title="Насмешка проигрывается и в витрине героя, и в редком простое">
         <i>${rareTauntFor(data.folder) ? '✓' : ''}</i> Показывать в витрине и в редком простое</button>`
    : '';

  container.innerHTML = `
    <div class="shelf-tabs">${tabs}</div>
    <section class="shelf">
      <div class="slot-head">
        <div><h2>${esc(current.title)}</h2><p>${plan.persona ? 'только предметы этой Личности' : plan.arcana ? 'свободные слоты подсвечены на кукле' : 'выбери базу или отдельный слот'}</p></div>
        <div class="spacer"></div>
        ${toggle}${tauntToggle}
      </div>
      <div class="items-row ${current.id === 'arcana' ? 'arcana-row' : ''}">${cards || '<div class="empty">Нет предметов для свободных слотов — сними часть арканы или нажми «Все предметы».</div>'}</div>
    </section>`;

  const tauntSwitch = $('#taunt-rare');
  if (tauntSwitch) {
    tauntSwitch.onclick = async () => {
      state.loadout.rareTaunt ??= {};
      const on = !rareTauntFor(data.folder);
      if (on) state.loadout.rareTaunt[data.folder] = true; else delete state.loadout.rareTaunt[data.folder];
      await savePicksNow(data.folder);
      renderShelves(data);
    };
  }

  const extraToggle = $('#extra-toggle');
  if (extraToggle) {
    extraToggle.onclick = () => {
      state.extraOnly = !state.extraOnly;
      if (!state.extraOnly && current.id === 'extras') state.shelf = 'sets';
      renderShelves(data);
    };
  }

  for (const tab of container.querySelectorAll('.shelf-tab')) {
    tab.addEventListener('click', () => {
      state.shelf = tab.dataset.shelf;
      renderShelves(data);
    });
  }

  const choose = (item, styleIndex) => {
    const map = picksFor(data.folder);
    const styles = stylesFor(data.folder);
    const bundle = Array.isArray(item.bundle) && item.bundle.length > 0;
    const persona = isPersonaItem(item);

    if (styleIndex != null) {
      const targets = Array.isArray(item.parts) && item.parts.length > 0
        ? item.parts.map((p) => p.slot)
        : bundle ? item.bundle.map((p) => p.slot) : [item.slot];
      for (const slot of targets) {
        if (styleIndex > 0) styles[slot] = styleIndex;
        else delete styles[slot];
      }
      persistPicks(data.folder);
      renderShelves(data);
      paintSelection(data);
      return;
    }

    if (Array.isArray(item.parts) && item.parts.length > 0) {
      const parts = item.parts;
      const ids = basePartIds(item);
      const wasSelected = map[item.slot] === item.id;
      const prev = activeArcana(data);
      if (prev && prev.id !== item.id) {
        for (const part of prev.parts || []) {
          if (map[part.slot] === part.itemId) {
            delete map[part.slot];
            delete styles[part.slot];
          }
        }
      }
      if (wasSelected) {
        for (const part of parts) {
          if (map[part.slot] === part.itemId) {
            delete map[part.slot];
            delete styles[part.slot];
          }
        }
        delete map[item.slot];
      } else {
        map[item.slot] = item.id;
        for (const part of parts) {
          if (part.slot === item.slot) continue;
          const cur = map[part.slot];
          if (cur == null || ids.has(cur)) map[part.slot] = part.itemId;
        }
        state.extraOnly = true;
        state.shelf = 'extras';
      }
    } else if (bundle) {
      const already = item.bundle.every((p) => map[p.slot] === p.itemId);
      for (const pick of item.bundle) {
        if (already) {
          delete map[pick.slot];
          delete styles[pick.slot];
        } else {
          map[pick.slot] = pick.itemId;
        }
      }
    } else if (map[item.slot] === item.id) {
      delete map[item.slot];
      delete styles[item.slot];
    } else {
      map[item.slot] = item.id;
      if (persona) {
        state.extraOnly = false;
        state.shelf = 'sets';
      }
    }


    // Любое действие на странице Личности снова ставит её базовый item_id.
    if (data.isPersona && data.personaId) map.persona_selector = Number(data.personaId);
    persistPicks(data.folder);
    renderShelves(data);
    renderHeroSlots(data);
    paintSelection(data);
  };

  for (const button of container.querySelectorAll('.item')) {
    button.addEventListener('click', (event) => {
      const item = current.items.find((i) => i.id === Number(button.dataset.item));
      if (!item) return;
      const styleButton = event.target.closest('[data-style]');
      choose(item, styleButton ? Number(styleButton.dataset.style) : null);
    });
  }

  renderHeroSlots(data);
}

function slotTitle(data, slot) {
  const found = data.slots.find((s) => s.slot === slot);
  return found?.label || slot;
}

function renderFooter(data, folder) {
  const install = $('#install');
  if (install) {
    const totalHeroes = Object.values(state.loadout.heroes).filter((p) => Object.keys(p).length > 0).length;
    install.textContent = totalHeroes > 1 ? `Собрать мод · ${totalHeroes}` : 'Собрать мод';
    install.onclick = compileAll;
  }

  const clear = $('#clear');
  if (clear) {
    clear.onclick = async () => {
      state.loadout.heroes[folder] = {};
      state.loadout.styles[folder] = {};
      await api.post('/api/loadout', { folder, picks: [] });
      await loadHeroes();
      renderShelves(data);
      renderHeroSlots(data);
      paintSelection(data);
      renderStrip();
      toast('образ снят', '', 2200);
    };
  }
}

async function compileAll() {
  if (state.busy) return;
  state.busy = true;
  document.body.classList.add('building');
  document.getElementById('brand-reactor')?.classList.remove('built');
  const buttons = ['#btn-compile', '#install'].map((sel) => $(sel)).filter(Boolean);
  const labels = buttons.map((b) => b.textContent);
  for (const b of buttons) {
    b.disabled = true;
    b.textContent = 'компилирую…';
  }

  try {
    await savePicksNow(state.hero?.folder);
    const result = await api.post('/api/compile', {});
    toast(result.message, result.success ? 'ok' : 'err', 7000);
    if (result.success) {
      // сборка НИКОГДА не меняет экран: игрок остаётся там, где был
      await refreshSession();
      await loadHeroes();
    }
  } catch (err) {
    toast('ошибка компиляции: ' + err.message, 'err', 6000);
  } finally {
    state.busy = false;
    document.body.classList.remove('building');
    const reactor = document.getElementById('brand-reactor');
    if (reactor) { reactor.classList.add('built'); setTimeout(() => reactor.classList.remove('built'), 1100); }
    buttons.forEach((b, i) => {
      b.disabled = false;
      b.textContent = labels[i];
    });
  }
}

async function decompileAll() {
  if (state.busy) return;
  if (!confirm('Удалить все паки мода из языковой папки и очистить примерку? Игра вернётся к стоковому состоянию.')) return;
  state.busy = true;
  try {
    const result = await api.post('/api/decompile', {});
    toast(result.message, result.success ? 'ok' : 'err', 7000);
    await loadLoadout();
    await refreshSession();
    await loadHeroes();
    route();
    renderStrip();
  } catch (err) {
    toast('ошибка: ' + err.message, 'err', 6000);
  } finally {
    state.busy = false;
  }
}

/* ── сессия ─────────────────────────────────────────────── */

async function refreshSession() {
  try {
    state.session = await api.get('/api/session');
  } catch (err) {
    state.session = null;
  }

  const chip = $('#chip-session');
  const dot = $('#dot-session');
  if (state.session?.active) {
    chip.textContent = `мод собран · ${(state.session.vpk / 1048576).toFixed(1)} МБ`;
    chip.title = `пак: ${state.session.pak} · папка: ${state.session.mountFolder} · героев: ${state.session.loadoutHeroes || 0}`;
    dot.className = 'dot ok';
  } else {
    chip.textContent = 'мод не собран';
    chip.title = '';
    dot.className = 'dot';
  }

  const launch = $('#btn-launch');
  launch.disabled = !state.session?.active;
  await refreshMount(false);
}

async function refreshMount(notify) {
  try {
    const mount = await api.get('/api/mount');
    state.mount = mount;
    const chip = $('#chip-session');
    if (mount.ok) {
      chip.title = `${mount.message} · ${mount.logPath}`;
      if (notify) toast(mount.message, 'ok', 7000);
    } else if (notify) {
      toast(mount.message, 'err', 8000);
    }
  } catch (err) {
    state.mount = null;
  }
}


/* ── мир: ландшафт, башни, эмблема ─────────────────────── */

async function loadWorld() {
  try {
    state.world = await api.get('/api/world');
  } catch (err) {
    state.world = { radiantTowers: [], direTowers: [], terrains: [], emblems: [], radiantCreeps: [], direCreeps: [], selected: {} };
  }
  state.world.selected ??= {};
}

const WORLD_KINDS = {
  terrain: { list: 'terrains', title: 'Ландшафт', note: 'карта в лобби и матче' },
  radiant_tower: { list: 'radiantTowers', title: 'Башни Света', note: 'все башни Radiant' },
  dire_tower: { list: 'direTowers', title: 'Башни Тьмы', note: 'все башни Dire' },
  radiant_creep: { list: 'radiantCreeps', title: 'Крипы Света', note: 'линия Radiant: ближние, дальние, осадные' },
  dire_creep: { list: 'direCreeps', title: 'Крипы Тьмы', note: 'линия Dire: ближние, дальние, осадные' },
  emblem: { list: 'emblems', title: 'Эмблема', note: 'кольцо выбора героя' },
};

function worldImage(item) {
  return item?.image ? `/api/img?inv=${encodeURIComponent(item.image)}` : FALLBACK;
}

function worldCard(kind, item) {
  const current = state.world.selected?.[kind] || '';
  const id = String(item?.id ?? '');
  const on = current === id;
  return html`
    <button class="world-card ${on ? 'on' : ''}" data-kind="${esc(kind)}" data-id="${esc(id)}">
      <span class="world-thumb">
        <img loading="lazy" src="${worldImage(item)}" alt="${esc(item?.name || '')}" onerror="this.src='${FALLBACK}'" />
        ${on ? '<span class="world-check">✓</span>' : ''}
      </span>
      <span class="world-cap"><b>${esc(item?.name || id)}</b><em>${esc(item?.note || item?.rarity || '')}</em></span>
    </button>`;
}

function worldResetCard(kind, label = 'Как в игре') {
  const on = !state.world.selected?.[kind] || state.world.selected[kind] === '0';
  return html`
    <button class="world-card reset ${on ? 'on' : ''}" data-kind="${esc(kind)}" data-id="">
      <span class="world-thumb stock">
        <span class="stock-badge"><b>ICDK</b><i>${esc(label === 'Авто' ? 'авто' : 'сток')}</i></span>
        ${on ? '<span class="world-check">✓</span>' : ''}
      </span>
      <span class="world-cap"><b>${esc(label)}</b><em>ICDK</em></span>
    </button>`;
}

function renderWorld() {
  const screen = $('#screen');
  const w = state.world;
  const sections = Object.entries(WORLD_KINDS).map(([kind, def]) => {
    const items = w[def.list] || [];
    const current = w.selected?.[kind] || '';
    const chosen = items.find((i) => String(i.id) === current);
    return html`
      <section class="world-sec">
        <div class="world-sec-head">
          <h2>${def.title}</h2>
          <span>${def.note}</span>
          <div class="spacer"></div>
          <b class="world-current">${chosen ? esc(chosen.name) : 'как в игре'}</b>
        </div>
        <div class="world-grid">
          ${worldResetCard(kind, def.reset || 'Как в игре')}
          ${items.map((item) => worldCard(kind, item)).join('')}
        </div>
      </section>`;
  }).join('');

  screen.innerHTML = html`
    <div class="world-screen">
      <div class="world-head">
        <div>
          <h1>Мир вокруг героя</h1>
          <p>Ландшафт, башни обеих сторон и кольцо выбора. Держится в примерке и пресетах, собирается тем же pak06.</p>
        </div>
        <div class="world-count">${[w.terrains, w.radiantTowers, w.direTowers, w.radiantCreeps, w.direCreeps, w.emblems]
          .reduce((n, list) => n + (list?.length || 0), 0)} вариантов</div>
      </div>
      <div class="world-scroll">
        <p class="world-hint">Ландшафт подменяет карту игры целиком: он виден в лобби, в матче и на «Опробовать ландшафт». Режим «Опробовать героя» грузит свою маленькую карту и ландшафт не покажет — это не ошибка сборки.</p>
        ${sections}
      </div>
    </div>`;

  for (const card of screen.querySelectorAll('.world-card')) {
    card.addEventListener('click', () => pickWorld(card.dataset.kind, card.dataset.id || ''));
  }
  updateCompileLabel();
}

async function pickWorld(kind, id) {
  const selected = { ...state.world.selected };
  const same = (selected[kind] || '') === id;
  selected[kind] = same ? '' : id;
  try {
    const result = await api.post('/api/world', { selected });
    if (!result.success) {
      toast(result.message || 'не удалось сохранить', 'err');
      return;
    }
    state.world.selected = result.selected || selected;
    renderWorld();
    toast(same ? 'вернул сток' : 'запомнил выбор', 'ok', 2200);
  } catch (err) {
    toast('сервер не ответил: ' + err.message, 'err');
  }
}


/* ── окно лаунчера: шапка без системной рамки ──────────── */
/** Мост к окну ICDK: WebView2 кладёт chrome.webview, обычный браузер — нет. */
const shell = window.chrome?.webview;
function shellPost(action) {
  try {
    shell?.postMessage({ action });
  } catch (err) {
    // окна нет — кнопки просто ничего не делают
  }
}

function bootShell() {
  const bar = document.getElementById('winbar');
  if (!bar) return;
  if (!shell) return;                    // в браузере остаётся обычная страница
  bar.hidden = false;
  document.body.classList.add('shell');

  const drag = document.getElementById('winbar-drag');
  drag?.addEventListener('mousedown', (e) => {
    if (e.button !== 0) return;
    shellPost('drag');
  });
  drag?.addEventListener('dblclick', () => shellPost('toggle'));

  document.getElementById('win-min')?.addEventListener('click', () => shellPost('minimize'));
  document.getElementById('win-max')?.addEventListener('click', () => shellPost('toggle'));
  document.getElementById('win-close')?.addEventListener('click', () => shellPost('close'));

  const status = document.getElementById('winbar-status');
  const page = () => {
    if (!status) return;
    const hash = location.hash || '#/';
    status.textContent = hash.startsWith('#/world') ? 'мир вокруг героя' : hash.startsWith('#/hero/') ? 'примерка героя' : 'примерочная';
  };
  window.addEventListener('hashchange', page);
  page();

  // страница смены размера окна: край ресайзится из Windows, шапке нужен только пересчёт
  window.addEventListener('resize', () => document.body.classList.toggle('maxed', screen.height - window.outerHeight < 4));
}

/* ── мелочи ─────────────────────────────────────────────── */


let toastTimer = null;
function toast(message, kind = '', ms = 3800) {
  for (const id of ['#toasts', '#toast-root', '.toasts']) {
    const box = document.querySelector(id);
    if (box) box.style.pointerEvents = 'none';
  }
  const existing = document.querySelector('.toast');
  if (existing) existing.remove();
  const node = document.createElement('div');
  node.className = `toast ${kind}`;
  node.textContent = message;
  document.body.appendChild(node);
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => node.remove(), ms);
}

bootShell();
boot();
