//! Tower looks as a file mod (issue #446). The items that dress the towers say
//! in their visuals which model the tower entity wears, which model stands in for each stage of its
//! destruction, and which particles it swaps. Those are files, so a pak can put them under the
//! default tower's names, for one side or both, with safe mode on. What a file cannot carry (a
//! particle the item creates, its sounds, level activities, skins) is left out and said.

use std::path::Path;

use indexmap::{IndexMap, IndexSet};
use regex::bytes::Regex as BytesRegex;
use regex::Regex;
use serde_json::{json, Map, Value};

use crate::item_visuals::{item_visuals, AssetModifier};
use crate::tree_sets::Planned;

/// The model every tower of a side wears; the map's tower entities name it.
pub fn default_tower(side: &str) -> &'static str {
    if side == "dire" {
        "models/props_structures/dire_tower002.vmdl"
    } else {
        "models/props_structures/radiant_tower002.vmdl"
    }
}

fn re(p: &str) -> Regex {
    Regex::new(p).expect("a fixed pattern")
}

fn is_entity(ty: &str) -> bool {
    re("^entity(_clientside)?_model$").is_match(ty)
}

/// The side an asset belongs to, by its name: the game names them for Radiant and Dire.
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

/// String.prototype.lastIndexOf: the last place at or before `from` where `needle` starts.
fn last_index_of(text: &[u8], needle: &[u8], from: usize) -> Option<usize> {
    let end = (from + needle.len()).min(text.len());
    text[..end].windows(needle.len()).rposition(|w| w == needle)
}

/// The ids of the items whose visuals put a model on a tower, in the table's order.
pub fn tower_look_ids(text: &[u8]) -> Vec<String> {
    let asset = BytesRegex::new(r#"(?-u)"asset"[\t-\r \xA0]+"npc_dota_(?:goodguys|badguys)_tower[A-Za-z0-9_]*""#)
        .expect("a fixed pattern");
    let head = BytesRegex::new(r#"(?-u)^\n\t\t"([0-9]+)"[\t-\r \xA0]*\{"#).expect("a fixed pattern");
    let needle = b"\n\t\t\"";
    let mut ids: Vec<String> = Vec::new();
    for m in asset.find_iter(text) {
        // the item this modifier sits in: the nearest item key before it, two tabs in
        let mut id = String::new();
        let mut at = last_index_of(text, needle, m.start());
        while let Some(a) = at.filter(|&a| a > 0 && id.is_empty()) {
            if let Some(c) = head.captures(&text[a..]) {
                id = String::from_utf8_lossy(&c[1]).into_owned();
            }
            at = last_index_of(text, needle, a - 1);
        }
        if !id.is_empty() && !ids.contains(&id) {
            ids.push(id);
        }
    }
    ids
}

/// Every look the table has, with the sides each dresses and the tower model it puts on each.
pub fn tower_looks(text: &[u8]) -> Result<Vec<Value>, String> {
    let mut out = Vec::new();
    for id in tower_look_ids(text) {
        let v = item_visuals(text, &id).map_err(|e| e.to_string())?;
        let mut sides: IndexSet<&str> = IndexSet::new();
        let mut models = Map::new();
        for m in v.as_ref().map(|v| v.modifiers.as_slice()).unwrap_or(&[]) {
            if !is_entity(&m.ty) {
                continue;
            }
            if let Some(s) = side_of(&m.asset) {
                sides.insert(s);
                if !m.modifier.is_empty() && !models.contains_key(s) {
                    models.insert(s.into(), json!(m.modifier));
                }
            }
        }
        let name = v.as_ref().map(|v| v.name.clone()).filter(|n| !n.is_empty()).unwrap_or_else(|| id.clone());
        let sides: Vec<&str> = ["radiant", "dire"].into_iter().filter(|s| sides.contains(s)).collect();
        if !sides.is_empty() {
            out.push(json!({ "id": id, "name": name, "sides": sides, "models": models }));
        }
    }
    Ok(out)
}

fn compiled(p: &str) -> String {
    let ext = re(r"(?i)\.(vmdl|vpcf)$");
    format!("{}_c", ext.replace(p, |c: &regex::Captures| c[0].to_lowercase()))
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

/// What a look's visuals put under which name for one side, and what it leaves out.
pub fn tower_plan(modifiers: &[AssetModifier], side: &str) -> (Vec<Planned>, Vec<String>) {
    let mut files: IndexMap<String, String> = IndexMap::new();
    let mut skipped: IndexSet<String> = IndexSet::new();
    let mut current_tower = String::new();
    for m in modifiers {
        if is_entity(&m.ty) {
            // one model for all ten towers of the side: a right-hand or ruined variant must not
            // take the place of the standing one, so the plainest model named wins
            if side_of(&m.asset) == Some(side) && !m.modifier.is_empty() {
                if current_tower.is_empty() || tower_model_rank(&m.modifier) < tower_model_rank(&current_tower) {
                    current_tower = m.modifier.clone();
                    files.insert(compiled(default_tower(side)), compiled(&m.modifier));
                    // The engine swaps to its own damaged tower below 65% and 35% health: the stock
                    // paths are models/props_structures/default/<side>_tower002_dest_lvl1 and _lvl2.
                    // A look that only fills the standing path turns back into the stock stone pillar
                    // the moment the tower is hurt, so the look's model goes on every stage the game
                    // can ask for — exactly the three paths the tower packs of the open catalog ship.
                    for stage in ["dest_lvl1", "dest_lvl2"] {
                        files.insert(format!("models/props_structures/default/{side}_tower002_{stage}.vmdl_c"), compiled(&m.modifier));
                    }
                }
            }
        } else if m.ty == "model" || m.ty == "particle" || m.ty == "particle_clientside" {
            if !m.asset.is_empty() && !m.modifier.is_empty() && side_of(&m.asset) == Some(side) {
                let at = compiled(&m.asset);
                if ambient_loadout(&at) {
                    // A tower's loadout ambient is a hook the item table fills in: the game spawns it
                    // on the tower it picked. A file cannot say which tower that is, so the effect is
                    // left unattached and Source 2 drops it at the world origin (0,0,0), the middle of
                    // the river, for the whole match. The look's model carries its own flames, so the
                    // hook keeps the game's own file and nothing hangs over the river.
                    skipped.insert(format!("{at} (loadout hook, the model carries its own lights)"));
                    continue;
                }
                files.insert(at, compiled(&m.modifier));
            }
        } else if side_of(if m.asset.is_empty() { &m.modifier } else { &m.asset }).unwrap_or(side) == side {
            // named for the other side: that side's pak says it
            let what = if m.modifier.is_empty() { m.ty.clone() } else { format!("{} {}", m.ty, m.modifier) };
            skipped.insert(what);
        }
    }
    (files.into_iter().map(|(at, from)| Planned { at, from }).collect(), skipped.into_iter().collect())
}

/// What building towers made: the pak, the looks' names, how many files, what the game lacked, and
/// what a file cannot carry.
#[derive(Debug, Clone)]
pub struct Towers {
    pub vpk: Vec<u8>,
    pub names: Vec<String>,
    pub files: usize,
    pub missing: Vec<String>,
    pub skipped: Vec<String>,
}

/// Each side's towers in a look of its own, or as they are where its id is None: one VPK out of pak01.
pub fn build_towers(pak01: &Path, text: &[u8], radiant: Option<&str>, dire: Option<&str>) -> Result<Towers, String> {
    let mut files: Vec<Planned> = Vec::new();
    let mut skipped: IndexSet<String> = IndexSet::new();
    let mut names: Vec<String> = Vec::new();
    for (side, id) in [("radiant", radiant), ("dire", dire)] {
        let Some(id) = id.filter(|i| !i.is_empty()) else { continue };
        let v = item_visuals(text, id).map_err(|e| e.to_string())?.ok_or_else(|| format!("towers: no item {id}"))?;
        let (plan, left) = tower_plan(&v.modifiers, side);
        if !plan.iter().any(|f| f.at == compiled(default_tower(side))) {
            return Err(format!("towers: {} has no {side} tower", v.name));
        }
        files.extend(plan);
        skipped.extend(left);
        if !names.contains(&v.name) {
            names.push(v.name);
        }
    }
    if files.is_empty() {
        return Err("towers: pick a look for Radiant or Dire".into());
    }
    let index = crate::vpk_shared::shared_index(pak01).map_err(|e| e.to_string())?;
    let towers = [compiled(default_tower("radiant")), compiled(default_tower("dire"))];
    let known: std::collections::HashSet<String> = index.paths().map(str::to_string).collect();
    let mut entries = Vec::new();
    let mut missing = Vec::new();
    for f in files {
        // a damaged stage the game does not have is not written: a pak stays as small as the
        // stock map of paths allows
        if f.at.contains("_dest_lvl") && !known.contains(&f.at) {
            continue;
        }

        let data = index.read(&f.from).map_err(|e| e.to_string())?;
        let plain = if towers.contains(&f.at) || f.at.contains("_dest_lvl") {
            let side = if f.at.contains("radiant") { "radiant" } else { "dire" };
            index.read(&compiled(default_tower(side))).map_err(|e| e.to_string())?
        } else {
            None
        };
        match data {
            Some(d) => {
                let bytes = match plain {
                    Some(p) => as_tower(&d, &p),
                    None => d,
                };
                entries.push(crate::vpk::entry_at(&f.at, std::sync::Arc::from(bytes)));
            }
            None => missing.push(f.from),
        }
    }
    let n = entries.len();
    Ok(Towers { vpk: crate::vpk::build(&entries), names, files: n, missing, skipped: skipped.into_iter().collect() })
}

/// The activities a look's tower plays while the item is on, and the plain tower's for each.
/// True for the tower ambient the item table hooks onto the tower it dresses.
pub fn ambient_loadout(path: &str) -> bool {
    re(r"(?i)^particles/units/(radiant|dire)_towers_ambient_loadout\.vpcf_c$").is_match(path)
}

/// The standing tower model a look puts on one side, as the plan would choose it.
pub fn chosen_model(text: &[u8], id: &str, side: &str) -> Option<String> {
    let v = item_visuals(text, id).ok()??;
    let mut best: Option<String> = None;
    for m in &v.modifiers {
        if is_entity(&m.ty) && side_of(&m.asset) == Some(side) && !m.modifier.is_empty() {
            if best.as_deref().is_none_or(|b| tower_model_rank(&m.modifier) < tower_model_rank(b)) {
                best = Some(m.modifier.clone());
            }
        }
    }
    best
}

/// A look's tower model made to play as the plain tower, the way the tower packs that move do it:
/// every sequence that plays a custom tower activity gets a twin beside it, named
/// `<sequence>_act_<plain activity>`, playing the same animation for the plain activity. The
/// look's own sequences, names and order stay untouched, so nothing in the resource is renumbered,
/// and the plain tower's attachments are copied over so the projectile leaves from attach_attack1.
pub fn as_tower(model: &[u8], plain: &[u8]) -> Vec<u8> {
    let paired = crate::model_sequences::with_plain_activities(model).unwrap_or_else(|| model.to_vec());
    crate::model_attachments::attached_as(&paired, plain)
}
