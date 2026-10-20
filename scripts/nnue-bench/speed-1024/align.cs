#:project ../../../src/ChessBruteforcer.Core/ChessBruteforcer.Core.csproj
#:property TargetFramework=net8.0
#:property PublishAot=false
// Where the engine's own arrays land: the feature weights, the output weights and a position's accumulator
// stack, in bytes past a 64-byte boundary (by reflection on the unmodified engine).
//   dotnet run -c Release align.cs <net .nnue>
using System.Reflection;
using System.Runtime.InteropServices;
using ChessBruteforcer.Core.Engine.Nnue;
using ChessBruteforcer.Core.Game;

var net = Network.Load(args[0]);
const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
long Offset(short[] array)
{
    var handle = GCHandle.Alloc(array, GCHandleType.Pinned);
    long at = (long)handle.AddrOfPinnedObject() % 64;
    handle.Free();
    return at;
}
var weights = (short[])typeof(Network).GetProperty("FeatureWeights", Any)!.GetValue(net)!;
var outputs = (short[])typeof(Network).GetProperty("OutputWeights", Any)!.GetValue(net)!;
var stacks = Enumerable.Range(0, 8).Select(_ =>
{
    var position = Position.FromFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", net);
    var stack = typeof(Position).GetProperty("Accumulators", Any)!.GetValue(position)!;
    var data = (short[])stack.GetType().GetField("_data", Any)!.GetValue(stack)!;
    return Offset(data);
}).ToArray();
Console.WriteLine($"{net}: feature weights at {Offset(weights)}, output weights at {Offset(outputs)}, eight positions' stacks at {string.Join(' ', stacks)} bytes past a 64-byte boundary");
