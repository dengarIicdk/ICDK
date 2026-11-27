//! The adapter WIBE FITTING ROOM calls: the item builder's own modules, fed from a JSON manifest
//! instead of the Tauri owner/library. Nothing of the build is reimplemented here; this file only
//! turns picks into library-shaped records and writes the resulting VPK.

use std::cell::RefCell;
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use serde::Deserialize;
use serde_json::{json, Value};

use crate::arcana_base_store::{BaseStore, Fetch, Got};
use crate::arcana_bases::BaseSource;
use crate::item_files::item_files;
use crate::schema::to_utf8;
use crate::schema_build::TableBuild;
use crate::vpk::{self, Entry};

#[derive(Debug, Deserialize)]
pub struct Manifest {
    pub game: String,
    pub out: String,
    #[serde(default)]
    pub bases_dir: Option<String>,
    #[serde(default)]
    pub picks: Vec<Pick>,
    #[serde(default)]
    pub world: Option<World>,
    #[serde(default)]
    pub hero_pedestals: Option<BTreeMap<String, String>>,
}

#[derive(Debug, Default, Deserialize)]
pub struct World {
    #[serde(default)]
    pub radiant_tower: Option<String>,
    #[serde(default)]
    pub dire_tower: Option<String>,
    #[serde(default)]
    pub emblem: Option<String>,
    #[serde(default)]
    pub radiant_creep: Option<String>,
    #[serde(default)]
    pub dire_creep: Option<String>,
}

#[derive(Debug, Deserialize)]
pub struct Pick {
    pub hero: String,
    #[serde(default)]
    pub slot: Option<String>,
    pub item_id: String,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub effect_id: Option<String>,
    #[serde(default)]
    pub rare_taunt: bool,
}

#[derive(Debug, serde::Serialize)]
pub struct Report {
    pub ok: bool,
    pub vpk: String,
    pub bytes: usize,
    pub files: usize,
    pub picks: usize,
    pub left: Vec<Value>,
}

struct StoreSource<'a> {
    store: RefCell<BaseStore>,
    fetch: Fetch<'a>,
    now: f64,
}

impl BaseSource for StoreSource<'_> {
    fn entries(&self, id: &str) -> Option<Vec<Entry>> {
        self.store.borrow_mut().entries(id)
    }

    fn want(&self, id: &str) {
        self.store.borrow_mut().want(id, self.fetch, self.now, &mut |_| {});
    }
}

fn item_of<'a>(items: &'a [crate::schema::SchemaItem], id: &str) -> Option<&'a crate::schema::SchemaItem> {
    items.iter().find(|i| i.id == id)
}

/// The loadout-only particle system a hero's own body creates, where its body cannot be rewritten.
/// The records the builder would have in the library: one enabled pick per selected item, in its
/// item's own slot (the persona selector included).
fn records(manifest: &Manifest, vanilla: &[u8], log: &mut dyn FnMut(String)) -> Result<Vec<Value>, String> {
    let items = crate::schema::items_of(vanilla).map_err(|e| e.to_string())?;
    let mut out = Vec::new();
    for p in &manifest.picks {
        let item = item_of(&items, &p.item_id);
        let slot = p
            .slot
            .as_deref()
            .filter(|s| !s.is_empty())
            .map(str::to_string)
            .or_else(|| item.map(crate::schema::inferred_item_slot).filter(|s| !s.is_empty()))
            .unwrap_or_else(|| "body".into());
        let name = p
            .name
            .as_deref()
            .filter(|n| !n.is_empty())
            .map(str::to_string)
            .or_else(|| item.map(|i| to_utf8(&i.name)))
            .unwrap_or_else(|| p.item_id.clone());
        let mut rec = json!({
            "name": name,
            "categoryId": "cosmetic",
            "styleLabel": null,
            "fileRef": null,
            "preview": null,
            "files": [],
            "enabled": true,
            "slot": format!("item:{}:{}", p.hero, slot),
            "itemId": p.item_id,
        });
        if let Some(effect) = p.effect_id.as_deref().filter(|e| !e.is_empty()) {
            rec["effectId"] = json!(effect);
        }
        if p.rare_taunt {
            rec["tauntRare"] = json!(true);
        }
        log(format!("pick {} {} -> {}", p.hero, p.item_id, slot));
        out.push(rec);
    }
    Ok(out)
}

fn fetch_url(url: &str) -> Result<Got, String> {
    let agent: ureq::Agent = ureq::Agent::config_builder()
        .http_status_as_error(false)
        .tls_config(crate::net_fetch::tls())
        .timeout_connect(Some(std::time::Duration::from_secs(10)))
        .timeout_recv_response(Some(std::time::Duration::from_secs(10)))
        .timeout_recv_body(Some(std::time::Duration::from_secs(120)))
        .build()
        .into();
    let mut res = agent.get(url).call().map_err(|e| e.to_string())?;
    let status = res.status().as_u16();
    let mut body = Vec::new();
    std::io::Read::read_to_end(&mut res.body_mut().as_reader(), &mut body).map_err(|e| e.to_string())?;
    Ok(Got { status, body })
}


/// Valve's coordinate files the builder must never touch. `dota.signatures` changes with every
/// game update, so it is only required to exist; the build snapshot still proves it was untouched.
const SIGNED: [(&str, &str); 2] = [
    ("game/dota/gameinfo.gi", "a0dc87acc7a659eedd1f9575eee2bd3883e8f201"),
    ("game/dota/gameinfo_branchspecific.gi", "224b6f879d00d419b6a10fe07b4ddb3d827fd31f"),
];

fn sha1_of(path: &Path) -> Option<String> {
    use sha1::{Digest, Sha1};
    let bytes = std::fs::read(path).ok()?;
    let mut h = Sha1::new();
    h.update(&bytes);
    Some(hex::encode(h.finalize()))
}

/// Refuse to build beside a client that is not Valve's: the signed files must hash as shipped, and
/// no other program's pak64 may be sitting in a language folder. Reads only; nothing is repaired.
fn client_is_clean(install: &Path) -> Result<(), String> {
    for (rel, want) in SIGNED {
        let path = install.join(rel);
        match sha1_of(&path) {
            Some(got) if got == want => {}
            Some(got) => return Err(format!("{rel} is not Valve's: sha1 {got}")),
            None => return Err(format!("{rel} is missing")),
        }
    }
    if !install.join("game/bin/win64/dota.signatures").is_file() {
        return Err("game/bin/win64/dota.signatures is missing".to_string());
    }

    let game = install.join("game");
    let Ok(dirs) = std::fs::read_dir(&game) else { return Ok(()) };
    for entry in dirs.flatten() {
        let dir = entry.path();
        if dir.is_dir() && dir.join("pak64_dir.vpk").exists() {
            return Err(format!("another tool's pak64 is present: {}", dir.join("pak64_dir.vpk").display()));
        }
    }
    Ok(())
}

/// Build the language-folder pak from the manifest. Progress is (done, total) builder picks.
pub fn run(
    manifest_json: &str,
    progress: &mut dyn FnMut(usize, usize),
    log: &mut dyn FnMut(String),
) -> Result<Report, String> {
    let manifest: Manifest = serde_json::from_str(manifest_json).map_err(|e| format!("manifest: {e}"))?;
    // the builder's `game` is the game folder (<install>/game), while the manifest names the install
    let install = PathBuf::from(&manifest.game);
    client_is_clean(&install)?;
    let game = install.join("game");
    let pak01 = game.join("dota").join("pak01_dir.vpk");
    let index = vpk::Index::open(&pak01).map_err(|e| format!("pak01: {e}"))?;
    let read = |p: &str| index.read(p).ok().flatten();
    let vanilla = read(crate::schema::SCHEMA_REL).ok_or_else(|| "the game has no item table".to_string())?;
    log(format!("pak01: {} entries", index.size()));
    let recs = records(&manifest, &vanilla, log)?;
    log(format!("picks: {}", recs.len()));

    let fetch: Fetch = &|url: &str, _small: bool| fetch_url(url);
    let mut source = manifest.bases_dir.as_deref().filter(|d| !d.is_empty()).map(|d| StoreSource {
        store: RefCell::new(BaseStore::new(Path::new(d))),
        fetch,
        now: 0.0,
    });

    let needed: Vec<String> = crate::arcana_bases::choose_bases(&TableBuild::picks(&recs), crate::data::arcana_bases())
        .into_iter()
        .map(|b| b.id.clone())
        .collect();
    let ready_before: std::collections::HashSet<String> = source
        .as_ref()
        .map(|s| s.store.borrow().ready().keys().cloned().collect())
        .unwrap_or_default();

    let mut builder = TableBuild::new();
    let mut built = builder.build(
        &vanilla,
        &game,
        &recs,
        source.as_mut().map(|s| s as &dyn BaseSource),
        progress,
        log,
    )?;
    // the bases are fetched during the build: when one that was missing is now here, build again
    // with it instead of shipping the game's fallback for that persona/arcana
    if let Some(src) = source.as_ref() {
        let ready = src.store.borrow().ready();
        let fresh: Vec<&String> = needed.iter().filter(|id| ready.contains_key(*id) && !ready_before.contains(*id)).collect();
        if !fresh.is_empty() {
            log(format!("arcana bases fetched: {}; building again", fresh.iter().map(|s| s.as_str()).collect::<Vec<_>>().join(", ")));
            drop(ready);
            builder.forget();
            built = builder.build(
                &vanilla,
                &game,
                &recs,
                Some(src as &dyn BaseSource),
                progress,
                log,
            )?;
        }
    }
    log(format!("table patches: {}", built.list.len()));
    let made = item_files(&built.list, &built.beneath, &read, Some(&built.files_shared))?;
    for l in &made.left {
        log(format!("without the table: {} has not got {}", l.source, l.what.join(", ")));
    }
    let mut entries = made.entries;
    // A taunt is a sequence of the hero's own body: the stock table, which is not shipped, still
    // asks for the stock sequence, so the body's stock sequence names are pointed at the picked
    // dance. This runs on the base repository's body already in the pak.
    let taunt_picks: Vec<(String, bool)> = manifest
        .picks
        .iter()
        .filter(|p| p.slot.as_deref().is_some_and(|s| s.rsplit(':').next() == Some("taunt")))
        .map(|p| (p.item_id.clone(), p.rare_taunt))
        .collect();
    if !taunt_picks.is_empty() {
        crate::item_builder_taunt_anim::apply_for_picks(&mut entries, &vanilla, &taunt_picks, &read, |m| log(m));
    }
    let silenced = crate::item_builder_orphan_fx::silence_loadout_particles(&mut entries, &read);
    if !silenced.is_empty() {
        log(format!("orphaned loadout effects silenced: {}", silenced.join(", ")));
    }
    // A combat ambient effect of a set or an arcana can bind a control point to an attachment the
    // model now under its slot does not have (a mix of an arcana body and another set's part keeps
    // neither set's points). Source 2 drops such a point at the world origin, in the mid river, so
    // every particle asking for an attachment no model in the pak brings is made one that draws
    // nothing. This runs once every model and every particle is in the pak, taunt patch included.
    let orphaned = crate::item_builder_orphan_fx::silence_orphan_attached_particles(&mut entries, &vanilla, &read);
    if !orphaned.is_empty() {
        log(format!(
            "orphaned attachment effects silenced: {}",
            orphaned.iter().map(|(p, a)| format!("{p} (no {a})")).collect::<Vec<_>>().join(", ")
        ));
    }
    // The game reads its stock table scripts itself. `scripts/npc/portraits.txt` is the one
    // exception: portraits of a replaced model are keyed by the stock path and only exist in the
    // pak. Everything the item table owns stays out.
    let scripts_before = entries.len();
    entries.retain(|e| {
        let path = e.path().to_ascii_lowercase();
        !path.starts_with("scripts/") || path == "scripts/npc/portraits.txt"
    });
    if entries.len() != scripts_before {
        log(format!("pak: {} table scripts left out", scripts_before - entries.len()));
    }
    // a separate last pass for the models the item builder leaves as empty persona slots and for Io
    let post_picks: Vec<crate::persona_post::Pick> = manifest
        .picks
        .iter()
        .map(|p| crate::persona_post::Pick {
            hero: p.hero.clone(),
            slot: p.slot.clone().unwrap_or_default(),
            item_id: p.item_id.clone(),
        })
        .collect();
    crate::persona_post::apply(&post_picks, &mut entries, &index, &vanilla, log)?;
    let portrait_picks: Vec<(String, String)> =
        manifest.picks.iter().map(|p| (p.hero.clone(), p.item_id.clone())).collect();
    crate::item_portraits::apply_item_portraits(&mut entries, &index, &vanilla, &portrait_picks, log)?;
    crate::arcana_attack::audit_razor(&entries, log);
    if let Some(world) = manifest.world.as_ref() {
        // towers go through the same module the app builds a tower look with
        if world.radiant_tower.is_some() || world.dire_tower.is_some() {
            let towers = crate::tower_looks::build_towers(
                &pak01,
                &vanilla,
                world.radiant_tower.as_deref().filter(|s| !s.is_empty()),
                world.dire_tower.as_deref().filter(|s| !s.is_empty()),
            )?;
            log(format!(
                "world towers: {} ({} files, {} missing, {} skipped)",
                towers.names.join(" + "),
                towers.files,
                towers.missing.len(),
                towers.skipped.len()
            ));
            let tmp = std::env::temp_dir().join(format!("wibe_towers_{}.vpk", std::process::id()));
            std::fs::write(&tmp, &towers.vpk).map_err(|e| e.to_string())?;
            let tower_index = vpk::Index::open(&tmp).map_err(|e| e.to_string())?;
            for path in tower_index.paths().map(str::to_string).collect::<Vec<_>>() {
                if let Some(data) = tower_index.read(&path).map_err(|e| e.to_string())? {
                    entries.retain(|e| !e.path().eq_ignore_ascii_case(&path));
                    entries.push(vpk::entry_at(&path, std::sync::Arc::from(data)));
                }
            }
            let _ = std::fs::remove_file(&tmp);
            // the portrait frame of a tower is framed by the stock model's key: the look's camera
            // goes there, else the frame shows a corner of a much bigger body
            let mut swaps: Vec<(String, String)> = Vec::new();
            for (side, id) in [
                ("radiant", world.radiant_tower.as_deref()),
                ("dire", world.dire_tower.as_deref()),
            ] {
                let Some(id) = id.filter(|s| !s.is_empty()) else { continue };
                if let Some(model) = crate::tower_looks::chosen_model(&vanilla, id, side) {
                    let to = crate::tower_looks::default_tower(side).to_string();
                    if model != to {
                        swaps.push((model, to));
                    }
                }
            }
            crate::item_portraits::patch_tower_portraits(&mut entries, &read, &swaps);
        }
        // creeps and the emblem are plain file aliases, so the world module does them together
        let creep_or_emblem = world.emblem.as_deref().is_some_and(|s| !s.is_empty())
            || world.radiant_creep.as_deref().is_some_and(|s| !s.is_empty())
            || world.dire_creep.as_deref().is_some_and(|s| !s.is_empty());
        if creep_or_emblem {
            let picks = crate::world::WorldPicks {
                radiant_tower: None,
                dire_tower: None,
                emblem: world.emblem.clone().filter(|s| !s.is_empty()),
                radiant_creep: world.radiant_creep.clone().filter(|s| !s.is_empty()),
                dire_creep: world.dire_creep.clone().filter(|s| !s.is_empty()),
            };
            let extra = crate::world::build(&index, &vanilla, &picks, log)?;
            for entry in extra {
                entries.retain(|e| e.path() != entry.path());
                entries.push(entry);
            }
        }
    }

    // The stock paths the other picked items explicitly replace. Another hero's arcana owns
    // these; Rubick's stolen-spell cleanup must not take them away from that hero.
    let mut picked_paths = std::collections::HashSet::new();
    for pick in manifest.picks.iter().filter(|p| p.item_id != "12451") {
        if let Ok(Some(visuals)) = crate::item_builder::item_visuals::item_visuals(&vanilla, &pick.item_id) {
            for m in &visuals.modifiers {
                if m.asset.is_empty() {
                    continue;
                }
                let lower = m.ty.to_lowercase();
                if lower.contains("particle") || lower.contains("model") || lower.contains("entity_model") {
                    picked_paths.insert(crate::item_builder_assets::compiled_asset_path(&m.asset).to_lowercase());
                }
            }
        }
    }

    // Rubick's arcana rewrites the particles of spells he steals. Those stock paths belong to
    // other heroes, so mounting them globally would give every Invoker Rubick's cube meteor.
    // Put back only the paths this item explicitly replaces, never every hero model.
    if manifest.picks.iter().any(|p| p.item_id == "12451") {
        let mut stolen = std::collections::HashSet::new();
        if let Ok(Some(visuals)) = crate::item_builder::item_visuals::item_visuals(&vanilla, "12451") {
            for m in &visuals.modifiers {
                if m.asset.is_empty() {
                    continue;
                }
                let lower = m.ty.to_lowercase();
                if lower.contains("particle") || lower.contains("model") || lower.contains("entity_model") {
                    stolen.insert(crate::item_builder_assets::compiled_asset_path(&m.asset).to_lowercase());
                }
            }
        }
        let mut restored = 0usize;
        entries.retain(|e| {
            let path = e.path().to_lowercase();
            if !stolen.contains(&path) || picked_paths.contains(&path) {
                return true;
            }
            match index.read(&path).ok().flatten() {
                Some(stock) if stock.as_slice() != e.data.as_ref() => {
                    restored += 1;
                    false
                }
                _ => true,
            }
        });
        if restored > 0 {
            log(format!("rubick: {restored} stolen-spell effects left to their own heroes"));
        }
    }

    // Razor's arcana attack. The base repository's particle was compiled inside the
    // `dota_addons/voidstorm_asylum` addon, so its search path cannot resolve in the game.
    // Use Valve's own arcana particle under the stock attack path; all of its dependencies
    // (the whip swoosh and sparks) are already in pak01.
    if manifest.picks.iter().any(|p| p.item_id == "23095") {
        let arc = "particles/econ/items/razor/razor_arcana/razor_arcana_base_attack.vpcf_c";
        let base = "particles/units/heroes/hero_razor/razor_base_attack.vpcf_c";
        if let Ok(Some(data)) = index.read(arc) {
            entries.retain(|e| !e.path().eq_ignore_ascii_case(base));
            entries.push(crate::vpk::entry_at(base, data.into()));
            log("razor attack: stock arcana particle under the base attack path".to_string());
        }
        let mut restored = 0usize;
        entries.retain(|e| {
            let path = e.path().to_lowercase();
            if !path.starts_with("particles/econ/items/razor/razor_arcana/") {
                return true;
            }
            match index.read(&path).ok().flatten() {
                Some(stock) if stock.as_slice() != e.data.as_ref() => {
                    restored += 1;
                    false
                }
                _ => true,
            }
        });
        if restored > 0 {
            log(format!("razor attack: {restored} addon-path particles left to the game"));
        }
    }

    // Some arcana effects are not the hero's own: a deny, a blink, a stun or an item effect is a
    // shared stock particle every player's client draws. A file cannot say whose arcana it is, so
    // an override there would give the whole match that one hero's effect (Spectre's deny on every
    // hero). Leave those shared stock particles to the game.
    const SHARED: [&str; 6] = [
        "particles/msg_fx/",
        "particles/generic_gameplay/",
        "particles/items_fx/",
        "particles/items2_fx/",
        "particles/items3_fx/",
        "particles/status_fx/",
    ];
    let mut shared_left = 0usize;
    entries.retain(|e| {
        let path = e.path().to_lowercase();
        if !SHARED.iter().any(|pre| path.starts_with(pre)) {
            return true;
        }
        match index.read(&path).ok().flatten() {
            Some(stock) if stock.as_slice() != e.data.as_ref() => {
                shared_left += 1;
                false
            }
            _ => true,
        }
    });
    if shared_left > 0 {
        log(format!("shared: {shared_left} global effects left to the game"));
    }

    // Skywrath Mage's arcana ambient sits in the base repository under its own econ path; the
    // ultimate asks for the stock one. Put the arcana bytes where the spell looks.
    if manifest.picks.iter().any(|p| p.item_id == "18539") {
        for (arc, base) in [
            (
                "particles/econ/items/skywrath_mage/skywrath_arcana/skywrath_arcana_mystic_flare_ambient.vpcf_c",
                "particles/units/heroes/hero_skywrath_mage/skywrath_mage_mystic_flare_ambient.vpcf_c",
            ),
            (
                "particles/econ/items/skywrath_mage/skywrath_arcana/skywrath_arcana_mystic_flare_hit_ambient.vpcf_c",
                "particles/units/heroes/hero_skywrath_mage/skywrath_mage_mystic_flare_hit_ambient.vpcf_c",
            ),
        ] {
            let own = entries
                .iter()
                .rev()
                .find(|e| e.path().eq_ignore_ascii_case(arc))
                .map(|e| e.data.to_vec());
            let Some(data) = own.or_else(|| index.read(arc).ok().flatten()) else { continue };
            entries.retain(|e| !e.path().eq_ignore_ascii_case(base));
            entries.push(crate::vpk::entry_at(base, data.into()));
        }
        log("skywrath arcana: stock mystic flare paths use the arcana ambient".to_string());
    }

    // Phantom Assassin's persona voice is a rule set of its own, and Exile Unveiled has no stock
    // slot to write: rewrite her voice file straight from the rule criterion.
    if manifest.picks.iter().any(|p| p.item_id == "22723") {
        let cue = crate::item_sounds::VoiceCue {
            hero: "npc_dota_hero_phantom_assassin".to_string(),
            key: "customresponse".to_string(),
            value: "pa_asan_vo".to_string(),
            whole: true,
        };
        let files = crate::item_sounds::sound_entries(&index, &[], std::slice::from_ref(&cue));
        let count = files.len();
        for (path, data) in files {
            entries.retain(|e| !e.path().eq_ignore_ascii_case(&path));
            entries.push(crate::vpk::entry_at(&path, data.into()));
        }
        if count > 0 {
            log(format!("phantom assassin: persona voice in {count} soundevents files"));
        }
    }

    // A persona base ships tiny placeholder models at the paths of the parts it hides. Where the
    // player picked such a part, the real model from pak01 belongs there instead.
    let mut own_models = std::collections::HashSet::new();
    for pick in &manifest.picks {
        if let Ok(Some(visuals)) = crate::item_builder::item_visuals::item_visuals(&vanilla, &pick.item_id) {
            for m in &visuals.modifiers {
                let ty = m.ty.to_lowercase();
                if m.asset.is_empty() || !(ty == "model" || ty == "hero_model_change") {
                    continue;
                }
                let path = crate::item_builder_assets::compiled_asset_path(&m.asset).to_lowercase();
                if path.starts_with("models/items/") {
                    own_models.insert(path);
                }
            }
        }
    }
    // These persona parts are worn by the base itself, so no pick names their model.
    if manifest.picks.iter().any(|p| p.item_id == "31367" || p.item_id == "36214") {
        for path in [
            "models/items/axe/axe_carnival/axe_carnival_weapon.vmdl_c",
            "models/items/axe/axe_carnival/axe_carnival_back.vmdl_c",
            "models/items/bristleback/bristlebot/bristlebot_weapon.vmdl_c",
            "models/items/bristleback/bristlebot/bristlebot_back.vmdl_c",
        ] {
            own_models.insert(path.to_string());
        }
    }
    let mut models_fixed = 0usize;
    for path in own_models {
        let Some(stock) = index.read(&path).ok().flatten() else { continue };
        if stock.len() < 8192 {
            continue;
        }
        let tiny = entries
            .iter()
            .rev()
            .find(|e| e.path().eq_ignore_ascii_case(&path))
            .is_some_and(|e| e.data.len() < 8192);
        if tiny {
            entries.retain(|e| !e.path().eq_ignore_ascii_case(&path));
            entries.push(crate::vpk::entry_at(&path, stock.into()));
            models_fixed += 1;
        }
    }
    if models_fixed > 0 {
        log(format!("persona: {models_fixed} picked item models put back from pak01"));
    }

    // The one allowed personal pedestal: Wraith King's arcana throne. Anything else in the
    // manifest is ignored, so no global stand ever appears under other heroes.
    if let Some(chosen) = manifest.hero_pedestals.as_ref().filter(|m| !m.is_empty()) {
        let mut map = indexmap::IndexMap::new();
        for (hero, model) in chosen {
            if hero.eq_ignore_ascii_case("skeleton_king") || hero.eq_ignore_ascii_case("npc_dota_hero_skeleton_king") {
                map.insert("skeleton_king".to_string(), model.clone());
            }
        }
        if !map.is_empty() {
            let current = |path: &str| {
                entries.iter().rev().find(|e| e.path().eq_ignore_ascii_case(path)).map(|e| e.data.to_vec())
            };
            let read = |path: &str| index.read(path).ok().flatten();
            let made = crate::item_builder::hero_pedestals::pedestal_files(
                &map,
                &indexmap::IndexMap::new(),
                &current,
                &read,
                false,
                log,
            );
            let paths: std::collections::HashSet<String> =
                made.entries.iter().map(|e| e.path().to_lowercase()).collect();
            entries.retain(|e| !paths.contains(&e.path().to_lowercase()));
            entries.extend(made.entries);
            // the throne model itself, so the particle can draw it without the arcana equipped
            for model in map.values() {
                let path = format!("{model}_c");
                if let Some(data) = read(&path) {
                    entries.retain(|e| !e.path().eq_ignore_ascii_case(&path));
                    entries.push(crate::vpk::entry_at(&path, data.into()));
                }
            }
            log(format!("hero pedestals: {}", made.heroes.join(" + ")));
        }
    }

    let vpk_bytes = vpk::build(&entries);
    let out = PathBuf::from(&manifest.out);
    if let Some(dir) = out.parent() {
        std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    }
    let tmp = PathBuf::from(format!("{}.part", out.display()));
    std::fs::write(&tmp, &vpk_bytes).map_err(|e| e.to_string())?;
    if let Err(e) = client_is_clean(&install) {
        let _ = std::fs::remove_file(&tmp);
        let _ = std::fs::remove_file(&out);
        return Err(e);
    }
    std::fs::rename(&tmp, &out).map_err(|e| e.to_string())?;
    log(format!("wrote {} ({} files, {} bytes)", out.display(), entries.len(), vpk_bytes.len()));

    Ok(Report {
        ok: true,
        vpk: out.to_string_lossy().into_owned(),
        bytes: vpk_bytes.len(),
        files: entries.len(),
        picks: recs.len(),
        left: made
            .left
            .iter()
            .map(|l| json!({"source": l.source, "what": l.what}))
            .collect(),
    })
}

/// Kept so a caller can check the base paths a build would ask for without building.
pub fn bases_of(manifest_json: &str) -> Result<Vec<String>, String> {
    let manifest: Manifest = serde_json::from_str(manifest_json).map_err(|e| format!("manifest: {e}"))?;
    let pak01 = PathBuf::from(&manifest.game).join("game").join("dota").join("pak01_dir.vpk");
    let index = vpk::Index::open(&pak01).map_err(|e| format!("pak01: {e}"))?;
    let vanilla = index
        .read(crate::schema::SCHEMA_REL)
        .ok()
        .flatten()
        .ok_or_else(|| "the game has no item table".to_string())?;
    let recs = records(&manifest, &vanilla, &mut |_| {})?;
    let picks = TableBuild::picks(&recs);
    Ok(crate::arcana_bases::choose_bases(&picks, crate::data::arcana_bases())
        .into_iter()
        .map(|b| b.id.clone())
        .collect())
}
