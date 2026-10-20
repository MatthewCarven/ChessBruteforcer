"""Mutation battery for version 2 networks (NNUE.md, session 6: the deeper nets). Each mutant is one text change in
Network.cs's version 2 code; the network tests run (dotnet test, NnueTests and NnueDeepTests); red = some test failed
or the run timed out. The source is restored after each mutant, whatever happens, and the build checked clean at the
end. Every mutant here must go red.

    python battery.py <repo> [first] [last]
"""
import ctypes
import os
import subprocess
import sys
import time

repo = sys.argv[1]
first = int(sys.argv[2]) if len(sys.argv) > 2 else 0
last = int(sys.argv[3]) if len(sys.argv) > 3 else 999
net = os.path.join("src", "ChessBruteforcer.Core", "Engine", "Nnue", "Network.cs")
FILTER = "FullyQualifiedName~NnueDeepTests|FullyQualifiedName~NnueTests"

# (name, file, old, new, expected: "red", or "equivalent" for a change no behaviour can show)
MUTANTS = [
    ("activation: no rounding in the SIMD pass", net,
     "Avx2.MultiplyHigh(v * v + half, magic)", "Avx2.MultiplyHigh(v * v, magic)", "red"),
    ("activation: the SIMD pass shifts 8, not 7", net,
     "Avx2.MultiplyHigh(v * v + half, magic), 7)", "Avx2.MultiplyHigh(v * v + half, magic), 8)", "red"),
    ("activation: no rounding one at a time", net,
     "            x[i] = (short)((v * v + QA / 2) / QA);\n", "            x[i] = (short)((v * v) / QA);\n", "red"),
    ("activation: the perspectives swapped", net,
     "        Activate(width, us[..h], x[..h]);\n        Activate(width, them[..h], x[h..]);\n",
     "        Activate(width, them[..h], x[..h]);\n        Activate(width, us[..h], x[h..]);\n", "red"),
    ("layers: no half step", net,
     "            int half = layer.Q >> 1;\n", "            int half = 0;\n", "red"),
    ("layers: the shift one too far", net,
     "outputs[j] = (short)Math.Clamp(s >> layer.Shift, 0, QA);",
     "outputs[j] = (short)Math.Clamp(s >> (layer.Shift + 1), 0, QA);", "red"),
    ("layers: no clamp at QA", net,
     "outputs[j] = (short)Math.Clamp(s >> layer.Shift, 0, QA);",
     "outputs[j] = (short)Math.Max(s >> layer.Shift, 0);", "red"),
    ("dot: the 512-bit part dropped", net,
     "            sum += Vector256.Sum(lanes);\n", "            sum = Vector256.Sum(lanes);\n", "red"),
    ("dot: a tail's last input left out", net,
     "        for (; i < n; i++)\n            sum += x[i] * w[i];\n        return sum;",
     "        for (; i < n - 1; i++)\n            sum += x[i] * w[i];\n        return sum;", "red"),
    ("output: the score's scale halved", net,
     "long score = total * Scale / ((long)QA * Output!.Q);",
     "long score = total * Scale / 2 / ((long)QA * Output!.Q);", "red"),
    ("dispatch: one-layer nets take version 1's pass", net,
     "internal int Forward(ReadOnlySpan<short> us, ReadOnlySpan<short> them) => Layers.Length != 0",
     "internal int Forward(ReadOnlySpan<short> us, ReadOnlySpan<short> them) => Layers.Length > 1", "red"),
    ("loader: a layer's rows read as columns", net,
     "            return new Layer(from, to, q, weights, ReadInts(source, to));",
     "            return new Layer(to, from, q, weights, ReadInts(source, to));", "red"),
    ("loader: no upper limit on a layer's size", net,
     "            if (size is < 1 or > MaxLayerSize)\n", "            if (size is < 1)\n", "red"),
    ("loader: Q need not be a power of two", net,
     "            if (q < 1 || q > MaxLayerQ || (q & (q - 1)) != 0)\n", "            if (q < 1 || q > MaxLayerQ)\n", "red"),
    ("loader: the row check without the half step", net,
     "                long most = Math.Abs((long)layer.Biases[j]) + half;\n",
     "                long most = Math.Abs((long)layer.Biases[j]);\n", "red"),
    ("loader: the row check skips the output", net,
     ".Append((Output!, \"the output\", 0))", "", "red"),
    ("writer: the sizes written as inputs", net,
     "            U32((uint)layer.Outputs);\n", "            U32((uint)layer.Inputs);\n", "red"),
    # The pass by listed pairs (the non-zero pairs listed first, then added two at a time).
    ("list: a bit's position one too far", net,
     "                    table[bits * 8 + k++] = (ushort)bit;\n", "                    table[bits * 8 + k++] = (ushort)(bit + 1);\n", "red"),
    ("list: each byte's count one short", net,
     "        return count + BitOperations.PopCount(bits);\n",
     "        return count + BitOperations.PopCount(bits) - (bits == 0 ? 0 : 1);\n", "red"),
    ("list: the high byte of a 512-bit mask dropped", net,
     "                count = Append(ref into, count, ref table, mask >> 8, p + 8);\n", "", "red"),
    ("list: the 512-bit zero test inverted", net,
     "uint mask = ~(uint)Vector512.Equals(", "uint mask = (uint)Vector512.Equals(", "red"),
    ("listed pairs, 32 at 512 bits: the second pair's sums thrown away", net,
     "                Avx512F.ConvertToVector256Int16(Vector512.Min(Vector512.Max(Vector512.ShiftRightArithmetic(s0 + t0, shift), zero), qa)).StoreUnsafe(ref to);\n"
     "                Avx512F.ConvertToVector256Int16(Vector512.Min(Vector512.Max(Vector512.ShiftRightArithmetic(s1 + t1, shift), zero), qa)).StoreUnsafe(ref to, 16);\n",
     "                Avx512F.ConvertToVector256Int16(Vector512.Min(Vector512.Max(Vector512.ShiftRightArithmetic(s0, shift), zero), qa)).StoreUnsafe(ref to);\n"
     "                Avx512F.ConvertToVector256Int16(Vector512.Min(Vector512.Max(Vector512.ShiftRightArithmetic(s1, shift), zero), qa)).StoreUnsafe(ref to, 16);\n", "red"),
    ("listed pairs, 32 at 512 bits: an odd count's last pair left out", net,
     "                if (i < count)\n                {\n                    int p = Unsafe.Add(ref at, i);\n                    var bp = ",
     "                if (false)\n                {\n                    int p = Unsafe.Add(ref at, i);\n                    var bp = ", "red"),
    ("listed pairs, 16 at 512 bits: an odd count's last pair left out", net,
     "                if (i < count)\n                {\n                    int p = Unsafe.Add(ref at, i);\n                    s0 += ",
     "                if (false)\n                {\n                    int p = Unsafe.Add(ref at, i);\n                    s0 += ", "red"),
    ("listed pairs at 256 bits: outputs 16-31 never added", net,
     "                if (two)\n                {\n                    s2 += ", "                if (false)\n                {\n                    s2 += ", "red"),
    ("activation at 512 bits: no rounding", net,
     "Avx512BW.MultiplyHigh(v * v + half, magic)", "Avx512BW.MultiplyHigh(v * v, magic)", "red"),
    ("pairs: a weight laid out at the wrong input of its pair", net,
     "                    pairs[(i / 2 * PaddedOutputs + j) * 2 + (i & 1)] = weights[j * inputs + i];\n",
     "                    pairs[(i / 2 * PaddedOutputs + j) * 2 + (1 - (i & 1))] = weights[j * inputs + i];\n", "red"),
    ("pairs: the starting sums without the half step", net,
     "                StartingSums[j] = biases[j] + q / 2;\n", "                StartingSums[j] = biases[j];\n", "red"),
    ("pairs: more than 32 outputs through memory, at 512 bits, reads 16 too few", net,
     "            int vectors = n / 16;\n            for (int v = 0; v < vectors; v++)\n                sums[v] = Vector512.LoadUnsafe",
     "            int vectors = n / 16 - 1;\n            for (int v = 0; v < vectors; v++)\n                sums[v] = Vector512.LoadUnsafe", "red"),
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
    # A guard: a build beside the bot wants headroom (the notebook's 2 GB floor, plus the build's own).
    while free_commit_mb() < 2600:
        print(f"  waiting for commit ({free_commit_mb()} MB free)", flush=True)
        time.sleep(30)
    started = time.time()
    try:
        result = subprocess.run(
            ["dotnet", "test", os.path.join(repo, "tests", "ChessBruteforcer.Tests"), "-c", "Release",
             "--disable-build-servers", "-p:UseSharedCompilation=false", "--filter", FILTER, "-v", "q", "--nologo"],
            capture_output=True, text=True, timeout=timeout, stdin=subprocess.DEVNULL)
        out = result.stdout + result.stderr
        tail = [line for line in out.splitlines() if "Passed!" in line or "Failed!" in line or "error" in line.lower()]
        return result.returncode, " | ".join(tail[-2:]), time.time() - started
    except subprocess.TimeoutExpired:
        return "timeout", "", time.time() - started


results = []
for index, (name, rel, old, new, expected) in enumerate(MUTANTS):
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
        code, tail, seconds = run_tests(timeout=300)
    finally:
        write(rel, original)
    verdict = "green" if code == 0 else "red"
    ok = (verdict == "red") == (expected == "red")
    results.append((index, name, verdict, expected, ok))
    print(f"{index:2} {verdict:5} ({'as expected' if ok else 'NOT AS EXPECTED'}, {expected}) {seconds:5.0f} s  {name}  [{code}] {tail}", flush=True)

# The sources back as they were, and the build of them clean (a battery leaves its last mutant's binaries).
code, tail, seconds = run_tests(timeout=600)
print(f"restored: tests exit {code} {tail}", flush=True)
print(f"{sum(1 for r in results if r[4])} of {len(results)} as expected", flush=True)
