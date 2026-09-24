using System.Diagnostics;
using DVDRescue.Core;
using DVDRescue.Native;

namespace DVDRescue.Recovery;

/// <summary>Quanto insistere su un settore che non risponde.</summary>
public enum ReadEffort
{
    /// <summary>Un tentativo e via. Su un disco sano è l'unica che ha senso.</summary>
    Fast,

    /// <summary>
    /// Un tentativo più il comando alternativo, e il blocco viene spezzato abbastanza da non
    /// buttare via i settori buoni attorno a quello rovinato. È la via di mezzo: recupera quasi
    /// quanto la modalità insistente e costa una frazione del tempo.
    /// </summary>
    Balanced,

    /// <summary>Tre tentativi, comando alternativo e suddivisione fino al singolo settore.</summary>
    Thorough
}

/// <summary>
/// Espone un lettore ottico come sorgente di blocchi.
///
/// La velocità qui dipende quasi tutta da due cose.
///
/// La prima è la dimensione delle richieste: l'adattatore ha un tetto ai byte trasferibili in
/// un comando e oltre quello rifiuta, disco sano o no. Quel taglio lo fa <see cref="OpticalDrive"/>
/// e non va confuso con un errore di lettura, altrimenti si finisce per leggere l'intero disco
/// un settore alla volta.
///
/// La seconda è come ci si comporta sugli errori veri. Un lettore che riceve una richiesta su
/// un'area non scritta ritenta per conto suo prima di rispondere: con un timeout generoso e tre
/// tentativi per settore, poche migliaia di settori vuoti diventano minuti di attesa. Per questo
/// l'impegno è graduato, e dentro una zona morta lunga si smette del tutto di insistere: spezzare
/// un blocco serve a salvare i settori buoni in mezzo a quelli rovinati, non ad arare il vuoto.
/// </summary>
public sealed class OpticalBlockSource : BlockSourceBase
{
    private readonly ISectorReader _reader;
    private readonly IDisposable _owned;
    private readonly byte[] _probe = new byte[2048];
    private long _totalBlocks;
    private long _bad;
    private bool _readSomething;

    public override string Name { get; }
    public override int BlockSize => 2048;
    public override long TotalBlocks => _totalBlocks;
    public override long BadBlockCount => _bad;

    /// <summary>Quanto insistere sui settori che non rispondono.</summary>
    public ReadEffort Effort { get; set; } = ReadEffort.Fast;

    /// <summary>Scorciatoia storica: vero quando l'impegno è al massimo.</summary>
    public bool ThoroughMode
    {
        get => Effort == ReadEffort.Thorough;
        set => Effort = value ? ReadEffort.Thorough : ReadEffort.Fast;
    }

    /// <summary>
    /// Fase esplorativa: si sta cercando <i>dove</i> stanno i dati, non li si sta ancora
    /// recuperando. Insistere qui non serve a niente e costa moltissimo — è quello che
    /// trasformava un semplice sondaggio in tre minuti di attesa — quindi finché è attiva
    /// l'impegno scende al minimo e il timeout si accorcia, qualunque cosa sia stata scelta.
    /// </summary>
    public bool Exploring { get; set; }

    /// <summary>Timeout di una lettura normale, in secondi.</summary>
    public int ReadTimeout { get; set; } = 10;

    /// <summary>Timeout dei sondaggi usati per trovare il limite dell'area scritta.</summary>
    public int ProbeTimeout { get; set; } = 4;

    /// <summary>
    /// Settori illeggibili di fila oltre i quali si smette di spezzare i blocchi.
    ///
    /// Il valore è basso di proposito. Spezzare serve a salvare i settori buoni attorno a un
    /// graffio, e un graffio contiguo più lungo di centoventotto kilobyte non esiste: quello
    /// che c'è oltre è area non scritta. Continuare a suddividerla costa un migliaio di comandi
    /// per megabyte — sul lettore vero sono minuti di attesa per non recuperare niente.
    /// </summary>
    public long StopSplittingAfterFailures { get; set; } = 64;   // 128 KB

    /// <summary>
    /// Tempo massimo da spendere complessivamente sui settori che non rispondono, dopo il quale
    /// si continua in modalità veloce.
    ///
    /// Senza un tetto, l'impegno massimo su un disco molto rovinato non finisce mai: ogni settore
    /// morto costa sei comandi, ognuno dei quali il lettore impiega decimi di secondo a rifiutare,
    /// e un solo megabyte distrutto si porta via sette minuti. Su mezzo gigabyte sono ore, con
    /// l'avanzamento fermo a zero — che è poi il momento in cui uno stacca il programma.
    ///
    /// Il tetto non toglie niente ai dischi solo graffiati: lì i settori che non rispondono sono
    /// pochi e sparsi, il bilancio non si esaurisce mai e l'insistenza resta piena. Serve a non
    /// spendere ore su un disco che non ha più niente da dare.
    /// </summary>
    public TimeSpan RetryBudget { get; set; } = TimeSpan.FromMinutes(3);

    private long _retryTicks;

    /// <summary>Tempo già speso sui settori che non rispondono.</summary>
    public TimeSpan RetrySpent => TimeSpan.FromTicks(_retryTicks);

    /// <summary>Vero quando il tetto è stato raggiunto: da lì in poi si legge in modalità veloce.</summary>
    public bool RetryBudgetSpent => RetryBudget > TimeSpan.Zero && _retryTicks >= RetryBudget.Ticks;

    public void ResetRetryBudget() => _retryTicks = 0;

    /// <summary>Settori illeggibili di fila oltre i quali si considera finita l'area scritta.</summary>
    public long ConsecutiveFailuresLimit { get; set; } = 8192;     // 16 MB

    /// <summary>Vero quando il limite qui sopra è stato superato: chi scandisce può fermarsi.</summary>
    public bool ReachedEndOfData { get; private set; }

    public long ConsecutiveFailures { get; private set; }
    public List<long> BadSectors { get; } = new();

    /// <summary>Settori per comando accettati dal lettore: le richieste più grandi vengono spezzate.</summary>
    public int MaxSectorsPerRead => Math.Max(1, _reader.MaxSectorsPerRead);

    public OpticalBlockSource(OpticalDrive drive, long totalBlocks, bool ownsDrive = true)
        : this(drive, totalBlocks, ownsDrive ? drive : null) { }

    /// <param name="owned">Da liberare alla chiusura, oppure null se lo tiene qualcun altro.</param>
    public OpticalBlockSource(ISectorReader reader, long totalBlocks, IDisposable owned = null)
    {
        _reader = reader;
        _totalBlocks = totalBlocks;
        _owned = owned;
        Name = reader.Description;
    }

    public void SetTotalBlocks(long blocks) => _totalBlocks = blocks;

    public void ResetEndOfData()
    {
        ReachedEndOfData = false;
        ConsecutiveFailures = 0;
    }

    private ReadEffort CurrentEffort =>
        Exploring || RetryBudgetSpent ? ReadEffort.Fast : Effort;

    private int CurrentTimeout => Exploring ? Math.Min(ReadTimeout, 5) : ReadTimeout;

    public override int ReadBlocks(long block, int count, byte[] destination, int destinationOffset)
    {
        if (count <= 0) return 0;

        // Taglio tecnico, non un tentativo fallito: oltre questa dimensione il comando viene
        // rifiutato dall'adattatore anche su un disco perfetto, quindi si spezza e basta.
        int max = MaxSectorsPerRead;

        if (count > max)
        {
            int done = 0;

            for (int p = 0; p < count; p += max)
            {
                if (ReachedEndOfData)
                {
                    Array.Clear(destination, destinationOffset + p * 2048, (count - p) * 2048);
                    break;
                }

                int n = Math.Min(max, count - p);
                done += ReadBlocks(block + p, n, destination, destinationOffset + p * 2048);
            }

            return done;
        }

        long started = Stopwatch.GetTimestamp();

        if (_reader.Read(block, count, destination, destinationOffset, CurrentTimeout))
        {
            ConsecutiveFailures = 0;
            _readSomething = true;
            return count;
        }

        // Da qui in poi si sta ritentando, ed è il tempo che l'utente vede passare guardando
        // un avanzamento fermo: va contato, perché è quello che il tetto limita.
        _retryTicks += Stopwatch.GetElapsedTime(started).Ticks;

        if (count == 1) return ReadSingle(block, destination, destinationOffset) ? 1 : 0;

        if (count <= MinChunkSectors())
        {
            Array.Clear(destination, destinationOffset, count * 2048);
            RegisterFailure(block, count);
            return 0;
        }

        int half = count / 2;
        int ok = ReadBlocks(block, half, destination, destinationOffset);
        ok += ReadBlocks(block + half, count - half, destination, destinationOffset + half * 2048);
        return ok;
    }

    /// <summary>
    /// Sotto questa dimensione non si spezza più.
    ///
    /// La modalità veloce si ferma a un pugno di settori: il suo scopo è capire in fretta se
    /// c'è del video, non salvare l'ultimo byte. Le altre due arrivano al singolo settore,
    /// perché è lì che si recupera davvero qualcosa attorno a un graffio. In mezzo al vuoto,
    /// però, non spezza nessuna delle tre.
    /// </summary>
    private int MinChunkSectors()
    {
        if (ConsecutiveFailures >= SplitLimit) return int.MaxValue;
        return CurrentEffort == ReadEffort.Fast ? 16 : 1;
    }

    /// <summary>Quanti errori di fila prima di considerarsi nel vuoto. L'insistente rinuncia molto dopo.</summary>
    private long SplitLimit => CurrentEffort == ReadEffort.Thorough
        ? StopSplittingAfterFailures * 4      // 512 KB
        : StopSplittingAfterFailures;         // 128 KB

    private bool ReadSingle(long block, byte[] destination, int destinationOffset)
    {
        var effort = CurrentEffort;
        int attempts = effort == ReadEffort.Thorough ? 3 : 1;
        bool useAlternate = effort != ReadEffort.Fast;

        // nel vuoto conclamato nemmeno il comando alternativo ha senso: costerebbe il doppio
        // dei comandi per non trovare mai niente
        if (ConsecutiveFailures >= SplitLimit) useAlternate = false;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            long started = Stopwatch.GetTimestamp();

            if (_reader.Read(block, 1, destination, destinationOffset, CurrentTimeout))
            {
                ConsecutiveFailures = 0;
                _readSomething = true;
                return true;
            }

            if (useAlternate &&
                _reader.ReadAlternate(block, 1, destination, destinationOffset, CurrentTimeout))
            {
                ConsecutiveFailures = 0;
                _readSomething = true;
                return true;
            }

            _retryTicks += Stopwatch.GetElapsedTime(started).Ticks;

            if (attempt + 1 < attempts) Thread.Sleep(20);
        }

        Array.Clear(destination, destinationOffset, 2048);
        RegisterFailure(block, 1);
        return false;
    }

    private void RegisterFailure(long block, int count)
    {
        _bad += count;
        ConsecutiveFailures += count;

        if (BadSectors.Count < 50000) BadSectors.Add(block);

        // "fine dell'area scritta" ha senso solo dopo aver letto qualcosa: se il disco comincia
        // con una zona illeggibile, fermarsi lì vorrebbe dire non trovare mai niente
        if (_readSomething && ConsecutiveFailures >= ConsecutiveFailuresLimit) ReachedEndOfData = true;
    }

    /// <summary>Lettura secca, timeout corto, nessun ritentativo: serve solo a sondare.</summary>
    public bool ProbeSector(long block)
        => _reader.Read(block, 1, _probe, 0, ProbeTimeout);

    public override void Dispose()
    {
        _owned?.Dispose();
        base.Dispose();
    }
}
