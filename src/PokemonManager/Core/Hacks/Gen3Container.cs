using System.Buffers.Binary;

namespace PokemonManager.Core.Hacks;

/// <summary>
/// The Game Boy Advance Gen 3 flash save container that Ruby/Sapphire/Emerald and the ROM hacks built on them share:
/// two 56 KB slots (at 0x0 and 0xE000) of fourteen 4 KB sections. Each section ends with a footer at 0xFF4: section
/// id (u16), checksum (u16), signature 0x08012025 (u32), save counter (u32). Sections are stored in rotating order,
/// so they are found by id; the slot with the higher counter is the one the game loads.
/// </summary>
public sealed class Gen3Container
{
    public const int SectionSize = 0x1000;
    public const int SectionCount = 14;
    public const int SlotSize = SectionSize * SectionCount;
    public const int FooterOffset = 0xFF4;
    public const uint Signature = 0x08012025;

    private readonly Dictionary<int, int> _offsets = [];
    private readonly Dictionary<int, int> _lengths = [];

    private Gen3Container(byte[] data, int slotBase, uint counter)
    {
        Data = data;
        SlotBase = slotBase;
        Counter = counter;
        for (int i = 0; i < SectionCount; i++)
        {
            int offset = slotBase + i * SectionSize;
            _offsets[BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + FooterOffset))] = offset;
        }
        foreach (var (id, offset) in _offsets)
            _lengths[id] = DetectLength(id, offset);
    }

    /// <summary>
    /// The vanilla length when the checksum fits it. Hacks grow SaveBlock2 (section 0) and sometimes SaveBlock1's last
    /// chunk (section 4), and the checksum covers the bigger structure, whose size is only in the ROM: then the shortest
    /// length that gives the stored checksum is used (the rest of the section is zeros up to the real size, so a longer
    /// length gives the same sum). Falls back to the vanilla length when none fits, which <see cref="ValidChecksums"/> reports.
    /// </summary>
    private int DetectLength(int id, int offset)
    {
        ushort stored = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(offset + FooterOffset + 2));
        int vanilla = VanillaLength(id);
        for (int length = vanilla; length <= FooterOffset; length += 4)
        {
            if (Checksum(Data.AsSpan(offset, length)) == stored)
                return length;
        }
        return vanilla;
    }

    public byte[] Data { get; }
    public int SlotBase { get; }
    public uint Counter { get; }

    /// <summary>Bytes of a section that its checksum covers in the vanilla games.</summary>
    public static int VanillaLength(int sectionId) => sectionId switch { 0 => 3884, 4 => 3848, 13 => 2000, _ => 3968 };

    /// <summary>The active slot's container, or null when the data isn't a Gen 3 flash save.</summary>
    public static Gen3Container? TryParse(byte[] data)
    {
        if (data.Length < 2 * SlotSize)
            return null;
        Gen3Container? best = null;
        foreach (int slotBase in new[] { 0, SlotSize })
        {
            if (!SlotIsComplete(data, slotBase, out uint counter))
                continue;
            if (best is null || counter > best.Counter)
                best = new Gen3Container(data, slotBase, counter);
        }
        return best;
    }

    private static bool SlotIsComplete(byte[] data, int slotBase, out uint counter)
    {
        counter = 0;
        var seen = new HashSet<int>();
        for (int i = 0; i < SectionCount; i++)
        {
            var footer = data.AsSpan(slotBase + i * SectionSize + FooterOffset, 12);
            if (BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) != Signature)
                return false;
            int id = BinaryPrimitives.ReadUInt16LittleEndian(footer);
            if (id >= SectionCount || !seen.Add(id))
                return false;
            counter = BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]);
        }
        return true;
    }

    public static ushort Checksum(ReadOnlySpan<byte> section)
    {
        uint sum = 0;
        for (int i = 0; i + 4 <= section.Length; i += 4)
            sum += BinaryPrimitives.ReadUInt32LittleEndian(section[i..]);
        return (ushort)((sum >> 16) + (sum & 0xFFFF));
    }

    public Span<byte> Section(int id) => Data.AsSpan(_offsets[id], _lengths[id]);

    /// <summary>How many of the 14 sections of the active slot have a correct checksum.</summary>
    public int ValidChecksums()
    {
        int valid = 0;
        for (int id = 0; id < SectionCount; id++)
        {
            ushort stored = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(_offsets[id] + FooterOffset + 2));
            if (Checksum(Section(id)) == stored)
                valid++;
        }
        return valid;
    }

    /// <summary>Several sections joined in the order given, the way the game addresses SaveBlock1 (1-4) and PC storage (5-13).</summary>
    public byte[] Block(params int[] ids)
    {
        var result = new List<byte>();
        foreach (int id in ids)
            result.AddRange(Section(id).ToArray());
        return [.. result];
    }

    /// <summary>Writes bytes at an offset of a joined block, then recomputes the checksum of every section it touched.</summary>
    public void WriteBlock(int[] ids, int offset, ReadOnlySpan<byte> bytes)
    {
        int sectionStart = 0, written = 0;
        foreach (int id in ids)
        {
            var section = Section(id);
            int sectionEnd = sectionStart + section.Length;
            if (offset + written < sectionEnd && written < bytes.Length)
            {
                int inSection = offset + written - sectionStart;
                int count = Math.Min(section.Length - inSection, bytes.Length - written);
                bytes.Slice(written, count).CopyTo(section[inSection..]);
                written += count;
                BinaryPrimitives.WriteUInt16LittleEndian(Data.AsSpan(_offsets[id] + FooterOffset + 2), Checksum(section));
            }
            sectionStart = sectionEnd;
        }
        if (written != bytes.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "write runs past the end of the block");
    }
}
