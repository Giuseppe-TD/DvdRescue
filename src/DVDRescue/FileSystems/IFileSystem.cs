using DVDRescue.Core;

namespace DVDRescue.FileSystems;

/// <summary>Un tratto di dati di un file, in byte, relativo alla sorgente del volume.</summary>
public struct FsExtent
{
    public long Offset;
    public long Length;

    public FsExtent(long offset, long length) { Offset = offset; Length = length; }
    public override string ToString() => $"@{Offset}+{Length}";
}

/// <summary>File o cartella trovati in un filesystem.</summary>
public sealed class FsEntry
{
    public string Name = "";
    public string FullPath = "";
    public bool IsDirectory;

    /// <summary>Voce cancellata recuperata dalle strutture residue.</summary>
    public bool IsDeleted;

    /// <summary>I dati potrebbero essere stati sovrascritti o non sono ricostruibili per intero.</summary>
    public bool IsUncertain;

    public long Length;
    public DateTime? Modified;
    public DateTime? Created;

    /// <summary>Posizione dei dati nel volume. Vuota se il file è residente in <see cref="InlineData"/>.</summary>
    public List<FsExtent> Extents = new();

    /// <summary>Dati contenuti direttamente nella struttura (NTFS residente, UDF inline, ext4 inline).</summary>
    public byte[] InlineData;

    /// <summary>Informazione specifica del driver (record MFT, inode, ICB...).</summary>
    public object Tag;

    public List<FsEntry> Children = new();
    public FsEntry Parent;

    public string SizeText => Length >= 1073741824L ? $"{Length / 1073741824.0:F2} GB"
                            : Length >= 1048576L ? $"{Length / 1048576.0:F1} MB"
                            : Length >= 1024L ? $"{Length / 1024.0:F1} KB"
                            : $"{Length} B";

    public override string ToString() => FullPath;
}

/// <summary>Driver di filesystem in sola lettura.</summary>
public interface IFileSystem : IDisposable
{
    /// <summary>Nome leggibile, es. "NTFS", "UDF 2.01", "ISO 9660 + Joliet".</summary>
    string TypeName { get; }

    string VolumeLabel { get; }

    long TotalBytes { get; }

    /// <summary>Radice dell'albero, popolata da <see cref="Scan"/>.</summary>
    FsEntry Root { get; }

    /// <summary>Osservazioni sul volume (strutture danneggiate, funzioni non supportate...).</summary>
    List<string> Notes { get; }

    /// <summary>Costruisce l'albero dei file. Chiamare una volta prima di usare Root.</summary>
    void Scan(bool includeDeleted, CancellationToken ct);

    /// <summary>Scrive il contenuto del file sullo stream indicato. Ritorna i byte scritti.</summary>
    long Extract(FsEntry entry, Stream destination, CancellationToken ct);
}

/// <summary>Base comune: estrazione dagli extent, elenco ricorsivo, utilità di lettura.</summary>
public abstract class FileSystemBase : IFileSystem
{
    protected readonly IBlockSource Source;

    protected FileSystemBase(IBlockSource source) => Source = source;

    public abstract string TypeName { get; }
    public virtual string VolumeLabel { get; protected set; } = "";
    public virtual long TotalBytes => Source.Length;
    public FsEntry Root { get; protected set; } = new() { IsDirectory = true, Name = "", FullPath = "" };
    public List<string> Notes { get; } = new();

    public abstract void Scan(bool includeDeleted, CancellationToken ct);

    public virtual long Extract(FsEntry entry, Stream destination, CancellationToken ct)
    {
        if (entry == null || entry.IsDirectory) return 0;

        if (entry.InlineData != null)
        {
            int n = (int)Math.Min(entry.InlineData.Length, entry.Length > 0 ? entry.Length : entry.InlineData.Length);
            destination.Write(entry.InlineData, 0, n);
            return n;
        }

        long remaining = entry.Length > 0 ? entry.Length : long.MaxValue;
        long written = 0;
        var buffer = new byte[1 << 20];

        foreach (var extent in entry.Extents)
        {
            if (remaining <= 0) break;
            long position = extent.Offset;
            long left = extent.Length;

            while (left > 0 && remaining > 0)
            {
                ct.ThrowIfCancellationRequested();

                int chunk = (int)Math.Min(buffer.Length, Math.Min(left, remaining));
                Array.Clear(buffer, 0, chunk);
                Source.ReadBytes(position, chunk, buffer, 0);
                destination.Write(buffer, 0, chunk);

                position += chunk;
                left -= chunk;
                remaining -= chunk;
                written += chunk;
            }
        }

        return written;
    }

    /// <summary>Tutte le voci dell'albero, radice esclusa.</summary>
    public IEnumerable<FsEntry> EnumerateAll()
    {
        var stack = new Stack<FsEntry>();
        foreach (var c in Root.Children) stack.Push(c);

        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            for (int i = e.Children.Count - 1; i >= 0; i--) stack.Push(e.Children[i]);
        }
    }

    protected void AddChild(FsEntry parent, FsEntry child)
    {
        child.Parent = parent;
        child.FullPath = string.IsNullOrEmpty(parent.FullPath) ? child.Name : parent.FullPath + "/" + child.Name;
        parent.Children.Add(child);
    }

    protected byte[] Read(long offset, int count)
    {
        var b = new byte[count];
        Source.ReadBytes(offset, count, b, 0);
        return b;
    }

    public virtual void Dispose() { }
}
