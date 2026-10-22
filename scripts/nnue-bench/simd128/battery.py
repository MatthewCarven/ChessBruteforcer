"""Mutation battery for the 128-bit path (NNUE.md, "128 bits for CPUs without AVX2"). Each mutant is one text change in
the 128-bit code (Kernels.cs, Network.cs) or in the choice of width; the network tests run (dotnet test, NnueTests,
NnueDeepTests and NnueBucketTests); red = some test failed or the run timed out. A mutant that doesn't compile is
"broken", not red: it proves nothing about the tests. The clean sources run first, for the test count every run is
held to (a filter that matched fewer tests would pass for the wrong reason); the source is restored after each mutant,
whatever happens, and the build checked clean at the end. Every mutant here must go red.

Left out as equivalent (no result can show them, only speed): 128 bits sent to the scalar pass by the dispatch or
ForwardDeep; Dot's 128-bit part with its sum assigned rather than added (nothing comes before it at 128 bits); the
pair list naming extra zero pairs (a zero pair adds 0). And no mutant moves a store past its span: an unchecked
write past an array could corrupt the test host rather than fail a test.

    python battery.py <repo> [first] [last]
"""
import ctypes
import os
import re
import subprocess
import sys
import time

repo = sys.argv[1]
first = int(sys.argv[2]) if len(sys.argv) > 2 else 0
last = int(sys.argv[3]) if len(sys.argv) > 3 else 999
nnue = os.path.join("src", "ChessBruteforcer.Core", "Engine", "Nnue")
net = os.path.join(nnue, "Network.cs")
kernels = os.path.join(nnue, "Kernels.cs")
FILTER = "FullyQualifiedName~NnueBucketTests|FullyQualifiedName~NnueDeepTests|FullyQualifiedName~NnueTests"

# (name, file, old, new)
MUTANTS = [
    # The width.
    ("widest: SSE2 alone runs one at a time", kernels,
     "sse2 ? 128 : 0", "sse2 ? 0 : 0"),
    ("choose: 128 not a width CB_NNUE_SIMD can ask for", kernels,
     "width is 0 or 128 or 256 or 512", "width is 0 or 256 or 512"),
    # The accumulator's kernels at 128 bits.
    ("add: every other 8 skipped", kernels,
     "            for (; i < n; i += 8)\n                (Vector128.LoadUnsafe(ref d, (nuint)i) + Vector128.LoadUnsafe(ref x, (nuint)i)).StoreUnsafe",
     "            for (; i < n; i += 16)\n                (Vector128.LoadUnsafe(ref d, (nuint)i) + Vector128.LoadUnsafe(ref x, (nuint)i)).StoreUnsafe"),
    ("add-sub: the subtracted column added", kernels,
     "                 - Vector128.LoadUnsafe(ref y, (nuint)i)).StoreUnsafe", "                 + Vector128.LoadUnsafe(ref y, (nuint)i)).StoreUnsafe"),
    ("add-sub-sub: the second subtracted column added", kernels,
     "Vector128.LoadUnsafe(ref x, (nuint)i)\n                 - Vector128.LoadUnsafe(ref y1, (nuint)i) - Vector128.LoadUnsafe(ref y2, (nuint)i)",
     "Vector128.LoadUnsafe(ref x, (nuint)i)\n                 - Vector128.LoadUnsafe(ref y1, (nuint)i) + Vector128.LoadUnsafe(ref y2, (nuint)i)"),
    ("add-add-sub-sub: the second added column subtracted", kernels,
     "+ Vector128.LoadUnsafe(ref x2, (nuint)i)", "- Vector128.LoadUnsafe(ref x2, (nuint)i)"),
    # Versions 1 and 3: the pass.
    ("dispatch: 128 bits on bucket 0", net,
     "            128 => Forward128(us, them, bucket),\n", "            128 => Forward128(us, them, 0),\n"),
    ("128 bits: bucket 0's row", net,
     "        ref short ow = ref MemoryMarshal.GetReference(BucketWeights(bucket));\n        long sum = Half128(",
     "        ref short ow = ref MemoryMarshal.GetReference(BucketWeights(0));\n        long sum = Half128("),
    ("128 bits: bucket 0's bias", net,
     "                 + Half128(ref MemoryMarshal.GetReference(them), ref Unsafe.Add(ref ow, Hidden));\n        return Finish(sum, bucket);",
     "                 + Half128(ref MemoryMarshal.GetReference(them), ref Unsafe.Add(ref ow, Hidden));\n        return Finish(sum, 0);"),
    ("128 bits: their half on our weights", net,
     "                 + Half128(ref MemoryMarshal.GetReference(them), ref Unsafe.Add(ref ow, Hidden));",
     "                 + Half128(ref MemoryMarshal.GetReference(them), ref ow);"),
    ("128 bits: one set of lanes (H / 4 products to a lane)", net,
     "            second += Sse2.MultiplyAddAdjacent(u * ", "            first += Sse2.MultiplyAddAdjacent(u * "),
    ("128 bits: the second 8 on the first 8's weights", net,
     "            second += Sse2.MultiplyAddAdjacent(u * Vector128.LoadUnsafe(ref weights, (nuint)(i + 8)), u);",
     "            second += Sse2.MultiplyAddAdjacent(u * Vector128.LoadUnsafe(ref weights, (nuint)i), u);"),
    ("128 bits: no clamp at QA", net,
     "            var v = Vector128.Min(Vector128.Max(Vector128.LoadUnsafe(ref accumulator, (nuint)i), zero), qa);",
     "            var v = Vector128.Max(Vector128.LoadUnsafe(ref accumulator, (nuint)i), zero);"),
    ("128 bits: no clamp at 0", net,
     "            var u = Vector128.Min(Vector128.Max(Vector128.LoadUnsafe(ref accumulator, (nuint)(i + 8)), zero), qa);",
     "            var u = Vector128.Min(Vector128.LoadUnsafe(ref accumulator, (nuint)(i + 8)), qa);"),
    ("128 bits: a widened half left out of the sum", net,
     "        return Vector128.Sum(a + b + c + d);", "        return Vector128.Sum(a + b + c);"),
    # Version 2 at 128 bits.
    ("activation: no rounding", net,
     "Sse2.MultiplyHigh(v * v + half, magic)", "Sse2.MultiplyHigh(v * v, magic)"),
    ("activation: shifts 8, not 7", net,
     "Vector128.ShiftRightLogical(Sse2.MultiplyHigh(v * v + half, magic), 7)", "Vector128.ShiftRightLogical(Sse2.MultiplyHigh(v * v + half, magic), 8)"),
    ("dot: the inputs times themselves", net,
     "lanes += Sse2.MultiplyAddAdjacent(Vector128.LoadUnsafe(ref xs, (nuint)i), Vector128.LoadUnsafe(ref ws, (nuint)i));",
     "lanes += Sse2.MultiplyAddAdjacent(Vector128.LoadUnsafe(ref xs, (nuint)i), Vector128.LoadUnsafe(ref xs, (nuint)i));"),
    ("list: each chunk's upper four pairs dropped", net,
     "count = Append(ref into, count, ref table, ~(low | high << 4) & 0xFF, p);", "count = Append(ref into, count, ref table, ~(low | high << 4) & 0x0F, p);"),
    ("list: the zero test inverted", net,
     "count = Append(ref into, count, ref table, ~(low | high << 4) & 0xFF, p);", "count = Append(ref into, count, ref table, (low | high << 4) & 0xFF, p);"),
    ("listed pairs: outputs 16-31 never added", net,
     "                if (two)\n                {\n                    s4 += ", "                if (false)\n                {\n                    s4 += "),
    ("listed pairs: outputs 12-15 start from 8-11's sums", net,
     "s3 = Vector128.LoadUnsafe(ref starts, 12);", "s3 = Vector128.LoadUnsafe(ref starts, 8);"),
    ("listed pairs: outputs 12-15 on 8-11's weights", net,
     "                s3 += Sse2.MultiplyAddAdjacent(both, Vector128.LoadUnsafe(ref weights, w + 24));",
     "                s3 += Sse2.MultiplyAddAdjacent(both, Vector128.LoadUnsafe(ref weights, w + 16));"),
    ("listed pairs: outputs 8-15 never written", net,
     "            Vector128.Narrow(Out(s2), Out(s3)).StoreUnsafe(ref to, 8);\n", ""),
    ("pairs past 32 outputs: the last 4 outputs never summed", net,
     "            int vectors = n / 4;\n", "            int vectors = n / 4 - 1;\n"),
    ("pairs past 32 outputs: the zero test inverted", net,
     "uint mask = (~Vector128.Equals(chunk, Vector128<int>.Zero)).ExtractMostSignificantBits();",
     "uint mask = Vector128.Equals(chunk, Vector128<int>.Zero).ExtractMostSignificantBits();"),
    ("pairs past 32 outputs: the tail's pairs left out", net,
     "            for (; p < pairs; p++)\n                if (Pair(ref xs, p) != 0)\n                    AddPair128(ref xs, ref weights, sums, p, n, vectors);\n", ""),
    ("pairs past 32 outputs: each pair's weights 4 outputs along, not 8", net,
     "            sums[v] += Sse2.MultiplyAddAdjacent(both, Vector128.LoadUnsafe(ref weights, at + (nuint)(8 * v)));",
     "            sums[v] += Sse2.MultiplyAddAdjacent(both, Vector128.LoadUnsafe(ref weights, at + (nuint)(4 * v)));"),
]


def free_commit_mb():
    class Status(ctypes.Structure):
        _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                    ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                    ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                    ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                    ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
    status = Status()
    status.dwLength = ctypes.sizeof(Status)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status))
    return status.ullAvailPageFile // (1024 * 1024)


def read(rel):
    with open(os.path.join(repo, rel), "rb") as f:
        return f.read()


def write(rel, data):
    with open(os.path.join(repo, rel), "wb") as f:
        f.write(data)


def run_tests(timeout):
    """(exit code or "timeout" or "build error", total tests counted or None, summary, seconds)."""
    # A guard: a build beside the bot wants headroom (the notebook's 2 GB floor, plus the build's own).
    while free_commit_mb() < 2600:
        print(f"  waiting for commit ({free_commit_mb()} MB free)", flush=True)
        time.sleep(30)
    started = time.time()
    # Its own process group, killed whole on a timeout (the notebook: a hung test host otherwise holds the pipes).
    process = subprocess.Popen(
        ["dotnet", "test", os.path.join(repo, "tests", "ChessBruteforcer.Tests"), "-c", "Release",
         "--disable-build-servers", "-p:UseSharedCompilation=false", "--filter", FILTER, "-v", "q", "--nologo"],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, text=True,
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
    try:
        out, _ = process.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(process.pid)], capture_output=True)
        process.communicate()
        return "timeout", None, "", time.time() - started
    if re.search(r"error CS\d+", out):
        return "build error", None, re.search(r"error CS\d+[^\n]*", out).group(0)[:200], time.time() - started
    total = re.search(r"Total:\s+(\d+)", out)
    tail = [line.strip() for line in out.splitlines() if "Passed!" in line or "Failed!" in line]
    return process.returncode, int(total.group(1)) if total else None, " | ".join(tail[-1:]), time.time() - started


# Every mutant's text must be in the sources once, before anything runs (a typo shouldn't cost a clean run first).
for index, (name, rel, old, new) in enumerate(MUTANTS):
    found = read(rel).decode("utf-8").replace("\r\n", "\n").count(old)
    if found != 1:
        sys.exit(f"mutant {index} ({name}): expected one of\n{old}\nfound {found}")

code, expected_total, tail, seconds = run_tests(timeout=600)
print(f"clean: exit {code}, {expected_total} tests, {seconds:.0f} s  {tail}", flush=True)
if code != 0 or not expected_total:
    sys.exit("the clean sources must pass first")

results = []
for index, (name, rel, old, new) in enumerate(MUTANTS):
    if index < first or index > last:
        continue
    original = read(rel)
    text = original.decode("utf-8")
    eol = "\r\n" if "\r\n" in text else "\n"
    body = text.replace("\r\n", "\n")
    try:
        write(rel, body.replace(old, new).replace("\n", eol).encode("utf-8"))
        code, total, tail, seconds = run_tests(timeout=420)
    finally:
        write(rel, original)
    verdict = "broken" if code == "build error" else "green" if code == 0 else "red"
    # A red that counted fewer tests than the clean run is a crash or a filter that missed, so it says so.
    counted = "" if total in (None, expected_total) else f" COUNTED {total} OF {expected_total}"
    results.append((index, name, verdict))
    print(f"{index:2} {verdict:6} {seconds:5.0f} s  {name}  [{code}]{counted} {tail}", flush=True)

# The sources back as they were, and the build of them clean (a battery leaves its last mutant's binaries).
code, total, tail, seconds = run_tests(timeout=600)
print(f"restored: exit {code}, {total} tests (clean run {expected_total}) {tail}", flush=True)
print(f"{sum(1 for r in results if r[2] == 'red')} of {len(results)} red", flush=True)
