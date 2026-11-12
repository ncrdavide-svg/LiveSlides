# LiveSlides (nome provvisorio) — note per Claude

App della suite LIVE (con LiveCount e LiveCue) per mandare in onda **PowerPoint e PDF in sequenza, come un'unica presentazione**, senza schermate nere, con immagine "tappo" tra una presentazione e l'altra. Windows, WPF, .NET 9. L'utente parla **italiano**: rispondi in italiano.

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

- Ogni file della cartella scelta è un `Deck`. I PDF si aprono con **Windows.Data.Pdf** (motore PDF di Windows 10/11, nessuna libreria esterna). Le presentazioni (.pptx/.ppt/.odp…) vengono **convertite in PDF con LibreOffice headless** (`PresentationConverter`, cache in `%LOCALAPPDATA%\LiveSlides\cache`); se LibreOffice manca e c'è PowerPoint, si usa PowerPoint via COM. Le immagini sono deck da 1 slide.
- `ShowSequence` (Core, testato) appiattisce la scaletta in un'unica sequenza di passi (slide + tappi).
- `OutputWindow` ha due livelli immagine: la slide nuova viene renderizzata **prima** di andare in onda e sfuma sopra la vecchia → mai un fotogramma nero. Le slide vicine sono pre-renderizzate (`MainViewModel.PrerenderAroundAsync`).
- Ordine, spunte, tappo e dissolvenza si salvano in `liveslides.json` **dentro la cartella dello show**; le impostazioni del PC in `%LOCALAPPDATA%\LiveSlides\settings.json`.
- API remota HTTP (porta 8788) come nelle altre app: `RemoteApi.cs`.

## Convenzioni (come LiveCue)

- Interfaccia in **italiano**; codice, commenti e commit in **inglese**. I commenti spiegano il *perché*.
- Logica testabile in `LiveSlides.Core` (senza WPF), con test.

## Insidie note

- **Animazioni, transizioni, video e audio dei PowerPoint non vengono riprodotti**: ogni slide è un'immagine fissa (conseguenza della conversione in PDF). È una scelta di progetto, non un bug.
- La finestra di uscita su monitor è `WS_EX_NOACTIVATE` e posizionata in pixel reali con `SetWindowPos` (app Per-Monitor-V2), come in LiveCue. Niente `AllowsTransparency` (rendering software, lento in 4K).
- I `ListBoxItem` devono restare focusabili: WPF seleziona una riga al clic solo se `Focus()` riesce. I pulsanti invece NON sono focusabili, così Spazio/Invio significano sempre "avanti".
- `Key.Next` e `Key.PageDown` sono lo stesso valore: non metterli entrambi in uno `switch`.
- LibreOffice va lanciato con un profilo privato (`-env:UserInstallation`), altrimenti una finestra di LibreOffice già aperta "ingoia" il comando di conversione.
