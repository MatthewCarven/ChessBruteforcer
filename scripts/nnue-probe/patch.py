import pathlib, shutil

base = pathlib.Path(__file__).parent
core = base / "tree" / "src" / "ChessBruteforcer.Core"

shutil.copy(base / "Probe.cs", core / "Engine" / "Probe.cs")

def patch(path, old, new):
    b = path.read_bytes().replace(b"\r\n", b"\n")
    s = b.decode("utf-8")
    assert s.count(old) == 1, (path, old)
    s = s.replace(old, new)
    path.write_bytes(s.encode("utf-8"))

patch(core / "Engine" / "Evaluation.cs",
      "    public static int Evaluate(Position position) => Evaluate(position, Parameters);\n",
      "    public static int Evaluate(Position position)\n"
      "    {\n"
      "        int v = Evaluate(position, Parameters);\n"
      "        Probe.Evals++;\n"
      "        if (Probe.Mode != 0) Probe.Extra(position);\n"
      "        return v;\n"
      "    }\n")

patch(core / "Game" / "Position.cs",
      "        _squares[square] = piece;\n",
      "        _squares[square] = piece;\n"
      "        if (ChessBruteforcer.Core.Engine.Probe.Mode >= 2) ChessBruteforcer.Core.Engine.Probe.OnPut(this, old, piece, square);\n")
print("patched")
