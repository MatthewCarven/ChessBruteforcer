# Replay the reads of a bench-threads --trace run through block caches, offline: the cache the
# tables had (each table direct-mapped, 1,024 slots of 64 KB), and others.  One-thread searches
# only (a multi-thread search's reads are traced thread by thread, out of their real order).
# Every cache is emptied before each search, as the bench empties the real one.
#   python -I block-cache-trace.py <trace.bin> [<bench.tsv>]
# The .tsv (the same run's --tsv) checks the replay: the current cache replayed must decompress
# exactly the blocks the real one did (for a trace of the per-table cache, before commit a5b38fa).
# "Settled DTZ" reads (bit 58) are marked only in traces from commit 04cd475: since a5b38fa they
# aren't read at all, so in a newer trace the two columns agree.
import sys, csv
from collections import OrderedDict, defaultdict
import numpy as np

trace_path = sys.argv[1]
tsv_path = sys.argv[2] if len(sys.argv) > 2 else None

tables = {}
with open(trace_path + ".tables.tsv", newline="") as f:
    for r in csv.DictReader(f, delimiter="\t"):
        tables[int(r["id"])] = (r["material"], r["dtz"] == "1", int(r["size"]), int(r["values_per_block"]))

raw = np.fromfile(trace_path, dtype=np.int64)
marks = np.flatnonzero(raw < 0)
searches = []   # (round, position, threads, reads)
for i, at in enumerate(marks):
    m = int(raw[at]) & 0x7FFFFFFFFFFFFFFF
    end = marks[i + 1] if i + 1 < len(marks) else len(raw)
    searches.append(((m >> 32) & 0x7FFFFFFF, (m >> 16) & 0xFFFF, m & 0xFFFF, raw[at + 1:end]))
one = [s for s in searches if s[2] == 1]
print(f"{len(raw) - len(marks):,} reads in {len(searches)} searches; {len(one)} on one thread, replayed below.")

INDEX = (1 << 40) - 1
def fields(reads):
    return reads & INDEX, (reads >> 40) & 0xFFF, ((reads >> 58) & 1).astype(bool)

def blocks_of(index, table, scale=1):
    """Block numbers at 64 KB / scale a block: (table << 32) | block."""
    vpb = np.array([tables[t][3] // scale for t in range(max(tables) + 1)], dtype=np.int64)
    return (table.astype(np.int64) << 32) | (index // vpb[table])

def direct_mapped(keys, slots=1024):
    """Each table its own cache, block b in slot b % slots.  Returns misses."""
    held = {}
    misses = 0
    for k in keys.tolist():
        place = (k >> 32, (k & 0xFFFFFFFF) % slots)
        if held.get(place) != k:
            held[place] = k
            misses += 1
    return misses

def per_table_lru(keys, slots=1024):
    """Each table its own fully associative LRU cache of `slots` blocks."""
    caches = defaultdict(OrderedDict)
    misses = 0
    for k in keys.tolist():
        c = caches[k >> 32]
        if k in c:
            c.move_to_end(k)
        else:
            misses += 1
            c[k] = None
            if len(c) > slots:
                c.popitem(last=False)
    return misses

def stack_distances(keys):
    """For every read, the distinct blocks read since its block was last read (-1: first read)."""
    n = len(keys)
    tree = [0] * (n + 1)
    last = {}
    out = np.empty(n, dtype=np.int64)
    marked = 0
    for t, k in enumerate(keys.tolist()):
        p = last.get(k)
        if p is None:
            out[t] = -1
        else:
            # marked positions after p: total marked - prefix(p)
            s, i = 0, p + 1
            while i > 0:
                s += tree[i]; i -= i & -i
            out[t] = marked - s
            i = p + 1
            while i <= n:
                tree[i] -= 1; i += i & -i
            marked -= 1
        i = t + 1
        while i <= n:
            tree[i] += 1; i += i & -i
        marked += 1
        last[k] = t
    return out

def lru_misses(dist, sizes):
    cold = int((dist < 0).sum())
    return {s: cold + int((dist >= s).sum()) for s in sizes}

def set_assoc_clock(keys, blocks, ways=16):
    """One cache for every table: `blocks` slots in sets of `ways`, a block's set from a hash of
    (table, block), the victim chosen by a clock hand within the set (a used bit set on a hit)."""
    sets = blocks // ways
    bits = sets.bit_length() - 1
    key_of = [[-1] * ways for _ in range(sets)]
    used = [[False] * ways for _ in range(sets)]
    hand = [0] * sets
    misses = 0
    for k in keys.tolist():
        s = ((k * 0x9E3779B97F4A7C15) & 0xFFFFFFFFFFFFFFFF) >> (64 - bits) if bits else 0
        row = key_of[s]
        try:
            w = row.index(k)
            used[s][w] = True
            continue
        except ValueError:
            pass
        misses += 1
        u = used[s]
        h = hand[s]
        while u[h]:
            u[h] = False
            h = (h + 1) % ways
        row[h] = k
        u[h] = True   # (a block just read counts as used: it is about to be read again)
        hand[s] = (h + 1) % ways
    return misses

# --- The replay ------------------------------------------------------------------------------
measured = {}
if tsv_path:
    with open(tsv_path, newline="") as f:
        for r in csv.DictReader(f, delimiter="\t"):
            if r["arm"] == "1":
                measured[(int(r["round"]), int(r["position"]))] = int(r["blocks"])

SIZES = [256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536]   # 64 KB blocks: 16 MB .. 4 GB
SCALES = [1, 2, 4, 8, 16]                                          # 64, 32, 16, 8, 4 KB blocks
tot = defaultdict(int)
lru = defaultdict(int)          # (scale, skip dtz, size) -> misses
by_table = defaultdict(lambda: defaultdict(int))
check = []
for rnd, pos, _, reads in one:
    index, table, settled = fields(reads)
    dtz = np.array([tables[t][1] for t in range(max(tables) + 1)])[table]
    keys = blocks_of(index, table)
    tot["reads"] += len(keys)
    tot["dtz reads"] += int(dtz.sum())
    tot["dtz settled"] += int(settled.sum())
    dm = direct_mapped(keys)
    tot["direct-mapped misses"] += dm
    if (rnd, pos) in measured:
        check.append((measured[(rnd, pos)], dm))
    cold = len(set(keys.tolist()))
    tot["cold"] += cold
    tot["per-table LRU misses"] += per_table_lru(keys)
    kept = keys[~settled]
    tot["direct-mapped misses, settled DTZ skipped"] += direct_mapped(kept)
    tot["cold, settled DTZ skipped"] += len(set(kept.tolist()))
    tot["dtm cold"] += len(set(keys[~dtz].tolist()))
    for scale in SCALES:
        for skip in (False, True):
            k2 = blocks_of(index[~settled], table[~settled], scale) if skip else blocks_of(index, table, scale)
            for s, m in lru_misses(stack_distances(k2), [x * scale for x in SIZES]).items():
                lru[(scale, skip, s // scale)] += m
    for skip in (False, True):
        k2 = kept if skip else keys
        for size in (4096, 8192, 16384):
            tot[("set16", skip, size)] += set_assoc_clock(k2, size, 16)
            tot[("set8", skip, size)] += set_assoc_clock(k2, size, 8)
    # by table: reads, cold blocks, misses in the old cache
    for t in np.unique(table).tolist():
        sel = table == t
        kt = keys[sel]
        by_table[t]["reads"] += int(sel.sum())
        by_table[t]["settled"] += int(settled[sel].sum())
        by_table[t]["cold"] += len(set(kt.tolist()))
        by_table[t]["misses"] += direct_mapped(kt)

n = len(one)
if check:
    same = sum(a == b for a, b in check)
    print(f"The replay against the bench: {same} of {len(check)} searches decompressed exactly the blocks replayed "
          f"({sum(a for a, _ in check):,} measured, {sum(b for _, b in check):,} replayed).")
print()
print(f"Per search (mean of {n}):")
print("| | per search |")
print("|---|---:|")
for k in ["reads", "dtz reads", "dtz settled", "direct-mapped misses", "cold", "per-table LRU misses",
          "direct-mapped misses, settled DTZ skipped", "cold, settled DTZ skipped", "dtm cold"]:
    print(f"| {k} | {tot[k] / n:,.0f} |")
dm, cold, fa = tot["direct-mapped misses"], tot["cold"], tot["per-table LRU misses"]
print()
print(f"The old cache's misses: cold {cold / dm:.0%}, capacity {(fa - cold) / dm:.0%}, "
      f"conflict (two blocks sharing a slot) {(dm - fa) / dm:.0%}.")

print()
print("One cache for every table, least recently used out, 64 KB blocks; misses a search:")
print("| cache | MB | all reads | settled DTZ skipped |")
print("|---|---:|---:|---:|")
print(f"| each table its own, direct-mapped, 1,024 | up to 14,080 | {dm / n:,.0f} | "
      f"{tot['direct-mapped misses, settled DTZ skipped'] / n:,.0f} |")
for s in SIZES:
    print(f"| shared LRU {s:,} | {s * 64 // 1024:,} | {lru[(1, False, s)] / n:,.0f} | {lru[(1, True, s)] / n:,.0f} |")
for kind in ("set16", "set8"):
    for size in (4096, 8192, 16384):
        print(f"| shared {kind[3:]}-way, clock, {size:,} | {size * 64 // 1024:,} | {tot[(kind, False, size)] / n:,.0f} | "
              f"{tot[(kind, True, size)] / n:,.0f} |")
print(f"| (cold: every block read once) | | {cold / n:,.0f} | {tot['cold, settled DTZ skipped'] / n:,.0f} |")

print()
print("Smaller blocks, the same memory, settled DTZ skipped, shared LRU; misses a search:")
print("| MB | " + " | ".join(f"{64 // s} KB" for s in SCALES) + " |")
print("|---:|" + "---:|" * len(SCALES))
for s in SIZES:
    print(f"| {s * 64 // 1024:,} | " + " | ".join(f"{lru[(scale, True, s)] / n:,.0f}" for scale in SCALES) + " |")

print()
print("By table (the 20 with the most misses in the old cache), a search:")
print("| table | kind | reads | settled DTZ | blocks read (cold) | misses, old cache | share of misses |")
print("|---|---|---:|---:|---:|---:|---:|")
for t, c in sorted(by_table.items(), key=lambda kv: -kv[1]["misses"])[:20]:
    mat, isdtz, _, _ = tables[t]
    print(f"| {mat} | {'DTZ' if isdtz else 'DTM'} | {c['reads'] / n:,.0f} | {c['settled'] / n:,.0f} | {c['cold'] / n:,.0f} | "
          f"{c['misses'] / n:,.0f} | {c['misses'] / dm:.1%} |")
dtz_misses = sum(c["misses"] for t, c in by_table.items() if tables[t][1])
print(f"\nDTZ tables' share of the old cache's misses: {dtz_misses / dm:.1%}; tables read at all: {len(by_table)}.")
