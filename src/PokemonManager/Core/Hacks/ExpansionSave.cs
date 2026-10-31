using System.Buffers.Binary;

namespace PokemonManager.Core.Hacks;

public enum MonStatus { Empty, Corrupt, Ok }

/// <summary>
/// A Pokémon record (80 bytes in a box, 100 in the party) of a ROM hack built on pokeemerald-expansion, read without
/// PKHeX. The record is the vanilla Gen 3 one (PV, OT id, names, a 48-byte body XOR-encrypted with PV^OTID and shuffled
/// by PV % 24 into the substructures G, A, E and M) but with wider ID ranges: species and move and item IDs are not the
/// vanilla ones, and the experience word carries other data above its 21 bits of experience.
/// </summary>
public sealed class ExpansionMon
{
    // Order of the four 12-byte substructures for PV % 24: the lexicographic permutations of G, A, E, M.
    private static readonly string[] Orders =
    [
        "GAEM", "GAME", "GEAM", "GEMA", "GMAE", "GMEA", "AGEM", "AGME", "AEGM", "AEMG", "AMGE", "AMEG",
        "EGAM", "EGMA", "EAGM", "EAMG", "EMGA", "EMAG", "MGAE", "MGEA", "MAGE", "MAEG", "MEGA", "MEAG",
    ];

    public const int BoxSize = 80;
    public const int PartySize = 100;

    public MonStatus Status { get; private init; }
    public uint Pid { get; private init; }
    public uint Otid { get; private init; }
    public string Nickname { get; private init; } = "";
    public string OtName { get; private init; } = "";

    /// <summary>Low 11 bits of the species word. The 5 above are not part of the species (see <see cref="SpeciesWordHigh"/>).</summary>
    public int Species { get; private init; }
    public int SpeciesWordHigh { get; private init; }
    public int HeldItem { get; private init; }

    /// <summary>The low 21 bits of the experience word.</summary>
    public uint Experience { get; private init; }

    /// <summary>The 11 bits above the experience. Not experience: anything that writes the word must keep them.</summary>
    public uint ExperienceWordHigh { get; private init; }
    public int[] Moves { get; private init; } = new int[4];
    public int[] Pp { get; private init; } = new int[4];
    public bool IsEgg { get; private init; }

    /// <summary>The level stored with a party record; 0 for a box record, which stores none.</summary>
    public int PartyLevel { get; private init; }

    public bool IsOk => Status == MonStatus.Ok;

    public static ExpansionMon Read(ReadOnlySpan<byte> record)
    {
        if (record.Length < BoxSize)
            throw new ArgumentException("a Pokémon record is at least 80 bytes", nameof(record));
        uint pid = BinaryPrimitives.ReadUInt32LittleEndian(record);
        uint otid = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
        if (record[..BoxSize].IndexOfAnyExcept((byte)0) < 0)
            return new ExpansionMon { Status = MonStatus.Empty };

        var body = record.Slice(0x20, 48).ToArray();
        uint key = pid ^ otid;
        for (int i = 0; i < 48; i += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(i), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i)) ^ key);
        ushort sum = 0;
        for (int i = 0; i < 48; i += 2)
            sum += BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(i));
        if (sum != BinaryPrimitives.ReadUInt16LittleEndian(record[0x1C..]))
            return new ExpansionMon { Status = MonStatus.Corrupt, Pid = pid, Otid = otid };

        var order = Orders[pid % 24];
        ReadOnlySpan<byte> Sub(char c) => body.AsSpan(order.IndexOf(c) * 12, 12);
        var g = Sub('G');
        var a = Sub('A');
        var m = Sub('M');
        int speciesWord = BinaryPrimitives.ReadUInt16LittleEndian(g);
        uint expWord = BinaryPrimitives.ReadUInt32LittleEndian(g[4..]);
        uint ivWord = BinaryPrimitives.ReadUInt32LittleEndian(m[4..]);
        var moves = new int[4];
        var pp = new int[4];
        for (int i = 0; i < 4; i++)
        {
            moves[i] = BinaryPrimitives.ReadUInt16LittleEndian(a[(i * 2)..]);
            pp[i] = a[8 + i];
        }
        return new ExpansionMon
        {
            Status = speciesWord == 0 ? MonStatus.Empty : MonStatus.Ok,
            Pid = pid,
            Otid = otid,
            Nickname = Gen3Text.Decode(record.Slice(8, 10)),
            OtName = Gen3Text.Decode(record.Slice(0x14, 7)),
            Species = speciesWord & 0x7FF,
            SpeciesWordHigh = speciesWord >> 11,
            HeldItem = BinaryPrimitives.ReadUInt16LittleEndian(g[2..]),
            Experience = expWord & 0x1FFFFF,
            ExperienceWordHigh = expWord >> 21,
            Moves = moves,
            Pp = pp,
            IsEgg = (ivWord & (1u << 30)) != 0,
            PartyLevel = record.Length >= PartySize ? record[0x54] : 0,
        };
    }
}

/// <summary>
/// A save of a pokeemerald-expansion based ROM hack (Pokémon Emerald Seaglass and others), read-only for now. The
/// container, SaveBlock1 and PC storage layouts are the vanilla Emerald ones; only the Pokémon data differs.
/// </summary>
public sealed class ExpansionSave
{
    public const int BoxCount = 14;
    public const int BoxSlots = 30;

    private const int VanillaPartyCountOffset = 0x234;
    private const int PcBoxesOffset = 4; // after currentBox (u32)
    private static readonly int[] SaveBlock1 = [1, 2, 3, 4];
    private static readonly int[] PcStorage = [5, 6, 7, 8, 9, 10, 11, 12, 13];

    // The highest vanilla Gen 3 IDs: species 411 (with 252-276 unused), moves 354, items 376.
    private const int VanillaMaxSpecies = 411;
    private const int VanillaFirstUnusedSpecies = 252;
    private const int VanillaLastUnusedSpecies = 276;
    private const int VanillaMaxMove = 354;
    private const int VanillaMaxItem = 376;

    private ExpansionSave(Gen3Container container)
    {
        Container = container;
        var section0 = container.Section(0);
        TrainerName = Gen3Text.Decode(section0[..8]);
        TrainerId = BinaryPrimitives.ReadUInt16LittleEndian(section0[0xA..]);
        SecretId = BinaryPrimitives.ReadUInt16LittleEndian(section0[0xC..]);
        Gender = section0[8] == 1 ? 1 : 0;

        var sb1 = container.Block(SaveBlock1);
        PartyCountOffset = FindPartyCount(sb1);
        int count = (int)Math.Min(6, BinaryPrimitives.ReadUInt32LittleEndian(sb1.AsSpan(PartyCountOffset)));
        int partyStart = PartyCountOffset + 4;
        Party = [.. Enumerable.Range(0, count).Select(i => ExpansionMon.Read(sb1.AsSpan(partyStart + i * ExpansionMon.PartySize, ExpansionMon.PartySize)))];

        var pc = container.Block(PcStorage);
        CurrentBox = (int)BinaryPrimitives.ReadUInt32LittleEndian(pc);
        Boxes = [.. Enumerable.Range(0, BoxCount).Select(b => Enumerable.Range(0, BoxSlots)
            .Select(s => ExpansionMon.Read(pc.AsSpan(PcBoxesOffset + (b * BoxSlots + s) * ExpansionMon.BoxSize, ExpansionMon.BoxSize)))
            .ToArray())];
    }

    /// <summary>
    /// Where the party count (u32) is in SaveBlock1, with the six 100-byte records after it. Vanilla Emerald has it at
    /// 0x234; a hack that adds fields to SaveBlock1 moves it (Heart and Soul's is at 0x238), so it is found, not assumed.
    /// </summary>
    public int PartyCountOffset { get; }

    public Gen3Container Container { get; }
    public string TrainerName { get; }
    public ushort TrainerId { get; }
    public ushort SecretId { get; }

    /// <summary>0 for a boy, 1 for a girl (section 0, offset 8).</summary>
    public int Gender { get; }
    public int CurrentBox { get; }
    public IReadOnlyList<ExpansionMon> Party { get; }
    public IReadOnlyList<ExpansionMon[]> Boxes { get; }

    public IEnumerable<ExpansionMon> AllMons => Party.Concat(Boxes.SelectMany(b => b)).Where(m => m.IsOk);

    /// <summary>
    /// The vanilla offset when a party starts there; otherwise the first 4-byte aligned offset holding a count of 1-6
    /// followed by that many valid Pokémon records and then only empty ones, which nothing else in SaveBlock1 looks like.
    /// An empty party has no records to find, so it falls back to the vanilla offset.
    /// </summary>
    private static int FindPartyCount(byte[] sb1)
    {
        if (LooksLikeParty(sb1, VanillaPartyCountOffset))
            return VanillaPartyCountOffset;
        for (int offset = 0; offset + 4 + 6 * ExpansionMon.PartySize <= sb1.Length; offset += 4)
        {
            if (LooksLikeParty(sb1, offset))
                return offset;
        }
        return VanillaPartyCountOffset;
    }

    private static bool LooksLikeParty(byte[] sb1, int countOffset)
    {
        if (countOffset + 4 + 6 * ExpansionMon.PartySize > sb1.Length)
            return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(sb1.AsSpan(countOffset));
        if (count is < 1 or > 6)
            return false;
        for (int i = 0; i < 6; i++)
        {
            var mon = ExpansionMon.Read(sb1.AsSpan(countOffset + 4 + i * ExpansionMon.PartySize, ExpansionMon.PartySize));
            if (i < count ? !mon.IsOk : mon.Status != MonStatus.Empty)
                return false;
        }
        return true;
    }

    /// <summary>The save as a Gen 3 flash save, or null when it isn't one.</summary>
    public static ExpansionSave? TryRead(byte[] data)
        => Gen3Container.TryParse(data) is { } container ? new ExpansionSave(container) : null;

    /// <summary>
    /// True when a Pokémon in the save can't be a vanilla Gen 3 one: the experience word has data above 21 bits
    /// (vanilla experience tops out at 1,640,000, under 2^21, and the word's top bits are always 0), a species is
    /// outside 1-251 and 277-411, or a move or item is beyond the vanilla ranges. A save that has none of these may
    /// still be a hack's; the ROM settles it.
    /// </summary>
    public bool UsesExpandedData => AllMons.Any(m =>
        m.ExperienceWordHigh != 0
        || m.Species > VanillaMaxSpecies
        || m.Species is >= VanillaFirstUnusedSpecies and <= VanillaLastUnusedSpecies
        || m.HeldItem > VanillaMaxItem
        || m.Moves.Any(move => move > VanillaMaxMove));
}
