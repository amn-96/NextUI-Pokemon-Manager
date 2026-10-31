namespace PokemonManager.Core.Hacks;

/// <summary>
/// A hack's species name table, found by searching the ROM rather than by a fixed address: hacks move it and change the
/// size of each entry. Entries are 12-byte names at a fixed stride, in dex order from species 1.
/// </summary>
public sealed record SpeciesNames(int Offset, int Stride, byte[] Rom)
{
    private static readonly string[][] Anchors =
    [
        ["Bulbasaur", "Ivysaur", "Venusaur"],
        ["BULBASAUR", "IVYSAUR", "VENUSAUR"],
    ];

    public string Name(int species)
    {
        int at = Offset + (species - 1) * Stride;
        return species < 1 || at < 0 || at + 12 > Rom.Length ? $"#{species}" : Gen3Text.Decode(Rom.AsSpan(at, 12));
    }

    /// <summary>
    /// Finds Bulbasaur, Ivysaur and Venusaur at an even stride (10-1000 bytes apart, in either capitalisation), which
    /// can only be the table. Null when the ROM has no such table, as with a hack that renamed or reordered its species.
    /// </summary>
    public static SpeciesNames? Find(byte[] rom)
    {
        foreach (var names in Anchors)
        {
            var first = Gen3Text.Encode(names[0]);
            var second = Gen3Text.Encode(names[1]);
            var third = Gen3Text.Encode(names[2]);
            if (first is null || second is null || third is null)
                continue;
            for (int from = 0; ;)
            {
                int start = rom.AsSpan(from).IndexOf(first);
                if (start < 0)
                    break;
                start += from;
                from = start + 1;
                if (start + first.Length >= rom.Length || rom[start + first.Length] != 0xFF)
                    continue;
                int next = rom.AsSpan(start + 1, Math.Min(1000, rom.Length - start - 1)).IndexOf(second);
                if (next < 0)
                    continue;
                int stride = next + 1;
                int afterThat = start + 2 * stride;
                if (stride >= 10 && afterThat + third.Length <= rom.Length && rom.AsSpan(afterThat, third.Length).SequenceEqual(third))
                    return new SpeciesNames(start, stride, rom);
            }
        }
        return null;
    }
}
