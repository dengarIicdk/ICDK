//! A hero's HUD portrait and minimap icon as the item builder's picks make them, written once for
//! the whole build: the portrait settings of an arcana's model written under
//! the plain model's path, and the plain icon's sprite cut where the item's is. Both files are
//! the game's with those entries changed, and only when something changed.

use regex::bytes::{Captures, Regex};

use crate::item_builder_assets::{hero_model_path, normalize_asset_path, PortraitSwap, SpriteSwap};
use crate::vpk::{Entry, Index};

const PORTRAITS: &str = "scripts/npc/portraits.txt";
const SPRITES: &str = "scripts/mod_textures.txt";
/// JavaScript's \s over latin1 text.
const WS: &str = r"[\t\n\x0B\x0C\r \xA0]";

/// Where a model's top-level block starts (at its name), where the name ends, and where the
/// block ends (after its brace).
fn portrait_block(text: &[u8], model: &str) -> Option<(usize, usize, usize)> {
    let re = Regex::new(&format!(r#"(?i-u)\t"{}"[ \t]*"#, regex::escape(model))).ok()?;
    // `^` and `(?=\r?$)` of a JavaScript multiline pattern: at a line's start, before its end
    let name = re.find_iter(text).find(|m| {
        (m.start() == 0 || matches!(text[m.start() - 1], b'\n' | b'\r'))
            && text.get(m.end()).is_none_or(|c| matches!(c, b'\r' | b'\n'))
    })?;
    let (mut depth, mut quoted) = (0i64, false);
    let from = name.start() + text[name.start()..].iter().position(|&c| c == b'{')?;
    for (i, &c) in text.iter().enumerate().skip(from) {
        if c == b'"' {
            quoted = !quoted;
        } else if !quoted && c == b'{' {
            depth += 1;
        } else if !quoted && c == b'}' {
            depth -= 1;
            if depth == 0 {
                return Some((name.start(), name.end(), i + 1));
            }
        }
    }
    None
}

fn scaled(v: &[u8], scale: f64) -> String {
    let n = crate::js::to_number(&serde_json::Value::String(crate::kv::latin1(v)));
    crate::js::number((n * scale * 1e4 + 0.5).floor() / 1e4)
}

/// A portrait block framed for a body `scale` times bigger: the camera and the light stand as
/// much further from the origin, at the same angles and field of view.
pub fn scaled_portrait(block: &[u8], scale: f64) -> Vec<u8> {
    let pos =
        Regex::new(&format!(r#"(?-u)("Portrait(?:Light)?Position"{WS}+")([-0-9.]+) ([-0-9.]+) ([-0-9.]+)""#)).unwrap();
    let dist = Regex::new(&format!(r#"(?-u)("PortraitLightDistance"{WS}+")([-0-9.]+)""#)).unwrap();
    let out = pos.replace_all(block, |c: &Captures| {
        let mut v = c[1].to_vec();
        v.extend_from_slice(
            format!("{} {} {}\"", scaled(&c[2], scale), scaled(&c[3], scale), scaled(&c[4], scale)).as_bytes(),
        );
        v
    });
    dist.replace_all(&out, |c: &Captures| {
        let mut v = c[1].to_vec();
        v.extend_from_slice(format!("{}\"", scaled(&c[2], scale)).as_bytes());
        v
    })
    .into_owned()
}

/// A portrait swap as resolved: from the item's model to the hero's own.
pub struct Swap {
    pub from: String,
    pub to: String,
    pub scale: Option<f64>,
    pub block: Option<String>,
}

/// The portrait camera of a look's tower model written under the plain tower's path.
///
/// The game frames a unit's portrait from `scripts/npc/portraits.txt` keyed by the model it wears.
/// A look's body is bigger than the stock tower, so the stock key's crop shows a corner of it (the
/// blue smudge in the portrait frame). This copies the look's own camera onto the plain key.
pub fn with_tower_portraits(text: &[u8], swaps: &[(String, String)]) -> Vec<u8> {
    let mut out = text.to_vec();
    for (from, to) in swaps {
        let Some((s, e, end)) = portrait_block(&out, to) else { continue };
        let Some((fs, _fe, fend)) = portrait_block(&out, from) else { continue };
        let source = out[fs..fend].to_vec();
        let Some(brace) = source.iter().position(|&c| c == b'{') else { continue };
        let key = out[s..e].to_vec();
        let mut next: Vec<u8> = Vec::with_capacity(out.len() + source.len());
        next.extend_from_slice(&out[..s]);
        next.extend_from_slice(&key);
        next.extend_from_slice(&source[brace..]);
        next.extend_from_slice(&out[end..]);
        out = next;
    }
    out
}

/// The portraits table with the looks' tower cameras in it: the pak's own entry, or the game's.
pub fn patch_tower_portraits(entries: &mut Vec<Entry>, read: &dyn Fn(&str) -> Option<Vec<u8>>, swaps: &[(String, String)]) {
    if swaps.is_empty() {
        return;
    }
    let at = entries.iter().position(|e| e.path().eq_ignore_ascii_case(PORTRAITS));
    let text = match at {
        Some(i) => entries[i].data.to_vec(),
        None => match read(PORTRAITS) {
            Some(t) => t,
            None => return,
        },
    };
    let next = with_tower_portraits(&text, swaps);
    if next == text {
        return;
    }
    match at {
        Some(i) => entries[i] = crate::vpk::entry_at(PORTRAITS, std::sync::Arc::from(next)),
        None => entries.push(crate::vpk::entry_at(PORTRAITS, std::sync::Arc::from(next))),
    }
}

/// The portrait settings with each arcana's block written under the model it stands in for.
pub fn with_portraits(text: &[u8], swaps: &[Swap]) -> Vec<u8> {
    let mut out = text.to_vec();
    // A stock block copied from a model is a fallback: an item's own `game` camera wins even
    // when a later pick of the same hero also copies the stock model's block.
    let ordered: Vec<&Swap> = swaps
        .iter()
        .filter(|s| s.block.is_none())
        .chain(swaps.iter().filter(|s| s.block.is_some()))
        .collect();
    for s in ordered {
        let settings: Vec<u8> = match &s.block {
            Some(item) => item.as_bytes().to_vec(),
            None => {
                // no settings of its own: the plain model's are the best there are
                let Some((_, name_end, end)) = portrait_block(text, &s.from) else { continue };
                let source = &text[name_end..end];
                match s.scale.filter(|&k| k != 0.0 && k != 1.0) {
                    Some(k) => scaled_portrait(source, k),
                    None => source.to_vec(),
                }
            }
        };
        let mut block = format!("\t\"{}\"", s.to).into_bytes();
        if s.block.is_some() {
            block.extend_from_slice(b"\n\t");
        }
        block.extend_from_slice(&settings);
        if let Some((start, _, end)) = portrait_block(&out, &s.to) {
            out.splice(start..end, block);
        } else {
            let close = out.iter().rposition(|&c| c == b'}').unwrap_or(0);
            block.extend_from_slice(b"\r\n");
            out.splice(close..close, block);
        }
    }
    out
}

/// The game's own portrait settings (`portraits.game`) of every item that has them, by item id.
/// An arcana's HUD camera is defined here even when the stock portrait file has no block for the
/// arcana's model at all.
pub fn settings_by_item(text: &[u8]) -> std::collections::HashMap<String, String> {
    let Ok(items) = crate::schema::items_of(text) else { return Default::default() };
    let mut out = std::collections::HashMap::new();
    for item in items.iter() {
        if item.start >= item.end || item.end > text.len() {
            continue;
        }
        let slice = &text[item.start..item.end];
        let lower = String::from_utf8_lossy(slice).to_lowercase();
        // Only real arcanas and persona selectors carry a HUD camera. Ordinary wearables and
        // sets use the armory preview camera, which crops the hero wrongly in the HUD.
        let allowed = lower.contains("\"item_rarity\"") && lower.contains("\"arcana\"")
            || lower.contains("\"item_slot\"") && lower.contains("\"persona_selector\"");
        if !allowed {
            continue;
        }
        if let Some(block) = item_portrait_settings(slice) {
            out.insert(item.id.clone(), block);
        }
    }
    out
}

/// `portraits.game` of one item block, braces included. The `icon` block is the armory preview,
/// usually with the hero hidden, and is deliberately not a HUD portrait.
fn item_portrait_settings(block: &[u8]) -> Option<String> {
    let portraits = child_block(block, b"portraits")?;
    let game = child_block(&portraits, b"game")?;
    Some(String::from_utf8_lossy(&game).into_owned())
}

/// A braced body (`{...}`) of the first `"key"` entry in a KeyValues block slice.
fn child_block(block: &[u8], key: &[u8]) -> Option<Vec<u8>> {
    let mut pattern = Vec::with_capacity(key.len() + 2);
    pattern.push(b'"');
    pattern.extend_from_slice(key);
    pattern.push(b'"');
    let mut from = 0usize;
    while from + pattern.len() <= block.len() {
        let pos = block[from..].windows(pattern.len()).position(|w| w.eq_ignore_ascii_case(&pattern))? + from;
        let mut k = pos + pattern.len();
        while k < block.len() && block[k].is_ascii_whitespace() {
            k += 1;
        }
        if k < block.len() && block[k] == b'{' {
            let open = k;
            let (mut depth, mut quoted) = (0i64, false);
            while k < block.len() {
                match block[k] {
                    b'"' => quoted = !quoted,
                    b'{' if !quoted => depth += 1,
                    b'}' if !quoted => {
                        depth -= 1;
                        if depth == 0 {
                            return Some(block[open..=k].to_vec());
                        }
                    }
                    _ => {}
                }
                k += 1;
            }
            return None;
        }
        from = pos + 1;
    }
    None
}

/// The sprite definitions with each swapped sprite cut out where its replacement is.
pub fn with_sprites(text: &[u8], swaps: &[SpriteSwap]) -> Vec<u8> {
    let find = |s: &[u8], name: &str| -> Option<(usize, usize)> {
        let re = Regex::new(&format!(r#"(?i-u)"{}"{WS}*(\{{[^{{}}]*\}})"#, regex::escape(name))).ok()?;
        re.captures(s).and_then(|c| c.get(1)).map(|m| (m.start(), m.end()))
    };
    let mut out = text.to_vec();
    for sw in swaps {
        let (Some((ss, se)), Some((ts, te))) = (find(text, &sw.as_), find(&out, &sw.sprite)) else { continue };
        let source = text[ss..se].to_vec();
        out.splice(ts..te, source);
    }
    out
}

/// The portrait settings and sprite sheet of every pick, to go into the build ahead of the
/// picks' own files. A hero whose own model the game does not name is left out and logged.
pub fn portrait_entries(
    read: &dyn Fn(&str) -> Option<Vec<u8>>,
    portraits: &[PortraitSwap],
    sprites: &[SpriteSwap],
    log: &mut dyn FnMut(String),
) -> Vec<Entry> {
    if portraits.is_empty() && sprites.is_empty() {
        return Vec::new();
    }
    let mut swaps = Vec::new();
    for p in portraits {
        match hero_model_path(read, &p.hero) {
            Ok(to) => swaps.push(Swap {
                from: normalize_asset_path(&p.model),
                to: normalize_asset_path(&to),
                scale: p.scale.filter(|&s| s != 0.0),
                block: p.block.clone(),
            }),
            Err(e) => log(format!("schema: the portrait of {} left out of the build: {e}", p.hero)),
        }
    }
    let mut out = Vec::new();
    // written only when changed: a game file put back as it was would still cover the game's own
    let mut rewrite = |file: &str, change: &dyn Fn(&[u8]) -> Vec<u8>| {
        if let Some(was) = read(file) {
            let now = change(&was);
            if now != was {
                out.push(crate::vpk::entry_at(file, now.into()));
            }
        }
    };
    if !swaps.is_empty() {
        rewrite(PORTRAITS, &|t| with_portraits(t, &swaps));
    }
    if !sprites.is_empty() {
        rewrite(SPRITES, &|t| with_sprites(t, sprites));
    }
    out
}



/// The portrait model the file already uses for a hero, if any.
fn block_model(text: &[u8], hero: &str) -> Option<String> {
    let text = String::from_utf8_lossy(text);
    let prefix = format!("\"models/heroes/{hero}/");
    for line in text.lines() {
        let line = line.trim();
        if line.starts_with(&prefix) && line.ends_with(".vmdl\"") {
            return Some(line.trim_matches('"').to_string());
        }
    }
    None
}

/// A model line in the portrait file whose path names an arcana of this hero.
fn arcana_model(text: &[u8], hero: &str) -> Option<String> {
    let text = String::from_utf8_lossy(text);
    let needle = format!("/{hero}/");
    for line in text.lines() {
        let line = line.trim();
        if line.starts_with("\"models/items/")
            && line.contains(&needle)
            && line.contains("arcana")
            && line.ends_with(".vmdl\"")
        {
            return Some(line.trim_matches('"').to_string());
        }
    }
    None
}

/// The braced settings of a model's block, for writing under another model's name.
fn model_settings(text: &[u8], model: &str) -> Option<String> {
    let (_, name_end, end) = portrait_block(text, model)?;
    Some(String::from_utf8_lossy(&text[name_end..end]).into_owned())
}

/// Puts the right HUD camera on every picked hero: the persona body's own block first, then an
/// arcana model's block, then an arcana/persona item's `portraits.game` camera. Ordinary set
/// items are never used, because their camera is an armory preview and crops the HUD portrait.
pub fn apply_item_portraits(
    entries: &mut Vec<Entry>,
    index: &Index,
    vanilla: &[u8],
    picks: &[(String, String)],
    log: &mut dyn FnMut(String),
) -> Result<(), String> {
    let Some(pos) = entries.iter().position(|e| e.path() == PORTRAITS) else { return Ok(()) };
    let mut text = entries[pos].data.to_vec();
    let blocks = settings_by_item(vanilla);
    let read = |path: &str| index.read(path).ok().flatten();
    let mut persona_models: std::collections::HashMap<String, String> = Default::default();
    for (hero, item) in picks {
        let hero = hero.trim().trim_start_matches("npc_dota_hero_").to_lowercase();
        if hero.is_empty() || persona_models.contains_key(&hero) {
            continue;
        }
        let Ok(Some(visuals)) = crate::item_builder::item_visuals::item_visuals(vanilla, item) else {
            continue;
        };
        if !visuals.slot.eq_ignore_ascii_case("persona_selector") {
            continue;
        }
        if let Some(model) = visuals
            .modifiers
            .iter()
            .find(|m| m.ty.eq_ignore_ascii_case("entity_model") && !m.modifier.is_empty())
        {
            persona_models.insert(hero, model.modifier.clone());
        }
    }
    let mut done = std::collections::HashSet::new();
    for (hero, item) in picks {
        let hero = hero.trim().trim_start_matches("npc_dota_hero_").to_lowercase();
        if hero.is_empty() || !done.insert(hero.clone()) {
            continue;
        }
        let npc = format!("npc_dota_hero_{hero}");
        let Ok(found) = hero_model_path(&read, &npc) else { continue };
        let to = block_model(&text, &hero).unwrap_or(found);
        let persona = persona_models
            .get(&hero)
            .and_then(|m| model_settings(&text, m))
            .map(|s| (s, "persona".to_string()));
        let chosen = persona
            .or_else(|| {
                let model = arcana_model(&text, &hero)?;
                let settings = model_settings(&text, &model)?;
                Some((settings, model))
            })
            .or_else(|| blocks.get(item).cloned().map(|s| (s, format!("item {item}"))));
        let Some((settings, source)) = chosen else { continue };
        let swap = Swap {
            from: String::new(),
            to: normalize_asset_path(&to),
            scale: None,
            block: Some(settings),
        };
        let next = with_portraits(&text, &[swap]);
        if next != text {
            log(format!("portrait: {hero} from {source}"));
            text = next;
        }
    }

    if let Some(pos) = entries.iter().position(|e| e.path() == PORTRAITS) {
        entries[pos] = crate::vpk::entry_at(PORTRAITS, text.into());
    }
    Ok(())
}
