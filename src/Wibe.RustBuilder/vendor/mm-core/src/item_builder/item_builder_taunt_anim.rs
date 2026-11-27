//! A picked taunt's own animation, moved over the stock taunt sequences of the same hero body.
//!
//! The client's item table is Valve's (no table is shipped), so it still asks for the taunt the
//! account wears. Every taunt animation of a hero already lives in that hero's model, under its own
//! sequence name. Copying the picked sequence's description under each stock taunt sequence's name
//! makes the stock table's request play the picked dance, with the picked sound and effects, while
//! the item row and the account stay untouched. A rare idle is retargeted the same way when asked.

use crate::item_builder::item_visuals;
use crate::item_builder_slots::item_heroes;
use crate::item_builder::item_builder_taunt::taunt_activity;
use crate::kv3::{self, Block, Node, Value};
use crate::schema::items_of;
use crate::vpk::Entry;

fn text(n: Option<&Node>) -> String {
    n.and_then(Node::as_str).unwrap_or("").to_string()
}

fn items(n: Option<&Node>) -> &[Node] {
    n.map_or(&[], Node::items)
}

fn name_at(desc: &Node) -> String {
    text(desc.get("m_sName"))
}

fn acts(desc: &Node) -> Vec<String> {
    items(desc.get("m_activityArray")).iter().map(|a| text(a.get("m_name"))).collect()
}

/// The model's sequence that answers to a table modifier (its own name or one of its activities).
fn sequence_for(aseq: &Block, marker: &str) -> Option<String> {
    let descs = items(aseq.root.get("m_localS1SeqDescArray"));
    let by_act: Vec<&Node> = descs
        .iter()
        .filter(|d| acts(d).iter().any(|a| a.eq_ignore_ascii_case(marker)))
        .collect();
    // a marker can name both the loadout statue effect and the taunt itself: the match plays the
    // real taunt, the showcase the effigy, so the combat activity comes first
    by_act
        .iter()
        .find(|d| acts(d).iter().any(|a| a.eq_ignore_ascii_case("ACT_DOTA_TAUNT")))
        .or_else(|| by_act.first())
        .map(|d| name_at(d))
        .filter(|n| !n.is_empty())
        .or_else(|| {
            descs
                .iter()
                .find(|d| name_at(d).eq_ignore_ascii_case(marker))
                .map(name_at)
                .filter(|n| !n.is_empty())
        })
}

/// The model's sequence names that play one of `wanted`, in model order.
fn sequences_playing(aseq: &Block, wanted: &[&str]) -> Vec<String> {
    items(aseq.root.get("m_localS1SeqDescArray"))
        .iter()
        .filter(|d| {
            let own = acts(d);
            wanted.iter().any(|w| own.iter().any(|a| a.eq_ignore_ascii_case(w)))
        })
        .map(name_at)
        .filter(|n| !n.is_empty())
        .collect()
}

/// Any sequence the model itself marks as a taunt, for an item whose table marker names none.
/// `ACT_DOTA_TAUNT`, its statue/effigy twin and every numbered variant count.
fn taunt_sequences(aseq: &Block) -> Vec<String> {
    items(aseq.root.get("m_localS1SeqDescArray"))
        .iter()
        .filter(|d| acts(d).iter().any(|a| a.to_ascii_uppercase().contains("TAUNT")))
        .map(name_at)
        .filter(|n| !n.is_empty())
        .collect()
}

/// The table's activity modifier a stock item's taunt asks for. It is the sequence's own second
/// activity name ("arcana_skip"), never the sequence name itself ("arc_mid_walk_chainskip").
fn table_modifier(base: &[u8], id: &str) -> Option<String> {
    let items = items_of(base).ok()?;
    let item = items.iter().find(|i| i.id == id)?;
    let visuals = item_visuals(base, item.start).ok()??;
    taunt_activity(&visuals).map(|(_, modifier)| modifier)
}

/// Whether a sequence of this model really carries a clip: its fetch names at least one sequence
/// of the model's animation array (a descriptor copied from a body without that clip has none).
fn plays_an_animation(aseq: &Block, name: &str) -> bool {
    let Some(desc) = items(aseq.root.get("m_localS1SeqDescArray"))
        .iter()
        .find(|d| name_at(d).eq_ignore_ascii_case(name))
    else {
        return false;
    };
    let refs = items(desc.get("m_fetch").and_then(|f| f.get("m_localReferenceArray")));
    !refs.is_empty()
}

/// The ASEQ block retargeted: every `target` name plays the source sequence's data. The target
/// descriptor keeps its slot and its exact name in `m_localSequenceNameArray`; only the fetch
/// references, layers, and activities are replaced by the source's. Renaming or appending a
/// descriptor breaks the name-to-index table the engine actually uses, which is why the earlier
/// string-only patch did nothing in game.
pub fn retarget_aseq(aseq: &mut Block, source: &str, targets: &[String]) -> bool {
    let Some(descs) = aseq.root.get_mut("m_localS1SeqDescArray").and_then(|n| match &mut n.value {
        Value::Array { items, .. } => Some(items),
        _ => None,
    }) else {
        return false;
    };
    retarget_descs(descs, source, targets)
}

/// The descriptor members that hold how a sequence is played. The target keeps its own name and
/// its own activity list: the game finds a taunt by the item's activity modifier ("sharp_blade",
/// "face_me"), so a descriptor that lost its own activities is never asked for at all and the dance
/// silently never plays, which is what a full member copy did before.
const PLAYED: [&str; 8] = [
    "m_fetch",
    "m_nLocalWeightlist",
    "m_autoLayerArray",
    "m_IKLockArray",
    "m_transition",
    "m_SequenceKeys",
    "m_keyValueText",
    "m_footMotion",
];

/// Copy the source sequence's playable data into the target descriptor in place, keeping the
/// target's own name and activities. The local sequence-name array still indexes this descriptor,
/// and the game still finds it under the account's own taunt.
pub fn retarget_descs(descs: &mut Vec<Node>, source: &str, targets: &[String]) -> bool {
    let Some(from) = descs.iter().find(|d| name_at(d).eq_ignore_ascii_case(source)).cloned() else {
        return false;
    };
    let mut changed = false;
    for target in targets {
        if target.eq_ignore_ascii_case(source) {
            continue;
        }
        let Some(old) = descs.iter_mut().find(|d| name_at(d).eq_ignore_ascii_case(target)) else {
            continue;
        };
        if let (Value::Object(m), Value::Object(src)) = (&mut old.value, &from.value) {
            for key in PLAYED {
                if let Some(value) = src.get(key) {
                    m.insert(key.to_string(), value.clone());
                }
            }
            // the flags that decide looping and layering belong to the animation played, but the
            // target keeps being visible/autoplay as it was so a stock taunt keeps its own entry
            if let Some(Node { value: Value::Object(src_flags), .. }) = src.get("m_flags") {
                let own = m.get_mut("m_flags").and_then(|n| match &mut n.value {
                    Value::Object(f) => Some(f),
                    _ => None,
                });
                if let Some(own) = own {
                    for (k, v) in src_flags {
                        if k != "m_bHidden" {
                            own.insert(k.clone(), v.clone());
                        }
                    }
                }
            }
        }
        changed = true;
    }
    changed
}

/// The compiled model with its stock taunt (and, when `rare`, its rare idle) sequences playing the
/// picked taunt's animation, or None when the model has neither side.
pub fn retarget_model(model: &[u8], source: &str, targets: &[String]) -> Option<Vec<u8>> {
    if targets.is_empty() {
        return None;
    }
    let mut aseq = kv3::read(crate::resource::data(model, "ASEQ", 0).ok()?).ok()?;
    if !retarget_aseq(&mut aseq, source, targets) {
        return None;
    }
    let next = kv3::write(&aseq).ok()?;
    crate::resource::replace(model, "ASEQ", 0, &next).ok()
}

/// Retarget every picked taunt in the pak's existing hero body models. The body is the base
/// repository's when there is one, so the entry already in `entries` is the model to change; adding
/// a body of our own would lose the base's fixes.
pub fn apply_for_picks(
    entries: &mut Vec<Entry>,
    base: &[u8],
    picks: &[(String, bool)],
    read: &dyn Fn(&str) -> Option<Vec<u8>>,
    mut log: impl FnMut(String),
) {
    for (item_id, rare) in picks {
        let Ok(items) = items_of(base) else { continue };
        let Some(source_item) = items.iter().find(|i| &i.id == item_id) else { continue };
        let Ok(heroes) = item_heroes(base, source_item.start) else { continue };
        let Ok(targets) = crate::item_builder::item_builder_taunt::taunt_targets(base, item_id) else { continue };
        let Some(source_marker) = table_modifier(base, item_id) else { continue };
        let target_markers: Vec<String> = targets.iter().filter_map(|t| table_modifier(base, t)).collect();
        for hero in heroes {
            let Ok(model) = crate::item_builder_assets::hero_model_path(read, &hero) else { continue };
            let at = if model.to_lowercase().ends_with(".vmdl") { format!("{model}_c") } else { model };
            // a taunt pick can be the only thing chosen for a hero: then the base repository has
            // no body for him and his stock body is the one the dance has to enter
            let pos = match entries.iter().position(|e| e.path().eq_ignore_ascii_case(&at)) {
                Some(pos) => pos,
                None => match read(&at) {
                    Some(stock) => {
                        entries.push(crate::vpk::entry_at(&at, stock.into()));
                        entries.len() - 1
                    }
                    None => {
                        log(format!("taunt {item_id}: no {at} in the pak to retarget"));
                        continue;
                    }
                },
            };
            let mut bytes = entries[pos].data.to_vec();
            // the base repository's body strips the item-specific second activity names, so the
            // table's marker is resolved on the game's stock body and only the sequence name is
            // carried to the base body
            let stock_bytes = read(&at);
            let stock = stock_bytes
                .as_ref()
                .and_then(|b| crate::kv3::read(crate::resource::data(b, "ASEQ", 0).ok()?).ok());
            let own = crate::kv3::read(crate::resource::data(&bytes, "ASEQ", 0).ok().unwrap_or_default()).ok();
            let (Some(mut own), Some(reference)) = (own, stock.or_else(|| Some(crate::kv3::read(crate::resource::data(&bytes, "ASEQ", 0).ok()?).ok()?))) else {
                continue;
            };
            let source = sequence_for(&reference, &source_marker)
                .or_else(|| sequence_for(&own, &source_marker))
                .or_else(|| taunt_sequences(&reference).into_iter().next())
                .or_else(|| taunt_sequences(&own).into_iter().next());
            let Some(mut source) = source else {
                log(format!("taunt {item_id}: {at} has no sequence for {source_marker}"));
                continue;
            };
            // The dance has to be a clip this body really carries: a base-repository body that
            // dropped the sequence's animation would copy an empty reference and the dance would
            // never play, so the game's own body (which embeds every clip of the hero) is used.
            if !plays_an_animation(&own, &source) {
                let Some(stock_bytes) = stock_bytes.as_ref() else {
                    log(format!("taunt {item_id}: {at} carries no clip for {source}"));
                    continue;
                };
                let Some(stock_aseq) = crate::resource::data(stock_bytes, "ASEQ", 0)
                    .ok()
                    .and_then(|d| crate::kv3::read(d).ok())
                else {
                    continue;
                };
                let Some(stock_source) = sequence_for(&stock_aseq, &source_marker)
                    .or_else(|| taunt_sequences(&stock_aseq).into_iter().next())
                else {
                    log(format!("taunt {item_id}: {at} has no stock clip for {source_marker}"));
                    continue;
                };
                own = stock_aseq;
                source = stock_source;
                bytes = stock_bytes.clone();
                log(format!("taunt {item_id}: {hero} body {at} has no clip, the game's body is used"));
            }
            let mut want: Vec<String> = target_markers
                .iter()
                .filter_map(|m| sequence_for(&reference, m).or_else(|| sequence_for(&own, m)))
                .collect();
            // Every sequence the model marks as a taunt is a possible key the stock table
            // may trigger. Retarget all of them, not just the marker of the item equipped on
            // the account, so any base taunt plays the picked dance.
            want.extend(taunt_sequences(&own));
            if *rare {
                for act in ["ACT_DOTA_IDLE_RARE", "ACT_DOTA_LOADOUT_RARE", "ACT_DOTA_IDLE", "ACT_DOTA_LOADOUT"] {
                    want.extend(sequences_playing(&own, &[act]));
                }
                // The hero screen in the menu plays the LOADOUT/SPAWN clip, not the taunt key.
                // Every clip whose name carries a showcase word gets the dance, so the effigy in
                // the shrine and the versus screen show it too on an account with no taunt.
                let showcase = ["loadout", "idle_rare", "spawn", "versus", "statue", "effigy"];
                if let Some(Node { value: Value::Array { items: descs, .. }, .. }) =
                    own.root.get("m_localS1SeqDescArray")
                {
                    for desc in descs {
                        let name = name_at(desc);
                        let lower = name.to_ascii_lowercase();
                        if !name.is_empty() && showcase.iter().any(|w| lower.contains(w)) {
                            want.push(name);
                        }
                    }
                }
            }
            want.sort();
            want.dedup();
            match retarget_model(&bytes, &source, &want) {
                Some(next) => {
                    entries[pos] = crate::vpk::entry_at(&at, next.into());
                    log(format!("taunt {item_id}: {hero} body {at} plays {source} for {} stock sequences", want.len()));
                }
                None => log(format!("taunt {item_id}: {hero} body has no stock taunt sequence to carry {source}")),
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn desc(name: &str, marker: &str, clip: &str) -> Node {
        let mut m = indexmap::IndexMap::new();
        m.insert("m_sName".to_string(), Node::new(Value::Str(name.to_string())));
        let mut activity = indexmap::IndexMap::new();
        activity.insert("m_name".to_string(), Node::new(Value::Str(marker.to_string())));
        m.insert(
            "m_activityArray".to_string(),
            Node::new(Value::Array { items: vec![Node::new(Value::Object(activity))], element: None, ty: 0 }),
        );
        let mut fetch = indexmap::IndexMap::new();
        fetch.insert(
            "m_localReferenceArray".to_string(),
            Node::new(Value::Array {
                items: vec![Node::new(Value::Str(clip.to_string()))],
                element: None,
                ty: 0,
            }),
        );
        m.insert("m_fetch".to_string(), Node::new(Value::Object(fetch)));
        m.insert("m_flags".to_string(), Node::new(Value::Object(indexmap::IndexMap::new())));
        Node::new(Value::Object(m))
    }

    fn clip_of(d: &Node) -> String {
        items(d.get("m_fetch").and_then(|f| f.get("m_localReferenceArray")))
            .first()
            .map(|n| text(Some(n)))
            .unwrap_or_default()
    }

    #[test]
    fn a_stock_taunt_plays_the_picked_dance_but_keeps_its_own_activity() {
        let mut list = vec![
            desc("taunt_sharp_blade_anim", "sharp_blade", "the_dance"),
            desc("taunt_face_me_anim", "face_me", "the_face"),
        ];
        assert!(retarget_descs(&mut list, "taunt_sharp_blade_anim", &["taunt_face_me_anim".into()]));
        assert_eq!(list.len(), 2);
        let stock = list.iter().find(|d| name_at(d) == "taunt_face_me_anim").unwrap();
        // the dance is played...
        assert_eq!(clip_of(stock), "the_dance");
        // ...and the account's own taunt still finds its sequence by its own activity
        assert_eq!(acts(stock), ["face_me"]);
        assert_eq!(name_at(stock), "taunt_face_me_anim");
        assert!(!list.iter().any(|d| name_at(d).starts_with("@wibe_stock_")));
    }

    #[test]
    fn a_name_the_model_has_not_is_left_alone() {
        let mut list = vec![
            desc("taunt_sharp_blade_anim", "sharp_blade", "the_dance"),
            desc("taunt_face_me_anim", "face_me", "the_face"),
        ];
        assert!(!retarget_descs(&mut list, "not_here", &["taunt_face_me_anim".into()]));
        assert_eq!(list.len(), 2);
        assert_eq!(clip_of(&list[1]), "the_face");
    }
}
