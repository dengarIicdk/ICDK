//! World cosmetics that live beside the item table: a tower model per side, and an emblem effect
//! on the player's selection ring. Everything is an alias over a stock path, so the client keeps
//! reading its own item table and the language-folder pak only replaces the files it names.
//!
//! The tower planning is the one from the reference file builder: read an item's visuals, pick the
//! modifiers that belong to one side, and put the chosen model under the default tower's name.

use std::collections::HashMap;
use std::sync::Arc;

use regex::Regex;

use crate::item_visuals::{item_visuals, AssetModifier};
use crate::kv3::{Node, Value as Kv};
use crate::vpk::{entry_at, Entry, Index};

#[derive(Debug, Clone, Default)]
pub struct WorldPicks {
    pub radiant_tower: Option<String>,
    pub dire_tower: Option<String>,
    pub emblem: Option<String>,
    pub radiant_creep: Option<String>,
    pub dire_creep: Option<String>,
}

fn re(p: &str) -> Regex {
    Regex::new(p).expect("a fixed pattern")
}

fn compiled(p: &str) -> String {
    let ext = re(r"(?i)\.(vmdl|vpcf)$");
    format!("{}_c", ext.replace(p, |c: &regex::Captures| c[0].to_lowercase()))
}

pub fn default_tower(side: &str) -> &'static str {
    if side == "dire" {
        "models/props_structures/dire_tower002.vmdl"
    } else {
        "models/props_structures/radiant_tower002.vmdl"
    }
}

fn is_entity(ty: &str) -> bool {
    re("^entity(_clientside)?_model$").is_match(ty)
}

/// The stock model each lane creep entity wears; a creep look replaces them one by one.
/// `None` for an asset that is not a lane creep of that side. Siege creeps are entities of their
/// own (`npc_dota_goodguys_siege`), so both spellings are read.
fn creep_stock(asset: &str) -> Option<&'static str> {
    let side = creep_side_of(asset)?;
    if !asset.contains("creep") && !asset.contains("siege") && !asset.contains("melee") && !asset.contains("ranged") {
        return None;
    }
    let kind = if asset.contains("siege") {
        "siege"
    } else if asset.contains("ranged") {
        "ranged"
    } else {
        "melee"
    };
    let mega = asset.ends_with("_mega");
    let flag = asset.contains("flagbearer");
    Some(match (side, kind, mega, flag) {
        // upgraded creeps are the plain model, and the mega ones have a model of their own
        ("radiant", "melee", false, false) => "models/creeps/lane_creeps/creep_radiant_melee/radiant_melee.vmdl",
        ("radiant", "melee", true, false) => "models/creeps/lane_creeps/creep_radiant_melee/radiant_melee_mega.vmdl",
        ("radiant", "melee", false, true) => "models/creeps/lane_creeps/creep_radiant_melee/radiant_flagbearer.vmdl",
        ("radiant", "melee", true, true) => "models/creeps/lane_creeps/creep_radiant_melee/radiant_flagbearer_mega.vmdl",
        ("radiant", "ranged", false, _) => "models/creeps/lane_creeps/creep_radiant_ranged/radiant_ranged.vmdl",
        ("radiant", "ranged", true, _) => "models/creeps/lane_creeps/creep_radiant_ranged/radiant_ranged_mega.vmdl",
        ("radiant", "siege", _, _) => "models/creeps/lane_creeps/creep_good_siege/creep_good_siege.vmdl",
        ("dire", "melee", false, false) => "models/creeps/lane_creeps/creep_bad_melee/creep_bad_melee.vmdl",
        ("dire", "melee", true, false) => "models/creeps/lane_creeps/creep_bad_melee/creep_bad_melee_mega.vmdl",
        ("dire", "melee", false, true) => "models/creeps/lane_creeps/creep_bad_melee/creep_bad_flagbearer.vmdl",
        ("dire", "melee", true, true) => "models/creeps/lane_creeps/creep_bad_melee/creep_bad_flagbearer_mega.vmdl",
        ("dire", "ranged", false, _) => "models/creeps/lane_creeps/creep_bad_ranged/lane_dire_ranged.vmdl",
        ("dire", "ranged", true, _) => "models/creeps/lane_creeps/creep_bad_ranged/lane_dire_ranged_mega.vmdl",
        ("dire", "siege", _, _) => "models/creeps/lane_creeps/creep_bad_siege/creep_bad_siege.vmdl",
        _ => return None,
    })
}

fn creep_side_of(asset: &str) -> Option<&'static str> {
    if asset.contains("goodguys") {
        Some("radiant")
    } else if asset.contains("badguys") {
        Some("dire")
    } else {
        None
    }
}

/// The creep models a look puts under the stock creep names of one side, and the stock particles
/// it swaps on the way (a siege creep's shot and its death cloud).
fn creep_plan(modifiers: &[AssetModifier], side: &str) -> (Vec<(String, String)>, Vec<String>) {
    let mut files: Vec<(String, String)> = Vec::new();
    let mut skipped: Vec<String> = Vec::new();
    for m in modifiers {
        if m.modifier.is_empty() {
            continue;
        }
        if is_entity(&m.ty) {
            match creep_side_of(&m.asset) {
                Some(s) if s == side => {
                    if let Some(stock) = creep_stock(&m.asset) {
                        put(&mut files, compiled(stock), compiled(&m.modifier));
                    }
                }
                Some(_) => {
                    let what = format!("{} {}", m.ty, m.asset);
                    if !skipped.contains(&what) {
                        skipped.push(what);
                    }
                }
                None => {}
            }
            continue;
        }
        // a creep look also swaps its shot and its death cloud; both are plain particles, named for
        // the side in the game's own words (`siege_good_death_01`, `ranged_siege_bad`)
        if m.ty == "particle" || m.ty == "particle_clientside" {
            let asset_side = creep_side_of(&m.asset).or_else(|| {
                let a = m.asset.to_ascii_lowercase();
                if a.contains("good") || a.contains("radiant") {
                    Some("radiant")
                } else if a.contains("bad") || a.contains("dire") {
                    Some("dire")
                } else {
                    None
                }
            });
            if !m.asset.is_empty() && asset_side == Some(side) {
                put(&mut files, compiled(&m.asset), compiled(&m.modifier));
            }
        }
    }
    (files, skipped)
}

fn put(files: &mut Vec<(String, String)>, at: String, from: String) {
    if let Some(existing) = files.iter_mut().find(|f| f.0 == at) {
        existing.1 = from;
    } else {
        files.push((at, from));
    }
}

fn side_of(asset: &str) -> Option<&'static str> {
    if let Some(m) = re("^npc_dota_(goodguys|badguys)_tower").captures(asset) {
        return Some(if &m[1] == "goodguys" { "radiant" } else { "dire" });
    }
    if re("radiant|_good|good_|goodguys").is_match(asset) {
        return Some("radiant");
    }
    if re("dire|_bad|bad_|badguys").is_match(asset) {
        return Some("dire");
    }
    None
}

/// How good a tower model is as the standing tower: 0 plain, 1 a left/right variant, 2 a ruin.
fn tower_model_rank(model: &str) -> usize {
    let m = model.to_ascii_lowercase();
    if m.contains("_dest") || m.contains("destruction") || m.contains("ruin") {
        2
    } else if m.contains("_left") || m.contains("_right") {
        1
    } else {
        0
    }
}

fn tower_plan(modifiers: &[AssetModifier], side: &str) -> (Vec<(String, String)>, Vec<String>) {
    let mut files: Vec<(String, String)> = Vec::new();
    let mut skipped: Vec<String> = Vec::new();
    let mut current_tower = String::new();
    let mut put = |at: String, from: String, files: &mut Vec<(String, String)>| {
        if let Some(existing) = files.iter_mut().find(|f| f.0 == at) {
            existing.1 = from;
        } else {
            files.push((at, from));
        }
    };
    for m in modifiers {
        if is_entity(&m.ty) {
            if side_of(&m.asset) == Some(side) && !m.modifier.is_empty() {
                // a look may name a right-hand or a ruined variant as well; the plain standing
                // model is the one all ten towers must wear
                if !current_tower.is_empty() && tower_model_rank(&m.modifier) >= tower_model_rank(&current_tower) {
                    continue;
                }
                current_tower = m.modifier.clone();
                put(compiled(default_tower(side)), compiled(&m.modifier), &mut files);
            }
        } else if m.ty == "model" || m.ty == "particle" || m.ty == "particle_clientside" {
            if !m.asset.is_empty() && !m.modifier.is_empty() && side_of(&m.asset) == Some(side) {
                let at = compiled(&m.asset);
                if crate::tower_looks::ambient_loadout(&at) {
                    // the loadout ambient hook: the item table says which tower it hangs on, a file
                    // cannot, and an unattached effect is drawn at the world origin (0,0,0) — the
                    // middle of the river — from the first second of the match
                    let what = format!("{at} (loadout hook)");
                    if !skipped.contains(&what) {
                        skipped.push(what);
                    }
                    continue;
                }
                put(at, compiled(&m.modifier), &mut files);
            }
        } else if side_of(if m.asset.is_empty() { &m.modifier } else { &m.asset }).unwrap_or(side) == side {
            let what = if m.modifier.is_empty() { m.ty.clone() } else { format!("{} {}", m.ty, m.modifier) };
            if !skipped.contains(&what) {
                skipped.push(what);
            }
        }
    }
    (files, skipped)
}

fn plain_act(name: &str) -> Option<&'static str> {
    match name {
        "ACT_DOTA_CUSTOM_TOWER_IDLE" => Some("ACT_DOTA_IDLE"),
        "ACT_DOTA_CUSTOM_TOWER_IDLE_RARE" => Some("ACT_DOTA_IDLE_RARE"),
        "ACT_DOTA_CUSTOM_TOWER_ATTACK" => Some("ACT_DOTA_ATTACK"),
        "ACT_DOTA_CUSTOM_TOWER_DIE" => Some("ACT_DOTA_DIE"),
        _ => None,
    }
}

fn text_of(n: Option<&Node>) -> String {
    n.and_then(Node::as_str).unwrap_or("").to_string()
}

/// A look's tower model made to play as the plain tower: its custom activities renamed to the
/// plain ones, sequences numbered as the plain tower's, attachments placed where it leaves them.
fn as_tower(model: &[u8], plain: &[u8]) -> Vec<u8> {
    let acted = plain_activities(model);
    let numbered = crate::model_sequences::numbered_as(&acted, plain).unwrap_or(acted);
    crate::model_attachments::attached_as(&numbered, plain)
}

fn plain_activities(model: &[u8]) -> Vec<u8> {
    let run = || -> Option<Vec<u8>> {
        if !crate::resource::blocks(model).ok()?.iter().any(|b| b.name == "ASEQ") {
            return None;
        }
        let mut kv = crate::kv3::read(crate::resource::data(model, "ASEQ", 0).ok()?).ok()?;
        let names: Vec<String> = kv
            .root
            .get("m_localSequenceNameArray")
            .map(|n| n.items().iter().map(|x| text_of(Some(x))).collect())
            .unwrap_or_default();
        let count = kv.root.get("m_localS1SeqDescArray").map_or(0, |n| n.items().len());
        let mut by_name: HashMap<String, usize> = HashMap::new();
        for (i, d) in kv.root.get("m_localS1SeqDescArray")?.items().iter().enumerate() {
            by_name.insert(text_of(d.get("m_sName")), i);
        }
        let mut changed = false;
        for i in 0..count {
            let d = kv.root.get("m_localS1SeqDescArray")?.items()[i].clone();
            let Some(acts) = d.get("m_activityArray").filter(|a| matches!(a.value, Kv::Array { .. })) else { continue };
            let Some(first) = acts.items().iter().find(|a| plain_act(&text_of(a.get("m_name"))).is_some()) else {
                continue;
            };
            if !matches!(first.value, Kv::Object(_)) {
                continue;
            }
            let refs: Vec<String> = d
                .get("m_fetch")
                .and_then(|f| f.get("m_localReferenceArray"))
                .map(|r| r.items())
                .unwrap_or(&[])
                .iter()
                .map(|r| match &r.value {
                    Kv::Number { .. } => kv
                        .number_of(r)
                        .filter(|n| *n >= 0.0 && n.fract() == 0.0)
                        .and_then(|n| names.get(n as usize))
                        .cloned()
                        .unwrap_or_default(),
                    _ => String::new(),
                })
                .collect();
            let part = if refs.len() > 1 {
                let neutral = re("(?i)neutral");
                let pick = refs.iter().find(|n| neutral.is_match(n)).unwrap_or(&refs[refs.len() / 2]);
                match by_name.get(pick) {
                    Some(&j) => j,
                    None => continue,
                }
            } else {
                i
            };
            let mut act = first.clone();
            if let Kv::Object(m) = &mut act.value {
                let wanted = plain_act(&text_of(m.get("m_name"))).unwrap_or("");
                m.insert("m_name".into(), Node::new(Kv::Str(wanted.into())));
            }
            let mut list = acts.clone();
            if let Kv::Array { items, .. } = &mut list.value {
                *items = vec![act];
            }
            let descs = kv.root.get_mut("m_localS1SeqDescArray")?;
            let Kv::Array { items, .. } = &mut descs.value else { return None };
            let Kv::Object(target) = &mut items[part].value else { continue };
            target.insert("m_activityArray".into(), list);
            changed = true;
        }
        if !changed {
            return None;
        }
        let next = crate::kv3::write(&kv).ok()?;
        crate::resource::replace(model, "ASEQ", 0, &next).ok()
    };
    run().unwrap_or_else(|| model.to_vec())
}

/// Последний шанс для облика, который не склеивается со стоковым крипом: у модели нет ни одной
/// секвенции с активностью ACT_DOTA_DIE, и движок уводит такого крипа под землю вместо смерти.
/// Секвенция, уже изображающая поражение (defeat), получает рядом двойника с активностью
/// ACT_DOTA_DIE: имена и порядок остальных не меняются, индексы никуда не съезжают.
fn with_die_activity(model: &[u8]) -> Option<Vec<u8>> {
    let mut kv = crate::kv3::read(crate::resource::data(model, "ASEQ", 0).ok()?).ok()?;
    let descs = kv.root.get("m_localS1SeqDescArray")?.items().to_vec();
    if descs.iter().any(|d| {
        d.get("m_activityArray")
            .map(|a| a.items().iter().any(|x| text_of(x.get("m_name")).eq_ignore_ascii_case("ACT_DOTA_DIE")))
            .unwrap_or(false)
    }) {
        return None;
    }
    let pick = |word: &str| {
        descs
            .iter()
            .find(|d| text_of(d.get("m_sName")).to_ascii_lowercase().contains(word))
            .cloned()
    };
    let source = pick("defeat").or_else(|| pick("idle")).or_else(|| descs.first().cloned())?;
    let mut twin = source.clone();
    let Kv::Object(map) = &mut twin.value else { return None };
    let own = text_of(map.get("m_sName"));
    map.insert("m_sName".into(), Node::new(Kv::Str(format!("{own}_act_dota_die"))));
    let mut fields = indexmap::IndexMap::new();
    fields.insert("m_name".to_string(), Node::new(Kv::Str("ACT_DOTA_DIE".into())));
    let die = Node::new(Kv::Object(fields));
    if let Some(Node { value: Kv::Array { items, .. }, .. }) = map.get_mut("m_activityArray") {
        *items = vec![die];
    } else {
        return None;
    }
    for key in ["m_localSequenceNameArray", "m_localS1SeqDescArray"] {
        if !matches!(kv.root.get(key).map(|n| &n.value), Some(Kv::Array { .. })) {
            return None;
        }
    }
    if let Some(Node { value: Kv::Array { items, .. }, .. }) = kv.root.get_mut("m_localS1SeqDescArray") {
        items.push(twin);
    }
    if let Some(Node { value: Kv::Array { items, .. }, .. }) = kv.root.get_mut("m_localSequenceNameArray") {
        items.push(Node::new(Kv::Str(format!("{own}_act_dota_die"))));
    }
    let next = crate::kv3::write(&kv).ok()?;
    crate::resource::replace(model, "ASEQ", 0, &next).ok()
}

/// The entries a world choice adds: one model per tower side and the emblem on the hero ring.
pub fn build(
    index: &Index,
    text: &[u8],
    picks: &WorldPicks,
    log: &mut dyn FnMut(String),
) -> Result<Vec<Entry>, String> {
    let mut out: Vec<Entry> = Vec::new();
    let mut missing: Vec<String> = Vec::new();

    for (side, id) in [("radiant", picks.radiant_tower.as_deref()), ("dire", picks.dire_tower.as_deref())] {
        let Some(id) = id.filter(|id| !id.is_empty()) else { continue };
        let v = item_visuals(text, id)
            .map_err(|e| e.to_string())?
            .ok_or_else(|| format!("world: no tower item {id}"))?;
        let (plan, skipped) = tower_plan(&v.modifiers, side);
        if !plan.iter().any(|f| f.0 == compiled(default_tower(side))) {
            return Err(format!("world: {} has no {side} tower model", v.name));
        }
        for line in skipped {
            log(format!("world: {} leaves out {}", v.name, line));
        }
        let plain_at = compiled(default_tower(side));
        let plain = index.read(&plain_at).ok().flatten();
        let mut made = 0usize;
        for (at, from) in plan {
            let Some(data) = index.read(&from).ok().flatten() else {
                missing.push(from);
                continue;
            };
            let bytes: Arc<[u8]> = if at == plain_at {
                match plain.as_deref() {
                    Some(p) => Arc::from(as_tower(&data, p)),
                    None => Arc::from(data),
                }
            } else {
                Arc::from(data)
            };
            out.retain(|e| e.path() != at);
            out.push(entry_at(&at, bytes));
            made += 1;
        }
        log(format!("world: {} {side} towers: {made} files", v.name));
    }

    // Lane creeps: a look names one model per creep entity, so each stock creep model of the side
    // takes the look's model of the same kind. The mega and flag-bearer variants follow only when
    // the look names them, otherwise the plain model stays where it is.
    for (side, id) in [("radiant", picks.radiant_creep.as_deref()), ("dire", picks.dire_creep.as_deref())] {
        let Some(id) = id.filter(|id| !id.is_empty()) else { continue };
        let v = item_visuals(text, id)
            .map_err(|e| e.to_string())?
            .ok_or_else(|| format!("world: no creep item {id}"))?;
        let (plan, skipped) = creep_plan(&v.modifiers, side);
        if plan.is_empty() {
            return Err(format!("world: {} has no {side} creeps", v.name));
        }
        for line in skipped {
            log(format!("world: {} leaves out {}", v.name, line));
        }
        let mut made = 0usize;
        for (at, from) in plan {
            let Some(data) = index.read(&from).ok().flatten() else {
                missing.push(from);
                continue;
            };
            let plain = index.read(&at).ok().flatten();
            let bytes: Arc<[u8]> = match plain.as_deref() {
                // A creep look of Valve's own making is meshes only: the model it ships has no
                // ACT_DOTA_DIE at all, and a creep whose model cannot die is sunk under the ground
                // by the engine's fallback instead of falling over. The look's meshes go inside the
                // stock creep model, so the stock ANIM/ASEQ stay as they are and the death plays.
                Some(p) => match crate::model_merge::merged_model(p, &data).ok().flatten() {
                    Some(merged) => Arc::from(merged),
                    // облик не склеивается (у модели есть морфы): оставляем её, но даём смерть
                    None => Arc::from(with_die_activity(&data).unwrap_or_else(|| as_tower(&data, p))),
                },
                None => Arc::from(data),
            };
            out.retain(|e| e.path() != at);
            out.push(entry_at(&at, bytes));
            made += 1;
        }
        log(format!("world: {} {side} creeps: {made} models", v.name));
    }

    if let Some(id) = picks.emblem.as_deref().filter(|id| !id.is_empty()) {
        let v = item_visuals(text, id)
            .map_err(|e| e.to_string())?
            .ok_or_else(|| format!("world: no emblem item {id}"))?;
        // the first bare particle_create is the effect the game itself puts on the hero
        let effect = v
            .modifiers
            .iter()
            .find(|m| m.ty == "particle_create" && m.asset.is_empty() && m.modifier.ends_with(".vpcf"))
            .or_else(|| v.modifiers.iter().find(|m| m.ty == "particle_create" && m.modifier.ends_with(".vpcf")))
            .map(|m| m.modifier.clone());
        if let Some(effect) = effect {
            let from = compiled(&effect);
            match index.read(&from).ok().flatten() {
                Some(data) => {
                    let bytes: Arc<[u8]> = Arc::from(data);
                    // `selected_ring` is the ring the game always draws under the picked hero, the
                    // one the published emblem packs replace; the hero ring is its hover twin.
                    for at in [
                        "particles/ui_mouseactions/selected_ring.vpcf_c",
                        "particles/ui_mouseactions/selected_ring_b.vpcf_c",
                        "particles/ui_mouseactions/hero_ring.vpcf_c",
                        "particles/ui_mouseactions/hero_ring_b.vpcf_c",
                    ] {
                        out.retain(|e| e.path() != at);
                        out.push(entry_at(at, bytes.clone()));
                    }
                    log(format!("world: {} on the selection ring", v.name));
                }
                None => missing.push(from),
            }
        } else {
            log(format!("world: {} has no hero particle", v.name));
        }
    }

    if !missing.is_empty() {
        log(format!("world: {} source files are not in the game, first {}", missing.len(), missing[0]));
    }
    Ok(out)
}
