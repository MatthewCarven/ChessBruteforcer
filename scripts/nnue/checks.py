"""The trainer's checks before a long run (NNUE.md, "Session 2"). Run with the venv's python.

    python scripts/nnue/checks.py features [n]                    the GPU's unpacking equals nnue_reference.feature
    python scripts/nnue/checks.py targets [n]                     the training targets (--result-blend, --mates), on the CPU
    python scripts/nnue/checks.py quantised <run .pt> [n]         the float net and its int16 export agree (centipawns)
    python scripts/nnue/checks.py buckets [n]                     output buckets (version 3), on the CPU
    python scripts/nnue/checks.py engine <net .nnue> <engine exe> [n]       the engine's eval equals nnue_reference's
    python scripts/nnue/checks.py numbers <numbers .txt> <engine exe> [n]   the engine's eval equals train.numbers_eval

Positions come from September's held-back.bin (mates left out, as in training), n of them (10,000 for features,
1,000 otherwise). The engine is driven through UCI: EvalFile, then "position fen" and "eval" for each. quantised
runs on the GPU, or on the CPU with CHECKS_DEVICE=cpu (the laptop's GPU is left alone while the bot plays).
"""
import os
import pathlib
import subprocess
import sys
import tempfile

import numpy as np

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import train  # noqa: E402  (puts nnue-reference on the path too)
import records  # noqa: E402
import nnue_reference  # noqa: E402


def sample(n, mates=False):
    """n held-back records (mates too when asked), spread evenly over the file."""
    r = records.read(str(records.held_back_file(str(train.DATA))))
    if not mates:
        r = r[~records.is_mate(r)]
    return r[np.linspace(0, len(r) - 1, n).astype(np.int64)]


def reference_features(r):
    """(n, 32) feature indices per perspective, by nnue_reference.feature, square order, PAD in empty slots."""
    out = np.full((2, len(r), 32), train.PAD, np.int64)
    for i, board in enumerate(records.boards(r)):
        slot = 0
        for square in np.nonzero(board)[0]:
            code = int(board[square])
            for p in (0, 1):
                out[p, i, slot] = nnue_reference.feature(code >> 3, code & 7, int(square), p)
            slot += 1
    return out


def features(n=10_000):
    import torch
    r = np.concatenate([sample(n // 2, mates=True), records.read(records.training_files(str(train.DATA))[0], 0, n - n // 2)])
    raw = torch.from_numpy(np.ascontiguousarray(r).view(np.uint8).reshape(-1, records.SIZE)).cuda()
    white, black, btm, score, mate = (t.cpu().numpy() for t in train.unpack(raw))
    ref = reference_features(r)
    bad = {
        "white's features": int((white != ref[0]).any(1).sum()),
        "black's features": int((black != ref[1]).any(1).sum()),
        "side to move": int((btm != records.black_to_move(r)).sum()),
        "score": int((score != r["score"]).sum()),
        "mate flag": int((mate != records.is_mate(r)).sum()),
    }
    pieces = int((ref[0] != train.PAD).sum())
    print(f"{len(r):,} records ({int(records.is_mate(r).sum()):,} mates, {int(records.black_to_move(r).sum()):,} black to move), "
          f"{pieces:,} pieces")
    for what, k in bad.items():
        print(f"  {what}: {k:,} records differ")
    ok = not any(bad.values())
    print("features: " + ("all equal" if ok else "DIFFER"))
    return 0 if ok else 1


def targets(n=10_000):
    """train.targets against its rule restated in numpy, mates included: s(cp) for the side to move (a mate exactly
    0 or 1), blended as (1 - x) s(cp) + x result for x = 0, 0.25 and 1. Runs on the CPU."""
    import torch
    r = sample(n, mates=True)
    raw = torch.from_numpy(np.ascontiguousarray(r).view(np.uint8).reshape(-1, records.SIZE))
    _, _, btm, score, _ = train.unpack(raw)
    black = records.black_to_move(r)
    cp = np.where(black, -1.0, 1.0) * r["score"].astype(np.float64)
    s = 1 / (1 + np.power(10.0, -cp / 400))
    white = r["result"].astype(np.float64) / 2
    result = np.where(black, 1 - white, white)
    mate = records.is_mate(r)
    print(f"{len(r):,} records ({int(mate.sum()):,} mates; results for the side to move: "
          f"{int((result == 1).sum()):,} won, {int((result == 0.5).sum()):,} drawn, {int((result == 0).sum()):,} lost)")
    ok = True
    for blend in (0.0, 0.25, 1.0):
        mine = train.targets(raw, btm, score, blend).numpy().astype(np.float64)
        worst = float(np.abs(mine - ((1 - blend) * s + blend * result)).max())
        ok &= worst < 1e-6
        print(f"  blend {blend}: largest difference {worst:.2e}")
    mates = train.targets(raw, btm, score)[torch.from_numpy(mate)].numpy()
    ok &= bool(np.isin(mates, (0.0, 1.0)).all())
    print(f"  mates' targets without a blend: {sorted(set(mates.tolist()))}")
    print("targets: " + ("all equal" if ok else "DIFFER"))
    return 0 if ok else 1


def quantised(checkpoint, n=1_000):
    import torch
    dev = os.environ.get("CHECKS_DEVICE", "cuda")
    saved = torch.load(checkpoint, map_location=dev, weights_only=False)
    layers = tuple(saved.get("layers", ()))   # a --layers net exports as version 2, an --output-buckets one as 3
    net = train.make_net(saved["hidden"], layers, saved.get("output_buckets", 0)).to(dev)
    net.load_state_dict({k: torch.as_tensor(v).to(dev) for k, v in saved["state"].items()})
    r = sample(n)
    raw = torch.from_numpy(np.ascontiguousarray(r).view(np.uint8).reshape(-1, records.SIZE)).to(dev)
    floats = train.scores(net, raw).cpu().numpy().astype(np.float64)
    with tempfile.TemporaryDirectory() as d:
        path = pathlib.Path(d) / "q.nnue"
        _, qb = train.export(saved["state"], path, layers, saved.get("bucket_positions"))
        q = nnue_reference.read_nnue(path)
        ints = np.array([nnue_reference.evaluate(q, records.to_fen(x)) for x in r], np.float64)
        gpu = train.int_scores(path, raw).cpu().numpy().astype(np.float64)
    signed = ints - floats
    diff = np.abs(signed)
    print(f"{n:,} positions, exported at QA {train.QA}, {qb}: int16 - float signed mean {signed.mean():+.2f} cp; "
          f"|int16 - float| mean {diff.mean():.2f}, median {np.median(diff):.2f}, 95th {np.percentile(diff, 95):.2f}, "
          f"max {diff.max():.2f} (scores: median |float| {np.median(np.abs(floats)):.0f} cp)")
    worst = int(diff.argmax())
    print(f"  worst: {records.to_fen(r[worst])}  float {floats[worst]:.2f}, int16 {ints[worst]:.0f}")
    differ = int((gpu != ints).sum())
    print(f"train.int_scores (the GPU's integer pass, for held-back error) against nnue_reference.evaluate: "
          f"{differ:,} of {n:,} differ")
    return 0 if differ == 0 else 1


def buckets(n=1_000, hidden=256, count=8):
    """Output buckets (version 3) on the CPU, against nnue_reference: (1) train.output_bucket, from unpack's indices,
    equals nnue_reference.bucket from the FEN's pieces for K = 1 to 16; (2) a float net with `count` buckets, its
    weights spread so that every bucket scores differently and the accumulators reach both ends of the clamp, goes
    through train.export: train.int_scores equals nnue_reference.evaluate, and the int16 scores are close to the
    float net's; (3) version 3 with every row the same (K = 1 and K = `count`) scores exactly as version 1 does
    with that row."""
    import torch
    # The positions spread over every piece count held-back.bin has, so every bucket is used (an even sample of
    # 1,000 had nothing under 6 pieces).
    held = sample(min(n * 200, 2_000_000))
    count_of = (records.boards(held) != 0).sum(1)
    rng = np.random.default_rng(20261022)
    picks = [rng.permutation(np.nonzero(count_of == c)[0])[:max(1, n // 31)] for c in range(2, 33)]
    r = held[np.sort(np.concatenate(picks))[:n]]
    n = len(r)
    fens = [records.to_fen(x) for x in r]
    raw = torch.from_numpy(np.ascontiguousarray(r).view(np.uint8).reshape(-1, records.SIZE))
    white = train.unpack(raw)[0]
    pieces = np.array([len(nnue_reference.parse_fen(f)[0]) for f in fens])
    ok = True
    wrong = {k: int((train.output_bucket(white, k).numpy() != [nnue_reference.bucket(p, k) for p in pieces]).sum())
             for k in range(1, nnue_reference.MAX_BUCKETS + 1)}
    ok &= not any(wrong.values())
    print(f"{n:,} positions, {pieces.min()} to {pieces.max()} pieces; train.output_bucket against nnue_reference.bucket "
          f"for K = 1 to 16: {sum(wrong.values()):,} differ")

    torch.manual_seed(20261022)
    net = train.make_net(hidden, (), count)
    with torch.no_grad():
        net.fw.mul_(4)                          # accumulators past both ends of the clamp
        net.ow.copy_(torch.randn(count, 2 * hidden) * 0.4)
        net.ob.copy_(torch.randn(count) * 0.5)
        net.clamp_()
    state = {k: v.detach().numpy() for k, v in net.state_dict().items()}
    floats = train.scores(net, raw).numpy().astype(np.float64)
    with tempfile.TemporaryDirectory() as d:
        path = pathlib.Path(d) / "ob.nnue"
        _, note = train.export(state, path)
        q = nnue_reference.read_nnue(path)
        ints = np.array([nnue_reference.evaluate(q, f) for f in fens], np.float64)
        gpu = train.int_scores(path, raw).numpy().astype(np.float64)
        of = np.array([nnue_reference.bucket(p, count) for p in pieces])
        differ = int((gpu != ints).sum())
        diff = np.abs(ints - floats)
        print(f"a float net, {hidden} x2 -> {count} buckets, exported ({note}; positions by bucket "
              f"{' '.join(map(str, np.bincount(of, minlength=count)))}): train.int_scores against nnue_reference.evaluate "
              f"{differ:,} of {n:,} differ; |int16 - float| mean {diff.mean():.2f} cp, max {diff.max():.2f} (median "
              f"|float| {np.median(np.abs(floats)):.0f} cp; the rounding of these deliberately wide weights)")
        ok &= differ == 0
        # Each bucket's positions as version 1 scores them with that bucket's row: in int16 (the export's numbers,
        # written as a version 1 file at the same QB) and in float (a one-row net).
        int_wrong = float_worst = 0
        for k in range(count):
            at = np.nonzero(of == k)[0]
            if not len(at):
                continue
            one = pathlib.Path(d) / f"row{k}.nnue"
            nnue_reference.write_nnue(one, q["fw"], q["fb"], q["ow"][k], int(q["ob"][k]), q["qa"], q["qb"], q["scale"])
            v1 = nnue_reference.read_nnue(one)
            int_wrong += sum(nnue_reference.evaluate(v1, fens[i]) != ints[i] for i in at)
            row = train.make_net(hidden)
            row.load_state_dict({"fw": net.fw, "fb": net.fb, "ow": net.ow[k], "ob": net.ob[k]})
            float_worst = max(float_worst, float(np.abs(train.scores(row, raw[at]).numpy() - floats[at]).max()))
        print(f"  each bucket's positions against version 1 with that bucket's row: {int_wrong:,} differ in int16; "
              f"in float the largest difference {float_worst:.2e} cp")
        ok &= int_wrong == 0 and float_worst < 1e-2

        # An untrained bucket is exported with its nearest trained neighbour's row (the higher on a tie).
        trained = [0, 5, 0, 0, 0, 7] + [1] * (count - 6)
        train.export(state, pathlib.Path(d) / "filled.nnue", (), trained)
        f = nnue_reference.read_nnue(pathlib.Path(d) / "filled.nnue")
        took = {0: 1, 2: 1, 3: 5, 4: 5}   # 3 is as near 1 as 5: the higher
        right = all((f["ow"][k] == q["ow"][took.get(k, k)]).all() and f["ob"][k] == q["ob"][took.get(k, k)]
                    for k in range(count)) and f["qb"] == q["qb"]
        print(f"  untrained buckets 0, 2, 3, 4 of {trained}: exported with buckets 1, 1, 5, 5's rows, the rest as they "
              f"were: " + ("yes" if right else "NO"))
        ok &= right

        same = 0
        one = dict(state, ow=state["ow"][0], ob=state["ob"][0])
        train.export(one, pathlib.Path(d) / "v1.nnue")
        v1 = nnue_reference.read_nnue(pathlib.Path(d) / "v1.nnue")
        reference = [nnue_reference.evaluate(v1, f) for f in fens]
        for k in (1, count):
            rows = dict(state, ow=np.repeat(state["ow"][:1], k, 0), ob=np.repeat(state["ob"][:1], k, 0))
            train.export(rows, pathlib.Path(d) / f"same{k}.nnue")
            v3 = nnue_reference.read_nnue(pathlib.Path(d) / f"same{k}.nnue")
            got = [nnue_reference.evaluate(v3, f) for f in fens]
            gpu = train.int_scores(pathlib.Path(d) / f"same{k}.nnue", raw).numpy()
            by_reference = sum(a != b for a, b in zip(got, reference))
            by_trainer = int((gpu != np.array(reference)).sum())
            same += by_reference + by_trainer
            print(f"  version 3 with K = {v3['buckets']}, every row bucket 0's (QB {v3['qb']}, version 1's {v1['qb']}): "
                  f"against version 1's scores {by_reference:,} differ (nnue_reference), {by_trainer:,} "
                  f"(train.int_scores)")
        ok &= same == 0
    print("buckets: " + ("all equal" if ok else "DIFFER"))
    return 0 if ok else 1


def engine_evals(engine, eval_file, fens):
    lines = ["uci", f"setoption name EvalFile value {eval_file}", "isready"]
    for fen in fens:
        lines += [f"position fen {fen}", "eval"]
    lines.append("quit")
    out = subprocess.run([str(engine)], input="\n".join(lines) + "\n", capture_output=True, text=True, timeout=600)
    evals = [int(line.split()[1]) for line in out.stdout.splitlines() if line.startswith("eval ")]
    said = [line for line in out.stdout.splitlines() if line.startswith("info string")]
    if len(evals) != len(fens):
        raise RuntimeError(f"{len(evals)} evals for {len(fens)} positions; engine said {said}; stderr {out.stderr[-500:]}")
    return np.array(evals, np.int64), said


def compare(name, mine, theirs, fens):
    differ = np.nonzero(mine != theirs)[0]
    print(f"{len(fens):,} positions: {len(differ):,} differ (scores {theirs.min()} .. {theirs.max()}, "
          f"median |score| {int(np.median(np.abs(theirs)))})")
    for i in differ[:5]:
        print(f"  {fens[i]}: engine {theirs[i]}, {name} {mine[i]}")
    print("engine: " + ("all equal" if len(differ) == 0 else "DIFFER"))
    return 0 if len(differ) == 0 else 1


def engine(net_path, exe, n=1_000):
    r = sample(n)
    fens = [records.to_fen(x) for x in r]
    theirs, said = engine_evals(exe, net_path, fens)
    print("  " + "; ".join(said))
    net = nnue_reference.read_nnue(net_path)
    mine = np.array([nnue_reference.evaluate(net, f) for f in fens], np.int64)
    return compare("nnue_reference", mine, theirs, fens)


def numbers(numbers_path, exe, n=1_000):
    r = sample(n)
    fens = [records.to_fen(x) for x in r]
    theirs, said = engine_evals(exe, numbers_path, fens)
    print("  " + "; ".join(said))
    white = train.numbers_eval(records.boards(r), train.load_numbers(numbers_path))
    mine = np.where(records.black_to_move(r), -white, white)
    return compare("train.numbers_eval", mine, theirs, fens)


def main(args):
    if args and args[0] == "features":
        return features(*(int(a) for a in args[1:]))
    if args and args[0] == "targets":
        return targets(*(int(a) for a in args[1:]))
    if args and args[0] == "buckets":
        return buckets(*(int(a) for a in args[1:]))
    if len(args) >= 2 and args[0] == "quantised":
        return quantised(args[1], *(int(a) for a in args[2:]))
    if len(args) >= 3 and args[0] == "engine":
        return engine(args[1], args[2], *(int(a) for a in args[3:]))
    if len(args) >= 3 and args[0] == "numbers":
        return numbers(args[1], args[2], *(int(a) for a in args[3:]))
    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
