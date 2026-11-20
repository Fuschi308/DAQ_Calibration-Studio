# Validierung

Die Software baut auf .NET 10. Die Release-Tests verwenden simulierte Geräte und synthetische Protokollantworten. Für die automatisierten Tests sind keine Hersteller- und USB-Treiber erforderlich.

| Prüfpfad | Abdeckung |
| --- | --- |
| Kalibrierung | Offset, Gain, lineare Regression, Toleranzen, Einheiten und Stabilität. Vollständige Spannungs-, Strom-, Frequenz- und Digitalprüfung, Speichern, Pause, Abbruch und Geräteausfall. Explizite Freigabe, Rücklesen, Rücksetzung und zusammenhängende A/B-Frequenz im Simulator. |
| Persistenz und Bericht | SQLite-Neustart, unveränderliche Laufdaten, Profilrevisionen, Freigabeprotokoll und Dateiexporte. PDF-Tests mit Mehrseitenumbruch. |
| Geräteprotokolle | Übernommene FNIRSI- und OWON-Fälle, synthetische Adapterantworten für die USB-Anbindung und Python-Testfälle für PeakTech. |
| Avalonia | Headless-Tests gegen die produktive Composition Root und native Ansichten. Navigation, Theme, Messwerte, Gerätefunktionen, Start, Abbruch, Freigabe und gespeicherte Berichte. |

## Prüfen und veröffentlichen

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
python -B -m unittest discover -s tests/DAQ.CalibrationStudio.Protocol.Tests -p test_peaktech_bridge.py
pwsh -File tools/Publish-Windows.ps1
```

Für reproduzierbare Vergleichsbilder optional `DAQ_UI_QA_DIR` auf einen lokalen Ausgabeordner setzen und die App-Tests starten. Die Tests rendern alle sechs Arbeitsbereiche in Hell und Dunkel bei 1280 × 800 und 1920 × 1080 aus echten Avalonia-Controls. Screenshots und Messdaten sind lokale Artefakte. Geprüft wurde unter Windows 11 Pro x64. Der Windows-10-Lauf war in dieser Umgebung nicht verfügbar.

## Hardwareabnahme

Die Studio-Adapter wurden offline geprüft. Die verfügbaren Funktionen und der historische PeakTech-Nachweis sind in der [Geräteintegration](../instruments/README.md) beschrieben. Reale Akzeptanztests bleiben für den konkreten Prüfaufbau offen. Für automatische Hardwarekalibrierung fehlen ein bestätigter FNIRSI-Schaltpfad und der verifizierte externe Prüfadapter. Die aktuell über USB zugänglichen DAQ-Daten sind Diagnosesnapshots. Ein gerätezeitgestempelter Ethernet-Datenstrom und frei beschreibbare Studio-Koeffizienten erfordern eine passende Firmware-Schnittstelle.
