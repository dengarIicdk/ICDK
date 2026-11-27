//! Weapon-swap arcanas bring their own attack clips. The builder must ship the fixed
//! `voidstorm_asylum` base, not the raw item model: the base is the one that still carries the
//! plain `ACT_DOTA_ATTACK` clips the game asks for when its item table is untouched. This pass
//! reads the finished Razor body and says what it found; it never rewrites the model.

use serde_json::Value;

use crate::vpk::Entry;

fn get<'a>(o: &'a Value, key: &str) -> Option<&'a Value> {
    o.get("members")?.as_array()?.iter().find(|m| m[0] == key).map(|m| &m[1])
}

fn items(o: &Value) -> &[Value] {
    o.get("items").and_then(Value::as_array).map(Vec::as_slice).unwrap_or(&[])
}

fn text(o: Option<&Value>) -> &str {
    o.and_then(|v| v.get("value")).and_then(Value::as_str).unwrap_or_default()
}

/// Counts the attack clips in a compiled hero body: `(all, with the plain activity, with an event)`.
fn attacks(model: &[u8]) -> Option<(usize, usize)> {
    if !crate::resource::blocks(model).ok()?.iter().any(|b| b.name == "ANIM") {
        return None;
    }
    let kv = crate::kv3::read(crate::resource::data(model, "ANIM", 0).ok()?).ok()?;
    let tree = kv.tree();
    let anims = get(&tree, "m_animArray").map(items).unwrap_or(&[]);
    let (mut plain, mut events) = (0usize, 0usize);
    for anim in anims {
        let acts = get(anim, "m_activityArray").map(items).unwrap_or(&[]);
        let names: Vec<&str> = acts.iter().map(|a| text(get(a, "m_name"))).collect();
        if !names.iter().any(|n| n.eq_ignore_ascii_case("ACT_DOTA_ATTACK")) {
            continue;
        }
        plain += 1;
        if !get(anim, "m_eventArray").map(items).unwrap_or(&[]).is_empty() {
            events += 1;
        }
    }
    Some((plain, events))
}

/// Razor's Voidstorm Asylum is the arcana the player reported as attacking without a swing. The
/// body has to be the base repository's model, and this line proves it still has its attacks.
pub fn audit_razor(entries: &[Entry], log: &mut dyn FnMut(String)) {
    const BODY: &str = "models/heroes/razor/razor.vmdl_c";
    let Some(body) = entries.iter().rev().find(|e| e.path().eq_ignore_ascii_case(BODY)) else {
        return;
    };
    match attacks(&body.data) {
        Some((0, _)) => log("razor attack: THE BODY HAS NO ACT_DOTA_ATTACK CLIP".to_string()),
        Some((plain, events)) => {
            log(format!("razor attack: {plain} attack clips, {events} with their own effect ({} bytes)", body.data.len()))
        }
        None => log("razor attack: body has no readable ANIM block".to_string()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_the_tree_the_way_a_compiled_model_writes_it() {
        let tree = serde_json::json!({
            "kind": "object",
            "members": [["m_animArray", { "kind": "array", "items": [{
                "kind": "object",
                "members": [
                    ["m_activityArray", { "kind": "array", "items": [
                        { "kind": "object", "members": [["m_name", { "kind": "string", "value": "ACT_DOTA_ATTACK" }]] }
                    ] }],
                    ["m_eventArray", { "kind": "array", "items": [1] }]
                ]
            }] }]]
        });
        let anims = get(&tree, "m_animArray").map(items).unwrap();
        let names: Vec<&str> = items(get(&anims[0], "m_activityArray").unwrap())
            .iter()
            .map(|a| text(get(a, "m_name")))
            .collect();
        assert_eq!(names, vec!["ACT_DOTA_ATTACK"]);
    }
}
