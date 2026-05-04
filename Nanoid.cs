using System.Security.Cryptography;

namespace Fedi;

public static class Nanoid
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz-";
    private const int DefaultSize = 21;

    public static string Generate(int size = DefaultSize)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        var chars = new char[size];
        for (var i = 0; i < size; i++)
            chars[i] = Alphabet[bytes[i] & 63];
        return new string(chars);
    }
}
