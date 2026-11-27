//! A taunt writes its perform over every taunt the hero can wear in that slot: the row the game
//! sees is one of the stock (or cheap) taunts as far as the account is concerned, while its
//! activity, prop, particles and sounds are the picked one's.

use std::collections::HashSet;

use crate::item_builder_styles::{with_style};
use crate::kv;
use crate::schema;
use crate::schema_merge::strip_key_blocks;

use super::item_builder_donor::with_modifiers;
use super::item_builder_slots::item_heroes;
use super::{
    field, item_assets, item_fields, item_visuals, normalize_asset_path, set_block_id, set_scalar_field,
    set_visuals_block, Patch,
};

fn title(rec: &str) -> String {
    rec.to_string()
}

/// All stock taunts a hero wears in the taunt slot, the picked one included when it is one.
pub fn taunt_targets(base: &[u8], item_id: &str) -> Result<Vec<String>, String> {
    let items = schema::items_of(base).map_err(|e| e.to_string())?;
    let Some(source) = items.iter().find(|i| i.id == item_id) else {
        return Err(title(&format!("items_game: предмет {item_id} не найден")));
    };
    let heroes: HashSet<String> = item_heroes(base, source.start).map_err(|e| e.to_string())?.into_iter().collect();
    Ok(items
        .iter()
        .filter(|i| i.id != item_id && i.prefab == "taunt" && !i.name.to_lowercase().contains("no taunt"))
        .filter(|i| {
            item_heroes(base, i.start)
                .map(|own| own.is_empty() || heroes.is_empty() || own.iter().any(|h| heroes.contains(h)))
                .unwrap_or(false)
        })
        .map(|i| i.id.clone())
        .collect())
}

pub(crate) fn taunt_activity(visuals: &[u8]) -> Option<(String, String)> {
    let bounds = kv::block_bounds(visuals, 0).ok()?;
    let mut hit = None;
    let _ = kv::each_child(visuals, bounds, |c| {
        if !c.is_block() || !c.key.to_ascii_lowercase().starts_with(b"asset_modifier") {
            return true;
        }
        let mut ty = String::new();
        let mut asset = String::new();
        let mut modifier = String::new();
        let _ = kv::each_child(visuals, c.body.unwrap_or_default(), |f| {
            if f.is_block() {
                return true;
            }
            let key = f.key.to_ascii_lowercase();
            let value = f.value.and_then(|v| std::str::from_utf8(v).ok()).unwrap_or("");
            if key == b"type" {
                ty = value.to_string();
            } else if key == b"asset" {
                asset = value.to_string();
            } else if key == b"modifier" {
                modifier = value.to_string();
            }
            true
        });
        if ty == "activity" && asset.eq_ignore_ascii_case("ACT_DOTA_TAUNT") && !modifier.is_empty() {
            hit = Some((asset, modifier));
            return false;
        }
        true
    });
    hit
}

/// The picked taunt over one target: the target stays the account's item, the picked one's
/// activities and files play. `rare` adds the idle-rare activity beside the taunt one.
pub fn taunt_patch(base: &[u8], item_id: &str, target_id: &str, rare: bool) -> Result<Patch, String> {
    let items = schema::items_of(base).map_err(|e| e.to_string())?;
    let Some(source) = items.iter().find(|i| i.id == item_id).cloned() else {
        return Err(title(&format!("items_game: предмет {item_id} не найден")));
    };
    let Some(target) = items.iter().find(|i| i.id == target_id).cloned() else {
        return Err(title(&format!("items_game: предмет {target_id} не найден")));
    };
    let source_fields = item_fields(base, source.start).map_err(|e| e.to_string())?;
    let target_fields = item_fields(base, target.start).map_err(|e| e.to_string())?;
    let own_visuals = item_visuals(base, source.start)
        .map_err(|e| e.to_string())?
        .unwrap_or_else(|| b"\"visuals\"\r\n{\r\n}".to_vec());
    let (mut visuals, _, applied) = with_style(&own_visuals, 0).map_err(|e| e.to_string())?;
    let heroes = item_heroes(base, source.start).map_err(|e| e.to_string())?;
    let mut assets = item_assets(base, &visuals, &heroes, None).map_err(|e| e.to_string())?;
    if !applied {
        visuals = strip_key_blocks(&visuals, "styles");
    }
    visuals = strip_key_blocks(&visuals, "unlock");
    if rare {
        if let Some((_, modifier)) = taunt_activity(&visuals) {
            let extra = format!(
                "\"asset_modifier99\"\r\n\t{{\r\n\t\t\"type\"\t\t\"activity\"\r\n\t\t\"asset\"\t\t\"ACT_DOTA_IDLE_RARE\"\r\n\t\t\"modifier\"\t\t\"{modifier}\"\r\n\t\t\"gesture\"\t\t\"1\"\r\n\t}}"
            );
            visuals = with_modifiers(&visuals, &[extra.into_bytes()]);
        }
    }
    let mut patched = set_block_id(&base[target.start..target.end], &target.id);
    // the slot still wears the stock/cheap taunt's name, prefab and slot; only what plays changes
    patched = set_visuals_block(&patched, &visuals).map_err(|e| e.to_string())?;
    if let Some(image) = field(&target_fields, "image_inventory").filter(|v| !v.is_empty()) {
        patched = set_scalar_field(&patched, "image_inventory", image).map_err(|e| e.to_string())?;
    }
    // an item's own picture is its square; keep the picked taunt's picture under the target's
    if let Some(from) = field(&source_fields, "image_inventory").filter(|v| !v.is_empty()) {
        let to = field(&target_fields, "image_inventory").unwrap_or("");
        if !to.is_empty() && normalize_asset_path(from) != normalize_asset_path(to) {
            let texture = |image: &str| {
                let clean = normalize_asset_path(image);
                if clean.ends_with(".vtex") {
                    format!("panorama/images/{clean}_c")
                } else {
                    format!("panorama/images/{clean}_png.vtex_c")
                }
            };
            assets.asset_copies.push(super::AssetCopy::Path {
                from: texture(from),
                to: texture(to),
                optional: Some(true),
                skin: None,
                form: None,
            });
        }
    }
    Ok(Patch {
        id: target.id.clone(),
        block: patched,
        own: None,
        added: Vec::new(),
        unplaced: false,
        kinetic: Vec::new(),
        heroes,
        assets,
    })
}
