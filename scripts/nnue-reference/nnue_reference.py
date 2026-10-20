"""A numpy reference for the engine's network (NNUE.md, "What the C# engine needs").

Written from NNUE.md alone, not from the C# code, so the two check each other: the .nnue file, the
feature index and the forward pass. numpy and the standard library only. The trainer (session 2) can
take write_nnue and feature from here; the format and the index are the contract.

Run as a script, it writes the C# tests' fixtures: a net with random weights and 1,000 positions with
the scores this code gives them (tests/ChessBruteforcer.Tests/Data/nnue-random-256.*), and two deeper nets
the same way (nnue-random-deep-32.*, nnue-random-deep-16-32.*). The C# tests MatchesTheNumpyReference and
MatchesTheNumpyReferenceDeep must give every one of them exactly.

    python scripts/nnue-reference/nnue_reference.py

Version 2 (2026-10-21, the deeper nets: NNUE.md, session 6) keeps the feature layer and adds small layers
after it. Little-endian throughout:

    8 bytes   "CBNNUE01"
    u32       version (2), inputs (768), hidden H
    i32       QA (255), scale (400)
    u32       L, the number of small layers (1-4), then their sizes (1-256 each)
    int16     feature weights [768][H], feature biases [H]          (as version 1)
    per layer: i32 Q, int16 weights [size][inputs] (one output's row of inputs contiguous), i32 biases [size]
    the output: i32 Q, int16 weights [the last layer's size], i32 bias
    32 bytes  SHA-256 of everything above

The first layer's inputs are x = (v x v + QA / 2) / QA for each of the 2H clamped accumulator numbers v (the
side to move's H first): SCReLU at the scale QA, rounded. Each layer: s = bias + sum of x x w (int32: the loader
checks QA x sum|w| + |bias| + Q / 2 per output), then x = clamp((s + Q / 2) / Q, 0, QA), Q a power of two. The
score: (bias + sum of x x w) x scale / (QA x Q), clamped as version 1's. Every division truncates towards zero
(C#'s); the + half steps round the layers' numbers to the nearest, which truncation alone would pull low by half
a step each, a few centipawns against the side to move in all.
"""

import hashlib
import pathlib
import struct
import sys

import numpy as np

MAGIC = b"CBNNUE01"
VERSION = 1
VERSION_DEEP = 2
INPUTS = 768
HEADER = struct.Struct("<IIIiii")   # version, inputs, hidden, QA, QB, scale (after the magic)
HEADER_DEEP = struct.Struct("<IIIiiI")   # version 2: version, inputs, hidden, QA, scale, layer count
MAX_LAYERS, MAX_LAYER_SIZE = 4, 256
MAX_SCORE = 28_999                  # Search.MateBound - 1: no network score may read as a mate
KINDS = "pnbrqk"                    # kind 1..6, as the engine's PieceType


def feature(colour, kind, square, perspective):
    """The input for a piece (colour 0 white / 1 black, kind 1 pawn .. 6 king) on a square (a1 = 0),
    as one perspective (0 white, 1 black) sees it: its own pieces first, black's board flipped."""
    return ((0 if colour == perspective else 6) + kind - 1) * 64 + (square if perspective == 0 else square ^ 56)


def write_nnue(path, fw, fb, ow, ob, qa=255, qb=64, scale=400):
    """fw [768][H], fb [H], ow [2H] (the side to move's half first) as int16; ob an int (i32)."""
    hidden = fb.shape[0]
    assert fw.shape == (INPUTS, hidden) and ow.shape == (2 * hidden,)
    body = (MAGIC + HEADER.pack(VERSION, INPUTS, hidden, qa, qb, scale)
            + fw.astype("<i2").tobytes() + fb.astype("<i2").tobytes() + ow.astype("<i2").tobytes()
            + struct.pack("<i", ob))
    pathlib.Path(path).write_bytes(body + hashlib.sha256(body).digest())


def write_nnue_deep(path, fw, fb, layers, final, qa=255, scale=400):
    """Version 2. fw [768][H], fb [H] int16; layers a list of (Q, weights [size][inputs] int16, biases [size]
    int32); final (Q, weights [last size] int16, bias int)."""
    hidden = fb.shape[0]
    assert fw.shape == (INPUTS, hidden) and 1 <= len(layers) <= MAX_LAYERS
    body = MAGIC + HEADER_DEEP.pack(VERSION_DEEP, INPUTS, hidden, qa, scale, len(layers))
    body += struct.pack(f"<{len(layers)}I", *(w.shape[0] for _, w, _ in layers))
    body += fw.astype("<i2").tobytes() + fb.astype("<i2").tobytes()
    inputs = 2 * hidden
    for q, w, b in layers:
        assert w.shape == (b.shape[0], inputs), (w.shape, b.shape, inputs)
        body += struct.pack("<i", q) + w.astype("<i2").tobytes() + b.astype("<i4").tobytes()
        inputs = w.shape[0]
    q, w, b = final
    assert w.shape == (inputs,)
    body += struct.pack("<i", q) + w.astype("<i2").tobytes() + struct.pack("<i", int(b))
    pathlib.Path(path).write_bytes(body + hashlib.sha256(body).digest())


def read_nnue(path):
    data = pathlib.Path(path).read_bytes()
    if data[:8] != MAGIC:
        raise ValueError(f"{path}: no CBNNUE01 at the start")
    if hashlib.sha256(data[:-32]).digest() != data[-32:]:
        raise ValueError(f"{path}: the SHA-256 doesn't match")
    (version,) = struct.unpack_from("<I", data, 8)
    if version == VERSION_DEEP:
        return read_deep(path, data)
    version, inputs, hidden, qa, qb, scale = HEADER.unpack_from(data, 8)
    if version != VERSION or inputs != INPUTS:
        raise ValueError(f"{path}: version {version}, {inputs} inputs")
    at = 8 + HEADER.size
    fw = np.frombuffer(data, "<i2", INPUTS * hidden, at).reshape(INPUTS, hidden)
    at += 2 * INPUTS * hidden
    fb = np.frombuffer(data, "<i2", hidden, at)
    at += 2 * hidden
    ow = np.frombuffer(data, "<i2", 2 * hidden, at)
    at += 4 * hidden
    (ob,) = struct.unpack_from("<i", data, at)
    if at + 4 + 32 != len(data):
        raise ValueError(f"{path}: {len(data)} bytes, expected {at + 4 + 32}")
    return dict(hidden=hidden, qa=qa, qb=qb, scale=scale, fw=fw, fb=fb, ow=ow, ob=ob, layers=None)


def read_deep(path, data):
    version, inputs, hidden, qa, scale, count = HEADER_DEEP.unpack_from(data, 8)
    if inputs != INPUTS or not 1 <= count <= MAX_LAYERS:
        raise ValueError(f"{path}: {inputs} inputs, {count} layers")
    at = 8 + HEADER_DEEP.size
    sizes = struct.unpack_from(f"<{count}I", data, at)
    at += 4 * count
    fw = np.frombuffer(data, "<i2", INPUTS * hidden, at).reshape(INPUTS, hidden)
    at += 2 * INPUTS * hidden
    fb = np.frombuffer(data, "<i2", hidden, at)
    at += 2 * hidden
    layers, n_in = [], 2 * hidden
    for size in sizes:
        (q,) = struct.unpack_from("<i", data, at)
        w = np.frombuffer(data, "<i2", size * n_in, at + 4).reshape(size, n_in)
        at += 4 + 2 * size * n_in
        b = np.frombuffer(data, "<i4", size, at)
        at += 4 * size
        layers.append((q, w, b))
        n_in = size
    (q,) = struct.unpack_from("<i", data, at)
    w = np.frombuffer(data, "<i2", n_in, at + 4)
    at += 4 + 2 * n_in
    (b,) = struct.unpack_from("<i", data, at)
    at += 4
    if at + 32 != len(data):
        raise ValueError(f"{path}: {len(data)} bytes, expected {at + 32}")
    return dict(hidden=hidden, qa=qa, scale=scale, fw=fw, fb=fb, layers=layers, final=(q, w, b))


def parse_fen(fen):
    """The pieces as (colour, kind, square) and the side to move (0 white, 1 black)."""
    fields = fen.split()
    pieces = []
    for row, text in enumerate(fields[0].split("/")):
        rank, file = 7 - row, 0
        for c in text:
            if c.isdigit():
                file += int(c)
            else:
                pieces.append((0 if c.isupper() else 1, KINDS.index(c.lower()) + 1, rank * 8 + file))
                file += 1
    return pieces, 0 if fields[1] == "w" else 1


def accumulator(net, pieces, perspective):
    """The bias plus one weight column per piece, wrapped to int16 as the engine's adds wrap."""
    columns = [feature(c, k, s, perspective) for c, k, s in pieces]
    total = net["fb"].astype(np.int64) + net["fw"][columns].astype(np.int64).sum(axis=0)
    return (total + 32768) % 65536 - 32768


def truncating_div(a, b):
    """Integer division towards zero, as C#'s (Python's // floors)."""
    q = abs(a) // abs(b)
    return q if (a < 0) == (b < 0) else -q


def truncating_div_array(a, b):
    """truncating_div for an int64 array and a positive int."""
    return np.where(a < 0, -((-a) // b), a // b)


def evaluate(net, fen):
    """The score for the side to move, in centipawns."""
    pieces, side = parse_fen(fen)
    h, qa = net["hidden"], net["qa"]
    us = np.clip(accumulator(net, pieces, side), 0, qa)
    them = np.clip(accumulator(net, pieces, 1 - side), 0, qa)
    if net.get("layers"):
        x = np.concatenate((us, them)).astype(np.int64)
        x = (x * x + qa // 2) // qa                       # SCReLU at the scale QA, rounded (>= 0: // truncates)
        for q, w, b in net["layers"]:
            s = w.astype(np.int64) @ x + b.astype(np.int64)
            x = np.clip(truncating_div_array(s + q // 2, q), 0, qa)
        q, w, b = net["final"]
        total = int(w.astype(np.int64) @ x) + int(b)
        score = truncating_div(total * net["scale"], qa * q)
        return max(-MAX_SCORE, min(MAX_SCORE, score))
    ow = net["ow"].astype(np.int64)
    total = int((us * us * ow[:h]).sum() + (them * them * ow[h:]).sum())
    score = truncating_div((truncating_div(total, qa) + net["ob"]) * net["scale"], qa * net["qb"])
    return max(-MAX_SCORE, min(MAX_SCORE, score))


# ---- the C# tests' fixture ----

KNOWN = [
    "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
    "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
    "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
    "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",
    "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
    "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
    "r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
    "rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6 0 3",
    "8/8/8/8/8/8/8/K6k w - - 0 1",
    "8/8/8/8/8/8/8/K6k b - - 0 1",
]


def random_fen(rng):
    """Kings plus 0-30 pieces from both sides' sets (now and then a promoted extra), pawns on ranks 2-7."""
    pool = [(colour, kind) for colour in (0, 1) for kind, count in ((1, 8), (2, 2), (3, 2), (4, 2), (5, 1))
            for _ in range(count)]
    chosen = [pool[i] for i in rng.choice(len(pool), int(rng.integers(0, 31)), replace=False)]
    if rng.random() < 0.1:
        chosen.append((int(rng.integers(0, 2)), int(rng.choice([2, 5]))))
    board = {}
    free = list(rng.permutation(64))
    for colour, kind in [(0, 6), (1, 6)] + chosen:
        square = next(s for s in free if kind != 1 or 8 <= s < 56)
        free.remove(square)
        board[int(square)] = (colour, kind)
    rows = []
    for rank in range(7, -1, -1):
        row, empty = "", 0
        for file in range(8):
            piece = board.get(rank * 8 + file)
            if piece is None:
                empty += 1
                continue
            if empty:
                row += str(empty)
                empty = 0
            letter = KINDS[piece[1] - 1]
            row += letter.upper() if piece[0] == 0 else letter
        rows.append(row + (str(empty) if empty else ""))
    return "/".join(rows) + (" w" if rng.integers(0, 2) == 0 else " b") + " - - 0 1"


def main():
    rng = np.random.default_rng(20261016)
    hidden = 256
    # Wide random weights, so the accumulators reach past both ends of the clamp and the sums are large.
    fw = rng.normal(0, 24, (INPUTS, hidden)).round().clip(-90, 90).astype(np.int16)
    fb = rng.integers(-40, 200, hidden).astype(np.int16)
    ow = rng.integers(-127, 128, 2 * hidden).astype(np.int16)
    ob = int(rng.integers(-50_000, 50_001))
    out = pathlib.Path(__file__).resolve().parents[2] / "tests" / "ChessBruteforcer.Tests" / "Data"
    out.mkdir(exist_ok=True)
    net_path = out / "nnue-random-256.nnue"
    write_nnue(net_path, fw, fb, ow, ob)
    net = read_nnue(net_path)

    fens = KNOWN + [random_fen(rng) for _ in range(1000 - len(KNOWN))]
    lines = ["# nnue-random-256.nnue: FEN, then the score for the side to move by scripts/nnue-reference/nnue_reference.py"]
    scores = []
    clamped = [0, 0]
    for fen in fens:
        score = evaluate(net, fen)
        scores.append(score)
        pieces, side = parse_fen(fen)
        for p, perspective in enumerate((side, 1 - side)):
            acc = accumulator(net, pieces, perspective)
            clamped[0] += int((acc < 0).sum())
            clamped[1] += int((acc > net["qa"]).sum())
        lines.append(f"{fen}\t{score}")
    (out / "nnue-random-256.tsv").write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    s = np.array(scores)
    print(f"{len(fens)} positions; scores {s.min()} .. {s.max()}, median |score| {int(np.median(np.abs(s)))}, "
          f"{int((np.abs(s) == MAX_SCORE).sum())} at the limit")
    print(f"accumulator numbers below 0: {clamped[0]:,}, above QA: {clamped[1]:,} "
          f"(of {2 * hidden * len(fens):,})")
    print(f"sha256 {hashlib.sha256(net_path.read_bytes()[:-32]).hexdigest()}")
    for sizes in ((32,), (16, 32)):
        deep_fixture(out, sizes)
    return 0


def deep_fixture(out, sizes, hidden=128, count=1000):
    """A version 2 net with random weights and `count` positions with its scores: every layer's numbers land on
    both sides of its clamp, so the clamps, the divisions and the signs are all exercised."""
    name = "nnue-random-deep-" + "-".join(map(str, sizes))
    rng = np.random.default_rng(20261021 + sum(sizes))
    fw = rng.normal(0, 24, (INPUTS, hidden)).round().clip(-90, 90).astype(np.int16)
    fb = rng.integers(-40, 200, hidden).astype(np.int16)
    layers, n_in = [], 2 * hidden
    for size in sizes:
        q = 1 << int(rng.integers(6, 12))
        w = rng.normal(0, 3 * q / n_in ** 0.5, (size, n_in)).round().clip(-32767, 32767).astype(np.int16)
        b = rng.integers(-60 * q, 60 * q, size).astype(np.int32)
        layers.append((q, w, b))
        n_in = size
    q = 1 << int(rng.integers(6, 12))
    final = (q, rng.integers(-4 * q, 4 * q + 1, n_in).astype(np.int16), int(rng.integers(-50 * q, 50 * q)))
    net_path = out / f"{name}.nnue"
    write_nnue_deep(net_path, fw, fb, layers, final)
    net = read_nnue(net_path)

    fens = KNOWN + [random_fen(rng) for _ in range(count - len(KNOWN))]
    lines = [f"# {name}.nnue: FEN, then the score for the side to move by scripts/nnue-reference/nnue_reference.py"]
    scores, hits = [], np.zeros((len(sizes), 3), np.int64)   # per layer: at 0, between, at QA
    for fen in fens:
        scores.append(evaluate(net, fen))
        lines.append(f"{fen}\t{scores[-1]}")
        pieces, side = parse_fen(fen)
        x = np.concatenate([np.clip(accumulator(net, pieces, p), 0, net["qa"]) for p in (side, 1 - side)])
        x = (x.astype(np.int64) ** 2 + net["qa"] // 2) // net["qa"]
        for k, (q, w, b) in enumerate(net["layers"]):
            x = np.clip(truncating_div_array(w.astype(np.int64) @ x + b + q // 2, q), 0, net["qa"])
            hits[k] += ((x == 0).sum(), ((x > 0) & (x < net["qa"])).sum(), (x == net["qa"]).sum())
    (out / f"{name}.tsv").write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    s = np.array(scores)
    print(f"{name}: {len(fens)} positions; scores {s.min()} .. {s.max()}, median |score| {int(np.median(np.abs(s)))}, "
          f"{int((np.abs(s) == MAX_SCORE).sum())} at the limit; per layer (at 0, between, at QA): "
          + "; ".join(f"{a:,} {m:,} {z:,}" for a, m, z in hits)
          + f"; sha256 {hashlib.sha256(net_path.read_bytes()[:-32]).hexdigest()}")


if __name__ == "__main__":
    sys.exit(main())
