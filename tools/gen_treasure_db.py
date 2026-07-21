"""Bake data/treasure_addrs.json + data/map_trap_formation.json + data/anchor_sigs.json
-> FFTTreasureMaster/treasure.json.

There are two hard gates: the cross-language self-test (g) and anchor-region assignment
(j). Every OTHER per-tile / per-address problem WARNS and drops just that tile or address
-- one messy capture must never block deploying every other map (the campaign accretes
captures over weeks; a single dirty tile bricking the whole bake would stall it). The
quality bars are: verified status, the in-session eyeball hold-test, and the
>= MIN_ADDRS_TO_SHIP clean-copy floor.

Bake rules:
  (a) Ship addresses for verified tiles that satisfy ONE of two modes:
        Fingerprinted mode: non-null fpHash AND capturedBuild present.
        Map-id-only mode:   fpVer == 0 AND fpHash is null/absent -- for water/lava maps
          whose terrain grid animates on every re-entry (no stable fingerprint possible).
          capturedBuild must still be present and current (L0 PE key check is NOT bypassed).
      Tiles that match neither mode are skipped.
  (b) Every shipped (x,y) must be a treasure tile (is_treasure) in the snapshot; a tile
      whose coord is absent from the snapshot is warned and skipped.
  (c) Per-address drops (warn + drop the single pair, keep the tile): off byte not 0x00/0x01
      (a lockstep coincidence, not a flag byte); the volatile 0x142e detail-store family
      (>= 0x142000000, relocates between runs); an address outside the module span
      0x140000000..0x143000000 or inside the UI render arena 0x140C63000..0x140CC5000;
      a duplicate of an address already claimed in this map; an unparseable pair. A tile
      ships only if >= MIN_ADDRS_TO_SHIP clean addresses remain after drops.
  (e) Build-key policy: dataset key = newest capturedBuild timeDateStamp. Maps captured under an
      older key are warned and dropped (never hard-fail -- would brick deploys post-patch).
      Map-id-only maps are NOT exempt from this: capturedBuild must be present and current.
  (f) Emit stub entries {mapId, name, tileCount, tiles:[]} for every populated treasure map with
      no shippable tiles (runtime nag needs names/counts).
  (g) Self-test on every invocation: 3 pinned FNV-1a64 vectors + join fixture + masked-hash
      pinned vector.  Exit 1 on fail.
  (h) Print coverage summary: shippable maps / stub maps / dropped.
  (i) Fingerprint version policy: maps with fpVer 2 or 3 are shipped as fingerprinted maps;
      maps with fpVer 0 are shipped as map-id-only (water/lava, no terrain gate); anything
      else is WARNED and demoted to stub (re-fingerprint needed via 'refp' or set map-id-only
      via 'nofp' in treasure_flags.py).
      fpVer=2: dry-land maps (field-0 hash, immune to field-1/6 churn).
      fpVer=3: water/lava maps that DO have a stable v3 hash (fields {2,3,4,5}).
      fpVer=0: map-id-only mode (no fingerprint at all -- see rule (a) above).
      Both fpVer=2 and fpVer=3 are shipped with fpLen == TERRAIN_RAW_LEN (1456); the runtime
      dispatches on fpVer.  Map-id-only maps are emitted with fpVer=0, fpHash=null, fpLen=null.
  (j) Schema v2: data/anchor_sigs.json (the 7 tile-region bases R0..R6 + 10 singleton sig
      anchors) is validated structurally (self-test AND at bake time) and baked verbatim
      (minus _meta) into treasure.json's "anchors" block. Every shipped tile addr is
      tagged with its region id by INCLUSIVE span membership. An addr matching NO region
      span or MORE THAN ONE is a HARD BAKE FAILURE (exit 1) -- unlike per-capture dirt,
      that means the anchor table itself is broken and must be fixed before shipping
      anything, so it is never silently warned-and-dropped like rule (c).

Populated = at least one is_treasure tile in map_trap_formation.json (mapIds 1-127).
"""
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.paths import ROOT
from lib.treasure import is_treasure

# ── constants ─────────────────────────────────────────────────────────────────

MODULE_BASE = 0x140000000
MODULE_END  = 0x143000000   # exclusive
UI_ARENA_LO = 0x140C63000
UI_ARENA_HI = 0x140CC5000   # exclusive

# The 0x142e detail-store family relocates between runs (prototype doc: observed at
# 0x142eca../0x142ec4../0x142ebed.. on successive captures). Anything at or above this
# cutoff is a volatile lockstep coincidence, never a stable flag byte.
VOLATILE_BASE = 0x142000000

# A capture normally yields ~6 copies (3 buffer families x a 2-byte pair), but the pair
# byte does not always toggle: Mandalia Plain captured 3 tiles at exactly 3 clean singles
# (one per family) and the in-session hold-test PAINTED on all of them (2026-06-11 live).
# The eyeball trust gate is the real quality bar; this floor only rejects captures too
# thin to have covered the families at all.
MIN_ADDRS_TO_SHIP = 3

# Terrain grid: 208 records x 7 bytes each.  The v2 fingerprint reads the full window
# (1456 raw bytes) but hashes only field-0 (offset i*7 for record i).
TERRAIN_RECORD_LEN = 7
TERRAIN_NUM_RECORDS = 208
TERRAIN_RAW_LEN = TERRAIN_RECORD_LEN * TERRAIN_NUM_RECORDS  # 1456

ADDRS_JSON       = ROOT / "data" / "treasure_addrs.json"
TRAP_JSON        = ROOT / "data" / "map_trap_formation.json"
ANCHOR_SIGS_JSON = ROOT / "data" / "anchor_sigs.json"
OUT_JSON         = ROOT / "FFTTreasureMaster" / "treasure.json"

SCHEMA_VERSION = 2

# ── FNV-1a 64-bit ─────────────────────────────────────────────────────────────

_FNV_OFFSET = 0xcbf29ce484222325
_FNV_PRIME  = 0x100000001b3

def fnv1a64(data: bytes) -> int:
    h = _FNV_OFFSET
    for b in data:
        h ^= b
        h = (h * _FNV_PRIME) & 0xFFFFFFFFFFFFFFFF
    return h


def masked_terrain_hash(raw: bytes) -> int:
    """Fingerprint v2: FNV-1a64 over field-0 bytes only (byte at i*TERRAIN_RECORD_LEN).
    Immune to changes in fields 1-6 which are live state (change during play).

    Pinned test vector (shared with C# MaskedTerrainHash -- never let them drift):
      raw = 01 02 03 04 05 06 07  11 12 13 14 15 16 17  (2 records)
      field-0 bytes = [0x01, 0x11]
      result = fnv1a64([0x01, 0x11]) = 0x082f3307b4e8a9a7

    Kept for dual-version support: dry-land maps with fpVer=2 continue to use this.
    """
    num_records = len(raw) // TERRAIN_RECORD_LEN
    field0 = bytes(raw[i * TERRAIN_RECORD_LEN] for i in range(num_records))
    return fnv1a64(field0)


def masked_terrain_hash_v3(raw: bytes) -> int:
    """Fingerprint v3: FNV-1a64 over bytes at positions where (i % 7) in {2,3,4,5}.

    Fields {2,3,4,5} are static geometry; fields {0,1,6} (height, slope, flow) animate
    on water/lava maps and are excluded.

    Pinned test vector (shared with C# MaskedTerrainHashV3 and treasure_flags.py):
      raw = 01 02 03 04 05 06 07  11 12 13 14 15 16 17  (2 records)
      fields {2,3,4,5} bytes = [03 04 05 06 13 14 15 16]
        (indices 2,3,4,5 from record 0; indices 9,10,11,12 from record 1)
      result = fnv1a64([03 04 05 06 13 14 15 16]) = 0x05708f90b5f5fac5
    """
    static_bytes = bytes(raw[i] for i in range(len(raw)) if (i % TERRAIN_RECORD_LEN) in (2, 3, 4, 5))
    return fnv1a64(static_bytes)

# ── self-test ─────────────────────────────────────────────────────────────────

def _self_test() -> bool:
    """Pinned FNV-1a64 vectors shared verbatim with TreasureMaster.Policy.cs."""
    vectors = [
        (b"",       0xcbf29ce484222325),
        (b"a",      0xaf63dc4c8601ec8c),
        (b"foobar", 0x85944171f73967e8),
    ]
    ok = True
    for data, expected in vectors:
        got = fnv1a64(data)
        if got != expected:
            print(f"SELF-TEST FAIL: fnv1a64({data!r}) = 0x{got:x}, expected 0x{expected:x}")
            ok = False
        else:
            print(f"  self-test: fnv1a64({data!r}) = 0x{got:016x}  OK")

    # Pinned masked-hash v2 vector (shared with C# MaskedTerrainHash and treasure_flags.py):
    # 14-byte buf = records [01 02 03 04 05 06 07] + [11 12 13 14 15 16 17]
    # field-0 bytes = [0x01, 0x11] => 0x082f3307b4e8a9a7
    _MASKED_HASH_VECTOR = 0x082f3307b4e8a9a7
    _mhv_raw = bytes([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
                      0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17])
    got_mhv = masked_terrain_hash(_mhv_raw)
    if got_mhv != _MASKED_HASH_VECTOR:
        print(f"SELF-TEST FAIL: masked_terrain_hash(vector) = 0x{got_mhv:016x}, "
              f"expected 0x{_MASKED_HASH_VECTOR:016x}")
        ok = False
    else:
        print(f"  self-test: masked_terrain_hash(vector) = 0x{got_mhv:016x}  OK")

    # Pinned masked-hash v3 vector (shared with C# MaskedTerrainHashV3 and treasure_flags.py):
    # 14-byte buf = records [01 02 03 04 05 06 07] + [11 12 13 14 15 16 17]
    # fields {2,3,4,5} bytes (i%7 in {2,3,4,5}):
    #   indices 2,3,4,5 -> 0x03,0x04,0x05,0x06; indices 9,10,11,12 -> 0x13,0x14,0x15,0x16
    # result = fnv1a64([03 04 05 06 13 14 15 16]) = 0x05708f90b5f5fac5
    _MASKED_HASH_V3_VECTOR = 0x05708f90b5f5fac5
    _mhv3_raw = bytes([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
                       0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17])
    got_mhv3 = masked_terrain_hash_v3(_mhv3_raw)
    if got_mhv3 != _MASKED_HASH_V3_VECTOR:
        print(f"SELF-TEST FAIL: masked_terrain_hash_v3(vector) = 0x{got_mhv3:016x}, "
              f"expected 0x{_MASKED_HASH_V3_VECTOR:016x}")
        ok = False
    else:
        print(f"  self-test: masked_terrain_hash_v3(vector) = 0x{got_mhv3:016x}  OK")

    # Join fixture: map 74 (The Siedge Weald). All four of its tiles carry a rare item, so
    # all four are treasure -- (6,6) is TRAPPED treasure (None flag), not a non-treasure.
    trap_data = json.loads(TRAP_JSON.read_text(encoding="utf-8"))
    m74 = trap_data.get("74", {})
    treasure_tiles = [(t["x"], t["y"]) for t in m74.get("tiles", []) if is_treasure(t)]
    expected_treasures = [(0, 1), (1, 9), (5, 11), (6, 6)]  # (6,6) = trapped treasure, still treasure
    for xy in expected_treasures:
        if xy not in treasure_tiles:
            print(f"SELF-TEST FAIL: map 74 tile {xy} not found in is_treasure set {treasure_tiles}")
            ok = False
    if ok:
        print(f"  self-test: map74 join fixture OK (treasure tiles: {treasure_tiles})")

    # Drop-rule fixture: clean pairs ship, lockstep coincidences are dropped.
    drop_cases = [
        (0x140de1ea7, 0x01, False),  # genuine flag byte
        (0x140de1ea7, 0x00, False),  # genuine flag byte, 0x00 rest
        (0x142eb9ba8, 0x40, True),   # the live 0x142e stray from the first Sledge session
        (0x140de1ea7, 0x40, True),   # bad off even at a stable address
        (0x142eb9ba8, 0x01, True),   # volatile family even with a clean off
    ]
    for addr, off, want_drop in drop_cases:
        dropped = pair_drop_reason(addr, off) is not None
        if dropped != want_drop:
            print(f"SELF-TEST FAIL: pair_drop_reason(0x{addr:x}, 0x{off:02x}) "
                  f"dropped={dropped}, expected {want_drop}")
            ok = False
    if ok:
        print("  self-test: pair drop rules OK")

    # Anchor table (schema v2): structural validation of the real data/anchor_sigs.json,
    # plus the pinned cross-language region-assignment vector shared with the C# tests.
    anchor_data = json.loads(ANCHOR_SIGS_JSON.read_text(encoding="utf-8"))
    anchor_errs = validate_anchor_table(anchor_data)
    if anchor_errs:
        print("SELF-TEST FAIL: data/anchor_sigs.json is structurally invalid:")
        for e in anchor_errs:
            print(f"  {e}")
        ok = False
    else:
        print(f"  self-test: anchor table ({len(anchor_data['regions'])} regions, "
              f"{len(anchor_data['singletons'])} singletons) structurally valid")

    region_spans = parse_region_spans(anchor_data["regions"])
    got_region = assign_region(0x140de8077, region_spans)
    if got_region != "R1":
        print(f"SELF-TEST FAIL: assign_region(0x140de8077) = {got_region!r}, expected 'R1'")
        ok = False
    else:
        print("  self-test: assign_region(0x140de8077) = R1  OK")

    return ok

# ── parsing helpers ───────────────────────────────────────────────────────────

def parse_hex(s: str) -> int:
    return int(s, 16)

def addr_valid(addr: int) -> bool:
    if addr < MODULE_BASE or addr >= MODULE_END:
        return False
    if UI_ARENA_LO <= addr < UI_ARENA_HI:
        return False
    return True

def pair_drop_reason(addr: int, off: int) -> str | None:
    """Non-fatal drop reasons for a captured (addr, off) pair: lockstep coincidences
    that a legitimate capture CAN produce but that must never ship. None = keep."""
    if off not in (0x00, 0x01):
        return f"off 0x{off:02x} is not a flag resting value"
    if addr >= VOLATILE_BASE:
        return f"volatile family >= 0x{VOLATILE_BASE:x} (relocates between runs)"
    return None

# ── anchor table (schema v2: region bases + singleton sig anchors) ────────────

def _anchor_parse_pattern(text: str) -> list:
    """'89 05 ?? ?? ?? ?? CC' -> [0x89, 0x05, None, None, None, None, 0xCC].
    Raises ValueError on a bad token -- broken anchor data must fail the gate, never
    ship a corrupt sig silently."""
    out = [None if tok == "??" else int(tok, 16) for tok in text.split()]
    if not out:
        raise ValueError("empty pattern")
    return out


def _check_anchor_sig(owner: str, sig: dict, errs: list[str]) -> None:
    """Structural checks shared by every sig (region or singleton). Appends to errs;
    never raises (a hand-edited data/anchor_sigs.json must always fail loud, not crash
    the gate with a traceback)."""
    label = f"{owner}/{sig.get('name', '?')}"
    pattern, disp_off, end_adjust, target_str = (
        sig.get("pattern"), sig.get("dispOff"), sig.get("endAdjust"), sig.get("target"))
    if pattern is None or disp_off is None or end_adjust is None or target_str is None:
        errs.append(f"{label}: missing required sig field (pattern/dispOff/endAdjust/target)")
        return
    try:
        pat = _anchor_parse_pattern(pattern)
    except ValueError as e:
        errs.append(f"{label}: bad pattern ({e})")
        return
    if disp_off + 4 + end_adjust > len(pat):
        errs.append(f"{label}: dispOff + 4 + endAdjust exceeds pattern length")
    try:
        target = parse_hex(target_str)
    except ValueError:
        errs.append(f"{label}: unparseable target {target_str!r}")
        return
    if not (MODULE_BASE <= target < MODULE_END):
        errs.append(f"{label}: target 0x{target:x} outside module span")
    pin_const_str = sig.get("pinConst")
    if pin_const_str is not None:
        try:
            parse_hex(pin_const_str)
        except ValueError:
            errs.append(f"{label}: unparseable pinConst {pin_const_str!r}")


def validate_anchor_table(data: dict) -> list[str]:
    """Hard-gate structural validation for the anchors block (data/anchor_sigs.json,
    re-checked here both at self-test time and at bake time). ANY problem here is a
    bake failure -- unlike per-tile capture dirt (pair_drop_reason), broken anchor
    data must block the deploy, never warn-and-drop."""
    errs: list[str] = []
    regions     = data.get("regions", [])
    singletons  = data.get("singletons", [])

    if len(regions) != 7:
        errs.append(f"expected 7 regions, got {len(regions)}")
    if len(singletons) != 10:
        errs.append(f"expected 10 singletons, got {len(singletons)}")

    total_sigs = 0
    spans: list[tuple[str, int, int]] = []

    for region in regions:
        rid = region.get("id", "?")
        base_str, span = region.get("base"), region.get("span")
        if base_str is None:
            errs.append(f"region {rid}: missing base")
        else:
            try:
                base = parse_hex(base_str)
                if not (MODULE_BASE <= base < MODULE_END):
                    errs.append(f"region {rid}: base 0x{base:x} outside module span")
            except ValueError:
                errs.append(f"region {rid}: unparseable base {base_str!r}")
        if not span or len(span) != 2:
            errs.append(f"region {rid}: missing/malformed span")
        else:
            try:
                lo, hi = parse_hex(span[0]), parse_hex(span[1])
            except ValueError:
                errs.append(f"region {rid}: unparseable span {span!r}")
            else:
                if lo > hi:
                    errs.append(f"region {rid}: span lo > hi")
                if not (MODULE_BASE <= lo < MODULE_END) or not (MODULE_BASE <= hi < MODULE_END):
                    errs.append(f"region {rid}: span outside module bounds")
                spans.append((rid, lo, hi))
        for sig in region.get("sigs", []):
            total_sigs += 1
            _check_anchor_sig(rid, sig, errs)

    for singleton in singletons:
        sname = singleton.get("name", "?")
        addr_str = singleton.get("addr")
        addr = None
        if addr_str is None:
            errs.append(f"singleton {sname}: missing addr")
        else:
            try:
                addr = parse_hex(addr_str)
                if not (MODULE_BASE <= addr < MODULE_END):
                    errs.append(f"singleton {sname}: addr 0x{addr:x} outside module span")
            except ValueError:
                errs.append(f"singleton {sname}: unparseable addr {addr_str!r}")
        for sig in singleton.get("sigs", []):
            total_sigs += 1
            _check_anchor_sig(sname, sig, errs)
            pin_const_str = sig.get("pinConst")
            if pin_const_str is not None and addr is not None:
                try:
                    target = parse_hex(sig["target"])
                    pin_const = parse_hex(pin_const_str)
                except (KeyError, ValueError):
                    continue  # already reported by _check_anchor_sig
                if target + pin_const != addr:
                    errs.append(f"singleton {sname}: addr 0x{addr:x} != "
                                f"target(0x{target:x}) + pinConst(0x{pin_const:x})")

    if total_sigs != 22:
        errs.append(f"expected 22 sigs total, got {total_sigs}")

    spans.sort(key=lambda t: t[1])
    for i in range(len(spans) - 1):
        _, _, hi = spans[i]
        _, lo2, _ = spans[i + 1]
        if hi >= lo2:
            errs.append(f"spans overlap: {spans[i][0]} and {spans[i + 1][0]}")

    return errs


def parse_region_spans(regions: list) -> list[tuple[str, int, int]]:
    """[{"id": "R1", "span": ["0x..", "0x.."]}, ...] -> [("R1", lo, hi), ...]."""
    return [(r["id"], parse_hex(r["span"][0]), parse_hex(r["span"][1])) for r in regions]


def assign_region(addr: int, spans: list[tuple[str, int, int]]) -> str | None:
    """Inclusive-span region-id lookup for a tile address. None = no region matched.
    Raises ValueError if MORE than one span matches -- broken anchor data (overlapping
    spans), a hard bake failure, never a silent pick-one."""
    hits = [rid for rid, lo, hi in spans if lo <= addr <= hi]
    if len(hits) > 1:
        raise ValueError(f"matches multiple regions: {hits}")
    return hits[0] if hits else None

# ── main bake ─────────────────────────────────────────────────────────────────

def main() -> int:
    print("gen_treasure_db.py — self-test first")
    if not _self_test():
        print("GATE FAIL: self-test failed.")
        return 1
    print()

    trap_data   = json.loads(TRAP_JSON.read_text(encoding="utf-8"))
    addrs_data  = json.loads(ADDRS_JSON.read_text(encoding="utf-8"))
    capture_maps: dict = addrs_data.get("maps", {})

    # Anchor table (schema v2): re-validated here (not just at self-test time) since it
    # gates the real bake -- a broken data/anchor_sigs.json must block deploying anything.
    anchor_data = json.loads(ANCHOR_SIGS_JSON.read_text(encoding="utf-8"))
    anchor_errs = validate_anchor_table(anchor_data)
    if anchor_errs:
        print("GATE FAIL: data/anchor_sigs.json is structurally invalid:")
        for e in anchor_errs:
            print(f"  {e}")
        return 1
    region_spans = parse_region_spans(anchor_data["regions"])

    # Build the authoritative set of populated treasure maps (mapIds 1-127 with
    # at least one is_treasure tile in the snapshot).
    populated: dict[int, dict] = {}
    for mid_str, mdata in trap_data.items():
        mid = int(mid_str)
        if mid < 1 or mid > 127:
            continue
        treasure_tiles = [t for t in mdata.get("tiles", []) if is_treasure(t)]
        if not treasure_tiles:
            continue
        populated[mid] = {
            "name":  mdata.get("name") or f"Map {mid}",
            "tiles": treasure_tiles,
        }

    # ── build-key policy ──────────────────────────────────────────────────────
    # Collect all distinct capturedBuild keys from capture data.
    all_builds: list[dict] = []
    seen_ts: set[int] = set()
    for mid_str, cmap in capture_maps.items():
        cb = cmap.get("capturedBuild")
        if cb and isinstance(cb, dict):
            ts = cb.get("timeDateStamp")
            if ts is not None and ts not in seen_ts:
                all_builds.append(cb)
                seen_ts.add(ts)

    newest_build: dict | None = None
    if all_builds:
        newest_build = max(all_builds, key=lambda b: b.get("timeDateStamp", 0))

    dataset_build_key = newest_build  # may be None if nothing captured yet

    # ── gate + bake ───────────────────────────────────────────────────────────
    gate_failures: list[str] = []
    warnings: list[str] = []
    shippable_count = 0
    stub_count      = 0
    dropped_count   = 0

    out_maps = []
    for mid in sorted(populated.keys()):
        pop = populated[mid]
        name        = pop["name"]
        pop_tiles   = pop["tiles"]
        tile_count  = len(pop_tiles)
        treasure_xy = {(t["x"], t["y"]) for t in pop_tiles}
        # (x,y) -> (rareItemId, commonItemId) for claim detection (the runtime watches these
        # inventory counts; a tile is claimed when its item count rises while a unit stands on it).
        item_by_xy  = {(t["x"], t["y"]): (t.get("rareItemId", 0), t.get("commonItemId", 0))
                       for t in pop_tiles}
        # (x,y) -> slot index (0-based, native X1..X4 file order) for collect detection.
        slot_by_xy  = {(t["x"], t["y"]): i for i, t in enumerate(pop_tiles)}

        cmap = capture_maps.get(str(mid))

        # Check if this map has any capturable data.
        if cmap is None:
            # No capture data at all — emit stub.
            stub_count += 1
            out_maps.append({
                "mapId":     mid,
                "name":      name,
                "tileCount": tile_count,
                "fpVer":     None,
                "fpLen":     None,
                "fpHash":    None,
                "tiles":     [],
            })
            continue

        fp_hash    = cmap.get("fpHash")
        fp_len     = cmap.get("fpLen")
        fp_ver     = cmap.get("fpVer")
        cap_build  = cmap.get("capturedBuild")

        # Determine arm mode for this map.
        # map-id-only: fpVer == 0 and fpHash absent/null -- water/lava maps.
        # fingerprinted: fpVer 2 or 3 with a non-null fpHash.
        # Anything else: demote to stub (needs 'refp' or 'nofp').
        is_map_id_only = (fp_ver == 0 and not fp_hash)

        if not is_map_id_only and fp_ver not in (2, 3):
            print(f"  WARN: map {mid} ({name}) fpVer={fp_ver!r} (not 0, 2, or 3) -- "
                  f"needs 'refp' re-fingerprint or 'nofp' for water/lava; emitting stub")
            stub_count += 1
            out_maps.append({
                "mapId":     mid,
                "name":      name,
                "tileCount": tile_count,
                "fpVer":     None,
                "fpLen":     None,
                "fpHash":    None,
                "tiles":     [],
            })
            continue

        # Build-key staleness check.  Map-id-only maps are NOT exempt (L0 still guards).
        if newest_build and cap_build and isinstance(cap_build, dict):
            cap_ts = cap_build.get("timeDateStamp", 0)
            new_ts = newest_build.get("timeDateStamp", 0)
            if cap_ts < new_ts:
                print(f"  WARN: map {mid} ({name}) captured under older build "
                      f"(ts={cap_ts:#010x}) vs current (ts={new_ts:#010x}) — DROPPED")
                dropped_count += 1
                # Still emit a stub for the runtime nag.
                out_maps.append({
                    "mapId":     mid,
                    "name":      name,
                    "tileCount": tile_count,
                    "fpVer":     None,
                    "fpLen":     None,
                    "fpHash":    None,
                    "tiles":     [],
                })
                continue

        shippable_tiles = []
        seen_addrs: set[int] = set()

        for tile in cmap.get("tiles", []):
            status = tile.get("status", "")
            tx, ty = tile.get("x"), tile.get("y")

            # (a) Only ship verified tiles.
            # Fingerprinted mode: requires non-null fpHash + capturedBuild.
            # Map-id-only mode:   requires capturedBuild (fpHash is intentionally null).
            if status != "verified":
                continue
            if not cap_build:
                continue
            if not is_map_id_only and not fp_hash:
                continue

            # (b) Coord must be a treasure tile in the snapshot. Warn + skip the tile
            # (a capture that does not match the table should not ship, but must not
            # brick the whole deploy).
            if (tx, ty) not in treasure_xy:
                warnings.append(
                    f"map {mid} tile ({tx},{ty}): not a treasure tile in snapshot -- tile skipped"
                )
                continue

            # Every per-address problem below WARNS and DROPS the single pair, never a
            # hard failure: capture artifacts (lockstep strays, the volatile 0x142e family,
            # duplicate copies, the rare out-of-span hit) are expected on real captures and
            # must not block deploying every other map. The >= MIN_ADDRS_TO_SHIP floor plus
            # the in-session eyeball hold-test are the quality bars. The ONE exception is
            # anchor-region assignment just below: an addr matching no region (or more than
            # one) means the anchor table itself is broken, so it goes to gate_failures
            # (hard exit-1 bake failure), never a warn+drop.
            valid_addrs: list[list[str]] = []
            for pair in tile.get("addrs", []):
                addr_str, off_str = pair[0], pair[1]
                try:
                    addr = parse_hex(addr_str)
                    off  = parse_hex(off_str)
                except (ValueError, IndexError, TypeError):
                    warnings.append(
                        f"map {mid} tile ({tx},{ty}): dropped unparseable addr/off {pair}"
                    )
                    continue

                if (reason := pair_drop_reason(addr, off)) is not None:
                    warnings.append(
                        f"map {mid} tile ({tx},{ty}): dropped addr {addr_str} ({reason})"
                    )
                    continue

                if not addr_valid(addr):
                    warnings.append(
                        f"map {mid} tile ({tx},{ty}): dropped addr {addr_str} "
                        f"(outside module span / inside UI arena)"
                    )
                    continue

                if addr in seen_addrs:
                    warnings.append(
                        f"map {mid} tile ({tx},{ty}): dropped duplicate addr {addr_str} "
                        f"(already claimed in this map)"
                    )
                    continue

                try:
                    region_id = assign_region(addr, region_spans)
                except ValueError as e:
                    gate_failures.append(
                        f"map {mid} tile ({tx},{ty}) addr {addr_str}: {e} "
                        f"(broken anchor data -- overlapping region spans)"
                    )
                    continue
                if region_id is None:
                    gate_failures.append(
                        f"map {mid} tile ({tx},{ty}) addr {addr_str}: not covered by "
                        f"any anchor region span (broken anchor data)"
                    )
                    continue

                seen_addrs.add(addr)
                valid_addrs.append([addr_str, off_str, region_id])

            if valid_addrs and len(valid_addrs) < MIN_ADDRS_TO_SHIP:
                warnings.append(
                    f"map {mid} tile ({tx},{ty}): only {len(valid_addrs)} clean addr(s) "
                    f"after drops -- not shipping this tile, re-capture it"
                )
                valid_addrs = []

            if valid_addrs:
                rare_id, common_id = item_by_xy.get((tx, ty), (0, 0))
                shippable_tiles.append({
                    "x": tx, "y": ty, "addrs": valid_addrs,
                    "rareItemId": rare_id, "commonItemId": common_id,
                    "slot": slot_by_xy.get((tx, ty), -1),
                })

        if shippable_tiles:
            shippable_count += 1
            if is_map_id_only:
                # Map-id-only: no fingerprint fields.
                out_maps.append({
                    "mapId":     mid,
                    "name":      name,
                    "tileCount": tile_count,
                    "fpVer":     0,
                    "fpLen":     None,
                    "fpHash":    None,
                    "tiles":     shippable_tiles,
                })
            else:
                fp_hash_hex = f"0x{int(fp_hash, 16):016x}" if fp_hash else None
                out_maps.append({
                    "mapId":     mid,
                    "name":      name,
                    "tileCount": tile_count,
                    "fpVer":     fp_ver,         # preserve the actual version (2 or 3)
                    "fpLen":     TERRAIN_RAW_LEN,
                    "fpHash":    fp_hash_hex,
                    "tiles":     shippable_tiles,
                })
        else:
            stub_count += 1
            out_maps.append({
                "mapId":     mid,
                "name":      name,
                "tileCount": tile_count,
                "fpVer":     None,
                "fpLen":     None,
                "fpHash":    None,
                "tiles":     [],
            })

    if warnings:
        print("\nWARNINGS (addresses/tiles dropped, non-fatal):")
        for w in warnings:
            print(f"  {w}")

    if gate_failures:
        print("\nGATE FAILURES:")
        for f in gate_failures:
            print(f"  {f}")
        print(f"\nGATE FAIL: {len(gate_failures)} violation(s). "
              f"Fix the capture data or data/anchor_sigs.json and re-run.")
        return 1

    # ── build key output (None when nothing captured yet) ────────────────────
    if dataset_build_key:
        out_key = {
            "timeDateStamp": dataset_build_key["timeDateStamp"],
            "sizeOfImage":   dataset_build_key["sizeOfImage"],
        }
    else:
        out_key = None

    out = {
        "schema":   SCHEMA_VERSION,
        "buildKey": out_key,
        "anchors": {
            "regions":    anchor_data["regions"],
            "singletons": anchor_data["singletons"],
        },
        "maps": out_maps,
    }
    OUT_JSON.write_text(json.dumps(out, indent=2, ensure_ascii=False), encoding="utf-8")

    total_populated = len(populated)
    print(f"\nwrote {OUT_JSON}")
    print(f"coverage: {shippable_count} shippable / {stub_count} stub / "
          f"{dropped_count} dropped (stale key) / {total_populated} total populated maps")
    return 0


if __name__ == "__main__":
    sys.exit(main())
