"""Probe for the visible Treasure Hunter tag (docs/HUNTER_CUE_AND_LABELS_PLAN.md section 1).

Dev only, run under a game lock with Patrick at the PC. Read only unless --write is given.

  python tools/probes/hunter_tag_probe.py jobrow 76            the row's four innate ability ids
  python tools/probes/hunter_tag_probe.py grant 76 --write     put 509 in that row's first free innate slot
  python tools/probes/hunter_tag_probe.py units                every live unit: side, job, innates, hunter bit
  python tools/probes/hunter_tag_probe.py grantall --write     509 into the 30 rows the 1.5.0 grant covered
  python tools/probes/hunter_tag_probe.py traps 82             a map's treasure rows, main table and battle copy
  python tools/probes/hunter_tag_probe.py --selftest           offline checks on a fake memory

Addresses are the 1.5.2 no DRM build's (FFTGameFacts job-table-row-layout and
unit-record-innate-ability-ids, both UNCERTAIN), rebased by the delta in Treasure Master's
"Game image base" log line.
"""
import ctypes
import os
import struct
import subprocess
import sys

JOB_TABLE, JOB_ROW, JOB_ROWS = 0x140785E30, 0x31, 176
UNIT_X, X_FIELD, STRIDE, SLOTS, SEAT0_SLOT = 0x141851F2F, 0x4F, 0x200, 41, 15
HUNTER = 509
TRAPS, TRAP_COPY = 0x140783840, 0x140D8DC90
GRANTED = [1, 2, 3, 13, 22, 25, 26, 30, 50, 72] + list(range(74, 94))
INNATE_AT = (1, 3, 5, 7)


def innates(row):
    return [struct.unpack_from("<H", row, o)[0] for o in INNATE_AT]


def free_slot(row):
    """The offset to write 509 at, or None when the row already has it or has no free slot."""
    ids = innates(row)
    if HUNTER in ids:
        return None
    for o, v in zip(INNATE_AT, ids):
        if v == 0:
            return o
    return None


def looks_like_job_row(row):
    """Move at +0x18 and Jump at +0x19 sit in 1 to 8 on every real job row."""
    return 1 <= row[0x18] <= 8 and 1 <= row[0x19] <= 8


def trap_rows(b):
    return ["(%d,%d) flags %02X rare %d common %d" % (b[i] >> 4, b[i] & 15, b[i + 1],
            struct.unpack_from("<H", b, i + 2)[0], struct.unpack_from("<H", b, i + 4)[0]) for i in range(0, 24, 6)]


def describe_unit(r):
    team = "enemy" if r[0x05] & 0x30 else ("guest" if (r[0x06] & 0x09) or (r[0x05] & 0x04) else "party")
    ids = [struct.unpack_from("<H", r, o)[0] for o in (0x0A, 0x0C, 0x0E, 0x10)]
    moved = struct.unpack_from("<H", r, 0x18)[0]
    return "%s job %d innates %s equipped move %d hunter bit %d" % (team, r[0x03], ids, moved, r[0x9E] & 1)


class Live:
    def __init__(self):
        out = subprocess.check_output('tasklist /FI "IMAGENAME eq FFT_enhanced.exe" /FO CSV /NH',
                                      shell=True, text=True)
        rows = [x for x in out.strip().splitlines() if x.startswith('"')]
        if not rows:
            raise SystemExit("no game running")
        self.k = ctypes.WinDLL("kernel32", use_last_error=True)
        self.k.OpenProcess.restype = ctypes.c_void_p
        self.h = self.k.OpenProcess(0x0010 | 0x0020 | 0x0008 | 0x0400, False, int(rows[0].split(",")[1].strip('"')))
        self.delta = log_delta()

    def read(self, addr, n):
        buf, got = (ctypes.c_ubyte * n)(), ctypes.c_size_t()
        if not self.k.ReadProcessMemory(ctypes.c_void_p(self.h), ctypes.c_void_p(addr + self.delta), buf, n,
                                        ctypes.byref(got)):
            raise SystemExit("unreadable at 0x%X" % addr)
        return bytes(buf)

    def write(self, addr, data):
        got = ctypes.c_size_t()
        buf = (ctypes.c_ubyte * len(data)).from_buffer_copy(data)
        if not self.k.WriteProcessMemory(ctypes.c_void_p(self.h), ctypes.c_void_p(addr + self.delta), buf,
                                         len(data), ctypes.byref(got)):
            raise SystemExit("write refused at 0x%X" % addr)


def log_delta():
    log = os.path.join(os.environ["RELOADEDIIMODS"], "prawl.fft.treasuremaster", "treasuremaster.log")
    for line in reversed(open(log, encoding="utf-8", errors="replace").readlines()):
        if "Game image base" in line and "delta 0x" in line:
            return int(line.split("delta 0x")[1].split(")")[0], 16)
    raise SystemExit("no image base line in the Treasure Master log")


def job_row(mem, job):
    if not 0 <= job < JOB_ROWS:
        raise SystemExit("job id out of range")
    return mem.read(JOB_TABLE + job * JOB_ROW, JOB_ROW)


def run(mem, argv, out=print):
    cmd = argv[0]
    if cmd == "jobrow":
        row = job_row(mem, int(argv[1]))
        out("job %s innates %s row %s" % (argv[1], innates(row), row.hex()))
    elif cmd == "grantall":
        for job in GRANTED:
            run(mem, ["grant", str(job)] + argv[1:], out)
    elif cmd == "traps":
        m = int(argv[1])
        out("map %d main %s" % (m, trap_rows(mem.read(TRAPS + m * 24, 24))))
        out("battle copy %s" % trap_rows(mem.read(TRAP_COPY, 24)))
    elif cmd == "grant":
        job = int(argv[1])
        row = job_row(mem, job)
        if not looks_like_job_row(row):
            out("job %d: REFUSED, Move %d Jump %d do not look like a job row" % (job, row[0x18], row[0x19]))
            return
        slot = free_slot(row)
        if slot is None:
            out("job %d: no write, innates %s (509 present or no free slot)" % (job, innates(row)))
            return
        if "--write" not in argv:
            out("job %d: would write 509 at +%d, innates %s (add --write)" % (job, slot, innates(row)))
            return
        mem.write(JOB_TABLE + job * JOB_ROW + slot, struct.pack("<H", HUNTER))
        out("job %d: wrote 509 at +%d, innates now %s" % (job, slot, innates(job_row(mem, job))))
    elif cmd == "units":
        for slot in range(SLOTS):
            r = mem.read(UNIT_X - X_FIELD + slot * STRIDE, STRIDE)
            if r[0x01] != 0xFF and r[0x1B5] == 1:
                out("seat %d %s" % (slot - SEAT0_SLOT, describe_unit(r)))
    else:
        raise SystemExit(__doc__)


class Fake:
    def __init__(self):
        self.mem = {}

    def read(self, addr, n):
        return bytes(self.mem.get(addr + i, 0) for i in range(n))

    def write(self, addr, data):
        for i, b in enumerate(data):
            self.mem[addr + i] = b


def selftest():
    f, said = Fake(), []
    run(f, ["grant", "75", "--write"], said.append)
    assert "REFUSED" in said[-1] and innates(job_row(f, 75)) == [0, 0, 0, 0], said
    for j in GRANTED + [77]:
        f.write(JOB_TABLE + j * JOB_ROW + 0x18, b"")
    row76 = JOB_TABLE + 76 * JOB_ROW
    f.write(row76 + 1, struct.pack("<HH", 400, 401))
    run(f, ["grant", "76"], said.append)
    assert "would write 509 at +5" in said[-1], said
    assert innates(job_row(f, 76)) == [400, 401, 0, 0]
    run(f, ["grant", "76", "--write"], said.append)
    assert innates(job_row(f, 76)) == [400, 401, 509, 0], innates(job_row(f, 76))
    run(f, ["grant", "76", "--write"], said.append)
    assert "no write" in said[-1] and innates(job_row(f, 76)) == [400, 401, 509, 0]
    f.write(JOB_TABLE + 77 * JOB_ROW + 1, struct.pack("<HHHH", 1, 2, 3, 4))
    run(f, ["grant", "77", "--write"], said.append)
    assert "no write" in said[-1] and innates(job_row(f, 77)) == [1, 2, 3, 4]
    enemy = UNIT_X - X_FIELD + (SEAT0_SLOT + 2) * STRIDE
    f.write(enemy + 0x01, b"\x02"); f.write(enemy + 0x1B5, b"\x01"); f.write(enemy + 0x05, b"\x10")
    f.write(enemy + 0x03, bytes([76])); f.write(enemy + 0x0E, struct.pack("<H", HUNTER)); f.write(enemy + 0x9E, b"\x01")
    empty = UNIT_X - X_FIELD + (SEAT0_SLOT + 3) * STRIDE
    f.write(empty + 0x01, b"\xFF"); f.write(empty + 0x1B5, b"\x01")
    said.clear()
    run(f, ["units"], said.append)
    assert said == ["seat 2 enemy job 76 innates [0, 0, 509, 0] equipped move 0 hunter bit 1"], said
    said.clear()
    run(f, ["grantall"], said.append)
    assert len(said) == 30 and all("would write" in s or "no write" in s for s in said), said
    assert innates(job_row(f, 74)) == [0, 0, 0, 0]
    f.write(TRAPS + 82 * 24, bytes([0x37, 0x10]) + struct.pack("<HH", 124, 124))
    f.write(TRAP_COPY, bytes([0x37, 0x10]) + struct.pack("<HH", 124, 241))
    said.clear()
    run(f, ["traps", "82"], said.append)
    assert said[0].startswith("map 82 main ['(3,7) flags 10 rare 124 common 124'"), said
    assert said[1].startswith("battle copy ['(3,7) flags 10 rare 124 common 241'"), said
    print("selftest: 10 checks pass")


if __name__ == "__main__":
    if sys.argv[1:] == ["--selftest"]:
        selftest()
    else:
        run(Live(), sys.argv[1:] or ["help"])
