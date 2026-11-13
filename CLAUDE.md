# LiveSlides (nome provvisorio) — note per Claude

App della suite LIVE (con LiveCount e LiveCue) per mandare in onda **PowerPoint e PDF in sequenza, come un'unica presentazione**, senza schermate nere, con immagine "tappo" tra una presentazione e l'altra. Windows, WPF, .NET 9. L'utente parla **italiano**: rispondi in italiano.

Repository: https://github.com/ncrdavide-svg/LiveSlides (pubblico, come LiveCue e LiveCount).

**Stato del lavoro e prossimi passi: `docs/STATO-PROGETTO.md`** (aggiornalo a fine sessione).

## Comandi

```
dotnet build LiveSlides.sln
dotnet test
dotnet run --project src/LiveSlides.App
dotnet publish src/LiveSlides.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/publish
python tools/make_icon.py        # rigenera Assets/LiveSlides.ico (serve Pillow)
```

## Come funziona

- Ogni file della cartella scelta è un `Deck`. I PDF si aprono con **Windows.Data.Pdf** (motore PDF di Windows 10/11, nessuna libreria esterna). Le presentazioni (.pptx/.ppt/.odp…) vengono **convertite in PDF con LibreOffice headless** (`PresentationConverter`, cache in `%LOCALAPPDATA%\LiveSlides\cache`); se LibreOffice manca e c'è PowerPoint, si usa PowerPoint via COM. Le immagini sono deck da 1 slide. **I file video non sono ammessi in scaletta** (scelta dell'utente: il player video è stato tolto); i filmati *dentro* le presentazioni invece funzionano, li riproduce il programma.
- **Le presentazioni vanno in onda riprodotte dal loro programma**, non come immagini: così animazioni, transizioni e filmati dentro le slide funzionano come li ha progettati il cliente (requisito dell'utente: il file originale non si tocca). `PresentationPlayers.Create` sceglie **PowerPoint** via COM se è installato (`PowerPointPlayer`), altrimenti **LibreOffice Impress** (`LibreOfficePlayer`, che parla via TCP locale con `Assets/lo_bridge.py`, eseguito dal Python di LibreOffice). La presentazione gira a schermo intero sul monitor di uscita **sotto** la nostra `OutputWindow`; per mandarla in onda la nostra finestra viene nascosta con il *cloak* di DWM (`NativeWindows.SetCloaked`), per uscirne riappare. La dissolvenza tra i due la fa `FadeOverlay`. Tutto in `MainViewModel.Native.cs`. «Avanti/indietro» dentro una presentazione vanno al programma; a fine presentazione (`PlayerState.AtEnd`) si passa al passo successivo della scaletta. La conversione in PDF resta, ma serve solo a miniature, anteprima PROSSIMA e numero di slide. Sulla «finestra di prova» e se non c'è né PowerPoint né LibreOffice le presentazioni vanno come immagini fisse (vecchio comportamento). `settings.json` → `PresentationEngine`: `auto` / `powerpoint` / `libreoffice` / `static`.
- **Tre schermi (tipico: portatile)**: principale = operatore (finestra di LiveSlides), «Uscita» = pieno schermo, «Relatore» = visualizzazione relatore (`PresenterTargetId`, «Nessuno» per spegnerla; di default scelto solo con 3+ schermi). Lo schermo relatore è una seconda `OutputWindow` (`OutputWindow.Mirror`) che ripete tutto ciò che mostra l'uscita (PDF, immagini, tappo, dissolvenze). Durante una presentazione riprodotta dal programma si fa da parte e lascia vedere la vista relatore del programma (console di LibreOffice / visualizzazione relatore di PowerPoint), che il player sposta su quello schermo (`PresenterWindow`, `NativeWindows.MoveNewWindowAsync`). Se il programma non ha la vista relatore, lo schermo relatore mostra l'immagine statica della slide.
- `ShowSequence` (Core, testato) appiattisce la scaletta in un'unica sequenza di passi (slide + tappi).
- `OutputWindow` ha due livelli immagine: la slide nuova viene renderizzata **prima** di andare in onda e sfuma sopra la vecchia → mai un fotogramma nero. Le slide vicine sono pre-renderizzate (`MainViewModel.PrerenderAroundAsync`).
- Ordine, spunte, tappo e dissolvenza si salvano in `liveslides.json` **dentro la cartella dello show**; le impostazioni del PC in `%LOCALAPPDATA%\LiveSlides\settings.json`.
- API remota HTTP (porta 8788) come nelle altre app: `RemoteApi.cs`.

## Convenzioni (come LiveCue)

- Interfaccia in **italiano**; codice, commenti e commit in **inglese**. I commenti spiegano il *perché*.
- Logica testabile in `LiveSlides.Core` (senza WPF), con test.

## Insidie note

- **`PowerPointPlayer` non è mai stato provato**: sul PC di sviluppo PowerPoint non c'è, il codice è scritto dal modello a oggetti documentato. Va provato sul PC dello show prima di fidarsi (monitor giusto, fuoco tastiera, fine presentazione, dissolvenza).
- LibreOffice riproduce le animazioni dei PowerPoint «a modo suo»: solo PowerPoint è fedele all'originale.
- **Mai chiedere alla finestra di presentazione di LibreOffice la sua immagine (`PrintWindow`)**: la presentazione si blocca e ignora ogni «avanti». `NativeWindows.CaptureScreen` legge lo schermo e si usa solo una volta, per tenere l'ultimo fotogramma nel passaggio diretto da una presentazione all'altra.
- **Avvio e fine presentazione in LibreOffice vanno eseguiti sul suo thread principale** (`Office.on_main` nel bridge, via `com.sun.star.awt.AsyncCallback`): chiamati dal thread del bridge, dopo qualche ciclo LibreOffice si blocca per sempre. Con `on_main`: 30 cicli su 30 senza blocchi.
- **Non cercare di nascondere la finestra di modifica di LibreOffice** caricando con `Hidden=True` o facendola nascere già a icona dalla configurazione: in entrambi i casi le presentazioni poi si bloccano o i documenti spariscono. Resta il breve lampo della finestra al caricamento (avviene tutto insieme, quando parte lo show).
- **Si pre-carica solo la presentazione successiva** (`MainViewModel.PreloadNative` → `LoadAsync`), mai tutte: con molti file la macchina si appesantiva e le animazioni scattavano. Caricata, **non avviata**: la presentazione parte solo quando va in onda (avviarla prima sotto il tappo e poi far ripartire la slide faceva partire due volte i filmati incorporati). Se una presentazione è appena andata in onda, il caricamento della successiva aspetta 2 s.
- **LibreOffice usa due istanze separate** (`LibreOfficePlayer`, «slot» 0 e 1, profili `lo-show-profile-0/-1`): una manda in onda, l'altra carica la successiva. Caricare un file blocca per un secondo il thread principale di LibreOffice, e nell'istanza in onda questo bloccherebbe animazioni e filmati. Ogni istanza tiene un solo file: per caricarne un altro l'istanza viene buttata via e riavviata (`reset`). Quindi mai più di 2 LibreOffice aperti. Mandare in onda una presentazione pre-caricata: 50–300 ms; una non pre-caricata (salto lontano): ~1,2 s, con l'immagine precedente in onda. Registro delle operazioni: `player.log` nella cartella dati.
- `PowerPointPlayer` tiene aperti al massimo 2 file (quello in onda e il successivo).
- I monitor nell'interfaccia sono indicati **per posizione** (sinistra/centro/destra), non per numero: il numero di `\\.\DISPLAYn` spesso non è quello delle Impostazioni di Windows (sul PC di sviluppo Windows mostra «3» per lo schermo che internamente è DISPLAY1).
- Il numero che LibreOffice dà al monitor (`Presentation.Display`) non si può leggere (il servizio `DisplayAccess` non esiste). Primo tentativo: l'ordine dei dispositivi di Windows (Display N = N-esimo in `Displays.GetTargets`, verificato con 3 schermi). Il numero che ha funzionato è salvato in `settings.json` (`LibreOfficeDisplays`, chiave = schermo + disposizione di tutti gli schermi). Un tentativo sbagliato farebbe partire la presentazione per un attimo su un altro schermo.
- La **console relatore di LibreOffice** (`EnablePresenterScreen`, attivata per ogni show dal bridge) si apre su uno schermo scelto da LibreOffice, di solito quello dell'operatore: viene spostata subito (finestra `SALTMPSUBFRAME` diversa da quella della presentazione), ma **solo dopo** aver visto la presentazione sullo schermo d'uscita, altrimenti con uno schermo sbagliato si sposterebbe la presentazione invece della console. Può comparire per un istante sullo schermo dell'operatore prima di essere spostata.
- La **visualizzazione relatore di PowerPoint** (`ShowPresenterView`) viene spostata allo stesso modo, ma **non è mai stata provata** (manca PowerPoint): presa come «l'altra finestra grande di PowerPoint».
- **LibreOffice e PowerPoint rubano la tastiera** ogni volta che caricano un file (anche in sottofondo, la presentazione successiva), partono o aprono la vista relatore: da lì frecce e telecomando non comandano più LiveSlides. Durante lo show `FocusGuard` (hook `EVENT_SYSTEM_FOREGROUND` + `LockSetForegroundWindow`) se la riprende subito, tranne quando è l'operatore a cambiare finestra (tasto del mouse premuto, Alt, tasto Windows). Verificato con tasti veri (PagGiù) mandati alla finestra in primo piano: senza guardia 0 su 18 arrivano a LiveSlides, con la guardia 18 su 18.
- «Avanti/indietro» premuti mentre una presentazione si sta aprendo vanno in coda (`_queuedSteps`) e vengono eseguiti appena è in onda.
- LibreOffice ogni tanto muore intorno alla fine di una presentazione: `LibreOfficePlayer.OpenAsync` riprova da solo una volta.
- **Niente letture periodiche dello schermo mentre una presentazione è in onda** (facevano scattare le animazioni): l'anteprima IN ONDA di una slide riprodotta dal programma è l'immagine statica della slide, non ciò che si vede davvero.
- **Mai `pause()`/`resume()` su LibreOffice** (dopo, «avanti» non funziona più) e **mai chiudere un documento dopo averlo proiettato** (LibreOffice va in crash): per liberarsi di un file si butta via l'intera istanza privata (`reset` nel bridge). Aprire i file con `OnMainThread`, altrimenti crash una volta su due. `Hidden=True` + presentazione = crash.
- Per sapere se una slide ha finito le animazioni non contare i clic (un clic durante un'animazione lunga la conclude soltanto): il bridge ascolta `slideAnimationsEnded`.
- WPF toglie da solo `WS_EX_LAYERED` alle sue finestre: la trasparenza dell'intera `OutputWindow` non si può fare, per questo esiste `FadeOverlay` (finestra Win32 pura).
- Le prove della riproduzione nativa richiedono un monitor vero come uscita (non la finestra di prova): compaiono sul secondo monitor dell'utente.
- **Mai provare l'app pilotando la porta 8788 o toccando `%LOCALAPPDATA%\LiveSlides` se l'utente ha LiveSlides aperto**: si comanda la SUA istanza. Per le prove usare un'istanza separata: compilare in un'altra cartella (`dotnet build src/LiveSlides.App -o <cartella>`, perché l'exe in `bin/` è bloccato) e avviarla con la variabile d'ambiente `LIVESLIDES_DATA=<cartella dati>` e un'altra porta nel suo `settings.json`.
- La finestra di uscita su monitor è `WS_EX_NOACTIVATE` e posizionata in pixel reali con `SetWindowPos` (app Per-Monitor-V2), come in LiveCue. Niente `AllowsTransparency` (rendering software, lento in 4K).
- I `ListBoxItem` devono restare focusabili: WPF seleziona una riga al clic solo se `Focus()` riesce. I pulsanti invece NON sono focusabili, così Spazio/Invio significano sempre "avanti".
- `Key.Next` e `Key.PageDown` sono lo stesso valore: non metterli entrambi in uno `switch`.
- LibreOffice va lanciato con un profilo privato (`-env:UserInstallation`), altrimenti una finestra di LibreOffice già aperta "ingoia" il comando di conversione.
