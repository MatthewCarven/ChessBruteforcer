"""Mutation battery for version 3 networks (NNUE.md, session 7: output buckets). Each mutant is one text change in the
version 3 code (Network.cs, and the two callers that pick the bucket); the network tests run (dotnet test, NnueTests,
NnueDeepTests and NnueBucketTests); red = some test failed or the run timed out. A mutant that doesn't compile is
"broken", not red: it proves nothing about the tests. The clean sources run first, for the test count every run is
held to (a filter that matched fewer tests would pass for the wrong reason); the source is restored after each mutant,
whatever happens, and the build checked clean at the end. Every mutant here must go red.

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
stack = os.path.join(nnue, "AccumulatorStack.cs")
evaluation = os.path.join("src", "ChessBruteforcer.Core", "Engine", "Evaluation.cs")
FILTER = "FullyQualifiedName~NnueBucketTests|FullyQualifiedName~NnueDeepTests|FullyQualifiedName~NnueTests"

# (name, file, old, new)
MUTANTS = [
    # Which bucket a board takes.
    ("bucket: n, not n - 1", net,
     "Math.Min(Math.Max(n - 1, 0) * Buckets / 32, Buckets - 1)", "Math.Min(n * Buckets / 32, Buckets - 1)"),
    ("bucket: over 33, not 32", net,
     "Math.Min(Math.Max(n - 1, 0) * Buckets / 32, Buckets - 1)", "Math.Min(Math.Max(n - 1, 0) * Buckets / 33, Buckets - 1)"),
    ("bucket: no clamp past 32 pieces", net,
     "(byte)Math.Min(Math.Max(n - 1, 0) * Buckets / 32, Buckets - 1)", "(byte)(Math.Max(n - 1, 0) * Buckets / 32)"),
    ("bucket: the table read one along", net,
     "internal int Bucket(int pieces) => _bucketOf[pieces];", "internal int Bucket(int pieces) => _bucketOf[Math.Min(pieces + 1, 64)];"),
    ("evaluation: one side's pieces counted", evaluation,
     "BitOperations.PopCount(position.Occupied)", "BitOperations.PopCount(position.Pieces(Colour.White))"),
    ("evaluation: the count one short", evaluation,
     "BitOperations.PopCount(position.Occupied)", "BitOperations.PopCount(position.Occupied) - 1"),
    ("stack: the bucket ignored", stack,
     "        int bucket = Network.Bucket(pieces);\n", "        int bucket = 0;\n"),
    ("stack: black to move on bucket 0", stack,
     "            : Network.Forward(slot[_hidden..], slot[.._hidden], bucket);",
     "            : Network.Forward(slot[_hidden..], slot[.._hidden], 0);"),
    ("refresh: Network.Evaluate on bucket 0", net,
     "        int bucket = Bucket(BitOperations.PopCount(position.Occupied));\n", "        int bucket = 0;\n"),
    # The passes.
    ("dispatch: 512 bits on bucket 0", net,
     "            512 => Forward512(us, them, bucket),\n", "            512 => Forward512(us, them, 0),\n"),
    ("dispatch: 256 bits on bucket 0", net,
     "            256 => Forward256(us, them, bucket),\n", "            256 => Forward256(us, them, 0),\n"),
    ("dispatch: one at a time on bucket 0", net,
     "            _ => ForwardScalar(us, them, bucket),\n", "            _ => ForwardScalar(us, them, 0),\n"),
    ("256 bits: bucket 0's row", net,
     "    internal int Forward256(ReadOnlySpan<short> us, ReadOnlySpan<short> them, int bucket)\n    {\n        CheckAccumulators(us, them);\n        ref short ow = ref MemoryMarshal.GetReference(BucketWeights(bucket));\n",
     "    internal int Forward256(ReadOnlySpan<short> us, ReadOnlySpan<short> them, int bucket)\n    {\n        CheckAccumulators(us, them);\n        ref short ow = ref MemoryMarshal.GetReference(BucketWeights(0));\n"),
    ("512 bits: bucket 0's row", net,
     "    internal int Forward512(ReadOnlySpan<short> us, ReadOnlySpan<short> them, int bucket)\n    {\n        CheckAccumulators(us, them);\n        ref short ow = ref MemoryMarshal.GetReference(BucketWeights(bucket));\n",
     "    internal int Forward512(ReadOnlySpan<short> us, ReadOnlySpan<short> them, int bucket)\n    {\n        CheckAccumulators(us, them);\n        ref short ow = ref MemoryMarshal.GetReference(BucketWeights(0));\n"),
    ("one at a time: bucket 0's row", net,
     "        var weights = BucketWeights(bucket);\n        long sum = 0;\n", "        var weights = BucketWeights(0);\n        long sum = 0;\n"),
    ("256 bits: bucket 0's bias", net,
     "                 + Half256(ref MemoryMarshal.GetReference(them), ref Unsafe.Add(ref ow, Hidden), 0);\n        return Finish(sum, bucket);",
     "                 + Half256(ref MemoryMarshal.GetReference(them), ref Unsafe.Add(ref ow, Hidden), 0);\n        return Finish(sum, 0);"),
    ("finish: bucket 0's bias", net,
     "(sum / QA + OutputBiases[bucket])", "(sum / QA + OutputBiases[0])"),
    ("rows: a bucket H apart, not 2H", net,
     "_outputWeights.AsSpan(_outputWeightsAt + bucket * 2 * Hidden, 2 * Hidden)", "_outputWeights.AsSpan(_outputWeightsAt + bucket * Hidden, 2 * Hidden)"),
    # The loader and its checks.
    ("loader: K read, one bucket kept", net,
     "            buckets = (int)k;\n", "            buckets = 1;\n"),
    ("loader: no upper limit on K", net,
     "            if (k is < 1 or > MaxBuckets)\n", "            if (k is < 1)\n"),
    ("loader: no lower limit on K", net,
     "            if (k is < 1 or > MaxBuckets)\n", "            if (k is > MaxBuckets)\n"),
    ("loader: every bias read from the first's place", net,
     "            outputBiases[b] = BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 4 * b)..]);\n",
     "            outputBiases[b] = BinaryPrimitives.ReadInt32LittleEndian(bytes[at..]);\n"),
    ("loader: K's four bytes not skipped", net,
     "            buckets = (int)k;\n            at += 4;\n", "            buckets = (int)k;\n"),
    ("loader: a version 3 net called version 1", net,
     "        var network = new Network((int)version, h, qa, qb, scale,", "        var network = new Network(Version, h, qa, qb, scale,"),
    ("check: only the first bucket's weights", net,
     "        for (int i = 0; i < weights.Length; i++)\n            if (Math.Abs(weights[i]) * QA > short.MaxValue)",
     "        for (int i = 0; i < 2 * Hidden; i++)\n            if (Math.Abs(weights[i]) * QA > short.MaxValue)"),
    # The writer.
    ("writer: K left out of the header", net,
     "            BinaryPrimitives.WriteUInt32LittleEndian(span[HeaderBytes..], (uint)k);\n", ""),
    ("writer: version 1 in the header", net,
     "        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)FileVersion);\n", "        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], Version);\n"),
    ("writer: every bias the first's", net,
     "            BinaryPrimitives.WriteInt32LittleEndian(span[at..], bias);\n", "            BinaryPrimitives.WriteInt32LittleEndian(span[at..], OutputBias);\n"),
    ("writer: the weights over K's place", net,
     "at = FileVersion == BucketVersion ? HeaderBytes + 4 : HeaderBytes;", "at = HeaderBytes;"),
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
    if body.count(old) != 1:
        sys.exit(f"mutant {index} ({name}): expected one of\n{old}\nfound {body.count(old)}")
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
