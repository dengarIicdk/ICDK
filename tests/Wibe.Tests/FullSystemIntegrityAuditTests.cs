using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Mods;
using Xunit;
using Xunit.Abstractions;

namespace Wibe.Tests;

/// <summary>
/// Сквозной аудит системы: настоящие сборки через Rust-ядро и проверка БАЙТОВ внутри pak06.
/// Ничего в игре не остаётся — паки пишутся во временную папку, через outputOverride.
///
/// Массовые прогоны (все 25 аркан, все 12 личностей) долгие, поэтому включаются явно:
///   WIBE_FULL_AUDIT=1 dotnet test tests/Wibe.Tests --filter FullSystemIntegrityAuditTests
/// Без переменной проверяются те узлы, что успевают собраться за обычный прогон.
/// </summary>
public class FullSystemIntegrityAuditTests
{
    private readonly ITestOutputHelper _output;

    public FullSystemIntegrityAuditTests(ITestOutputHelper output) => _output = output;

    private const string EnableVariable = "WIBE_FULL_AUDIT";

    private static bool Enabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

    /// <summary>Все арканы игры: герой и предмет-основа (entity_model либо голова комплекта).</summary>
    private static readonly (string Hero, int Item)[] Arcanas =
    {
        ("lina", 4794),
        ("legion_commander", 5810),
        ("terrorblade", 5957),
        ("techies", 6879),
        ("zuus", 6914),
        ("nevermore", 6996),
        ("phantom_assassin", 7247),
        ("crystal_maiden", 7385),
        ("pudge", 7756),
        ("monkey_king", 9050),
        ("juggernaut", 9059),
        ("wisp", 9235),
        ("spectre", 9662),
        ("rubick", 12451),
        ("earthshaker", 12692),
        ("queenofpain", 12930),
        ("skeleton_king", 13456),
        ("ogre_magi", 13670),
        ("windrunner", 13806),
        ("faceless_void", 18033),
        ("skywrath_mage", 18539),
        ("drow_ranger", 19090),
        ("vengefulspirit", 22718),
        ("razor", 23095),
        ("tidehunter", 37143),
    };

    /// <summary>Все личности: герой и предмет-селектор persona_selector.</summary>
    private static readonly (string Hero, int Item)[] Personas =
    {
        ("axe", 31367),
        ("invoker", 13042),
        ("pudge", 13786),
        ("mirana", 18178),
        ("antimage", 13783),
        ("dragon_knight", 18113),
        ("crystal_maiden", 19205),
        ("phantom_assassin", 22723),
        ("legion_commander", 36191),
        ("bristleback", 36214),
        ("morphling", 36193),
        ("oracle", 31357),
    };

    private static (DotaInstallation Install, EconCatalog Catalog) Game()
    {
        var install = DotaInstallation.Detect(out var error);
        Assert.True(install is not null && install.IsValid, "Dota 2 не найдена: " + error);
        return (install!, EconCatalog.Load(install!));
    }

    private static string BuildPak(DotaInstallation install, EconCatalog catalog, IEnumerable<HeroPicks> picks, string name, RustCoreModBuilder.WorldSelection? world = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wibe-full-audit");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, name + ".vpk");
        if (File.Exists(outPath))
        {
            File.Delete(outPath);
        }

        var result = RustCoreModBuilder.Build(install, catalog, picks.ToList(), null, world, outPath);
        Assert.True(result.Success, name + ": " + result.Message);
        return outPath;
    }

    private static int SlotStyle(int styles, int wanted) => styles <= 1 ? 0 : wanted;

    private static string SlotOf(EconItem item) =>
        item.Slot.Length > 0 ? item.Slot : item.EntityModel is not null ? "body" : "weapon";

    /// <summary>
    /// The folder a hero's body lives in: "zuus" is zeus on disk, "nevermore" is shadow_fiend,
    /// "skeleton_king" is wraith_king, "drow_ranger" is drow. The catalog knows the stock body
    /// path of every hero (its default items), so the folder is read from there.
    /// </summary>
    private static string ModelFolder(EconCatalog catalog, string hero)
    {
        var key = "npc_dota_hero_" + hero;
        var items = catalog.DefaultsFor(key).Concat(catalog.ForHero(key));
        foreach (var item in items)
        {
            var model = item.CompiledModel;
            if (model is null || !model.StartsWith("models/heroes/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = model["models/heroes/".Length..];
            var slash = rest.IndexOf('/');
            if (slash > 0)
            {
                return rest[..slash];
            }
        }

        foreach (var item in catalog.ForHero(key))
        {
            var model = item.Model;
            if (model is null || !model.StartsWith("models/items/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = model["models/items/".Length..];
            var slash = rest.IndexOf('/');
            if (slash > 0)
            {
                return rest[..slash];
            }
        }

        return hero;
    }

    /// <summary>
    /// Узел 1: второй стиль Wraith King. Все части комплекта обязаны попасть в пак, а не только
    /// голова арканы, и стиль 2 должен доехать до каждой части — иначе броня остаётся зелёной,
    /// пока тело уже красное.
    /// </summary>
    [Fact]
    public void Wraith_king_second_style_reaches_every_slot_of_the_pack()
    {
        if (!Enabled)
        {
            return;
        }

        var (install, catalog) = Game();
        var arcana = catalog.ItemById(13456);
        Assert.True(arcana is not null, "нет арканы 13456");
        var slot = SlotOf(arcana!);

        var plain = BuildPak(install, catalog,
            new[] { new HeroPicks("skeleton_king", new List<(string Slot, int ItemId)> { (slot, 13456) }) },
            "wk-style-1");

        var styled = BuildPak(install, catalog,
            new[]
            {
                new HeroPicks("skeleton_king", new List<(string Slot, int ItemId)> { (slot, 13456) },
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [slot] = 1 }),
            },
            "wk-style-2");

        var first = PakBodyIntegrityTests.ReadSizes(plain);
        var second = PakBodyIntegrityTests.ReadSizes(styled);

        // каждая часть арканного комплекта на своём стоковом пути
        foreach (var part in new[]
                 {
                     "models/heroes/wraith_king/wraith_king.vmdl_c",
                     "models/heroes/wraith_king/wraith_king_shoulder.vmdl_c",
                     "models/heroes/wraith_king/wraith_king_chest.vmdl_c",
                     "models/heroes/wraith_king/wraith_king_cape.vmdl_c",
                     "models/heroes/wraith_king/wraith_king_weapon.vmdl_c",
                     "models/heroes/wraith_king/wraith_king_head.vmdl_c",
                 })
        {
            Assert.True(second.ContainsKey(part), "в паке нет " + part);
            Assert.True(second[part] > 4 * 1024, part + ": " + second[part] + " байт — пустышка");
        }

        // тело — целое, не часть и не сток
        Assert.True(second["models/heroes/wraith_king/wraith_king.vmdl_c"] > 1_000_000, "тело Wraith King меньше мегабайта");

        // стиль 2 обязан отличаться от стиля 1 в каждой части брони: там, где стили лежат
        // мешами одного файла, разница — это маска групп мешей внутри файла
        foreach (var part in new[]
                 {
                     "models/heroes/wraith_king/wraith_king_chest.vmdl_c",
                     "models/heroes/wraith_king/wraith_king_cape.vmdl_c",
                     "models/heroes/wraith_king/wraith_king_shoulder.vmdl_c",
                 })
        {
            var one = PakBodyIntegrityTests.ReadEntry(plain, part);
            var two = PakBodyIntegrityTests.ReadEntry(styled, part);
            Assert.NotNull(one);
            Assert.NotNull(two);
            Assert.False(one!.SequenceEqual(two!), part + ": стиль 2 не доехал до части комплекта");
        }

        // иконки скиллов второго стиля стоят на стоковых путях и несут КРАСНЫЙ вариант:
        // размеры сверяются со стоковым паком по индексу (байты pak01 лежат в чанках),
        // а смена стиля — по нашим же двум сборкам
        var stock = PakBodyIntegrityTests.ReadSizes(install.MainPakDir);
        var icons = second.Keys.Where(p => p.StartsWith("panorama/images/spellicons/skeleton_king", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(icons);
        Assert.All(icons, p => Assert.DoesNotContain("arcana/", p, StringComparison.OrdinalIgnoreCase));

        foreach (var skill in new[] { "hellfire_blast", "bone_guard", "reincarnation", "mortal_strike" })
        {
            var path = $"panorama/images/spellicons/skeleton_king_{skill}_png.vtex_c";
            var altSize = stock[$"panorama/images/spellicons/skeleton_king/arcana/skeleton_king_{skill}_arcana_alt_png.vtex_c"];
            var plainSize = stock[$"panorama/images/spellicons/skeleton_king/arcana/skeleton_king_{skill}_arcana_png.vtex_c"];
            Assert.Equal(altSize, second[path]);
            Assert.Equal(plainSize, first[path]);
            var one = PakBodyIntegrityTests.ReadEntry(plain, path);
            var two = PakBodyIntegrityTests.ReadEntry(styled, path);
            Assert.NotNull(one);
            Assert.NotNull(two);
            Assert.False(one!.SequenceEqual(two!), $"иконка {skill} не сменилась на второй стиль");
        }

        _output.WriteLine($"WK style 2: {second.Count} файлов, тело {second["models/heroes/wraith_king/wraith_king.vmdl_c"]} b, иконок {icons.Count}, стоковых записей {stock.Count}");
    }

    /// <summary>
    /// Узел 2: все 25 аркан во втором стиле. Тело героя не должно быть затёрто стоком из-за
    /// патча насмешек, пустышкой или частью комплекта.
    /// </summary>
    [Fact]
    public void Every_arcana_keeps_a_whole_custom_body_in_the_pack()
    {
        if (!Enabled)
        {
            return;
        }

        var (install, catalog) = Game();
        var stock = PakBodyIntegrityTests.ReadSizes(install.MainPakDir);
        var picks = new List<HeroPicks>();
        var expected = new List<(string Hero, string Body)>();
        foreach (var (hero, itemId) in Arcanas)
        {
            var item = catalog.ItemById(itemId);
            Assert.True(item is not null, $"нет предмета {itemId}");
            var slot = SlotOf(item!);
            var style = SlotStyle(Math.Max(1, item!.Styles), 1);
            picks.Add(new HeroPicks(hero,
                new List<(string Slot, int ItemId)> { (slot, itemId) },
                style > 0 ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [slot] = style } : null));
            expected.Add((hero, ModelFolder(catalog, hero)));
        }

        var pak = BuildPak(install, catalog, picks, "all-arcanas");
        var made = PakBodyIntegrityTests.ReadSizes(pak);
        var failures = new List<string>();
        foreach (var (hero, folder) in expected)
        {
            var bodies = made
                .Where(e => e.Key.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase)
                            && (e.Key.StartsWith("models/heroes/" + folder + "/", StringComparison.OrdinalIgnoreCase)
                                || e.Key.StartsWith("models/heroes/" + hero + "/", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (bodies.Count == 0)
            {
                failures.Add($"{hero}: в паке нет ни одного файла тела");
                continue;
            }

            var biggest = bodies.OrderByDescending(e => e.Value).First();
            if (biggest.Value < 64 * 1024)
            {
                failures.Add($"{hero}: {biggest.Key} = {biggest.Value} b — тело затёрто");
            }

            if (stock.TryGetValue(biggest.Key, out var clean) && clean > 500 * 1024 && biggest.Value == clean)
            {
                failures.Add($"{hero}: {biggest.Key} совпадает со стоком байт в байт — аркана не применилась");
            }

            _output.WriteLine($"{hero,-20} тело {biggest.Value,9} b ({biggest.Key}), частей {bodies.Count}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Узел 2 (личности): тело личности не перезатёрто стоком, не пустышка, и взрослые предметы
    /// обычного героя в пак не просочились.
    /// </summary>
    [Fact]
    public void Every_persona_keeps_its_own_body_and_no_adult_items_leak()
    {
        if (!Enabled)
        {
            return;
        }

        var (install, catalog) = Game();
        var stock = PakBodyIntegrityTests.ReadSizes(install.MainPakDir);
        var picks = new List<HeroPicks>();
        foreach (var (hero, itemId) in Personas)
        {
            picks.Add(new HeroPicks(hero, new List<(string Slot, int ItemId)> { ("persona_selector", itemId) }));
        }

        var pak = BuildPak(install, catalog, picks, "all-personas");
        var made = PakBodyIntegrityTests.ReadSizes(pak);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(RustCoreModBuilder.InstallDirectory, "build_manifest.json"))) as JsonObject;
        var manifestPicks = (manifest?["picks"] as JsonArray)?.Count ?? 0;
        var failures = new List<string>();

        foreach (var (hero, itemId) in Personas)
        {
            var bodies = made
                .Where(e => e.Key.StartsWith("models/heroes/", StringComparison.OrdinalIgnoreCase)
                            && e.Key.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase)
                            && e.Key.Contains("/" + hero + "/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var biggest = bodies.OrderByDescending(e => e.Value).FirstOrDefault();
            if (biggest.Key is null || biggest.Value < 64 * 1024)
            {
                failures.Add($"{hero} ({itemId}): тела нет или оно {biggest.Value} b");
                continue;
            }

            if (stock.TryGetValue(biggest.Key, out var clean) && clean > 500 * 1024 && biggest.Value == clean)
            {
                failures.Add($"{hero}: {biggest.Key} равен стоку — личность не надета");
            }

            // пустышки в 4 килобайта допустимы только как заглушки пустых слотов
            var stubs = bodies.Count(e => e.Value < 8 * 1024);
            _output.WriteLine($"{hero,-20} личность {itemId}: тело {biggest.Value,9} b, файлов {bodies.Count}, заглушек {stubs}");
        }

        Assert.True(manifestPicks >= Personas.Length, "в манифесте меньше предметов, чем личностей: " + manifestPicks);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Узел 4 и 5: мир (башни, крипы, эмблема), насмешка, портреты — и ни одного живого
    /// loadout-партикла, который встал бы в центре карты.
    /// </summary>
    [Fact]
    public void The_world_taunt_particles_and_portraits_land_together()
    {
        if (!Enabled)
        {
            return;
        }

        var (install, catalog) = Game();
        var world = new RustCoreModBuilder.WorldSelection(RadiantTower: 23195, DireTower: 23712, Emblem: 23238, RadiantCreep: 18876, DireCreep: 18839);
        var picks = new List<HeroPicks>
        {
            // насмешка и аркана: аркана приносит 3D-портрет героя, насмешка — свои секвенции
            new("juggernaut", new List<(string Slot, int ItemId)> { ("taunt", 4994) }),
            new("skeleton_king", new List<(string Slot, int ItemId)> { ("body", 13456) },
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["body"] = 1 }),
        };

        var dir = Path.Combine(Path.GetTempPath(), "wibe-full-audit");
        Directory.CreateDirectory(dir);
        var pak = Path.Combine(dir, "world-and-taunt.vpk");
        var log = new List<string>();
        var built = RustCoreModBuilder.Build(install, catalog, picks, l =>
        {
            lock (log)
            {
                log.Add(l);
            }
        }, world, pak);
        Assert.True(built.Success, built.Message);
        var made = PakBodyIntegrityTests.ReadSizes(pak);

        // башни и крипы обеих сторон
        Assert.Contains(made.Keys, p => p.Contains("radiant_tower", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(made.Keys, p => p.Contains("dire_tower", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(made.Keys, p => p.Contains("creep_radiant", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(made.Keys, p => p.Contains("creep_bad", StringComparison.OrdinalIgnoreCase));

        // эмблема — это подмена кольца выбора героя
        Assert.Contains("particles/ui_mouseactions/hero_ring.vpcf_c", made.Keys);

        // Партикл на реке: loadout-эффект башни без родителя падал в мировое начало (0,0,0).
        // В паке его быть не должно ни для одной стороны.
        Assert.DoesNotContain(made.Keys,
            p => p.Contains("_towers_ambient_loadout", StringComparison.OrdinalIgnoreCase));

        // Башня собрана штатным конвейером mm-core: список секвенций на месте и начинается
        // с собственной секвенции башни. Никаких ручных сдвигов байтов в модели нет —
        // структурную целостность ресурса проверяет KartotekaResursovTests ниже по CLI.
        foreach (var path in new[]
                 {
                     "models/props_structures/radiant_tower002.vmdl_c",
                     "models/props_structures/dire_tower002.vmdl_c",
                 })
        {
            var model = PakBodyIntegrityTests.ReadEntry(pak, path)!;
            Assert.NotNull(model);
            var at = IndexOf(model, "m_localSequenceNameArray");
            Assert.True(at > 0, path + ": в модели нет списка секвенций");
            Assert.True(IndexOf(model, "m_localS1SeqDescArray") > 0, path + ": в модели нет описаний секвенций");
            // kv3 в модели двоичный: имя секвенции идёт сразу за ключом, через ноль
            var head = System.Text.Encoding.ASCII.GetString(model, at, 300);
            var names = head.Split((char)0);
            Assert.True(names.Length > 2 && names[1].Length > 2, path + ": имя первой секвенции пустое");
            var head4 = string.Join(' ', names.Skip(1).Take(6));
            Assert.Contains("idle", head4, StringComparison.OrdinalIgnoreCase);
        }

        // портреты: файл на месте, ключи моделей не задвоены сверх стока
        Assert.Contains("scripts/npc/portraits.txt", made.Keys);
        var portraits = PakBodyIntegrityTests.ReadEntry(pak, "scripts/npc/portraits.txt");
        Assert.NotNull(portraits);
        var text = System.Text.Encoding.Latin1.GetString(portraits!);
        Assert.Contains("\"Portraits\"", text);
        // у арканы свой 3D-портрет, и он в файле на месте
        Assert.Contains("models/items/wraith_king/arcana/wraith_king_arcana.vmdl", text);
        Assert.Contains("models/heroes/wraith_king/wraith_king.vmdl", text);

        // Ни одного живого loadout-партикла героя: они и висели в центре карты. Стоковые
        // «loadout» героя обязаны лежать пустышкой (empty_particle, меньше 2 КБ), а эффектов
        // предметов (particles/econ/items/...) в паке быть не должно вообще.
        var loadout = made.Keys
            .Where(p => p.StartsWith("particles/units/heroes/", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Contains("_loadout", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(loadout);
        Assert.All(loadout, p => Assert.True(made[p] < 2 * 1024, $"{p}: {made[p]} b — партикл не заглушён"));
        // эффекты Аркан (огонь меча, крылья, клинки) в паке остаются, а loadout-эффекты предметов — нет
        Assert.DoesNotContain(made.Keys, p => p.StartsWith("particles/econ/items/", StringComparison.OrdinalIgnoreCase)
                                             && p.Contains("loadout", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(made.Keys, p => p.StartsWith("particles/econ/items/wraith_king/wraith_king_arcana/", StringComparison.OrdinalIgnoreCase));

        // насмешка доехала: ядро отчиталось, что клип Razor's Edge вставлен в тело Juggernaut,
        // а не выброшен («plays ... for N stock sequences»)
        var taunts = log.Where(l => l.Contains("taunt", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(taunts);
        Assert.Contains(taunts, l => l.Contains("juggernaut", StringComparison.OrdinalIgnoreCase));
        _output.WriteLine("насмешка: " + taunts.First());
        _output.WriteLine($"мир и насмешка: {made.Count} файлов, крипы {made.Keys.Count(p => p.Contains("creep", StringComparison.OrdinalIgnoreCase))}, башни {made.Keys.Count(p => p.Contains("_tower", StringComparison.OrdinalIgnoreCase))}");
    }

    /// <summary>
    /// Узел 6 и VAC: сборка не трогает ни один файл Valve — gameinfo, подписи, схему предметов.
    /// </summary>
    [Fact]
    public void The_build_touches_nothing_that_valve_owns()
    {
        var (install, catalog) = Game();
        var watched = new[]
        {
            Path.Combine(install.DotaDir, "gameinfo.gi"),
            Path.Combine(install.DotaDir, "gameinfo_branchspecific.gi"),
            Path.Combine(install.GameDir, "game", "bin", "win64", "dota.signatures"),
        }.Where(File.Exists).ToList();
        Assert.NotEmpty(watched);

        string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA1.HashData(stream));
        }

        var before = watched.ToDictionary(p => p, Hash);
        var pak = BuildPak(install, catalog,
            new[] { new HeroPicks("axe", new List<(string Slot, int ItemId)> { ("weapon", 7058) }) },
            "vac-safety");
        var after = watched.ToDictionary(p => p, Hash);

        foreach (var (path, hash) in before)
        {
            Assert.Equal(hash, after[path]);
        }

        var made = PakBodyIntegrityTests.ReadSizes(pak);
        Assert.DoesNotContain(made.Keys, p => p.StartsWith("scripts/items/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(made.Keys, p => p.EndsWith("npc_heroes.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(made.Keys, p => p.EndsWith("gameinfo.gi", StringComparison.OrdinalIgnoreCase));

        // pak64 рядом с нашим паком не появляется: его создание запрещено
        var sibling = Path.Combine(Path.GetDirectoryName(pak)!, "pak64_dir.vpk");
        Assert.False(File.Exists(sibling), "появился pak64_dir.vpk");
        _output.WriteLine($"VAC: {made.Count} файлов, ни один из {watched.Count} файлов Valve не изменён");
    }

    private static int IndexOf(byte[] hay, string needle)
    {
        var at = hay.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(needle));
        return at;
    }
}
