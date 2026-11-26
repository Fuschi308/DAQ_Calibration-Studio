# Benutzeroberfläche

DAQ Calibration Studio übernimmt das Layout aus dem Claude-Canvas-Handoff. Die lokale Vorlage enthält einen interaktiven HTML-Entwurf, neun Ansichten in beiden Farbschemata und vier zusätzliche Screenshots für Prüfablauf, Freigabe und Gerätefehler. Für die Umsetzung waren insbesondere die Handoff-Dokumente 01 (Designsystem), 02 (Ansichten) und 04 (Verhalten) maßgeblich. Der Desktoprahmen, die Gerätebedienung und alle Diagramme laufen nativ in Avalonia 12.1.6 und ScottPlot 5.1.59. Das Handoff-Paket bleibt lokal.

| Bereich | Native Umsetzung |
|---|---|
| Rahmen | 48 px hohe Kopfzeile mit sechs Reitern, Gerätestatus, kontextabhängiger Prüfaktion, Theme und Protokoll. 28 px hohe Statusleiste mit Betriebsart, DAQ-Identität, Prüfer und Zeitraffer für die Simulation. |
| Übersicht | Kennzahlen, intern scrollbare Kanaltabelle, Filter nach Typ, Status und Gruppe sowie Mehrfachwahl über Checkboxen. Kalibrierparameter und Ablaufstatus bleiben in einer separat scrollbaren rechten Spalte erreichbar. Der Liveplot zeigt Soll/Ist, Abweichung oder Stabilität. |
| Geräte | Gemeinsamer Gerätewechsel und Verbindungsfelder für DAQ, FNIRSI, OWON und PeakTech. Jede Detailansicht besitzt eigene Messwert- und Bedienfelder. Treibervorgaben und Verbindungsstatus steuern die Verfügbarkeit der Aktionen. |
| Live-Messdaten | Schaltbare Kanalwahl, je Einheit ein Diagramm, Zeitfenster, Halten, Statistik, Rohmesswerte, Aufzeichnung und CSV-Export. Mausrad, Ziehen und Doppelklick dienen zum Zoomen, Verschieben und Zurücksetzen. |
| Prüfabläufe | Versionierte Profile, bearbeitbare Schritte, Fortschritts- und Ergebnisansicht. |
| Berichte | Archivierte Prüfläufe mit Filtern und Messwerttabellen. Die Vorschau verwendet eine helle Papierfläche in beiden Themes. |
| Einstellungen | Seitliche Navigation zu Prüfer, Kalibrierung, Grenzwerten, Referenzen, Simulation, Speicherung und Architektur. Die Geräteverwaltung befindet sich im Reiter Geräte. |
| Justierung | Overlay mit Koeffizientenvergleich, Speicherziel, Pflichtfeldern für Prüfer und Freigabe, Übertragungsstatus und Abbruch. Esc ruft die Abbruchaktion auf. Während der Übertragung sind die Bedienfelder gesperrt. |

## Designressourcen

Die dynamischen Dark- und Light-Ressourcen liegen in [Tokens.axaml](../../src/DAQ.CalibrationStudio.App/Styles/Tokens.axaml). Oberflächen und Text folgen den Handoff-Schlüsseln. Die Hintergrund- und Panelwerte sind im dunklen Schema #111316 und #181B1F und im hellen Schema #F3F4F6 und #FFFFFF. Die Akzente sind #679DCB und #1F6FB2. Status verwenden eigene Grün-, Gelb-, Rot- und Violettwerte und enthalten Text oder ein unterscheidbares Symbol. Der Oszilloskopbildschirm verwendet in beiden Themes Dunkel #0C0E11 mit gelber CH1- und blauer CH2-Kurve.

[Studio.axaml](../../src/DAQ.CalibrationStudio.App/Styles/Studio.axaml) definiert Segoe UI, tabellarische Ziffern als OpenType-Feature und Cascadia Mono für Geräte- und Protokollkennungen. Seitenabstand und Abstand zwischen Hauptpanels liegen bei 16 px, Panelradien bei 8 px, Eingabefelder und primäre Aktionen bei rund 32 px. Das Fenster verwendet geräteunabhängige Pixel und kompaktere Kopf- und Spaltenmaße bei kleinerer Breite. Linien-Icons sind Avalonia-Geometrien. Das Anwendungsicon wurde aus dem Wellenmotiv gezeichnet.

## Abweichungen und Stand

- Die native Übersicht verwendet eine editierbare Punktliste mit Trennzeichen. Der Prototyp stellt einzelne Punkte als entfernbare Pillen dar.
- Der Prüfprofileditor zeigt fachliche Mess-, Frequenz-, Digital- und Warteschritte. Ein Messschritt kapselt Erzeugen, Stabilisieren, Mitteln und Bewerten.
- Die sichtbaren Hardwareeinstellungen richten sich nach der integrierten Treiberschnittstelle. Bei Geräten mit begrenztem Protokollumfang stehen einzelne Einstellungen oder Leseoperationen in der Simulation zur Verfügung.
- Die Live-Ansicht zeigt Rohcode, skalierten und korrigierten Wert des letzten Blocks je ausgewähltem Kanal. Die auf Datenträger gespeicherten Messblöcke können als CSV ausgewertet werden.
- Die native Vorschau zeigt die Art des gespeicherten Prüflaufs. Export und persistierte Messwerte verwenden dieselbe Prüflauf-ID.
- Die Zoom- und Cursorfunktionen verwenden den Desktopplot. Beim Oszilloskop sind A/B-Cursor entlang der Zeitachse verschiebbar. Das Mausrad ändert den sichtbaren Ausschnitt.
- Die Oszilloskopachsen verwenden dynamische Ticks. Die Vorlage kombiniert ein festes Raster mit zusätzlichen Trigger- und Kanalmarkern.
- Die visuelle Referenz nutzt simulierte Vorbelegungen. In der Anwendung stammen Messwerte und Verlauf aus der aktuellen Simulation oder dem tatsächlich verbundenen Gerät und die Historie aus dem lokalen Archiv.

## Visueller Vergleich

Zum Vergleich bei 1920 × 1080 dieselbe Seite und Betriebsart sowie das gleiche Farbschema öffnen. Handoff 01 bis 09 zeigen die regulären Ansichten. Handoff 10 bis 13 zeigen laufende und abgeschlossene Kalibrierung, die Freigabe und das eingeblendete Fehlerprotokoll. Bei kleinerer Fenstergröße zusätzlich prüfen, dass Tabellen horizontal und die Seiten vertikal erreichbar bleiben. Der bedienbare Viewport beginnt bei 1180 × 720 logischen Pixeln.

Die lokale headless Prüfung hat alle Hauptansichten und Geräte in beiden Farbschemata gerendert. Sie erfasste den laufenden und abgeschlossenen simulierten Prüflauf, die Freigabe, den gespeicherten Prüfbericht, den simulierten FNIRSI-Fehler bei geöffnetem Protokoll sowie die kompakte Übersicht bei 1180 × 720. Die Vergleichsbilder liegen lokal unter artifacts/ui/headless.
