using DVDRescue.Core;

namespace DVDRescue.Images;

/// <summary>
/// Lettore di immagini Alcohol 120% (MDS descrittore + MDF dati).
///
/// L'MDS comincia con "MEDIA DESCRIPTOR" e un'intestazione di 88 byte che punta ai blocchi
/// sessione (24 byte ciascuno); ogni sessione punta ai propri blocchi traccia (80 byte),
/// che dichiarano modalità, dimensione del settore, LBA iniziale e offset nel file MDF.
/// La lunghezza della traccia sta in un "extra block" di 8 byte (pregap + settori);
/// sui DVD il campo che punta all'extra block contiene direttamente il numero di settori.
/// </summary>
public static class MdsImage
{
    private const string Signature = "MEDIA DESCRIPTOR";

    public static bool TryOpen(string path, out DiscImage image)
    {
        image = null;
        DiscImage result = null;
        FileBlockSource mdf = null;
        try
        {
            string mdsPath = path;
            if (!string.Equals(Path.GetExtension(path), ".mds", StringComparison.OrdinalIgnoreCase))
            {
                string sibling = DiscImage.ResolveCaseInsensitive(Path.ChangeExtension(path, ".mds"));
                if (sibling == null) return false;
                mdsPath = sibling;
            }
            if (!File.Exists(mdsPath)) return false;

            byte[] mds = File.ReadAllBytes(mdsPath);
            if (mds.Length < 88) return false;
            if (ByteUtils.Ascii(mds, 0, 16) != Signature) return false;

            int versionMajor = mds[0x10];
            int versionMinor = mds[0x11];
            int mediumType = ByteUtils.U16LE(mds, 0x12);
            int sessionCount = ByteUtils.U16LE(mds, 0x14);
            long bcaOffset = ByteUtils.U32LE(mds, 0x24);
            int bcaLength = ByteUtils.U16LE(mds, 0x1A);
            long sessionsOffset = ByteUtils.U32LE(mds, 0x50);
            long dpmOffset = ByteUtils.U32LE(mds, 0x54);

            if (sessionCount <= 0 || sessionCount > 99) return false;
            if (sessionsOffset <= 0 || sessionsOffset >= mds.Length) return false;

            // il file dati sta accanto al descrittore, con lo stesso nome
            string mdfPath = DiscImage.ResolveCaseInsensitive(Path.ChangeExtension(mdsPath, ".mdf"));
            if (mdfPath == null || !File.Exists(mdfPath)) return false;
            long mdfLength = new FileInfo(mdfPath).Length;
            if (mdfLength <= 0) return false;

            result = new DiscImage { FormatName = "Alcohol MDS/MDF", Path = mdsPath };
            result.Note($"Descrittore MDS versione {versionMajor}.{versionMinor}, " +
                        $"supporto {MediumName(mediumType)} (0x{mediumType:X2}), {sessionCount} sessioni.");
            if (bcaLength > 0 && bcaOffset > 0) result.Note($"Presente un blocco BCA di {bcaLength} byte, non utilizzato.");
            if (dpmOffset > 0) result.Note("Presenti dati DPM (misure di densità), non utilizzati.");

            bool isDvd = mediumType >= 0x10;

            mdf = new FileBlockSource(mdfPath, 2048);
            result.Own(mdf);

            int skippedToc = 0;
            for (int s = 0; s < sessionCount; s++)
            {
                long so = sessionsOffset + s * 24L;
                if (so + 24 > mds.Length)
                {
                    result.Note($"Blocco della sessione {s + 1} oltre la fine del descrittore: ignorato.");
                    break;
                }

                int sessionNumber = ByteUtils.U16LE(mds, (int)so + 0x08);
                int allBlocks = mds[(int)so + 0x0A];
                int nonTrackBlocks = mds[(int)so + 0x0B];
                long tracksOffset = ByteUtils.U32LE(mds, (int)so + 0x14);
                if (sessionNumber <= 0) sessionNumber = s + 1;

                if (tracksOffset <= 0 || tracksOffset >= mds.Length)
                {
                    result.Note($"Sessione {sessionNumber}: blocchi traccia non raggiungibili, saltata.");
                    continue;
                }

                for (int b = 0; b < allBlocks; b++)
                {
                    long to = tracksOffset + b * 80L;
                    if (to + 80 > mds.Length)
                    {
                        result.Note($"Sessione {sessionNumber}: blocco traccia {b} troncato, lettura interrotta.");
                        break;
                    }
                    int o = (int)to;

                    int mode = mds[o + 0x00];
                    int subchannel = mds[o + 0x01];
                    int point = mds[o + 0x04];
                    long extraOffset = ByteUtils.U32LE(mds, o + 0x0C);
                    int sectorSize = ByteUtils.U16LE(mds, o + 0x10);
                    long startSector = unchecked((int)ByteUtils.U32LE(mds, o + 0x24));
                    ulong startOffset = ByteUtils.U64LE(mds, o + 0x28);
                    long footerOffset = ByteUtils.U32LE(mds, o + 0x34);

                    // i punti da 0xA0 in su sono voci di TOC (prima traccia, ultima traccia, lead-out)
                    if (point == 0 || point >= 0xA0) { skippedToc++; continue; }

                    long sectors = 0;
                    if (isDvd) sectors = extraOffset;
                    else if (extraOffset > 0 && extraOffset + 8 <= mds.Length)
                    {
                        long pregap = ByteUtils.U32LE(mds, (int)extraOffset);
                        sectors = ByteUtils.U32LE(mds, (int)extraOffset + 4);
                        if (pregap > 0)
                            result.Note($"Traccia {point}: pregap di {pregap} settori dichiarato nell'extra block.");
                    }

                    if (sectorSize <= 0) sectorSize = DefaultSectorSize(mode);
                    if (sectors <= 0 && sectorSize > 0)
                    {
                        // ultima risorsa: quello che resta del file dati
                        sectors = (mdfLength - (long)startOffset) / sectorSize;
                        if (sectors > 0)
                            result.Note($"Traccia {point}: lunghezza non dichiarata, dedotta dalla dimensione del MDF " +
                                        $"({sectors} settori).");
                    }
                    if (sectors <= 0) { result.Note($"Traccia {point}: lunghezza nulla, saltata."); continue; }

                    if (startOffset > (ulong)mdfLength)
                    {
                        result.Note($"Traccia {point}: offset {startOffset} oltre la fine del MDF, saltata.");
                        continue;
                    }

                    long available = (mdfLength - (long)startOffset) / sectorSize;
                    if (sectors > available)
                    {
                        result.Note($"Traccia {point}: dichiarati {sectors} settori ma il MDF ne contiene {available}, " +
                                    "la traccia è stata accorciata.");
                        sectors = available;
                    }
                    if (sectors <= 0) continue;

                    bool audio = mode == 0xA9;
                    var track = new ImageTrack
                    {
                        Number = point,
                        Session = sessionNumber,
                        IsAudio = audio,
                        StartLba = startSector,
                        Sectors = sectors,
                        SectorSize = sectorSize,
                        Title = ReadTrackFileName(mds, footerOffset)
                    };
                    ApplyGeometry(track, mode, audio);
                    result.Tracks.Add(track);
                    result.Attach(track, mdf, (long)startOffset);

                    if (subchannel != 0 && sectorSize == 2448)
                        result.Note($"Traccia {point}: settori da 2448 byte con subchannel, usati solo i primi 2352.");
                }

                if (nonTrackBlocks > 0 && s == 0)
                    result.Note($"Ignorati {skippedToc} blocchi di TOC/lead-in (punti 0xA0-0xA2).");
            }

            if (result.Tracks.Count == 0) { result.Dispose(); return false; }

            result.Note($"MDS letto: {result.Tracks.Count} tracce, dati in \"{Path.GetFileName(mdfPath)}\".");
            image = result;
            return true;
        }
        catch
        {
            result?.Dispose();
            mdf?.Dispose();
            return false;
        }
    }

    /// <summary>Il footer di una traccia può contenere il nome del file dati.</summary>
    private static string ReadTrackFileName(byte[] mds, long footerOffset)
    {
        try
        {
            if (footerOffset <= 0 || footerOffset + 8 > mds.Length) return null;
            long nameOffset = ByteUtils.U32LE(mds, (int)footerOffset);
            bool wide = ByteUtils.U32LE(mds, (int)footerOffset + 4) != 0;
            if (nameOffset <= 0 || nameOffset >= mds.Length) return null;

            int end = (int)nameOffset;
            if (wide)
            {
                while (end + 1 < mds.Length && !(mds[end] == 0 && mds[end + 1] == 0)) end += 2;
                return ByteUtils.Utf16LE(mds, (int)nameOffset, end - (int)nameOffset);
            }
            while (end < mds.Length && mds[end] != 0) end++;
            return ByteUtils.Ascii(mds, (int)nameOffset, end - (int)nameOffset);
        }
        catch { return null; }
    }

    private static void ApplyGeometry(ImageTrack track, int mode, bool audio)
    {
        if (audio)
        {
            track.DataOffset = 0;
            track.UserDataSize = track.SectorSize >= 2352 ? 2352 : track.SectorSize;
            return;
        }

        track.UserDataSize = 2048;
        switch (track.SectorSize)
        {
            case 2048: track.DataOffset = 0; break;
            case 2336: track.DataOffset = 8; break;
            case 2352:
            case 2448:
                // MODE1 ha i dati a 16, MODE2 FORM1 a 24: lo decide il sync settore per settore
                track.DataOffset = -1;
                break;
            default:
                track.DataOffset = 0;
                track.UserDataSize = Math.Min(2048, track.SectorSize);
                break;
        }

        // sui DVD (mode 0x02) il settore è sempre 2048 pieni
        if (mode == 0x02 && track.SectorSize == 2048) track.DataOffset = 0;
    }

    private static int DefaultSectorSize(int mode) => mode switch
    {
        0x02 => 2048,   // DVD
        0xA9 => 2352,   // audio
        0xAA => 2048,   // MODE1
        0xAB => 2336,   // MODE2
        0xAC => 2048,   // MODE2 FORM1
        0xAD => 2324,   // MODE2 FORM2
        0xEC => 2336,   // MODE2 misto
        _ => 2048
    };

    private static string MediumName(int type) => type switch
    {
        0x00 => "CD-ROM",
        0x01 => "CD-R",
        0x02 => "CD-RW",
        0x10 => "DVD-ROM",
        0x12 => "DVD-R",
        _ => "sconosciuto"
    };
}
