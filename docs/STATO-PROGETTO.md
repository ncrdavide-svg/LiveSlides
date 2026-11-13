# LiveSlides — stato del progetto

*Ultimo aggiornamento: 12 novembre 2026*

## Dove siamo

Prima versione (0.1.0) scritta in una sessione cloud (Linux). Compila senza errori e i test di Core passano (7/7).

**12 nov 2026 — prima prova su Windows 11 vero (fatta da Claude, pilotando l'app dall'API remota e catturando le finestre):** funziona. Cartella di prova con 2 PDF (16:9 e 4:3), 1 .pptx, 1 immagine e tappo; nessun errore in `errors.log`. Unica correzione: le anteprime IN ONDA / PROSSIMA ora sono riquadri 16:9 fissi (prima mostravano bande nere che in uscita non c'erano).

**12 nov 2026, seconda parte:**
- **Video in scaletta**: i file video (.mp4, .m4v, .mov, .wmv, .avi, .mkv, .mpg) sono voci della scaletta (etichetta VID). In uscita partono dall'inizio con l'audio, entrano in dissolvenza senza nero, alla fine resta l'ultimo fotogramma finché non si preme avanti. Provato: slide → video → tappo → video, tappo manuale sopra un video (al ritorno il video riparte da capo), avvio direttamente su un video.
- **La scaletta scorre da sola** per tenere in vista il file in onda (provato con 30 file).
- **Cartella `prova-show/`** (non in git): PowerPoint con animazioni e transizioni, PowerPoint con filmato incorporato, un video. Risultato atteso e confermato: animazioni e filmati *dentro* i PowerPoint escono come immagine fissa (vedi CLAUDE.md, «Insidie note»). I due .pptx sono generati con LibreOffice + XML scritto a mano: **non sono mai stati aperti in PowerPoint** (qui non c'è).
- Variabile d'ambiente `LIVESLIDES_DATA` per avviare un'istanza di prova separata.

**12 nov 2026, terza parte — presentazioni riprodotte dal loro programma (richiesta dell'utente: il file del cliente deve andare esattamente come progettato, con le animazioni):**
- I PowerPoint non vanno più in onda come immagini: li riproduce **PowerPoint** (se installato) o **LibreOffice Impress**, a schermo intero sotto la nostra finestra di uscita. Dettagli in CLAUDE.md.
- **Provato con LibreOffice** su `prova-show/`, uscita sul secondo monitor, istanza separata: animazioni a ogni clic, transizioni, filmato dentro la slide che parte e scorre, tappo → presentazione e ritorno con dissolvenza vera (fotografata a metà), tappo manuale sopra la presentazione e ritorno, presentazione → presentazione senza tappo (resta l'ultimo fotogramma finché la successiva è pronta, ~1-2 s), indietro fino a rientrare nella presentazione precedente, pressioni rapide, stop e riavvio. La tastiera resta a LiveSlides. Nessun errore nel log.
- **PowerPoint: codice scritto ma MAI provato** (qui non è installato). L'utente ha detto che il PC dello show avrà PowerPoint: è la prima cosa da provare lì.
- Limiti noti: rientrando all'indietro in una presentazione si arriva all'ultima slide nel suo stato iniziale (non con le animazioni già fatte); alla prima apertura di ogni file la finestra di LibreOffice lampeggia un istante sullo schermo dell'operatore prima di ridursi a icona; slide nascoste non gestite (i numeri di slide potrebbero sfasarsi); sulla finestra di prova le presentazioni restano immagini fisse.
- L'utente ha chiesto di **lasciare stare i video in scaletta** e concentrarsi su ppt e pdf: il codice video è rimasto com'è, non toccato.

**12 nov 2026, quarta parte — fluidità (l'utente: apertura macchinosa, animazioni scattose) e rimozione dei video:**
- **Player video tolto del tutto** su richiesta: i file video non sono più accettati in scaletta (codice rimosso da `Deck`, `OutputWindow`, `SlideRenderer`, `MainViewModel`). In `prova-show/` il file `04 Video in scaletta.mp4` ora viene semplicemente ignorato.
- **Apertura**: all'avvio dello show le presentazioni vengono pre-caricate in background; mandarne in onda una già caricata costa 30–150 ms (prima 1–2 s). Provato: salto diretto su una presentazione a profilo LibreOffice nuovo, 12 cambi rapidi avanti e indietro tra due presentazioni, stop/riavvio, avanti e indietro oltre i confini delle presentazioni. Nessun errore nel log.
- **Animazioni scattose**: tolta la lettura dello schermo ogni 0,6 s che serviva per l'anteprima IN ONDA (causa più probabile; **non misurata**, va confermata a occhio dall'utente). L'anteprima IN ONDA ora è l'immagine statica della slide.
- **Stabilità LibreOffice**: avvio/fine presentazione ora eseguiti sul thread principale di LibreOffice (prima, blocco permanente circa 1 volta su 10 al riavvio di una presentazione). Dettagli e vicoli ciechi già provati in CLAUDE.md.

**13 nov 2026 — apertura ottimizzata e filmati doppi (l'utente: non si possono aprire tutte le presentazioni all'avvio; animazioni scattose con molti file aperti; filmati dentro le slide che partivano due volte):**
- Niente più pre-caricamento di tutte le presentazioni: si carica **solo la successiva**, in una **seconda istanza di LibreOffice**, così non disturba quella in onda. Al massimo 2 LibreOffice aperti, qualunque sia la lunghezza della scaletta (provato con 8 presentazioni di fila, senza tappo tra l'una e l'altra).
- **Filmati doppi risolti**: la presentazione partiva già nascosta sotto il tappo e veniva fatta ripartire quando andava in onda. Ora parte solo quando va in onda. Verificato col timecode del filmato di prova: avanza senza ripartire.
- Pressioni di «avanti» durante l'apertura di una presentazione: prima andavano perse, ora vanno in coda.
- Tempi misurati qui: presentazione pre-caricata in onda in 50–300 ms; salto a una non pre-caricata ~1,2 s; la primissima volta su un PC (creazione dei profili LibreOffice) ~10 s, coperti dal tappo iniziale.
- Da verificare a occhio dall'utente: fluidità delle animazioni sul PC dello show. Audio dei filmati non verificabile da qui (ho controllato solo l'immagine).
- PowerPoint: ancora mai provato.

**13 nov 2026, seconda parte — tre schermi e visualizzazione relatore (l'utente: l'app girerà su portatili; schermo del portatile = app, un secondo schermo = uscita a pieno schermo, un terzo = visualizzazione relatore; con PDF e tappo anche il terzo mostra PDF/tappo):**
- Nuovo menu **«Relatore»** accanto a «Uscita». I monitor nei menu sono indicati per posizione (sinistra/centro/destra), perché i numeri interni di Windows non coincidono con quelli che l'utente vede nelle Impostazioni.
- Schermo relatore: con tappo, PDF e immagini mostra la stessa cosa dell'uscita (anche le dissolvenze); durante una presentazione mostra la vista relatore del programma (slide attuale con le animazioni, successiva, note, orologio, cronometro).
- **Provato con LibreOffice e 3 schermi veri** (principale a sinistra, uscita al centro, relatore a destra): tappo, PDF, presentazione con clic, tappo manuale e ritorno, presentazione col filmato, PDF finale. Tutto sullo schermo giusto. Il filmato nella console relatore resta fermo sul primo fotogramma (è LibreOffice).
- **PowerPoint: vista relatore scritta ma mai provata.**
- Da verificare dall'utente: con LibreOffice la console può comparire per un istante sullo schermo del portatile prima di essere spostata.

**13 nov 2026, terza parte — tastiera rubata (l'utente: quando si apre una presentazione in attesa in sottofondo, la finestra di LiveSlides perde il fuoco e le frecce / l'avanzatore non mandano più avanti):**
- Riprodotto: appena avviato lo show, LibreOffice carica in sottofondo la prima presentazione e la sua finestra (ridotta a icona) si prende la tastiera per sempre.
- Corretto con `FocusGuard`: durante lo show LiveSlides si riprende subito la tastiera se un programma di presentazione la prende senza che l'operatore abbia cliccato o usato Alt+Tab. Prova con tasti veri: prima 0 su 18, ora 18 su 18.
- Non provato: telecomando fisico (manda gli stessi tasti PagSu/PagGiù), PowerPoint.

**13 nov 2026, quarta parte — il messaggio «In attesa della connessione alla stampante...» (l'utente lo vedeva a ogni operazione):**
- Viene da LibreOffice, non dai PDF: compare solo al primo avvio di LibreOffice con un profilo nuovo (la stampante predefinita di questo PC è di rete). Lo si vedeva spesso perché nelle prove creavo continuamente profili nuovi.
- Corretto: i profili usati durante lo show sono copiati da quello della conversione, già inizializzato. Il messaggio può comparire al massimo una volta su un PC nuovo, al primo caricamento di una cartella con dei PowerPoint, mai durante lo show. Effetto collaterale utile: la prima presentazione dello show si apre in ~2 s invece di ~10.

**13 nov 2026, quinta parte — revisione esterna del codice (versione 0.2.1):**
- Corretto: la conversione in PDF usa lo stesso programma della riproduzione (prima, con PowerPoint e LibreOffice entrambi installati, miniature da LibreOffice e show da PowerPoint), con ripiego su LibreOffice se PowerPoint fallisce o si blocca.
- Corretto (trovato durante la revisione, più grave): la conversione con PowerPoint chiudeva PowerPoint alla fine, anche se stava riproducendo lo show. Ora lo chiude solo se non ha altri file aperti, e rilascia subito i riferimenti COM.
- Corretto: il bridge di LibreOffice si sceglie la porta da solo; il pre-caricamento non può più far chiudere l'app per un errore imprevisto.
- Nuovo: menu «Presentazioni» (Automatico / PowerPoint / LibreOffice / Immagini fisse), utile sul PC dello show se PowerPoint desse problemi.
- Scartati con motivazione: vedi CLAUDE.md, «Insidie note».
- La conversione e la riproduzione con PowerPoint restano mai provate (qui PowerPoint non c'è).

Vecchie note sui video (player rimosso, restano solo come storia): pausa e riavvio, avanzamento automatico a fine video, barra del tempo rimanente, volume, passaggio diretto video → video senza tappo (previsto nel codice ma non provato).

## Provato su Windows

- [x] Avvio, riapertura dell'ultima cartella, elenco file.
- [x] Conversione PowerPoint con LibreOffice (prima volta ~8 s compresa la creazione del profilo; poi dalla cache, show pronto in ~1 s). PowerPoint via COM **non provato** (su questo PC non c'è PowerPoint).
- [x] Miniature, anteprime IN ONDA / PROSSIMA, PDF 4:3 con bande laterali.
- [x] Uscita su secondo monitor (1920×1080 esatti in pixel, sempre in primo piano, non ruba il focus) e in finestra di prova.
- [x] Tappo: inizio / tra le presentazioni / fine, tappo manuale e ritorno alla slide.
- [x] API remota sulla porta 8788: start, stop, next, prev, first, last, goto, tappo, status; limiti di inizio/fine scaletta.

## Ancora da provare a mano (servono mouse/tastiera/occhio)

- [ ] Scelta cartella dal pulsante «Cartella…» e trascinamento file da Esplora risorse.
- [ ] Trascinamento per riordinare la scaletta, pulsanti Su/Giù, spunte.
- [ ] Tastiera e telecomando da presentazione (PagSu/PagGiù, T, F5, Esc).
- [ ] Dissolvenza a occhio (nessun nero) con presentazioni vere e pesanti; monitor 4K o con scala diversa dal 100%.

## Non ancora fatto

- Nome definitivo dell'app (ora "LiveSlides"). Logo fatto il 13 nov 2026 (`tools/make_icon.py`, stessa famiglia di LiveCue e LiveCount).
- Installer: fatto il 13 nov 2026 (`installer/`, come LiveCue). Provato qui: installazione silenziosa "solo per me", avvio dell'app installata, disinstallazione pulita. Non provato su un altro PC.
- Esportazione pagina Companion, finestra "Buy me a coffee" (presenti nelle altre due app).
- Tasti globali (telecomando funzionante anche quando LiveSlides non ha il focus).
