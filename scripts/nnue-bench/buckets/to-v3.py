"""A version 1 .nnue as version 3 with its one output row and bias in all K buckets: the same scores, for a speed A/B.

    python to-v3.py <in.nnue> <out.nnue> [K]
"""
import hashlib
import struct
import sys

source, target = sys.argv[1], sys.argv[2]
k = int(sys.argv[3]) if len(sys.argv) > 3 else 8
data = open(source, "rb").read()
assert data[:8] == b"CBNNUE01"
version, inputs, hidden = struct.unpack_from("<III", data, 8)
assert version == 1 and inputs == 768, (version, inputs)
assert hashlib.sha256(data[:-32]).digest() == data[-32:]
header = data[8:32]
features = data[32:32 + 2 * (768 * hidden + hidden)]
at = 32 + len(features)
row = data[at:at + 4 * hidden]
bias = data[at + 4 * hidden:at + 4 * hidden + 4]
assert at + 4 * hidden + 4 + 32 == len(data)
body = b"CBNNUE01" + struct.pack("<I", 3) + header[4:] + struct.pack("<I", k) + features + row * k + bias * k
open(target, "wb").write(body + hashlib.sha256(body).digest())
print(f"{target}: hidden {hidden}, {k} buckets, {len(body) + 32:,} bytes, network sha256 {hashlib.sha256(body).hexdigest()[:12]}")
