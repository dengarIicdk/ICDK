//! Stock effects that outlive the model they were hung on.
//!
//! The client's item table is Valve's: a slot's stock block keeps asking for its stock particles
//! even after the model under that stock path is a bought one. Those particles find no bone and
//! spawn at world 0,0,0, in the river at the middle of the map. A loadout particle is asked for by
//! the hero's own block too and is the worst of the class: it plays in a match with no loadout
//! attachment at all.
//!
//! The second class is subtler and was what still drew a hero-shaped flame over the mid river: a
//! combat `*_ambient*.vpcf_c` of a set or an arcana names an attachment (`attach_wing_L`,
//! `attach_weapon`, a custom cloth point) that the model now under that slot simply does not have,
//! because a mix of an arcana body and another set's part carries neither set's points. Source 2
//! resolves an attachment by name on the entity's model; a name it cannot find leaves the control
//! point at the world origin, so the effect draws there for the whole match. Every such particle is
//! found here by reading the attachment names the compiled models in the pak really have and
//! replacing the particle that asks for a missing one with the game's own empty effect.

use std::collections::{HashMap, HashSet};

use crate::kv3::{self, Node, Value};
use crate::vpk::Entry;

/// The loadout particle's bytes, the one a build puts nowhere (the game's own empty effect).
pub const EMPTY_FX: &str = "particles/dev/empty_particle.vpcf_c";

fn is_loadout_particle(path: &str) -> bool {
    let p = path.to_ascii_lowercase();
    p.ends_with(".vpcf_c") && p.rsplit('/').next().is_some_and(|n| n.contains("loadout")) && !PROTECTED.contains(&p.as_str())
}

/// The world's own effects: the ring under the picked hero, which the emblem and the terrain
/// packs replace. No build silences them, whatever its item table says.
const PROTECTED: [&str; 4] = [
    "particles/ui_mouseactions/selected_ring.vpcf_c",
    "particles/ui_mouseactions/selected_ring_b.vpcf_c",
    "particles/ui_mouseactions/hero_ring.vpcf_c",
    "particles/ui_mouseactions/hero_ring_b.vpcf_c",
];

/// Every loadout-only particle in the pak made one that draws nothing; returns their paths.
pub fn silence_loadout_particles(entries: &mut [Entry], read: &dyn Fn(&str) -> Option<Vec<u8>>) -> Vec<String> {
    let Some(empty) = read(EMPTY_FX) else { return vec![] };
    let mut out = Vec::new();
    for e in entries.iter_mut() {
        let path = e.path().to_string();
        if !is_loadout_particle(&path) {
            continue;
        }
        *e = crate::vpk::entry_at(&path, empty.clone().into());
        out.push(path);
    }
    out
}

fn items(n: Option<&Node>) -> &[Node] {    match n.map(|n| &n.value) {
        Some(Value::Array { items, .. }) => items,
        _ => &[],
    }
}

fn text(n: Option<&Node>) -> String {
    n.and_then(Node::as_str).unwrap_or("").to_string()
}

/// Every attachment name a particle binds a control point to, in a configuration the match uses
/// (the `preview` one is the tool's own and is left out). A name here that the model does not have
/// is a control point at the world origin.
pub fn bound_attachments(particle: &[u8]) -> Vec<String> {
    crate::resource::data_block(particle)
        .ok()
        .and_then(|data| kv3::read(data).ok())
        .map(|kv| attachments_of(&kv.root))
        .unwrap_or_default()
}

/// The attachment names of a particle's definitions, without the tool's preview one.
pub fn attachments_of(root: &Node) -> Vec<String> {
    let mut out: Vec<String> = Vec::new();
    for config in items(root.get("m_controlPointConfigurations")) {
        if text(config.get("m_name")).eq_ignore_ascii_case("preview") {
            continue;
        }
        for driver in items(config.get("m_drivers")) {
            for key in ["m_attachmentName", "m_sAttachmentName"] {
                let name = text(driver.get(key));
                if !name.is_empty() && !out.iter().any(|n| n.eq_ignore_ascii_case(&name)) {
                    out.push(name);
                }
            }
        }
    }
    out
}

/// The hero a path belongs to, lowercased without the `hero_` prefix (`models/heroes/axe/x`,
/// `particles/units/heroes/hero_axe/x` and `particles/econ/items/axe/x` all answer `axe`).
pub fn hero_of(path: &str) -> Option<String> {
    let p = path.replace('\\', "/").to_ascii_lowercase();
    let parts: Vec<&str> = p.split('/').collect();
    // item effects live under `items/<hero>`, unit effects under `heroes/hero_<hero>`
    let at = parts
        .iter()
        .position(|s| *s == "heroes" || *s == "hero" || *s == "items")?;
    let raw = parts.get(at + 1)?.trim_start_matches("hero_");
    // a shared namespace such as `items/` of the game root is not a hero
    (!raw.is_empty() && raw != "econ" && raw != "particles").then(|| raw.to_string())
}

/// The first attachment a particle asks for that no model of its hero has, if any.
pub fn missing_attachment(
    particle: &[u8],
    hero_models: &HashSet<String>,
    all_models: &HashSet<String>,
) -> Option<String> {
    let known = if hero_models.is_empty() { all_models } else { hero_models };
    bound_attachments(particle)
        .into_iter()
        .find(|a| !known.contains(&a.to_ascii_lowercase()))
}

fn add_attachments(into: &mut HashMap<String, HashSet<String>>, all: &mut HashSet<String>, path: &str, model: &[u8]) {
    let names = crate::model_attachments::attachment_names(model);
    if names.is_empty() {
        return;
    }
    let set: HashSet<String> = names.into_iter().map(|n| n.to_ascii_lowercase()).collect();
    all.extend(set.iter().cloned());
    if let Some(hero) = hero_of(path) {
        into.entry(hero).or_default().extend(set);
    }
}

/// Every stock particle system the hero's own stock items ask the game to create, from the table
/// (which is the server's and is never shipped): `particles/...vpcf_c`.
pub fn stock_hero_systems(base: &[u8], heroes: &HashSet<String>) -> Vec<String> {
    let mut out: Vec<String> = Vec::new();
    let Ok(items) = crate::schema::items_of(base) else { return out };
    for item in items.iter().filter(|i| i.prefab == "default_item") {
        let Ok(own) = crate::item_builder_slots::item_heroes(base, item.start) else { continue };
        if !own.iter().any(|h| heroes.contains(&h.to_ascii_lowercase())) {
            continue;
        }
        let Ok(systems) = crate::item_builder::stock_particle_systems(base, item.start) else { continue };
        for s in systems {
            if !out.iter().any(|p| p.eq_ignore_ascii_case(&s)) {
                out.push(s);
            }
        }
    }
    out
}

/// Every particle the pak carries or asks the game for that binds a control point to an attachment
/// no model of its hero has made one that draws nothing. The hero's model namespace is the union of
/// the models the pak replaces and the stock models the table still names for him, so a particle
/// of a body we did not touch is never silenced. Returns the path and the name asked for in vain.
pub fn silence_orphan_attached_particles(
    entries: &mut Vec<Entry>,
    base: &[u8],
    read: &dyn Fn(&str) -> Option<Vec<u8>>,
) -> Vec<(String, String)> {
    let mut by_hero: HashMap<String, HashSet<String>> = HashMap::new();
    let mut all: HashSet<String> = HashSet::new();
    let pak_models: Vec<(String, Vec<u8>)> = entries
        .iter()
        .filter(|e| e.path().to_ascii_lowercase().ends_with(".vmdl_c"))
        .map(|e| (e.path().to_string(), e.data.to_vec()))
        .collect();
    for (path, data) in &pak_models {
        add_attachments(&mut by_hero, &mut all, path, data);
    }
    // the stock models the table still names for a hero the pak dresses: his body and his slots.
    // A model the pak already replaces is not counted: the model in the pak is what the game draws,
    // so a bone only the stock body has is exactly the bone that makes a particle fall to the
    // middle of the river.
    let replaced: HashSet<String> = pak_models.iter().map(|(p, _)| p.to_ascii_lowercase()).collect();
    for hero in by_hero.keys().cloned().collect::<Vec<_>>() {
        let mut stock: Vec<String> = Vec::new();
        if let Ok(body) = crate::item_builder_assets::hero_model_path(read, &hero) {
            stock.push(body);
        }
        if let Ok(models) = crate::item_builder_slots::stock_models(base, std::slice::from_ref(&hero)) {
            stock.extend(models);
        }
        for model in stock {
            let at = if model.to_ascii_lowercase().ends_with(".vmdl") { format!("{model}_c") } else { model };
            if replaced.contains(&at.to_ascii_lowercase()) {
                continue;
            }
            if let Some(bytes) = read(&at) {
                add_attachments(&mut by_hero, &mut all, &at, &bytes);
            }
        }
    }
    if all.is_empty() || by_hero.is_empty() {
        return vec![];
    }
    let Some(empty) = read(EMPTY_FX) else { return vec![] };
    // the particles in the pak, and the stock ones the unshipped table still creates for a hero
    let mut candidates: Vec<String> = entries
        .iter()
        .map(|e| e.path().to_string())
        .filter(|p| p.to_ascii_lowercase().ends_with(".vpcf_c"))
        .collect();
    let heroes: HashSet<String> = by_hero.keys().cloned().collect();
    for system in stock_hero_systems(base, &heroes) {
        if !candidates.iter().any(|p| p.eq_ignore_ascii_case(&system)) {
            candidates.push(system);
        }
    }
    let empty_set: HashSet<String> = HashSet::new();
    let mut out = Vec::new();
    for path in candidates {
        let hero = hero_of(&path);
        let hero_models = hero.as_ref().and_then(|h| by_hero.get(h)).unwrap_or(&empty_set);
        if hero_models.is_empty() {
            continue;
        }
        let Some(bytes) = entries
            .iter()
            .find(|e| e.path().eq_ignore_ascii_case(&path))
            .map(|e| e.data.to_vec())
            .or_else(|| read(&path))
        else {
            continue;
        };
        let Some(missing) = missing_attachment(&bytes, hero_models, &all) else { continue };
        match entries.iter().position(|e| e.path().eq_ignore_ascii_case(&path)) {
            Some(pos) => entries[pos] = crate::vpk::entry_at(&path, empty.clone().into()),
            None => entries.push(crate::vpk::entry_at(&path, empty.clone().into())),
        }
        out.push((path.to_ascii_lowercase(), missing));
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;

    #[test]
    fn a_loadout_particle_is_made_empty_and_a_combat_one_is_not() {
        let mut entries = vec![
            crate::vpk::entry_at("particles/units/heroes/hero_x/x_loadout.vpcf_c", Arc::from(&b"x"[..])),
            crate::vpk::entry_at("particles/units/heroes/hero_x/x_ambient.vpcf_c", Arc::from(&b"y"[..])),
        ];
        let read = |p: &str| (p == EMPTY_FX).then(|| b"empty".to_vec());
        let hit = silence_loadout_particles(&mut entries, &read);
        assert_eq!(hit, ["particles/units/heroes/hero_x/x_loadout.vpcf_c"]);
        assert_eq!(&*entries[0].data, b"empty");
        assert_eq!(&*entries[1].data, b"y");
    }

    #[test]
    fn a_hero_is_read_out_of_a_model_and_a_particle_path() {
        assert_eq!(hero_of("models/heroes/axe/axe.vmdl_c").as_deref(), Some("axe"));
        assert_eq!(hero_of("particles/units/heroes/hero_axe/axe_ambient.vpcf_c").as_deref(), Some("axe"));
        assert_eq!(hero_of("particles/dev/empty_particle.vpcf_c"), None);
    }

    #[test]
    fn a_particle_names_the_attachments_it_binds_and_skips_the_preview() {
        let obj = |pairs: Vec<(&str, Node)>| {
            let mut m = indexmap::IndexMap::new();
            for (k, v) in pairs {
                m.insert(k.to_string(), v);
            }
            Node::new(Value::Object(m))
        };
        let arr = |items: Vec<Node>| Node::new(Value::Array { items, element: None, ty: 0 });
        let s = |v: &str| Node::new(Value::Str(v.to_string()));
        let driver = |name: Option<&str>| match name {
            Some(n) => obj(vec![("m_iAttachType", s("PATTACH_POINT_FOLLOW")), ("m_attachmentName", s(n))]),
            None => obj(vec![("m_iAttachType", s("PATTACH_WORLDORIGIN"))]),
        };
        let preview = obj(vec![("m_name", s("preview")), ("m_drivers", arr(vec![driver(Some("attach_hitloc"))]))]);
        let default = obj(vec![
            ("m_name", s("default")),
            ("m_drivers", arr(vec![driver(Some("attach_wing_L")), driver(None), driver(Some("attach_wing_L"))])),
        ]);
        let root = obj(vec![("m_controlPointConfigurations", arr(vec![preview, default]))]);
        assert_eq!(attachments_of(&root), ["attach_wing_L"]);
        assert!(bound_attachments(b"not a resource").is_empty());
    }

    #[test]
    fn an_attachment_the_model_does_not_have_is_named_and_one_it_has_is_not() {
        let hero: HashSet<String> = ["attach_hitloc".to_string()].into_iter().collect();
        assert_eq!(missing_attachment_for(&["attach_hitloc"], &hero), None);
        assert_eq!(missing_attachment_for(&["attach_wing_L"], &hero).as_deref(), Some("attach_wing_L"));
        assert_eq!(missing_attachment_for(&[], &hero), None);
    }

    /// The pure part of the audit, without a compiled particle to read.
    fn missing_attachment_for(asked: &[&str], known: &HashSet<String>) -> Option<String> {
        asked.iter().map(|s| s.to_string()).find(|a| !known.contains(&a.to_ascii_lowercase()))
    }
}
