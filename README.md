# DVDRescue

Recupera i video dai dischi che il computer non riesce ad aprire — i dischetti da 8 cm delle
videocamere rimasti **non finalizzati**, i DVD-VR dei registratori, le riprese AVCHD, i Blu-ray
registrati, le registrazioni interrotte a metà — e li consegna in **MP4**, pronti da guardare.

Niente VOB, niente IFO, niente authoring: si mette il disco, si preme un tasto, escono i video.

---

## Cosa legge

| Supporto | Formato | Come li trova |
|---|---|---|
| DVD-Video | `VIDEO_TS` con IFO | legge le IFO: titoli e capitoli veri |
| DVD-VR | `DVD_RTAV/VR_MOVIE.VRO` | VR_MANGR.IFO, con ripiego sull'analisi del flusso |
| DVD non finalizzato / +VR | VOB senza strutture | analisi diretta dei settori |
| Blu-ray video | `BDMV` | playlist `.mpls` e clip `.m2ts` |
| Blu-ray registrato | `BDAV` | playlist `.rpls`/`.vpls` |
| AVCHD (videocamere) | `AVCHD/BDMV`, anche sotto `PRIVATE` | playlist, o direttamente i file `.MTS` |
| Disco illeggibile | nessuna struttura | ricerca dei flussi MPEG nei settori |

Apre anche le immagini già riversate con altri programmi: **ISO, BIN/CUE, NRG, MDS/MDF,
CCD/IMG, CDI, DAA** e i dump grezzi.

---

## Le tre cose che lo rendono usabile

### Un file solo, non venti

Se estrai un DVD con la sola ricerca dei flussi, l'orologio interno del video (SCR) riparte a
ogni cella e a ogni capitolo: il risultato sono decine di frammenti da un filmato unico.
DVDRescue legge invece le strutture che il disco dichiara — le IFO sui DVD, le playlist sui
Blu-ray — che dicono esattamente dove comincia e dove finisce ogni registrazione.

Poi lascia scegliere come dividere:

- **Tutto in un unico file** (predefinito) — la ripresa di famiglia torna com'era
- **Un file per registrazione** — utile quando sul disco ci sono riprese di giorni diversi
- **Un file per capitolo** — quando il disco li dichiara

### Analisi in secondi, non in minuti

Su un lettore ottico il tempo se ne va in due modi: leggendo byte che non servono, e aspettando
risposte su settori che non ci sono. Un lettore a cui si chiede un settore non scritto ritenta
per conto suo prima di rispondere, quindi ogni richiesta a vuoto costa secondi.

DVDRescue evita entrambe le cose:

- **Il limite dell'area scritta si cerca solo se serve.** Se il disco ha filesystem e strutture
  di navigazione — il caso normale — quel dato non serve a niente: si leggono le IFO e si sa già
  dove sono i video. La ricerca a tentoni parte soltanto quando l'unica strada rimasta è la
  scansione, e comunque con pochi sondaggi a timeout breve invece di decine a timeout lungo.
- **Le strutture si leggono con lettura anticipata.** Descrittori UDF, tabella dei file e IFO
  stanno in poche zone ma si leggono a morsi da un settore: centinaia di richieste, ognuna delle
  quali costa una ricerca della testina. Con una cache che tira su 128 KB per volta le richieste
  al lettore passano da decine a due o tre.
- **La scansione salta il vuoto, mai il video.** Assaggiare il disco a intervalli fissi sembra
  furbo e invece è il peggio dei due mondi: riempie il lettore di salti e rischia di non vedere
  una ripresa più corta dell'intervallo. Qui il video si legge sempre tutto di seguito — che è
  anche il modo in cui un lettore ottico va più veloce — e si salta soltanto dentro il vuoto,
  dopo averne letto abbastanza da sapere che vuoto è. Il salto non supera mai i 4 MB, quindi non
  può scavalcare una registrazione di più di quattro o cinque secondi; con la spunta *scansione
  approfondita* i salti si disattivano del tutto.
- **Sugli errori si rinuncia in fretta.** Un tentativo per settore, timeout corto, e un blocco
  illeggibile non viene suddiviso fino al singolo settore: costava più di mille comandi al lettore
  per un megabyte di nulla. Dopo qualche migliaio di settori illeggibili di fila la scansione si
  ferma da sola: l'area scritta è finita.

Misurato sul materiale di prova, contando le richieste al supporto (che è ciò che costa tempo su
un lettore ottico, non i byte):

| Caso | Letto | Richieste al lettore |
|---|---|---|
| DVD con strutture di navigazione | 0,2 MB su 23 MB | **2** (erano 26) |
| Disco breve senza filesystem | 21 MB su 21 MB, di seguito | 26 |
| Disco da 300 MB quasi vuoto | 93 MB su 300 MB | 98 |

Il registro riporta i tempi di ogni fase e il numero di letture, così se qualcosa rallenta si vede
subito dove.

Per i dischi messi male c'è la spunta **Recupero insistente**: rimette i tentativi ripetuti e la
suddivisione fino al singolo settore. Recupera di più, ma è molto più lento — va usata quando
serve davvero, non per abitudine.

### Lettura al volo

Non copia prima tutto il disco su un'immagine: i byte vanno dal lettore direttamente a ffmpeg
mentre il disco gira, senza file intermedi. Il disco viene letto **una volta sola**, e se chiedi
sia l'MP4 ricodificato sia quello senza ricodifica escono entrambi da quella stessa lettura.

La copia integrale del disco resta disponibile come opzione, per quando il supporto è messo male
e conviene salvarlo prima di lavorarci sopra.

### Legge dove gli altri si fermano

Un disco "non finalizzato" contiene i dati ma non la tabella dei contenuti: il filesystem viene
scritto solo alla chiusura del disco, quindi Windows non trova niente da montare. DVDRescue
lavora sotto quel livello:

1. Interroga il lettore con i comandi MMC (`READ DISC INFORMATION`, `READ TRACK INFORMATION`,
   `READ DVD STRUCTURE`) per sapere fin dove il disco è stato scritto davvero; se il lettore non
   risponde o mente, cerca il limite reale con una ricerca binaria sui settori.
2. Se il volume non è accessibile, passa al device fisico del lettore.
3. Legge l'UDF anche quando è parziale — sulle videocamere viene aggiornato durante la ripresa,
   quindi spesso c'è anche a disco aperto — compresi i casi con VAT (scrittura a pacchetti),
   sparing table (DVD-RAM/+RW) e partizione di metadati (UDF 2.50 dei Blu-ray).
4. Legge a ritentativi progressivi: blocco grande → blocchi piccoli → settore singolo. I settori
   irrecuperabili vengono contati e saltati, senza fermare il resto.

---

## Uso

1. Scarica l'ultima release, scompatta, avvia `DVDRescue.exe` (chiede i permessi di
   amministratore: servono per parlare col lettore a livello di settore).
2. Inserisci il disco, scegli il lettore, premi **Leggi disco**.
3. Compaiono i video trovati, con durata e provenienza (`IFO — titolo 2`, `playlist 00003.mpls`,
   `scansione diretta`…). Spunta quelli che vuoi.
4. Scegli come dividerli, la cartella di destinazione e il formato. Premi **Estrai e converti**.

**Formati di uscita**

| Opzione | Risultato |
|---|---|
| MP4 H.264 + AAC | ricodifica con libx264: si apre su telefoni, TV, web |
| MP4 senza ricodifica | video copiato tale e quale, audio in AAC: immediato, qualità intatta |
| Flusso grezzo | `.mpg` (DVD) o `.m2ts` (Blu-ray/AVCHD) così come sta sul disco |

I file prendono il nome dal prefisso scelto, senza suffissi: `ripresa.mp4`. Se si chiedono
entrambe le uscite MP4 serve per forza un secondo nome, e solo in quel caso compare
`ripresa_originale.mp4` per la copia senza ricodifica. Estraendo di nuovo nella stessa cartella
il file precedente non viene sovrascritto: il nuovo diventa `ripresa_2.mp4`.

Il deinterlacciamento è attivo di default: le videocamere registrano interlacciato e senza
quel passaggio si vedono i pettini sui movimenti.

La durata mostrata accanto a ogni video è misurata, non dichiarata: l'orologio interno dello
stream riparte a ogni registrazione, quindi sottrarre il primo valore dall'ultimo darebbe la
durata della sola ultima ripresa — mezz'ora può risultare di cinque secondi. DVDRescue misura
invece il ritmo pezzo per pezzo lungo tutto il tratto, così regge sia i reset dell'orologio sia
il fatto che una ripresa ferma occupa molti meno byte di una piena di movimento. Sul materiale
di prova la stima cade entro il 2% del valore reale.

Una nota sul flusso grezzo unito: mettendo in fila registrazioni diverse l'orologio MPEG riparte
da capo, quindi il `.mpg` può dichiarare una durata più corta del vero. I dati ci sono tutti —
l'MP4 convertito riporta la durata giusta.

---

## Salvare su un'unità di rete

DVDRescue gira come amministratore, e Windows tiene separate le connessioni di rete delle due
sessioni: le lettere mappate dall'utente (`Z:` verso `\\server\condivisione`) **non esistono**
per un processo elevato, quindi non compaiono nella finestra di scelta della cartella. Non è un
limite del programma, è come funziona l'elevazione dei privilegi.

All'avvio DVDRescue legge le mappature salvate nel profilo dell'utente e le rifà nella propria
sessione, usando le credenziali già memorizzate in Windows: nella maggior parte dei casi le
unità ricompaiono da sole e il registro elenca quali.

Il pulsante **Rete ▾** accanto alla cartella di destinazione apre tre possibilità:

- **Connetti a una cartella di rete** — per le condivisioni che vogliono credenziali diverse da
  quelle del computer: si scrive il percorso per esteso nella casella
  (`\\server\condivisione\video`) e si inseriscono utente e password nella finestra di Windows.
  Il percorso per esteso funziona sempre, anche senza lettera assegnata.
- **Rendi le unità di rete sempre visibili** — attiva `EnableLinkedConnections` di Windows, che
  collega le unità della sessione normale e di quella amministratore. È una modifica al sistema:
  vale per tutti i programmi e per tutti gli utenti del computer, ha effetto dal riavvio
  successivo, e si annulla dallo stesso menu (il valore viene rimosso, non azzerato, quindi il
  sistema torna esattamente com'era). Comoda su un computer personale; su una postazione condivisa
  conviene lasciar perdere e usare il percorso per esteso.
- **Riprova a ripristinare le unità mappate** — rifà al volo il tentativo dell'avvio, utile se la
  rete è tornata disponibile dopo.

Due avvertenze pratiche. Il ripristino automatico funziona solo con le mappature **persistenti**:
se la lettera è stata assegnata senza spuntare *Riconnetti all'accesso*, Windows non la salva nel
profilo e non c'è niente da ripristinare — in quel caso si rimappa spuntando l'opzione, oppure si
usa il percorso per esteso. E il percorso per esteso si può incollare anche **dentro** la finestra
di scelta della cartella, nella casella "Cartella:" in basso: non serve che la lettera compaia
nell'albero.

Una lettera di rete scritta a mano (`Y:\riprese`) viene tradotta da sola nel percorso per esteso
quando non è raggiungibile: il programma prova `Y:\riprese`, poi `\\server\condivisione\riprese`,
e solo se fallisce anche quello chiede le credenziali. La casella si aggiorna con il percorso che
ha funzionato, così la volta dopo parte già giusta.

## Lettura rapida quando serve un file unico

Su un disco senza filesystem — il caso dei miniDVD non finalizzati — trovare i confini fra una
registrazione e l'altra richiede di passare in rassegna tutta l'area scritta: più di un gigabyte
alla velocità del lettore, cioè qualche minuto.

Ma se la divisione scelta è **Tutto in un unico file**, quei confini non servono a niente: il
programma prende l'intera area scritta, individua il primo e l'ultimo settore video con due
letture e ne misura la durata. Qualche secondo invece di qualche minuto. I settori inutili
vengono scartati durante l'estrazione, quindi il risultato è lo stesso.

Se poi servono le registrazioni separate, basta scegliere la divisione e premere di nuovo
**Leggi disco**: il programma lo segnala nel registro.

## Le impostazioni restano

Cartella di destinazione, cartella della copia disco, modalità di divisione, formati di uscita,
qualità, preset, prefisso dei nomi, velocità di lettura e lettore usato l'ultima volta vengono
salvati alla chiusura e rimessi al prossimo avvio. Un percorso di rete incollato a mano viene
conservato come qualsiasi altro.

Il file è `%APPDATA%\DVDRescue\impostazioni.json`: niente registro e niente installazione, per
spostare la configurazione su un altro computer basta copiarlo. Se si rovina o si cancella, il
programma riparte dai valori predefiniti senza lamentarsi.

## Se il primo tentativo non trova niente, ci riprova da solo

Il metodo veloce va bene sulla maggior parte dei dischi, ma su un supporto vecchio o rovinato
può non bastare: i settori che al primo colpo non rispondono vengono lasciati perdere, e se
capitano proprio dove sta il video il risultato è "nessun video individuato".

Quando succede il programma **non si ferma lì e non chiede niente**: rifà l'analisi da solo con
la scansione approfondita e il recupero insistente, e lo scrive nel registro. Su un DVD-RW del
2006 questa è la differenza fra un disco apparentemente vuoto e quaranta minuti di riprese.

Le due caselle restano a disposizione per partire subito in modalità ostinata quando si sa già
che il disco è messo male, ma non c'è bisogno di ricordarsene: servono a risparmiare il primo
tentativo, non a far funzionare il programma.

Un'altra cosa che il metodo veloce ora gestisce: il video non sempre comincia all'inizio del
disco. Su un DVD-RW formattato in modalità VR la testa è occupata da strutture e riserve, e la
prima ripresa parte anche decine di megabyte più avanti — la ricerca rapida arriva fino a 256 MB
prima di rinunciare.

## Se non trova niente

- **Prova un altro lettore.** È il consiglio che risolve più spesso: non tutti i masterizzatori
  leggono i dischi da 8 cm non finalizzati, e i DVD-RAM richiedono un drive compatibile.
- **Rallenta la lettura** (2x o 4x nella schermata): sui dischi rovinati recupera settori che a
  piena velocità saltano.
- **Spunta la scansione approfondita**: ignora le strutture e passa in rassegna tutti i settori.
  Più lenta, ma è l'ultima spiaggia quando le IFO sono distrutte.
- **Pulisci il disco** dal centro verso il bordo, in linea retta.

---

## Compilare

Serve .NET 8 SDK.

```bash
dotnet publish src/DVDRescue/DVDRescue.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Metti `ffmpeg.exe` in `publish/ffmpeg/` oppure lascia che l'app lo scarichi al primo utilizzo.

La build automatica è in `.github/workflows/build.yml`: a ogni push esegue i test e, su un tag
`v*`, pubblica la release con due pacchetti — uno con ffmpeg incluso e uno senza.

```bash
git tag v1.0.0 && git push origin v1.0.0
```

---

## Test

Il motore di recupero ha un banco di prova che non richiede né lettore né dischi veri: autora
due DVD-Video con `dvdauthor` (uno con tre titoli e capitoli, uno con un titolo composto da più
celle), costruisce un disco grezzo senza filesystem, e verifica riconoscimento del disco, lettura
delle IFO, numero di titoli, unione in un file solo, conversione al volo e robustezza sul rumore.

```bash
bash tests/TestHarness/make-test-images.sh      # serve ffmpeg, dvdauthor, genisoimage
cd tests/TestHarness && dotnet run -c Release
```

Il controllo che conta è il confronto fra i due criteri: sullo stesso disco le strutture danno
**1 titolo** dove il vecchio taglio a ogni salto d'orologio ne produceva **4**. Su un disco vero
con molti capitoli la differenza è fra un file e più di cento.

---

## Struttura

```
src/DVDRescue/
├── Core/             astrazione dei blocchi (lettore, immagine, partizione) e utilità
├── Native/           SCSI pass-through, comandi MMC, elenco lettori
├── FileSystems/      UDF (VAT, sparing, metadati 2.50) e ISO 9660 + Joliet + Rock Ridge
├── Dvd/              IFO DVD-Video (titoli, capitoli, celle) e DVD-VR
├── Bluray/           playlist BDMV/BDAV/AVCHD e analisi Transport Stream
├── Images/           ISO, BIN/CUE, NRG, MDS/MDF, CCD, CDI, DAA
├── Media/            ricerca dei flussi MPEG, ffmpeg
├── Recovery/         motore: riconoscimento disco, titoli, estrazione al volo
└── MainForm.cs       interfaccia

tests/TestHarness/    banco di prova su DVD autorati
extra/                driver non usati da questo programma (vedi extra/README.md)
```

---

## Limiti noti, detti chiaramente

- **Solo Windows x64**: l'accesso al lettore passa per le API SCSI di Windows.
- **Niente dischi protetti**: CSS sui DVD e AACS sui Blu-ray non vengono aggirati. Riguardano i
  film commerciali, non le registrazioni casalinghe, che sono il motivo per cui esiste questo
  programma.
- **DVD-VR**: il parser delle strutture VR è scritto sulla descrizione pubblica disponibile e
  **non è mai stato provato su un disco reale**. Quando i controlli di coerenza non passano, il
  programma lo dice e ripiega sull'analisi diretta del VRO, che funziona comunque.
- **Blu-ray e AVCHD**: playlist e clip info sono verificate contro materiale generato qui, non
  contro dischi commerciali. Il percorso che funziona sempre — l'elenco dei file `.m2ts`/`.MTS` —
  è quello usato quando le playlist mancano.
- **CDI e DAA**: formati proprietari non documentati ufficialmente. Sono letti per quanto è
  ricostruibile, con controlli che fanno fallire l'apertura invece di restituire dati sbagliati.
  I DAA cifrati o divisi in più parti vengono rifiutati con un messaggio esplicito.
- **Nessuna correzione EDC/ECC**: i settori danneggiati vengono letti così come sono, non riparati.

## Licenza

MIT. ffmpeg è distribuito separatamente sotto la propria licenza.
