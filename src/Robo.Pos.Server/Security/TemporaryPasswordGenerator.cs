using System.Security.Cryptography;

namespace Robo.Pos.Server.Security;

public static class TemporaryPasswordGenerator
{
    private const string Uppercase =
        "ABCDEFGHJKLMNPQRSTUVWXYZ";

    private const string Lowercase =
        "abcdefghijkmnopqrstuvwxyz";

    private const string Numbers =
        "23456789";

    private const string Symbols =
        "!@#$%*-_";

    private static readonly string AllCharacters =
        Uppercase +
        Lowercase +
        Numbers +
        Symbols;

    public static string Generate(
        int length = 16)
    {
        if (length < 12)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                "Temporary passwords must contain at least 12 characters.");
        }

        var characters = new List<char>(length)
        {
            RandomCharacter(Uppercase),
            RandomCharacter(Lowercase),
            RandomCharacter(Numbers),
            RandomCharacter(Symbols)
        };

        while (characters.Count < length)
        {
            characters.Add(
                RandomCharacter(AllCharacters));
        }

        SecureShuffle(characters);

        return new string(characters.ToArray());
    }

    private static char RandomCharacter(
        string source)
    {
        int index =
            RandomNumberGenerator.GetInt32(
                source.Length);

        return source[index];
    }

    private static void SecureShuffle(
        IList<char> characters)
    {
        for (int index = characters.Count - 1;
             index > 0;
             index--)
        {
            int swapIndex =
                RandomNumberGenerator.GetInt32(
                    index + 1);

            (characters[index], characters[swapIndex]) =
                (characters[swapIndex], characters[index]);
        }
    }
}
