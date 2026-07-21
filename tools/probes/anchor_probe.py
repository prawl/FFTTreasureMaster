#!/usr/bin/env python
"""Anchor probe: verify AoB code signatures resolve to their pinned addresses (READ-ONLY).

Feasibility gate for the planned sig-anchored-bases update hardening.  Each spec (loaded
from data/anchor_sigs.json) is an AoB code signature (captured by offline analysis of
FFT_enhanced.exe build 1.5.1) whose RIP-relative disp32 must resolve to a pinned target
address:

    target = matchVA + dispOffset + 4 + endAdjust + int32(disp)

where endAdjust counts trailing immediate bytes AFTER the disp32 in the instruction
(e.g. the `83 3D disp32 imm8` cmp form has endAdjust 1).  Parent anchors carry a
pinConstant: pin = target + pinConstant.

Verbs
-----
  python tools\\probes\\anchor_probe.py selftest
      Fully offline gate (no exe, no game): pinned vectors for the masked pattern
      matcher (implemented WITHOUT regex) and the RIP resolve arithmetic, incl. an
      endAdjust!=0 case and a negative-disp32 case, plus embedded-table validation.

  python tools\\probes\\anchor_probe.py filecheck [exePath]
      Against the exe FILE on disk: masked-search the entire file per spec, require
      exactly 1 hit, resolve the disp, compare to the expected target.  PASS/FAIL
      table; exit nonzero on any FAIL.

  python tools\\probes\\anchor_probe.py resolve
      Against the LIVE game: RPM the code section (VA 0x140001000, 0x610000 bytes,
      chunked) and run the same per-spec check on live bytes.  Exit 2 if the game
      process is not running.

  python tools\\probes\\anchor_probe.py flip
      Live only: read the qword current-buffer pointer @0x140DE2BC8 and report
      whether it equals buffer A (0x140DE2BE0) or buffer B (0x140F96348).

  python tools\\probes\\anchor_probe.py all
      resolve + flip.

RPM only -- this probe never writes game memory.  The process handle is opened with
PROCESS_VM_READ | PROCESS_QUERY_INFORMATION only; no write right is ever requested.
"""
import ctypes
import ctypes.wintypes as w
import json
import mmap
import pathlib
import struct
import sys
from typing import NamedTuple

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
_HERE = pathlib.Path(__file__).resolve()
REPO = _HERE.parents[2]
sys.path.insert(0, str(REPO / "tools"))
from lib.paths import ROOT, STEAM_FFT

DEFAULT_EXE = STEAM_FFT / "FFT_enhanced.exe"
ANCHOR_SIGS_JSON = ROOT / "data" / "anchor_sigs.json"

# ---------------------------------------------------------------------------
# PE facts (verified for build 1.5.1; the code section is named '.bss' -- the
# section names in this exe are scrambled, but it is the real primary code).
# ---------------------------------------------------------------------------
IMAGE_BASE = 0x140000000
CODE_VA = 0x140001000          # code section virtual address
CODE_SIZE = 0x610000           # code section size (virtual == raw)
CODE_FILE_OFF = 0x400          # code section raw file offset
EXPECTED_FILE_SIZE = 356024064
EXPECTED_TIMESTAMP = 0x6A3C5497  # PE TimeDateStamp of build 1.5.1

# Current-buffer pointer (semantic find: the flip function at 0x140274D54 swaps it
# between buffer A and buffer B = A + 0x1B3768 on each battle-buffer flip).
CURBUF_PTR = 0x140DE2BC8
BUF_A = 0x140DE2BE0
BUF_B = 0x140F96348

_HIT_CAP = 8  # stop collecting matches past this many (enough to prove non-unique)


# ---------------------------------------------------------------------------
# Anchor table, loaded from data/anchor_sigs.json.  Patterns and referencing-site
# VAs come from the offline 1.5.1 signature analysis (region-base + singleton xref
# scans); every pattern was validated whole-file-unique there and is re-proven by
# `filecheck` here.  data/anchor_sigs.json is the single hand-maintained sig source
# next to treasure_addrs.json; this loader just reshapes it into the flat Spec list
# the rest of this module (and gen_treasure_db.py's bake) already expects.
# ---------------------------------------------------------------------------
class Spec(NamedTuple):
    name: str
    pattern: str       # hex bytes, ?? = wildcard
    disp_off: int      # byte index of the disp32 within the pattern
    end_adjust: int    # trailing immediate bytes AFTER the disp32 in the instruction
    target: int        # expected resolved VA
    pin_const: int | None = None  # pin = target + pin_const (parent anchors only)


def _hex(s: str) -> int:
    return int(s, 16)


def _spec_from_json(entity_name: str, sig: dict) -> Spec:
    pin = sig.get("pinConst")
    return Spec(
        sig["name"],
        sig["pattern"],
        sig["dispOff"],
        sig["endAdjust"],
        _hex(sig["target"]),
        _hex(pin) if pin is not None else None,
    )


def load_anchors(path: pathlib.Path = ANCHOR_SIGS_JSON) -> list:
    """Build the flat Spec list from data/anchor_sigs.json (regions then singletons,
    in file order -- matches the historical embedded-table order)."""
    data = json.loads(path.read_text(encoding="utf-8"))
    specs = []
    for region in data["regions"]:
        for sig in region["sigs"]:
            specs.append(_spec_from_json(region["id"], sig))
    for singleton in data["singletons"]:
        for sig in singleton["sigs"]:
            specs.append(_spec_from_json(singleton["name"], sig))
    return specs


ANCHORS = load_anchors()


# ---------------------------------------------------------------------------
# Masked pattern engine (NO regex: raw byte loops -- pattern bytes like 0x29
# or 0x2A would break a naive bytes-regex approach).
# ---------------------------------------------------------------------------
def parse_pattern(text: str) -> list:
    """'89 05 ?? ?? ?? ?? CC' -> [0x89, 0x05, None, None, None, None, 0xCC]."""
    out = []
    for tok in text.split():
        if tok == "??":
            out.append(None)
        else:
            out.append(int(tok, 16))
    if not out:
        raise ValueError("empty pattern")
    return out


def longest_run(pat: list) -> tuple:
    """(offset, bytes) of the longest contiguous literal run -- the search anchor."""
    best_off, best_len = -1, 0
    i, n = 0, len(pat)
    while i < n:
        if pat[i] is None:
            i += 1
            continue
        j = i
        while j < n and pat[j] is not None:
            j += 1
        if j - i > best_len:
            best_off, best_len = i, j - i
        i = j
    if best_len == 0:
        raise ValueError("pattern is all wildcards")
    return best_off, bytes(pat[best_off:best_off + best_len])


def masked_search(hay, pat: list, limit: int = _HIT_CAP) -> list:
    """All start offsets in hay matching pat (wildcards honored), capped at limit.

    hay may be bytes or an mmap.  Anchors on the longest literal run via .find
    (a C-speed scan), then verifies every literal position of each candidate.
    """
    n = len(pat)
    size = len(hay)
    lits = [(i, b) for i, b in enumerate(pat) if b is not None]
    run_off, needle = longest_run(pat)
    hits = []
    pos = run_off
    while len(hits) < limit:
        j = hay.find(needle, pos)
        if j < 0:
            break
        start = j - run_off
        if start >= 0 and start + n <= size:
            window = hay[start:start + n]
            if all(window[i] == b for i, b in lits):
                hits.append(start)
        pos = j + 1
    return hits


# ---------------------------------------------------------------------------
# RIP resolve + VA/file-offset math
# ---------------------------------------------------------------------------
def rip_resolve(match_va: int, disp_off: int, end_adjust: int, disp: int) -> int:
    """target = matchVA + dispOffset + 4 + endAdjust + int32(disp)."""
    return match_va + disp_off + 4 + end_adjust + disp


def read_disp32(window: bytes, disp_off: int) -> int:
    return struct.unpack_from("<i", window, disp_off)[0]


def file_off_to_va(off: int):
    """File offset -> code VA, or None if outside the code section raw range."""
    if CODE_FILE_OFF <= off < CODE_FILE_OFF + CODE_SIZE:
        return CODE_VA + (off - CODE_FILE_OFF)
    return None


def va_to_file_off(va: int):
    if CODE_VA <= va < CODE_VA + CODE_SIZE:
        return va - CODE_VA + CODE_FILE_OFF
    return None


# ---------------------------------------------------------------------------
# Spec table validation (run before any check pass; also a selftest gate)
# ---------------------------------------------------------------------------
def validate_specs(specs) -> list:
    """Return a list of error strings (empty == table is structurally sound)."""
    errs = []
    seen = set()
    for s in specs:
        if s.name in seen:
            errs.append(f"{s.name}: duplicate spec name")
        seen.add(s.name)
        try:
            pat = parse_pattern(s.pattern)
        except ValueError as e:
            errs.append(f"{s.name}: bad pattern ({e})")
            continue
        if s.disp_off < 0 or s.disp_off + 4 > len(pat):
            errs.append(f"{s.name}: disp32 window not inside the pattern")
            continue
        if any(pat[i] is not None for i in range(s.disp_off, s.disp_off + 4)):
            errs.append(f"{s.name}: disp32 bytes must be wildcarded")
        if sum(1 for b in pat if b is not None) < 4:
            errs.append(f"{s.name}: fewer than 4 literal bytes (uniqueness implausible)")
        if s.end_adjust < 0 or s.end_adjust > 4:
            errs.append(f"{s.name}: endAdjust out of range")
        if s.disp_off + 4 + s.end_adjust > len(pat):
            errs.append(f"{s.name}: dispOff + 4 + endAdjust exceeds pattern length")
        if not (IMAGE_BASE <= s.target < IMAGE_BASE + 0x18800000):
            errs.append(f"{s.name}: target outside the image span")
    return errs


# ---------------------------------------------------------------------------
# Shared per-spec check pass (filecheck and resolve differ only in hay + VA map)
# ---------------------------------------------------------------------------
def _loc(off_to_va, off: int) -> str:
    va = off_to_va(off)
    return f"0x{va:011x}" if va is not None else f"file+0x{off:x}"


def run_checks(hay, off_to_va, specs=None) -> tuple:
    """Run every spec against hay.  Returns (all_ok, lines, n_pass)."""
    specs = ANCHORS if specs is None else specs
    lines, n_pass, all_ok = [], 0, True
    for s in specs:
        pat = parse_pattern(s.pattern)
        hits = masked_search(hay, pat)
        if not hits:
            all_ok = False
            lines.append(f"FAIL  {s.name:<24} hits=0   -- pattern not found")
            continue
        if len(hits) > 1:
            all_ok = False
            capped = "+" if len(hits) >= _HIT_CAP else ""
            where = ", ".join(_loc(off_to_va, o) for o in hits[:4])
            lines.append(f"FAIL  {s.name:<24} hits={len(hits)}{capped}  -- NOT unique: {where}")
            continue
        off = hits[0]
        va = off_to_va(off)
        if va is None:
            all_ok = False
            lines.append(f"FAIL  {s.name:<24} hits=1   -- match outside the code section "
                         f"(file off 0x{off:x})")
            continue
        window = bytes(hay[off:off + len(pat)])
        disp = read_disp32(window, s.disp_off)
        resolved = rip_resolve(va, s.disp_off, s.end_adjust, disp)
        pin_note = ""
        if s.pin_const is not None:
            sign = "+" if s.pin_const >= 0 else "-"
            pin_note = (f"  pin=0x{resolved + s.pin_const:011x}"
                        f" (target{sign}0x{abs(s.pin_const):x})")
        if resolved == s.target:
            n_pass += 1
            lines.append(f"PASS  {s.name:<24} hits=1  @{_loc(off_to_va, off)}"
                         f"  -> 0x{resolved:011x}{pin_note}")
        else:
            all_ok = False
            lines.append(f"FAIL  {s.name:<24} hits=1  @{_loc(off_to_va, off)}"
                         f"  -> 0x{resolved:011x}  expected 0x{s.target:011x}")
    return all_ok, lines, n_pass


# ---------------------------------------------------------------------------
# Verb: selftest (fully offline)
# ---------------------------------------------------------------------------
def _selftest() -> bool:
    ok = True

    def gate(label, cond, detail=""):
        nonlocal ok
        print(f"  {label}: {'OK' if cond else 'FAIL  ' + detail}")
        if not cond:
            ok = False

    # -- pattern parse ------------------------------------------------------
    p = parse_pattern("11 05 ?? ?? ?? ?? 0F")
    gate("parse_pattern", p == [0x11, 0x05, None, None, None, None, 0x0F], f"got {p}")

    # -- longest literal run (anchor selection) -----------------------------
    r1 = next(s for s in ANCHORS if s.name == "R1-primary")
    off, run = longest_run(parse_pattern(r1.pattern))
    gate("longest_run(R1-primary)",
         off == 7 and run == bytes.fromhex("33d2488b89a8001b00e8"),
         f"got off={off} run={run.hex()}")

    # -- masked search: regex-special pattern bytes (0x29 '(' 0x2A '*') -----
    pat = parse_pattern("29 05 ?? 2A")
    hay = b"\x01\x29\x05\xaa\x2a\x00\x29\x05\xbb\x2a\x29\x05\xcc\x2b"
    hits = masked_search(hay, pat)
    gate("search multi-hit + wildcard", hits == [1, 6], f"got {hits}")

    gate("search zero-hit", masked_search(b"\x00" * 32, pat) == [])
    gate("search hit at offset 0", masked_search(b"\x29\x05\x00\x2a", pat) == [0])
    gate("search hit at buffer end",
         masked_search(b"\x00\x00\x29\x05\xff\x2a", pat) == [2])

    # anchor run present but a literal elsewhere mismatched -> rejected
    gate("search literal mismatch rejected",
         masked_search(b"\x29\x05\x00\x99", pat) == [])

    # leading-wildcard pattern: candidate start < 0 must be skipped, not crash
    pat_lead = parse_pattern("?? ?? 29 05")
    gate("search leading-wildcard boundary",
         masked_search(b"\x29\x05\x01\x29\x05", pat_lead) == [1])

    # hit-cap: 10 occurrences, capped at _HIT_CAP
    hits = masked_search(b"\x29\x05\x00\x2a" * 10, pat)
    gate("search hit cap", len(hits) == _HIT_CAP, f"got {len(hits)}")

    # -- RIP resolve arithmetic (pinned vectors) ----------------------------
    # R0-primary real site: 0x1401D52B1 + 2 + 4 + 0xB0656D = 0x140CDB824
    got = rip_resolve(0x1401D52B1, 2, 0, 0x0B0656D)
    gate("rip_resolve endAdjust=0", got == 0x140CDB824, f"got 0x{got:x}")

    # PauseFlag real site (cmp imm8 form): 0x140207889 + 2 + 4 + 1 + 0xA63938 = 0x140C6B1C8
    got = rip_resolve(0x140207889, 2, 1, 0x0A63938)
    gate("rip_resolve endAdjust=1", got == 0x140C6B1C8, f"got 0x{got:x}")

    # negative disp32: 0x140500000 + 3 + 4 - 0x12345 = 0x1404EDCC2
    got = rip_resolve(0x140500000, 3, 0, -0x12345)
    gate("rip_resolve negative disp", got == 0x1404EDCC2, f"got 0x{got:x}")

    # disp extraction from a window (LE bytes 6D 65 B0 00 -> 0x0B0656D)
    disp = read_disp32(bytes.fromhex("89056d65b000"), 2)
    gate("read_disp32", disp == 0x0B0656D, f"got 0x{disp:x}")
    disp = read_disp32(b"\x8b\x05\xbb\xed\xfe\xff", 2)  # LE BB ED FE FF -> -0x11245
    gate("read_disp32 negative", disp == -0x11245, f"got {disp:#x}")

    # -- file-offset <-> VA math --------------------------------------------
    gate("file_off_to_va start", file_off_to_va(CODE_FILE_OFF) == CODE_VA)
    gate("file_off_to_va site", file_off_to_va(0x1D46B1) == 0x1401D52B1)
    gate("file_off_to_va outside", file_off_to_va(CODE_FILE_OFF + CODE_SIZE) is None)
    gate("va_to_file_off", va_to_file_off(0x1401D52B1) == 0x1D46B1)

    # -- end-to-end synthetic pipeline through run_checks -------------------
    synth = Spec("SYNTH", "AA BB ?? ?? ?? ?? CC", 2, 0, 0x140001156)
    buf = bytearray(0x400)
    buf[0x50:0x57] = b"\xaa\xbb" + struct.pack("<i", 0x100) + b"\xcc"
    live_map = lambda o: CODE_VA + o
    e_ok, _, e_pass = run_checks(bytes(buf), live_map, [synth])
    gate("end-to-end unique PASS", e_ok and e_pass == 1)

    # decoy second occurrence -> non-unique FAIL
    buf[0x100:0x107] = b"\xaa\xbb" + struct.pack("<i", 0x100) + b"\xcc"
    e_ok, e_lines, _ = run_checks(bytes(buf), live_map, [synth])
    gate("end-to-end non-unique FAIL", not e_ok and "NOT unique" in e_lines[0])

    # wrong expected target -> resolve-mismatch FAIL
    buf[0x100:0x107] = bytes(7)
    bad = synth._replace(target=0x140001157)
    e_ok, e_lines, _ = run_checks(bytes(buf), live_map, [bad])
    gate("end-to-end mismatch FAIL", not e_ok and "expected" in e_lines[0])

    # pin constant: pin printed as target + const
    pinned = synth._replace(name="SYNTHPIN", pin_const=0x10)
    e_ok, e_lines, _ = run_checks(bytes(buf), live_map, [pinned])
    gate("end-to-end pinConstant", e_ok and "pin=0x00140001166" in e_lines[0],
         f"got {e_lines[0]!r}")

    # -- anchor table validation (loaded from data/anchor_sigs.json) --------
    errs = validate_specs(ANCHORS)
    gate(f"anchor table ({len(ANCHORS)} specs) structurally valid",
         not errs, "; ".join(errs))
    gate("anchor table has 22 specs", len(ANCHORS) == 22, f"got {len(ANCHORS)}")
    kinds = sum(1 for s in ANCHORS if s.pin_const is not None)
    gate("anchor table has 2 parent anchors", kinds == 2, f"got {kinds}")

    return ok


def cmd_selftest() -> None:
    print("Running self-test (no exe, no game required) ...")
    if _selftest():
        print("\nAll self-tests PASSED.")
        sys.exit(0)
    print("\nSelf-test FAILED.")
    sys.exit(1)


# ---------------------------------------------------------------------------
# Verb: filecheck
# ---------------------------------------------------------------------------
def cmd_filecheck(exe_path=None) -> None:
    path = pathlib.Path(exe_path) if exe_path else DEFAULT_EXE
    if not path.is_file():
        print(f"exe not found: {path}")
        sys.exit(2)

    errs = validate_specs(ANCHORS)
    if errs:
        print("embedded spec table invalid:")
        for e in errs:
            print(f"  {e}")
        sys.exit(1)

    print(f"== anchor filecheck: {path} ==")
    size = path.stat().st_size
    tag = "OK" if size == EXPECTED_FILE_SIZE else "WARN: build changed?"
    print(f"file size {size} (expected {EXPECTED_FILE_SIZE}) -- {tag}")

    with open(path, "rb") as f:
        with mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ) as mm:
            e_lfanew = struct.unpack_from("<I", mm, 0x3C)[0]
            ts = struct.unpack_from("<I", mm, e_lfanew + 8)[0]
            tag = "OK" if ts == EXPECTED_TIMESTAMP else "WARN: build changed?"
            print(f"PE TimeDateStamp 0x{ts:08x} (expected 0x{EXPECTED_TIMESTAMP:08x}) -- {tag}")
            print()
            all_ok, lines, n_pass = run_checks(mm, file_off_to_va)

    for ln in lines:
        print(ln)
    print(f"\n{n_pass}/{len(ANCHORS)} PASS")
    if all_ok:
        print("all anchors are whole-file-unique and resolve to their pins on this build")
        sys.exit(0)
    print("FILECHECK FAILED -- at least one anchor did not verify")
    sys.exit(1)


# ---------------------------------------------------------------------------
# Live process access (READ-ONLY: no write right is ever requested)
# ---------------------------------------------------------------------------
PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
_OPEN_RIGHTS = PROCESS_VM_READ | PROCESS_QUERY_INFORMATION

k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi


def _open_game(name: str = "fft_enhanced.exe"):
    arr = (w.DWORD * 4096)()
    needed = w.DWORD()
    psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    for i in range(needed.value // ctypes.sizeof(w.DWORD)):
        h = k32.OpenProcess(_OPEN_RIGHTS, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name:
            return h
        k32.CloseHandle(h)
    return None


def _rpm(h, addr: int, n: int):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    ok = k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    if not ok or got.value != n:
        return None
    return buf.raw


def _read_code_section(h):
    """Chunked RPM of the code section (CODE_VA, CODE_SIZE).  None on any failure."""
    out = bytearray()
    addr, remaining, chunk = CODE_VA, CODE_SIZE, 0x100000
    while remaining:
        n = min(chunk, remaining)
        b = _rpm(h, addr, n)
        if b is None:
            print(f"RPM failed @0x{addr:011x} ({n} bytes)")
            return None
        out += b
        addr += n
        remaining -= n
    return bytes(out)


def _require_game():
    h = _open_game()
    if not h:
        print("fft_enhanced.exe not running")
        sys.exit(2)
    return h


# ---------------------------------------------------------------------------
# Verbs: resolve / flip / all
# ---------------------------------------------------------------------------
def _do_resolve(h) -> bool:
    errs = validate_specs(ANCHORS)
    if errs:
        print("embedded spec table invalid:")
        for e in errs:
            print(f"  {e}")
        return False
    print(f"== anchor resolve: LIVE code section @0x{CODE_VA:011x} ({CODE_SIZE:#x} bytes) ==")
    code = _read_code_section(h)
    if code is None:
        return False
    all_ok, lines, n_pass = run_checks(code, lambda o: CODE_VA + o)
    for ln in lines:
        print(ln)
    print(f"\n{n_pass}/{len(ANCHORS)} PASS")
    if all_ok:
        print("all anchors are unique in the live code section and resolve to their pins")
    else:
        print("RESOLVE FAILED -- at least one anchor did not verify against the live game")
    return all_ok


def _do_flip(h) -> bool:
    print(f"== flip: current-buffer pointer @0x{CURBUF_PTR:011x} ==")
    raw = _rpm(h, CURBUF_PTR, 8)
    if raw is None:
        print("pointer unreadable (RPM failed)")
        return False
    val = struct.unpack("<Q", raw)[0]
    if val == BUF_A:
        print(f"qword = 0x{val:011x}  -> buffer A (0x{BUF_A:011x})")
        return True
    if val == BUF_B:
        print(f"qword = 0x{val:011x}  -> buffer B (0x{BUF_B:011x} = A + 0x1B3768)")
        return True
    print(f"qword = 0x{val:x}  -> NEITHER buffer A (0x{BUF_A:011x}) nor B (0x{BUF_B:011x})")
    return False


def cmd_resolve() -> None:
    h = _require_game()
    try:
        ok = _do_resolve(h)
    finally:
        k32.CloseHandle(h)
    sys.exit(0 if ok else 1)


def cmd_flip() -> None:
    h = _require_game()
    try:
        ok = _do_flip(h)
    finally:
        k32.CloseHandle(h)
    sys.exit(0 if ok else 1)


def cmd_all() -> None:
    h = _require_game()
    try:
        ok = _do_resolve(h)
        print()
        ok = _do_flip(h) and ok
    finally:
        k32.CloseHandle(h)
    sys.exit(0 if ok else 1)


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------
def main() -> None:
    args = sys.argv[1:]
    if not args or args[0] in ("-h", "--help", "help"):
        print(__doc__)
        return
    verb = args[0]
    if verb in ("selftest", "--selftest"):
        cmd_selftest()
    elif verb == "filecheck":
        cmd_filecheck(args[1] if len(args) > 1 else None)
    elif verb == "resolve":
        cmd_resolve()
    elif verb == "flip":
        cmd_flip()
    elif verb == "all":
        cmd_all()
    else:
        print(f"Unknown verb: {verb!r}")
        print(__doc__)
        sys.exit(2)


if __name__ == "__main__":
    main()
