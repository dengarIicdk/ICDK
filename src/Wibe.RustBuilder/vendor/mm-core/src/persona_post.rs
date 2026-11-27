//! A last, separate pass over the files arcanas and personas already produced.
//!
//! The item builder ships an empty model for a persona's own slot: the persona's base body is
//! expected to carry that part. On this game build several of those bases carry an empty 4 KB
//! model instead, so the dressed persona arrives bald. This pass takes the persona item's real
//! compiled model from pak01 and puts it under the stock part path the empty model occupied. It
//! also gives Io's cube the stock wisp body path and swaps the arcana's effects in.
//!
//! It reads and writes no scripts, no gameinfo and no table; every path is a model or particle.

use std::collections::{HashMap, HashSet};

use crate::item_builder::item_builder_assets::compiled_asset_path;
use crate::item_builder::item_builder_slots::item_heroes;
use crate::item_builder::item_visuals::{item_visuals, AssetModifier};
use crate::schema::{items_of, SchemaItem};
use crate::vpk::{self, Entry, Index};

const EMPTY_MODEL: usize = 8 * 1024;
const EMPTY_PATH: &str = "models/heroes/tiny/tiny_01/tiny_empty.vmdl";
const NULL_PARTICLE: &str = "particles/error/null.vpcf";

/// One manifest pick: hero, slot and item id, exactly as the caller passed them.
#[derive(Debug, Clone)]
pub struct Pick {
    pub hero: String,
    pub slot: String,
    pub item_id: String,
}

struct Target {
    path: String,
    size: usize,
}

pub fn apply(
    picks: &[Pick],
    entries: &mut Vec<Entry>,
    index: &Index,
    vanilla: &[u8],
    log: &mut dyn FnMut(String),
) -> Result<(), String> {
    let items = items_of(vanilla).map_err(|e| e.to_string())?;
    let mut by_hero: HashMap<String, Vec<&Pick>> = HashMap::new();
    for p in picks {
        let hero = p.hero.trim().trim_start_matches("npc_dota_hero_").to_lowercase();
        by_hero.entry(hero).or_default().push(p);
    }

    let mut dressed = 0usize;
    for (hero, hero_picks) in &by_hero {
        let persona = hero_picks
            .iter()
            .any(|p| p.slot.eq_ignore_ascii_case("persona_selector"));
        if persona {
            dressed += dress_persona(hero, hero_picks, &items, vanilla, entries, index, log);
        } else {
            // arcanas without a persona still leave stock parts behind
            tidy(hero, &mut HashSet::new(), entries, index, vanilla, log);
        }
    }
    if by_hero.get("wisp").is_some_and(|p| p.iter().any(|x| x.item_id == "9235")) {
        io(entries, index, vanilla, log);
    }
    if dressed > 0 {
        log(format!("persona post: {dressed} slots refilled with their real models"));
    }
    Ok(())
}

fn dress_persona(
    hero: &str,
    picks: &[&Pick],
    items: &[SchemaItem],
    vanilla: &[u8],
    entries: &mut Vec<Entry>,
    index: &Index,
    log: &mut dyn FnMut(String),
) -> usize {
    let npc = format!("npc_dota_hero_{hero}");
    if let Some(selector) = picks
        .iter()
        .find(|p| p.slot.eq_ignore_ascii_case("persona_selector"))
        .and_then(|p| items.iter().find(|i| i.id == p.item_id))
    {
        dress_body(hero, selector, index, entries, vanilla, log);
    }
    let mut wanted: Vec<(&SchemaItem, String)> = Vec::new();
    let mut covered: HashSet<String> = HashSet::new();
    for p in picks {
        let Some(item) = items.iter().find(|i| i.id == p.item_id) else { continue };
        if !p.slot.contains("_persona_") {
            continue;
        }
        covered.insert(p.slot.to_lowercase());
        wanted.push((item, p.slot.to_lowercase()));
    }
    // every free slot of the persona wears the game's own default item
    for item in items {
        if item.prefab != "default_item" || !item.slot.contains("_persona_") {
            continue;
        }
        if item.model.is_empty() && entity_model_of(vanilla, &item.id).is_none() {
            continue;
        }
        if !item_heroes(vanilla, item.start)
            .map(|h| h.iter().any(|x| x.eq_ignore_ascii_case(&npc)))
            .unwrap_or(false)
        {
            continue;
        }
        if covered.contains(&item.slot.to_lowercase()) {
            continue;
        }
        wanted.push((item, item.slot.to_lowercase()));
    }

    let stubs = stock_stubs(hero, entries);
    let parts = stock_parts(hero, entries);
    let adults = adult_paths(vanilla, &npc, items);
    let mut used: HashSet<String> = HashSet::new();
    let mut done = 0usize;
    for (item, slot) in wanted {
        let Some(model) = real_model(item)
            .or_else(|| entity_model_of(vanilla, &item.id).map(|(_, m)| compiled_asset_path(&m)))
        else {
            continue;
        };
        let Some(data) = read_index(index, &model) else {
            log(format!("persona post: {hero} {slot}: {} missing in pak01", item.name));
            continue;
        };
        let target = if slot.starts_with("summon") {
            entity_model_of(vanilla, &item.id)
                .and_then(|(unit, _)| unit_model(index, &unit))
                .map(|path| Target { path, size: 0 })
        } else {
            choose_target(&slot, &stubs, &parts, &adults, &used)
                .or_else(|| synthetic(hero, &slot, &parts))
        };
        let Some(target) = target else {
            log(format!("persona post: {hero} {slot}: no stock slot left for {}", item.name));
            continue;
        };
        used.insert(target.path.clone());
        put(entries, &target.path, data);
        log(format!(
            "persona post: {hero} {slot} -> {} ({} bytes, was {})",
            target.path,
            entries_size(entries, &target.path),
            target.size
        ));
        done += 1;
        swap_particles(index, entries, vanilla, &item.id, log);
    }
    tidy(hero, &mut used, entries, index, vanilla, log);
    done
}

/// The leftovers the item builder or the adult body leaves on a persona or an arcana.
fn tidy(
    hero: &str,
    used: &mut HashSet<String>,
    entries: &mut Vec<Entry>,
    index: &Index,
    vanilla: &[u8],
    log: &mut dyn FnMut(String),
) {
    let mut hide = |name: &str, used: &HashSet<String>, entries: &mut Vec<Entry>| {
        let path = format!("models/heroes/{hero}/{name}.vmdl_c");
        if used.contains(&path) {
            return;
        }
        if let Some(data) = read_index(index, &compiled_asset_path(EMPTY_PATH)) {
            let bytes = data.len();
            put(entries, &path, data);
            log(format!("cleanup: {hero} {name} -> empty ({bytes} bytes)"));
        }
    };
    match hero {
        "phantom_assassin" => {
            for name in ["phantom_assassin_cape", "phantom_assassin_head", "phantom_assassin_belt"] {
                hide(name, used, entries);
            }
        }
        "bristleback" => hide("bristleback_bracer", used, entries),
        "queenofpain" => {
            for name in ["queenofpain_legs", "queenofpain_armor", "queenofpain_head_item"] {
                hide(name, used, entries);
            }
        }
        "techies" => {
            // the arcana mount owns the cart; the game's own cart leaves a red sack behind it
            if let Some(data) = read_index(index, "models/items/techies/bigshot/bigshot.vmdl_c") {
                let dst = "models/heroes/techies/techies_cart.vmdl_c";
                let bytes = data.len();
                put(entries, dst, data);
                used.insert(dst.to_string());
                log(format!("cleanup: techies cart -> arcana mount ({bytes} bytes)"));
            }
        }
        "antimage" => {
            // Wei brings her own effects; the male blades' trail stays on the body otherwise
            if let Some(null) = read_index(index, &compiled_asset_path(NULL_PARTICLE)) {
                for name in [
                    "antimage_blade",
                    "antimage_blade_a",
                    "antimage_blade_b",
                    "antimage_blade_c",
                    "antimage_blade_d",
                    "antimage_blade_generic_a",
                    "antimage_blade_generic_b",
                    "antimage_blade_generic_c",
                    "antimage_blade_generic_d",
                    "antimage_blade_primary",
                    "antimage_blade_offhand",
                ] {
                    let dst = format!("particles/units/heroes/hero_antimage/{name}.vpcf_c");
                    put(entries, &dst, null.clone());
                }
                log("cleanup: antimage male blade trail -> null".to_string());
            }
        }
        _ => {}
    }
    let _ = vanilla;
}

fn dress_body(
    hero: &str,
    selector: &SchemaItem,
    index: &Index,
    entries: &mut Vec<Entry>,
    vanilla: &[u8],
    log: &mut dyn FnMut(String),
) {
    let Ok(Some(visuals)) = item_visuals(vanilla, &selector.id) else { return };
    let model = visuals
        .modifiers
        .iter()
        .find(|m| m.ty.eq_ignore_ascii_case("entity_model") && !m.modifier.is_empty())
        .map(|m| m.modifier.as_str());
    let Some(model) = model else { return };
    let dst = format!("models/heroes/{hero}/{hero}.vmdl_c");
    if let Some(data) = read_index(index, &compiled_asset_path(model)) {
        let bytes = data.len();
        put(entries, &dst, data);
        log(format!("persona post: {hero} body -> {dst} ({bytes} bytes)"));
    }
}

fn synthetic(hero: &str, slot: &str, parts: &[Target]) -> Option<Target> {
    let base = slot.split("_persona_").next().unwrap_or(slot);
    let prefix = format!("models/heroes/{hero}/");
    let names: &[&str] = match base {
        "head" => &["helmet", "head_item", "hair"],
        "weapon" => &["weapon", "bow", "righthook"],
        "offhand_weapon" => &["offhand_weapon", "leftweapon", "shield"],
        "armor" => &["shoulders", "armor", "chest"],
        "legs" => &["daggers", "legs", "belt"],
        "back" => &["cape", "back"],
        "arms" => &["cuffs", "bracers", "arms"],
        "belt" => &["belt", "daggers"],
        "misc" => &["shoulders", "misc"],
        _ => &[],
    };
    for name in names {
        let path = format!("{prefix}{hero}_{name}.vmdl_c");
        if let Some(hit) = parts.iter().find(|p| p.path == path) {
            return Some(Target { path: hit.path.clone(), size: hit.size });
        }
        if !parts.iter().any(|p| p.path == path) {
            return Some(Target { path, size: 0 });
        }
    }
    None
}

fn io(entries: &mut Vec<Entry>, index: &Index, vanilla: &[u8], log: &mut dyn FnMut(String)) {
    let body = "models/heroes/wisp/wisp.vmdl_c";
    let additive = "models/heroes/wisp/wisp_additive.vmdl_c";
    let mut put_one = |src: &str, dst: &str, what: &str, log: &mut dyn FnMut(String)| {
        if let Some(data) = read_index(index, &compiled_asset_path(src)) {
            let bytes = data.len();
            put(entries, dst, data);
            log(format!("io post: {what} -> {dst} ({bytes} bytes)"));
        }
    };
    put_one("models/items/io/io_ti7/io_ti7.vmdl", body, "cube", log);
    put_one(
        "models/items/io/io_ti7/io_ti7_additive.vmdl",
        additive,
        "cube additive",
        log,
    );
    swap_particles(index, entries, vanilla, "9235", log);
}

fn swap_particles(
    index: &Index,
    entries: &mut Vec<Entry>,
    vanilla: &[u8],
    item_id: &str,
    log: &mut dyn FnMut(String),
) {
    let Ok(Some(visuals)) = item_visuals(vanilla, item_id) else { return };
    for m in visuals.modifiers {
        if !m.ty.to_lowercase().contains("particle") || m.asset.is_empty() || m.modifier.is_empty() {
            continue;
        }
        let Some(data) = read_index(index, &compiled_asset_path(&m.modifier)) else { continue };
        let dst = compiled_asset_path(&m.asset);
        if !entries.iter().any(|e| e.path() == dst) && read_index(index, &dst).is_none() {
            continue;
        }
        let bytes = data.len();
        put(entries, &dst, data);
        log(format!("post: {} -> {dst} ({bytes} bytes)", m.modifier));
    }
}

/// `(unit, model)` of an item's `entity_model` modifier: a summoned unit's model, which is not
/// the item's own `model` field.
fn entity_model_of(vanilla: &[u8], id: &str) -> Option<(String, String)> {
    let visuals = item_visuals(vanilla, id).ok()??;
    let m = visuals
        .modifiers
        .iter()
        .find(|m| m.ty.eq_ignore_ascii_case("entity_model") && !m.modifier.is_empty())?;
    Some((m.asset.clone(), m.modifier.clone()))
}

/// The stock model a unit name uses, read from `npc_units.txt`.
fn unit_model(index: &Index, unit: &str) -> Option<String> {
    let text = String::from_utf8_lossy(&index.read("scripts/npc/npc_units.txt").ok().flatten()?).to_string();
    let at = text.find(&format!("\"{unit}\""))?;
    let tail = &text[at..text.len().min(at + 5000)];
    let m = tail.find("\"Model\"")?;
    let rest = &tail[m + 7..];
    let open = rest.find('"')?;
    let value = &rest[open + 1..];
    let close = value.find('"')?;
    Some(compiled_asset_path(value[..close].trim()))
}

fn real_model(item: &SchemaItem) -> Option<String> {
    let model = item.model.trim();
    if model.is_empty() {
        None
    } else {
        Some(compiled_asset_path(model))
    }
}

fn adult_paths(vanilla: &[u8], npc: &str, items: &[SchemaItem]) -> HashMap<String, String> {
    let mut out = HashMap::new();
    for item in items {
        if item.prefab != "default_item" || item.slot.contains("_persona_") || item.model.is_empty() {
            continue;
        }
        if !item_heroes(vanilla, item.start)
            .map(|h| h.iter().any(|x| x.eq_ignore_ascii_case(npc)))
            .unwrap_or(false)
        {
            continue;
        }
        out.entry(item.slot.to_lowercase())
            .or_insert_with(|| compiled_asset_path(&item.model));
    }
    out
}

fn stock_stubs(hero: &str, entries: &[Entry]) -> Vec<Target> {
    stock_parts(hero, entries)
        .into_iter()
        .filter(|t| t.size < EMPTY_MODEL)
        .collect()
}

fn stock_parts(hero: &str, entries: &[Entry]) -> Vec<Target> {
    let prefix = format!("models/heroes/{hero}/");
    let body = format!("{prefix}{hero}.vmdl_c");
    let mut out: Vec<Target> = entries
        .iter()
        .filter(|e| {
            let p = e.path();
            p.starts_with(&prefix) && p.ends_with(".vmdl_c") && p != body
        })
        .map(|e| Target { path: e.path(), size: e.data.len() })
        .collect();
    out.sort_by(|a, b| a.path.cmp(&b.path));
    out
}

fn choose_target(
    slot: &str,
    stubs: &[Target],
    parts: &[Target],
    adults: &HashMap<String, String>,
    used: &HashSet<String>,
) -> Option<Target> {
    let base = slot.split("_persona_").next().unwrap_or(slot);
    let keys: &[&str] = match base {
        "head" => &["hair", "head", "helmet"],
        "weapon" => &["hook", "weapon", "sword", "blade", "bow", "halberd"],
        "offhand_weapon" => &["offhand", "shield", "leftweapon"],
        "armor" => &["chest", "armor", "costume", "belt", "clothing"],
        "back" => &["cape", "back", "wings"],
        "shoulder" => &["shoulder"],
        "arms" => &["bracer", "arms"],
        "belt" => &["belt", "dress"],
        "misc" => &["belt", "misc", "back", "armor", "shoulder", "arms"],
        _ => &[],
    };
    let fits = |name: &str| {
        if base == "weapon" && (name.contains("offhand") || name.contains("left")) {
            return false;
        }
        keys.is_empty() || keys.iter().any(|k| name.contains(k))
    };
    for part in parts {
        if used.contains(&part.path) {
            continue;
        }
        let name = part.path.rsplit('/').next().unwrap_or(&part.path);
        if fits(name) {
            return Some(Target { path: part.path.clone(), size: part.size });
        }
    }
    if let Some(path) = adults.get(base) {
        let path = path.to_lowercase();
        if !used.contains(&path) {
            return Some(Target {
                path: path.clone(),
                size: parts.iter().find(|s| s.path == path).map_or(0, |s| s.size),
            });
        }
    }
    // no name of this kind is left: the remaining empty stock slot of this hero takes the part
    stubs
        .iter()
        .find(|s| !used.contains(&s.path))
        .map(|s| Target { path: s.path.clone(), size: s.size })
}

fn read_index(index: &Index, path: &str) -> Option<Vec<u8>> {
    index.read(path).ok().flatten().filter(|d| !d.is_empty())
}

fn put(entries: &mut Vec<Entry>, path: &str, data: Vec<u8>) {
    let path = path.to_lowercase();
    entries.retain(|e| e.path() != path);
    entries.push(vpk::entry_at(&path, data.into()));
}

fn entries_size(entries: &[Entry], path: &str) -> usize {
    entries.iter().find(|e| e.path() == path).map_or(0, |e| e.data.len())
}

// A single modifier field is enough for the common particle swaps; the unused variant keeps the
// fn signature above stable when the item table has non-particle modifiers.
#[allow(dead_code)]
fn modifier_particle(m: &AssetModifier) -> bool {
    m.ty.to_lowercase().contains("particle")
}
