# LiveSlides — stato del progetto

*Ultimo aggiornamento: 12 novembre 2026*

## Dove siamo

Prima versione (0.1.0) scritta in una sessione cloud (Linux). **Compila senza errori e i test di Core passano (7/7), ma l'app non è ancora mai stata provata su un vero Windows**: l'unica verifica di esecuzione è stata l'avvio sotto Wine, che arriva fino al disegno della finestra principale e poi si ferma per un limite di Wine sui font WPF. Il rendering PDF (Windows.Data.Pdf) non è verificabile fuori da Windows.

## Da provare su Windows (prima prova dell'utente)

- [ ] Avvio, scelta cartella, elenco file.
- [ ] Conversione PowerPoint con LibreOffice (la prima è lenta: crea il profilo privato).
- [ ] Miniature, anteprime IN ONDA / PROSSIMA.
- [ ] Uscita su secondo monitor e in finestra di prova; dissolvenza; nessun nero tra le presentazioni.
- [ ] Trascinamento per riordinare la scaletta (in alternativa: pulsanti Su/Giù).
- [ ] Tappo: inizio / tra le presentazioni / fine, e tasto T.
- [ ] Telecomando da presentazione (PagSu/PagGiù) e API remota sulla porta 8788.

## Non ancora fatto

- Nome definitivo dell'app (ora "LiveSlides") e logo.
- Installer Inno Setup (riusare `installer/` di LiveCue).
- Esportazione pagina Companion, finestra "Buy me a coffee" (presenti nelle altre due app).
- Repository GitHub: il progetto per ora esiste solo come zip dei sorgenti.
- Tasti globali (telecomando funzionante anche quando LiveSlides non ha il focus).
