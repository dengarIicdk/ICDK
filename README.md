# ICDK — примерочная Dota 2

Локальный менеджер косметики Dota 2: арканы, личности, Immortals, сеты и мир. Мод собирается
в **отдельный пак в языковой папке игры** (`game/dota_danish/pak02_dir.vpk`), поэтому Source 2
монтирует его сам — **без инжекта в процесс, без правки подписанных файлов Valve, без оверлеев**.

```
game/dota/gameinfo.gi          не изменяется ни на байт (md5 eb67f7069f6e95e1ffa469ec060d3ff5)
game/bin/win64/dota.signatures не изменяется
dota_danish/pak01_*.vpk        официальная русская озвучка, хардлинки под родными именами
dota_danish/pak02_dir.vpk      наш мод + русская локализация интерфейса
dota_danish/maps/dota.vpk      ландшафт (если выбран)
```

Снятие мода — удаление одной папки `game/dota_danish`.

## Возможности

- **127 героев, 10 982 предмета** — каталог читается из пака игры и кэшируется по `buildid`.
- **Примерка на много героев** сразу: сеты, арканы, личности, отдельные слоты, стили, насмешки.
- **Один `pakNN_dir.vpk`** на всех выбранных героев — сборка через Rust-ядро (`mm-core`).
- **Иконки без интернета и без распаковки VPK**: готовый кэш `ICDK_AssetVault`
  (`temp/icons/icons_v10`, 12 871 PNG). Пак игры декодируется только если кэша нет.
- **3D-примерка (Beta)** на three.js: тело героя и предметы берутся из `<vault>/meshes` и
  `<vault>/temp/meshes`. По умолчанию включён быстрый 2D-режим.
- **Русский интерфейс при `-language danish`**: наши `*_danish.txt` перекрывают официальный
  датский перевод из `dota/pak01.vpk` (см. «Локализация»).
- **Свой тёмный установщик** с payload в хвосте exe, ярлыками, записью в «Программы и компоненты»
  и рабочим удалением.
- Пресеты, ландшафты мира, лендинг в `site/`.

## Как это работает

```
items_game.txt / модели из game/dota/pak01_dir.vpk
        │  Rust-ядро (mm-core) собирает ассеты выбранных героев
        ▼
game/dota_danish/pak02_dir.vpk
   models/… particles/… materials/… panorama/… soundevents/…
   resource/localization/*_danish.txt      ← русский интерфейс
        ▼
Dota 2 запускается через Steam с -language danish
   движок сам монтирует pak02 поверх pak01 → скины видны в меню и в матче
```

Почему `danish`: неизвестное значение `-language` не создаёт search path, а официальный
Steam-язык `danish` гарантированно существует. Русский текст и озвучка остаются, потому что
паки `dota_russian/pak01_*` линкуются в `dota_danish/` **под родными именами** — переименование
в `pak02_*` ломало внутренний индекс томов и давало
`FATAL ERROR: Error reading from loaded packed store "…pak02_135.vpk"`.

## Локализация

При `-language danish` движок берёт строки из `*_danish.txt` **внутри `.vpk`**; распакованные
`.txt` в `game/dota_danish/resource/` стоят ниже `.vpk` в таблице `[Filesystem]` и игнорируются.

`LanguageMount`:

1. читает из `game/dota/pak01_dir.vpk` и языковых паков все `*_russian.txt`
   (`resource/localization/**`, включая `dota_russian.txt`, `gameui_russian.txt`, `abilities_russian.txt`);
2. переименовывает их в `*_danish.txt`, меняет заголовок `"Language"` на `danish`, сохраняет
   кодировку и BOM;
3. снимает родовые варианты Valve — ключи `"npc_dota_hero_antimage:n"` и значения
   `"#|m|#Anti-Mage"`. Язык `danish` родов не знает, и без этого валидатор выбрасывал токены
   (`LOCFAIL … uses a gender not valid`);
4. вливает результат в `pak02_dir.vpk` (проверка чтением перед заменой) — при занятом паке
   кладёт локализацию отдельным `pak03_dir.vpk`.

Кэш: `%LOCALAPPDATA%\ICDK\LocalizationCache\v3-danish` (пересобирается при обновлении паков игры).

Проверено живым запуском: `0 FATAL`, `0 LOCFAIL`, главное меню — «ГЕРОИ · АРСЕНАЛ · ПРОСМОТР ·
БАЗА ЗНАНИЙ · ИГРОТЕКА · ИГРАТЬ».

## Кэш ICDK AssetVault

Готовый кэш ищется в одном месте (`Wibe.Core/Cache/AssetVaultLocator`):

1. переменная `WIBE_CACHE`;
2. `cachePath` из `%LOCALAPPDATA%\ICDK\UserData\settings.json`;
3. готовый `X:\ICDK_AssetVault` на любом диске (самый наполненный);
4. `%LOCALAPPDATA%\ICDK\AssetVault`.

| Папка | Внутри | Кто читает |
|---|---|---|
| `meshes/hero-*.glb` | тела героев + `_meshinfo.json` | 3D-примерка |
| `temp/meshes/<модель>__<слот>/` | готовые `.glb` предметов | 3D-примерка |
| `temp/icons/icons_v10/` | 12 871 PNG; имя = `image_inventory` с `/` → `_` | `/api/img` |
| `temp/meta`, `cache/` | метаданные и vpk-блоб конвейера | сам конвейер |

Кэш только читается; пишем мы лишь в `<vault>/meshes`.

## Установка

1. Скачать `ICDK_Setup_v<версия>.exe` (собран `publish.ps1`).
2. Запустить, проверить папку установки, папку Dota 2 и диск кэша — установщик сам подставляет
   найденный `X:\ICDK_AssetVault`.
3. «Установить ICDK →». Ярлыки на столе/в Пуске, запись в реестре, удаление — через
   «Программы и компоненты» (или `ICDK_Setup_v<версия>.exe --uninstall`).

Молчаливая установка:

```powershell
dist\ICDK_Setup_v1.4.2.exe --silent --dir "C:\ICDK" --cache "Z:\ICDK_AssetVault" --no-run
```

Требования: Windows 10/11 x64, Steam с Dota 2, WebView2 Runtime (без него установщик покажет
резервный экран, приложение всё равно работает).

## Запуск из исходников

```powershell
dist\ICDK.exe                          # окно-лаунчер + веб-интерфейс на 127.0.0.1:17890
dist\ICDK.exe --headless-server        # только сервер (для проверок)
dist\ICDK.exe --no-browser             # без авто-открытия браузера
```

## Сборка

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1
```

Скрипт:

1. собирает Rust-ядро `src/Wibe.RustBuilder` (`rustup run stable-x86_64-pc-windows-gnu cargo build --release`);
2. пакует `bin/` (Source2Viewer-CLI + нативные библиотеки) в `scratch/engine.zip` и вшивает в exe;
3. публикует `Wibe.Server` одним self-contained файлом → `dist\ICDK.exe`;
4. собирает установщик и приклеивает `ICDK.exe` в хвост **без сжатия**
   (int64 длина + magic `ICDKRAW1`) → `dist\ICDK_Setup_v<версия>.exe`.

Нужны: .NET 8 SDK, Rust (stable, `x86_64-pc-windows-gnu`), `bin\Source2Viewer-CLI.exe`.

## API

| Метод | Назначение |
|---|---|
| `GET /api/state` | готовность, buildid, языковая папка, слоты |
| `GET /api/heroes` | герои, личности, обложки |
| `GET /api/hero?folder=axe` | слоты, полки, предметы |
| `GET/POST /api/loadout` | примерка на много героев |
| `POST /api/compile` | собрать один pak из всех выбранных героев |
| `POST /api/decompile` | убрать все наши паки и примерку |
| `GET/POST /api/presets` | пресеты |
| `GET /api/img?inv=…` | иконка предмета (кэш AssetVault → пак → заглушка) |
| `GET /api/mesh/hero?folder=…`, `/api/mesh/item?itemId=…` | `.glb` для 3D |
| `POST /api/launch` | запуск Dota 2 через Steam |
| `GET /api/session` | активен ли мод (`dota_danish/pak02_dir.vpk`) |
| `GET /api/icons` | статус кэша иконок |

CLI: `wibe doctor`, `wibe econ --hero axe`, `wibe overlay --hero axe --pick weapon=13927`,
`wibe overlay --status|--revert|--launch --condebug`.

## Тесты

```powershell
dotnet test tests/Wibe.Tests/Wibe.Tests.csproj
```

137 тестов: VPK-writer/reader, мм-ядро и сборка паков, монтирование `dota_danish` (нативные
`pak01_*` + наш `pak02_dir.vpk`), локализация (родовые варианты, `_danish`), установщик
(payload raw, отсутствие lock-ошибки), кэш AssetVault, каталог, бренд.

Тяжёлые проверки каталога и неизменности заблокированных героев можно исключить:

```powershell
dotnet test --filter "FullyQualifiedName!~LockedHeroesImmunity&FullyQualifiedName!~AllHeroItemsCatalogLock"
```

## Структура репозитория

```
src/Wibe.Server        веб-сервер (HttpListener), окно-лаунчер, языковой контейнер, кэш
src/Wibe.Core          VPK, KeyValues, каталог косметики, сессии мода, сбока
src/Wibe.Installer     тёмный установщик (WebView2, payload в хвосте)
src/Wibe.Cli           CLI wibe
src/Wibe.RustBuilder   Rust-ядро (mm-core) — сборка ассетов в pak
tests/Wibe.Tests       xUnit
site/                  лендинг
installer/             лицензия и старые артефакты Inno Setup (не используются)
publish.ps1            сборка релиза
```

## Безопасность и ограничения

- `game/dota/gameinfo.gi`, `gameinfo_branchspecific.gi`, `game/dota_russian/gameinfo.gi` и
  `game/bin/win64/dota.signatures` не изменяются; `gameinfo` только чистит легаси-патчи старых версий.
- Никакого `pak64_dir.vpk`, инжекта и сторонних файлов в базовой папке игры.
- Контейнер `game/dota_danish/` полностью наш: официальные паки в нём — хардлинки, поэтому места
  почти не занимают, а удаление контейнера игру не портит.
- 3D-примерка помечена Beta: часть моделей может требовать пересборки кэша.

## Журнал изменений

### 1.4.2
- `dota_danish/pak01_*` — официальная озвучка под родными именами; наш мод — `pak02_dir.vpk`
  (исправлен `FATAL ERROR … pak02_135.vpk`).
- Русский интерфейс при `-language danish`: `*_danish.txt` внутри `pak02_dir.vpk`,
  `LOCFAIL = 0`.
- Установщик: payload читается с общим доступом, `.part` переименовывается после закрытия потока,
  повтор до 30 с, лог ошибок, версия 1.4.2, анимированный Shadow Fiend в шапке.
- Каталог лаунчера: личности — переключатель на странице героя, 2D по умолчанию, «3D Beta»,
  панель «Надето», вкладки разделов без дублей, иконки из AssetVault.
- Кэш: единый резолвер, защита от перезаписи базового пака игры, ретраи записи.

### 1.4.0
- 3D-подиум на three.js, единый бандл, `mesh/meta`, удаление Babylon.

### 1.3.x
- Свой установщик вместо Inno Setup, WebView2, payload в хвосте.
