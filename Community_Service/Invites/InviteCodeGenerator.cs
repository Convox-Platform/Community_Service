using System.Security.Cryptography;

namespace Community_Service.Invites;

public sealed class InviteCodeGenerator : IInviteCodeGenerator
{
    public const int GeneratedLength = 8;
    public const int MaximumLength = 12;

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public string Generate()
    {
        return string.Create(GeneratedLength, 0, static (buffer, _) =>
        {
            for (var i = 0; i < buffer.Length; i++)
                buffer[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        });
    }

    public static bool IsValid(string code) =>
        code.Length is > 0 and <= MaximumLength &&
        code.All(static c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9');
}
