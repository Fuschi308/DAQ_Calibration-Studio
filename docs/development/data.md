# Persistenz und Berichte

## SQLite

`SqliteStudioStore` implementiert `IStudioStore`. Die Datenbankdatei wird dem Konstruktor übergeben. Für die WPF-Anwendung sollte sie in einem beschreibbaren Anwendungsdatenordner liegen, nicht im Installationsverzeichnis.

Beim ersten Zugriff werden SQL-Dateien aus `Migrations/` in aufsteigender Versionsreihenfolge angewendet und in `SchemaMigrations` protokolliert. Ergänze Schemaänderungen als neue nummerierte Datei, zum Beispiel `002_ReferenceIndex.sql`; vorhandene Migrationen dürfen nicht nachträglich geändert werden.

Prüfprofile werden nach Profil-ID und Versionsnummer gespeichert. Eine bereits gespeicherte Version ist unveränderlich; eine Anpassung benötigt eine neue Nummer. Ein abgeschlossener Lauf speichert das vollständige JSON-Abbild des Domain-Datensatzes einschließlich Profil, Geräte- und Referenzinformationen, Punkten, Koeffizienten und Simulationsstatus. Der JSON-Inhalt erhält einen SHA-256-Prüfwert. Beim Abruf wird der Wert geprüft, bevor das Abbild zurückgegeben wird.

Prüfläufe und Freigaben sind durch SQLite-Trigger gegen Update und Delete geschützt. Neue Freigaben werden als eigene Datensätze mit Prüfer, Ziel, Koeffizienten, Rücklesestatus und Fehlerhinweis angehängt. Einstellungen dürfen überschrieben werden. Suchfilter unterstützen Text, Zeitraum, Seriennummer, Profil-ID und Ergebnis.

Der SHA-256-Wert erkennt unbeabsichtigte Änderungen. Er ist keine kryptographische Signatur und schützt nicht vor einer Person, die Datenbankinhalt und Prüfsumme gemeinsam manipuliert. Für echte Manipulationserkennung außerhalb des lokalen Rechners wäre eine extern signierte oder anderweitig geschützte Ablage erforderlich.

## Messdatenaufzeichnung

`CsvMeasurementWriter` schreibt eingehende `DaqFrame`- und `ChannelSample`-Werte zeilenweise in eine separate UTF-8-CSV-Datei. Damit landen hochfrequente Messdaten nicht als einzelne Datenbankzeilen in der Historie. Der Dateikopf kennzeichnet ausdrücklich, ob die Aufzeichnung aus einer Simulation oder einem DAQ-Messlauf stammt. Die Datei wird neu angelegt und nicht an vorhandene Daten angehängt.

## Berichte

`ReportExporter.WriteCsv` und `WritePdf` erhalten den gespeicherten `RunResult`-Snapshot sowie optional die zugehörigen `Approval`-Datensätze. Die Berichtsart ergibt sich aus dem Prüfprofil: Funktionsprüfbericht, Kalibrierprotokoll oder Verifikationsbericht. CSV verwendet Semikolon, Dezimalkomma und UTF-8-BOM für die direkte Verwendung in deutschsprachigen Tabellenprogrammen. PDF nutzt PDFsharp-MigraDoc 6.2.4 (MIT-Lizenz); die PDF-Generierung rendert den vollständigen Bericht mit Kopfbereich, Tabellen, Seitenzahlen und automatischem Seitenumbruch.

Jeder Bericht nennt den Simulationsstatus, dokumentiert Referenzgeräte und zeigt die Rückführbarkeitsangabe ohne daraus eine Kalibrierbescheinigung abzuleiten. Fehlende oder nicht verifizierte Messunsicherheiten werden ausdrücklich kenntlich gemacht. Ein vorgeschlagener Koeffizient wird als Vorschlag ausgewiesen; ein erfolgreicher DAQ-Schreibvorgang wird nur bei protokollierter Freigabe mit verifizierter Rücklesung behauptet.

## Paketwahl

- `Microsoft.Data.Sqlite.Core` 10.0.13 mit `SQLitePCLRaw.bundle_winsqlite3` verwendet die von Windows bereitgestellte SQLite-Bibliothek und vermeidet das zuvor bekannte Updateproblem der mitgelieferten SQLite-Binärdatei. Die Anwendung ist Windows/WPF; der Provider setzt Windows 10 oder neuer voraus.
- `PDFsharp-MigraDoc` 6.2.4 ist die letzte MIT-lizenzierte PDFsharp/MigraDoc-Version, die hier eingesetzt wird. Spätere 6.5.x-Versionen verwenden AGPL-3.0-only.
- Direkte Referenz auf `System.Security.Cryptography.Pkcs` 10.0.13 hält den PDF-Abhängigkeitsbaum auf einer gepatchten Version.

`dotnet list package --vulnerable --include-transitive` sollte vor Paketupdates erneut geprüft werden. Die SQLite-Bibliothek von Windows stammt vom installierten Betriebssystem; dieser Paketweg bringt daher keine plattformübergreifende native SQLite-Datei mit.

## Überprüfung

Die Integrationstests prüfen Wiederöffnung der Datenbank, Profilversionen, Append-only-Regeln, Freigabe- und Einstellungsdaten, Filter, PDF-Erzeugung und Seitenumbruch sowie CSV-Inhalte und Simulationskennzeichnung.
