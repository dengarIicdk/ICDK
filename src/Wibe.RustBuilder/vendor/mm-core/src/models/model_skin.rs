//! A model with one of its material sets made the one it wears by default. An item can wear its model in another material set ("skin" in its
//! visuals), and a model put under a plain unit's path is worn in set 0 online. So the meshes draw
//! with the set's materials, matched to set 0's by place, and set 0 takes the set's list.

use indexmap::IndexMap;

use crate::kv3::{self, Node, Value};
use crate::resource;

fn strings(n: Option<&Node>) -> Vec<String> {
    n.map(|n| n.items().iter().map(|x| x.as_str().unwrap_or("").to_string()).collect()).unwrap_or_default()
}

fn material_swaps(groups: &[Node], skin: usize) -> IndexMap<String, String> {
    let plain = strings(groups.first().and_then(|g| g.get("m_materials")));
    let chosen = strings(groups.get(skin).and_then(|g| g.get("m_materials")));
    let mut out = IndexMap::new();
    for (i, material) in plain.iter().enumerate() {
        if let Some(c) = chosen.get(i).filter(|c| !c.is_empty() && c.to_lowercase() != material.to_lowercase()) {
            out.insert(material.to_lowercase(), c.clone());
        }
    }
    out
}

/// The material substitutions of a preview skin: set 0's material (lower case) to the set's.
pub fn model_skin_materials(model: Option<&[u8]>, skin: usize) -> IndexMap<String, String> {
    let (Some(model), true) = (model, skin != 0) else { return IndexMap::new() };
    let read = || -> Option<IndexMap<String, String>> {
        let kv = kv3::read(resource::data_block(model).ok()?).ok()?;
        Some(material_swaps(kv.root.get("m_materialGroups").map(Node::items).unwrap_or(&[]), skin))
    };
    read().unwrap_or_default()
}

/// Every m_material string under a node swapped by `swap`; whether any was.
fn swap_materials(n: &mut Node, swap: &IndexMap<String, String>) -> bool {
    let mut hit = false;
    match &mut n.value {
        Value::Object(m) => {
            for (k, v) in m.iter_mut() {
                if k == "m_material" {
                    if let Value::Str(s) = &mut v.value {
                        if let Some(to) = swap.get(&s.to_lowercase()) {
                            *s = to.clone();
                            hit = true;
                        }
                        continue;
                    }
                }
                hit |= swap_materials(v, swap);
            }
        }
        Value::Array { items, .. } => {
            for v in items {
                hit |= swap_materials(v, swap);
            }
        }
        _ => {}
    }
    hit
}

/// The style number a mesh group names: "arcana_@2_#&armor_style2" is 2, its copy too.
fn style_number(name: &str) -> Option<u64> {
    let at = name.rfind("style")?;
    let digits: String = name[at + 5..].chars().take_while(char::is_ascii_digit).collect();
    digits.parse().ok()
}

/// The mask drawing the groups of style `skin`: groups naming another style go off, groups naming
/// none belong to every style and stay on. None when no group names a style at all.
fn mesh_group_mask(groups: &[Node], skin: usize) -> Option<u64> {
    let want = skin as u64 + 1;
    let mut mask = 0u64;
    let mut named = false;
    // one group per style, the first that names it: a "style1_copy" beside "style1" is the same
    // look drawn twice, and the model itself draws the first one only
    let mut drawn = false;
    for (i, group) in groups.iter().enumerate() {
        if i >= 63 {
            break;
        }
        match style_number(&group.as_str().unwrap_or("").to_lowercase()) {
            Some(n) => {
                named = true;
                if n == want && !drawn {
                    mask |= 1u64 << i;
                    drawn = true;
                }
            }
            None => mask |= 1u64 << i,
        }
    }
    (named && mask != 0).then_some(mask)
}

/// A model that keeps its styles as meshes of one model draws the set `skin` asks for: Wraith
/// King's arcana armour holds style1 and style2 side by side and never swaps a material, so the
/// only lever is which mesh group the model shows by default. None: no style groups to choose.
fn style_groups(model: &[u8], skin: usize) -> Option<Vec<u8>> {
    let mut kv = kv3::read(resource::data_block(model).ok()?).ok()?;
    let Value::Array { items, .. } = &kv.root.get("m_meshGroups")?.value else { return None };
    let mask = mesh_group_mask(items, skin)?;
    let mut root = std::mem::replace(&mut kv.root, Node::new(Value::Other { ty: 1, value: None }));
    let held = root.get_mut("m_nDefaultMeshGroupMask")?;
    // a mask the model keeps as a number with bytes is set where it is; the 0-or-1 kind it is
    // often stored as (a type alone, no bytes) has no room for a second bit, so it is written anew
    let set = kv.set_number(held, mask as f64).unwrap_or(false);
    if !set {
        *held = Node::new(Value::Number {
            ty: 3,
            cell: None,
            type_at: None,
            raw: Some(mask.to_le_bytes().to_vec()),
        });
    }
    kv.root = root;
    resource::replace(model, "DATA", 0, &kv3::write(&kv).ok()?).ok()
}

/// `model` wearing material set `skin` by default; None where it has no such set or the model
/// cannot be read. Set 0 gives the model back as it is. A model that carries its styles as mesh
/// groups instead of material sets wears the wanted one too (see style_groups).
pub fn with_skin(model: &[u8], skin: usize) -> Option<Vec<u8>> {
    if skin == 0 {
        return Some(model.to_vec());
    }
    let set = material_set(model, skin);
    match style_groups(set.as_deref().unwrap_or(model), skin) {
        Some(styled) => Some(styled),
        None => set,
    }
}

/// `model` wearing material set `skin`; None where it has no such set or cannot be read.
fn material_set(model: &[u8], skin: usize) -> Option<Vec<u8>> {
    let mut data = kv3::read(resource::data_block(model).ok()?).ok()?;
    let groups = match data.root.get("m_materialGroups") {
        Some(Node { value: Value::Array { items, .. }, .. }) => items.clone(),
        _ => return None,
    };
    if skin >= groups.len() {
        return None;
    }
    let swap = material_swaps(&groups, skin);
    if swap.is_empty() {
        return Some(model.to_vec());
    }
    let mut out = model.to_vec();
    let meshes = resource::blocks(model).ok()?.iter().filter(|b| b.name == "MDAT").count();
    for k in 0..meshes {
        let Ok(block) = resource::data(&out, "MDAT", k) else { return None };
        // a mesh of the older binary kind is left as it is
        let Ok(mut kv) = kv3::read(block) else { continue };
        let mut root = std::mem::replace(&mut kv.root, Node::new(Value::Other { ty: 1, value: None }));
        let hit = swap_materials(&mut root, &swap);
        kv.root = root;
        if hit {
            out = resource::replace(&out, "MDAT", k, &kv3::write(&kv).ok()?).ok()?;
        }
    }
    // even with external meshes, the material groups in DATA decide the look
    let set = groups[skin].get("m_materials")?.clone();
    let Some(Node { value: Value::Array { items, .. }, .. }) = data.root.get_mut("m_materialGroups") else {
        return None;
    };
    let Value::Object(first) = &mut items.first_mut()?.value else { return None };
    first.insert("m_materials".into(), set);
    resource::replace(&out, "DATA", 0, &kv3::write(&data).ok()?).ok()
}

#[cfg(test)]
mod tests {
    use super::{mesh_group_mask, style_number};
    use crate::kv3::{Node, Value};

    fn groups(names: &[&str]) -> Vec<Node> {
        names.iter().map(|n| Node::new(Value::Str((*n).to_string()))).collect()
    }

    #[test]
    fn a_style_number_is_read_off_the_mesh_group_name() {
        assert_eq!(style_number("arcana_@0_#&armor_style1"), Some(1));
        assert_eq!(style_number("arcana_@1_#&armor_style1_copy"), Some(1));
        assert_eq!(style_number("arcana_@2_#&armor_style2"), Some(2));
        assert_eq!(style_number("plain_mesh"), None);
    }

    #[test]
    fn the_second_style_draws_the_groups_that_name_it() {
        let names = ["arcana_@0_#&cape_style1", "arcana_@1_#&cape_style1_copy", "arcana_@2_#&cape_style2"];
        // style 1 is the model's own default (bit 0), the second style is bit 2
        assert_eq!(mesh_group_mask(&groups(&names), 0), Some(1));
        assert_eq!(mesh_group_mask(&groups(&names), 1), Some(4));
        // a copied group beside the first one is the same look, drawn once
        assert_eq!(mesh_group_mask(&groups(&["a_style1", "a_style1_copy"]), 0), Some(1));
    }

    #[test]
    fn a_group_that_names_no_style_is_drawn_in_every_style() {
        let names = ["shoulder_style1", "keep_me", "shoulder_style2"];
        assert_eq!(mesh_group_mask(&groups(&names), 1), Some(0b110));
    }

    #[test]
    fn a_model_with_no_style_groups_has_no_mask() {
        assert_eq!(mesh_group_mask(&groups(&["head", "body"]), 1), None);
        // the style asked for is not one this model has
        assert_eq!(mesh_group_mask(&groups(&["a_style1", "b_style2"]), 4), None);
    }
}
