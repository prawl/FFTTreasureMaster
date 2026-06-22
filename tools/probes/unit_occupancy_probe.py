"""Read-only live probe: unit occupancy / claim-detection anchor for FFT:IC 1.5.

Verifies the candidate memory addresses used by ClaimAudit.cs against the running
game (fft_enhanced.exe, image base 0x140000000) so the "disable tile once claimed"
feature can be anchored to the 1.5 build before going live.

ANCHORING RUNBOOK
-----------------
1.  Start a battle with a Chemist as the active unit (their turn queued).
2.  Run:  python tools\\probes\\unit_occupancy_probe.py peek
3.  Check the output:
      - nameId should be non-zero and match the Chemist's slot in the roster dump.
      - job should read 0x4b (Chemist) -- the line will print "Chemist".
      - (x, y) should match the Chemist's tile on the battlefield.
      - The last line should print ELIGIBLE.
4.  If job == 0x4b and (x,y) match the Chemist tile -- the candidate addresses are
    good for 1.5.  Flip Tuning.ClaimDetectionEnabled and test in-game.
5.  If the values look wrong (0x0000 nameId, unexpected job id, mismatched coords):
    the addresses have shifted for 1.5.  RE-ANCHOR with diag (below).

RE-ANCHOR RUNBOOK (when peek shows garbage)
-------------------------------------------
The cursor (0x140C6AFB8 / 0x140C6ADAC) and map id (0x140784478) ARE 1.5-verified, so
they are the ground truth used to re-find the unit data:
1.  In a battle, hover the cursor over the active unit's own tile.
2.  Run:  python tools\\probes\\unit_occupancy_probe.py diag
3.  diag reads the cursor (x,y), then finds the unit record whose (x,y) matches and
    hex-dumps it -- revealing the real 1.5 offsets for job (0x4B) and move (0xFD).
    If the static array base itself shifted, diag scans a broad window and reports
    stride-aligned candidates for the new base.  Paste the output to update Offsets.cs.

Verbs
-----
  python tools\\probes\\unit_occupancy_probe.py diag
      RE-ANCHOR. Hover the active unit's tile first. Uses the 1.5-verified cursor as
      ground-truth (x,y) to locate the unit record in the static array, hex-dumps the
      matched slot (so the job / move offsets are visible), and -- if the base shifted --
      scans a broad window for stride-aligned candidates. The fastest path to 1.5 addrs.

  python tools\\probes\\unit_occupancy_probe.py finddiff
      MOST ROBUST re-anchor. Interactive: hover the unit, snapshot; MOVE the unit,
      hover it, snapshot; the one byte pair that tracked (oldX,oldY) -> (newX,newY)
      at the same address is the unit's grid X (Y at +1). Also reports where 0x4b
      (Chemist) / 0xfd (Treasure Hunter) sit relative to it. Use this when diag's
      single-shot match is ambiguous (coincidental coords).

  python tools\\probes\\unit_occupancy_probe.py peek
      Read all candidates once.  Prints the active unit nameId, the matched roster
      slot, its job id (tagged "Chemist" if 0x4b), its move-ability id (tagged
      "Treasure Hunter" if 0xfd), active (x,y), and "ELIGIBLE" or "not eligible".
      Says so plainly if reads fail (addresses likely shifted for 1.5).

  python tools\\probes\\unit_occupancy_probe.py watch
      ~5 Hz live stream of (nameId, job, move, x, y, eligible) so you cycle/move
      units in-battle and confirm the values track.  Ctrl-C to stop.

  python tools\\probes\\unit_occupancy_probe.py roster
      Dump all 20 roster slots (index, nameId, job, move-ability) to sanity-check
      RosterBase / RosterStride / offset layout.

  python tools\\probes\\unit_occupancy_probe.py scan <expectedHex>
      Re-anchor helper.  Scans a window around each candidate address for a u16/u8
      equal to <expectedHex> and prints hits with their delta from the candidate --
      enough to re-find a shifted address.  Mirror of treasure_reanchor_probe.py
      mapscan / diff logic.

  python tools\\probes\\unit_occupancy_probe.py collectsnap <tag> [loHex hiHex]
      Snapshot writable game memory to a temp file for later cross-battle diffing.

  python tools\\probes\\unit_occupancy_probe.py collectdiff <baselineTag> <wonATag> [<wonBTag>]
      Diff saved snapshots to surface persistent collected-treasure flag candidates.

  python tools\\probes\\unit_occupancy_probe.py --selftest
      OFFLINE (no game).  Validates: the roster slot-address formula
      (RosterBase + slot*0x258 + off), the eligibility rule (Chemist 0x4b OR
      Treasure Hunter 0xfd -> eligible; neither -> not), and a synthetic
      roster-match over a fake byte buffer (a slot whose nameId matches -> correct
      job/move returned).  Exit 1 on any failure, 0 on success.
"""

import ctypes
import ctypes.wintypes as w
import os
import pickle
import struct
import sys
import tempfile
import time

# ---------------------------------------------------------------------------
# Candidate addresses (kept in sync with FFTTreasureMaster/Offsets.cs --
# UNVERIFIED for 1.5; the non-uniform shift means they may need re-anchoring)
# ---------------------------------------------------------------------------
CONDENSED_ACTIVE_NAME_ID  = 0x14077D2A4   # u16 -- active unit condensed nameId
ACTIVE_UNIT_X             = 0x14077D360   # u16 -- active unit battlefield X
ACTIVE_UNIT_Y             = 0x14077D362   # u16 -- active unit battlefield Y

ROSTER_BASE               = 0x1411A18D0   # start of the 20-slot player roster array
ROSTER_STRIDE             = 0x258         # bytes per roster slot
ROSTER_SLOTS              = 20
ROSTER_NAME_ID_OFF        = 0x230         # u16 offset within a slot: nameId
ROSTER_JOB_OFF            = 0x02          # u8  offset within a slot: job id
ROSTER_MOVE_ABIL_OFF      = 0x0C          # u8  offset within a slot: movement ability id

CHEMIST_JOB_ID            = 0x4B          # 75
TREASURE_HUNTER_ABIL_ID   = 0xFD          # 253

# Scan window: +/- this many bytes around each candidate when re-anchoring
SCAN_WINDOW               = 0x400

# ---------------------------------------------------------------------------
# 1.5-VERIFIED ground-truth anchors (from tools/probes/treasure_flags.py and
# FFTTreasureMaster/Offsets.cs -- these are re-found and live-confirmed for 1.5,
# so they are the lever for re-anchoring the UNVERIFIED candidates above).
# ---------------------------------------------------------------------------
CURSOR_X   = 0x140C6AFB8   # u8 -- cursor tile X (1.5, treasure_flags.py)
CURSOR_Y   = 0x140C6ADAC   # u8 -- cursor tile Y (1.5, treasure_flags.py)
MAP_ID     = 0x140784478   # u8 -- live battle map id (1.5, Offsets.LiveBattleMapId)

# FFTHandsFree static battle unit array (rated 1.5-verified there; CONFIRM via diag).
# Per-unit record is STATIC_STRIDE bytes; player slots are n=1..10, enemy n=-20..-1.
STATIC_ARRAY_BASE = 0x140893C00
STATIC_STRIDE     = 0x200
STATIC_OFF_ALIVE  = 0x12    # u16 -- in-battle flag (1 = alive/in-battle)
STATIC_OFF_X      = 0x33    # u8  -- grid X
STATIC_OFF_Y      = 0x34    # u8  -- grid Y (immediately follows X)
STATIC_SLOT_LO    = -22     # scan margin below the base
STATIC_SLOT_HI    = 12      # scan margin above the base
STATIC_DUMP_LEN   = 0x60    # bytes of a matched slot to hex-dump (hunt job 0x4B / move 0xFD)

# Broad relocate window if the static base itself shifted for 1.5.
RELOC_LO   = 0x140870000
RELOC_HI   = 0x1408C0000

# ---------------------------------------------------------------------------
# Process access (lazy, fail-soft -- no sys.exit at module load)
# ---------------------------------------------------------------------------
PROCESS_VM_READ           = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400

# Region-walk constants for the move-differential snapshot (module span only).
MEM_COMMIT    = 0x1000
PAGE_GUARD    = 0x100
PAGE_NOACCESS = 0x01
WRITABLE_PROT = 0x04 | 0x08 | 0x40 | 0x80   # RW | WriteCopy | ExecRW | ExecWriteCopy
IMAGE_BASE    = 0x140000000
IMAGE_END     = 0x143000000
UI_ARENA      = (0x140C63000, 0x140CC5000)  # cursor/render arena -- unit data is NOT here

k32   = ctypes.windll.kernel32
psapi = ctypes.windll.psapi


class _MBI(ctypes.Structure):
    _fields_ = [
        ("BaseAddress",       ctypes.c_void_p),
        ("AllocationBase",    ctypes.c_void_p),
        ("AllocationProtect", w.DWORD),
        ("PartitionId",       w.WORD),
        ("RegionSize",        ctypes.c_size_t),
        ("State",             w.DWORD),
        ("Protect",           w.DWORD),
        ("Type",              w.DWORD),
    ]


_HANDLE = None


def _open_process(name: str = "fft_enhanced.exe"):
    arr    = (w.DWORD * 4096)()
    needed = w.DWORD()
    psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    want = PROCESS_QUERY_INFORMATION | PROCESS_VM_READ
    for i in range(needed.value // ctypes.sizeof(w.DWORD)):
        h = k32.OpenProcess(want, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower():
            return h
        k32.CloseHandle(h)
    return None


def _handle():
    global _HANDLE
    if _HANDLE is None:
        _HANDLE = _open_process()
    return _HANDLE


def _require_game():
    h = _handle()
    if not h:
        print("process not found (fft_enhanced.exe not running)")
        sys.exit(1)
    return h


# ---------------------------------------------------------------------------
# RPM helpers -- RPM ONLY, never WriteProcessMemory
# ---------------------------------------------------------------------------
def rpm(addr: int, n: int) -> bytes | None:
    h = _handle()
    if not h:
        return None
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    ok  = k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    if not ok or got.value != n:
        return None
    return buf.raw


def ru8(addr: int) -> int | None:
    b = rpm(addr, 1)
    return b[0] if b is not None else None


def ru16(addr: int) -> int | None:
    b = rpm(addr, 2)
    return struct.unpack_from("<H", b)[0] if b is not None else None


# ---------------------------------------------------------------------------
# Module-span snapshot + move-differential (RPM + VirtualQueryEx only)
# ---------------------------------------------------------------------------
def _writable_regions() -> list[tuple[int, int]]:
    """(base, size) for committed, writable, non-guard regions in the module span."""
    out: list[tuple[int, int]] = []
    h = _handle()
    if not h:
        return out
    addr, mbi = IMAGE_BASE, _MBI()
    while addr < IMAGE_END:
        if not k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize
        if (mbi.State == MEM_COMMIT and (mbi.Protect & WRITABLE_PROT)
                and not (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS))):
            out.append((base, size))
        nxt  = base + size
        addr = nxt if nxt > addr else addr + 0x1000
    return out


def _in_ui(addr: int) -> bool:
    return UI_ARENA[0] <= addr < UI_ARENA[1]


def _snapshot() -> dict[int, bytes]:
    """Snapshot every writable module-span region (excluding the UI render arena)."""
    snap: dict[int, bytes] = {}
    for base, size in _writable_regions():
        if _in_ui(base):
            continue
        buf = rpm(base, size)
        if buf is not None:
            snap[base] = buf
    return snap


def _diff_position_pairs(snap1: dict[int, bytes], snap2: dict[int, bytes],
                         x1: int, y1: int, x2: int, y2: int) -> list[int]:
    """Addresses A where snap1 has adjacent bytes (x1,y1) at A and snap2 has (x2,y2) at A.
    That byte pair tracked the unit's move -- it is the unit's grid X (Y at A+1)."""
    out: list[int] = []
    for base, b1 in snap1.items():
        b2 = snap2.get(base)
        if b2 is None:
            continue
        n = min(len(b1), len(b2)) - 1
        for i in range(n):
            if b1[i] == x1 and b1[i + 1] == y1 and b2[i] == x2 and b2[i + 1] == y2:
                out.append(base + i)
    return out


INVENTORY_COUNT_BASE = 0x1411A17C0   # prototype note: count[id] = u8 @ base + itemId (verify on 1.5)


def _find_increments(snap1: dict[int, bytes], snap2: dict[int, bytes]) -> list[tuple[int, int, int]]:
    """Addresses where snap2 byte == snap1 byte + 1 -- a count tick (e.g. an item added)."""
    out: list[tuple[int, int, int]] = []
    for base, b1 in snap1.items():
        b2 = snap2.get(base)
        if b2 is None:
            continue
        n = min(len(b1), len(b2))
        for i in range(n):
            if b1[i] != 0xFF and b2[i] == b1[i] + 1:
                out.append((base + i, b1[i], b2[i]))
    return out


def _window_from_snap(snap: dict[int, bytes], lo: int, n: int) -> tuple[bytes, int] | None:
    """Return (window_bytes, lo) for [lo, lo+n) if a single snapshot region covers it."""
    for base, buf in snap.items():
        if base <= lo and lo + n <= base + len(buf):
            return buf[lo - base: lo - base + n], lo
    return None


# ---------------------------------------------------------------------------
# Domain helpers
# ---------------------------------------------------------------------------
def _slot_base(slot: int) -> int:
    return ROSTER_BASE + slot * ROSTER_STRIDE


def _read_slot(slot: int) -> tuple[int | None, int | None, int | None]:
    """Return (nameId, job, move_abil) for a roster slot.  Any field is None on read failure."""
    base   = _slot_base(slot)
    name   = ru16(base + ROSTER_NAME_ID_OFF)
    job    = ru8 (base + ROSTER_JOB_OFF)
    move   = ru8 (base + ROSTER_MOVE_ABIL_OFF)
    return name, job, move


def _eligible(job: int | None, move: int | None) -> bool:
    if job  == CHEMIST_JOB_ID:          return True
    if move == TREASURE_HUNTER_ABIL_ID: return True
    return False


def _fmt_job(job: int | None) -> str:
    if job is None:        return "??"
    tag = " (Chemist)"        if job  == CHEMIST_JOB_ID          else ""
    return f"0x{job:02x}{tag}"


def _fmt_move(move: int | None) -> str:
    if move is None:       return "??"
    tag = " (Treasure Hunter)" if move == TREASURE_HUNTER_ABIL_ID else ""
    return f"0x{move:02x}{tag}"


def _read_active() -> dict:
    """Read all candidate fields once.  Returns a dict with keys:
    nameId, matched_slot, job, move, x, y -- each None on read failure."""
    name_id = ru16(CONDENSED_ACTIVE_NAME_ID)
    x       = ru16(ACTIVE_UNIT_X)
    y       = ru16(ACTIVE_UNIT_Y)

    matched_slot = None
    job          = None
    move         = None

    if name_id is not None:
        for slot in range(ROSTER_SLOTS):
            sn, sj, sm = _read_slot(slot)
            if sn == name_id:
                matched_slot = slot
                job          = sj
                move         = sm
                break

    return dict(nameId=name_id, matched_slot=matched_slot, job=job, move=move, x=x, y=y)


# ---------------------------------------------------------------------------
# Verb: peek
# ---------------------------------------------------------------------------
def cmd_peek() -> None:
    _require_game()
    d = _read_active()

    name_id = d["nameId"]
    if name_id is None:
        print("CondensedActiveNameId read FAILED -- address may have shifted for 1.5")
        print("  try: scan <expected_nameId_hex>  to re-find the address")
        return

    print(f"  CondensedActiveNameId @ 0x{CONDENSED_ACTIVE_NAME_ID:011x} = 0x{name_id:04x}")

    if d["matched_slot"] is None:
        print(f"  roster scan: no slot matched nameId 0x{name_id:04x}")
        print("  (nameId 0x0000 is normal outside a player turn; otherwise RosterBase/Stride may be off)")
    else:
        slot = d["matched_slot"]
        base = _slot_base(slot)
        print(f"  roster match: slot {slot}  (base 0x{base:011x})")
        print(f"    job        @ +0x{ROSTER_JOB_OFF:03x} = {_fmt_job(d['job'])}")
        print(f"    move-abil  @ +0x{ROSTER_MOVE_ABIL_OFF:03x} = {_fmt_move(d['move'])}")

    x = d["x"]
    y = d["y"]
    if x is None or y is None:
        print("  ActiveUnitX / ActiveUnitY read FAILED -- addresses may have shifted for 1.5")
    else:
        print(f"  ActiveUnitX @ 0x{ACTIVE_UNIT_X:011x} = {x}")
        print(f"  ActiveUnitY @ 0x{ACTIVE_UNIT_Y:011x} = {y}")

    if _eligible(d["job"], d["move"]):
        print("ELIGIBLE (Chemist or Treasure Hunter on active turn)")
    else:
        print("not eligible (no match, read failure, or non-eligible job/move)")


# ---------------------------------------------------------------------------
# Verb: watch
# ---------------------------------------------------------------------------
def cmd_watch() -> None:
    _require_game()
    print("watch ~5 Hz -- Ctrl-C to stop")
    print(f"  {'nameId':>8}  {'slot':>4}  {'job':>8}  {'move':>8}  {'x':>4}  {'y':>4}  eligible")
    last = None
    try:
        while True:
            d   = _read_active()
            row = (d["nameId"], d["matched_slot"], d["job"], d["move"], d["x"], d["y"])
            if row != last:
                name_s = f"0x{d['nameId']:04x}" if d["nameId"] is not None else "??"
                slot_s = str(d["matched_slot"]) if d["matched_slot"] is not None else "--"
                job_s  = f"0x{d['job']:02x}"    if d["job"]  is not None else "??"
                move_s = f"0x{d['move']:02x}"   if d["move"] is not None else "??"
                x_s    = str(d["x"])             if d["x"]   is not None else "??"
                y_s    = str(d["y"])             if d["y"]   is not None else "??"
                elig   = "YES" if _eligible(d["job"], d["move"]) else "no"
                print(f"  {name_s:>8}  {slot_s:>4}  {job_s:>8}  {move_s:>8}  {x_s:>4}  {y_s:>4}  {elig}")
                last = row
            time.sleep(0.2)
    except KeyboardInterrupt:
        print("\nstopped.")


# ---------------------------------------------------------------------------
# Verb: roster
# ---------------------------------------------------------------------------
def cmd_roster() -> None:
    _require_game()
    print(f"Roster dump (base 0x{ROSTER_BASE:011x}, stride 0x{ROSTER_STRIDE:x}, slots {ROSTER_SLOTS})")
    print(f"  {'slot':>4}  {'base':>13}  {'nameId':>8}  {'job':>6}  {'move':>6}")
    for slot in range(ROSTER_SLOTS):
        base        = _slot_base(slot)
        name, j, m = _read_slot(slot)
        name_s = f"0x{name:04x}" if name is not None else "??"
        j_s    = f"0x{j:02x}"   if j    is not None else "??"
        m_s    = f"0x{m:02x}"   if m    is not None else "??"
        tags   = []
        if j == CHEMIST_JOB_ID:          tags.append("Chemist")
        if m == TREASURE_HUNTER_ABIL_ID: tags.append("TreasureHunter")
        tag_s  = "  <- " + ", ".join(tags) if tags else ""
        print(f"  {slot:>4}  0x{base:011x}  {name_s:>8}  {j_s:>6}  {m_s:>6}{tag_s}")


# ---------------------------------------------------------------------------
# Verb: scan
# ---------------------------------------------------------------------------
def cmd_scan(expected_hex: str) -> None:
    _require_game()
    try:
        expected = int(expected_hex, 16)
    except ValueError:
        print(f"expected a hex value, got {expected_hex!r}")
        sys.exit(2)

    # Candidates: (label, addr, width)
    candidates = [
        ("CondensedActiveNameId", CONDENSED_ACTIVE_NAME_ID, 2),
        ("ActiveUnitX",           ACTIVE_UNIT_X,            2),
        ("ActiveUnitY",           ACTIVE_UNIT_Y,            2),
    ]

    print(f"scan for 0x{expected:x} around unit-occupancy candidates (window +/-0x{SCAN_WINDOW:x})")
    for label, cand, width in candidates:
        lo  = cand - SCAN_WINDOW
        buf = rpm(lo, SCAN_WINDOW * 2 + width)
        if buf is None:
            print(f"  {label} @ 0x{cand:011x}: read FAILED")
            continue
        hits = []
        fmt   = "<H" if width == 2 else "<B"
        limit = len(buf) - width + 1
        for i in range(limit):
            val = struct.unpack_from(fmt, buf, i)[0]
            if val == expected:
                hits.append(lo + i)
        if not hits:
            print(f"  {label} @ 0x{cand:011x}: no hits for 0x{expected:x} in window")
        else:
            hits.sort(key=lambda a: abs(a - cand))
            print(f"  {label} @ 0x{cand:011x}: {len(hits)} hit(s)")
            for a in hits[:12]:
                delta = a - cand
                sign  = "+" if delta >= 0 else "-"
                print(f"    0x{a:011x}  (candidate {sign}0x{abs(delta):x})")


# ---------------------------------------------------------------------------
# Verb: diag -- one-shot re-anchor diagnostic keyed on the 1.5-verified cursor
# ---------------------------------------------------------------------------
def _find_adjacent_pairs(buf: bytes, cx: int, cy: int) -> list[int]:
    """Offsets i where buf[i]==cx and buf[i+1]==cy (grid X and Y are adjacent bytes)."""
    out = []
    for i in range(len(buf) - 1):
        if buf[i] == cx and buf[i + 1] == cy:
            out.append(i)
    return out


def _hexdump(base: int, data: bytes) -> None:
    for off in range(0, len(data), 16):
        row  = data[off:off + 16]
        hexs = " ".join(f"{b:02x}" for b in row)
        print(f"    +0x{off:02x} (0x{base + off:011x}): {hexs}")


def cmd_diag() -> None:
    """Hover the active unit's tile, then run this. Uses the 1.5-verified cursor as
    ground-truth (x,y) to locate the unit record and hex-dump it for job/move offsets."""
    _require_game()

    map_id = ru8(MAP_ID)
    cx     = ru8(CURSOR_X)
    cy     = ru8(CURSOR_Y)
    print(f"map id @ 0x{MAP_ID:011x} = {map_id}")
    print(f"cursor @ ({cx}, {cy})  <- hover the unit's tile so this equals its position")
    if cx is None or cy is None:
        print("cursor unreadable -- get a unit on the field, hover its tile, then re-run.")
        return

    nm = ru16(CONDENSED_ACTIVE_NAME_ID)
    print("\ncurrent UNVERIFIED candidates (expected garbage if shifted for 1.5):")
    print(f"  CondensedActiveNameId = {('0x%04x' % nm) if nm is not None else '??'}")
    print(f"  ActiveUnitX / Y       = {ru16(ACTIVE_UNIT_X)} / {ru16(ACTIVE_UNIT_Y)}")

    print(f"\nstatic array @ 0x{STATIC_ARRAY_BASE:011x} stride 0x{STATIC_STRIDE:x} "
          f"(slots {STATIC_SLOT_LO}..{STATIC_SLOT_HI})")
    print(f"  {'slot':>4}  {'base':>13}  {'alive':>6}  {'x':>3}  {'y':>3}")
    matches = []
    for n in range(STATIC_SLOT_LO, STATIC_SLOT_HI + 1):
        base  = STATIC_ARRAY_BASE + n * STATIC_STRIDE
        alive = ru16(base + STATIC_OFF_ALIVE)
        x     = ru8(base + STATIC_OFF_X)
        y     = ru8(base + STATIC_OFF_Y)
        is_match  = (x is not None and y is not None and x == cx and y == cy)
        plausible = (alive in (0, 1) and x is not None and x <= 30 and y is not None and y <= 30)
        if is_match or plausible:
            tag    = "  <- MATCHES CURSOR" if is_match else ""
            alives = f"0x{alive:04x}" if alive is not None else "??"
            print(f"  {n:>4}  0x{base:011x}  {alives:>6}  "
                  f"{(x if x is not None else '??'):>3}  {(y if y is not None else '??'):>3}{tag}")
        if is_match:
            matches.append((n, base))

    if matches:
        for n, base in matches:
            print(f"\nslot {n} matches the cursor tile -- hex dump (hunt job 0x4B / move 0xFD):")
            data = rpm(base, STATIC_DUMP_LEN)
            if data is not None:
                _hexdump(base, data)
                for tag, val in (("Chemist 0x4b", CHEMIST_JOB_ID),
                                 ("TreasureHunter 0xfd", TREASURE_HUNTER_ABIL_ID)):
                    offs = [o for o, b in enumerate(data) if b == val]
                    if offs:
                        print(f"      {tag} appears at: " + ", ".join(f"+0x{o:02x}" for o in offs))
        print("\n-> static array looks VALID for 1.5. Paste this; the detector can re-base on it.")
        return

    print("\nno nominal slot matched -- scanning a broad window to relocate the array ...")
    buf = rpm(RELOC_LO, RELOC_HI - RELOC_LO)
    if buf is None:
        print("  relocate read FAILED")
        return
    hits = _find_adjacent_pairs(buf, cx, cy)
    print(f"  {len(hits)} adjacent ({cx},{cy}) byte-pair(s) in "
          f"0x{RELOC_LO:011x}..0x{RELOC_HI:011x}:")
    for i in hits[:24]:
        addr         = RELOC_LO + i
        implied_base = addr - STATIC_OFF_X
        rel          = implied_base - STATIC_ARRAY_BASE
        aligned      = (rel % STATIC_STRIDE == 0)
        note         = f"  slot {rel // STATIC_STRIDE} (aligned to stride)" if aligned else ""
        sign         = "+" if rel >= 0 else "-"
        print(f"    x@0x{addr:011x}  impliedBase 0x{implied_base:011x}  rel {sign}0x{abs(rel):x}{note}")
    print("\n-> paste this; aligned hits reveal the 1.5 static-array base/stride.")


# ---------------------------------------------------------------------------
# Verb: array -- map the unit array off one known grid-X address (stride 0x200)
# ---------------------------------------------------------------------------
UNIT_RECORD_STRIDE = 0x200    # confirmed: two player units' X fields were 0x200 apart
MOVE_OFF_FROM_X    = -0x37    # confirmed: move-ability byte sits at X-0x37


def cmd_array(addr_hex: str, count_hex: str = "0x20") -> None:
    """Given one unit's grid-X address, walk +/- count slots at stride 0x200 and
    print each slot's (x, y, move-ability). Flags on-grid units and Treasure Hunter
    (0xfd) so the array base, extent, and which units can claim become visible."""
    _require_game()
    try:
        x0    = int(addr_hex, 16)
        count = int(count_hex, 16)
    except ValueError:
        print("usage: array <Xaddr_hex> [count_hex]"); return

    cx, cy = ru8(CURSOR_X), ru8(CURSOR_Y)
    print(f"cursor (hovered tile) = ({cx}, {cy})")
    print(f"unit array off X 0x{x0:011x}, stride 0x{UNIT_RECORD_STRIDE:x}, "
          f"move-ability @ X{MOVE_OFF_FROM_X:#x}")
    print(f"  {'n':>4}  {'slotX':>13}  {'x':>3}  {'y':>3}  {'move':>5}  flags")
    for n in range(-count, count + 1):
        sx   = x0 + n * UNIT_RECORD_STRIDE
        x    = ru8(sx)
        y    = ru8(sx + 1)
        move = ru8(sx + MOVE_OFF_FROM_X)
        on_grid = (x is not None and x <= 30 and y is not None and y <= 30)
        th      = (move == TREASURE_HUNTER_ABIL_ID)
        if on_grid or th:
            flags = []
            if on_grid: flags.append("on-grid")
            if th:      flags.append("TREASURE-HUNTER")
            if x == cx and y == cy: flags.append("<-CURSOR")
            xs = f"{x}"    if x    is not None else "??"
            ys = f"{y}"    if y    is not None else "??"
            ms = f"0x{move:02x}" if move is not None else "??"
            print(f"  {n:>4}  0x{sx:011x}  {xs:>3}  {ys:>3}  {ms:>5}  {' '.join(flags)}")
    print("  -> contiguous on-grid rows = the live units; note the lowest/highest n.")


# ---------------------------------------------------------------------------
# Verb: record -- dump a unit record around its grid-X address (offsets vs X)
# ---------------------------------------------------------------------------
def cmd_record(addr_hex: str, span_hex: str = "0x80") -> None:
    """Hex-dump [X-span, X+span) around a grid-X address found by finddiff, with
    offsets shown relative to X (so the same layout lines up across two units)."""
    _require_game()
    try:
        x_addr = int(addr_hex, 16)
        span   = int(span_hex, 16)
    except ValueError:
        print("usage: record <Xaddr_hex> [span_hex]"); return

    lo   = x_addr - span
    data = rpm(lo, span * 2)
    if data is None:
        print(f"  read FAILED at 0x{lo:011x}"); return

    print(f"record around X @ 0x{x_addr:011x}  (offsets relative to X; X=+0x00, Y=+0x01)")
    for off in range(0, len(data), 16):
        row     = data[off:off + 16]
        abs_lo  = lo + off
        rel0    = abs_lo - x_addr
        cells = []
        for j, b in enumerate(row):
            rel = (abs_lo + j) - x_addr
            mark = ""
            if rel == 0:                         mark = "<"   # X
            elif rel == 1:                       mark = ">"   # Y
            elif b == CHEMIST_JOB_ID:            mark = "J"   # 0x4b candidate
            elif b == TREASURE_HUNTER_ABIL_ID:   mark = "T"   # 0xfd candidate
            cells.append(f"{b:02x}{mark}" if mark else f"{b:02x} ")
        sign = "+" if rel0 >= 0 else "-"
        print(f"  X{sign}0x{abs(rel0):03x}: " + " ".join(cells))
    print("  legend: <=X  >=Y  J=0x4b(Chemist?)  T=0xfd(TreasureHunter?)")


# ---------------------------------------------------------------------------
# Verb: finddiff -- move-differential to pin the unit record (robust re-anchor)
# ---------------------------------------------------------------------------
def cmd_finddiff() -> None:
    """Pin the unit's grid (x,y) field by moving the unit and diffing snapshots.
    Coincidental (x,y) byte pairs do not also change to the new tile, so this
    isolates the real unit record even when the array layout is unknown."""
    _require_game()
    print("Move-differential re-anchor. Keep the SAME unit; hover ITS tile each time.")
    try:
        input("  1/2  hover the unit on its CURRENT tile, then press Enter ...")
    except (EOFError, KeyboardInterrupt):
        print("\naborted."); return
    x1, y1 = ru8(CURSOR_X), ru8(CURSOR_Y)
    if x1 is None or y1 is None:
        print("  cursor unreadable -- is a unit on the field?"); return
    print(f"  tile 1 = ({x1}, {y1}); snapshotting module memory ...")
    snap1 = _snapshot()
    print(f"  snapshot 1: {len(snap1)} region(s).")

    try:
        input("  2/2  MOVE the unit to a DIFFERENT tile, hover it, then press Enter ...")
    except (EOFError, KeyboardInterrupt):
        print("\naborted."); return
    x2, y2 = ru8(CURSOR_X), ru8(CURSOR_Y)
    if x2 is None or y2 is None:
        print("  cursor unreadable."); return
    print(f"  tile 2 = ({x2}, {y2}); snapshotting ...")
    if (x1, y1) == (x2, y2):
        print("  tile did not change -- move the unit to a different tile and retry."); return
    snap2 = _snapshot()

    hits = _diff_position_pairs(snap1, snap2, x1, y1, x2, y2)
    print(f"\n{len(hits)} byte pair(s) tracked ({x1},{y1}) -> ({x2},{y2})  "
          f"(the unit grid X; Y at +1):")
    if not hits:
        print("  none -- ensure you moved the SAME unit and re-hovered its tile, then retry.")
        return
    for a in hits[:24]:
        print(f"  X @ 0x{a:011x}   Y @ 0x{a + 1:011x}")
        win = _window_from_snap(snap2, a - 0x60, 0xC0)
        if win is not None:
            seg, seg_lo = win
            chem = [(seg_lo + i) - a for i, b in enumerate(seg) if b == CHEMIST_JOB_ID]
            th   = [(seg_lo + i) - a for i, b in enumerate(seg) if b == TREASURE_HUNTER_ABIL_ID]
            if chem:
                print("      0x4b (Chemist) near X at: " + ", ".join(f"X{d:+#x}" for d in chem))
            if th:
                print("      0xfd (Treasure Hunter) near X at: " + ", ".join(f"X{d:+#x}" for d in th))
    print("\n-> paste this. The single stable X address (and the 0x4b/0xfd offsets relative to it)")
    print("   define the 1.5 unit record; I will re-base ClaimAudit + Offsets on it.")


# ---------------------------------------------------------------------------
# Verb: claimdiff -- find the claim signal (what increments when a treasure is taken)
# ---------------------------------------------------------------------------
def cmd_claimdiff() -> None:
    """Snapshot, claim a treasure, snapshot, report bytes that incremented by +1 -- the
    inventory count for the claimed item ticks up, which is the real 'claimed' signal
    (covers Chemist AND a Treasure-Hunter unit alike). Confirms the signal exists
    mid-battle and locates the 1.5 inventory-count address."""
    _require_game()
    print("Claim-signal finder. A claimed Move-Find item is added to inventory (count +1).")
    try:
        input("  1/2  BEFORE claiming -- in a battle, ready, then press Enter ...")
    except (EOFError, KeyboardInterrupt):
        print("\naborted."); return
    snap1 = _snapshot()
    print(f"  snapshot 1: {len(snap1)} region(s).")
    try:
        input("  2/2  CLAIM a treasure now (Chemist or Treasure-Hunter unit onto a tile), Enter ...")
    except (EOFError, KeyboardInterrupt):
        print("\naborted."); return
    snap2 = _snapshot()

    incs = _find_increments(snap1, snap2)
    # Drop the UI/render arena (cursor + billboard churn from the move animation) -- _snapshot's
    # base-level filter leaks individual UI addresses, so reject them by address here.
    incs = [t for t in incs if not (0x140C63000 <= t[0] < 0x140CC5000)]
    print(f"\n{len(incs)} +1 increment(s) outside the UI arena (the claimed item's count is among these).")

    # Histogram by 64KB band -- the inventory count is in a 'data' band that barely churns.
    from collections import Counter
    bands = Counter((a >> 16) << 16 for a, _, _ in incs)
    print("\n  increments per 64KB band (band: count):")
    for band in sorted(bands):
        print(f"    0x{band:011x}: {bands[band]}")

    # Party/inventory data band (roster 0x1411A18D0, inventory hypothesis 0x1411A17C0 both here).
    lo, hi = 0x141190000, 0x1411B0000
    band_hits = [t for t in incs if lo <= t[0] < hi]
    print(f"\n  increments in the party/inventory band 0x{lo:011x}..0x{hi:011x} ({len(band_hits)}):")
    for a, b, c in band_hits:
        item = a - INVENTORY_COUNT_BASE
        tag  = f"   <- itemId {item} (0x{item:02x}) at base+0x{item:x}" if 0 <= item <= 0xFF else ""
        print(f"    0x{a:011x}  {b} -> {c}{tag}")
    if not incs:
        print("  none at all -- the claim may not hit inventory mid-battle (added on the results screen?).")
    print(f"\n  (inventory-count base hypothesis: 0x{INVENTORY_COUNT_BASE:011x} + itemId)")


# ---------------------------------------------------------------------------
# Verb: claimisolate -- subtract move-noise to isolate the claim's inventory tick
# ---------------------------------------------------------------------------
def cmd_claimisolate() -> None:
    """Three snapshots: baseline, a NON-claiming move, then a CLAIMING move. The per-turn
    array churn happens on BOTH moves (so it cancels); the item-count +1 happens only on the
    claim, so it survives. Isolates the inventory-count address that claimdiff drowned in noise."""
    _require_game()
    print("Claim isolator: a normal move's churn is subtracted so the claim's +1 survives.")
    try:
        input("  1/3  BEFORE moving -- in a battle, ready, Enter ...")
        snap_a = _snapshot()
        print(f"    baseline: {len(snap_a)} region(s).")
        input("  2/3  Make a NON-claiming move (a unit onto a NORMAL tile, end its move), Enter ...")
        snap_b = _snapshot()
        input("  3/3  Now CLAIM a treasure (Chemist or Treasure-Hunter unit onto a treasure tile), Enter ...")
        snap_c = _snapshot()
    except (EOFError, KeyboardInterrupt):
        print("\naborted."); return

    move_noise = {a for a, _, _ in _find_increments(snap_a, snap_b)}
    claim_incs = _find_increments(snap_b, snap_c)
    specific = [(a, b, c) for a, b, c in claim_incs
                if a not in move_noise and not (0x140C63000 <= a < 0x140CC5000)]

    print(f"\n  move-noise subtracted: {len(move_noise)} addr(s).")
    print(f"  {len(specific)} claim-specific +1 increment(s) (the item count should be here):")
    for a, b, c in specific[:60]:
        item = a - INVENTORY_COUNT_BASE
        tag  = f"   <- itemId {item} (0x{item:02x})" if 0 <= item <= 0xFF else ""
        print(f"    0x{a:011x}  {b} -> {c}{tag}")
    if not specific:
        print("    none -- claim added nothing distinct from a normal move (added at battle end?).")


# ---------------------------------------------------------------------------
# Verb: invtrack -- find the PERSISTENT inventory count via a +/-1 menu change
# ---------------------------------------------------------------------------
def _find_pm1(snap1: dict[int, bytes], snap2: dict[int, bytes]) -> list[tuple[int, int, int]]:
    """Addresses that changed by exactly +/-1 with both values <= 99 (an item count tick).
    Excludes the UI render arena."""
    out: list[tuple[int, int, int]] = []
    for base, b1 in snap1.items():
        b2 = snap2.get(base)
        if b2 is None:
            continue
        n = min(len(b1), len(b2))
        for i in range(n):
            a = base + i
            if 0x140C63000 <= a < 0x140CC5000:
                continue
            if b1[i] <= 99 and b2[i] <= 99 and abs(b2[i] - b1[i]) == 1:
                out.append((a, b1[i], b2[i]))
    return out


def _find_stable_flips(before, after):
    """Addresses whose byte changed between two snapshots (each a dict[int, bytes] keyed by region
    base). Skips the UI/render arena and any region missing from either snapshot. Returns a list of
    (addr, before_byte, after_byte). 'Stable' means the caller took before/after at the SAME game
    context (e.g. both on the world map) so a difference reflects persistent state, not animation."""
    out = []
    for base, b1 in before.items():
        b2 = after.get(base)
        if b2 is None:
            continue
        n = min(len(b1), len(b2))
        for i in range(n):
            if b1[i] != b2[i]:
                a = base + i
                if _in_ui(a):
                    continue
                out.append((a, b1[i], b2[i]))
    return out


def _snap_path(tag):
    return os.path.join(tempfile.gettempdir(), "fft_collectsnap_" + tag + ".pkl")


def _save_snapshot(tag, snap):
    with open(_snap_path(tag), "wb") as f:
        pickle.dump(snap, f, protocol=pickle.HIGHEST_PROTOCOL)


def _load_snapshot(tag):
    with open(_snap_path(tag), "rb") as f:
        return pickle.load(f)


def cmd_collectsnap(tag, lo=None, hi=None):
    """Snapshot writable game memory to a temp file for later cross-battle diffing.
    Run this BEFORE and AFTER a collect-and-WIN, BOTH taken at the SAME context (e.g. world map):
      1. collectsnap baseline                  (before, on the world map)
      2. enter a battle, collect ONE Move-Find treasure, WIN, return to the world map
      3. collectsnap wonA                       (after, same world-map spot)
      4. collectdiff baseline wonA              (see candidates)
    Optional loHex hiHex bounds the snapshot to a sub-range for a smaller file."""
    _require_game()
    snap = _snapshot()   # writable regions, excludes the UI arena
    if lo is not None and hi is not None:
        snap = {b: d for b, d in snap.items() if not (b + len(d) <= lo or b >= hi)}
    _save_snapshot(tag, snap)
    total = sum(len(d) for d in snap.values())
    print("  saved snapshot '" + tag + "' -> " + _snap_path(tag))
    print("  " + str(len(snap)) + " regions, " + str(total) + " bytes")
    print("  (these temp .pkl files are tens of MB each -- delete them when done)")


def cmd_collectdiff(baseline, won_a, won_b=None):
    """Diff saved snapshots to surface persistent collected-treasure flag CANDIDATES.
    IMPORTANT: this is a CANDIDATE LIST, not an isolation. A won battle also changes gil, XP/JP,
    the in-game clock, and story flags, so there will be noise. To narrow:
      * For won_b, collect a DIFFERENT tile on the SAME map from the SAME starting save. The flag for
        tile A vs tile B differs while most win-noise (gil/XP magnitude) is similar -- compare them.
      * Also take a 'collect but LOSE' snapshot: the real flag is SET when won and CLEAR when lost.
    Prefers single-bit 0->1 flips (a collected flag is usually one bit), banded by 64KB."""
    base = _load_snapshot(baseline)
    fa = _find_stable_flips(base, _load_snapshot(won_a))
    def _single_bit_set(t):
        x = t[1] ^ t[2]
        return t[2] > t[1] and (x & (x - 1)) == 0 and x != 0
    bits_a = [t for t in fa if _single_bit_set(t)]
    print("  baseline->" + won_a + ": " + str(len(fa)) + " byte flips, " + str(len(bits_a)) + " single-bit 0->1")
    for (a, b, c) in bits_a[:40]:
        print("    0x%011x  %02x -> %02x" % (a, b, c))
    if won_b:
        fb = _find_stable_flips(base, _load_snapshot(won_b))
        sa = {t[0] for t in fa}
        sb = {t[0] for t in fb}
        only_a = sorted(sa - sb)
        only_b = sorted(sb - sa)
        print("  shared (win-noise): " + str(len(sa & sb)) + "   only-A: " + str(len(only_a)) + "   only-B: " + str(len(only_b)))
        print("  per-tile candidates (unique to one collected tile):")
        for a in only_a[:40]:
            print("    only-A 0x%011x" % a)
        for a in only_b[:40]:
            print("    only-B 0x%011x" % a)


def cmd_invtrack() -> None:
    """Equip or UNEQUIP a Galewall in the menu (its inventory count changes by 1) between two
    snapshots; the count byte is the +/-1 change. Menu churn is tiny, so this isolates the
    persistent inventory-count address (the battle-claim signal was transient)."""
    _require_game()
    print("Inventory-count finder. A menu equip/unequip moves one Galewall in/out of inventory (count +/-1).")
    try:
        input("  1/2  In the equip menu, BEFORE the change, press Enter ...")
        snap1 = _snapshot()
        print(f"    snapshot 1: {len(snap1)} region(s).")
        input("  2/2  EQUIP or UNEQUIP a Galewall (count changes by 1), then Enter ...")
        snap2 = _snapshot()
    except (EOFError, KeyboardInterrupt):
        print("\naborted."); return

    hits = _find_pm1(snap1, snap2)
    print(f"\n{len(hits)} byte(s) changed by +/-1 (count <=99) -- the Galewall count is here:")
    for a, b, c in hits[:80]:
        item = a - GALEWALL_ITEM_ID   # if this is count[129], item is the array base
        print(f"  0x{a:011x}  {b} -> {c}   (if count[129]: invBase 0x{item:011x})")
    if not hits:
        print("  none in the module span -- the inventory may live outside it; widen the scan.")


# ---------------------------------------------------------------------------
# Verb: invscan -- disambiguate the inventory-count array from the 2->3 candidates
# ---------------------------------------------------------------------------
# The three bytes that went 2->3 on a Galewall (ItemData id 129) claim. If a candidate is the
# real count[129], then invBase = candidate - 129 and the whole 256-byte array should read like
# a sane sparse item-count list (id129 == 3, other ids small plausible counts).
GALEWALL_ITEM_ID  = 129
# Confirmed candidate: 0x1411A7C81 rose +1 on a Galewall claim AND fell -1 on a Galewall equip,
# and 0x1411A7C8F (= invBase+143, the swapped-out shield) moved with it -> invBase 0x1411A7C00.
_INV_CANDIDATES   = [0x1411A7C81]


def cmd_invscan() -> None:
    _require_game()
    for c in _INV_CANDIDATES:
        base = c - GALEWALL_ITEM_ID
        data = rpm(base, 256)
        if data is None:
            print(f"\ncandidate 0x{c:011x} -> invBase 0x{base:011x}: READ FAILED")
            continue
        nz   = [(i, data[i]) for i in range(256) if data[i] != 0]
        big  = sum(1 for _, v in nz if v > 99)   # real item counts are <=99; many >99 = not inventory
        print(f"\ncandidate 0x{c:011x} -> invBase 0x{base:011x}")
        print(f"  id129 (Galewall) = {data[GALEWALL_ITEM_ID]}   nonzero={len(nz)}/256   over-99={big}")
        print("  (id:count) " + " ".join(f"{i}:{v}" for i, v in nz[:80]))
    print("\n  -> the REAL inventory: id129==3, a modest count of small (<=99) nonzero entries.")


# ---------------------------------------------------------------------------
# Verb: tilescan -- per-tile captured-byte states, to compare a LIT tile vs a DARK
# tile in the SAME frame (zero battle-state noise). Read-only.
# ---------------------------------------------------------------------------
import json as _json
import os as _os


def _addr_dataset_path() -> str:
    return _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", "..",
                         "data", "treasure_addrs.json")


def cmd_tilescan(map_arg: str) -> None:
    """Dump every CAPTURED address (incl. the off=0x3f/0x7f companion families the bake drops and
    the mod never writes) for each tile of a map, with each byte's current state vs its resting
    'off': rest (==off), ON (==off|0x80), unread, or some other value (e.g. the mod's 0xCC).
    After Move mode lights SOME treasure tiles, run this once: a companion byte that reads ON on a
    lit tile but rest/other on a dark tile is the render gate the mod must also hold."""
    _require_game()
    try:
        mid = str(int(map_arg, 0))
    except ValueError:
        print(f"usage: tilescan <mapId>  (got {map_arg!r})"); return
    try:
        data = _json.load(open(_addr_dataset_path()))
    except OSError as e:
        print(f"could not read data/treasure_addrs.json: {e}"); return

    m = data.get("maps", {}).get(mid)
    if not m:
        print(f"map {mid} not in data/treasure_addrs.json"); return

    print(f"map {mid} {m.get('name')}: per-tile captured-byte states "
          f"(mod holds only the off=0/1 shipped bytes at 0xCC).")
    for t in m.get("tiles", []):
        x, y = t.get("x"), t.get("y")
        comp, ship = [], []
        for ah, oh in t.get("addrs", []):
            a, off = int(ah, 16), int(oh, 16)
            v = ru8(a)
            if   v is None:          st = "unread"
            elif v == off:           st = "rest"
            elif v == (off | 0x80):  st = "ON"
            else:                    st = f"0x{v:02x}"
            (comp if off not in (0, 1) else ship).append((a, off, v, st))
        on   = sum(1 for *_, s in comp if s == "ON")
        rest = sum(1 for *_, s in comp if s == "rest")
        un   = sum(1 for *_, s in comp if s == "unread")
        print(f"\n  tile ({x},{y}): companion[off!=0/1] ON={on} rest={rest} unread={un} "
              f"other={len(comp) - on - rest - un}   shipped[off=0/1]={len(ship)}")
        for a, off, v, st in comp:
            vs = f"0x{v:02x}" if v is not None else "??"
            print(f"      0x{a:011x} off=0x{off:02x} now={vs}  {st}")
    print("\n  -> a companion byte ON for a tile that RENDERED but rest/other for a DARK tile is the gate.")


# ---------------------------------------------------------------------------
# Verb: rendergate -- find the byte that gates the move-find highlight render
# (set when tiles are LIT, cleared after an in-battle Retry leaves them DARK).
# Noise-subtracted 3-snapshot differential INCLUDING the UI/render arena.
# ---------------------------------------------------------------------------
RENDER_ARENA = (0x140C63000, 0x140CC5000)   # the live render arena the bake EXCLUDES

# Representative captured render-flag mirrors for map 74 tile (0,1) (from
# data/treasure_addrs.json) -- used only to flag candidates that sit ADJACENT to a
# known tile mirror (a per-tile companion enable would land here).
_MAP74_TILE01_MIRRORS = [
    0x140de8077, 0x140f93191, 0x140f9b7df, 0x1411468f9, 0x14116c567,  # shipped (off 0/1)
    0x140f95982, 0x1411490ea, 0x1434b59ff,                            # dropped (off 0x3f / 0x7f)
]


def _snapshot_full() -> dict[int, bytes]:
    """Like _snapshot but INCLUDES the UI/render arena -- here we WANT the arena, because
    the visible highlight is most likely driven by a buffer inside it that the bake excludes."""
    snap: dict[int, bytes] = {}
    for base, size in _writable_regions():
        buf = rpm(base, size)
        if buf is not None:
            snap[base] = buf
    return snap


def cmd_rendergate() -> None:
    """Find the render-gate. Map 74, mod ON (holding 0xCC). Three snapshots: two while LIT
    (the second subtracts animation noise) and one after a Retry leaves the tiles DARK. A byte
    that is STABLE while lit but CHANGES when dark is a candidate gate. The arena and any byte
    adjacent to a known tile mirror are called out first."""
    _require_game()
    print("Render-gate finder. Map 74, mod running (holding 0xCC on the shipped mirrors).")
    try:
        input("  1/3  FIRST-LOAD the battle so tiles are LIT, then Enter ...")
        good1 = _snapshot_full()
        print(f"    snapshot 1: {len(good1)} region(s).")
        input("  2/3  wait ~1 second (let the water animate), then Enter (noise baseline) ...")
        good2 = _snapshot_full()
        input("  3/3  RETRY from Start of Battle; once tiles are DARK, Enter ...")
        bad = _snapshot_full()
    except (EOFError, KeyboardInterrupt):
        print("\naborted."); return

    from collections import Counter
    bands = Counter()
    arena_hits: list[tuple[int, int, int]] = []
    near_hits: list[tuple[int, int, int]] = []
    for base, g1 in good1.items():
        g2 = good2.get(base)
        bb = bad.get(base)
        if g2 is None or bb is None:
            continue
        n = min(len(g1), len(g2), len(bb))
        for i in range(n):
            if g1[i] == g2[i] and g1[i] != bb[i]:   # stable while lit, changed when dark
                a = base + i
                bands[(a >> 16) << 16] += 1
                if RENDER_ARENA[0] <= a < RENDER_ARENA[1]:
                    arena_hits.append((a, g1[i], bb[i]))
                if any(abs(a - t) <= 0x40 for t in _MAP74_TILE01_MIRRORS):
                    near_hits.append((a, g1[i], bb[i]))

    print(f"\n  ARENA candidates (lit-stable, dark-changed) in "
          f"0x{RENDER_ARENA[0]:011x}..0x{RENDER_ARENA[1]:011x}: {len(arena_hits)}")
    for a, gv, bv in arena_hits[:80]:
        print(f"    0x{a:011x}  lit 0x{gv:02x} -> dark 0x{bv:02x}")
    print(f"\n  candidates ADJACENT (<=0x40) to a captured tile-(0,1) mirror: {len(near_hits)}")
    for a, gv, bv in near_hits[:80]:
        print(f"    0x{a:011x}  lit 0x{gv:02x} -> dark 0x{bv:02x}")
    print("\n  all candidates per 64KB band (band: count) -- enable flags cluster, noise scatters:")
    for bd in sorted(bands):
        print(f"    0x{bd:011x}: {bands[bd]}")
    print("\n  -> paste this. An arena or near-mirror byte that is set when lit and cleared when")
    print("     dark (esp. a small {0,1,2}-valued flip) is the render-gate the mod must also hold.")


# ---------------------------------------------------------------------------
# Self-test (OFFLINE -- no game required)
# ---------------------------------------------------------------------------
def _selftest() -> bool:
    ok = True

    # ------------------------------------------------------------------
    # 1. Roster slot-address formula
    # ------------------------------------------------------------------
    # slot 0 -> RosterBase + 0
    # slot 3 -> RosterBase + 3*0x258
    # slot 19 -> RosterBase + 19*0x258
    cases = [
        (0,  ROSTER_BASE),
        (3,  ROSTER_BASE + 3  * ROSTER_STRIDE),
        (19, ROSTER_BASE + 19 * ROSTER_STRIDE),
    ]
    for slot, expected in cases:
        got = _slot_base(slot)
        if got == expected:
            print(f"  slot_base({slot}): 0x{got:011x}  OK")
        else:
            print(f"  slot_base({slot}): FAIL  expected 0x{expected:011x}  got 0x{got:011x}")
            ok = False

    # ------------------------------------------------------------------
    # 2. Sub-field addresses within a slot
    # ------------------------------------------------------------------
    base0 = _slot_base(0)
    name_addr  = base0 + ROSTER_NAME_ID_OFF
    job_addr   = base0 + ROSTER_JOB_OFF
    move_addr  = base0 + ROSTER_MOVE_ABIL_OFF
    name_ok = name_addr == ROSTER_BASE + 0x230
    job_ok  = job_addr  == ROSTER_BASE + 0x02
    move_ok = move_addr == ROSTER_BASE + 0x0C
    for label, passed in (("nameId offset", name_ok), ("job offset", job_ok), ("move offset", move_ok)):
        if passed:
            print(f"  {label}: OK")
        else:
            print(f"  {label}: FAIL")
            ok = False

    # ------------------------------------------------------------------
    # 3. Eligibility rule
    # ------------------------------------------------------------------
    elig_cases = [
        (CHEMIST_JOB_ID,         0x00,                  True,  "Chemist job -> eligible"),
        (0x00,                   TREASURE_HUNTER_ABIL_ID, True,  "TreasureHunter move -> eligible"),
        (CHEMIST_JOB_ID,         TREASURE_HUNTER_ABIL_ID, True,  "both -> eligible"),
        (0x01,                   0x01,                  False, "neither -> not eligible"),
        (None,                   None,                  False, "None/None -> not eligible"),
    ]
    for job, move, expected, label in elig_cases:
        got = _eligible(job, move)
        if got == expected:
            print(f"  eligibility ({label}): OK")
        else:
            print(f"  eligibility ({label}): FAIL  expected {expected}  got {got}")
            ok = False

    # ------------------------------------------------------------------
    # 4. Synthetic roster-match over a fake byte buffer
    #
    # Build a fake roster in a bytearray:
    #   slot 0: nameId=0x0001, job=0x01, move=0x01  (ineligible, wrong nameId)
    #   slot 1: nameId=0x0042, job=0x4B, move=0x00  (Chemist, target)
    #   slot 2: nameId=0x0099, job=0x00, move=0xFD  (TreasureHunter)
    # Simulate _read_slot against this buffer via a local helper.
    # ------------------------------------------------------------------
    FAKE_SLOTS  = 3
    fake_buf    = bytearray(FAKE_SLOTS * ROSTER_STRIDE)
    # slot 0
    struct.pack_into("<H", fake_buf, 0 * ROSTER_STRIDE + ROSTER_NAME_ID_OFF, 0x0001)
    fake_buf[0 * ROSTER_STRIDE + ROSTER_JOB_OFF]        = 0x01
    fake_buf[0 * ROSTER_STRIDE + ROSTER_MOVE_ABIL_OFF]  = 0x01
    # slot 1 (Chemist, nameId=0x0042)
    struct.pack_into("<H", fake_buf, 1 * ROSTER_STRIDE + ROSTER_NAME_ID_OFF, 0x0042)
    fake_buf[1 * ROSTER_STRIDE + ROSTER_JOB_OFF]        = CHEMIST_JOB_ID
    fake_buf[1 * ROSTER_STRIDE + ROSTER_MOVE_ABIL_OFF]  = 0x00
    # slot 2 (TreasureHunter, nameId=0x0099)
    struct.pack_into("<H", fake_buf, 2 * ROSTER_STRIDE + ROSTER_NAME_ID_OFF, 0x0099)
    fake_buf[2 * ROSTER_STRIDE + ROSTER_JOB_OFF]        = 0x00
    fake_buf[2 * ROSTER_STRIDE + ROSTER_MOVE_ABIL_OFF]  = TREASURE_HUNTER_ABIL_ID

    def _fake_read_slot(slot: int):
        off   = slot * ROSTER_STRIDE
        name  = struct.unpack_from("<H", fake_buf, off + ROSTER_NAME_ID_OFF)[0]
        job   = fake_buf[off + ROSTER_JOB_OFF]
        move  = fake_buf[off + ROSTER_MOVE_ABIL_OFF]
        return name, job, move

    def _fake_scan(target_name_id: int):
        for s in range(FAKE_SLOTS):
            n, j, m = _fake_read_slot(s)
            if n == target_name_id:
                return s, j, m
        return None, None, None

    # Search for slot 1 (Chemist)
    slot, job, move = _fake_scan(0x0042)
    if slot == 1 and job == CHEMIST_JOB_ID and move == 0x00:
        print("  fake roster scan (Chemist slot): OK")
    else:
        print(f"  fake roster scan (Chemist slot): FAIL  slot={slot} job={job} move={move}")
        ok = False

    # Chemist slot is eligible
    if _eligible(job, move):
        print("  fake roster eligibility (Chemist): OK")
    else:
        print("  fake roster eligibility (Chemist): FAIL")
        ok = False

    # Search for slot 2 (TreasureHunter)
    slot2, job2, move2 = _fake_scan(0x0099)
    if slot2 == 2 and job2 == 0x00 and move2 == TREASURE_HUNTER_ABIL_ID:
        print("  fake roster scan (TreasureHunter slot): OK")
    else:
        print(f"  fake roster scan (TreasureHunter slot): FAIL  slot={slot2} job={job2} move={move2}")
        ok = False

    if _eligible(job2, move2):
        print("  fake roster eligibility (TreasureHunter): OK")
    else:
        print("  fake roster eligibility (TreasureHunter): FAIL")
        ok = False

    # Search for unknown nameId -> no match
    slot3, job3, move3 = _fake_scan(0xFFFF)
    if slot3 is None:
        print("  fake roster scan (unknown nameId -> no match): OK")
    else:
        print(f"  fake roster scan (unknown nameId -> no match): FAIL  slot={slot3}")
        ok = False

    # ------------------------------------------------------------------
    # 5. Coordinate constants: confirm ACTIVE_UNIT_Y immediately follows X (u16 layout)
    # ------------------------------------------------------------------
    if ACTIVE_UNIT_Y == ACTIVE_UNIT_X + 2:
        print("  ActiveUnitY == ActiveUnitX + 2 (packed u16 pair): OK")
    else:
        print(f"  ActiveUnitY layout: FAIL  X=0x{ACTIVE_UNIT_X:011x} Y=0x{ACTIVE_UNIT_Y:011x}")
        ok = False

    # ------------------------------------------------------------------
    # 6. Adjacent-pair finder (the diag relocate scan) over a synthetic buffer.
    #    Grid X and Y are adjacent bytes; the finder returns the X offset.
    # ------------------------------------------------------------------
    pair_buf = bytearray(40)
    pair_buf[10] = 7    # X
    pair_buf[11] = 9    # Y  -> a (7,9) pair at offset 10
    pair_buf[20] = 7    # lone 7 (no 9 after) -> must NOT match
    pair_buf[21] = 3
    found = _find_adjacent_pairs(bytes(pair_buf), 7, 9)
    if found == [10]:
        print("  adjacent-pair finder (7,9): OK")
    else:
        print(f"  adjacent-pair finder (7,9): FAIL  expected [10]  got {found}")
        ok = False

    # Static-array slot-address formula (player slot 1, enemy slot -1).
    if (STATIC_ARRAY_BASE + 1 * STATIC_STRIDE == STATIC_ARRAY_BASE + 0x200
            and STATIC_ARRAY_BASE + (-1) * STATIC_STRIDE == STATIC_ARRAY_BASE - 0x200
            and STATIC_OFF_Y == STATIC_OFF_X + 1):
        print("  static-array slot/offset layout: OK")
    else:
        print("  static-array slot/offset layout: FAIL")
        ok = False

    # ------------------------------------------------------------------
    # 7. Move-differential: only the pair that tracked (x1,y1)->(x2,y2) at the
    #    SAME address is returned; a coincidental (x1,y1) that does not change
    #    must be rejected.
    # ------------------------------------------------------------------
    BASE = 0x140900000
    # snap1: real unit X/Y (4,4) at +10; a decoy (4,4) at +20 that will NOT move.
    s1 = bytearray(64); s1[10] = 4; s1[11] = 4; s1[20] = 4; s1[21] = 4
    # snap2: the unit moved to (7,2) at +10; the decoy stays (4,4) at +20.
    s2 = bytearray(64); s2[10] = 7; s2[11] = 2; s2[20] = 4; s2[21] = 4
    diff = _diff_position_pairs({BASE: bytes(s1)}, {BASE: bytes(s2)}, 4, 4, 7, 2)
    if diff == [BASE + 10]:
        print("  move-differential (tracks mover, rejects decoy): OK")
    else:
        print(f"  move-differential: FAIL  expected [0x{BASE + 10:x}]  got {[hex(d) for d in diff]}")
        ok = False

    # ------------------------------------------------------------------
    # 8. Increment finder (claimdiff): only a byte that went X -> X+1 is reported;
    #    a +2 jump and a 0xFF (no wrap) are rejected.
    # ------------------------------------------------------------------
    i1 = bytes([5, 9,  0xFF, 0])
    i2 = bytes([6, 11, 0x00, 0])   # +1 at idx0; +2 at idx1; 0xFF->0x00 wrap at idx2
    incs = _find_increments({BASE: i1}, {BASE: i2})
    if incs == [(BASE + 0, 5, 6)]:
        print("  increment finder (+1 only, rejects +2 and 0xFF-wrap): OK")
    else:
        print(f"  increment finder: FAIL  got {[(hex(a), b, c) for a, b, c in incs]}")
        ok = False

    # ------------------------------------------------------------------
    # 9. +/-1 finder (invtrack): accepts +1 and -1 with both values <=99; rejects
    #    big values and +/-2.
    # ------------------------------------------------------------------
    p1 = bytes([3, 50, 200, 10])
    p2 = bytes([2, 51, 201, 12])   # -1 ok; +1 ok; 200->201 rejected (>99); +2 rejected
    pm1 = _find_pm1({BASE: p1}, {BASE: p2})
    if pm1 == [(BASE + 0, 3, 2), (BASE + 1, 50, 51)]:
        print("  +/-1 finder (count ticks, rejects >99 and +/-2): OK")
    else:
        print(f"  +/-1 finder: FAIL  got {[(hex(a), b, c) for a, b, c in pm1]}")
        ok = False

    # ------------------------------------------------------------------
    # 10. persistent stable-flip finder: keeps a real flip, skips missing regions + the UI arena.
    # ------------------------------------------------------------------
    _sf_base = 0x140900000
    _sf_before = {_sf_base: bytes([0x00, 0x05, 0x00, 0x10]), 0x141000000: bytes([0x00])}
    _sf_after  = {_sf_base: bytes([0x01, 0x05, 0x00, 0x10])}   # offset 0 flips; region 0x14100.. absent in 'after'
    _sf_got = _find_stable_flips(_sf_before, _sf_after)
    if _sf_got == [(_sf_base, 0x00, 0x01)]:
        print("  stable-flip finder: OK")
    else:
        print("  stable-flip finder: FAIL  got " + str(_sf_got)); ok = False
    _sf_ui = 0x140C63000   # inside UI_ARENA -- must be rejected
    if _find_stable_flips({_sf_ui: bytes([0x00])}, {_sf_ui: bytes([0x01])}) == []:
        print("  stable-flip UI-arena reject: OK")
    else:
        print("  stable-flip UI-arena reject: FAIL"); ok = False

    return ok


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------
def main() -> None:
    args = sys.argv[1:]

    if not args or args[0] in ("-h", "--help", "help"):
        print(__doc__)
        return

    if args[0] == "--selftest":
        print("Running self-test (no game required) ...")
        passed = _selftest()
        if passed:
            print("\nAll self-tests PASSED.")
            sys.exit(0)
        else:
            print("\nSelf-test FAILED.")
            sys.exit(1)

    if args[0] == "peek":
        cmd_peek()
        return

    if args[0] == "watch":
        cmd_watch()
        return

    if args[0] == "roster":
        cmd_roster()
        return

    if args[0] == "scan":
        if len(args) < 2:
            print("Usage: scan <expectedHex>")
            sys.exit(2)
        cmd_scan(args[1])
        return

    if args[0] == "diag":
        cmd_diag()
        return

    if args[0] == "finddiff":
        cmd_finddiff()
        return

    if args[0] == "record":
        if len(args) < 2:
            print("Usage: record <Xaddr_hex> [span_hex]")
            sys.exit(2)
        cmd_record(args[1], args[2] if len(args) > 2 else "0x80")
        return

    if args[0] == "array":
        if len(args) < 2:
            print("Usage: array <Xaddr_hex> [count_hex]")
            sys.exit(2)
        cmd_array(args[1], args[2] if len(args) > 2 else "0x20")
        return

    if args[0] == "claimdiff":
        cmd_claimdiff()
        return

    if args[0] == "claimisolate":
        cmd_claimisolate()
        return

    if args[0] == "invscan":
        cmd_invscan()
        return

    if args[0] == "invtrack":
        cmd_invtrack()
        return

    if args[0] == "rendergate":
        cmd_rendergate()
        return

    if args[0] == "tilescan":
        if len(args) < 2:
            print("Usage: tilescan <mapId>")
            sys.exit(2)
        cmd_tilescan(args[1])
        return

    if args[0] == "collectsnap":
        if len(args) < 2:
            print("usage: collectsnap <tag> [loHex hiHex]"); sys.exit(2)
        lo = int(args[2], 16) if len(args) > 3 else None
        hi = int(args[3], 16) if len(args) > 3 else None
        cmd_collectsnap(args[1], lo, hi); sys.exit(0)
    if args[0] == "collectdiff":
        if len(args) < 3:
            print("usage: collectdiff <baselineTag> <wonATag> [<wonBTag>]"); sys.exit(2)
        cmd_collectdiff(args[1], args[2], args[3] if len(args) > 3 else None); sys.exit(0)

    print(f"Unknown verb: {args[0]!r}")
    print(__doc__)
    sys.exit(2)


if __name__ == "__main__":
    main()
