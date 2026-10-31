namespace PokemonManager.Core.Hacks;

/// <summary>The Gen 3 English character set, enough for names: letters, digits and common punctuation.</summary>
public static class Gen3Text
{
    private static readonly Dictionary<byte, char> Chars = Build();

    private static Dictionary<byte, char> Build()
    {
        var map = new Dictionary<byte, char>
        {
            [0x00] = ' ', [0xAB] = '!', [0xAC] = '?', [0xAD] = '.', [0xAE] = '-', [0xB4] = '\'', [0xB8] = ',', [0xBA] = '/', [0x1B] = 'é',
        };
        for (int i = 0; i < 10; i++)
            map[(byte)(0xA1 + i)] = (char)('0' + i);
        for (int i = 0; i < 26; i++)
        {
            map[(byte)(0xBB + i)] = (char)('A' + i);
            map[(byte)(0xD5 + i)] = (char)('a' + i);
        }
        return map;
    }

    /// <summary>Text up to the 0xFF terminator; characters outside the table show as '?'.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var text = new System.Text.StringBuilder();
        foreach (byte b in bytes)
        {
            if (b == 0xFF)
                break;
            text.Append(Chars.GetValueOrDefault(b, '?'));
        }
        return text.ToString().TrimEnd();
    }

    public static byte[]? Encode(string text)
    {
        var reverse = Chars.ToDictionary(kv => kv.Value, kv => kv.Key);
        var result = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            if (!reverse.TryGetValue(text[i], out result[i]))
                return null;
        }
        return result;
    }
}
