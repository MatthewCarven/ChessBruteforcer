#:project ../../src/ChessBruteforcer.Core/ChessBruteforcer.Core.csproj
#:property TargetFramework=net8.0
#:property PublishAot=false
// Writes a 768 -> H x2 -> 1 network that computes an evaluation file's material and piece-square values
// (Network.FromPieceSquares): the yardstick for `bench`, since its search tree looks like a real
// evaluation's, while random weights make the capture search explode.
//   dotnet run -c Release pst-net.cs <numbers .txt> <out .nnue> [hidden]
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Engine.Nnue;

var numbers = EvalParams.Load(args[0]);
int hidden = args.Length > 2 ? int.Parse(args[2]) : 256;
var network = Network.FromPieceSquares(numbers, hidden);
network.Save(args[1]);
Console.WriteLine($"{args[1]}: {network} from {args[0]}");
