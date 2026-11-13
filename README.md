# LiveSlides

App per Windows della suite LIVE (con [LiveCue](https://github.com/ncrdavide-svg/LiveCue) e [LiveCount](https://github.com/ncrdavide-svg/LiveCount)): manda in onda **presentazioni PowerPoint e PDF in sequenza, come un'unica presentazione**, senza schermate nere, con un'immagine «tappo» tra una presentazione e l'altra.

- Le presentazioni sono riprodotte da **PowerPoint** (se installato) o da **LibreOffice**: animazioni, transizioni e filmati dentro le slide vanno come li ha progettati chi le ha fatte.
- Tre schermi, tipico del portatile: lo schermo del PC per l'operatore, uno per l'**uscita** a pieno schermo, uno per la **visualizzazione relatore** (slide attuale, successiva, note, tempo; con PDF e tappo ripete l'uscita).
- Avanti/indietro da tastiera, da telecomando per presentazioni o da remoto (Bitfocus Companion / Stream Deck, API HTTP sulla porta 8788).
- Scaletta con trascinamento, spunte per escludere un file, tappo all'inizio, tra le presentazioni e alla fine.

## Installazione

Scarica `LiveSlides-Setup-<versione>.exe` dalle [Release](https://github.com/ncrdavide-svg/LiveSlides/releases) e lancialo. Contiene già .NET: funziona su Windows 10/11 a 64 bit. Durante l'installazione si può scegliere "per tutti gli utenti" (richiede admin) o "solo per me".

Per le presentazioni PowerPoint serve **PowerPoint** oppure **[LibreOffice](https://www.libreoffice.org/download/download-libreoffice/)** (gratuito); se mancano entrambi, l'installer lo segnala. I PDF e le immagini funzionano comunque.

## Sviluppo

Requisiti: Windows 10/11, [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```
dotnet run --project src/LiveSlides.App
dotnet test
```

### Installer per altri PC

```
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 0.2.0
```

Crea `artifacts\installer\LiveSlides-Setup-0.2.0.exe` (serve [Inno Setup 6](https://jrsoftware.org/isinfo.php): `winget install JRSoftware.InnoSetup`).

## Struttura

| Progetto | Contenuto |
|---|---|
| `src/LiveSlides.Core` | Logica senza interfaccia: la scaletta appiattita in un'unica sequenza di passi (`ShowSequence`), il file `liveslides.json` dello show. |
| `src/LiveSlides.App` | Interfaccia WPF, finestre di uscita e relatore, rendering dei PDF (motore PDF di Windows), riproduzione delle presentazioni con PowerPoint (COM) o LibreOffice (`Assets/lo_bridge.py`). |
| `tests/LiveSlides.Core.Tests` | Test della logica di `LiveSlides.Core`. |
| `installer/` | Script Inno Setup e script di build dell'installer. |
