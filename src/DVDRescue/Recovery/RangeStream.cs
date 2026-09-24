using DVDRescue.Core;
using DVDRescue.Media;

namespace DVDRescue.Recovery;

/// <summary>
/// Flusso in sola lettura che concatena una serie di tratti del disco.
/// È quello che permette la lavorazione al volo: si dà in pasto direttamente a ffmpeg
/// senza scrivere prima un file intermedio, così il disco viene letto una volta sola
/// e la conversione procede mentre il lettore gira.
/// </summary>
public sealed class RangeStream : Stream
{
    private readonly IBlockSource _source;
    private readonly List<RecoveryRange> _ranges;
    private readonly bool _filterInvalidSectors;
    private readonly CancellationToken _ct;

    private int _rangeIndex;
    private long _positionInRange;
    private long _totalRead;

    private readonly byte[] _buffer;
    private int _bufferLength;
    private int _bufferPosition;

    /// <summary>Byte consegnati a chi legge, cioè quelli buoni.</summary>
    public long BytesRead => _totalRead;

    /// <summary>
    /// Byte di disco già attraversati, compresi quelli persi.
    ///
    /// È questo il numero da mostrare durante l'estrazione, non quello sopra. Chi legge tira i
    /// byte quando gli servono e i settori rovinati vengono scartati, quindi su un disco messo
    /// male i byte consegnati restano a zero per minuti mentre il lettore sta arando la superficie:
    /// l'avanzamento sembra piantato proprio quando il programma lavora di più.
    /// </summary>
    public long PositionOnDisc { get; private set; }

    /// <summary>Settori scartati perché non contenevano dati validi.</summary>
    public long SkippedSectors { get; private set; }

    public long TotalLength { get; }

    /// <param name="filterInvalidSectors">
    /// Per i DVD: scarta i settori che non iniziano con un pack header MPEG, così
    /// eventuali settori illeggibili non finiscono nello stream come spazzatura.
    /// </param>
    public RangeStream(IBlockSource source, IEnumerable<RecoveryRange> ranges,
                       bool filterInvalidSectors, CancellationToken ct, int bufferSectors = 256)
    {
        _source = source;
        _ranges = new List<RecoveryRange>(ranges);
        _filterInvalidSectors = filterInvalidSectors;
        _ct = ct;
        _buffer = new byte[Math.Max(1, bufferSectors) * 2048];

        foreach (var r in _ranges) TotalLength += r.Length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => TotalLength;

    public override long Position
    {
        get => _totalRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] destination, int offset, int count)
    {
        int written = 0;

        while (written < count)
        {
            if (_bufferPosition >= _bufferLength)
            {
                if (!FillBuffer()) break;
                if (_bufferLength == 0) continue;
            }

            int available = _bufferLength - _bufferPosition;
            int take = Math.Min(available, count - written);
            Array.Copy(_buffer, _bufferPosition, destination, offset + written, take);

            _bufferPosition += take;
            written += take;
        }

        _totalRead += written;
        return written;
    }

    /// <summary>Carica il prossimo blocco; false quando i tratti sono finiti.</summary>
    private bool FillBuffer()
    {
        _ct.ThrowIfCancellationRequested();
        _bufferPosition = 0;
        _bufferLength = 0;

        while (_rangeIndex < _ranges.Count)
        {
            var range = _ranges[_rangeIndex];
            long remaining = range.Length - _positionInRange;

            if (remaining <= 0)
            {
                _rangeIndex++;
                _positionInRange = 0;
                continue;
            }

            int want = (int)Math.Min(_buffer.Length, remaining);
            long offset = range.Offset + _positionInRange;

            _source.ReadBytes(offset, want, _buffer, 0);
            _positionInRange += want;
            PositionOnDisc += want;

            if (!_filterInvalidSectors)
            {
                _bufferLength = want;
                return true;
            }

            // compatta i soli settori validi all'inizio del buffer
            int kept = 0;
            for (int i = 0; i + 2048 <= want; i += 2048)
            {
                if (MpegPsCarver.IsPackHeader(_buffer, i))
                {
                    if (kept != i) Array.Copy(_buffer, i, _buffer, kept, 2048);
                    kept += 2048;
                }
                else
                {
                    SkippedSectors++;
                }
            }

            if (kept > 0) { _bufferLength = kept; return true; }
            // blocco interamente inutilizzabile: prosegue col successivo
        }

        return false;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
