//! `wibe-rust-builder <manifest.json>`: builds the language-folder pak from the manifest and
//! writes one JSON line per log/progress event, then a final `{"ok":...}` line.

use std::io::Write;

fn emit(v: serde_json::Value) {
    println!("{v}");
    let _ = std::io::stdout().flush();
}

fn main() {
    let Some(path) = std::env::args().nth(1) else {
        eprintln!("usage: wibe-rust-builder <manifest.json>");
        std::process::exit(2);
    };
    let manifest = match std::fs::read_to_string(&path) {
        Ok(s) => s,
        Err(e) => {
            emit(serde_json::json!({ "ok": false, "error": format!("manifest {path}: {e}") }));
            std::process::exit(1);
        }
    };
    let mut log = |m: String| emit(serde_json::json!({ "log": m }));
    let mut progress = |done: usize, total: usize| {
        emit(serde_json::json!({ "stage": "build", "done": done, "total": total }))
    };
    match mm_core::wibe_api::run(&manifest, &mut progress, &mut log) {
        Ok(r) => emit(serde_json::to_value(r).unwrap_or_else(|_| serde_json::json!({ "ok": true }))),
        Err(e) => {
            emit(serde_json::json!({ "ok": false, "error": e }));
            std::process::exit(1);
        }
    }
}
