//! A model put under another model's path that numbers its sequences as that one does.
//!
//! Online the server picks a unit's animation in its own copy of the plain model and sends the
//! client a number: the place of the sequence in the model's name list (ASEQ
//! m_localSequenceNameArray). So the plain model's names come first, in the plain order, each
//! matched to the item's sequence of the same name (any case, with or without underscores) or of
//! the same activity; the rest follow in their own order, and every reference into the list moves
//! with its entry. A plain name with nothing left to stand for it gets a copy of the item's own
//! sequence for that activity under the plain name; a part with no activity holds its place with
//! the item's bind pose. A model with no run or no idle of its own still gets none.

use std::collections::{HashMap, HashSet};

use crate::kv3::{self, Block, Node, Value};
use crate::resource;

fn text(n: Option<&Node>) -> String {
    n.and_then(Node::as_str).unwrap_or("").to_string()
}

fn items(n: Option<&Node>) -> &[Node] {
    n.map(Node::items).unwrap_or(&[])
}

#[derive(Debug, Clone)]
struct Described {
    name: String,
    act: Option<String>,
    /// its place in m_localS1SeqDescArray
    at: usize,
}

/// Each sequence's name and the activity it plays (the first ACT_ of its list), or None.
fn described(root: &Node) -> Vec<Described> {
    items(root.get("m_localS1SeqDescArray"))
        .iter()
        .enumerate()
        .map(|(at, d)| Described {
            name: text(d.get("m_sName")),
            act: items(d.get("m_activityArray")).iter().map(|a| text(a.get("m_name"))).find(|n| n.starts_with("ACT_")),
            at,
        })
        .collect()
}

/// A number in a sequence that points into the name list: an entry of what it fetches, or the
/// reference of one of the layers it plays.
#[derive(Debug, Clone, Copy)]
enum Ptr {
    Fetch(usize),
    Layer(usize),
}

fn pointers(desc: &Node) -> Vec<Ptr> {
    let mut out = Vec::new();
    if let Some(refs) = desc.get("m_fetch").and_then(|f| f.get("m_localReferenceArray")) {
        if let Value::Array { items, .. } = &refs.value {
            out.extend(
                items
                    .iter()
                    .enumerate()
                    .filter(|(_, r)| matches!(r.value, Value::Number { .. }))
                    .map(|(i, _)| Ptr::Fetch(i)),
            );
        }
    }
    for (j, layer) in items(desc.get("m_autoLayerArray")).iter().enumerate() {
        if matches!(layer.value, Value::Object(_))
            && matches!(layer.get("m_nLocalReference").map(|r| &r.value), Some(Value::Number { .. }))
        {
            out.push(Ptr::Layer(j));
        }
    }
    out
}

fn at_ptr(desc: &mut Node, p: Ptr) -> Option<&mut Node> {
    match p {
        Ptr::Fetch(i) => match &mut desc.get_mut("m_fetch")?.get_mut("m_localReferenceArray")?.value {
            Value::Array { items, .. } => items.get_mut(i),
            _ => None,
        },
        Ptr::Layer(j) => match &mut desc.get_mut("m_autoLayerArray")?.value {
            Value::Array { items, .. } => items.get_mut(j)?.get_mut("m_nLocalReference"),
            _ => None,
        },
    }
}

/// Put a new number at a pointer. A fetch list that takes one becomes untyped: a typed array writes
/// its items in its element's storage, and a 0 or 1 stored as a type has none.
fn set_ptr(desc: &mut Node, p: Ptr, n: Node) -> Option<()> {
    if let Ptr::Fetch(_) = p {
        if let Value::Array { element, .. } = &mut desc.get_mut("m_fetch")?.get_mut("m_localReferenceArray")?.value {
            *element = None;
        }
    }
    *at_ptr(desc, p)? = n;
    Some(())
}

/// A number written anew as an int32, for one stored as its type alone.
fn made_int(v: i64) -> Option<Node> {
    let v = i32::try_from(v).ok()?;
    Some(Node::new(Value::Number { ty: 11, cell: None, type_at: None, raw: Some(v.to_le_bytes().to_vec()) }))
}

/// `v` as a number of the same type and width as `like`, which a typed array's lane needs.
fn made_like(like: &Node, v: i64) -> Option<Node> {
    let Value::Number { ty, cell, raw, .. } = &like.value else { return made_int(v) };
    let width = cell.map(|c| c.width as usize).or(raw.as_ref().map(Vec::len)).unwrap_or(0);
    if width == 0 {
        return made_int(v);
    }
    let bytes = match width {
        8 => v.to_le_bytes().to_vec(),
        w if w <= 6 => {
            // writeIntLE: a signed value that must fit in w bytes
            let bits = (w * 8) as u32;
            let (lo, hi) = (-(1i64 << (bits - 1)), (1i64 << (bits - 1)) - 1);
            if v < lo || v > hi {
                return None;
            }
            v.to_le_bytes()[..w].to_vec()
        }
        _ => return None,
    };
    Some(Node::new(Value::Number { ty: *ty, cell: None, type_at: None, raw: Some(bytes) }))
}

/// The "<word>_" every one of these names starts with ("donkey_", "donkey_wings_"), or "".
fn name_prefix(names: &[String]) -> String {
    if names.is_empty() {
        return String::new();
    }
    let lower: Vec<String> = names.iter().map(|n| n.to_lowercase()).collect();
    let mut prefix = lower[0][..lower[0].rfind('_').map_or(0, |i| i + 1)].to_string();
    while !prefix.is_empty() && !lower.iter().all(|n| n.starts_with(&prefix)) {
        let search = &prefix[..prefix.len().saturating_sub(1)];
        let next = search.rfind('_').map_or(0, |i| i + 1);
        if next >= prefix.len() {
            return String::new();
        }
        prefix.truncate(next);
    }
    prefix
}

/// A sequence that plays no activity, found by its name without the plain model's own prefix.
fn same_rest(names: &[String], used: &HashSet<usize>, name: &str, prefix: &str) -> Option<usize> {
    let at = if name.starts_with('@') { "@" } else { "" };
    let rest = name[at.len()..].to_lowercase();
    if prefix.is_empty() || !rest.starts_with(prefix) || rest == prefix {
        return None;
    }
    let tail = &rest[prefix.len()..];
    let free: Vec<(String, usize)> = names
        .iter()
        .enumerate()
        .map(|(i, n)| (n.to_lowercase(), i))
        .filter(|(n, i)| !used.contains(i) && n.starts_with('@') == !at.is_empty())
        .collect();
    let whole = free.iter().find(|(n, _)| n.get(at.len()..) == Some(tail));
    let end = format!("_{tail}");
    whole.or_else(|| free.iter().find(|(n, _)| n.ends_with(&end))).map(|(_, i)| *i)
}

/// Any sequence left over, for a plain one that plays no activity and has no namesake.
fn spare(names: &[String], used: &HashSet<usize>, name: &str) -> Option<usize> {
    let free: Vec<(usize, &String)> = names.iter().enumerate().filter(|(i, _)| !used.contains(i)).collect();
    free.iter().find(|(_, n)| n.starts_with('@') == name.starts_with('@')).or(free.first()).map(|(i, _)| *i)
}

fn is_bind_pose(name: &str) -> bool {
    let l = name.to_lowercase();
    l == "bindpose" || l == "bind_pose"
}

/// The item's sequence to copy for a plain one that has nothing left.
fn stand_in<'a>(seqs: &'a [Described], act: Option<&str>) -> Option<&'a Described> {
    let Some(act) = act.filter(|a| !a.is_empty()) else {
        return seqs.iter().find(|s| s.act.is_none() && is_bind_pose(&s.name));
    };
    let by = |a: &str| {
        seqs.iter()
            .find(|s| s.act.as_deref() == Some(a) && !s.name.starts_with('@'))
            .or_else(|| seqs.iter().find(|s| s.act.as_deref() == Some(a)))
    };
    let common = act.strip_suffix("_RARE").unwrap_or(act);
    by(act).or_else(|| by(common)).or_else(|| {
        if common == "ACT_DOTA_RUN" || common == "ACT_DOTA_IDLE" {
            None
        } else {
            by("ACT_DOTA_IDLE")
        }
    })
}

fn place_of(map: &HashMap<usize, usize>, old: f64) -> Option<usize> {
    (old >= 0.0 && old.fract() == 0.0).then(|| map.get(&(old as usize)).copied()).flatten()
}

fn at_ref(desc: &Node, p: Ptr) -> Option<&Node> {
    match p {
        Ptr::Fetch(i) => desc.get("m_fetch")?.get("m_localReferenceArray")?.items().get(i),
        Ptr::Layer(j) => desc.get("m_autoLayerArray")?.items().get(j)?.get("m_nLocalReference"),
    }
}

fn desc_list(root: &mut Node) -> Option<&mut Vec<Node>> {
    match &mut root.get_mut("m_localS1SeqDescArray")?.value {
        Value::Array { items, .. } => Some(items),
        _ => None,
    }
}

/// A stand-in with its tree and the old numbers of its references.
type Copy = (StandIn, Node, Vec<(Ptr, f64)>);

struct StandIn {
    place: usize,
    name: String,
    act: Option<String>,
    from: Described,
}

/// `model` with its sequences numbered as `plain` numbers its own, or None where that cannot be
/// done: a plain run or idle with nothing to play it, a list either has not got, or sequences of
/// kinds whose references this does not move (multi, synth, command).
pub fn numbered_as(model: &[u8], plain: &[u8]) -> Option<Vec<u8>> {
    for f in [model, plain] {
        if !resource::blocks(f).ok()?.iter().any(|b| b.name == "ASEQ") {
            return None;
        }
    }
    let theirs = kv3::read(resource::data(plain, "ASEQ", 0).ok()?).ok()?;
    let mut kv: Block = kv3::read(resource::data(model, "ASEQ", 0).ok()?).ok()?;
    let is_list = |n: Option<&Node>| matches!(n.map(|n| &n.value), Some(Value::Array { .. }));
    if !is_list(kv.root.get("m_localSequenceNameArray")) || !is_list(theirs.root.get("m_localSequenceNameArray")) {
        return None;
    }
    for key in ["m_localMultiSeqDescArray", "m_localSynthAnimDescArray", "m_localCmdSeqDescArray"] {
        if !items(kv.root.get(key)).is_empty() {
            return None;
        }
    }
    let names: Vec<String> = items(kv.root.get("m_localSequenceNameArray")).iter().map(|n| text(Some(n))).collect();
    let wanted: Vec<String> =
        items(theirs.root.get("m_localSequenceNameArray")).iter().map(|n| text(Some(n))).collect();
    if names == wanted {
        return Some(model.to_vec());
    }

    // a name described twice plays what its last description does, in the place of its first
    let mut plain_acts: HashMap<String, Option<String>> = HashMap::new();
    let mut plain_order: Vec<String> = Vec::new();
    for s in described(&theirs.root) {
        if !plain_acts.contains_key(&s.name) {
            plain_order.push(s.name.clone());
        }
        plain_acts.insert(s.name, s.act);
    }
    let act_of = |name: &str| plain_acts.get(name).cloned().flatten();
    let seqs = described(&kv.root);
    let mut used: HashSet<usize> = HashSet::new();
    let mut stand_ins: Vec<StandIn> = Vec::new();
    let mut order: Vec<Option<usize>> = Vec::new();
    // "bindPose" is "bind_pose" in half of Invoker's spirits, and it plays no activity to find it by
    let loose = |n: &str| -> String {
        n.to_lowercase().chars().filter(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || *c == '@').collect()
    };
    let index_of = |n: &str| names.iter().position(|x| x == n);
    let acted: Vec<String> = plain_order.iter().filter(|n| act_of(n).is_some()).cloned().collect();
    let prefix = name_prefix(&acted);
    for name in &wanted {
        let act = act_of(name);
        let mut at = index_of(name).filter(|i| !used.contains(i));
        if at.is_none() {
            let ln = loose(name);
            at = names.iter().enumerate().position(|(i, n)| !used.contains(&i) && loose(n) == ln);
        }
        if at.is_none() {
            // a sequence of its own name before one played through another ("@forge_spirit_runx")
            let same: Vec<&Described> = match &act {
                Some(a) => seqs
                    .iter()
                    .filter(|s| s.act.as_ref() == Some(a) && index_of(&s.name).is_none_or(|i| !used.contains(&i)))
                    .collect(),
                None => vec![],
            };
            let pick = same.iter().find(|s| !s.name.starts_with('@')).or(same.first());
            at = pick.and_then(|p| index_of(&p.name));
        }
        if at.is_none() {
            at = same_rest(&names, &used, name, &prefix);
        }
        if at.is_none() && act.is_none() {
            at = spare(&names, &used, name);
        }
        match at {
            Some(i) => {
                used.insert(i);
                order.push(Some(i));
            }
            None => {
                let from = stand_in(&seqs, act.as_deref())?.clone();
                let written = if names.contains(name) { format!("{name}_d2mm") } else { name.clone() };
                stand_ins.push(StandIn { place: order.len(), name: written, act, from });
                order.push(None);
            }
        }
    }
    for i in 0..names.len() {
        if !used.contains(&i) {
            order.push(Some(i));
        }
    }
    let place_map: HashMap<usize, usize> =
        order.iter().enumerate().filter_map(|(place, old)| old.map(|o| (o, place))).collect();

    let mut root = std::mem::replace(&mut kv.root, Node::new(Value::Other { ty: 1, value: None }));
    // the copies' references, read before any original is renumbered: they share its cells
    let mut copies: Vec<Copy> = Vec::new();
    for s in stand_ins {
        let desc = items(root.get("m_localS1SeqDescArray")).get(s.from.at)?.clone();
        let refs: Vec<(Ptr, f64)> = pointers(&desc)
            .into_iter()
            .map(|p| (p, at_ref(&desc, p).and_then(|n| kv.number_of(n)).unwrap_or(f64::NAN)))
            .collect();
        copies.push((s, desc, refs));
    }
    if let Some(descs) = desc_list(&mut root) {
        for s in &seqs {
            let desc = descs.get_mut(s.at)?;
            for p in pointers(desc) {
                let node = at_ptr(desc, p)?;
                let old = kv.number_of(node)?;
                if old < 0.0 {
                    continue;
                }
                let place = place_of(&place_map, old)?;
                if place as f64 != old && !kv.set_number(node, place as f64).ok()? {
                    set_ptr(desc, p, made_int(place as i64)?)?;
                }
            }
        }
    }
    if !copies.is_empty() && desc_list(&mut root).is_none() {
        return None;
    }
    let mut copy_names: HashMap<usize, String> = HashMap::new();
    for (s, mut desc, refs) in copies {
        for (p, old) in refs {
            let place = place_of(&place_map, old)?;
            let like = at_ref(&desc, p)?.clone();
            set_ptr(&mut desc, p, made_like(&like, place as i64)?)?;
        }
        let Value::Object(members) = &mut desc.value else { return None };
        members.insert("m_sName".into(), Node::new(Value::Str(s.name.clone())));
        // played for the plain activity: a courier's idle standing in for the donkey's rare one is
        // its rare one now, as the donkey's was
        if let Some(act) = s.act.as_ref().filter(|a| Some(*a) != s.from.act.as_ref()) {
            if let Some(Node { value: Value::Array { items: acts, .. }, .. }) = members.get_mut("m_activityArray") {
                for a in acts.iter_mut() {
                    if !text(a.get("m_name")).starts_with("ACT_") {
                        continue;
                    }
                    if let Value::Object(m) = &mut a.value {
                        m.insert("m_name".into(), Node::new(Value::Str(act.clone())));
                        break;
                    }
                }
            }
        }
        desc_list(&mut root)?.push(desc);
        copy_names.insert(s.place, s.name);
    }
    if let Some(Node { value: Value::Array { items: list, .. }, .. }) = root.get_mut("m_localSequenceNameArray") {
        let old = std::mem::take(list);
        *list = order
            .iter()
            .enumerate()
            .map(|(place, i)| match i {
                Some(i) => old[*i].clone(),
                None => Node::new(Value::Str(copy_names.get(&place).cloned().unwrap_or_default())),
            })
            .collect();
    }
    kv.root = root;
    resource::replace(model, "ASEQ", 0, &kv3::write(&kv).ok()?).ok()
}

/// The plain activity a custom tower one stands for, the way the working tower packs pair them.
fn plain_act(name: &str) -> Option<&'static str> {
    match name {
        "ACT_DOTA_CUSTOM_TOWER_IDLE" => Some("ACT_DOTA_IDLE"),
        "ACT_DOTA_CUSTOM_TOWER_IDLE_RARE" => Some("ACT_DOTA_IDLE_RARE"),
        "ACT_DOTA_CUSTOM_TOWER_ATTACK" => Some("ACT_DOTA_ATTACK"),
        "ACT_DOTA_CUSTOM_TOWER_DIE" => Some("ACT_DOTA_DIE"),
        _ => None,
    }
}

/// A look's tower model made to answer to the plain tower's activities, the way the packs that are
/// known to move do it: every sequence that plays a custom tower activity gets a twin next to it
/// named `<sequence>_<plain activity>`, the way the packs name them (`idle_default_act_dota_idle`), playing the same animation for the plain activity. The
/// original sequences, their names and their order stay exactly as the look shipped them, the two
/// lists only grow by the same count at their end, and no index anywhere is renumbered — so the
/// resource the engine loads is the resource it was given, plus the pairs it expects to find.
pub fn with_plain_activities(model: &[u8]) -> Option<Vec<u8>> {
    if !resource::blocks(model).ok()?.iter().any(|b| b.name == "ASEQ") {
        return None;
    }
    let mut kv = kv3::read(resource::data(model, "ASEQ", 0).ok()?).ok()?;
    if !add_plain_activities(&mut kv.root) {
        return None;
    }
    let next = kv3::write(&kv).ok()?;
    resource::replace(model, "ASEQ", 0, &next).ok()
}

/// The same on a parsed sequence block: true when the twins went in.
/// The same pose of the same family with the aim taken out: `idle_multi_aim` has `@idle_neutral`
/// beside it, `idle_v02_multi_aim` has `@idle_neutral_v02`, `attack_multi_aim` has `@attack_neutral`.
///
/// A tower's idle is a blend across the aim directions, driven by a pose parameter the item table
/// owns. With the table off nothing drives that parameter and the tower stands at whatever the
/// blend's default happens to be — off the lane. The neutral sequence is the one the authors drew
/// facing the lane, so a plain activity is twinned from it.
fn neutral_sibling(name: &str, names: &[String]) -> Option<String> {
    let lower = name.to_ascii_lowercase();
    if !lower.contains("multi_aim") {
        return None;
    }
    let family = lower.split('_').next().unwrap_or("").to_string();
    if family.is_empty() {
        return None;
    }
    let key = variant_key(&lower, &family);
    let mut best: Option<(usize, String)> = None;
    for candidate in names {
        let c = candidate.to_ascii_lowercase();
        if !c.contains("neutral") || !c.contains(&family) {
            continue;
        }
        if variant_key(&c, &family) != key {
            continue;
        }
        // the shortest name of the family is the plain one: @idle_neutral over @idle_neutral_taunt
        let score = c.len();
        if best.as_ref().is_none_or(|(b, _)| score < *b) {
            best = Some((score, candidate.clone()));
        }
    }
    best.map(|(_, n)| n)
}

/// What is left of a sequence's name once the family and the aim words are taken out: `idle_v02_multi_aim`
/// and `@idle_neutral_v02` both come down to `v02`, `idle_alt_01_multi_aim` and `@idle_neutral_alt01`
/// both to `alt01`. The empty string is the plain pose of the family.
fn variant_key(name: &str, family: &str) -> String {
    let mut rest = name.replace(family, "");
    for word in ["multi", "aim", "neutral"] {
        rest = rest.replace(word, "");
    }
    rest.chars().filter(|c| c.is_ascii_alphanumeric()).collect()
}

pub fn add_plain_activities(root: &mut Node) -> bool {
    let descs = match root.get("m_localS1SeqDescArray") {
        Some(n) => n.items().to_vec(),
        None => return false,
    };
    let names: Vec<String> = root
        .get("m_localSequenceNameArray")
        .map(|n| n.items().iter().map(|i| text(Some(i))).collect())
        .unwrap_or_default();
    let mut twins: Vec<Node> = Vec::new();
    for d in &descs {
        let Some(acts) = d.get("m_activityArray").filter(|a| matches!(a.value, Value::Array { .. })) else {
            continue;
        };
        let Some(first) = acts.items().iter().find(|a| plain_act(&text(a.get("m_name"))).is_some()) else {
            continue;
        };
        if !matches!(first.value, Value::Object(_)) {
            continue;
        }
        let wanted = plain_act(&text(first.get("m_name"))).unwrap_or("");
        // the pose the tower shows when nothing aims it, not the blend across the aim directions
        let own = text(d.get("m_sName"));
        let source = neutral_sibling(&own, &names)
            .and_then(|n| descs.iter().find(|c| text(c.get("m_sName")) == n))
            .unwrap_or(d);
        let mut twin = source.clone();
        let Value::Object(map) = &mut twin.value else { continue };
        map.insert("m_sName".into(), Node::new(Value::Str(format!("{}_{}", own, wanted.to_lowercase()))));
        let mut act = first.clone();
        if let Value::Object(m) = &mut act.value {
            m.insert("m_name".into(), Node::new(Value::Str(wanted.into())));
        }
        if let Some(Node { value: Value::Array { items, .. }, .. }) = map.get_mut("m_activityArray") {
            *items = vec![act];
        }
        twins.push(twin);
    }
    if twins.is_empty() {
        return false;
    }
    if let Some(Node { value: Value::Array { items, .. }, .. }) = root.get_mut("m_localS1SeqDescArray") {
        items.extend(twins.iter().cloned());
    } else {
        return false;
    }
    if let Some(Node { value: Value::Array { items: names, .. }, .. }) = root.get_mut("m_localSequenceNameArray") {
        for t in &twins {
            names.push(Node::new(Value::Str(text(t.get("m_sName")))));
        }
    }
    true
}

#[cfg(test)]
mod twin_tests {
    use super::*;

    fn bool_node(v: bool) -> Node {
        Node::new(Value::Other { ty: if v { 13 } else { 12 }, value: Some(if v { 1 } else { 0 }) })
    }

    fn desc(name: &str, act: &str, looping: bool) -> Node {
        let mut flags: indexmap::IndexMap<String, Node> = indexmap::IndexMap::new();
        flags.insert("m_bLooping".into(), bool_node(looping));
        flags.insert("m_bHidden".into(), bool_node(false));
        let mut a: indexmap::IndexMap<String, Node> = indexmap::IndexMap::new();
        a.insert("m_name".into(), Node::new(Value::Str(act.into())));
        let mut fetch: indexmap::IndexMap<String, Node> = indexmap::IndexMap::new();
        fetch.insert("m_localReferenceArray".into(), Node::new(Value::Array {
            items: vec![made_int(1).unwrap()],
            element: None,
            ty: 0,
        }));
        let mut d: indexmap::IndexMap<String, Node> = indexmap::IndexMap::new();
        d.insert("m_sName".into(), Node::new(Value::Str(name.into())));
        d.insert("m_flags".into(), Node::new(Value::Object(flags)));
        d.insert("m_fetch".into(), Node::new(Value::Object(fetch)));
        d.insert(
            "m_activityArray".into(),
            Node::new(Value::Array {
                items: if act.is_empty() { vec![] } else { vec![Node::new(Value::Object(a))] },
                element: None,
                ty: 0,
            }),
        );
        Node::new(Value::Object(d))
    }

    fn block(descs: Vec<Node>) -> Node {
        let names: Vec<Node> = descs.iter().map(|d| Node::new(Value::Str(text(d.get("m_sName"))))).collect();
        let mut root: indexmap::IndexMap<String, Node> = indexmap::IndexMap::new();
        root.insert("m_localS1SeqDescArray".into(), Node::new(Value::Array { items: descs, element: None, ty: 0 }));
        root.insert("m_localSequenceNameArray".into(), Node::new(Value::Array { items: names, element: None, ty: 0 }));
        Node::new(Value::Object(root))
    }

    fn names(root: &Node) -> Vec<String> {
        root.get("m_localSequenceNameArray").unwrap().items().iter().map(|n| text(Some(n))).collect()
    }

    fn desc_names(root: &Node) -> Vec<String> {
        root.get("m_localS1SeqDescArray").unwrap().items().iter().map(|d| text(d.get("m_sName"))).collect()
    }

    fn acts_of(root: &Node, i: usize) -> Vec<String> {
        root.get("m_localS1SeqDescArray").unwrap().items()[i]
            .get("m_activityArray")
            .map(|a| a.items().iter().map(|x| text(x.get("m_name"))).collect())
            .unwrap_or_default()
    }

    /// A tower's idle is a blend across the aim directions; the twin takes the neutral pose of the
    /// same family instead, so a tower nothing aims stands facing the lane and not at a blend default.
    #[test]
    fn a_twin_of_a_multi_aim_pose_takes_the_neutral_one() {
        let mut root = block(vec![
            desc("idle_multi_aim", "ACT_DOTA_CUSTOM_TOWER_IDLE", true),
            desc("@idle_minus_179", "ACT_DOTA_CUSTOM_TOWER_IDLE", true),
            desc("@idle_neutral", "ACT_DOTA_CUSTOM_TOWER_IDLE", true),
            desc("@idle_180", "ACT_DOTA_CUSTOM_TOWER_IDLE", true),
            desc("idle_v02_multi_aim", "ACT_DOTA_CUSTOM_TOWER_IDLE", true),
            desc("@idle_neutral_v02", "ACT_DOTA_CUSTOM_TOWER_IDLE", true),
        ]);
        assert_eq!(neutral_sibling("idle_multi_aim", &names(&root)).as_deref(), Some("@idle_neutral"));
        assert_eq!(neutral_sibling("idle_v02_multi_aim", &names(&root)).as_deref(), Some("@idle_neutral_v02"));
        assert_eq!(neutral_sibling("attack_multi_aim", &names(&root)), None);

        assert!(add_plain_activities(&mut root));
        let now = desc_names(&root);
        let twin = now.iter().position(|n| n == "idle_multi_aim_act_dota_idle").expect("twin");
        let neutral = now.iter().position(|n| n == "@idle_neutral").expect("neutral");
        let fetch = |i: usize| -> Vec<String> {
            root.get("m_localS1SeqDescArray").unwrap().items()[i]
                .get("m_fetch")
                .and_then(|f| f.get("m_localReferenceArray"))
                .map(|r| r.items().iter().map(|n| format!("{:?}", n.value)).collect())
                .unwrap_or_default()
        };
        assert_eq!(fetch(twin), fetch(neutral), "twin plays the neutral pose, not the blend");
    }

    /// The shape of the packs that move: each custom tower activity also answers under its plain
    /// name, in a sequence of its own beside the custom one, with the look's own order untouched.
    #[test]
    fn every_custom_tower_activity_gets_its_plain_twin_and_nothing_moves() {
        let mut root = block(vec![
            desc("idle_default", "ACT_DOTA_CUSTOM_TOWER_IDLE", true),
            desc("attack_1", "ACT_DOTA_CUSTOM_TOWER_ATTACK", false),
            desc("death", "ACT_DOTA_CUSTOM_TOWER_DIE", false),
            desc("capture", "ACT_DOTA_CAPTURE", true),
        ]);
        let original = desc_names(&root);
        let original_names = names(&root);
        assert!(add_plain_activities(&mut root));

        // the look's own four stay exactly where they were, with their own activities
        assert_eq!(&desc_names(&root)[..4], &original[..]);
        assert_eq!(&names(&root)[..4], &original_names[..]);
        assert_eq!(acts_of(&root, 0), vec!["ACT_DOTA_CUSTOM_TOWER_IDLE"]);

        // three plain twins went in at the end, one per custom activity, and the two lists agree
        let now = desc_names(&root);
        assert_eq!(now.len(), 7);
        assert_eq!(names(&root), now, "имя и описание идут один к одному");
        assert_eq!(now[4], "idle_default_act_dota_idle");
        assert_eq!(now[5], "attack_1_act_dota_attack");
        assert_eq!(now[6], "death_act_dota_die");
        assert_eq!(acts_of(&root, 4), vec!["ACT_DOTA_IDLE"]);
        assert_eq!(acts_of(&root, 5), vec!["ACT_DOTA_ATTACK"]);
        assert_eq!(acts_of(&root, 6), vec!["ACT_DOTA_DIE"]);

        // a twin keeps the animation of its custom sequence: same reference, nothing renumbered
        let refs = |i: usize| -> String {
            root.get("m_localS1SeqDescArray").unwrap().items()[i]
                .get("m_fetch")
                .and_then(|f| f.get("m_localReferenceArray"))
                .map(|r| r.items().len().to_string())
                .unwrap_or_default()
        };
        assert_eq!(refs(0), refs(4));
        // the looping flag travels with the twin, so the idle still loops
        assert_eq!(
            root.get("m_localS1SeqDescArray").unwrap().items()[4]
                .get("m_flags").and_then(|f| f.get("m_bLooping")).map(|n| format!("{:?}", n.value)),
            Some(format!("{:?}", bool_node(true).value)),
        );
    }

    #[test]
    fn a_plain_tower_gets_no_twins() {
        let mut root = block(vec![
            desc("idle", "ACT_DOTA_IDLE", true),
            desc("attack", "ACT_DOTA_ATTACK", false),
        ]);
        assert!(!add_plain_activities(&mut root));
        assert_eq!(desc_names(&root).len(), 2);
    }
}
