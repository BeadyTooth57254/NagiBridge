"""Scan every SMAPI mod's config files and build a mod -> feature -> keybind map.

The result is written to scripts/mods_keybinds.json as a JSON array of entries:
    { "mod": <mod name>, "modId": <uniqueID>, "file": <relative config path>,
      "feature": <human label>, "path": <json field path>, "keys": ["LeftShift", "A"] }

Heuristics (SMAPI configs vary a lot):
  * A "Controls"/"Keybinds"/"Keys"/"Hotkeys"/"Inputs" object: each child field is a
    feature, its string value is the key (or key list / compound like "LeftControl + LeftShift").
  * A field whose name contains key/bind/hotkey/shortcut/button/control whose value is a key.
  * Keys are strings matching SMAPI SButton names, single letters A-Z, digits, or compound
    chains joined with "+" and optional spaces. "None" / "" means "no key" -> skipped.

Usage:
    python extract_keybinds.py [MODS_ROOT]
"""
import json
import os
import re
import sys

MODS_ROOT_DEFAULT = r"D:\SteamLibrary\steamapps\common\Stardew Valley\Mods"
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
OUT_PATH = os.path.join(SCRIPT_DIR, "mods_keybinds.json")


def find_mods_root():
    """Determine the mods directory: NAGI_MODS_PATH env, else the standard game folder."""
    return os.environ.get("NAGI_MODS_PATH") or MODS_ROOT_DEFAULT

# SMAPI SButton key names that we recognise as keys (single letters/digits handled separately).
KEY_NAMES = {
    # named keyboard keys
    "enter", "return", "escape", "esc", "space", "back", "backspace", "tab", "up", "down",
    "left", "right", "home", "end", "pageup", "pageup", "pagedown", "insert", "delete",
    "ins", "del", "capslock", "scrolllock", "pause", "printscreen", "grave", "tilde",
    # modifiers
    "leftshift", "rightshift", "leftcontrol", "rightcontrol", "leftalt", "rightalt",
    "leftctrl", "rightctrl", "shift", "control", "ctrl", "alt", "win", "windows",
    # oem / punctuation (SMAPI names)
    "oemcomma", "oemperiod", "oemquestion", "oemsemicolon", "oemquotes", "oemopenbrackets",
    "oemclosebrackets", "oempipe", "oemminus", "oemplus", "oemtilde", "oembackslash", "oem8",
    "comma", "period", "slash", "backslash", "semicolon", "equals", "minus", "apostrophe",
    # function keys / numpad
    *[f"f{i}" for i in range(1, 25)],
    *[f"d{i}" for i in range(10)],
    *[f"numpad{i}" for i in range(10)],
    "numpadplus", "numpadminus", "numpadmultiply", "numpaddivide", "numpaddecimal",
    # mouse
    "mouseleft", "mouseright", "mousemiddle", "mouse4", "mouse5", "mouse6", "mouse7",
    "mouse8", "mouse9", "mouse10", "mouse11", "mouse12",
    # gamepad (rarely used via keyboard injection, but resolve to names)
    "leftshoulder", "rightshoulder", "lefttrigger", "righttrigger", "leftstick", "rightstick",
}

# Object keys whose children are treated as feature->keybind pairs.
CONTROLS_CONTAINERS = {"controls", "keybinds", "keys", "hotkeys", "inputs", "keybindings"}

# Field-name keyword that marks a value as (likely) a keybind.
KEY_FIELD_RE = re.compile(r"(key|bind|hotkey|shortcut|button|control)", re.I)

# Containers we skip entirely (pure colour/geometry/UI options that sometimes carry "Key"-like names).
SKIP_FIELD_RE = re.compile(r"(color|colour|position|offset|opacity|alpha|size|width|height|scale)", re.I)


def norm(s):
    return re.sub(r"\s+", "", s).lower()


def split_compound(value):
    """Return list of key tokens from a SMAPI key string or compound chain."""
    s = value.strip()
    if not s or norm(s) in ("none", "null", ""):
        return []
    # SMAPI KeybindList can be like "LeftControl + LeftShift" or just "Q"
    parts = [p.strip() for p in s.split("+") if p.strip()]
    if not parts:
        return []
    # Every part must look like a recognised key (single letter/digit/name).
    ok = []
    for p in parts:
        lp = norm(p)
        if re.fullmatch(r"[a-z]", lp):          # single letter
            ok.append(p)
        elif re.fullmatch(r"\d", lp):            # single digit
            ok.append(p)
        elif lp in KEY_NAMES:
            ok.append(p)
        else:
            return []                           # not a keybind at all
    return ok


def walk(node, path, parent_key_lower, out, feature_hint=None, in_controls=False):
    """Recursively walk a parsed JSON config, emitting keybind entries to `out`."""
    if isinstance(node, dict):
        for k, v in node.items():
            kl = k.lower()
            full = f"{path}.{k}" if path else k
            # A container object of controls -> children are feature->key
            if isinstance(v, dict) and kl in CONTROLS_CONTAINERS:
                for ck, cv in v.items():
                    keys = split_string_or_list(cv)
                    if keys:
                        out.append({
                            "feature": ck,
                            "path": f"{full}.{ck}",
                            "keys": keys,
                        })
                continue
            if isinstance(v, (dict, list)):
                walk(v, full, kl, out, feature_hint=feature_hint, in_controls=in_controls)
            else:
                # scalar leaf: record if it looks like a keybind
                if isinstance(v, str) and KEY_FIELD_RE.search(kl) and not SKIP_FIELD_RE.search(kl):
                    keys = split_compound(v)
                    if keys:
                        out.append({"feature": k, "path": full, "keys": keys})
    elif isinstance(node, list):
        # arrays are usually lists of keys for a keybind field, handled by caller
        pass


def split_string_or_list(value):
    if isinstance(value, str):
        return split_compound(value)
    if isinstance(value, list):
        keys = []
        for item in value:
            if isinstance(item, str):
                keys.extend(split_compound(item))
        return keys
    return []


def load_json(path):
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except Exception:
        return None


def find_manifests(root):
    for dirpath, dirnames, filenames in os.walk(root):
        if "manifest.json" in filenames:
            yield dirpath


def build_keybind_map(mods_root):
    """Scan all mods under mods_root and return (results, mod_dirs)."""
    results = []
    mod_dirs = sorted(find_manifests(mods_root))
    for mod_dir in mod_dirs:
        name = os.path.basename(mod_dir)
        mod_id = None
        man = load_json(os.path.join(mod_dir, "manifest.json"))
        if man:
            name = man.get("Name") or name
            mod_id = man.get("UniqueID")

        # Gather config candidate files: config.json in mod root and any *.json under config/
        cfg_files = []
        cfg_root = os.path.join(mod_dir, "config.json")
        if os.path.isfile(cfg_root):
            cfg_files.append(("config.json", cfg_root))
        cfg_sub = os.path.join(mod_dir, "config")
        if os.path.isdir(cfg_sub):
            for dp, _, files in os.walk(cfg_sub):
                for fn in files:
                    if fn.endswith(".json"):
                        cfg_files.append((os.path.relpath(os.path.join(dp, fn), mod_dir), os.path.join(dp, fn)))

        for rel_cfg, cfg_path in cfg_files:
            data = load_json(cfg_path)
            if data is None or not isinstance(data, dict):
                continue
            entries = []
            walk(data, "", "", entries)
            for e in entries:
                results.append({
                    "mod": name,
                    "modId": mod_id,
                    "file": rel_cfg,
                    "feature": e["feature"],
                    "path": e["path"],
                    "keys": e["keys"],
                })
    return results, mod_dirs


def latest_config_mtime(mods_root):
    """Newest mtime across all config.json under mods_root (for freshness checks)."""
    latest = 0.0
    for dp, _, files in os.walk(mods_root):
        for fn in files:
            if fn == "config.json":
                try:
                    latest = max(latest, os.path.getmtime(os.path.join(dp, fn)))
                except Exception:
                    pass
    return latest


def main():
    mods_root = sys.argv[1] if len(sys.argv) > 1 else find_mods_root()
    results, mod_dirs = build_keybind_map(mods_root)
    os.makedirs(os.path.dirname(OUT_PATH), exist_ok=True)
    with open(OUT_PATH, "w", encoding="utf-8") as fh:
        json.dump(results, fh, ensure_ascii=False, indent=2)

    # Summary
    mods_with = {}
    for r in results:
        mods_with.setdefault(r["mod"], []).append(r)
    print(f"Scanned mods root: {mods_root}")
    print(f"Mods scanned (with manifest): {len(mod_dirs)}")
    print(f"Keybind entries found: {len(results)}")
    print(f"Mods with keybinds: {len(mods_with)}")
    print(f"Saved -> {OUT_PATH}")
    print("\n--- mods with most keybinds ---")
    for mod, entries in sorted(mods_with.items(), key=lambda kv: -len(kv[1]))[:12]:
        print(f"  {mod}: {len(entries)} bindings")
    print(f"\n--- sample entries ---")
    for r in results[:15]:
        print(f"  {r['mod']} | {r['feature']} | {r['keys']}")


if __name__ == "__main__":
    main()
