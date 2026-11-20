# DAQ Calibration Studio

Native Avalonia-Desktopanwendung für Kalibrierung, Verifikation und Funktionsprüfung der STM32H7-DAQ. Die sechs Arbeitsbereiche entsprechen dem Claude-Canvas: Übersicht, Geräte, Messdaten, Prüfabläufe, Berichte und Einstellungen.

## Start unter Windows

Voraussetzung für den bereitgestellten Windows-x64-Build ist Windows 10 (22H2) oder Windows 11. Der eigenständige Build enthält die .NET-Laufzeit. `DAQ.CalibrationStudio.App.exe` direkt aus dem vollständigen Publish-Ordner starten.

Die Anwendung startet im Simulationsmodus und verbindet die vier virtuellen Geräte. Im Hardwaremodus werden Verbindungen pro Gerät ausdrücklich geöffnet. Der Modus ist in der Statusleiste sichtbar.

1. In der Übersicht z. B. **AI-V3** auswählen und **Kalibrierung starten**. Die Anwendung setzt nacheinander die Prüfpunkte, wartet auf stabile Messwerte, mittelt und bewertet den Messfehler.
2. **Bericht öffnen** zeigt den gespeicherten As-found-Befund. CSV und PDF stehen im Bereich **Berichte** zur Verfügung.
3. **Ergebnis übernehmen** öffnet die Vorschau der Justierung. Prüfer-Kennung, Checkbox und Auswahl von RAM oder Flash sind erforderlich. Die simulierte DAQ liest die geschriebenen Werte zur Kontrolle zurück.
4. **Verifikation** verwendet dieselben Prüfpunkte für eine neue As-left-Messung. Für eine Stromprüfung das Standardprofil 0–20 mA oder 4–20 mA unter **Prüfabläufe** wählen.

Passende Profile für einen Encoder-Frequenztest, digitale Loopbacks und eine simulierte Gesamtprüfung sind vorkonfiguriert. Der Profil-Editor speichert Änderungen als neue Revision. Der Zeitraffer wirkt ausschließlich auf die Simulation.

## Geräte

| Gerät | Simulation | Hardwaremodus |
| --- | --- | --- |
| STM32H7-DAQ | Analogeingänge, RTD, Encoder, digitale Loopbacks, RAM-/Flash-Justierung | USB-CDC-Diagnosesnapshots über Firmware-Protokoll 1, explizites Start/Stopp. Ethernet-Datenstrom und Schreiben der berechneten Koeffizienten sind in der Firmware noch offen. |
| FNIRSI SG-004A | Spannung, Strom, Sollwert-Rücklesen, Ein/Aus und Prüfpunktautomatik | Serielle Kommunikation, Sollwerte im vorhandenen Modus explizit programmieren und rücklesen. Die Ausgangsschaltung und ein sicherer Moduswechsel sind noch nicht bestätigt. |
| OWON DGE2035 | CH1/CH2, Sweep, synchronisierte A/B-Encoder-Simulation | Modellgeprüfte SCPI-Befehle über USBTMC/VISA. Abnahme am realen Generator und dynamische Quadratur stehen aus. |
| PeakTech P 1400 | CH1/CH2, Trigger und Kurvenanalyse | Wiederverwendeter USB-Treiber für Bildschirmkurven und dokumentierte Bedieneinstellungen. Die Kurven sind Min/Max-Hüllkurven. Bandbreite und Vertikalauflösung betragen 5 MHz und 8 Bit. |

Den Anschluss, die optionalen Herstellertreiber, den historischen Hardware-Nachweis und die exakten Einschränkungen pro Funktion beschreibt die [Geräteintegration](docs/instruments/README.md).

## Entwicklung

.NET 10 SDK und Windows x64. Die Projekte kompilieren und testen ohne angeschlossene Messgeräte.

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release --no-build
dotnet run -c Release --project src/DAQ.CalibrationStudio.App
```

Release erstellen mit `pwsh -File tools/Publish-Windows.ps1`. Der Standardpfad ist `artifacts/publish/win-x64`. Der Quellcode, die wiederverwendeten Treiber und alle für die Simulation benötigten Dateien liegen im Repository. Eine Treiberinstallation für den Hardwarebetrieb erfolgt über die jeweilige Geräteumgebung.

Standarddatenordner `%LOCALAPPDATA%\DAQ Calibration Studio`. Mit `--data-dir "C:\Pfad\Testdaten"` lässt sich ein isolierter Prüfdatensatz verwenden. `--hardware` startet im getrennten Hardwaremodus. SQLite speichert Prüfläufe und Profile, CSV-Dateien enthalten aufgezeichnete Messblöcke und JSONL protokolliert Ereignisse. Simulation und Hardware sind in den Ergebnisdaten und Exporten gekennzeichnet.

## Projektstruktur

| Verzeichnis | Aufgabe |
| --- | --- |
| `src/DAQ.CalibrationStudio.App` | Avalonia-Views, MVVM, Desktopintegration und Benutzerführung |
| `src/DAQ.CalibrationStudio.Domain` | Kanalkatalog, Prüfprofile, Korrekturen, Berechnungen und Messdaten |
| `src/DAQ.CalibrationStudio.Application` | Geräteschnittstellen, Messdatenpuffer, Laufsteuerung und Freigabe |
| `src/DAQ.CalibrationStudio.Instruments`, `src/DAQ.CalibrationStudio.Daq` | Reale Gerätetreiber, Firmware- und USB-Anbindung |
| `src/DAQ.CalibrationStudio.Simulation` | Vier zusammenhängende Geräte und simulierter Prüfadapter |
| `src/DAQ.CalibrationStudio.Infrastructure` | SQLite, versionierte Datenhaltung, CSV und PDF |
| `tests` | Rechen-, Ablauf-, Persistenz-, Treiber- und UI-Tests |

[Architektur](docs/architecture/README.md) · [Prüfablauf und Berechnung](docs/calibration/README.md) · [UI-Design](docs/ui/design.md) · [Daten und Berichte](docs/development/data.md) · [Validierung](docs/development/validation.md)
