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
        => FindByAnchors(rom) ?? FindByVote(rom);

    private static SpeciesNames? FindByAnchors(byte[] rom)
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

    private const int VoteSampleStep = 4; // every 4th species: a quarter of the ROM searches, still hundreds of agreeing pairs
    private const int VoteMinNameLength = 5;
    private const int VoteMaxPerName = 6;
    private const int VoteMaxDexGap = 40;
    private const int VoteMinStride = 12;
    private const int VoteMaxStride = 600;
    private const int VoteThreshold = 10;

    /// <summary>
    /// For a hack that doesn't hold Bulbasaur, Ivysaur and Venusaur in a row (Lazarus drops some species and keeps its
    /// names inside each species' data): every pair of well-known species names found in the ROM votes for the stride
    /// and start that would put them at their national dex distance apart. The table is where most names agree, and a
    /// custom species with an ID of its own can't outvote the hundreds that follow the dex.
    /// </summary>
    private static SpeciesNames? FindByVote(byte[] rom)
    {
        // the ROM's names are English whatever language the app is showing
        var english = PKHeX.Core.GameInfo.GetStrings("en").specieslist;
        var hits = new List<(int Dex, int At)>();
        for (int dex = 1; dex <= 386; dex += VoteSampleStep)
        {
            var name = english[dex];
            if (name.Length < VoteMinNameLength || Gen3Text.Encode(name) is not { } encoded)
                continue;
            int found = 0;
            for (int from = 0; found < VoteMaxPerName && from < rom.Length;)
            {
                int at = rom.AsSpan(from).IndexOf(encoded);
                if (at < 0)
                    break;
                at += from;
                from = at + 1;
                if (at + encoded.Length < rom.Length && rom[at + encoded.Length] == 0xFF)
                {
                    hits.Add((dex, at));
                    found++;
                }
            }
        }

        var votes = new Dictionary<(int Stride, int Start), int>();
        for (int i = 0; i < hits.Count; i++)
        {
            for (int j = i + 1; j < hits.Count; j++)
            {
                int gap = hits[j].Dex - hits[i].Dex;
                if (gap is <= 0 or > VoteMaxDexGap)
                    continue;
                int distance = hits[j].At - hits[i].At;
                if (distance <= 0 || distance % gap != 0)
                    continue;
                int stride = distance / gap;
                if (stride is < VoteMinStride or > VoteMaxStride)
                    continue;
                var key = (stride, hits[i].At - (hits[i].Dex - 1) * stride);
                votes[key] = votes.GetValueOrDefault(key) + 1;
            }
        }
        if (votes.Count == 0)
            return null;
        var best = votes.MaxBy(v => v.Value);
        return best.Value >= VoteThreshold ? new SpeciesNames(best.Key.Start, best.Key.Stride, rom) : null;
    }
}
