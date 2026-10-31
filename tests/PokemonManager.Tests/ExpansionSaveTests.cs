using System.Buffers.Binary;
using PokemonManager.Core.Hacks;
using Xunit;

namespace PokemonManager.Tests;

public class ExpansionSaveTests
{
    private static readonly string[] Orders =
    [
        "GAEM", "GAME", "GEAM", "GEMA", "GMAE", "GMEA", "AGEM", "AGME", "AEGM", "AEMG", "AMGE", "AMEG",
        "EGAM", "EGMA", "EAGM", "EAMG", "EMGA", "EMAG", "MGAE", "MGEA", "MAGE", "MAEG", "MEGA", "MEAG",
    ];

    /// <summary>A party or box record the way a pokeemerald-expansion game writes it.</summary>
    private static byte[] Record(uint pid, int species, uint exp, uint expHigh, int[] moves, int item = 0, int partyLevel = 0)
    {
        var record = new byte[partyLevel > 0 ? 100 : 80];
        BinaryPrimitives.WriteUInt32LittleEndian(record, pid);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), 0x12345678);
        Gen3Text.Encode("MON")!.CopyTo(record, 8);
        record[11] = 0xFF;
        var g = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(g, (ushort)species);
        BinaryPrimitives.WriteUInt16LittleEndian(g.AsSpan(2), (ushort)item);
        BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(4), exp | (expHigh << 21));
        var a = new byte[12];
        for (int i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(i * 2), (ushort)moves[i]);
        var parts = new Dictionary<char, byte[]> { ['G'] = g, ['A'] = a, ['E'] = new byte[12], ['M'] = new byte[12] };
        var body = new byte[48];
        var order = Orders[pid % 24];
        for (int i = 0; i < 4; i++)
            parts[order[i]].CopyTo(body, i * 12);
        ushort sum = 0;
        for (int i = 0; i < 48; i += 2)
            sum += BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(i));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x1C), sum);
        uint key = pid ^ 0x12345678;
        for (int i = 0; i < 48; i += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(i), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i)) ^ key);
        body.CopyTo(record, 0x20);
        if (partyLevel > 0)
            record[0x54] = (byte)partyLevel;
        return record;
    }

    /// <summary>A 128 KB save with its sections rotated, so finding them by id is exercised.</summary>
    private static byte[] Save(Action<Gen3Container> fill, uint counter = 7)
    {
        var data = new byte[2 * Gen3Container.SlotSize];
        Array.Fill(data, (byte)0xFF);
        foreach (int slotBase in new[] { 0, Gen3Container.SlotSize })
        {
            for (int i = 0; i < Gen3Container.SectionCount; i++)
            {
                int offset = slotBase + i * Gen3Container.SectionSize;
                Array.Clear(data, offset, Gen3Container.FooterOffset);
                var footer = data.AsSpan(offset + Gen3Container.FooterOffset);
                BinaryPrimitives.WriteUInt16LittleEndian(footer, (ushort)((i + 5) % Gen3Container.SectionCount));
                BinaryPrimitives.WriteUInt32LittleEndian(footer[4..], Gen3Container.Signature);
                BinaryPrimitives.WriteUInt32LittleEndian(footer[8..], slotBase == 0 ? counter : counter - 1);
            }
        }
        // the checksums of the sections that fill doesn't touch
        foreach (int slotBase in new[] { 0, Gen3Container.SlotSize })
        {
            for (int i = 0; i < Gen3Container.SectionCount; i++)
            {
                int offset = slotBase + i * Gen3Container.SectionSize;
                int id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + Gen3Container.FooterOffset));
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + Gen3Container.FooterOffset + 2),
                    Gen3Container.Checksum(data.AsSpan(offset, Gen3Container.VanillaLength(id))));
            }
        }
        fill(Gen3Container.TryParse(data)!);
        return data;
    }

    private static readonly int[] Sb1 = [1, 2, 3, 4];

    [Fact]
    public void AnExpansionPartyIsReadWithItsWideIds()
    {
        var data = Save(c =>
        {
            c.WriteBlock([0], 0, Gen3Text.Encode("Ax")!.Concat(new byte[] { 0xFF }).ToArray());
            c.WriteBlock(Sb1, 0x234, [1, 0, 0, 0]);
            c.WriteBlock(Sb1, 0x238, Record(0xA1B2C3D4, species: 1294, exp: 97336, expHigh: 255, moves: [126, 799, 304, 247], item: 521, partyLevel: 46));
        });

        var save = ExpansionSave.TryRead(data)!;
        Assert.Equal("Ax", save.TrainerName);
        var mon = Assert.Single(save.Party);
        Assert.True(mon.IsOk);
        Assert.Equal(1294, mon.Species);
        Assert.Equal(97336u, mon.Experience);
        Assert.Equal(255u, mon.ExperienceWordHigh);
        Assert.Equal([126, 799, 304, 247], mon.Moves);
        Assert.Equal(521, mon.HeldItem);
        Assert.Equal(46, mon.PartyLevel);
        Assert.Equal("MON", mon.Nickname);
        Assert.True(save.UsesExpandedData);
        Assert.Equal(14, save.Container.ValidChecksums());
    }

    [Fact]
    public void AHackThatMovedThePartyIsStillFound()
    {
        // Heart and Soul keeps its party four bytes later than Emerald does
        var data = Save(c =>
        {
            c.WriteBlock(Sb1, 0x238, [2, 0, 0, 0]);
            c.WriteBlock(Sb1, 0x23C, Record(0x11111111, species: 92, exp: 216, expHigh: 255, moves: [1, 2, 3, 4], partyLevel: 6));
            c.WriteBlock(Sb1, 0x23C + 100, Record(0x22222222, species: 158, exp: 1000, expHigh: 255, moves: [1, 2, 3, 4], partyLevel: 10));
        });
        var save = ExpansionSave.TryRead(data)!;
        Assert.Equal(0x238, save.PartyCountOffset);
        Assert.Equal([92, 158], save.Party.Select(m => m.Species));
    }

    [Fact]
    public void AnEmptyPartyFallsBackToTheVanillaOffset()
        => Assert.Equal(0x234, ExpansionSave.TryRead(Save(c => { }))!.PartyCountOffset);

    [Fact]
    public void AVanillaRangeSaveIsNotCalledExpanded()
    {
        var data = Save(c =>
        {
            c.WriteBlock(Sb1, 0x234, [1, 0, 0, 0]);
            c.WriteBlock(Sb1, 0x238, Record(0x01020304, species: 25, exp: 1000, expHigh: 0, moves: [1, 2, 3, 4], partyLevel: 10));
        });
        Assert.False(ExpansionSave.TryRead(data)!.UsesExpandedData);
    }

    [Fact]
    public void ABoxedPokemonIsFoundInItsSlot()
    {
        int[] pc = [5, 6, 7, 8, 9, 10, 11, 12, 13];
        var data = Save(c => c.WriteBlock(pc, 4 + (2 * 30 + 5) * 80, Record(0x0A0B0C0D, species: 700, exp: 5, expHigh: 0, moves: [1, 0, 0, 0])));
        var save = ExpansionSave.TryRead(data)!;
        Assert.Equal(700, save.Boxes[2][5].Species);
        Assert.Single(save.AllMons);
        Assert.Equal(MonStatus.Empty, save.Boxes[0][0].Status);
    }

    [Fact]
    public void ADamagedRecordIsCorruptNotEmpty()
    {
        var record = Record(0x01020304, species: 25, exp: 1, expHigh: 0, moves: [1, 2, 3, 4]);
        record[0x30] ^= 0x55;
        Assert.Equal(MonStatus.Corrupt, ExpansionMon.Read(record).Status);
    }

    [Fact]
    public void TheNewerSlotIsTheActiveOne()
    {
        var data = Save(c => { }, counter: 9);
        Assert.Equal(0, Gen3Container.TryParse(data)!.SlotBase);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(Gen3Container.SlotSize + 13 * Gen3Container.SectionSize + Gen3Container.FooterOffset + 8), 99);
        for (int i = 0; i < 14; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(Gen3Container.SlotSize + i * Gen3Container.SectionSize + Gen3Container.FooterOffset + 8), 99);
        Assert.Equal(Gen3Container.SlotSize, Gen3Container.TryParse(data)!.SlotBase);
    }

    [Fact]
    public void WritingABlockFixesTheChecksumOfEverySectionItTouches()
    {
        var data = Save(c => c.WriteBlock(Sb1, 3960, new byte[20]));   // runs from section 1 into section 2
        var container = Gen3Container.TryParse(data)!;
        Assert.Equal(14, container.ValidChecksums());
        Assert.Throws<ArgumentOutOfRangeException>(() => container.WriteBlock(Sb1, 3968 * 3 + 3848 - 4, new byte[8]));
    }

    [Fact]
    public void AGrownSaveBlock2IsChecksummedOverItsRealLength()
    {
        var data = Save(c => { });
        // a hack with a SaveBlock2 40 bytes bigger: data past the vanilla length, checksum over all of it
        foreach (int slotBase in new[] { 0, Gen3Container.SlotSize })
        {
            for (int i = 0; i < Gen3Container.SectionCount; i++)
            {
                int offset = slotBase + i * Gen3Container.SectionSize;
                if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + Gen3Container.FooterOffset)) != 0)
                    continue;
                data[offset + 3900] = 0x42;
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + Gen3Container.FooterOffset + 2), Gen3Container.Checksum(data.AsSpan(offset, 3924)));
            }
        }
        var container = Gen3Container.TryParse(data)!;
        Assert.Equal(14, container.ValidChecksums());
        Assert.True(container.Section(0).Length > 3884);
    }

    [Fact]
    public void SpeciesNamesAreFoundByTheirSpacingInTheRom()
    {
        foreach (var (stride, a, b, c) in new[] { (208, "Bulbasaur", "Ivysaur", "Venusaur"), (268, "BULBASAUR", "IVYSAUR", "VENUSAUR") })
        {
            var rom = new byte[0x4000];
            Array.Fill(rom, (byte)0xFF);
            int at = 0x1234;
            foreach (var (name, i) in new[] { (a, 0), (b, 1), (c, 2) })
                Gen3Text.Encode(name)!.CopyTo(rom, at + i * stride);
            var names = SpeciesNames.Find(rom)!;
            Assert.Equal(at, names.Offset);
            Assert.Equal(stride, names.Stride);
            Assert.Equal(b, names.Name(2));
        }
        Assert.Null(SpeciesNames.Find(new byte[0x4000]));
    }

    [Fact]
    public void SpeciesNamesInsideBiggerStructsAreFoundByVote()
    {
        // no Bulbasaur: dex 4 onwards, a stride of 212 with an empty entry now and then, and one custom id out of order
        var rom = new byte[0x20000];
        const int stride = 212, start = 0x3000;
        for (int dex = 4; dex <= 150; dex++)
        {
            if (dex % 17 == 0)
                continue;
            var name = PKHeX.Core.GameInfo.GetStrings("en").specieslist[dex];
            if (Gen3Text.Encode(name) is not { } encoded)
                continue;
            encoded.CopyTo(rom, start + (dex - 1) * stride + 0x2C);
            rom[start + (dex - 1) * stride + 0x2C + name.Length] = 0xFF;
        }
        var names = SpeciesNames.Find(rom)!;
        Assert.Equal(stride, names.Stride);
        Assert.Equal("Pikachu", names.Name(25));
    }

    [Fact]
    public void AViewOnlyEntryIsMadeForExpandedSavesAndLeavesTheFileAlone()
    {
        var data = Save(c =>
        {
            c.WriteBlock([0], 0, Gen3Text.Encode("Ax")!.Concat(new byte[] { 0xFF }).ToArray());
            c.WriteBlock(Sb1, 0x234, [1, 0, 0, 0]);
            c.WriteBlock(Sb1, 0x238, Record(0xA1B2C3D4, species: 25, exp: 97336, expHigh: 255, moves: [126, 799, 304, 247], partyLevel: 46));
        });
        var path = Path.Combine(Path.GetTempPath(), $"hack-{Guid.NewGuid():N}.srm");
        File.WriteAllBytes(path, data);
        try
        {
            var entry = HackSaveEntry.TryLoad(path, [])!;
            Assert.Contains("Lv.46", entry.Report());
            Assert.Contains("View only", entry.Report());
            Assert.Equal(data, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }
}
