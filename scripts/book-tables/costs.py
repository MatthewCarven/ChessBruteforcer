"""Tree sizes and CPU-hours for margin books, from the measured near-best counts (cloud, geometric means) and
search times (depth 14 = 11.4 s on one thread, 2.2x a ply: WORKLOG p2.1 and the learner's re-score)."""
K = {"20 cp": 2.65, "30 cp": 3.41, "50 cp": 4.0}
SEARCH = {d: 11.4 * 2.2 ** (d - 14) for d in (10, 12, 14, 16, 18, 20)}
print("search seconds:", {d: round(s, 1) for d, s in SEARCH.items()})


def ours(k, plies):
    """Our positions (one search each), both colours: White moves at 0, 2, ...; Black at 1, 3, ..."""
    n = plies // 2
    white = sum(k ** i for i in range(n))
    black = sum(k ** i for i in range(1, n + 1))
    return white + black


print("| margin | plies | both sides within margin: positions at the last ply (one colour) | our best, theirs within margin: our positions (both colours) | core-hours at depth 14 / 16 / 18 |")
for name, k in K.items():
    for plies in (10, 14, 20):
        both = k ** plies
        o = ours(k, plies)
        hours = " / ".join(f"{o * SEARCH[d] / 3600:,.0f}" for d in (14, 16, 18))
        print(f"| {name} (k {k}) | {plies} | {both:,.3g} | {o:,.0f} | {hours} |")
for name, n in (("5-ply table, their every move (T=3)", 7374), ("5-ply table, their every move (T=1)", 5050)):
    print(name, " / ".join(f"d{d} {n * SEARCH[d] / 3600:,.0f} core-h" for d in (14, 16, 18)))
for name, n in (("5-ply full minimax, leaves (T=3)", 715940), ("5-ply full minimax, leaves (none blocked)", 822518)):
    print(name, " / ".join(f"d{d} {n * SEARCH[d] / 3600:,.0f} core-h" for d in (10, 12, 14)))
yrs = 85375278064 * SEARCH[10] / 3600 / 24 / 365
print(f"10 plies, every position, depth 10: {yrs:,.0f} core-years; 1.37 TB at 16 bytes")
