# extra — codice non compilato nell'applicazione

Questa cartella è **fuori dalla build**: niente qui dentro finisce in `DVDRescue.exe`.

## filesystem-drivers/

Driver di filesystem in sola lettura scritti quando il progetto stava per diventare un
recupero dati generalista. Sono completi e verificati su filesystem veri, ma non servono a un
programma che recupera video da dischi ottici, dove contano solo UDF e ISO 9660.

| File | Cosa legge | Verificato su |
|---|---|---|
| `NtfsFileSystem.cs` | NTFS: MFT, runlist, compressione LZNT1, file cancellati | volumi creati con `mkntfs`, confronto file per file con ntfs-3g |
| `ExtFileSystem.cs` | ext2/ext3/ext4: puntatori indiretti, extent tree, inline data, cancellati | tre volumi `mke2fs` popolati con `debugfs` |
| `ExFatFileSystem.cs` | exFAT: catene FAT, NoFatChain, up-case table, cancellati | immagini validate da `fsck.exfat` |
| `HfsPlusFileSystem.cs` | HFS+/HFSX: catalog B-tree, extents overflow, decmpfs | immagini validate da `fsck.hfsplus` |

Implementano tutti l'interfaccia `IFileSystem` di `src/DVDRescue/FileSystems/IFileSystem.cs`:
per rimetterli in gioco basta spostare il file in `src/DVDRescue/FileSystems/` e aggiungerli
all'elenco in `RecoveryEngine.EnumerateFileSystems`.

## carving/

Motore di recupero per firma (trova i file dai byte, ignorando il filesystem). Era destinato al
recupero generalista ed è rimasto incompleto: non è stato portato a termine né verificato.
