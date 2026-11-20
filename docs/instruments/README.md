# Geräteschnittstellen

Der Hardwaremodus startet ohne aktive Sitzungen. Die Gerätesuche liefert COM-Kandidaten, VISA-Ressourcennamen oder die USB-Seriennummer des PeakTech. Erst **Verbinden** spricht das ausgewählte Ziel mit dem jeweiligen Protokoll an. Bei FNIRSI stammt die Modellangabe aus der Gerätekonfiguration. OWON, PeakTech und DAQ prüfen eine Modellkennung aus der Geräteantwort.

| Gerät | Übernommene und integrierte Funktionen | Automatische Prüfung im Studio | Physischer Nachweis |
| --- | --- | --- | --- |
| FNIRSI SG-004A | 9600 8N1, Registerrahmen mit CRC, Firmware-Rohwort, Modus, Eingangsregister, typisierter Sollwert, Sollwertprogrammierung im bereits am Gerät eingestellten Modus | 23 übernommene .NET-Protokollfälle sowie Adaptertest mit synthetischem Byte-Transport | COM-Port, Protokoll, Betriebsmodi und elektrische Wirkung offen |
| OWON DGE2035, DGE2070, DGE3062 | USBTMC/VISA, Modellprofile, zwei Kanäle, Sinus/Rechteck/Puls/Rampe, Frequenz, Vpp, Offset, Phase, Puls-Tastgrad, High-Z/50 Ω, Timer-Sweep und Statusrücklesung | 24 übernommene .NET-Protokollfälle sowie Adaptertest mit synthetischem SCPI | VISA-Treiber, Modellantworten und Ausgangsfunktionen offen |
| PeakTech P 1400 | SCREEN CH1/CH2 mit Min/Max-Hüllkurve, Geräte-ID, Auswahl bestätigter Bedienbefehle, Einzeltrigger | Synthetische Python-Tests für Abschnittsgrößen, CH1/CH2, Skalierung, Rücklesung und abgewiesene Einstellungen | Historisch am P 1400 V1.2.1 geprüft. Neuer Studio-Ablauf mit Python-Brücke offen |
| STM32H7-DAQ RevB | USB CDC, Protokoll 1, Identifikation, Diagnose, explizites Start/Stopp, Analog-/RTD-/Digital-Snapshots und Rohzähler | DAQ-Firmware-/Hosttests der Vorprojekte; Studio-Adaptertests mit synthetischen Serviceantworten | USB, Messdaten, Start-/Stopp-Verhalten und Messgenauigkeit am Board offen |

## FNIRSI SG-004A · USB / COM

COM-Port im Windows-Geräte-Manager dem SG-004A zuordnen und erst dann auswählen. Die Zuordnung aus der COM-Liste erfordert den Benutzer, da die Vorarbeit keine bestätigte USB-VID/PID oder Seriennummer liefert. Beim Verbinden wird Register 40001 gelesen und als Hexwort angezeigt.

Die Treibergrenzen für das Programmieren eines Sollwerts sind 0–24 V bzw. 0–24 mA. Die App reicht Strom in mA weiter. Zuerst den passenden aktiven Strom- oder Spannungsmodus am Gerät einstellen. Die Programmierung prüft den exakten Modus, Registeradresse und Anzahl im ACK, Wert und danach den Modus erneut. Der Befehl kann einen bereits aktiven Ausgang verändern. Die Software kennt den elektrischen Ausgangszustand nicht. Modusumschaltung und Ausgang EIN/AUS sind gesperrt, da die Registerwirkung von 40003 und 40008 nicht belegt ist. Das schließt automatische Prüfabläufe mit dieser physischen Quelle derzeit aus.

Gerätesollwert, Anforderung und unabhängiger Referenzmesswert sind getrennte Felder. Das Eingangsregister 40004 zeigt den Eingang des SG-004A. Das Rohwort 40008 bleibt ein Diagnosewert. Eine abgebrochene oder mehrdeutige Antwort schließt den Port. Vor Wiederholung den Ausgang am Gerät kontrollieren.

Die Register- und Rahmenreferenz stammt aus dem Herstellerhandbuch V0.2. Für 9600 8N1, den Registerrahmen und die angenommenen Moduscodes diente zusätzlich der gepinnte Wirenboard-Stand als unabhängige Protokollreferenz. Der lokale C#-Treiber wurde im Vorprojekt eigenständig geschrieben. Python-Fremdcode von Wirenboard wurde wegen der ungeklärten Lizenzlage nicht übernommen. [Herstellerhandbuch V0.2](https://www.docs.beti.com.tr/FNIRSI/SG-004A/SG-004A-Datasheet.pdf) · [Untersuchter Wirenboard-Stand](https://github.com/wirenboard/fnirsi-sg004a/tree/daaad7590d85307c53e009e7d3135a7f935ded43)

## OWON DGE · USBTMC / VISA

Erfordert Windows mit vorhandener VISA-Laufzeit in passender Prozessarchitektur und funktionierendem USBTMC-Treiber. Die Anwendung sucht die systemweite VISA-Bibliothek und listet USB-INSTR-Ressourcen. Beispielsyntax: `USB0::0x1234::0x5678::SERIENNUMMER::INSTR`. Die Modellwahl erfolgt per `*IDN?`. `viOpen` fordert exklusiven Zugriff an. Die App installiert keine Treiber. [VISA-Suche](https://www.ni.com/docs/en-US/bundle/ni-visa-api-ref/page/ni-visa-api-ref/vifindrsrc.html) · [VISA-Sitzung und Sperren](https://www.ni.com/docs/en-US/bundle/ni-visa-api-ref/page/ni-visa-api-ref/viopen.html)

Die Kanalparameter verwenden Hz, Vpp, V, Grad und Prozent. Einstellbar sind 50 Ω und High-Z. Die Lastangabe steht für die Anzeige- und Pegelskalierung der Quelle. Die Grenzprüfung berücksichtigt Signalform, Modell, Frequenz, Amplitude, Offset und Lastanzeige. Der bestätigte Kanalstatus und die Werte stammen aus SCPI-Rücklesungen. Reale Pegel und tatsächliche Last separat messen. Das Tastverhältnis ist für den Puls dokumentiert. Für Rechteck wird 50 % für die Sollkurve angenommen. Rauschen, DC und Arbiträrformen sind im Hardwarepanel gesperrt.

Vor einer Neukonfiguration liest der Adapter OFF zurück. Er prüft die programmierten Einstellungen vor dem Einschalten, danach den gemeldeten Zustand. Beim Sweep prüft er Start, Ende, Dauer und linearen oder logarithmischen Modus. Der vorhandene Treiber aktiviert den validierten Festträger kurz vor dem Sweep-Start. Stopp schaltet den zugehörigen Ausgang aus. Ein verifizierter Ausgangszustand setzt eine antwortende Sitzung voraus. Bei Verbindungsverlust kann der Ausgang aktiv bleiben. Das gilt auch bei Zeitüberschreitung während eines nativen VISA-Aufrufs. Der Transport wartet dessen begrenzte I/O-Frist ab.

Die VISA- und SCPI-Antworten der drei Modelle sind noch am Gerät zu prüfen. Das Programmierhandbuch nennt DGE2000. DGE3062 verwendet im Studio ein eigenes konservatives Profil für die gemeinsame dokumentierte Teilmenge. Für dynamische A/B-Quadratur fehlt ein Nachweis phasenstarrer Frequenzänderungen, daher bleibt diese Betriebsart in der Simulation. Die spätere RS422-Leitungstreiberstufe gehört zum externen Aufbau. [OWON-Programmierhandbuch](https://www.owon.co.jp/download/01a11fa0-84c7-7dbf-894c-c655b032c508/)

## PeakTech P 1400 · USB Bulk

Erfordert Windows mit vorhandenem libusb-win32-Treiber, `libusb0.dll` in der Systemarchitektur des Python-Prozesses und installiertem Python 3.10 oder neuer. Die Anwendung sucht eine vorhandene `python.exe` im PATH oder in der Bestandsliste des Python-Launchers. Optional die vorhandene Datei mit `DAQ_CALIBRATION_PYTHON` als vollständigen Pfad angeben. Die Python-Brücke nutzt die Standardbibliothek. Es sind keine pip-Pakete erforderlich. DS_Wave schließen und genau ein P 1400 anschließen. Die explizit ausgewählte USB-Seriennummer, VID/PID `5345:1234`, Interface mit Bulk `01/81` und Modellkennung müssen passen.

Die Anwendung übernimmt Fenster-Zeitbasis, vertikale Skala, Offset und Tastkopffaktor aus dem Geräteheader. `SCREEN` enthält ein Max/Min-Paar je Bildschirmposition. An beiden Kanälen wird die Hüllkurve dekodiert. Die Mittellinie dient der Anzeige. Zeitnull ist der Fensteranfang. Die beiden Blöcke werden nacheinander abgefragt. Eine garantierte gleichzeitige Erfassung zwischen den Kanälen oder ein exakter Triggerzeitpunkt kann daraus nicht abgeleitet werden. Hüllkurven liefern Extremwerte. Für berechnete Mittelwerte, RMS, Tastgrad und Phase ist die Aussagekraft durch Fenster, Quantisierung, fehlende Zeitordnung im Paar und möglichen Kanalversatz begrenzt. Bei invertierten Kanälen und abweichenden Fensterformaten schlägt die Dekodierung mit einem Hinweis fehl.

Fernsteuerung mit Rücklesung umfasst 100 µs, 500 µs und 1 ms je Division, 0,5 und 1 V je Division, AC/DC/GND-Kopplung, CH2 ein-/ausblenden, Triggerquelle und -flanke, Triggerpegel 2,5 und 3,5 V und RUN/STOP/Single. Für CH1-Sichtbarkeit das Frontpanel verwenden. Auslesen ist mit weiteren Skaleneinstellungen möglich. Einzeltrigger wird als Modus rückgelesen. Die Aufnahme endet nach einem passenden Signaltrigger. Deep-Memory-Abruf, invertierte Skalierung und zeitgenaue Phasenmessung sind nicht in das Studio übernommen. Das Gerät hat 5 MHz analoge Bandbreite und eignet sich für Funktionsprüfung in diesem Messbereich. [P-1400-Handbuch](https://www.peaktech.de/media/e3/ff/2b/1739280169/PeakTech_1400-1404_02-2025_EN.pdf)

## STM32H7-DAQ RevB · USB CDC

Den im Geräte-Manager zugeordneten CDC-Port auswählen. USB nutzt DTR, ASCII-Zeilen mit LF und 500 ms Antwortfrist. Sitzungsstart liest `*IDN?` und erwartet `model=DAQ hw=B proto=1`. Firmwareflags `control=1` und `readings=1` schalten die zugehörigen Funktionen frei. Die Verbindung startet die Erfassung nicht. Der erste Start erfordert eine bewusste Aktion und die Freigaben der Firmware. Der Firmware-Standardbuild hält die Erfassung gesperrt. Beim USB-Verlust kann die Erfassung auf dem Board weiterlaufen. Nach Wiederverbindung den Zustand prüfen.

- `MEAS:ANALOG?` liefert den jüngsten für die USB-Abfrage gültigen Messsatz von V1–V3 und I1–I3. Die Rohcodes sind vorzeichenbehaftete 24-Bit-Werte. Die Werte in V bzw. mA sind bereits mit der Firmwarekalibrierung berechnet. Der USB-Snapshot überträgt keine vorher skalierten Werte ohne Korrektur. Ungültige Werte bleiben leer.
- `MEAS:RTD?` liefert RTD1–RTD4 in Ω und °C. Das Studio-Messwertmodell verwendet Widerstand. Die Firmware prüft Alter und Gültigkeit je Kanal. Die Produktkonfiguration erlaubt RTD-Typen PT100 und PT1000 jeweils in Vierleiterverdrahtung.
- `MEAS:IO?` liefert DI1–DI8, sofern gültig, und Zähler für den Encoder sowie HSC4–HSC6. Vollständige Zählerstände, Übererfassung, empfangene und produzierte Frames stehen im USB-Adapter als Diagnosewerte zur Verfügung. Ein Zählerstand ist keine Frequenzmessung. In Frames ohne passenden Datentyp bleibt der Anzeigewert leer.
- Der PC vergibt Zeit und Sequenz der vollständigen Abfrage aus drei Gruppen. Die Firmware liefert keinen gemeinsamen Gerätezeitstempel für diese Snapshots. Geräteframes oder Paketverluste lassen sich damit nicht rekonstruieren.
- Die Firmware kann einen geräteintern aufgenommenen Zweipunktentwurf verifiziert im EEPROM sichern. Ein Befehl für den vollständigen aktiven Koeffizientensatz oder zur Übertragung frei berechneter Studio-Koeffizienten fehlt. Der reale Adapter sperrt deren Übernahme. Das gilt auch für Umschaltrouting und Digitalausgänge.
- Ethernet-Rohdatenstreaming befindet sich noch in der Firmware-Entwicklung. `DAQ1` ist das Format eines Offline-Encoders. Der vorhandene TCP-Sendebaustein ist im geprüften Firmwarestand weder mit Netzwerkstack und Netzwerkschnittstelle noch mit dem Messdatenring verbunden. Das Studio behandelt USB als Diagnose- und Snapshot-Transport mit deutlich weniger als 8.000 Samples/s je Kanal.

[Firmware-USB-Protokoll](https://github.com/Fuschi308/DAQ_STM32_Firmware/blob/eeb10c8f959628d4d2e61baf76ead38cf0a65a2b/docs/service_protocol.md) · [Firmware-Encoder und TCP-Stand](https://github.com/Fuschi308/DAQ_STM32_Firmware/blob/eeb10c8f959628d4d2e61baf76ead38cf0a65a2b/README.md)

## Quellen im Projekt und Übernahme

| Ausgangsquelle | Übernommene Dateien und Anpassungen |
| --- | --- |
| `FNIRSI Schnittstelle/src/Fnirsi.SG004A/` | `FnirsiClient.cs`, `IByteTransport.cs`, `SerialByteTransport.cs`, `SgProtocol.cs` liegen unter `Instruments/Fnirsi/`. Das Verhalten bei Portentfernung während Abbruch wurde angepasst. `FnirsiDevice.cs` ist der Studio-Adapter. `ProtocolTests.cs` und `SimulatedByteTransport.cs` stammen aus den Test- und Simulatorordnern des Vorprojekts. |
| `OWON Schnittstelle/src/Owon.DGE/` | `OwonDgeClient.cs` und `VisaUsbTmcTransport.cs` liegen unter `Instruments/Owon/`. Ergänzt wurden VISA-Suche und exklusives Öffnen, automatische Auswahl geprüfter Modellprofile und Rücklesen der Sweep-Parameter. `OwonDevice.cs` implementiert die Studio-Sitzung. `OwonSimulatorTransport.cs` stammt aus dem Simulatorprojekt. Die 24 ursprünglichen Console-Testfälle laufen als xUnit-Fälle. |
| `PeakTech Schnittstelle/python/` | `peaktech_p1400.py` und `libusb0_windows.py` sind unter `Instruments/PeakTech/` gebündelt. Ergänzt wurden kanalbezogene Dekodierung, 64-Byte-Headerauftakt mit anschließend größenbegrenztem Lesen und Deskriptorsuche. Die persönliche USB-Seriennummer wurde entfernt. `studio_bridge.py` verbindet das Protokoll mit dem .NET-Adapter `PeakTechDevice.cs`. |
| `Integration/src/Calibration.Instruments.Abstractions/` und `Integration/device-interfaces.md` | Gemeinsame Verträge für Sitzungen, Capability, Signalsteuerung und Trennung von Referenz und Sollwert waren die Architekturgrundlage. Die Studio-Verträge leben in `Application/DeviceContracts.cs`. Die Integration benötigt das frühere Projekt zur Laufzeit nicht. |
| `DAQ_Service_Tool/src/Daq.Service.Protocol/` | `ILineTransport.cs`, `ProtocolFields.cs`, `SerialLineTransport.cs`, `ServiceClient.cs` und `ServiceProtocol.cs` wurden aus dem Nutzer-Repository übernommen. `UsbDaqDevice.cs` bindet die für Studio benötigten Abfragen an die gemeinsamen Verträge an. [Service-Tool-Quellstand](https://github.com/Fuschi308/DAQ_Service_Tool/tree/b48dec1c5fb43d8ac20a3eaa0a40cfe5e8c105c3/src/Daq.Service.Protocol) |

Die Originalordner und Firmware blieben unverändert. Herstellerbibliotheken, Geräteinstallationspakete und aufgezeichnete Hardwaredaten gehören nicht zu diesem Repository. Die Vorarbeiten enthalten keine separat ausgewiesene Lizenz für die lokalen Eigenimplementierungen. Die hier übernommenen Dateien sind der im Entwicklungsauftrag genannte Projektquellcode. `System.IO.Ports` wird als NuGet-Abhängigkeit verwendet. Die Testtransporte und öffentlichen Testdaten verwenden synthetische Antworten.

## Reproduzierbare Offline-Prüfung

```powershell
dotnet test tests/DAQ.CalibrationStudio.Protocol.Tests/DAQ.CalibrationStudio.Protocol.Tests.csproj -c Release
python -B tests/DAQ.CalibrationStudio.Protocol.Tests/test_peaktech_bridge.py -v
```

Die Tests benötigen weder Messgeräte noch VISA- oder libusb-Treiber. Vor realer Freigabe sind die beschriebenen Funktionen an den eingesetzten Modellen, mit bekannten Referenzen und definiertem mechanischem und elektrischem Aufbau zu prüfen.
