namespace FsCopilot;

using System.Security.Cryptography;

public static class Random
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    
    public static string TailNumber()
    {
        var rnd = new System.Random();
        var num = rnd.Next(0, 100000);
        return $"{String(2)}-{num:D5}";
    }
    
    public static string String(int length)
    {
        var chars = new char[length];

        for (var i = 0; i < length; i++)
        {
            var index = RandomNumberGenerator.GetInt32(Alphabet.Length);
            chars[i] = Alphabet[index];
        }

        return new(chars);
    }
}