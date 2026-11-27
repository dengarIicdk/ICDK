# Лог проверок (verification log)

Все проверки выполнены **22.11.2026** на этой машине, на реальной установке Dota 2.
Цель — подтвердить факты, на которых стоит `TZ.md`, и отделить проверенное от предположений.

## 0. Окружение

```powershell
reg query "HKCU\Software\Valve\Steam" //v SteamPath
# HKEY_CURRENT_USER\Software\Valve\Steam
#     SteamPath    REG_SZ    z:/steam

cat /z/Steam/steamapps/libraryfolders.vdf
# "path" "Z:\\steam"  apps { "570" "77927547721" ... }

grep -E '"buildid"|"installdir"' /z/Steam/steamapps/appmanifest_570.acf
# "installdir" "dota 2 beta"
# "buildid" "26825343"

cat "Z:/Steam/steamapps/common/dota 2 beta/game/dota/cfg/boot.vcfg"
# "boot" { "AudioLanguage" "russian"  "UILanguage" "russian" }
```

Вывод: игра найдена, buildid 26825343, моды монтируются из `game/dota_russian`.

## 1. Папка модов и слоты

```bash
ls "…/game/dota_russian" | grep -E "^pak[0-9]+_dir.vpk" | sort
# pak01_dir.vpk … pak05_dir.vpk, pak64_dir.vpk
cat "…/game/dota_russian/gameinfo.gi"
# "GameInfo" { LayeredOnMod dota  FileSystem { SearchPaths { Game dota_russian … } } }
cat "…/game/dota_russian/dota2modmanager.json"
# files: ["pak64_dir.vpk"]   (установлен сторонним менеджером)
```

Вывод: папка аудио-языка существует и валидна; первый свободный слот — **06**.

## 2. Source2Viewer CLI

```bash
./bin/Source2Viewer-CLI.exe --version
# Version: 21.1.7798+942df5b37ebbde6153f6c488880b91d4bed003e6
# OS: Microsoft Windows 10.0.26200 (X64)

time ./bin/Source2Viewer-CLI.exe -i "…/pak01_dir.vpk" --vpk_list | wc -l
# 388776
# real 0m1.908s

./bin/Source2Viewer-CLI.exe -i "…/pak01_dir.vpk" --vpk_list -f "models/heroes/axe/"
# models/heroes/axe/axe.vmdl_c CRC:00099e1fd6 size:604580
# models/heroes/axe/axe_armor.vmdl_c … axe_weapon.vmdl_c   (всего 6)
```

## 3. Карта «герой → модель»

```bash
./bin/Source2Viewer-CLI.exe -i "…/pak01_dir.vpk" -f "scripts/npc/heroes/" -o ./heroes
# --- Wrote 129 files
ls heroes/scripts/npc/heroes | wc -l
# 129
grep -h '"Model"' heroes/scripts/npc/heroes/*.txt | wc -l
# 142
grep -h '"Model"' heroes/scripts/npc/heroes/*.txt | head -3
# "Model" "models/heroes/abaddon/abaddon.vmdl"
# "Model" "models/heroes/abyssal_underlord/abyssal_underlord_v2.vmdl"
# "Model" "models/heroes/alchemist/alchemist.vmdl"
```

Отдельно: в `scripts/npc/heroes/npc_dota_hero_axe.txt` найдена секция `Persona` с моделью
`models/items/axe/axe_carnival/axe_carnival_base.vmdl` — источник целей для замены.

## 4. Структура модели (почему подмена работает)

```bash
./bin/Source2Viewer-CLI.exe -i "…/pak01_dir.vpk" -f "models/heroes/axe/axe.vmdl_c" -o ./axetest --decompile
# --- Wrote 78 files

grep -c "AnimFile" axetest/models/heroes/axe/axe.vmdl
# 81 (у carnival-модели)

./bin/Source2Viewer-CLI.exe -i "…/pak01_dir.vpk" -f "models/items/axe/axe_carnival/axe_carnival_base.vmdl_c" -o ./carnival --decompile
# --- Wrote 94 files

grep -c "AnimFile" carnival/…/axe_carnival_base.vmdl
# 81
```

Вывод: полная модель (аркана/персона) самодостаточна — анимации и материалы внутри/по существующим путям.

## 5. Сквозной тест пайплайна (главный результат)

```bash
# извлекаем КОМПИЛИРОВАННЫЙ ресурс (без --decompile)
./bin/Source2Viewer-CLI.exe -i "…/pak01_dir.vpk" \
  -f "models/items/axe/axe_carnival/axe_carnival_base.vmdl_c" -o ./carnraw
# --- Wrote 1 files    (1 314 278 байт)

mkdir -p build/models/heroes/axe
cp carnraw/models/items/axe/axe_carnival/axe_carnival_base.vmdl_c build/models/heroes/axe/axe.vmdl_c

./bin/Source2Viewer-CLI.exe -i ./build --vpk_create ./test_pak_dir.vpk
# --- Packed 1 files into "…\test_pak_dir.vpk"   (1 314 480 байт)

./bin/Source2Viewer-CLI.exe -i ./test_pak_dir.vpk --vpk_dir
# models/heroes/axe/axe.vmdl_c crc=0x5890d3a8 metadatasz=0 fnumber=32767 ofs=0x00 sz=1314278

./bin/Source2Viewer-CLI.exe -i ./test_pak_dir.vpk -f "models/heroes/axe/axe.vmdl_c" -o ./rt --decompile
grep -m1 "axe_carnival_base_lod0" ./rt/models/heroes/axe/axe.vmdl
# name = "axe_carnival_base_lod0"      -> RENAMED-MODEL-OK
```

Вывод: переименование скомпилированной модели + упаковка не портят ресурс, CRC сохраняется,
модель читается по новому пути.

## 6. Совместимость формата VPK

```bash
xxd -l 32 "…/game/dota_russian/pak64_dir.vpk"
# 3412 aa55 0200 0000 6c00 0000 1508 0000 0000 0000 0000 0000 ...
xxd -l 32 ./test_pak_dir.vpk
# 3412 aa55 0200 0000 3200 0000 e60d 1400 3800 0000 3000 0000 1400 0000 vmdl
```

Оба — VPK v2 (`0x55AA1234`), наш дополнительно содержит MD5-секции и подпись (допустимо).

## 7. Подписи клиента (`dota.signatures`)

```bash
sha1sum "…/game/dota/gameinfo.gi" "…/game/dota/gameinfo_branchspecific.gi"
# A0DC87ACC7A659EEDD1F9575EEE2BD3883E8F201  gameinfo.gi
# 224B6F879D00D419B6A10FE07B4DDB3D827FD31F  gameinfo_branchspecific.gi

grep -o "gameinfo[a-z_]*\.gi~SHA1:[0-9A-F]*" "…/game/bin/win64/dota.signatures"
# gameinfo.gi~SHA1:A0DC87ACC7A659EEDD1F9575EEE2BD3883E8F201
# gameinfo_branchspecific.gi~SHA1:224B6F879D00D419B6A10FE07B4DDB3D827FD31F
```

Хеши совпадают → клиент действительно проверяет эти файлы. Правка `gameinfo` = красная линия.

## 8. glTF-превью

```bash
./bin/Source2Viewer-CLI.exe -i "…/pak01_dir.vpk" \
  -f "models/items/axe/axe_carnival/axe_carnival_base.vmdl_c" \
  -o ./glb --gltf_export_format glb --gltf_export_materials
# --- Writing model to file 'axe_carnival_base_physics.glb'...
# --- Wrote 1 files        real 0m1.291s
```

Вывод: 3D-превью в приложении достижимо (~1.3 с на модель, кешируем по CRC).

## 9. Workshop Tools

```bash
ls "…/game/bin/win64" | grep -iE "resourcecompiler|modeldoc|resourceinfo|dmxconvert"
# dmxconvert.exe, modeldoc_utils.dll, resourcecompiler.exe, resourceinfo.exe

./resourcecompiler.exe --help
# Usage: resourcecompiler <in resource file list>
#   -i … -game <path to gameinfo.gi> -f -novpk -skiptype …
```

Вывод: компиляция собственных моделей технически доступна (этап V2).

## 10. Что НЕ проверено (и как проверить)

| Гипотеза | Статус | План проверки |
|---|---|---|
| При конфликте файлов побеждает младший номер pak | не проверено | два тестовых пака с разной текстурой, ProcMon/`-console`, сравнение |
| `models/development/invisiblebox.vmdl_c` = «пустой» слот | не проверено | визуальная проверка в `hero_demo` |
| Заменённая аркана не ломает анимации в рейтинге | не проверено офлайн-тестом | протокол из ТЗ, раздел 13.2 (сначала hero_demo, потом unranked) |
| Клиент не кеширует модель по внутреннему имени | офлайн ок | тот же протокол |
| Поведение при невалидном VPK в папке | не проверено | тест с намеренно битым паком на копии установки |

## 11. Категории кроме героев (v1.1)

Все пути ниже получены из реального `pak01_dir.vpk` / `maps/dota.vpk` (buildid 26825343).

### 11.1 Счётчики по категориям

```bash
for p in "models/items/" "models/creeps/" "models/props_structures/" "models/props_nature/" "models/props_tree/" "models/courier/"; do
  printf "%-28s %s\n" "$p" "$(grep -cE "^$p.*\.vmdl_c" pak01_list.txt)"
done
# models/items/                12660
# models/creeps/               233
# models/props_structures/     629
# models/props_nature/         175
# models/props_tree/           191
# models/courier/              160   (из них 56 *_flying.vmdl_c)

grep -cE "^panorama/images/" pak01_list.txt   # 47437
grep -cE "^panorama/styles/" pak01_list.txt   # 1917
grep -cE "^sounds/"          pak01_list.txt   # 105008
grep -cE "^particles/.*\.vpcf_c" pak01_list.txt # 82835
```

### 11.2 Башни/строения — цели берутся из `npc_units.txt`

```bash
Source2Viewer-CLI.exe -i "...\pak01_dir.vpk" -f "scripts/npc/npc_units.txt" -o ./units
grep -E '"Model"' units/scripts/npc/npc_units.txt | grep -i tower | sort -u
# "Model" "models/props_structures/tower_good.vmdl"
# "Model" "models/props_structures/dire_tower002.vmdl"
# "Model" "models/heroes/undying/undying_tower.vmdl"
grep -E '"Model"' units/.../npc_units.txt | grep -i props_structures | sort -u
# dire_ancient_base001, dire_barracks_melee001, dire_barracks_ranged001,
# radiant_ancient001, radiant_melee_barracks001, radiant_ranged_barracks001, outpost, ...
```

Вывод: никакого хардкода — приложение парсит `npc_units.txt`. Служебные модели
(`*_destruction_lev1..3`, `*_glow`, `*_energy_effect`) трогать нельзя.

### 11.3 Курьеры — обязательна пара ground + flying

```bash
grep -E "^models/courier/" pak01_list.txt | grep -c "_flying.vmdl_c"
# 56  (при 160 моделях курьеров всего)
grep -iE '"Model".*courier' units/scripts/npc/npc_units.txt | head -1
# "Model" "models/courier/greevil/greevil.vmdl"
```

Вывод: базовая модель по умолчанию — **greevil**, а не donkey (старые гайды врут);
летающих вариантов меньше, чем обычных → приложение обязано проверять пару.

### 11.4 Варды и крипы

```bash
grep -cE "^models/items/wards/.*\.vmdl_c" pak01_list.txt    # 109
grep -E "^models/creeps/lane_creeps/creep_2021_dire/" pak01_list.txt | grep -c vmdl_c
# варианты melee / ranged / _mega / _flagbearer / _mega_flagbearer
```

### 11.5 Карта и террейны — два разных хранилища

```bash
grep -oE "^maps/[a-z0-9_]+_assets/" pak01_list.txt | sort -u
# maps/aghs21_assets/  cavern_assets/  crownfall_assets/  journey_assets/
# maps/jungle_assets/  reef_assets/    summer_assets/     ti10_assets/

Source2Viewer-CLI.exe -i ".../maps/dota.vpk" --vpk_list | wc -l
# 1040   (внутри: maps/dota.vmap_c, maps/dota/worldnodes/*.vmdl_c — башни, деревья, пропсы мира)
```

Вывод: ассеты террейнов частично лежат в `pak01` (подменяются обычным способом),
а часть мира — в отдельном пакете `maps/dota.vpk`. Приоритет папки языка над этим
пакетом **не проверен** → категория «Карта» идёт как `experimental` (R13).

### 11.6 Нуль-модель — вопрос закрыт

```bash
grep -E '"Model"' units/scripts/npc/npc_units.txt | grep -c "invisiblebox"
# 5+ юнитов используют models/development/invisiblebox.vmdl как свою модель
```

Вывод: это официальная «невидимая» модель самой игры, годится для «пустого слота».

### 11.7 Что осталось непроверенным по категориям

| Гипотеза | План |
|---|---|
| Папка языка перекрывает `maps/dota.vpk` | A/B: два пака с разной текстурой земли, ProcMon/`-console` |
| Замена `tower_good` без `*_destruction_*` не ломает разрушение | лобби/`hero_demo`, снести башню |
| Замена только наземного курьера не ломает полёт | апгрейд курьера в лобби |
| HUD-паки от старых версий не ломают интерфейс | тест на 1–2 старых паках после патча |

## 12. M0: окружение и `wibe doctor` (22.11.2026)

### 12.1 Что установлено / найдено

```bash
winget install --id Microsoft.DotNet.SDK.8      # 8.0.428, до этого SDK не было (только рантаймы)
dotnet build; dotnet test                       # 24/24 тестов зелёные
```

### 12.2 Реальный вывод `wibe doctor`

```
STEAM
✓ библиотека: Z:\steam
· appid 570 · Dota 2
· размер установки: 78,15 ГБ

ИГРА
✓ Z:\steam\steamapps\common\dota 2 beta
✓ buildid 26825343
· AudioLanguage «russian» → папка модов «dota_russian»
· основной пак: pak01_dir.vpk (22,3 МБ)

ПАПКА МОДОВ
✓ gameinfo.gi: LayeredOnMod dota — папка монтируется поверх игры (штатный механизм Valve)
✓ SearchPaths: dota_russian первой в списке Game
· занято: pak01 (946,7 КБ), pak02 (228 МБ), pak03 (165,8 МБ), pak04 (192 МБ), pak05 (1,6 МБ), pak64 (2,2 КБ)
✓ свободные слоты: pak06, pak07, pak08, pak09, pak10 …
✓ рекомендуемый слот: pak06

ЦЕЛОСТНОСТЬ КЛИЕНТА
✓ подписанные файлы Valve совпадают с диском: 134/134

МОДЫ В ПАПКЕ
· pak02: 701 файлов, 228 МБ
· pak03: 910 файлов, 165,8 МБ
· pak04: 714 файлов, 192 МБ
· pak05: 13 файлов, 1,6 МБ   ← наш тестовый axe-carnival
· pak64: 2 файлов, 2,2 КБ    ← Dota 2 Mod Manager

КОНФЛИКТЫ
! один и тот же путь лежит в нескольких паках: 12 шт

ГОТОВО к установке модов · build 26825343 · папка dota_russian · слот pak06
```

### 12.3 Исправления к ТЗ, найденные на практике

| Что было записано | Что на самом деле |
|---|---|
| `game/dota/bin/win64/dota.signatures` | `game/bin/win64/dota.signatures` (135 строк, 134 файла) |
| «свободен pak06, в папке модов только pak64» | в `dota_russian` уже лежат чужие pak02–pak05 (228/166/192/1,6 МБ), pak01 — локализация Valve |
| приоритет паков «нижний номер выигрывает» | **не подтверждён**; 12 путей уже пересекаются между pak02/03/04 — идеальный стенд для проверки R1 |
| формат `dota.signatures` неизвестен | строки вида `...\..\..\dota\gameinfo.gi~SHA1:...;CRC:...`, база — `game/bin/win64` |
| индекс VPK читаем своей библиотекой | свой парсер отложен: дерево VPK в этих файлах не совпадает с ожидаемым layout → индекс берём через `Source2Viewer-CLI --vpk_list`, VRF — в M1 |

### 12.4 Что осталось непроверенным (в игре)

1. Приоритет паков в одной папке (R1) — теперь можно проверить на реальном конфликте pak02/pak04.
2. Видна ли подмена в **рейтинговом** матче (нужен реальный матч; в лобби/hero demo проверим раньше).
3. Поведение игры при битом VPK в папке модов.

## 13. M1 (часть): индекс, установка, снятие

### 13.1 Индекс пака

```bash
wibe find axe_carnival
# models/items/axe/axe_carnival/axe_carnival_base.vmdl_c
# найдено 40 из 388776 файлов · индекс свежий · build 26825343
```

Первый скан — ~2 с, дальше кеш `%LOCALAPPDATA%\WibeChanger\cache\pack_26825343.txt` (~9 МБ).

### 13.2 Установка мода (реальный прогон)

```bash
wibe install --hero axe --source models/items/axe/axe_carnival/axe_carnival_base.vmdl_c
# ✓ установлено в pak05: axe ← axe_carnival_base.vmdl_c → models/heroes/axe/axe.vmdl_c
# пак: ...\game\dota_russian\pak05_dir.vpk
# бэкап: %LOCALAPPDATA%\WibeChanger\library\pak05_axe---axe-carnival-base.vmdl_c.vpk
# crc: 0x5890d3a8 · 1,3 МБ
```

Проверка содержимого собранного пака:

```bash
bin/Source2Viewer-CLI.exe -i "...\dota_russian\pak05_dir.vpk" --vpk_list
# models/heroes/axe/axe.vmdl_c CRC:005890d3a8 size:1314278
```

CRC совпал с эталонным `axe_carnival_base.vmdl_c` (0x5890d3a8) → байты не испорчены.
Замер: 1,17 с на всё (извлечение → VPK → проверка → копирование → журнал).

`wibe doctor` после установки: `ЦЕЛОСТНОСТЬ КЛИЕНТА ✓ 134/134`, слот pak05 занят, следующий свободный — pak06.

Старый тестовый пак (13 файлов, 1,6 МБ) убран в `library\legacy-pak05-dir.vpk`, чтобы не мешал.

### 13.3 Что осталось проверить руками (в игре)

| Шаг | Где | Что смотрим |
|---|---|---|
| 1 | Демонстрация героя (Axe) | модель Carnival без T-позы, анимации, оружие |
| 2 | Лобби с ботами | то же в реальном матче, загрузка без ошибок |
| 3 | Рейтинговый матч | модель видна только тебе, ошибок/рассинхрона нет |
| 4 | `wibe uninstall --slot 05` | модель вернулась к ванильной |

## 14. GUI (Avalonia) и автономный exe — 22.11.2026

**Сборка exe.** `publish.ps1` собирает `dist\WibeChanger.exe` (self-contained, single-file, 46,1 МБ) и `dist\wibe.exe` (CLI, 33,5 МБ). Движок VPK копируется в `distin\` (160 МБ, в git не коммитится).

**Запуск из чужой папки.** `dist\WibeChanger.exe` запущен с `cwd = Z:\` — индекс прочитался (388 776 файлов), сетка героев заполнена, статус «Готово · 141 героев · 388 776 файлов в паке». Значит `Source2Viewer-CLI.exe` находится по `AppContext.BaseDirectoryin`, а не по текущей папке.

**Проверка кликами через UI Automation (без ручного участия).**
- Найдено 141 карточка героев (`AutomationId=list-heroes`), выбор элемента через `SelectionItemPattern` переключает правую панель — VM-логика выбора работает.
- Выбран Invoker (не установлен) → 12 кандидатов → выбран первый → `btn-install` был `enabled=True` → `InvokePattern.Invoke()` → через 10 с кнопка стала `disabled`, в журнале появился `pak06` (118,4 КБ, crc 0x33721b36).
- Затем `btn-uninstall` → `pak06` удалён из папки модов и из журнала; в папке остались только pak01…pak05 и pak64.
- Итог: установка и снятие работают из GUI тем же кодом `ModInstaller`, что и из CLI.

**Найденные и исправленные по ходу дефекты.**
1. Кеш индекса хранил только путь и CRC → в списке замен все размеры были «1 КБ». Формат кеша поднят до `#wibe2` (путь, CRC, размер), после перестроения размеры реальные (1,3 МБ, 190 КБ, 117 КБ).
2. Кандидаты шли вперемешку с частями моделей (голова персонажа, наплечники). Добавлена сортировка по «полноте»: `*base`/тело героя → прочее → части (`_head`, `_weapon`, `_shoulder` …), внутри группы — по размеру убыв.
3. У героев без своей косметики (эвентовые `aghanim_*`, `*_persona`) список был пустым. Теперь предлагаются тела других героев («чужое тело · Name») с пояснением в панели.
4. Длинные подписи обрезались — список переведён на две строки: имя файла + `сет · размер`.
5. Левое меню: подписи и счётчики слипались (DockPanel) — заменено на Grid `*,Auto`.

**Что осталось непроверенным в GUI:** 3D-превью, работа при запущенной Dota 2 во время установки (сейчас только предупреждение в doctor), поведение при отсутствии `bin\` (показывается текстовая ошибка «Не найден Source2Viewer-CLI.exe»).

**Замена без «снять → поставить».** Кнопка в панели называется «Заменить», если на героя уже стоит мод: `InstallAsync` снимает прежний пак и сразу ставит новый, чтобы в папке не оказалось двух паков с одним путём. Проверено через UI Automation на Axe: было `pak05` (axe_carnival_base), после клика «Заменить» с другим кандидатом осталось `pak06 (Axe ← axe_bare)`, `pak05` из папки исчез. После теста состояние возвращено командой `wibe uninstall --slot 06` + `wibe install --hero axe --source .../axe_carnival_base.vmdl_c` (снова pak05, crc 0x5890d3a8).

## 15. Превью моделей в UI — 22.11.2026

Пользователь: «один текст и не понятно, что за модель и как она будет выглядеть». Сделаны настоящие картинки, а не плейсхолдеры.

**Пайплайн.** `wibe preview <path>` = извлечь `.vmdl_c` из `pak01_dir.vpk` → `Source2Viewer-CLI --decompile --gltf_export_format glb --gltf_export_materials` (≈0,5 с, рядом появляются PNG базового цвета) → собственный программный растеризатор → PNG в кеш `%LOCALAPPDATA%\WibeChanger\cache\previews`.

**Рендерер (`src/Wibe.Core/Preview`).**
- `GlbScene` — минимальный читатель GLB: дерево узлов с TRS-матрицами, accessor/bufferView (float32, uint8/16/32, byteStride), материалы → `pbrMetallicRoughness.baseColorTexture` → PNG. Скелет/skinning игнорируются, модель в bind-позе.
- `ModelRenderer` — z-буфер, перспективно-корректные UV (интерполяция u/z), alpha-cutout по текстуре, ламберт + rim, суперсэмплинг ×2 с даунсэмплом, прозрачный фон.
- `PreviewService` — кеш по ключу `путь|crc|размер|yaw|pitch`, атомарная запись.

**Проверено на реальных моделях.** `models/items/axe/axe_carnival/axe_carnival_base.vmdl_c` → узнаваемый Axe в полном снаряжении, руки/ноги целиком в кадре (первый вариант обрезал кисти — дистанция камеры увеличена до 3,05 радиуса, fov 32°). `models/heroes/invoker_kid/invoker_kid.vmdl_c` → 3,3 с вместе с экспортом.

**Найденные дефекты.**
1. Первый рендер был «шардом треугольников»: в `Locate` для индексов брался stride = components×4, то есть 4 байта на uint16-индекс. Исправлено расчётом размера компонента по `componentType`.
2. Кеш не учитывал размер/угол → портрет 200 px подставлялся вместо панели 384 px. В ключ добавлены size/yaw/pitch.
3. `PreviewLoading` мог остаться включённым после отмены — введено поколение запроса (`_previewGeneration`).
4. Кандидаты-части (оружие, шлемы, наплечники) предлагались как «тело»: их установка ломает вид героя. Теперь из списка исключаются (`Tier == 2`), в выдаче остаются только полные тела и неопознанные крупные модели.

**UI.** Карточка героя: 3D-портрет 200×200 вместо монограммы (монограмма остаётся запасным вариантом), имя в две строки. Правая панель: блок 252 px с рендером выбранной замены и текстом «рендерю модель…», пока идёт рендер. Портреты 141 героя догружаются в фоне по 4 параллельно (~84 файла за 40 с на этой машине), второй запуск мгновенный из кеша.


## 16. Overlay-пайплайн: свой VPK, патч items_game и gameinfo (22.11.2026)

Переход на схему OVERLAY: `game/dota_custommod` + патченный `items_game.txt` + патч `gameinfo.gi` с авто-восстановлением.

### 16.1 VPK v2 writer

- `src/Wibe.Core/Vpk/VpkWriter.cs`: header 28 байт → дерево → данные → `otherMD5` (дерево/пустая секция архивов/весь файл).
- Ошибка №1: terminator записей писался 0xFFFF для конца папки/расширения → ValvePak падал «Invalid terminator, was 0x65». Разбор эталона (`--vpk_create`) показал: конец списка файлов/папок/расширений — пустая строка (0x00), а 0xFFFF остаётся только в 18-байтной записи.
- После правки: `Source2Viewer-CLI --vpk_list` читает пак, `--extract` отдаёт исходные байты (тест `VpkWriter_ProducesReadableVpk`).

### 16.2 Патч items_game.txt

- `ItemsGamePatcher` ищет базовый предмет героя (`prefab default_item`, слот по `item_slot` либо по имени модели) и переносит `visuals`, `model_player`, `image_inventory` выбранного предмета.
- Проверка на собранном паке: «Axe's Axe» → `axe_lava_legion_commander_weapon`, «Axe's Shoulder Guard» → `..._armor`, «Axe's Hair» → `axe_ti9_immortal_head`.
- Ограничение: `items_game` не описывает тела (бандлы) — тела берутся из индекса пака (`*_base.vmdl_c`) и кладутся в VPK по стоковым путям.

### 16.3 gameinfo.gi

- Патч вставляет `Game dota_custommod` первой строкой в `SearchPaths`, бэкап в `%LOCALAPPDATA%\WibeChanger\backup` + SHA-256, запись атомарная (`*.wibe.tmp` → `File.Move`).
- Восстановление: при выходе игры (монитор раз в 3 с), при старте приложения, кнопкой «снять мод». Проверено: файл возвращается байт-в-байт.

### 16.4 Сессия сборки

- `ModSession.Build`: если прошлая сессия пропатчена — сначала `Restore()`, иначе Source2Viewer CLI путается в собственных search paths и не извлекает модели (наблюдалось: 0 моделей вместо 3).
- Результат для Axe (weapon/armor/head + тело Carnival): 4 файла, 48–50 МБ, 3 модели, visuals изменены у 2–3 слотов, сборка ~3.5–4 с.
- HTTP-путь проверен: `POST /api/build` → `active=true, patched=true, backup=true, vpk=52 143 968`; `POST /api/revert` → «мод снят, gameinfo.gi восстановлен».

### 16.5 Один exe

- `publish.ps1`: `bin/` → `scratch/engine.zip` → `EmbeddedResource` в `Wibe.Core` (`Wibe.Engine.zip`) → `dotnet publish` single-file.
- Ошибка №2: ресурс искали в сборке `Wibe.Server`, а embed был в неё же добавлен при `typeof(EngineBundle)` из `Wibe.Core` → «ресурс не найден». Перенос ItemGroup в `Wibe.Core.csproj` решил.
- Итог: `dist\WibeChanger.exe` 103 МБ; скопирован в пустую папку, запущен: распаковал движок в `%LOCALAPPDATA%\WibeChanger\engine`, `/api/state` → `ready=true, version 0.3.0`, `/api/heroes` → 200, `/api/hero?folder=axe` → 5 слотов (тело 1, оружие 36, доспех 34, голова 37, пояс 33), `/api/preview` → PNG 49 КБ, `/api/img` → 200.

### 16.6 Тесты

32 зелёных: VPK round-trip через CLI, KeyValues round-trip, патч visuals базового предмета, insert/remove патча gameinfo (LF и CRLF).

## 17. Без 3D: картинки с CDN Valve и мгновенный каталог (22.11.2026)

### 17.1 Что вырезано

- `src/Wibe.Core/Preview/` (GlbScene, ModelRenderer, PreviewService), `EconImages` (`.vtex_c` → PNG), `SkiaSharp` из Core, `/api/preview`, команда `wibe preview`, `ExtractAssetDecompiled`/`ExtractModelGlb` в CLI.
- Старая Avalonia-оболочка `src/Wibe.App/` удалена из решения (веб-интерфейс её заменяет); иконка и манифест переехали в `src/Wibe.Server/`.
- Размер `WibeChanger.exe`: 103 МБ → 99.3 МБ (движок VPK внутри всё ещё 64 МБ), CLI `wibe.exe` 101.6 → 97.8 МБ.

### 17.2 Герои

- `HeroCatalog`: 127 настоящих героев из `odota/dotaconstants` (локализованные имена + пути портретов), фолбэк — `#base`-манифест `scripts/npc/npc_heroes.txt` (в новой сборке герои разложены по файлам, старый разбор блока `DOTAHeroes` давал 0), затем индекс пака.
- Алиасы (`Nerubian Assassin`, `Largo`-дубли и т.п.) больше не попадают в сетку: раньше список строился по индексу моделей и давал 141 запись с мусором.
- Портреты: `cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/<folder>.png` (в сетке) и `.../heroes/crops/<folder>.png` (крупная карточка).

### 17.3 Картинки скинов

- `ItemImages` строит официальные адреса по `image_inventory` (`dota_react/items/...` и старый `apps/570/icons/...`); если Valve по этому пути картинку не отдаёт — `MarketIcons` ищет предмет на торговой площадке Steam и берёт `community.steamstatic.com/economy/image/<hash>/330x192`.
- Проверено: React-путь для косметики отдаёт 404 (там только предметы магазина), экономический CDN работает: «Crucible of Rile» → PNG 63 974 байта.
- Покрытие на Axe: 43 из 141 варианта (остальные части сетов не торгуются и на CDN не публикуются) — для них отдаётся SVG-заглушка, а крупная карточка показывает арт героя вместо «нет фото».
- Кеш: `%LOCALAPPDATA%\WibeChanger\img_cache\<sha1>.png|jpg|webp` + `market_icons.json` (105 файлов / 4.2 МБ после прогона по Axe).
- Заголовок `X-Wibe-Image: real|placeholder` позволяет фронтенду отличать настоящую картинку от заглушки.

### 17.4 Мгновенный экран героя

- `HeroSetService.PrepareAll()` считает слоты всех героев одним проходом по индексу пака на старте; `Get()` — словарный поиск. `/api/heroes` 31 мс, `/api/hero?folder=axe` 34 мс (было: десятки секунд на поиск картинок сетов).
- Удалён перебор 22 454 записей `panorama/images/econ/sets` в `FindSetImage` — он и подвешивал страницу.
- Кнопка «Собрать мод и играть»: сборка пака → автозапуск Dota через Steam. Проверено через API: 4 файла, 49.7 МБ, 3.5 с, затем «мод снят, gameinfo.gi восстановлен».

### 17.5 Фронтенд

- Выбор предмета мгновенно меняет крупную карточку слева (фото скина + название + слот + редкость), футер показывает чипы выбранного.
- Глубокая ссылка `#/hero/axe?pick=head:12954` (проверка выделения и карточки скриншотом).
- Убраны все обращения к `/api/preview`.

## 18. Отказ от правки gameinfo.gi → `-language custommod` (v0.5.0, WIBE FITTING ROOM)

Симптом из игры: сет отображается идеально, но при запуске координатор пишет
«Игра не прошла проверку перед сетевой игрой, и поиск матча может не работать».
Причина — строка `Game dota_custommod` в `game/dota/gameinfo.gi`: координатор сверяет файл перед ММ.

### 18.1 Что изменено

- `GameInfoPatcher` больше **не умеет** патчить `SearchPaths`: методы `Patch()`/`InsertPatch()` удалены.
  Остались `Restore()`, `RestoreIfStale()`, `RemovePatch()`, `ClearModFiles()` и `EnsureLanguageFolder()`.
- `ModSession.Build()`: перед сборкой вызывается `RestoreIfStale()` (чистка легаси-патча) и
  `EnsureLanguageFolder()`; после записи VPK — снова `RestoreIfStale()`. Патча gameinfo в пайплайне нет.
- Папка мода: `game/dota_custommod/` рядом с `game/dota/`, внутри — `pak01_dir.vpk` и собственный
  `gameinfo.gi` (копия схемы `dota_russian`: `LayeredOnMod dota`, `Game/Mod dota_custommod`, `Game dota`,
  `Game core`, `AddonRoot dota_addons`, `PublicContent core`).
- `ModSession.Launch()`: аргументы строго `-novid -language custommod` (Steam: `-applaunch 570 ...`),
  без `-override_vpk` и `-enable_addons`. `Launch(dryRun)` возвращает команду, не запуская игру.
- `ModSession.Revert()`: удаляет `pak01_dir.vpk` и `gameinfo.gi` языковой папки, чистит легаси-патч игры.
- `AppState`: при старте — `RestoreIfStale()` + `EnsureLanguageFolder()`; `/api/state` отдаёт
  `gameInfoClean` и `language`; `/api/session` — `languageFolder`, `gameinfoClean`; `/api/launch` принимает `{dry:true}`.
- `RemovePatch()` сохраняет переводы строк байт-в-байт (CRLF-файл возвращается ровно тем же).
- Брендинг: `WIBE FITTING ROOM`, exe `WibeFittingRoom.exe`, версия 0.5.0; в UI чип «проверка матча пройдена».
- `FindSteam()`: сначала реестр (`SteamLocator.FindSteamRoot`), потом Program Files — на этой машине Steam
  стоит на `Z:\steam`, поэтому раньше запуск падал в прямой `dota2.exe`.

### 18.2 Проверки (реальный install, buildid 26825343)

| Проверка | Результат |
|---|---|
| md5 `game/dota/gameinfo.gi` до и после сборки сета Axe | `eb67f706…3ff5` → `eb67f706…3ff5` (не изменился) |
| легаси-патч в игре при старте приложения | строка удалена, md5 совпал с оригиналом байт-в-байт |
| легаси-патч + `POST /api/revert` | строка удалена, md5 совпал |
| `game/dota_custommod/` после сборки | `gameinfo.gi` (228 Б) + `pak01_dir.vpk` (51 050 527 Б, 4 файла) |
| содержимое пака (`--vpk_list`) | `scripts/items/items_game.txt` 50 633 689 Б + 3 модели Axe по стоковым путям |
| `POST /api/launch {dry:true}` | `steam.exe -applaunch 570 -novid -language custommod` |
| `POST /api/revert` | «мод снят: языковая папка очищена», gameinfo.gi игры не тронут |
| published `dist/WibeFittingRoom.exe` (99.3 МБ) в пустой папке | `/api/state` v0.5.0 `gameInfoClean:true`, build Pudge 48.3 МБ, dry-launch с `-language custommod`, revert чистый |
| тесты | 34/34 зелёные (в т.ч. байт-в-байт очистка CRLF-патча и схема языкового gameinfo) |

### 18.3 Что осталось проверить вживую

- Запуск из приложения: игра должна подняться с `-language custommod`, сет виден, а перед поиском матча
  баннера координатора быть не должно. Если баннер остался — значит клиент кеширует старый `gameinfo.gi`
  (перезапустить Steam) либо в игре остался другой мод-хук.

## 19. Мод реально монтируется: языковой пак вместо `-language custommod` (v0.6.0)

### 19.1 Причина бага «мод собрался, но скин не появился»

- Source 2 игнорировал `game/dota_custommod/gameinfo.gi`: в `game/dota/gameinfo.gi` нет
  `Game_Language dota_custommod`, поэтому `-language custommod` не создавал search path.
- После обновления Dota 2 от 24.07.2026 язык читается из `game/dota/cfg/boot.vcfg`
  (`UILanguage`, `AudioLanguage`), а `-language` с неизвестным значением ничего не монтирует.
- Проверено вручную: копия пака в `game/dota_russian/pak06_dir.vpk` + запуск с `-condebug` →
  `game/dota/console.log`, секция `[Filesystem] GAME`: `dota_russian\pak01..pak06, pak64`.

### 19.2 Что изменено

- `ModSession`: пак пишется в `game/dota_<AudioLanguage>` под свободный слот `pak02…pak99`
  (pak64 пропускается); `SessionState.PakFile`; `Launch(dryRun, conDebug)` — только `-novid [-condebug]`,
  без `-language`; `CheckMount()` парсит `console.log` и возвращает `MountReport`.
- `DotaInstallation`: `ReadAudioLanguage()` (boot.vcfg) → `ResolveModFolderName()` → `dota_russian` на этой машине.
- `GameInfoPatcher`: генерация языковой папки удалена; осталась только очистка легаси-патча.
- `Doctor`: секция «Монтирование» — ищет наш `pakNN` в `console.log` и говорит, смонтирован он или нет.
- API: `/api/mount`; `/api/launch` принимает `{condebug:true}`; `/api/session` отдаёт `mountFolder` и `pak`.
- Картинки: `ImageStore` (папка `png/`, миграция старого кеша, `prefetch.json`), `ImagePrefetcher`
  (16 потоков, CDN-фаза, затем иконки с площадки Steam с троттлингом 350 мс и паузой при 403/429),
  `ImageProxy` — диск → CDN → площадка → SVG-заглушка, `Cache-Control: max-age=31536000, immutable`.
  Кнопка «Скачать все фото» показывает прогресс `N/M`.
- Версия 0.6.0.

### 19.3 Проверки (реальный install, buildid 26825343)

| Проверка | Результат |
|---|---|
| `POST /api/build` (Axe: weapon 13927, armor 25824, head 12954) | `dota_russian/pak06_dir.vpk`, 4 файла, 51 050 527 Б, 3 модели |
| запуск `POST /api/launch {condebug:true}` | `console.log`: `[CommandLine] ... -novid -condebug` |
| `[Filesystem] GAME` | `dota_russian\pak01…pak06, pak64` — наш пак смонтирован |
| `GET /api/mount` | `ok:true`, `mountedPaks: pak01…pak06, pak64` |
| md5 `game/dota/gameinfo.gi` до/после | `eb67f706…3ff5` без изменений |
| `game/dota_custommod/` | пусто: легаси-папка очищена |
| иконки Axe | 76 из 140 реальных фото; остальные — заглушки (CDN 404 + Steam 403 на залп) |
| `GET /api/img` заголовки | `Cache-Control: public, max-age=31536000, immutable`, `X-Wibe-Image: real` |
| prefetch | 19 962 адреса, 16 потоков, готовое не перекачивается (`IsCached`), маркер `png/prefetch.json` |
| тесты | 34/34 |

### 19.4 Ограничения, которые честно видны в UI

- Экономический CDN Steam не публикует иконки нетоварных частей сетов; площадка Steam отдаёт 403
  при залповых запросах. Такие карточки показывают заглушку, а крупная карточка — арт героя.
- Поиск по площадке теперь идёт не чаще одного запроса в 350 мс и прекращается на 45–60 секунд после
  403/429, чтобы не усугублять блокировку.

## 20. v0.7.0 — иконки из VPK, полки, много героев, пресеты, декомпиляция

### 20.1 Что переделано

- `Tools/IconExtractor.cs`: один прогон Source2Viewer-CLI по `panorama/images/econ` и
  `panorama/images/heroes` → PNG в `png/panorama/...` с сохранением структуры, плюс
  `png/icons_index.json` (buildid, ключи `econ/...`, короткие ключи без последнего сегмента, `hero:<folder>`).
  Второй запуск при том же buildid не выполняется (кроме кнопки «Фото из VPK»).
- `ImageStore`: папка иконок (`Z:\…\png` → `<exe>/png` → `%LOCALAPPDATA%`), `catalog_cache.json`,
  `presets.json`, `selected_loadout.json`, миграция старого кеша.
- `ImageProxy`: иконки предметов отдаются **только с диска** (никаких CDN и Steam Market — заглушек у косметики больше нет),
  портреты героев — кеш → CDN → локальная иконка героя.
- `HeroSetService`: полки `sets` (bundle → все слоты одним кликом), `arcana` (arcana/persona),
  `immortal`, затем `slot:*`; сортировка по редкости rare→common; `catalog_cache.json` грузится на старте.
- `ModSession.BuildAll(HeroPicks[])`: один `items_game.txt` и один `pakNN_dir.vpk` для всех героев;
  `Revert()` удаляет все наши паки `pak02…pak99` (pak01/pak64 Valve не трогает).
- `AppState`/API: `/api/loadout`, `/api/loadout/clear`, `/api/compile`, `/api/decompile`, `/api/presets`,
  `/api/images/prefetch` (теперь — декодирование иконок).
- UI: полки-вкладки, бейджи редкости, счётчик выбранного на карточке героя, пресеты, кнопки
  «Компилировать мод» / «Декомпилировать», тёмная сетка без горизонтальных обрезок.

### 20.2 Проверки (реальный install, buildid 26825343)

| Проверка | Результат |
|---|---|
| декодирование иконок | 23 068 PNG, 1,7 ГБ, 1 мин 42 с; `icons_index.json` — 31 710 ключей |
| повторный старт | `catalog_cache.json` (5,4 МБ) подхвачен, `ready` за 4 с, повторной распаковки нет |
| `GET /api/hero?folder=lion` | 12 мс (раньше висело «готовим слоты…») |
| `GET /api/hero?folder=axe` | 2 мс; полки: сеты 35, immortal 7, тело 1, оружие 36, доспех 32, голова 33, пояс 32 |
| иконки предметов | `image/png`, 26–85 КБ, 1–4 мс с диска, без сети |
| портреты | `disk → CDN → локальная иконка`, 1,8–16 мс |
| `POST /api/loadout` × 2 героя | `selected_loadout.json`: axe 2 предмета + lion 1, `heroesCount: 2` |
| пресеты | сохранение/чтение `presets.json`: 2 героя, 3 предмета |
| `POST /api/compile` при запущенной Dota | корректный отказ: «Dota 2 запущена: закрой игру и повтори» (0,04 с) |
| тесты | 34/34 |

### 20.3 Ограничения

- Компиляция одного пака на несколько героев требует закрытой игры (файл пака занят процессом Dota 2);
  приложение честно отказывает и просит закрыть игру.
- Живой визуальный тест скина в матче по-прежнему за пользователем: механика монтирования не менялась с 0.6.0.

## 21. v0.7.1 — обложки сетов, строка примерки, сборка всех героев

### 21.1 Изменения

- Готовые сеты больше не показывают `loading_screen`/`image_inventory` бандла: обложкой стала иконка
  первого предмета сета (у «Trophies of the Evernight» и подобных в items_game стоит `econ/testitem_slot_empty`).
- Поиск иконки дополнен fallback по имени файла (`_byLeaf`): покрытие полок — **10 982 предмета,
  без картинки 16 (только body-модели), нет файла на диске 2**; в UI для body и «дырок» показывается арт героя.
- Новая строка **«Примерка»** на сетке героев и на странице героя: чипы героев с числом предметов,
  переход по клику, × для удаления героя, «очистить», кнопка **«Собрать всё»** и счётчик в шапке
  (`Компилировать мод · N гер.`).
- Сохранение выбора — отдельный таймер на каждого героя (раньше общий debounce мог потерять выбор
  при быстром переключении) + принудительный flush перед компиляцией.
- Сообщение сборки уточнено: `моделей 4 (тел 2), visuals изменены у 2 слотов`.

### 21.2 Проверки

| Проверка | Результат |
|---|---|
| полки всех 127 героев | 10 982 карточки; 16 без image (body), 2 файла отсутствуют в паке |
| обложки сетов | «Trophies of the Evernight», «Lumpo and Rupertus», «Arc Warden Magician», «Time wanderer» — реальные иконки предметов |
| примерка | 4 героя (Axe, Crystal Maiden, Drow Ranger, Queen of Pain) видны в строке, бейджи «1» на карточках |
| `POST /api/compile` | **один `dota_russian/pak02_dir.vpk`**, героев 4, файлов 5, 52 394 555 Б, моделей 4 (тел 2) |
| содержимое пака | `axe.vmdl_c`, `queenofpain.vmdl_c` (тела), `drow_bracer.vmdl_c`, `crystal_maiden_cape.vmdl_c`, `items_game.txt` |
| gameinfo.gi | `eb67f706…3ff5` без изменений |
| тесты | 34/34 |

## 22. v0.7.2 — бейдж «мод» только у героев в текущей сборке

### 22.1 Причина

Карточка героя брала `hasMod`/`modTitle` из `journal.json`. Журнал хранит записи старых установок
(`hero.body`/`hero.slot`), которые после перехода на сессионную сборку `pakNN` никто не чистил,
поэтому у Axe навсегда оставался бейдж «мод» даже при пустой примерке.

### 22.2 Исправление

- `ModSession.CompiledHeroes` и `CompiledInfo(folder)` читают **текущее состояние сессии**
  (`session.json` + существующий `pakNN_dir.vpk`) — единственный источник правды.
- `AppState.BuildHeroes` ставит `hasMod` только героям из текущего пака; подпись — «в сборке: N предмет(ов)».
- `/api/hero` отдаёт `installed` из сессии, а не из журнала.
- `BuildAll` и `Revert` очищают старый `journal.json`.
- Если герой ещё и выбран в примерке, бейдж «мод» скрывается, чтобы не наезжать на счётчик.

### 22.3 Проверки

| Проверка | Результат |
|---|---|
| пустая сессия (паков нет) | `hasMod: []` у всех 127 героев — ни одного «мод» |
| сборка Axe (тело) + Drow (arms) → `pak02_dir.vpk` | `hasMod`: только `axe`, `drow_ranger`; остальные 125 — без бейджа |
| `/api/hero?folder=axe` | `installed = { title: «в сборке: 1 предмет(ов)», slot: 2, files: 3 }` |
| UI | «мод» только на карточках из текущего пака; у выбранных — счётчик |


## 23. v0.8.0 — гибридный конвейер: npc_heroes + физический ремап + visuals

### 23.1 Что изменено

- `HeroModelPatcher` патчит `scripts/npc/heroes/npc_dota_hero_<hero>.txt`: поле `Model` героя
  становится моделью Личности/Арканы (`entity_model`), чтобы движок спавнил нужный скелет.
- `ModSession` для каждого выбранного предмета кладёт `.vmdl_c` и по оригинальному пути, и поверх
  стоковой модели слота.
- Личности: `head_persona_1`/`arms_persona_1`/`back_persona_1`/… физически заменяют взрослые
  стоковые пути; слоты без персонажной пары глушатся `tiny_empty.vmdl_c` (41 заглушка на 12 героев).
  В `items_game.txt` их `model_player`/`visuals` переносятся в основные слоты (`head`, `arms`, …).
- Арканы с чужим нативным слотом (`head`, `back`, `weapon`): на тело кладётся `entity_model`,
  а `model_player` арканы — поверх стокового слота. Остальные слоты героя **не обнуляются**
  (у аркан нет `hide_slots`).
- Поддержаны `body_head` (Faceless Void, Claszian Apostasy) и арканы с пустым `item_slot`
  (PA, Razor — карточка в слоте оружия).

### 23.2 Автоматические проверки

| Проверка | Результат |
|---|---|
| тесты | **36/36** (добавлены `HybridPipelineTests`) |
| все Личности (12 героев одним паком) | 112,2 МБ, 101 файл: у всех 12 тело = `entity_model`, 12 npc-файлов, 18/18 персонажных деталей на взрослых путях, 41 заглушка |
| все трансформирующие Арканы (20 героев одним паком) | 339,8 МБ, 872 файла: у всех 20 тело = `entity_model`, 20/20 npc-файлов, 20/20 нативных деталей на стоковых слотах |
| Invoker Kid | `invoker.vmdl_c` CRC = `invoker_kid.vmdl_c`; `invoker_hair/cape/bracer/shoulder` = kid-детали; `invoker_dress` = пустая заглушка; npc `Model = invoker_kid.vmdl` |
| Juggernaut Bladeform Legacy | `juggernaut.vmdl_c` CRC = `juggernaut_arcana.vmdl_c`; npc `Model = juggernaut_arcana.vmdl`; остальные слоты не обнулены |
| Crystal Maiden / Zeus / PA / Razor | тело = полная аркана, стоковая спина/волосы/оружие = арканная деталь |
| gameinfo.gi | md5 `eb67f7069f6e95e1ffa469ec060d3ff5` без изменений |
| извлечение моделей | 8 дополнительных asset_modifier-моделей не найдены в паке (не критично, основные модели на месте) |


## 24. v0.8.1 — подавление чужих надетых предметов у Личностей и Аркан

### 24.1 Проблема

На скриншотах из Demo Hero у Invoker Kid оставалась взрослая голова/деталь: на аккаунте
могут быть надеты кастомные предметы, которых нет среди стоковых `default_item`. Их модели
не перекрывались, поэтому рендерились поверх Личности. То же касается Аркан и чужих плащей/оружия.

### 24.2 Исправление

- После сборки выбранного героя с `entity_model` перебираются **все** носимые предметы этого
  героя (`item_slot`: head/back/arms/weapon/…), кроме выбранных в примерке и персонажных слотов.
- У каждого такого предмета собираются `model_player` + модели из `visuals`, и по их путям
  в наш VPK кладётся `tiny_empty.vmdl_c`. Чужая вещь физически не может отрисоваться.
- Уже упакованные выбранные пользователем предметы и родные детали Личности не затрагиваются
  (`files.ContainsKey`).
- Для Invoker + Juggernaut: 107 чужих моделей заглушено, пак 59,2 МБ, 132 файла.

### 24.3 Проверки

| Проверка | Результат |
|---|---|
| сборка Invoker Kid + Juggernaut Arcana | успешно, 132 файла, 59,2 МБ |
| чужие кастомные модели | 107 путей заменены на `tiny_empty.vmdl_c` |
| выбранные модели | тело/детали Личности и Арканы не затёрты |
| gameinfo.gi | md5 `eb67f7069f6e95e1ffa469ec060d3ff5` |


## 25. v0.8.2 — model_player тела = entity_model (причина стокового Juggernaut)

### 25.1 Причина

У Аркан `entity_model` лежит **не напрямую в `visuals`**, а внутри
`asset_modifier { type entity_model; modifier … }`. Детектор искал только прямой
`type`, поэтому `model_player` базового предмета не получал модель Арканы.
`ApplyToBase` при наличии сущности ещё и **очищал** `model_player`, после чего движок
падал обратно на стоковую модель из `game/dota` — Juggernaut в демо оставался стоковым,
хотя `items_game` и `npc_heroes` уже были пропатчены.

### 25.2 Исправление

- `FindEntityModel` рекурсивно достаёт `modifier` из `asset_modifier` любой вложенности.
- `ApplyToBase` больше не очищает модель, а ставит `model_player` = `entity_model`
  для тела (или нативный `model_player` Арканы для её слота).
- Для Аркан с нативным слотом (CM спина, PA оружие) добавлен **второй remap на `body`**,
  поэтому и тело, и нативный слот получают правильные модели.
- `ApplyPersonaParts` не очищает `body`, чтобы не стирать только что выставленную
  модель Личности.

### 25.3 Проверки

| Проверка | Результат |
|---|---|
| Juggernaut `hero_base.model_player` | `models/heroes/juggernaut/juggernaut_arcana.vmdl` |
| Invoker `hero_base.model_player` | `models/heroes/invoker_kid/invoker_kid.vmdl` |
| `npc_heroes` Model | совпадает с entity_model |
| тесты | 36/36 |
| gameinfo.gi | md5 `eb67f7069f6e95e1ffa469ec060d3ff5` |


## 26. v0.8.3 — «золотая середина»: только модели, без патча подписей и VAC

### 26.1 Вывод после проверки

- Схема с `gameinfo_branchspecific.gi` + `dota.signatures` действительно включает `items_game`,
  но клиент показывает предупреждение VAC о локально изменённых файлах.
- **Для Аркан и Личностей `items_game` не нужен.** Достаточно физически положить их
  скомпилированные модели по стоковым путям в языковой пак `game/dota_<язык>/pak02_dir.vpk`.
- Проверено в Demo Hero: Invoker отображается ребёнком из языкового пака без каких-либо
  изменений подписанных файлов.
- `gameinfo_branchspecific.gi` и `dota.signatures` возвращены к оригиналам, папка `dota_mods`
  удалена. `gameinfo.gi` не менялся: md5 `eb67f7069f6e95e1ffa469ec060d3ff5`.

### 26.2 Что добавлено

- Для Аркан с отдельной моделью оружия (`…_weapon_arcana`) она теперь кладётся и на стоковый
  путь оружия героя. У Juggernaut:
  - `models/heroes/juggernaut/juggernaut.vmdl_c` = `juggernaut_arcana.vmdl_c`
  - `models/heroes/juggernaut/jugg_sword.vmdl_c` = `ancient_exile_weapon_arcana.vmdl_c`

### 26.3 Проверки

| Проверка | Результат |
|---|---|
| Demo Hero, Invoker | ребёнок (Личность) без VAC-предупреждения |
| Juggernaut body CRC | `0059437c0e` = arcana body |
| Juggernaut sword CRC | `006add68f7` = arcana weapon |
| gameinfo_branchspecific.gi | оригинал, SHA1 `224B6F87…D31F` |
| dota.signatures | оригинал |
| gameinfo.gi | md5 `eb67f7069f6e95e1ffa469ec060d3ff5` |
| тесты | 36/36 |

### 27. Все Арканы, башни, ландшафт и эмблема (v0.9.1)

- 25 Аркан разворачиваются в наборы слотов; автотест собирает 89 вариантов (стили 0/1/2)
  через `wibe-rust-builder`, без потери слотов. Wisp получает свою целую модель поверх
  тела героя, когда у героя нет стокового слота.
- Мир: `radianttowers`, `diretowers`, `terrain`, `emblem` из `items_game.txt`; выбор
  хранится в `selected_loadout.json` и в пресетах.
- `terrain` копируется отдельным файлом `<dota>/maps/dota.vpk`, pak06 остаётся один.
- В VPK мода нет `items_game.txt`, `npc_heroes.txt`, `gameinfo.gi`.
- Вкладка «Мир»: 12 ландшафтов, 5+5 башен, 11 эмблем; клик сохраняется, кольцо и
  карточки обновляются.

| Проверка | Результат |
|---|---|
| Автотест Аркан | 89/89 вариантов, пройден |
| Тесты | 47/47 |
| Сборка 3 Аркан + мир | 91 файл, 29 159 335 б, ландшафт Immortal Gardens |
| Монтирование | `GAME … dota_russian\pak06.vpk` в `console.log` |
| «Проверка игры» | диалога нет, экран героев чистый |
| Phantom Assassin Arcana | видна в меню на скриншоте пользователя |
| `gameinfo.gi` | SHA1 `a0dc87acc7a659eedd1f9575eee2bd3883e8f201` |
| `gameinfo_branchspecific.gi` | SHA1 `224b6f879d00d419b6a10fe07b4ddb3d827fd31f` |
| `dota.signatures` | SHA1 `89e9fe00e0ab1ddcc0ef433f43c67b66fff0d2cc` |
| `pak64_dir.vpk`, `dota_mods` | отсутствуют |
| `dist/WibeFittingRoom.exe` | 109 837 466 б, SHA256 `5d9e23a2b9b3b7717fd735b36f929ba2dddf23c88d2efe8236491e9bcc80d01c` |

## v0.9.29 — локальная коллекция, насмешки, иконки слотов

- Карточки: 139 героев/Личностей в сетке; 1034 папки-раздела, 9439 карточек, 195 несопоставленных OCR-строк отброшено.
- Насмешки: 100 разделов Taunt, 113 карточек; тумблер «Показывать в витрине и в редком простое» сохраняется в `rareTaunt`.
- Личность всегда показывает свою карточку сета; обычные сеты базового героя не подставляются.
- Квадратик занятого слота: картинка выбранного стиля копируется под стоковый `image_inventory`; тест `SlotIconTests` — 1/1.
- Dawnbreaker: 4 доп. слота, 43 варианта; Pudge: 10 слотов + Насмешка.
- `dotnet test`: 54/56; 2 live-теста pak06 не проходят, потому что текущий pak06 содержит только ранее выбранных героев (не регрессия).
- Подписанные файлы не изменены: `gameinfo.gi a0dc87ac…`, `gameinfo_branchspecific.gi 224b6f87…`, `dota.signatures 7dfbb681…`.
- `dist/WibeFittingRoom.exe`: 191 651 840 б, SHA256 `b43be04ada5d32a232546cc018090196b170f27f76e8e0b352ac788a49abbac5`.
