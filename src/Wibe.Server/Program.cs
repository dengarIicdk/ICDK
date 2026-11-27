using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wibe.Core.Dota;
using Wibe.Core.Mods;

namespace Wibe.Server;

/// <summary>
/// Локальный веб-сервер приложения: отдаёт страницу /www и JSON-API, ставит моды через Wibe.Core.
/// Слушает только 127.0.0.1 — снаружи недоступен.
/// </summary>
internal static class Program
{
    private static string _root = string.Empty;

    [STAThread]
    private static int Main(string[] args)
    {
        _root = Path.Combine(AppContext.BaseDirectory, "www");
        bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        // Голый сервер: так приложение проверяют автотесты и так его можно поднять без окна.
        if (Has("--headless-server"))
        {
            var headless = Launcher.ServerHost.Start();
            if (headless is null)
            {
                return 1;
            }

            headless.RunBlocking();
            headless.Dispose();
            return 0;
        }

        // Один экземпляр: второй запуск поднимает уже открытое окно и уходит.
        using var single = new System.Threading.Mutex(true, @"Local\ICDK_launcher", out var isFirst);
        if (!isFirst)
        {
            Launcher.LauncherForm.BringExistingToFront();
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Launcher.LauncherForm());
        return 0;
    }

    internal static async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var query = context.Request.QueryString;

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleApiAsync(context, path, query).ConfigureAwait(false);
                return;
            }

            await ServeStaticAsync(context, path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "errors.log"), DateTime.Now + " " + context.Request.Url + " " + ex + Environment.NewLine);
            }
            catch (IOException)
            {
            }

            if (ex is HttpListenerException or IOException or ObjectDisposedException)
            {
                return;
            }
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception)
            {
                // клиент уже отключился
            }
        }
    }

    private static async Task HandleApiAsync(HttpListenerContext context, string path, System.Collections.Specialized.NameValueCollection query)
    {
        var state = AppState.Instance;

        switch (path.ToLowerInvariant())
        {
            case "/api/ping":
                await WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
                return;

            case "/api/state":
                await WriteJsonAsync(context, state.Snapshot()).ConfigureAwait(false);
                return;

            case "/api/dota":
                await WriteJsonAsync(context, state.DotaInfo()).ConfigureAwait(false);
                return;

            case "/api/dota/path":
            {
                if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 405;
                    return;
                }

                var body = await ReadJsonAsync(context).ConfigureAwait(false);
                var dotaPath = body?["path"]?.GetValue<string>();
                await WriteJsonAsync(context, await state.SetDotaPathAsync(dotaPath).ConfigureAwait(false)).ConfigureAwait(false);
                return;
            }

            case "/api/dota/browse":
            {
                if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 405;
                    return;
                }

                var selected = NativeFolderPicker.Pick(state.Install?.GameDir);
                await WriteJsonAsync(context, new
                {
                    success = selected is not null,
                    path = selected,
                    message = selected is null ? "выбор папки отменён" : selected,
                }).ConfigureAwait(false);
                return;
            }

            case "/api/heroes":
                await state.WhenReady.ConfigureAwait(false);
                await WriteJsonAsync(context, state.HeroList()).ConfigureAwait(false);
                return;

            case "/api/loadout":
            {
                await state.WhenReady.ConfigureAwait(false);
                if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    var body = await ReadJsonAsync(context).ConfigureAwait(false);
                    var folder = body?["folder"]?.GetValue<string>() ?? string.Empty;
                    var picks = ParsePicks(body);
                    var styles = ParseStyles(body);
                    var rareTaunt = body?["rareTaunt"]?.GetValue<bool>() ?? false;
                    await WriteJsonAsync(context, state.SetHeroPicks(folder, picks, styles, rareTaunt)).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(context, state.Loadout.Info()).ConfigureAwait(false);
                return;
            }

            case "/api/loadout/clear":
            {
                await state.WhenReady.ConfigureAwait(false);
                await WriteJsonAsync(context, state.ClearLoadout()).ConfigureAwait(false);
                return;
            }

            case "/api/world":
            {
                await state.WhenReady.ConfigureAwait(false);
                if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    var body = await ReadJsonAsync(context).ConfigureAwait(false);
                    var selected = body?["selected"] ?? body;
                    var result = state.SetWorldSelection(
                        selected?["radiant_tower"]?.GetValue<string>(),
                        selected?["dire_tower"]?.GetValue<string>(),
                        selected?["terrain"]?.GetValue<string>(),
                        selected?["emblem"]?.GetValue<string>(),
                        selected?["radiant_creep"]?.GetValue<string>(),
                        selected?["dire_creep"]?.GetValue<string>());
                    await WriteJsonAsync(context, result).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(context, state.WorldState()).ConfigureAwait(false);
                return;
            }

            case "/api/compile":
            {
                await state.WhenReady.ConfigureAwait(false);
                await state.InstallGate.WaitAsync().ConfigureAwait(false);
                object compiled;
                try
                {
                    compiled = state.Compile();
                }
                finally
                {
                    state.InstallGate.Release();
                }

                await WriteJsonAsync(context, compiled).ConfigureAwait(false);
                return;
            }

            case "/api/decompile":
            {
                await state.WhenReady.ConfigureAwait(false);
                await state.InstallGate.WaitAsync().ConfigureAwait(false);
                object removed;
                try
                {
                    removed = state.DecompileAll();
                }
                finally
                {
                    state.InstallGate.Release();
                }

                await WriteJsonAsync(context, removed).ConfigureAwait(false);
                return;
            }

            case "/api/presets":
            {
                await state.WhenReady.ConfigureAwait(false);
                if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(context, state.PresetList()).ConfigureAwait(false);
                    return;
                }

                var body = await ReadJsonAsync(context).ConfigureAwait(false);
                var action = body?["action"]?.GetValue<string>() ?? "save";
                switch (action.ToLowerInvariant())
                {
                    case "apply":
                        await WriteJsonAsync(context, state.ApplyPreset(body?["id"]?.GetValue<string>() ?? string.Empty)).ConfigureAwait(false);
                        return;
                    case "delete":
                        await WriteJsonAsync(context, state.DeletePreset(body?["id"]?.GetValue<string>() ?? string.Empty)).ConfigureAwait(false);
                        return;
                    default:
                        await WriteJsonAsync(context, state.SavePreset(body?["name"]?.GetValue<string>() ?? string.Empty)).ConfigureAwait(false);
                        return;
                }
            }

            case "/api/hero":
            {
                await state.WhenReady.ConfigureAwait(false);
                var folder = query["folder"] ?? "axe";
                var set = state.Sets!.Get(folder);
                if (set is null)
                {
                    context.Response.StatusCode = 404;
                    await WriteJsonAsync(context, new { error = "герой не найден" }).ConfigureAwait(false);
                    return;
                }

                var compiled = state.InstalledFor(folder);
                var view = HeroFolderCatalog.Read(folder, query["view"]);
                var sections = view?.Sections
                    .Select(section => new HeroFolderCatalog.Section(section.Name, section.Cards.ToList()))
                    .ToList() ?? new List<HeroFolderCatalog.Section>();

                // Совсем без покупных карточек остались только Kez и Largo: в их
                // скриншотах один EQUIPPED. Показываем реальные базовые item_id
                // героя из каталога, чтобы страница не была пустой.
                if (sections.Sum(s => s.Cards.Count) == 0)
                {
                    var defaults = state.Catalog?.DefaultsFor(set.HeroKey) ?? Array.Empty<Wibe.Core.Econ.EconItem>();
                    var cards = defaults
                        .Where(item => item.Id > 0)
                        .Select(item => new HeroFolderCatalog.Card(
                            item.Id,
                            item.Name,
                            string.IsNullOrWhiteSpace(item.Slot) ? "misc" : item.Slot,
                            item.Rarity,
                            $"/api/crop?path=heroes/{Uri.EscapeDataString(set.HeroKey)}.jpg",
                            Prefab: "default_item"))
                        .ToList();
                    if (cards.Count > 0)
                    {
                        var first = sections.FirstOrDefault(s => string.Equals(s.Name, "Fullsets", StringComparison.OrdinalIgnoreCase))
                                    ?? sections.FirstOrDefault();
                        if (first is null)
                        {
                            sections.Add(new HeroFolderCatalog.Section("Fullsets", cards));
                        }
                        else
                        {
                            var index = sections.IndexOf(first);
                            sections[index] = first with { Cards = cards };
                        }
                    }
                }

                var folderSections = sections.Select(section => new
                {
                    name = section.Name,
                    cards = section.Cards.Select(card => new
                    {
                        id = card.Id,
                        name = card.Name,
                        title = card.Title ?? card.Name,
                        slot = card.Slot,
                        rarity = card.Rarity,
                        art = card.Art,
                        ability = card.Ability,
                        prefab = card.Prefab,
                        styles = card.Styles,
                    }),
                });
                await WriteJsonAsync(context, new
                {
                    set.HeroKey,
                    set.Folder,
                    set.Name,
                    set.BodyTarget,
                    set.ItemFolder,
                    set.Slots,
                    set.Shelves,
                    picks = state.Loadout.ForHero(set.Folder),
                    styles = state.Loadout.StylesForHero(set.Folder),
                    rareTaunt = state.Loadout.RareTauntFor(set.Folder),
                    installed = compiled,
                    catalogFolder = view?.Folder ?? set.Folder,
                    catalogName = view?.Name ?? set.Name,
                    isPersona = view?.IsPersona ?? false,
                    personaId = view?.PersonaId ?? 0,
                    folderSections,
                }).ConfigureAwait(false);
                return;
            }

            case var tex when tex.StartsWith("/api/mesh/tex/", StringComparison.OrdinalIgnoreCase):
            {
                var name = Uri.UnescapeDataString(tex.Substring("/api/mesh/tex/".Length));
                var bytes = GlbPreviewService.Texture(name);
                if (bytes is null)
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                context.Response.ContentType = "image/png";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                return;
            }

            case "/api/mesh/meta":
            {
                var id = int.TryParse(query["itemId"], out var parsed) ? parsed : 0;
                await WriteJsonAsync(context, GlbPreviewService.Meta(id, query["folder"])).ConfigureAwait(false);
                return;
            }

            case "/api/mesh/hero":
            {
                var mesh = await Task.Run(() => GlbPreviewService.Hero(query["folder"] ?? "axe")).ConfigureAwait(false);
                await WriteMeshAsync(context, mesh.Glb, mesh.Error).ConfigureAwait(false);
                return;
            }

            case "/api/mesh/item":
            {
                var id = int.TryParse(query["itemId"], out var parsed) ? parsed : 0;
                var style = int.TryParse(query["style"], out var st) ? st : 1;
                var mesh = await Task.Run(() => GlbPreviewService.Item(id, style)).ConfigureAwait(false);
                await WriteMeshAsync(context, mesh.Glb, mesh.Error).ConfigureAwait(false);
                return;
            }

            case "/api/vpk-icon":
            {
                // картинка шмотки из пака игры: тёплый кэш — чтение файла, холодный — одна распаковка
                var inv = (query["inv"] ?? string.Empty).Trim();
                if (inv.Length == 0 || inv.Contains("..", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                var png = await Task.Run(() => VpkIconService.Get(inv)).ConfigureAwait(false);
                if (png is null)
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                context.Response.ContentType = "image/png";
                context.Response.Headers["Cache-Control"] = "public, max-age=604800";
                await context.Response.OutputStream.WriteAsync(png).ConfigureAwait(false);
                return;
            }

            case "/api/crop":
            {
                var relative = (query["path"] ?? string.Empty).Replace('\\', '/').TrimStart('/');
                if (relative.Length == 0 || relative.Contains("..", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                var root = Path.Combine(AppContext.BaseDirectory, "crops");
                for (var at = new DirectoryInfo(AppContext.BaseDirectory); !Directory.Exists(root) && at?.Parent is not null; at = at.Parent)
                {
                    root = Path.Combine(at.Parent.FullName, "crops");
                }

                var file = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (file.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                {
                    var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                    context.Response.ContentType = Path.GetExtension(file).ToLowerInvariant() switch
                    {
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".webp" => "image/webp",
                        _ => "image/png",
                    };
                    context.Response.ContentLength64 = bytes.Length;
                    context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                    await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    return;
                }

                var slashed = "crops/" + relative.Replace('/', '\\');
                var name = slashed;
                var stream = typeof(Program).Assembly.GetManifestResourceStream(name)
                             ?? typeof(Program).Assembly.GetManifestResourceStream("crops/" + relative);
                if (stream is null)
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                context.Response.ContentType = name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png";
                context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                await stream.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
                return;
            }

            case "/api/img":
            {
                await state.WhenReady.ConfigureAwait(false);
                var inventory = query["inv"] ?? query["path"] ?? string.Empty;
                var name = query["name"];
                var hero = query["hero"];
                var crop = string.Equals(query["kind"], "crop", StringComparison.OrdinalIgnoreCase);

                var (bytes, type) = hero is { Length: > 0 }
                    ? await ImageProxy.GetHeroAsync(hero, crop).ConfigureAwait(false)
                    : await ImageProxy.GetAsync(inventory, name).ConfigureAwait(false);

                context.Response.ContentType = type;
                context.Response.Headers["X-Wibe-Image"] = type == ImageProxy.PlaceholderType ? "placeholder" : "real";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                return;
            }

            case "/api/images/prefetch":
            case "/api/icons":
            {
                await state.WhenReady.ConfigureAwait(false);
                if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    var started = state.Icons?.Start() ?? false;
                    await WriteJsonAsync(context, new
                    {
                        started,
                        message = started
                            ? "разбираю иконки из pak01_dir.vpk"
                            : "извлечение уже идёт",
                    }).ConfigureAwait(false);
                    return;
                }

                var status = state.Icons?.Status ?? new IconStatus();
                await WriteJsonAsync(context, new
                {
                    status.Running,
                    status.Done,
                    status.Total,
                    status.Indexed,
                    status.Phase,
                    status.Directory,
                    status.BuildId,
                    status.Message,
                    indexKeys = ImageStore.Index.Count,
                }).ConfigureAwait(false);
                return;
            }

            case "/api/mount":
                await state.WhenReady.ConfigureAwait(false);
                await WriteJsonAsync(context, state.MountInfo()).ConfigureAwait(false);
                return;

            case "/api/session":
                await state.WhenReady.ConfigureAwait(false);
                await WriteJsonAsync(context, state.SessionInfo()).ConfigureAwait(false);
                return;

            case "/api/build":
            {
                await state.WhenReady.ConfigureAwait(false);
                var build = await ReadJsonAsync(context).ConfigureAwait(false);
                var picks = ParsePicks(build);
                var folder = build?["folder"]?.GetValue<string>() ?? string.Empty;
                await state.InstallGate.WaitAsync().ConfigureAwait(false);
                object result;
                try
                {
                    result = state.BuildSession(folder, picks);
                }
                finally
                {
                    state.InstallGate.Release();
                }

                await WriteJsonAsync(context, result).ConfigureAwait(false);
                return;
            }

            case "/api/revert":
            {
                await state.WhenReady.ConfigureAwait(false);
                await state.InstallGate.WaitAsync().ConfigureAwait(false);
                object result;
                try
                {
                    result = state.RevertSession();
                }
                finally
                {
                    state.InstallGate.Release();
                }

                await WriteJsonAsync(context, result).ConfigureAwait(false);
                return;
            }

            case "/api/launch":
            {
                await state.WhenReady.ConfigureAwait(false);
                var body = await ReadJsonAsync(context).ConfigureAwait(false);
                var dry = body?["dry"]?.GetValue<bool>() ?? false;
                var conDebug = body?["condebug"]?.GetValue<bool>() ?? false;
                await WriteJsonAsync(context, state.LaunchGame(dry, conDebug)).ConfigureAwait(false);
                return;
            }

            case "/api/install":
            {
                await state.WhenReady.ConfigureAwait(false);
                var body = await ReadJsonAsync(context).ConfigureAwait(false);
                if (body is null || !state.Ready)
                {
                    await WriteJsonAsync(context, new { success = false, message = state.Error ?? "приложение не готово" }).ConfigureAwait(false);
                    return;
                }

                var folder = body["folder"]?.GetValue<string>() ?? string.Empty;
                var picks = new List<(string Slot, int ItemId)>();
                if (body["picks"] is JsonArray array)
                {
                    foreach (var node in array)
                    {
                        var slot = node?["slot"]?.GetValue<string>();
                        var itemId = node?["itemId"]?.GetValue<int>() ?? 0;
                        if (slot is not null && itemId != 0)
                        {
                            picks.Add((slot, itemId));
                        }
                    }
                }

                if (!await state.InstallGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                {
                    await WriteJsonAsync(context, new { success = false, message = "другая установка ещё идёт" }).ConfigureAwait(false);
                    return;
                }

                object result;
                try
                {
                    result = state.InstallPicks(folder, picks);
                }
                finally
                {
                    state.InstallGate.Release();
                }

                await WriteJsonAsync(context, result).ConfigureAwait(false);
                return;
            }

            case "/api/uninstall":
            {
                await state.WhenReady.ConfigureAwait(false);
                var body = await ReadJsonAsync(context).ConfigureAwait(false);
                var folder = body?["folder"]?.GetValue<string>() ?? string.Empty;

                await state.InstallGate.WaitAsync().ConfigureAwait(false);
                object result;
                try
                {
                    result = state.Uninstall(folder);
                }
                finally
                {
                    state.InstallGate.Release();
                }

                await WriteJsonAsync(context, result).ConfigureAwait(false);
                return;
            }

            case "/api/mods":
            {
                var mods = ModJournal.Load().Mods.Select(m => new
                {
                    m.Slot,
                    m.Title,
                    m.Kind,
                    m.HeroId,
                    files = m.Files?.Count ?? 1,
                    m.InstalledAt,
                }).ToList();
                await WriteJsonAsync(context, new { mods }).ConfigureAwait(false);
                return;
            }

            case "/api/quit":
                await WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
                _ = Task.Run(() =>
                {
                    Thread.Sleep(300);
                    Environment.Exit(0);
                });
                return;

            default:
                context.Response.StatusCode = 404;
                return;
        }
    }

    private static Dictionary<string, int> ParseStyles(JsonNode? body)
    {
        var styles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (body?["styles"] is JsonArray array)
        {
            foreach (var node in array)
            {
                var slot = node?["slot"]?.GetValue<string>();
                var index = node?["index"]?.GetValue<int>() ?? 0;
                if (slot is not null && index > 0)
                {
                    styles[slot] = index;
                }
            }
        }

        return styles;
    }

    private static List<(string Slot, int ItemId)> ParsePicks(JsonNode? body)
    {
        var picks = new List<(string, int)>();
        if (body?["picks"] is JsonArray array)
        {
            foreach (var node in array)
            {
                var slot = node?["slot"]?.GetValue<string>();
                var id = node?["itemId"]?.GetValue<int>() ?? 0;
                if (slot is not null && id != 0)
                {
                    picks.Add((slot, id));
                }
            }
        }

        return picks;
    }

    private static async Task ServeStaticAsync(HttpListenerContext context, string path)
    {
        if (path == "/" || path.Length == 0)
        {
            path = "/index.html";
        }

        if (path.StartsWith("/static/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["/static".Length..];
        }

        var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            // www может быть вшит в exe — тогда отдаём из ресурсов.
            if (await TryServeEmbeddedAsync(context, relative).ConfigureAwait(false))
            {
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.ContentType = "text/plain; charset=utf-8";
            var message = Encoding.UTF8.GetBytes("нет файла " + path);
            await context.Response.OutputStream.WriteAsync(message).ConfigureAwait(false);
            return;
        }

        var type = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            ".json" => "application/json; charset=utf-8",
            _ => "application/octet-stream",
        };

        await WriteFileAsync(context, full, type, cache: false).ConfigureAwait(false);
    }

    private static async Task<bool> TryServeEmbeddedAsync(HttpListenerContext context, string relative)
    {
        var assembly = typeof(Program).Assembly;
        var flat = "www." + relative.Replace(Path.DirectorySeparatorChar, '.').Replace('/', '.');
        var raw = "www." + relative.Replace('/', '\\');
        await using var stream = assembly.GetManifestResourceStream(flat)
                               ?? assembly.GetManifestResourceStream(raw);
        if (stream is null)
        {
            return false;
        }

        var type = Path.GetExtension(relative).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".json" => "application/json; charset=utf-8",
            _ => "application/octet-stream",
        };

        context.Response.ContentType = type;
        context.Response.Headers["Cache-Control"] = "no-store";
        await stream.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
        return true;
    }

    private static async Task WriteFileAsync(HttpListenerContext context, string file, string contentType, bool cache)
    {
        var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.Headers["Cache-Control"] = cache ? "public, max-age=604800" : "no-store";
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>Отдача бинарного glTF или понятной ошибки для 3D-подиума.</summary>
    private static async Task WriteMeshAsync(HttpListenerContext context, byte[]? glb, string? error)
    {
        if (glb is null)
        {
            context.Response.StatusCode = 404;
            await WriteJsonAsync(context, new { error = error ?? "модель не экспортирована" }).ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = "model/gltf-binary";
        context.Response.Headers["Cache-Control"] = "public, max-age=604800";
        await context.Response.OutputStream.WriteAsync(glb).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, AppState.Json);
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpListenerContext context)
    {
        try
        {
            return await JsonNode.ParseAsync(context.Request.InputStream).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
