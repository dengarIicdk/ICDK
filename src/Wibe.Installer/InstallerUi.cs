namespace Wibe.Installer;

/// <summary>
/// Разметка окна установщика: тёмное стекло и сталь, рубиновый реактор, карточки-секции.
/// Живёт строкой внутри установщика, чтобы UI не зависел от внешних файлов.
/// Каркас на flex: футер с кнопкой «Установить» прибит к низу и не обрезается ни на одном DPI.
/// </summary>
public static class InstallerUi
{
    public const string Html = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Установка ICDK</title>
<style>
  :root {
    --graphite: #121418;
    --steel: #1e222a;
    --steel-2: #262b34;
    --ruby: #dc2626;
    --ruby-2: #ef4444;
    --text: #f5f7fa;
    --dim: #98a1ad;
    --ok: #4ade80;
    --line: rgba(148, 163, 184, 0.16);
  }
  * { box-sizing: border-box; }
  html, body { height: 100%; margin: 0; }
  body {
    background: #121418;
    color: var(--text);
    font: 13.5px/1.45 "Segoe UI Variable Display", "Segoe UI", system-ui, sans-serif;
    overflow: hidden;
    user-select: none;
  }
  .shell {
    position: relative;
    height: 100vh;
    display: flex;
    flex-direction: column;
    background:
      radial-gradient(760px 300px at 18% -8%, rgba(220, 38, 38, 0.28), transparent 62%),
      radial-gradient(620px 240px at 92% -14%, rgba(148, 163, 184, 0.14), transparent 60%),
      linear-gradient(180deg, #15181d 0%, #101216 58%, #0d0f12 100%);
  }
  .shell::before {
    content: "";
    position: absolute; inset: 0;
    background-image:
      linear-gradient(rgba(255, 255, 255, 0.028) 1px, transparent 1px),
      linear-gradient(90deg, rgba(255, 255, 255, 0.028) 1px, transparent 1px);
    background-size: 46px 46px;
    mask-image: radial-gradient(680px 340px at 30% 0%, #000 0%, transparent 78%);
    pointer-events: none;
  }

  header {
    flex-shrink: 0;
    display: flex; align-items: center; gap: 14px;
    padding: 16px 20px 14px;
    cursor: default;
  }
  .reactor { width: 52px; height: 52px; flex: 0 0 auto; filter: drop-shadow(0 0 10px rgba(220, 38, 38, 0.35)); }
  .reactor .sf-skull { transform-origin: 48px 50px; animation: sf-float 5.4s ease-in-out infinite; }
  .reactor .sf-horns { transform-origin: 48px 30px; animation: sf-float 5.4s ease-in-out infinite; }
  .reactor .sf-eyes { animation: sf-eyes 2.8s ease-in-out infinite; }
  .reactor .sf-maw { animation: sf-maw 3.6s ease-in-out infinite; }
  .reactor .sf-rift { animation: sf-rift 2.2s ease-in-out infinite; }
  .reactor .sf-souls circle { fill: var(--ruby-2); opacity: 0.45; animation: sf-souls 4.2s ease-in-out infinite; }
  .reactor .sf-souls circle:nth-child(2) { animation-delay: 0.5s; }
  .reactor .sf-souls circle:nth-child(3) { animation-delay: 1.1s; }
  .reactor .sf-souls circle:nth-child(4) { animation-delay: 1.7s; }
  .reactor .sf-souls circle:nth-child(5) { animation-delay: 2.3s; }
  .reactor .sf-wrath { fill: none; stroke: var(--ruby-2); stroke-width: 1.4; opacity: 0; transform-origin: 48px 46px; animation: sf-wrath 4.8s ease-out infinite; }
  @keyframes sf-float { 0%, 100% { transform: translateY(0); } 50% { transform: translateY(-2.5px); } }
  @keyframes sf-eyes { 0%, 100% { opacity: 1; } 46% { opacity: 1; } 52% { opacity: 0.25; } 58% { opacity: 1; } }
  @keyframes sf-maw { 0%, 100% { opacity: 0.75; } 50% { opacity: 1; } }
  @keyframes sf-rift { 0%, 100% { opacity: 0.75; } 50% { opacity: 1; } }
  @keyframes sf-souls { 0%, 100% { opacity: 0.15; transform: translateY(1.5px); } 50% { opacity: 0.85; transform: translateY(-2px); } }
  @keyframes sf-wrath { 0% { opacity: 0; transform: scale(0.72); } 35% { opacity: 0.5; } 100% { opacity: 0; transform: scale(1.18); } }

  .titles { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
  .titles h1 { margin: 0; font-size: 17px; font-weight: 600; letter-spacing: -0.01em; }
  .titles p { margin: 0; color: var(--dim); font-size: 12px; }
  .spacer { flex: 1 1 auto; }
  .badge {
    padding: 4px 10px; border-radius: 999px; font-size: 11.5px; color: var(--dim);
    background: rgba(148, 163, 184, 0.1); border: 1px solid var(--line);
  }
  .win { display: flex; gap: 6px; margin-left: 4px; }
  .win button {
    width: 30px; height: 26px; border-radius: 8px; border: 1px solid transparent;
    background: rgba(255, 255, 255, 0.04); color: var(--dim); cursor: pointer; font-size: 13px;
  }
  .win button:hover { background: rgba(255, 255, 255, 0.09); color: #fff; }
  .win button.close:hover { background: var(--ruby); border-color: var(--ruby); color: #fff; }

  main {
    flex: 1 1 auto;
    min-height: 0;
    overflow-y: auto;
    padding: 2px 20px 12px;
    display: flex; flex-direction: column; gap: 10px;
  }
  main::-webkit-scrollbar { width: 8px; }
  main::-webkit-scrollbar-thumb { background: rgba(148, 163, 184, 0.25); border-radius: 8px; }

  .card {
    background: linear-gradient(180deg, rgba(30, 34, 42, 0.96), rgba(22, 25, 31, 0.96));
    border: 1px solid var(--line);
    border-radius: 12px;
    padding: 12px 14px 13px;
    box-shadow: 0 10px 26px rgba(0, 0, 0, 0.34), inset 0 1px 0 rgba(255, 255, 255, 0.04);
    flex: 0 0 auto;
  }
  .card h2 {
    margin: 0 0 9px; font-size: 11px; font-weight: 600; letter-spacing: 0.09em; text-transform: uppercase;
    color: var(--dim);
  }
  .row { display: flex; gap: 8px; align-items: center; }
  .field {
    flex: 1 1 auto; min-width: 0; height: 36px; border-radius: 9px;
    background: rgba(9, 11, 14, 0.78); border: 1px solid rgba(148, 163, 184, 0.22);
    color: var(--text); padding: 0 12px; font: inherit; outline: none;
    transition: border-color 0.16s ease, box-shadow 0.16s ease;
  }
  .field:focus { border-color: rgba(220, 38, 38, 0.65); box-shadow: 0 0 0 3px rgba(220, 38, 38, 0.16); }
  .btn {
    height: 36px; padding: 0 14px; border-radius: 9px; cursor: pointer; font: inherit;
    color: var(--text); background: rgba(148, 163, 184, 0.12);
    border: 1px solid rgba(148, 163, 184, 0.22); transition: background 0.16s ease, transform 0.12s ease;
    white-space: nowrap;
  }
  .btn:hover { background: rgba(148, 163, 184, 0.2); }
  .btn:active { transform: translateY(1px); }
  .hint { margin-top: 7px; font-size: 11.5px; color: var(--dim); display: flex; align-items: center; gap: 7px; }
  .hint b { color: #dbe3ec; font-weight: 600; }
  .dot { width: 7px; height: 7px; border-radius: 50%; background: var(--ok); box-shadow: 0 0 8px rgba(74, 222, 128, 0.75); flex: 0 0 auto; }
  .dot.warn { background: #facc15; box-shadow: 0 0 8px rgba(250, 204, 21, 0.6); }

  .disks { display: flex; flex-wrap: wrap; gap: 8px; }
  .disk {
    position: relative; overflow: hidden; cursor: pointer;
    padding: 8px 12px 9px; border-radius: 10px; min-width: 116px;
    background: rgba(9, 11, 14, 0.7); border: 1px solid rgba(148, 163, 184, 0.2);
    transition: border-color 0.16s ease, background 0.16s ease;
  }
  .disk:hover { border-color: rgba(220, 38, 38, 0.5); }
  .disk.on { border-color: var(--ruby); background: rgba(220, 38, 38, 0.16); box-shadow: 0 0 0 1px rgba(220, 38, 38, 0.35); }
  .disk .letter { font-size: 13px; font-weight: 600; }
  .disk .free { font-size: 10.5px; color: var(--dim); margin-top: 1px; }
  .disk .bar { margin-top: 6px; height: 3px; border-radius: 3px; background: rgba(148, 163, 184, 0.18); overflow: hidden; }
  .disk .bar i { display: block; height: 100%; background: linear-gradient(90deg, var(--ruby), var(--ruby-2)); }
  .disk .ready { margin-top: 5px; font-size: 10px; color: var(--ok); }

  footer {
    flex-shrink: 0;
    display: flex; align-items: center; gap: 14px;
    padding: 12px 20px 14px;
    border-top: 1px solid var(--line);
    background: linear-gradient(180deg, rgba(13, 15, 18, 0.2), rgba(9, 11, 14, 0.85));
  }
  .toggles { display: flex; flex-wrap: wrap; gap: 12px; flex: 1 1 auto; min-width: 0; }
  .toggle { display: inline-flex; align-items: center; gap: 8px; cursor: pointer; font-size: 12px; color: var(--dim); }
  .toggle input { display: none; }
  .toggle .track {
    width: 34px; height: 18px; border-radius: 999px; background: rgba(148, 163, 184, 0.22);
    border: 1px solid rgba(148, 163, 184, 0.25); position: relative; transition: background 0.18s ease, border-color 0.18s ease;
  }
  .toggle .track::after {
    content: ""; position: absolute; top: 2px; left: 2px; width: 12px; height: 12px; border-radius: 50%;
    background: #e2e8f0; transition: transform 0.18s ease, background 0.18s ease;
  }
  .toggle input:checked + .track { background: rgba(220, 38, 38, 0.85); border-color: var(--ruby); }
  .toggle input:checked + .track::after { transform: translateX(16px); background: #fff; }
  .toggle:hover { color: #e2e8f0; }

  .install {
    position: relative; overflow: hidden; flex: 0 0 auto;
    min-width: 210px; height: 44px; border-radius: 11px; border: 0; cursor: pointer;
    color: #fff; font: 600 14px "Segoe UI Semibold", "Segoe UI", system-ui, sans-serif;
    background: linear-gradient(135deg, var(--ruby) 0%, var(--ruby-2) 100%);
    box-shadow: 0 8px 22px rgba(220, 38, 38, 0.32), inset 0 1px 0 rgba(255, 255, 255, 0.18);
    transition: transform 0.12s ease, box-shadow 0.18s ease, filter 0.18s ease;
  }
  .install:hover { filter: brightness(1.06); box-shadow: 0 10px 26px rgba(220, 38, 38, 0.42); }
  .install:active { transform: translateY(1px); }
  .install .bar {
    position: absolute; inset: 0 auto 0 0; width: 0%;
    background: rgba(255, 255, 255, 0.22); transition: width 0.18s ease;
  }
  .install .label { position: relative; z-index: 1; }
  .install.busy { filter: saturate(1.1); }
  .foot-note { flex: 0 0 auto; font-size: 10.5px; color: rgba(152, 161, 173, 0.75); max-width: 40%; }
</style>
</head>
<body>
<div class="shell" id="shell">
  <header>
    <svg class="reactor sf" viewBox="0 0 96 96" aria-label="Shadow Fiend">
      <defs>
        <linearGradient id="sfHorn" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stop-color="#3b0a0a"/><stop offset="1" stop-color="#090a0d"/>
        </linearGradient>
        <radialGradient id="sfEye">
          <stop offset="0" stop-color="#fff3f3"/><stop offset="0.45" stop-color="#ef4444"/><stop offset="1" stop-color="#7f1d1d"/>
        </radialGradient>
      </defs>
      <g class="sf-horns">
        <path d="M20 34 C8 22 6 12 12 6 C14 18 24 24 32 30 Z" fill="url(#sfHorn)" stroke="#7f1d1d" stroke-width="0.8"/>
        <path d="M76 34 C88 22 90 12 84 6 C82 18 72 24 64 30 Z" fill="url(#sfHorn)" stroke="#7f1d1d" stroke-width="0.8"/>
      </g>
      <path class="sf-skull" d="M48 16 C33 16 24 27 24 41 C24 52 29 59 34 63 L34 70 C34 74 38 77 48 77 C58 77 62 74 62 70 L62 63 C67 59 72 52 72 41 C72 27 63 16 48 16 Z" fill="#0c0d11" stroke="#2b1113" stroke-width="1.2"/>
      <g class="sf-eyes">
        <path d="M34 42 L44 38 L45 44 L35 46 Z" fill="url(#sfEye)"/>
        <path d="M62 42 L52 38 L51 44 L61 46 Z" fill="url(#sfEye)"/>
      </g>
      <path class="sf-maw" d="M40 60 L48 66 L56 60 L50 62 L48 58 Z" fill="#ef4444" opacity="0.85"/>
      <path class="sf-rift" d="M48 47 L45 60 L48 72 L51 60 Z" fill="#dc2626" opacity="0.9"/>
      <g class="sf-souls">
        <circle cx="30" cy="80" r="2"/><circle cx="44" cy="86" r="1.6"/><circle cx="58" cy="82" r="2.2"/><circle cx="68" cy="88" r="1.4"/><circle cx="38" cy="92" r="1.2"/>
      </g>
      <circle class="sf-wrath" cx="48" cy="46" r="34"/>
    </svg>
    <div class="titles">
      <h1>Установка ICDK</h1>
      <p>Лаунчер косметики и примерочная Dota 2</p>
    </div>
    <div class="spacer"></div>
    <span class="badge" id="version">v1.4.2</span>
    <div class="win">
      <button type="button" id="min" title="Свернуть">—</button>
      <button type="button" id="close" class="close" title="Закрыть">✕</button>
    </div>
  </header>

  <main>
    <section class="card">
      <h2>Папка установки</h2>
      <div class="row">
        <input class="field" id="dir" spellcheck="false" />
        <button class="btn" type="button" id="browse">Обзор</button>
      </div>
      <div class="hint"><span class="dot" id="dir-dot"></span><span id="dir-free">считаю свободное место…</span></div>
    </section>

    <section class="card">
      <h2>Клиент Dota 2</h2>
      <div class="row">
        <input class="field" id="dota" spellcheck="false" placeholder="папка dota 2 beta" />
        <button class="btn" type="button" id="pick-dota">Изменить</button>
      </div>
      <div class="hint"><span class="dot" id="dota-dot"></span><span id="dota-state">ищу установленную Dota 2…</span></div>
    </section>

    <section class="card">
      <h2>Хранилище 3D-кэша</h2>
      <div class="disks" id="disks"></div>
      <div class="hint"><span id="cache-hint">кэш моделей и иконок, до 4 ГБ</span></div>
    </section>
  </main>

  <footer>
    <div class="toggles">
      <label class="toggle"><input type="checkbox" id="opt-desktop" checked><span class="track"></span>Ярлык на столе</label>
      <label class="toggle"><input type="checkbox" id="opt-start" checked><span class="track"></span>Меню Пуск</label>
      <label class="toggle"><input type="checkbox" id="opt-run" checked><span class="track"></span>Запустить после установки</label>
    </div>
    <button class="install" type="button" id="install">
      <span class="bar" id="install-bar"></span>
      <span class="label" id="install-label">Установить ICDK →</span>
    </button>
  </footer>
</div>

<script>
  const bridge = window.chrome && window.chrome.webview;
  const state = { dir: '', dota: '', cache: '', disks: [], busy: false };

  const send = (type, payload) => {
    if (!bridge) return;
    try {
      bridge.postMessage(Object.assign({ type }, payload || {}));
    } catch (err) {
      document.getElementById('cache-hint').textContent = 'мост недоступен: ' + err;
    }
  };

  window.onerror = (message) => {
    const hint = document.getElementById('cache-hint');
    if (hint) hint.textContent = 'ошибка интерфейса: ' + message;
  };

  const formatGb = (bytes) => (bytes / (1024 * 1024 * 1024)).toFixed(1).replace('.', ',') + ' ГБ';

  function renderDisks() {
    const host = document.getElementById('disks');
    host.innerHTML = '';
    for (const disk of state.disks) {
      const card = document.createElement('div');
      card.className = 'disk' + (disk.path === state.cache ? ' on' : '');
      card.dataset.path = disk.path;
      const used = disk.total > 0 ? Math.min(100, Math.round(((disk.total - disk.free) / disk.total) * 100)) : 0;
      card.innerHTML =
        '<div class="letter">' + disk.letter + '</div>' +
        '<div class="free">свободно ' + formatGb(disk.free) + '</div>' +
        '<div class="bar"><i style="width:' + used + '%"></i></div>' +
        (disk.ready > 0 ? '<div class="ready">✓ Готовый кэш ' + formatGb(disk.ready) + '</div>' : '');
      card.onclick = () => {
        state.cache = disk.path;
        renderDisks();
        document.getElementById('cache-hint').textContent = disk.ready > 0
          ? 'использую готовый набор на ' + disk.letter + ' — конвертация не нужна'
          : 'кэш соберётся при первом запуске ICDK на ' + disk.letter;
        send('cache', { path: disk.path });
      };
      host.appendChild(card);
    }
  }

  function applyInit(payload) {
    state.dir = payload.dir;
    state.dota = payload.dota || '';
    state.cache = payload.cache;
    state.disks = payload.disks || [];
    document.getElementById('version').textContent = 'v' + payload.version;
    document.getElementById('dir').value = payload.dir;
    document.getElementById('dota').value = state.dota;
    if (!state.disks.some((d) => d.path === state.cache) && state.disks.length) {
      state.cache = state.disks[0].path;
    }
    renderDisks();
    updateDirFree(payload.dirFree || '');
    document.getElementById('dota-state').textContent = state.dota
      ? 'Найдена: ' + state.dota
      : 'не найдена — укажите папку вручную';
    document.getElementById('dota-dot').className = 'dot' + (state.dota ? '' : ' warn');
    var ready = state.disks.find((d) => d.path === state.cache);
    if (ready && ready.ready > 0) {
      document.getElementById('cache-hint').textContent =
        'использую готовый набор на ' + ready.letter + ' — конвертация не нужна';
    }
  }

  function updateDirFree(text) {
    document.getElementById('dir-free').textContent = text || 'свободно: —';
    document.getElementById('dir-dot').className = 'dot' + (text ? '' : ' warn');
  }

  document.getElementById('dir').addEventListener('input', (event) => {
    state.dir = event.target.value;
    send('dir', { path: state.dir });
  });

  document.getElementById('browse').onclick = () => send('browse');
  document.getElementById('pick-dota').onclick = () => send('pickDota');
  document.getElementById('min').onclick = () => send('minimize');
  document.getElementById('close').onclick = () => send('close');

  document.getElementById('install').onclick = () => {
    if (state.busy) return;
    state.busy = true;
    const button = document.getElementById('install');
    button.classList.add('busy');
    send('install', {
      path: state.cache,
      dir: document.getElementById('dir').value,
      dota: document.getElementById('dota').value,
      cache: state.cache,
      desktop: document.getElementById('opt-desktop').checked,
      startMenu: document.getElementById('opt-start').checked,
      run: document.getElementById('opt-run').checked,
    });
  };

  window.icdk = {
    init: applyInit,
    dirFree: updateDirFree,
    progress: (value, text) => {
      const bar = document.getElementById('install-bar');
      const label = document.getElementById('install-label');
      bar.style.width = Math.round(value * 100) + '%';
      label.textContent = text + ' · ' + Math.round(value * 100) + '%';
    },
    done: () => {
      const label = document.getElementById('install-label');
      label.textContent = 'Готово ✓';
      document.getElementById('install-bar').style.width = '100%';
    },
    fail: (message) => {
      state.busy = false;
      const button = document.getElementById('install');
      button.classList.remove('busy');
      document.getElementById('install-bar').style.width = '0%';
      document.getElementById('install-label').textContent = 'Установить ICDK →';
      document.getElementById('cache-hint').textContent = message;
    },
    dragging: () => send('drag'),
  };

  // перетаскивание окна за шапку
  document.querySelector('header').addEventListener('pointerdown', (event) => {
    if (event.button !== 0 || event.target.closest('button')) return;
    send('drag');
  });
</script>
</body>
</html>
""";
}
