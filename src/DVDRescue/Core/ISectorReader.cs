namespace DVDRescue.Core;

/// <summary>
/// Il minimo che serve per leggere settori da un supporto ottico: lo implementa il lettore vero
/// e lo può implementare un finto lettore nei collaudi.
///
/// Esiste per una ragione precisa. La logica che decide quanto insistere su un settore che non
/// risponde — quanto spezzare un blocco, quando usare il comando alternativo, quando prendere
/// atto di essere nel vuoto — è la parte del programma dove gli errori costano ore di attesa
/// all'utente, ed è anche l'unica che senza questa interfaccia si potrebbe provare soltanto
/// con un DVD rovinato in mano.
/// </summary>
public interface ISectorReader
{
    /// <summary>Come chiamare il supporto nei messaggi.</summary>
    string Description { get; }

    /// <summary>
    /// Settori accettati in un solo comando. Il pass-through SCSI passa per l'adattatore, che
    /// ha un tetto ai byte trasferibili in una volta: su parecchi lettori sono 64 KB. Oltre
    /// quel tetto il comando viene rifiutato con il disco perfettamente sano, e scambiare quel
    /// rifiuto per un settore rovinato è il modo più rapido di leggere un DVD in mezz'ora.
    /// </summary>
    int MaxSectorsPerRead { get; }

    /// <summary>Lettura normale, READ(10) sul lettore vero.</summary>
    bool Read(long lba, int count, byte[] destination, int destinationOffset, int timeoutSeconds);

    /// <summary>
    /// Comando alternativo (READ CD): più lento, ma certi lettori lo accettano dove il primo
    /// fallisce, soprattutto sui dischi non finalizzati.
    /// </summary>
    bool ReadAlternate(long lba, int count, byte[] destination, int destinationOffset, int timeoutSeconds);

    /// <summary>
    /// Limita la velocità di rotazione; false se il lettore non accetta il comando.
    ///
    /// Sembra un controsenso ma su un disco rovinato rallentare fa andare più veloce: a piena
    /// velocità il lettore sbaglia la lettura e la ritenta per conto suo decine di volte prima
    /// di rispondere, mentre a 4x la prende al primo colpo.
    /// </summary>
    bool TrySetReadSpeed(int kilobytesPerSecond);
}
