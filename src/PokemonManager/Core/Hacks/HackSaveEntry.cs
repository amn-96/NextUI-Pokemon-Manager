using System.Text;

namespace PokemonManager.Core.Hacks;

/// <summary>
/// A pokeemerald-expansion hack's save, shown view-only: nothing here writes to the file.
/// </summary>
public sealed class HackSaveEntry(string path, ExpansionSave save, IReadOnlyList<string> romPaths)
{
    public string Path { get; } = path;
    public ExpansionSave Save { get; } = save;
    private readonly Lazy<SpeciesNames?> _names = new(() => RomNames(romPaths));

    /// <summary>The ROM's species names, read the first time they're needed: searching a 32 MB ROM takes seconds on a handheld, so listing the saves doesn't.</summary>
    public SpeciesNames? Names => _names.Value;
    public IReadOnlyList<string> RomPaths { get; } = romPaths;

    /// <summary>The base game named by the ROM's header (a hack keeps its base game's code), Emerald when it says nothing.</summary>
    public string BaseGame => RomPaths.Select(GameBackgrounds.GameFromRom).FirstOrDefault(g => g is not null) ?? "emerald";

    public string FileName => System.IO.Path.GetFileName(Path);
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>The list row: the player and how many Pokémon they have.</summary>
    public string Label => $"{Title} - {Save.TrainerName} ({Save.AllMons.Count()} Pokémon)";

    /// <summary>The save as a Gen 3 hack save with expanded data, or null (vanilla saves stay with PKHeX).</summary>
    public static HackSaveEntry? TryLoad(string path, IReadOnlyList<string> romPaths)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (Rzip.IsCompressed(data))
                data = Rzip.Decompress(data) ?? [];
            var save = ExpansionSave.TryRead(data);
            if (save is null || !save.UsesExpandedData)
                return null;
            return new HackSaveEntry(path, save, romPaths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static SpeciesNames? RomNames(IReadOnlyList<string> romPaths)
    {
        foreach (var rom in romPaths)
        {
            try
            {
                if (SpeciesNames.Find(File.ReadAllBytes(rom)) is { } found)
                    return found;
            }
            catch (IOException) { }
        }
        return null;
    }

    private string SpeciesName(ExpansionMon m)
        => Names?.Name(m.Species) ?? PokemonManager.Core.Names.Species((ushort)m.Species);

    /// <summary>The party and boxes as plain text for the text viewer.</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Save.TrainerName}  ID {Save.TrainerId:D5}");
        sb.AppendLine("View only: this game is a ROM hack, so nothing here can be changed or moved.");
        if (Names is null)
            sb.AppendLine("The ROM has no readable name table: names follow the national Pokédex and may be wrong.");
        sb.AppendLine();
        sb.AppendLine("Party");
        for (int i = 0; i < Save.Party.Count; i++)
            if (Save.Party[i].IsOk)
                sb.AppendLine($"  {SpeciesName(Save.Party[i])} Lv.{Save.Party[i].PartyLevel}");
        for (int b = 0; b < Save.Boxes.Count; b++)
        {
            var box = Save.Boxes[b].Where(m => m.IsOk).Select(SpeciesName).ToList();
            if (box.Count == 0)
                continue;
            sb.AppendLine();
            sb.AppendLine($"Box {b + 1}");
            sb.AppendLine("  " + string.Join(", ", box));
        }
        return sb.ToString();
    }
}
