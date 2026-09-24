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
- **Le richieste sono grandi quanto il lettore le accetta, non di più.** È il numero che decide
  tutto ed è anche quello che si sbaglia più facilmente. Il pass-through SCSI passa per
  l'adattatore, che ha un tetto ai byte trasferibili in un solo comando: su parecchi lettori USB
  e ATAPI sono 64 KB. Chiedere un megabyte non dà una lettura parziale, dà un **rifiuto secco**,
  con il disco perfettamente sano. Chi scambia quel rifiuto per un settore rovinato si mette a
  suddividere il blocco fino al singolo settore e finisce per leggere tutto il DVD 2 KB alla
  volta: funziona, e ci mette un'ora. All'apertura del lettore DVDRescue chiede il tetto al
  driver e poi lo **verifica leggendo davvero**, perché i due valori non sempre coincidono.
- **Sugli errori si rinuncia in fretta, e nel vuoto si rinuncia subito.** Suddividere un blocco
  serve a salvare i settori buoni attorno a un graffio; oltre i 128 KB di errori consecutivi non
  c'è nessun graffio, c'è area non scritta, e da lì in poi ogni suddivisione produce solo comandi
  destinati a fallire — un migliaio per megabyte, che sul lettore vero sono minuti di attesa per
  non recuperare niente. Dopo qualche migliaio di settori illeggibili di fila la scansione si
  ferma da sola: l'area scritta è finita (a meno che il limite non sia già noto, e allora quella
  scorciatoia si disattiva, così una zona non scritta in mezzo al disco non fa perdere il video
  che viene dopo).
- **Mentre si cerca non si insiste mai.** Capire *dove* stanno i dati e recuperarli sono due
  mestieri diversi. Durante il riconoscimento e i sondaggi l'impegno scende al minimo qualunque
  cosa sia stata scelta, e prima di ogni lettura grande si prova un settore solo: se quello non
  risponde si tira dritto. Senza questa distinzione un semplice sondaggio su un disco messo male
  costava tre minuti e mezzo.

Misurato sul materiale di prova, contando le richieste al supporto (che è ciò che costa tempo su
un lettore ottico, non i byte):

| Caso | Letto | Richieste al lettore |
|---|---|---|
| DVD con strutture di navigazione | 0,2 MB su 23 MB | **2** (erano 26) |
| Disco breve senza filesystem | 21 MB su 21 MB, di seguito | 26 |
| Disco da 300 MB quasi vuoto | 93 MB su 300 MB | 98 |

Il registro riporta i tempi di ogni fase e il numero di letture, così se qualcosa rallenta si vede
subito dove.

Sui dischi messi male l'impegno cresce da solo, in tre gradini (vedi più avanti). La spunta
**Parti subito col recupero insistente** salta direttamente all'ultimo: serve solo se si sa già
com'è messo il disco, altrimenti conviene lasciar fare al programma.

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

## La durata che vedi nell'elenco

È il numero che si guarda per capire se il recupero ha funzionato, quindi vale la pena spiegare
come viene fuori — ci sono tre trappole e ci sono cascato in tutte e tre.

**Sottrarre il primo orologio dall'ultimo non funziona.** L'orologio dello stream MPEG riparte da
capo a ogni registrazione: su un DVD di famiglia con venti riprese quella differenza misura solo
l'ultima, e mezz'ora di video diventa cinque secondi.

**Un ritmo unico applicato a tutto nemmeno.** I byte al secondo cambiano parecchio: una ripresa
ferma su un muro occupa pochissimo, una piena di movimento molto di più. Il ritmo va misurato
pezzo per pezzo.

**Un tratto non è tutto video.** In modalità file unico si prende l'intera area fra il primo e
l'ultimo settore video: se in mezzo ci sono megabyte vuoti — un DVD-RW con registrazioni
cancellate — contarli al ritmo del video gonfiava la durata di nove volte.

Come funziona adesso: sedici assaggi da un megabyte sparsi sul tratto; di ognuno si conta
*quanti* settori sono video e si misura il ritmo dal primo all'ultimo pack dentro quella stessa
lettura. Un assaggio da un megabyte letto di seguito costa meno di otto letture sparse, e in
cambio dà sia il ritmo sia la densità, contati esattamente. I tratti sotto gli 8 MB si leggono
tutti: è esatto e costa niente. E se la densità complessiva scende sotto la metà, la lettura
rapida si tira indietro da sola e passa alla ricerca vera, perché su un disco pieno di buchi la
sua assunzione non regge.

Misurato sul materiale di prova, contro la verità nota:

| Caso | Durata reale | File unico | File separati |
|---|---|---|---|
| 10 riprese da 20 s in fila (orologio che riparte 9 volte) | 03:20 | **03:23** | 03:24 |
| 2 riprese da 20 s in 300 MB quasi vuoti | 40 s | **40,9 s** (scorciatoia rifiutata) | 40,9 s |
| 2 registrazioni attaccate, 20 s + 7 s | 28 s | **24,8 s** | 28,6 s |

Sul caso che conta — riprese continue, il DVD di videocamera — l'errore è sotto il 2%.

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

## I DVD scritti a più riprese

Una videocamera non scrive il DVD-R tutto d'un fiato: ogni sessione diventa una **traccia**, e
fra una traccia e l'altra restano zone mai scritte. Su un disco così il settore 0 **non risponde
affatto** — il filesystem ci sarebbe finito solo alla chiusura del disco, che non è mai avvenuta.

Questo rompeva il programma in due modi, e il secondo costava caro.

Il primo: si controllava il settore 0 per decidere se il disco fosse leggibile. Non lo era, e il
programma se ne andava dicendo *"disco vuoto o illeggibile"* con mezzo gigabyte di riprese
intatte due settori più in là.

Il secondo: il lettore dichiara esattamente dove ha scritto, e quell'informazione veniva stampata
nel registro e poi buttata via. La scansione partiva da zero e attraversava le zone mai scritte
un pezzo alla volta, ritentando su ognuna. Su un disco vero — tracce a 528, 34336 e 34816, ultimo
settore scritto 248543 — sono **66 MB di nulla** macinati a forza di comandi destinati a fallire:
venti minuti di attesa per non recuperare niente.

Adesso i tratti dichiarati dal lettore guidano tutto: la calibrazione li usa come banco di prova,
la verifica del limite cerca un appiglio fra gli inizi delle tracce invece di fissarsi sul settore
0, e la scansione passa in rassegna solo quelli, saltando il resto senza nemmeno chiederlo. Nel
collaudo con un disco finto fatto così — due riprese separate da 37 MB mai scritti, settore 0
morto — il vuoto costa **zero comandi in più**, e le spunte "scansione approfondita" e "recupero
insistente" non cambiano il tempo: 658 comandi contro 660.

## Quando il disco è davvero rovinato

Prima o poi capita il disco che non si legge e basta. Lì contano due cose, e nessuna delle due
è recuperare l'ultimo byte.

**Sapere che sta lavorando.** L'avanzamento dell'estrazione segue la testina, non il convertitore:
mostra dove sta sul disco, quanto ne è uscito di buono, quanti settori si sono persi e a che
velocità sta andando. Se il lettore è in difficoltà lo dice invece di mostrare `0 MB` fermo —
prima contava i byte consegnati a ffmpeg, che su un disco messo male restano a zero per minuti
proprio mentre il lettore lavora di più.

**Che finisca.** Il recupero insistente spende sei comandi per ogni settore che non risponde, e
il lettore ci mette decimi di secondo a rifiutarne ognuno: un solo megabyte distrutto sono sette
minuti, mezzo gigabyte sono ore. C'è quindi un tetto al tempo complessivo speso a ritentare —
tre minuti — dopo il quale si prosegue in lettura veloce e lo si scrive nel registro. Ai dischi
solo graffiati non toglie niente, perché lì i settori muti sono pochi e sparsi e il bilancio non
si esaurisce mai; serve a non spendere ore su un disco che non ha più niente da dare.

Nel collaudo, su un finto disco con un quarto della superficie morta: **6,9 s contro 14,9 s**,
tutti i settori buoni recuperati lo stesso, quelli rotti scartati invece di finire nel file come
spazzatura.

## Se il primo tentativo non trova niente, ci riprova da solo

Il metodo veloce va bene sulla maggior parte dei dischi, ma su un supporto vecchio o rovinato
può non bastare: i settori che al primo colpo non rispondono vengono lasciati perdere, e se
capitano proprio dove sta il video il risultato è "nessun video individuato".

Quando succede il programma **non si ferma lì e non chiede niente**: riprova da solo, e lo
scrive nel registro. Ma riprova *per gradi*, che è la parte che conta — saltare dritti al metodo
ostinato su un disco grande vuol dire mezz'ora di attesa per scoprire che non c'era niente.

| Gradino | Cosa fa | Quanto costa |
|---|---|---|
| **1. veloce** | un tentativo per settore, salta le zone vuote, sondaggio rapido dell'area video | secondi |
| **2. via di mezzo** | legge tutto di seguito senza saltare, e sui settori che non rispondono prova il comando alternativo (READ CD) — quello che certi lettori accettano dove il primo fallisce | qualche minuto su un disco intero |
| **3. insistente** | tre tentativi per settore e suddivisione fino al singolo settore | lento, e lo dice: si ferma con **Interrompi** |

Il secondo gradino è quello nuovo, ed è quello che risolve quasi tutti i casi reali: su un DVD-RW
del 2006 è la differenza fra un disco apparentemente vuoto e quaranta minuti di riprese, senza
pagare il prezzo del terzo. L'estrazione poi usa lo stesso impegno che ha permesso di trovare il
video, altrimenti i settori strappati a fatica tornerebbero vuoti nel file.

Il sondaggio rapido non viene rifatto ai tentativi successivi al primo: è già stato fatto e non
ha trovato niente, ripeterlo è solo tempo.

Un'altra cosa che il metodo veloce gestisce: il video non sempre comincia all'inizio del disco.
Su un DVD-RW formattato in modalità VR la testa è occupata da strutture e riserve, e la prima
ripresa parte anche decine di megabyte più avanti. I primi 16 MB si leggono di seguito, così una
ripresa breve in testa non sfugge, poi si assaggia ogni 8 MB **fino in fondo all'area scritta**
invece di arrendersi a metà disco.

## Se non trova niente

- **Prova un altro lettore.** È il consiglio che risolve più spesso: non tutti i masterizzatori
  leggono i dischi da 8 cm non finalizzati, e i DVD-RAM richiedono un drive compatibile.
- **Rallenta la lettura** (2x o 4x nella schermata): sui dischi rovinati recupera settori che a
  piena velocità saltano.
- **Spunta la scansione approfondita**: ignora le strutture e passa in rassegna tutti i settori.
  Più lenta, ma è l'ultima spiaggia quando le IFO sono distrutte.
- **Guarda il registro alla riga "Trasferimento massimo"**: dice quanti settori per volta accetta
  il lettore. Se è sceso a 1 o 2 settori il lettore sta rifiutando quasi tutto e conviene
  provarne un altro, prima ancora di insistere col recupero.
- **Pulisci il disco** dal centro verso il bordo, in linea retta.

---

## Compilare

Serve .NET 8 SDK.

```bash
dotnet publish src/DVDRescue/DVDRescue.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Metti `ffmpeg.exe` in `publish/ffmpeg/` oppure lascia che l'app lo scarichi al primo utilizzo.

La build automatica è in `.github/workflows/build.yml`: a ogni push esegue i test e costruisce
l'eseguibile, che resta scaricabile fra gli artifact dell'esecuzione per trenta giorni.

### Pubblicare una release scaricabile

Due modi, entrambi producono gli stessi due pacchetti — uno con ffmpeg incluso e uno senza — e
li allegano alla release, da cui chiunque può scaricarli senza passare da GitHub Actions.

**A mano, senza toccare niente.** Scheda **Actions** → workflow **build** → **Run workflow** →
spunta **Pubblica una release scaricabile** → **Run**. Il tag lo crea la build da sola usando la
versione dichiarata nel `.csproj` (oggi `v1.3.1`); volendone un altro si scrive nella casella
**tag**. Ripubblicare la stessa versione sostituisce i file della release esistente.

**Col tag, come prima.** Resta valido e ha la precedenza: il numero di versione lo detta il tag.

```bash
git tag v1.3.1 && git push origin v1.3.1
```

In entrambi i casi la release parte solo se i test passano: il lavoro di build dipende da quello
di test, quindi un controllo rosso ferma la pubblicazione invece di mandare fuori un eseguibile
che non è stato verificato.

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

C'è poi una sezione che collauda la parte più difficile da provare senza un DVD rovinato in mano:
il comportamento sul lettore. Un finto lettore riproduce il tetto ai settori per comando e i
settori che non rispondono, e il banco verifica che un megabyte venga letto per intero in 16
comandi anche con il tetto a 64 KB, che la via di mezzo recuperi un graffio col comando
alternativo in una trentina di comandi, e che sei megabyte di vuoto non ne costino mai più di
qualche centinaio. È il collaudo che avrebbe preso al volo il difetto che faceva leggere i dischi
grandi 2 KB alla volta.

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
