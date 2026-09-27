using System.Text;
using ChessBruteforcer.Core.Game;
using ChessBruteforcer.Core.Match;

namespace ChessBruteforcer.Core.Records;

/// <summary>
/// PGN in and out.  Reading keeps the tags and the main line; comments,
/// variations, NAGs ($1) and "%" escape lines are skipped.  Writing puts the
/// tags back in the order they came, then the moves numbered and wrapped at
/// 79 columns, so a PGN this writer made reads back and writes out unchanged.
/// </summary>
public static class Pgn
{
    public static IEnumerable<StoredGame> ReadFile(string path)
    {
        using var reader = new StreamReader(path);
        foreach (var game in Read(reader))
            yield return game;
    }

    public static List<StoredGame> Parse(string text) => Read(new StringReader(text)).ToList();

    /// <summary>Stream games one at a time, so a file can be bigger than memory.</summary>
    public static IEnumerable<StoredGame> Read(TextReader reader)
    {
        var lexer = new Lexer(reader);
        for (int number = 1; ; number++)
        {
            var game = ReadGame(lexer, number);
            if (game is null)
                yield break;
            yield return game;
        }
    }

    private static StoredGame? ReadGame(Lexer lexer, int number)
    {
        var tags = new List<(string Name, string Value)>();
        var token = lexer.Next();
        while (token.Kind == TokenKind.Tag)
        {
            tags.Add((token.Name, token.Text));
            token = lexer.Next();
        }
        if (token.Kind == TokenKind.End && tags.Count == 0)
            return null;

        string Context() =>
            $"game {number} ({tags.FirstOrDefault(t => t.Name == "White").Value ?? "?"} v " +
            $"{tags.FirstOrDefault(t => t.Name == "Black").Value ?? "?"})";

        string start = tags.FirstOrDefault(t => t.Name == "FEN").Value ?? Fen.StartPosition;
        Position position;
        try
        {
            position = Position.FromFen(start);
        }
        catch (FormatException e)
        {
            throw new FormatException($"{Context()}: bad FEN tag: {e.Message}");
        }

        var moves = new List<Move>();
        string? result = null;
        while (result is null)
        {
            switch (token.Kind)
            {
                case TokenKind.Result:
                    result = token.Text;
                    continue;
                case TokenKind.End:
                case TokenKind.Tag:
                    // No result after the moves: the file ended, or the next game began.
                    if (token.Kind == TokenKind.Tag)
                        lexer.PushBack(token);
                    string? tagged = tags.FirstOrDefault(t => t.Name == "Result").Value;
                    result = tagged is not null && StoredGame.Results.Contains(tagged) ? tagged : "*";
                    continue;
            }

            string word = StripMoveNumber(token.Text);
            if (word.Length > 0 && word.Trim('!', '?').Length > 0 && word != "e.p.")
            {
                Move move;
                try
                {
                    move = San.Parse(position, word);
                }
                catch (FormatException e)
                {
                    throw new FormatException($"{Context()}, move {position.FullmoveNumber}: {e.Message}");
                }
                position.MakeMove(move);
                moves.Add(move);
            }
            token = lexer.Next();
        }
        return new StoredGame(tags, moves, result);
    }

    /// <summary>"12." or "12..." (on its own or stuck to the move) is dropped; "0-0" is kept.</summary>
    private static string StripMoveNumber(string word)
    {
        int digits = 0;
        while (digits < word.Length && char.IsAsciiDigit(word[digits]))
            digits++;
        if (digits == word.Length)
            return "";
        if (digits > 0 && word[digits] != '.')
            return word;
        return word.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').TrimStart('.');
    }

    public static string Write(StoredGame game)
    {
        var pgn = new StringBuilder();
        foreach (var (name, value) in game.Tags)
            AppendTag(pgn, name, value);
        pgn.AppendLine();
        AppendMoveText(pgn, game.StartPosition(), game.Moves, game.Result);
        return pgn.ToString();
    }

    public static void AppendTag(StringBuilder pgn, string name, string value) =>
        pgn.Append('[').Append(name).Append(" \"")
           .Append(value.Replace("\\", "\\\\").Replace("\"", "\\\""))
           .AppendLine("\"]");

    /// <summary>The numbered moves and the result, wrapped at 79 columns.  Plays the moves on <paramref name="position"/>.</summary>
    public static void AppendMoveText(StringBuilder pgn, Position position, IEnumerable<Move> moves, string result)
    {
        var words = new List<string>();
        foreach (var move in moves)
        {
            if (position.SideToMove == Colour.White)
                words.Add($"{position.FullmoveNumber}.");
            else if (words.Count == 0)
                words.Add($"{position.FullmoveNumber}...");
            words.Add(San.Of(position, move));
            position.MakeMove(move);
        }
        words.Add(result);

        var line = new StringBuilder();
        foreach (string word in words)
        {
            if (line.Length + word.Length + 1 > 79)
            {
                pgn.AppendLine(line.ToString());
                line.Clear();
            }
            if (line.Length > 0)
                line.Append(' ');
            line.Append(word);
        }
        pgn.AppendLine(line.ToString()).AppendLine();
    }

    private enum TokenKind { Tag, Word, Result, End }

    private readonly record struct Token(TokenKind Kind, string Text, string Name = "");

    /// <summary>Splits PGN into tags, move words and results, dropping everything else.</summary>
    private sealed class Lexer(TextReader reader)
    {
        private Token? _pushedBack;
        private bool _lineStart = true;

        public void PushBack(Token token) => _pushedBack = token;

        public Token Next()
        {
            if (_pushedBack is Token pushed)
            {
                _pushedBack = null;
                return pushed;
            }
            while (true)
            {
                int c = reader.Peek();
                if (c < 0)
                    return new Token(TokenKind.End, "");
                if (_lineStart && c == '%')
                {
                    SkipLine();
                    continue;
                }
                switch ((char)c)
                {
                    case var w when char.IsWhiteSpace(w):
                        Read();
                        break;
                    case '[':
                        return ReadTag();
                    case '{':
                        SkipComment();
                        break;
                    case ';':
                        SkipLine();
                        break;
                    case '(':
                        SkipVariation();
                        break;
                    case '$':
                        Read();
                        while (reader.Peek() is >= '0' and <= '9')
                            Read();
                        break;
                    case ')':
                    case ']':
                    case '}':
                        throw new FormatException($"Unexpected '{(char)c}' in PGN.");
                    default:
                        string word = ReadWord();
                        return StoredGame.Results.Contains(word)
                            ? new Token(TokenKind.Result, word)
                            : new Token(TokenKind.Word, word);
                }
            }
        }

        private int Read()
        {
            int c = reader.Read();
            _lineStart = c == '\n';
            return c;
        }

        private void SkipLine()
        {
            while (reader.Peek() >= 0 && Read() != '\n')
            {
            }
        }

        private void SkipComment()
        {
            Read();
            int c;
            while ((c = Read()) != '}')
                if (c < 0)
                    throw new FormatException("A PGN comment { ... } never ends.");
        }

        private void SkipVariation()
        {
            int depth = 0;
            do
            {
                int c = reader.Peek();
                switch (c)
                {
                    case < 0:
                        throw new FormatException("A PGN variation ( ... ) never ends.");
                    case '{':
                        SkipComment();
                        continue;
                    case ';':
                        SkipLine();
                        continue;
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        break;
                }
                Read();
            } while (depth > 0);
        }

        private string ReadWord()
        {
            var word = new StringBuilder();
            while (reader.Peek() is int c and >= 0 && !char.IsWhiteSpace((char)c) && "[]{}();$".IndexOf((char)c) < 0)
                word.Append((char)Read());
            return word.ToString();
        }

        private Token ReadTag()
        {
            Read();                                   // [
            SkipSpaces();
            var name = new StringBuilder();
            while (reader.Peek() is int c and >= 0 && !char.IsWhiteSpace((char)c) && c != '"' && c != ']')
                name.Append((char)Read());
            SkipSpaces();
            if (Read() != '"')
                throw new FormatException($"PGN tag [{name} ...] has no quoted value.");
            var value = new StringBuilder();
            while (true)
            {
                int c = Read();
                if (c is < 0 or '\n')
                    throw new FormatException($"PGN tag [{name} ...] never closes its quotes.");
                if (c == '"')
                    break;
                if (c == '\\' && reader.Peek() is '"' or '\\')
                    c = Read();
                value.Append((char)c);
            }
            SkipSpaces();
            if (Read() != ']')
                throw new FormatException($"PGN tag [{name} ...] has no closing ']'.");
            return new Token(TokenKind.Tag, value.ToString(), name.ToString());
        }

        private void SkipSpaces()
        {
            while (reader.Peek() is ' ' or '\t')
                Read();
        }
    }
}
