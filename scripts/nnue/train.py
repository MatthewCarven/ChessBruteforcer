"""Train the engine's network on the GPU (NNUE.md, "Training"): 768 -> H x2 -> 1, SCReLU, exported as int16.

    python scripts/nnue/train.py train <run> [options]   writes <out>/<run>.pt (each pass), <run>.nnue (at the end), <run>.log
    python scripts/nnue/train.py export <run .pt> <.nnue>  a .nnue from a checkpoint
    python scripts/nnue/train.py evaluate <.nnue>...       the held-back error of nets, as the engine computes them
    python scripts/nnue/train.py numbers <numbers .txt>... the held-back error of evaluation numbers (sf1, sf2, ...)

Run it with the venv's python (scripts/nnue/.venv, PyTorch with CUDA). Options of train:

    --data <dir>            `nnue data`'s output (default: September, games\\nnue\\2026-09 in the main checkout)
    --add-data MONTH...     more months to train on, folders beside --data (2026-07 2026-08); held-back error
                            stays --data's alone, so it compares with nets trained before
    --out <dir>             where the net, checkpoint and log go (default games\\nnue\\nets)
    --hidden 256            hidden numbers per perspective
    --layers 32             small hidden layers after it (32, or 16/32), exported as .nnue version 2
    --output-buckets K      K output rows instead of one (1-16), each position using the row of its piece count's
                            bucket, (n - 1) K / 32, n the pieces on the board, kings included: .nnue version 3.
                            A bucket no training position used (at K 8, bucket 0: the records have 6+ pieces) is
                            exported with its nearest trained neighbour's row. Not with --layers
    --passes 20             passes over the training buckets
    --batch 16384           positions a step
    --lr 0.001              AdamW's learning rate, times --lr-gamma after each pass in --lr-drops
    --lr-drops 8,14,18      --lr-gamma 0.3
    --weight-decay 0.01
    --max-positions N       train on the first N positions only (non-mate, from bucket-00 on, held in memory): checks
    --buckets N             train on the first N bucket files only: trials, with the full run's memory use
    --held-back-every 1     passes between held-back reports (0: never)
    --held-back-max N       report on the first N held-back positions only
    --seed 20261016
    --resume                carry on from <run>.pt
    --below-normal          lower this process's priority (beside the bot)

    --duty 1.0              the share of the time the GPU works: after every 4 steps it rests in proportion
    --result-blend 0        the game result's share of the target: (1 - x) s(cp) + x result (session 5's lambda)
    --mates                 train on mate-scored positions too (their target is exactly 0 or 1)

Held-back error is always measured as NNUE.md's "How it is judged" says: against s(cp), mates left out. So it
compares nets trained either way, but a net trained with --result-blend is pulled towards results on purpose and
looks a little worse by that measure without being worse: for those, the match is the judge.

While <out>/<run>.pause exists, or the laptop is on battery (unless --on-battery), training waits between buckets
(a few seconds' notice): the bot's turn, with nothing lost. Delete the file, or plug in, to carry on. <out>/<run>.duty, if it holds a number (0.3, say), sets the duty from
the next bucket on, so a run can be slowed or sped up without stopping it. On this laptop the GPU and the CPU
share one 180 W charger: at full duty the GPU draws ~99 W, and with the bot's 8 threads the CPU drops to about
its base clock, which costs the bot ~22% of its speed; at half duty a bench loses ~10% and the bot's own speed
nothing measurable (NNUE.md, session 2).

What it does. Buckets are read in a random order, each whole (~116 MB), and its raw 32-byte records go to the GPU,
which drops the mates (left out at first, as the tuner did), shuffles them and unpacks each batch into feature
indices exactly as nnue_reference.feature numbers them (scripts/nnue/test-train.py checks that). The feature layer
is a sum of embedding rows, one per piece, for each perspective; the side to move's comes first; SCReLU; one output.
The output y is in units of the file's scale: the score is 400 y centipawns for the side to move.

The loss is the tuner's: (s(400 y) - s(cp))^2 with s(x) = 1 / (1 + 10^(-x / 400)) (Tuner.Sigmoid at K = 1; cp is
Lichess's score for the side to move). After every step each weight is clamped to +-1.98, so the int16 export
(feature layer x QA 255, output weights x QB, output bias x QA x QB) can't overflow; the export checks that anyway.
QB is the largest that fits the trained output weights (64 at the clamp; see output_qb), written in the file.

Held-back error, each pass, as NNUE.md's "How it is judged" measures it: K fitted (golden section on [0.1, 4]),
then the mean of (s(K e) - s(cp))^2 over held-back.bin's positions without mates, e being the net's score, or the
numbers' (tapered, as Evaluation.Evaluate(position, numbers) computes them; numpy).
"""
import argparse
import concurrent.futures
import ctypes
import math
import os
import pathlib
import sys
import time

# Host commit is what the laptop runs short of (Claude-Notebook, 2026-10-15). numpy's BLAS reserves a buffer per
# core (~480 MB here) and nothing in this script needs it; one CPU thread is all the trainer uses.
for _name in ("OPENBLAS_NUM_THREADS", "OMP_NUM_THREADS", "MKL_NUM_THREADS"):
    os.environ.setdefault(_name, "1")

import numpy as np  # noqa: E402

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "nnue-reference"))
import records  # noqa: E402
import nnue_reference  # noqa: E402

MAIN = pathlib.Path(r"C:\Users\Admin\Desktop\-=Programming=-\Chess Bruteforcer")   # the main checkout (games/ lives there)
DATA = MAIN / "games" / "nnue" / "2026-09"
NETS = MAIN / "games" / "nnue" / "nets"
NUMBERS = [MAIN / "bot" / "eval" / "eval-sf1.txt", MAIN / "games" / "tune" / "eval-sf2.txt"]

INPUTS = 768
PAD = INPUTS            # the embedding row an empty piece slot points at: left out of the sum
QA, QB, SCALE = 255, 64, 400
CLAMP = 1.98
LN10 = math.log(10)


def s10(x):
    """The tuner's sigmoid at K = 1 on centipawns: 1 / (1 + 10^(-x / 400)). Works on numpy and torch alike."""
    if isinstance(x, np.ndarray):
        return 1 / (1 + np.power(10.0, -x / 400))
    import torch
    return torch.sigmoid(x * (LN10 / 400))


# ---- the records on the GPU ----

def unpack(raw):
    """A batch of raw records, (n, 32) uint8 on the GPU, as the network reads them: the feature index of every piece
    for white's perspective and for black's, (n, 32) int64 each with PAD in empty slots; black to move (bool n);
    white's score (float n); mate (bool n). The index is nnue_reference.feature's (NNUE.md, the C# Network.Feature):
    ((piece colour == p ? 0 : 6) + kind - 1) * 64 + (p == 0 ? square : square ^ 56)."""
    import torch
    n, dev = raw.shape[0], raw.device
    bits = ((raw[:, :8, None] >> torch.arange(8, device=dev, dtype=torch.uint8)) & 1).reshape(n, 64)   # square 8k + j
    rank = bits.to(torch.int64).cumsum(1) - 1                          # each occupied square's place among them
    slot = torch.where(bits.bool(), rank, torch.full_like(rank, 32))    # empty squares all go to a slot thrown away
    square = torch.zeros(n, 33, dtype=torch.int64, device=dev)
    square.scatter_(1, slot, torch.arange(64, device=dev).expand(n, 64))
    square = square[:, :32]
    packed = raw[:, 8:24]
    codes = torch.stack((packed & 15, packed >> 4), 2).reshape(n, 32).to(torch.int64)   # the i-th piece's code
    colour, kind = codes >> 3, codes & 7
    used = codes != 0
    white = torch.where(used, ((colour != 0) * 6 + kind - 1) * 64 + square, PAD)
    black = torch.where(used, ((colour != 1) * 6 + kind - 1) * 64 + (square ^ 56), PAD)
    flags = raw[:, 24]
    score = (raw[:, 26].to(torch.int32) | (raw[:, 27].to(torch.int32) << 8))
    score = torch.where(score >= 32768, score - 65536, score).to(torch.float32)
    return white, black, (flags & records.BLACK_TO_MOVE) != 0, score, (flags & records.MATE_FLAG) != 0


RESULT = 25   # the record's result byte, for white: 0 lost, 1 drawn, 2 won


def targets(batch, btm, score, blend=0.0):
    """What the net is trained towards, for the side to move, in [0, 1]: s(cp) of Lichess's score (a mate's +-32000
    gives exactly 0 or 1), and with blend > 0, (1 - blend) s(cp) + blend x the game's result (1 won, 1/2 drawn, 0
    lost, for the side to move). batch is the raw (n, 32) records that btm and score were unpacked from."""
    import torch
    t = s10(torch.where(btm, -score, score))
    if blend:
        white = batch[:, RESULT].to(torch.float32) / 2
        t = (1 - blend) * t + blend * torch.where(btm, 1 - white, white)
    return t


def output_bucket(white, buckets):
    """Each position's output bucket (version 3), from unpack's feature indices: (n - 1) x K / 32, n the pieces on
    the board, kings included (nnue_reference.bucket)."""
    return ((white != PAD).sum(1) - 1) * buckets // nnue_reference.MAX_PIECES


def make_net(hidden, layers=(), buckets=0):
    import torch

    class Net(torch.nn.Module):
        """768 -> hidden x2 -> 1, SCReLU. y = sum of clamp(a, 0, 1)^2 x w + b; the score is 400 y centipawns.
        With layers (--layers 32, or 16/32): 768 -> hidden x2 -> 32 [-> ...] -> 1 instead, each small layer a
        torch.nn.Linear then clamp(0, 1) (2026-10-21, Matthew: does a deeper net fit the data better?). Exported
        as .nnue version 2 (nnue_reference's docstring has the format), each small layer at its own scale.
        With buckets (--output-buckets K): the output layer is 2 x hidden -> K, ow [K][2 hidden] and ob [K], and
        each position takes the output of its piece count's bucket (output_bucket); version 3 (2026-10-22,
        Matthew's B: Stockfish's output buckets, free at evaluation time). Each row starts as version 1's one."""

        def __init__(self):
            super().__init__()
            bound = 1 / math.sqrt(INPUTS)   # torch.nn.Linear's own initial range, for both layers
            self.hidden = hidden
            self.layers = tuple(layers)
            self.buckets = buckets
            self.fw = torch.nn.Parameter(torch.empty(INPUTS + 1, hidden).uniform_(-bound, bound))   # row PAD unused
            self.fb = torch.nn.Parameter(torch.empty(hidden).uniform_(-bound, bound))
            if self.buckets:
                out = 1 / math.sqrt(2 * hidden)
                self.ow = torch.nn.Parameter(torch.empty(self.buckets, 2 * hidden).uniform_(-out, out))
                self.ob = torch.nn.Parameter(torch.zeros(self.buckets))
            elif not self.layers:
                out = 1 / math.sqrt(2 * hidden)
                self.ow = torch.nn.Parameter(torch.empty(2 * hidden).uniform_(-out, out))
                self.ob = torch.nn.Parameter(torch.zeros(()))
            else:
                sizes = (2 * hidden,) + self.layers
                self.dense = torch.nn.ModuleList(torch.nn.Linear(a, b) for a, b in zip(sizes, sizes[1:]))
                self.final = torch.nn.Linear(sizes[-1], 1)

        def accumulators(self, white, black):
            f = torch.nn.functional.embedding_bag
            return (f(white, self.fw, mode="sum", padding_idx=PAD) + self.fb,
                    f(black, self.fw, mode="sum", padding_idx=PAD) + self.fb)

        def forward(self, white, black, black_to_move):
            aw, ab = self.accumulators(white, black)
            b = black_to_move[:, None]
            h = torch.cat((torch.where(b, ab, aw), torch.where(b, aw, ab)), 1).clamp(0, 1).square()
            if self.buckets:   # every bucket's output (a small matrix product: cuBLAS, unlike version 1), then its own
                y = torch.nn.functional.linear(h, self.ow, self.ob)
                return y.gather(1, output_bucket(white, self.buckets)[:, None]).squeeze(1)
            if not self.layers:
                return (h * self.ow).sum(1) + self.ob   # not h @ ow: that loads cuBLAS, ~350 MB more host commit
            for layer in self.dense:
                h = layer(h).clamp(0, 1)
            return self.final(h).squeeze(1)

        @torch.no_grad()
        def clamp_(self):
            # The feature layer keeps the export's clamp either way; the small layers, a float experiment, go free.
            for p in (self.fw, self.fb) + ((self.ow,) if not self.layers else ()):
                p.clamp_(-CLAMP, CLAMP)

    return Net()


def scores(net, raw, chunk=None):
    """The net's score, centipawns for the side to move, of each of these raw records (no mates expected)."""
    import torch
    # 64 k positions a chunk at 256 hidden, fewer as the net grows, so a chunk's tensors stay ~256 MB: at 2048 the
    # old 64 k ran the laptop's card out of memory, and what the cache keeps counts against Windows' commit.
    chunk = chunk or max(4096, (1 << 24) // net.hidden)
    out = []
    with torch.no_grad():
        for at in range(0, raw.shape[0], chunk):
            w, b, btm, _, _ = unpack(raw[at:at + chunk])
            out.append(net(w, b, btm) * SCALE)
    return torch.cat(out)


def side_to_move_score(r):
    """Lichess's score for the side to move of structured records, float64 (n,)."""
    score = r["score"].astype(np.float64)
    return np.where(records.black_to_move(r), -score, score)


# ---- the error, with K fitted ----

def error_at(e, target, k):
    return float(((s10(k * e) - target) ** 2).mean())


def fit_k(e, target, low=0.1, high=4.0, steps=60):
    """K minimising the mean of (s(K e) - target)^2: golden section on [low, high], as Tuner.FitK."""
    g = (math.sqrt(5) - 1) / 2
    a, b = low, high
    c, d = b - g * (b - a), a + g * (b - a)
    fc, fd = error_at(e, target, c), error_at(e, target, d)
    for _ in range(steps):
        if fc < fd:
            b, d, fd = d, c, fc
            c = b - g * (b - a)
            fc = error_at(e, target, c)
        else:
            a, c, fc = c, d, fd
            d = a + g * (b - a)
            fd = error_at(e, target, d)
    k = (a + b) / 2
    return k, error_at(e, target, k)


# ---- evaluation numbers (sf1, sf2) in numpy ----

NAMES = ["", "pawn", "knight", "bishop", "rook", "queen", "king"]
PHASE_BY_CODE = np.array([0, 0, 1, 1, 2, 4, 0, 0] * 2, np.int64)   # PhaseWeights by piece code
MAX_PHASE = 24


def load_numbers(path):
    """An EvalParams text file (EvalParams.Load's rules) as (mg, eg) tables by piece code and square, signed for
    white (16, 64) int64, and the bishop pair's (mg, eg)."""
    tokens = [t for line in pathlib.Path(path).read_text(encoding="utf-8").splitlines() for t in line.split("#")[0].split()]
    mg_t, eg_t, pair = {}, {}, None
    i = 0
    while i < len(tokens):
        if tokens[i] == "bishop-pair":
            pair = (int(tokens[i + 1]), int(tokens[i + 2]))
            i += 3
            continue
        kind, phase = NAMES.index(tokens[i]), tokens[i + 1]
        (mg_t if phase == "mg" else eg_t)[kind] = np.array([int(v) for v in tokens[i + 2:i + 66]], np.int64)
        i += 66
    if pair is None or len(mg_t) != 6 or len(eg_t) != 6:
        raise ValueError(f"{path}: not 13 parts")
    squares = np.arange(64)
    tables = []
    for source in (mg_t, eg_t):
        t = np.zeros((16, 64), np.int64)
        for kind in range(1, 7):
            t[kind] = source[kind][squares ^ 56]    # white: the tables are written rank 8 first, as seen from white
            t[8 + kind] = -source[kind][squares]    # black: the same table, the board flipped, negated
        tables.append(t)
    return tables[0], tables[1], pair


def numbers_eval(boards, numbers):
    """Evaluation.Evaluate(position, numbers) for white, centipawns, int64 (n,): tapered by phase, the bishop pair,
    C#'s truncating division by 24."""
    mg_t, eg_t, (pair_mg, pair_eg) = numbers
    squares = np.arange(64)
    b = boards.astype(np.int64)
    mg = mg_t[b, squares].sum(1)
    eg = eg_t[b, squares].sum(1)
    phase = np.minimum(PHASE_BY_CODE[b].sum(1), MAX_PHASE)
    pairs = ((boards == 3).sum(1) >= 2).astype(np.int64) - ((boards == 11).sum(1) >= 2)
    mg += pairs * pair_mg
    eg += pairs * pair_eg
    total = mg * phase + eg * (MAX_PHASE - phase)
    q = np.abs(total) // MAX_PHASE
    return np.where(total < 0, -q, q)


def held_back_records(data, limit=None):
    """held-back.bin's records without mates, as a structured numpy array."""
    r = records.read(str(records.held_back_file(str(data))))
    r = r[~records.is_mate(r)]
    return r[:limit] if limit else r


def numbers_scores(held, numbers):
    """The numbers' evaluation for white of each of these records, float64 (n,)."""
    e = np.empty(len(held), np.float64)
    for at in range(0, len(held), 1 << 18):
        e[at:at + (1 << 18)] = numbers_eval(records.boards(held[at:at + (1 << 18)]), numbers)
    return e


def numbers_error(held, numbers):
    """(K, error at K, error at K = 1) of evaluation numbers on these held-back records (white's view throughout)."""
    e, target = numbers_scores(held, numbers), s10(held["score"].astype(np.float64))
    k, err = fit_k(e, target)
    return k, err, error_at(e, target, 1.0)


# ---- export ----

def output_qb(ow):
    """The output layer's scale: the largest QB with every |output weight| x QB <= 128, so that times QA = 255 it
    still fits in 16 bits (Network.Read's check, and the SIMD pass's v x w). Never below QB = 64, since the clamp
    keeps |w| <= 1.98. The file carries QB, so the engine needs nothing new. A trained net's output weights sit far
    inside the clamp (largest ~0.5), where QB = 64 leaves a typical weight ~7 steps and the roundings add up to a
    near-constant ~10 cp for the side to move; this QB is the same rounding with ~4x finer steps."""
    return max(QB, min(32767, int(128 / max(float(np.abs(ow).max()), 1e-9))))


def quantise(state):
    """(feature weights, feature biases, output weights) as int16, the i32 output bias and QB, from a checkpoint's
    float weights, checked as Network.Read checks them."""
    fw = np.asarray(state["fw"], np.float64)[:INPUTS]
    fb, ow, ob = (np.asarray(state[k], np.float64) for k in ("fb", "ow", "ob"))
    qb = output_qb(ow)
    q = [np.round(fw * QA), np.round(fb * QA), np.round(ow * qb)]
    for a in q:
        if np.abs(a).max() > 32767:
            raise ValueError("a weight doesn't fit in int16")
    fwq, fbq, owq = (a.astype(np.int16) for a in q)
    obq = int(round(float(ob) * QA * qb))
    if abs(obq) >= 2**31:
        raise ValueError("the output bias doesn't fit in 32 bits")
    if np.abs(owq.astype(np.int64)).max() * QA > 32767:
        raise ValueError("an output weight times QA doesn't fit in 16 bits")
    cols = np.sort(fwq.astype(np.int64), axis=0)
    low = fbq + np.minimum(cols[:32], 0).sum(0)
    high = fbq + np.maximum(cols[-32:], 0).sum(0)
    if low.min() < -32768 or high.max() > 32767:
        raise ValueError(f"a hidden unit can reach {low.min()}..{high.max()} on a full board")
    return fwq, fbq, owq, obq, qb


def feature_layer(state):
    """The feature weights and biases as int16, checked as quantise checks them (a full board can't overflow)."""
    fw = np.round(np.asarray(state["fw"], np.float64)[:INPUTS] * QA)
    fb = np.round(np.asarray(state["fb"], np.float64) * QA)
    if max(np.abs(fw).max(), np.abs(fb).max()) > 32767:
        raise ValueError("a feature weight doesn't fit in int16")
    fwq, fbq = fw.astype(np.int16), fb.astype(np.int16)
    cols = np.sort(fwq.astype(np.int64), axis=0)
    low = fbq + np.minimum(cols[:32], 0).sum(0)
    high = fbq + np.maximum(cols[-32:], 0).sum(0)
    if low.min() < -32768 or high.max() > 32767:
        raise ValueError(f"a hidden unit can reach {low.min()}..{high.max()} on a full board")
    return fwq, fbq


def fill_untrained(ow, ob, trained):
    """Rows of buckets that no training position used, replaced by the nearest bucket's that one did (the higher
    piece count on a tie). The records hold more than 5 pieces only (the tuner's quiet rule; the tables settle the
    rest), so at K = 8 bucket 0 (2-4 pieces) is never trained, but quiescence, which doesn't probe the tables,
    still evaluates such positions after captures. Returns (ow, ob, [(bucket, the bucket it took)])."""
    used = np.nonzero(np.asarray(trained) > 0)[0]
    if not len(used) or len(used) == len(trained):
        return ow, ob, []
    ow, ob, filled = ow.copy(), ob.copy(), []
    for k in range(len(trained)):
        if trained[k] == 0:
            near = int(min(used, key=lambda u: (abs(u - k), -u)))
            ow[k], ob[k] = ow[near], ob[near]
            filled.append((k, near))
    return ow, ob, filled


def quantise_buckets(state, trained=None):
    """Version 3's numbers from an --output-buckets checkpoint: the feature layer as version 1's, and each bucket's
    output row and bias as version 1 quantises its one, at one QB for all (output_qb over every bucket's weights).
    With trained (positions per bucket in training), untrained buckets take a trained neighbour's row
    (fill_untrained). Returns version 3's numbers, QB and the filled buckets."""
    fwq, fbq = feature_layer(state)
    ow, ob = np.asarray(state["ow"], np.float64), np.asarray(state["ob"], np.float64)
    ow, ob, filled = fill_untrained(ow, ob, trained) if trained is not None else (ow, ob, [])
    qb = output_qb(ow)
    owq, obq = np.round(ow * qb), np.round(ob * QA * qb)
    if np.abs(owq).max() * QA > 32767:
        raise ValueError("an output weight times QA doesn't fit in 16 bits")
    if np.abs(obq).max() >= 2**31:
        raise ValueError("an output bias doesn't fit in 32 bits")
    return fwq, fbq, owq.astype(np.int16), obq.astype(np.int32), qb, filled


def has_buckets(state):
    """True for an --output-buckets checkpoint's weights: one output row per bucket."""
    return "ow" in state and np.ndim(state["ow"]) == 2


def layer_q(w, b):
    """A small layer's scale Q (version 2): the largest power of two up to 2^14 with every weight x Q within int16
    and every output's sum within int32 at any inputs, QA x sum|w x Q| + |b x QA x Q| + Q / 2 (Network.Read's
    check)."""
    q = 1 << 14
    while q >= 1:
        wq, bq = np.round(w * q), np.round(b * QA * q)
        if np.abs(wq).max() <= 32767 and (QA * np.abs(wq).sum(-1) + np.abs(bq)).max() + q // 2 <= 2**31 - 1:
            return q, wq.astype(np.int16), bq.astype(np.int64)
        q //= 2
    raise ValueError("a small layer's weights don't fit even at Q = 1")


def quantise_deep(state, layers):
    """Version 2's numbers from a --layers checkpoint: the feature layer as version 1's, each small layer and the
    output with its own Q (layer_q)."""
    fwq, fbq = feature_layer(state)
    quantised = []
    for k in range(len(layers)):
        w = np.asarray(state[f"dense.{k}.weight"], np.float64)
        b = np.asarray(state[f"dense.{k}.bias"], np.float64)
        q, wq, bq = layer_q(w, b)
        quantised.append((q, wq, bq.astype(np.int32)))
    w = np.asarray(state["final.weight"], np.float64).reshape(-1)
    b = np.asarray(state["final.bias"], np.float64).reshape(-1)
    q, wq, bq = layer_q(w[None, :], b)
    return fwq, fbq, quantised, (q, wq[0], int(bq[0]))


def export(state, path, layers=(), trained=None):
    """Writes the .nnue (version 2 for a net with --layers, 3 for one with --output-buckets: trained, the positions
    each bucket had in training, fills the untrained ones); returns (the engine's Network.Sha256, a note of its
    scales: QB, or each layer's Q)."""
    tmp = str(path) + ".tmp"
    if has_buckets(state):
        fwq, fbq, owq, obq, qb, filled = quantise_buckets(state, trained)
        nnue_reference.write_nnue_buckets(tmp, fwq, fbq, owq, obq, QA, qb, SCALE)
        note = f"QB {qb}, {owq.shape[0]} output buckets" + "".join(
            f", bucket {k} untrained: bucket {near}'s row" for k, near in filled)
    elif layers:
        fwq, fbq, quantised, final = quantise_deep(state, layers)
        nnue_reference.write_nnue_deep(tmp, fwq, fbq, quantised, final, QA, SCALE)
        note = "Q " + " / ".join(str(q) for q, _, _ in quantised + [final])
    else:
        fwq, fbq, owq, obq, qb = quantise(state)
        nnue_reference.write_nnue(tmp, fwq, fbq, owq, obq, QA, qb, SCALE)
        note = f"QB {qb}"
    os.replace(tmp, path)
    data = pathlib.Path(path).read_bytes()
    return data[-32:].hex(), note   # the hash of everything before it, as the engine prints it


def int_scores(net_file, raw, chunk=None):
    """A .nnue's scores, exactly as the engine computes them (Network.Forward), for these raw records on the GPU:
    the accumulators and sums in float64, which holds these integers exactly, then C#'s truncating divisions in
    int64. checks.py holds this to nnue_reference.evaluate."""
    import torch
    q = nnue_reference.read_nnue(net_file)
    dev, h = raw.device, q["hidden"]
    chunk = chunk or max(1024, (1 << 23) // h)   # 32 k positions at 256 hidden, fewer as it grows (as in scores)
    fw = torch.zeros(INPUTS + 1, h, dtype=torch.float64, device=dev)
    fw[:INPUTS] = torch.from_numpy(q["fw"].astype(np.float64))
    fb = torch.from_numpy(q["fb"].astype(np.float64)).to(dev)
    deep = q.get("layers")
    if deep:   # version 2: the small layers' weights as float64 too (integers below 2^53 stay exact)
        layers = [(lq, torch.from_numpy(w.astype(np.float64)).to(dev), torch.from_numpy(b.astype(np.float64)).to(dev))
                  for lq, w, b in deep]
        fq, fwt, fbias = q["final"]
        fwt = torch.from_numpy(fwt.astype(np.float64)).to(dev)
    else:
        ow = torch.from_numpy(q["ow"].astype(np.float64)).to(dev)
    buckets = q.get("buckets")
    if buckets:   # version 3: [K][2H] rows and K biases, each position its bucket's
        ob = torch.from_numpy(q["ob"].astype(np.int64)).to(dev)
    out = []
    with torch.no_grad():
        for at in range(0, raw.shape[0], chunk):
            w, b, btm, _, _ = unpack(raw[at:at + chunk])
            f = torch.nn.functional.embedding_bag
            aw, ab = f(w, fw, mode="sum", padding_idx=PAD) + fb, f(b, fw, mode="sum", padding_idx=PAD) + fb
            m = btm[:, None]
            v = torch.cat((torch.where(m, ab, aw), torch.where(m, aw, ab)), 1).clamp(0, q["qa"])
            if deep:
                # As nnue_reference.evaluate: rounded SCReLU, then each layer rounded to the nearest step.
                x = torch.div(v * v + q["qa"] // 2, q["qa"], rounding_mode="floor")   # >= 0: floor = truncation
                for lq, lw, lb in layers:
                    x = torch.div(x @ lw.T + lb + lq // 2, lq, rounding_mode="trunc").clamp(0, q["qa"])
                total = (x @ fwt).to(torch.int64) + fbias
                s = torch.div(total * q["scale"], q["qa"] * fq, rounding_mode="trunc")
            elif buckets:
                k = output_bucket(w, buckets)
                total = (v * v * ow[k]).sum(1).to(torch.int64)
                s = torch.div(total, q["qa"], rounding_mode="trunc") + ob[k]
                s = torch.div(s * q["scale"], q["qa"] * q["qb"], rounding_mode="trunc")
            else:
                total = (v * v * ow).sum(1).to(torch.int64)
                s = torch.div(total, q["qa"], rounding_mode="trunc") + q["ob"]
                s = torch.div(s * q["scale"], q["qa"] * q["qb"], rounding_mode="trunc")
            out.append(s.clamp(-nnue_reference.MAX_SCORE, nnue_reference.MAX_SCORE).to(torch.float32))
    return torch.cat(out)


# ---- training ----

class Log:
    def __init__(self, path):
        self.f = open(path, "a", encoding="utf-8", buffering=1) if path else None

    def __call__(self, text):
        line = f"{time.strftime('%H:%M:%S')} {text}"
        print(line, flush=True)
        if self.f:
            self.f.write(line + "\n")


def below_normal():
    """SetPriorityClass(GetCurrentProcess(), BELOW_NORMAL), typed for 64 bits (Claude-Notebook, 2026-10-14)."""
    k = ctypes.WinDLL("kernel32", use_last_error=True)
    k.GetCurrentProcess.restype = ctypes.c_void_p
    k.SetPriorityClass.argtypes = (ctypes.c_void_p, ctypes.c_uint32)
    k.SetPriorityClass.restype = ctypes.c_int
    if not k.SetPriorityClass(k.GetCurrentProcess(), 0x4000):
        raise OSError(ctypes.get_last_error(), "SetPriorityClass failed")


def on_battery():
    """True while Windows says the AC line is off (GetSystemPowerStatus). On battery this laptop throttles the CPU
    and the GPU alike, so the bot is already slow and training would drain the battery for little."""
    class Status(ctypes.Structure):
        _fields_ = [("ACLineStatus", ctypes.c_uint8), ("BatteryFlag", ctypes.c_uint8),
                    ("BatteryLifePercent", ctypes.c_uint8), ("SystemStatusFlag", ctypes.c_uint8),
                    ("BatteryLifeTime", ctypes.c_uint32), ("BatteryFullLifeTime", ctypes.c_uint32)]
    s = Status()
    k = ctypes.WinDLL("kernel32")
    k.GetSystemPowerStatus.argtypes = (ctypes.c_void_p,)
    k.GetSystemPowerStatus.restype = ctypes.c_int
    return bool(k.GetSystemPowerStatus(ctypes.byref(s))) and s.ACLineStatus == 0


def host_memory():
    """(working set, private commit) of this process in MB."""
    class Counters(ctypes.Structure):
        _fields_ = [("cb", ctypes.c_uint32), ("PageFaultCount", ctypes.c_uint32)] + [
            (n, ctypes.c_size_t) for n in ("PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
                                           "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage",
                                           "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage",
                                           "PrivateUsage")]
    c = Counters()
    c.cb = ctypes.sizeof(c)
    psapi = ctypes.WinDLL("psapi")
    k = ctypes.WinDLL("kernel32")
    k.GetCurrentProcess.restype = ctypes.c_void_p
    psapi.GetProcessMemoryInfo.argtypes = (ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32)
    psapi.GetProcessMemoryInfo(k.GetCurrentProcess(), ctypes.byref(c), c.cb)
    return c.WorkingSetSize / 2**20, c.PrivateUsage / 2**20


def training_sets(args):
    """The training data as a list of loaders: each returns a raw (n, 32) uint8 numpy array of one bucket. The
    buckets of --data and of every --add-data month beside it, read in one random order."""
    files = records.training_files(str(args.data))
    for name in args.add_data:
        files += records.training_files(str(args.data.parent / name))
    if args.buckets:
        files = files[:args.buckets]
    if not args.max_positions:
        return [lambda p=p: np.fromfile(p, np.uint8).reshape(-1, records.SIZE) for p in files]
    taken, need = [], args.max_positions
    for p in files:
        r = np.fromfile(p, np.uint8).reshape(-1, records.SIZE)
        r = r[(r[:, 24] & records.MATE_FLAG) == 0][:need]
        taken.append(r)
        need -= len(r)
        if need <= 0:
            break
    fixed = np.concatenate(taken)
    return [lambda: fixed]


def train(args):
    import torch
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    log = Log(out / f"{args.run}.log")
    if args.below_normal:
        below_normal()
    dev = torch.device("cuda")
    log(f"run {args.run}: {' '.join(sys.argv[1:])}")
    log(f"torch {torch.__version__}, {torch.cuda.get_device_name(0)}")
    torch.manual_seed(args.seed)
    rng = np.random.default_rng(args.seed)
    gen = torch.Generator(device=dev)
    gen.manual_seed(args.seed)

    layers = tuple(int(x) for x in args.layers.replace("/", ",").split(",") if x)
    net = make_net(args.hidden, layers, args.output_buckets).to(dev)
    opt = torch.optim.AdamW(net.parameters(), lr=args.lr, weight_decay=args.weight_decay)
    drops = [int(x) for x in args.lr_drops.replace("/", ",").split(",") if x]   # 4/7/9 too (see --lr-drops)
    checkpoint = out / f"{args.run}.pt"
    first = 1
    # --output-buckets: the training positions each bucket has had, over the run (export fills the untrained)
    trained = torch.zeros(args.output_buckets, dtype=torch.int64, device=dev)
    if args.resume:
        saved = torch.load(checkpoint, map_location=dev, weights_only=False)
        net.load_state_dict({k: torch.as_tensor(v, device=dev) for k, v in saved["state"].items()})
        opt.load_state_dict(saved["optimizer"])
        first = saved["pass"] + 1
        if args.output_buckets and "bucket_positions" in saved:
            trained = torch.as_tensor(saved["bucket_positions"], dtype=torch.int64, device=dev)
        rng = np.random.default_rng([args.seed, first])
        gen.manual_seed(args.seed + first)
        log(f"resumed after pass {saved['pass']}")

    held = None
    if args.held_back_every:
        h = held_back_records(args.data, args.held_back_max)
        held = torch.from_numpy(np.ascontiguousarray(h).view(np.uint8).reshape(-1, records.SIZE)).to(dev)
        held_target = s10(torch.from_numpy(side_to_move_score(h)).to(dev, torch.float32))
        log(f"held back: {len(h):,} positions without mates")
        for path in NUMBERS:
            if path.exists():
                k, err, err1 = numbers_error(h, load_numbers(path))
                log(f"  {path.stem}: {err1:.6f} at K 1, {err:.6f} at K {k:.4f}")
        del h

    loaders = training_sets(args)
    pool = concurrent.futures.ThreadPoolExecutor(1)
    pause = out / f"{args.run}.pause"
    duty_file = out / f"{args.run}.duty"

    def duty():
        """The share of the time the GPU works: <run>.duty if it holds a number, else --duty."""
        try:
            return min(1.0, max(0.02, float(duty_file.read_text().strip())))
        except (OSError, ValueError):
            return args.duty

    current_duty = None
    for p in range(first, args.passes + 1):
        lr = args.lr * args.lr_gamma ** sum(1 for d in drops if p > d)
        for g in opt.param_groups:
            g["lr"] = lr
        order = rng.permutation(len(loaders))
        t0, seen = time.time(), 0
        nxt = pool.submit(loaders[order[0]])
        loss_total = torch.zeros((), device=dev)
        for i in range(len(order)):
            battery = not args.on_battery and on_battery()
            if pause.exists() or battery:   # the bot's turn: wait between buckets, out of the pass's clock
                stopped = time.time()
                log(f"  paused ({'on battery' if battery else f'{pause} exists'})")
                while pause.exists() or (not args.on_battery and on_battery()):
                    time.sleep(2)
                t0 += time.time() - stopped
                log(f"  carrying on after {time.time() - stopped:,.0f} s")
            arr = nxt.result()
            if i + 1 < len(order):
                nxt = pool.submit(loaders[order[i + 1]])
            d = duty()
            if d != current_duty:
                log(f"  duty {d:.2f}: the GPU works {d:.0%} of the time")
                current_duty = d
            raw = torch.from_numpy(arr).to(dev)
            if not args.mates:
                raw = raw[(raw[:, 24] & records.MATE_FLAG) == 0]
            perm = torch.randperm(raw.shape[0], device=dev, generator=gen)
            mark = time.perf_counter()
            for k, at in enumerate(range(0, raw.shape[0], args.batch)):
                batch = raw[perm[at:at + args.batch]]
                w, b, btm, score, _ = unpack(batch)
                if args.output_buckets:
                    trained += torch.bincount(output_bucket(w, args.output_buckets), minlength=args.output_buckets)
                y = net(w, b, btm)
                loss = ((torch.sigmoid(y * LN10) - targets(batch, btm, score, args.result_blend)) ** 2).mean()
                opt.zero_grad(set_to_none=True)
                loss.backward()
                opt.step()
                net.clamp_()
                loss_total += loss.detach() * w.shape[0]
                seen += w.shape[0]
                if d < 1 and k % 4 == 3:   # a rest after every 4 steps (~70 ms of work), in proportion
                    torch.cuda.synchronize()
                    time.sleep((time.perf_counter() - mark) * (1 / d - 1))
                    mark = time.perf_counter()
            del raw, perm
            if args.progress and (i + 1) % args.progress == 0:
                torch.cuda.synchronize()
                dt = time.time() - t0
                ws, priv = host_memory()
                log(f"  pass {p} bucket {i + 1}/{len(order)}: {seen:,} positions, {seen / dt:,.0f} a second, "
                    f"loss {float(loss_total) / seen:.6f}; GPU {torch.cuda.max_memory_allocated() / 2**20:,.0f} MB "
                    f"peak allocated; host {ws:,.0f} MB working set, {priv:,.0f} MB commit")
        torch.cuda.synchronize()
        dt = time.time() - t0
        line = (f"pass {p}/{args.passes}: lr {lr:.2e}, {seen:,} positions in {dt:,.1f} s ({seen / dt:,.0f} a second), "
                f"training error {float(loss_total) / max(seen, 1):.6f}")
        if held is not None and (p % args.held_back_every == 0 or p == args.passes):
            e = scores(net, held)
            k, err = fit_k(e, held_target)
            line += f"; held back {error_at(e, held_target, 1.0):.6f} at K 1, {err:.6f} at K {k:.4f}"
            # Hand the scoring's cached GPU memory back: on Windows it stayed in the process's commit for the rest of
            # the run (+1.1 GB at 512 hidden, +2.2 GB at 1024 on the desktop, from the end of pass 1 on).
            del e
            torch.cuda.empty_cache()
        log(line)
        if args.output_buckets:
            log(f"  training positions by output bucket, so far: {' '.join(f'{c:,}' for c in trained.tolist())}")
        state = {k: v.detach().cpu().numpy() for k, v in net.state_dict().items()}
        tmp = str(checkpoint) + ".tmp"
        torch.save({"state": state, "optimizer": opt.state_dict(), "pass": p, "hidden": args.hidden,
                    "layers": layers, "output_buckets": args.output_buckets, "bucket_positions": trained.tolist(),
                    "args": vars(args) | {"data": str(args.data), "out": str(args.out)}}, tmp)
        os.replace(tmp, checkpoint)
    pool.shutdown()
    ws, priv = host_memory()
    log(f"GPU peak allocated {torch.cuda.max_memory_allocated() / 2**20:,.0f} MB, reserved "
        f"{torch.cuda.max_memory_reserved() / 2**20:,.0f} MB; host {ws:,.0f} MB working set, {priv:,.0f} MB commit")
    state = {k: v.detach().cpu().numpy() for k, v in net.state_dict().items()}
    path = out / f"{args.run}.nnue"
    # version 2 for a net with --layers, 3 with --output-buckets (its untrained buckets filled)
    sha, note = export(state, path, layers, trained.tolist() if args.output_buckets else None)
    log(f"wrote {path} (QA {QA}, {note}; network sha256 {sha})")
    if held is not None:
        e = int_scores(path, held)
        k, err = fit_k(e, held_target)
        log(f"{path.name} as the engine computes it: held back {error_at(e, held_target, 1.0):.6f} at K 1, "
            f"{err:.6f} at K {k:.4f}")
    return 0


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="command", required=True)
    t = sub.add_parser("train")
    t.add_argument("run")
    t.add_argument("--data", type=pathlib.Path, default=DATA)
    t.add_argument("--add-data", nargs="+", default=[], metavar="MONTH",
                   help="more months to train on, as folders beside --data (2026-07 2026-08); held-back error stays "
                        "--data's, so it compares with earlier nets")
    t.add_argument("--out", type=pathlib.Path, default=NETS)
    t.add_argument("--hidden", type=int, default=256)
    t.add_argument("--layers", default="",
                   help="small hidden layers after the feature layer, e.g. 32 or 16/32 (exported as .nnue version 2)")
    t.add_argument("--output-buckets", type=int, default=0, metavar="K",
                   help="K output rows (1-16), each position its piece count's, (n - 1) K / 32 (.nnue version 3); "
                        "not with --layers")
    t.add_argument("--passes", type=int, default=20)
    t.add_argument("--batch", type=int, default=16384)
    t.add_argument("--lr", type=float, default=0.001)
    t.add_argument("--lr-drops", default="8,14,18",
                   help="passes after which the rate drops: 8,14,18, or 8/14/18 where a shell splits commas "
                        "(PowerShell reads an unquoted 4,7,9 as three arguments)")
    t.add_argument("--lr-gamma", type=float, default=0.3)
    t.add_argument("--weight-decay", type=float, default=0.01)
    t.add_argument("--max-positions", type=int, default=0)
    t.add_argument("--buckets", type=int, default=0, help="train on the first N bucket files only (0: all)")
    t.add_argument("--held-back-every", type=int, default=1)
    t.add_argument("--held-back-max", type=int, default=0)
    t.add_argument("--progress", type=int, default=8, help="buckets between progress lines (0: none)")
    t.add_argument("--seed", type=int, default=20261016)
    t.add_argument("--resume", action="store_true")
    t.add_argument("--below-normal", action="store_true")
    t.add_argument("--duty", type=float, default=1.0, help="share of the time the GPU works (<run>.duty overrides)")
    t.add_argument("--on-battery", action="store_true", help="keep training on battery (default: wait for AC)")
    t.add_argument("--result-blend", type=float, default=0.0,
                   help="the game result's share of the target (0: Lichess's score alone)")
    t.add_argument("--mates", action="store_true",
                   help="train on mate-scored positions too (target 0 or 1); held-back error still leaves them out")
    e = sub.add_parser("export")
    e.add_argument("checkpoint")
    e.add_argument("nnue")
    n = sub.add_parser("numbers")
    n.add_argument("files", nargs="+")
    n.add_argument("--data", type=pathlib.Path, default=DATA)
    v = sub.add_parser("evaluate")
    v.add_argument("files", nargs="+")
    v.add_argument("--data", type=pathlib.Path, default=DATA)
    args = ap.parse_args(argv)
    if args.command == "train":
        if args.output_buckets and args.layers:
            ap.error("--output-buckets and --layers don't go together: no .nnue version has both")
        if not 0 <= args.output_buckets <= nnue_reference.MAX_BUCKETS:
            ap.error(f"--output-buckets takes 1 to {nnue_reference.MAX_BUCKETS}")
        return train(args)
    if args.command == "export":
        import torch
        saved = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
        sha, note = export(saved["state"], args.nnue, tuple(saved.get("layers", ())), saved.get("bucket_positions"))
        print(f"QA {QA}, {note}; network sha256 {sha}")
        return 0
    if args.command == "evaluate":
        import torch
        h = held_back_records(args.data)
        held = torch.from_numpy(np.ascontiguousarray(h).view(np.uint8).reshape(-1, records.SIZE)).cuda()
        target = s10(torch.from_numpy(side_to_move_score(h)).cuda().to(torch.float32))
        print(f"held back: {len(h):,} positions without mates")
        for path in args.files:
            e = int_scores(path, held)
            k, err = fit_k(e, target)
            print(f"{path}: {error_at(e, target, 1.0):.6f} at K 1, {err:.6f} at K {k:.4f}")
        return 0
    held = held_back_records(args.data)
    print(f"held back: {len(held):,} positions without mates")
    for path in args.files:
        k, err, err1 = numbers_error(held, load_numbers(path))
        print(f"{path}: {err1:.6f} at K 1, {err:.6f} at K {k:.4f}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
