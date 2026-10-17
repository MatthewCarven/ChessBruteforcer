"""The trainer's checks before a long run (NNUE.md, "Session 2"). Run with the venv's python.

    python scripts/nnue/checks.py features [n]                    the GPU's unpacking equals nnue_reference.feature
    python scripts/nnue/checks.py targets [n]                     the training targets (--result-blend, --mates), on the CPU
    python scripts/nnue/checks.py quantised <run .pt> [n]         the float net and its int16 export agree (centipawns)
    python scripts/nnue/checks.py engine <net .nnue> <engine exe> [n]       the engine's eval equals nnue_reference's
    python scripts/nnue/checks.py numbers <numbers .txt> <engine exe> [n]   the engine's eval equals train.numbers_eval

Positions come from September's held-back.bin (mates left out, as in training), n of them (10,000 for features,
1,000 otherwise). The engine is driven through UCI: EvalFile, then "position fen" and "eval" for each.
"""
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
    saved = torch.load(checkpoint, map_location="cuda", weights_only=False)
    net = train.make_net(saved["hidden"]).cuda()
    net.load_state_dict({k: torch.as_tensor(v).cuda() for k, v in saved["state"].items()})
    r = sample(n)
    raw = torch.from_numpy(np.ascontiguousarray(r).view(np.uint8).reshape(-1, records.SIZE)).cuda()
    floats = train.scores(net, raw).cpu().numpy().astype(np.float64)
    with tempfile.TemporaryDirectory() as d:
        path = pathlib.Path(d) / "q.nnue"
        _, qb = train.export(saved["state"], path)
        q = nnue_reference.read_nnue(path)
        ints = np.array([nnue_reference.evaluate(q, records.to_fen(x)) for x in r], np.float64)
        gpu = train.int_scores(path, raw).cpu().numpy().astype(np.float64)
    signed = ints - floats
    diff = np.abs(signed)
    print(f"{n:,} positions, exported at QA {train.QA}, QB {qb}: int16 - float signed mean {signed.mean():+.2f} cp; "
          f"|int16 - float| mean {diff.mean():.2f}, median {np.median(diff):.2f}, 95th {np.percentile(diff, 95):.2f}, "
          f"max {diff.max():.2f} (scores: median |float| {np.median(np.abs(floats)):.0f} cp)")
    worst = int(diff.argmax())
    print(f"  worst: {records.to_fen(r[worst])}  float {floats[worst]:.2f}, int16 {ints[worst]:.0f}")
    differ = int((gpu != ints).sum())
    print(f"train.int_scores (the GPU's integer pass, for held-back error) against nnue_reference.evaluate: "
          f"{differ:,} of {n:,} differ")
    return 0 if differ == 0 else 1


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
