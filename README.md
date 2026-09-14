# DVDRescue

Recupera i video dai DVD che Windows non riesce ad aprire — i dischetti da 8 cm delle
videocamere miniDVD rimasti **non finalizzati**, i DVD-VR, le registrazioni interrotte a metà —
e li esporta direttamente in **MP4**, senza passare per VOB, IFO o programmi di authoring.

È l'equivalente open della funzione di recupero di IsoBuster, ma specializzato su una cosa sola:
tirare fuori il video e consegnarlo pronto da guardare.

---

## Come funziona

Un disco "non finalizzato" contiene i dati video, ma non la tabella dei contenuti: il filesystem
e le strutture di navigazione vengono scritte solo alla chiusura del disco. Windows non trova
nulla da montare e mostra un disco vuoto. I dati però ci sono.

DVDRescue lavora sotto il livello del filesystem:

1. **Interroga il lettore** con i comandi MMC (`READ DISC INFORMATION`, `READ TRACK INFORMATION`,
   `READ DVD STRUCTURE`) per sapere se il disco è finalizzato e fin dove è stato scritto davvero.
   Se il lettore mente o non risponde, cerca il limite reale con una ricerca binaria sui settori.
2. **Copia l'area scritta in un'immagine grezza** (`.bin`), settore per settore, con ritentativi
   progressivi: blocco grande → blocchi piccoli → singolo settore. I settori irrecuperabili
   vengono azzerati e contati, senza interrompere il recupero. Il disco viene letto una volta sola:
   tutto il resto del lavoro avviene sull'immagine.
3. **Legge il filesystem se c'è**: parser UDF (ECMA-167) e ISO 9660 ridotti all'essenziale, per
   trovare `VIDEO_TS/*.VOB`, `DVD_RTAV/VR_MOVIE.VRO` e le date di registrazione. Sui dischi VR
   delle videocamere l'UDF viene aggiornato durante la ripresa, quindi spesso è leggibile anche
   se il disco non è mai stato chiuso.
4. **Cerca il video direttamente nei settori**, che è ciò che funziona sempre: su DVD ogni settore
   da 2048 byte che contiene video inizia con un pack header MPEG-2 (`00 00 01 BA`). Dal pack
   header si legge anche l'orologio di riferimento (SCR), che serve per calcolare la durata reale
   e per capire dove finisce una registrazione e ne comincia un'altra.
5. **Converte con ffmpeg**: H.264 + AAC per avere file che si aprono ovunque, oppure remux senza
   ricodifica per tenere la qualità originale al 100%.

---

## Uso

1. Scarica l'ultima release, scompatta, avvia `DVDRescue.exe` (chiede i permessi di
   amministratore: servono per parlare con il lettore a livello di settore).
2. Inserisci il disco, scegli il lettore, premi **Leggi disco**.
3. Al termine della scansione compare l'elenco dei video trovati con durata e dimensione.
   Spunta quelli che ti interessano.
4. Scegli cartella e formato di uscita, premi **Estrai e converti**.

Il pulsante **Apri immagine...** riapre un `.bin` già creato (o un `.iso` fatto con altri
programmi) e rifà l'analisi senza toccare il disco.

### Formati di uscita

| Opzione | Risultato |
|---|---|
| **MP4 H.264 + AAC** | ricodifica con libx264, si apre su telefoni, TV, web. Lento ma universale. |
| **MP4 senza ricodifica** | video MPEG-2 copiato tale e quale, audio convertito in AAC. Immediato, qualità intatta. |
| **Tieni anche il .mpg grezzo** | lo stream estratto così com'è, utile come backup se qualcosa va storto. |

Il deinterlacciamento (`yadif`) è attivo di default: le videocamere registrano interlacciato e
senza questo passaggio si vedono i pettini sui movimenti.

---

## Se non trova niente

- **Prova un altro lettore.** È il consiglio che risolve più spesso. Non tutti i masterizzatori
  leggono i dischi 8 cm non finalizzati, e i DVD-RAM richiedono un drive compatibile.
- **Rallenta la lettura.** Sui dischi rovinati aiuta molto (l'app usa `SET CD SPEED`).
- **Pulisci il disco** dal centro verso il bordo, in linea retta.
- Se l'immagine viene creata ma non emergono titoli, riapri il `.bin`: l'analisi si ripete in
  pochi secondi senza stressare ancora il disco.

---

## Compilare

Serve .NET 8 SDK.

```bash
dotnet publish src/DVDRescue/DVDRescue.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Metti `ffmpeg.exe` in `publish/ffmpeg/` oppure lascia che l'app lo scarichi al primo utilizzo.

La build automatica è in `.github/workflows/build.yml`: parte a ogni push e, su un tag `v*`,
pubblica la release con due pacchetti — uno con ffmpeg incluso e uno senza.

```bash
git tag v1.0.0 && git push origin v1.0.0
```

---

## Test

La logica di recupero ha un banco di prova che non richiede né lettore né dischi veri: genera
due clip MPEG-2 in formato DVD, le impacchetta in tre immagini (un disco grezzo senza filesystem,
un DVD-Video con UDF, un DVD-VR da videocamera) e verifica riconoscimento dei pack header,
lettura dell'SCR, divisione in titoli, parsing UDF/ISO 9660, estrazione e conversione.

```bash
bash tests/TestHarness/make-test-images.sh          # serve ffmpeg + genisoimage
cd tests/TestHarness && dotnet run -c Release
```

Gira anche in CI a ogni push (job `test` del workflow), prima della build di Windows.

---

## Struttura

```
src/DVDRescue/
├── Native/
│   ├── NativeMethods.cs      P/Invoke: CreateFile, DeviceIoControl, SCSI_PASS_THROUGH_DIRECT
│   ├── AlignedBuffer.cs      buffer allineato richiesto dall'IOCTL
│   ├── OpticalDrive.cs       comandi MMC: READ(10), READ CD, DISC/TRACK INFORMATION, DVD STRUCTURE
│   └── DriveEnumerator.cs    elenco lettori + INQUIRY
├── Disc/
│   ├── ISectorSource.cs      astrazione settori: lettore ottico o immagine, con ritentativi
│   ├── DiscEngine.cs         limite dell'area scritta, immagine grezza, analisi, estrazione
│   ├── UdfReader.cs          UDF/ECMA-167: anchor, partizione, fileset, file entry, extent
│   └── Iso9660Reader.cs      ISO 9660 come ripiego
├── Media/
│   ├── MpegCarver.cs         pack header MPEG-2, SCR, divisione in titoli
│   ├── FfmpegRunner.cs       conversione e avanzamento
│   └── FfmpegLocator.cs      ricerca e download automatico di ffmpeg
└── MainForm.cs               interfaccia

tests/TestHarness/            banco di prova su immagini sintetiche
.github/workflows/build.yml   test su Linux + build Windows + release automatica
```

---

## Limiti noti

- Solo Windows x64: l'accesso al lettore passa per le API SCSI di Windows.
- I dischi **cifrati** (CSS) non sono gestiti: riguardano i film commerciali, non le registrazioni
  casalinghe, che è il caso d'uso di questo programma.
- I capitoli interni a una registrazione non vengono ricostruiti: la divisione in titoli segue le
  interruzioni di registrazione, che è quello che serve nella pratica.

## Licenza

MIT. ffmpeg è distribuito separatamente sotto la propria licenza.
