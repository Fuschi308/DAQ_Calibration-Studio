# Prüfen, kalibrieren und justieren

**Funktionsprüfung** vergleicht Messungen mit vorgegebenen Grenzen. **Kalibrierung** ermittelt den Istzustand der vorhandenen Korrektur (As-found). **Justierung** ändert Gain und Offset nach Freigabe. **Verifikation** ist ein eigener Prüflauf und kann nach einer Justierung den neuen Messzustand dokumentieren.

## Prüfschritte

Ein Profil enthält analoges Anfahren, Frequenzmessung, digitalen Loopback und Wartezeiten in konfigurierbarer Reihenfolge. Pro Analogpunkt prüft die Engine Ausgang und Geräte-Rücklesung, ermittelt das Stabilitätsfenster und erfasst anschließend die Mittelung. Toleranzen für Volt, mA und Ω sind absolut. Frequenzen werden relativ zur Prüffrequenz in ppm bewertet. Digitale Prüfungen vergleichen LOW oder HIGH.

Ein abgebrochener oder fehlgeschlagener Lauf bleibt mit den vorhandenen Punkten in der Historie. Bei Pause schaltet der Ablauf die verwendeten Prüfstimuli aus und fährt den betroffenen Punkt beim Fortsetzen erneut an. Ein übersprungener Punkt macht den Lauf unvollständig und sperrt die resultierende Justierung.

## Rechnung

- Vorzeichenbehafteter Fehler `Ist − Referenz`. Die Größe der Abweichung entscheidet über die absolute Toleranz.
- Prozent vom Messbereich `Δ / Spanne × 100`, Prozent vom Messwert `Δ / |Referenz| × 100`. Bei einer Referenz von null entfällt die Anzeige in Prozent vom Messwert.
- Die Engine vereinigt Mittelwert, Varianz und Anzahl der einzelnen Blöcke. Das Stabilitätskriterium prüft Standardabweichung und Spannweite innerhalb des gewählten Fensters.
- Nur Offset hält den Gain konstant. Zweipunkt verwendet die äußeren Referenzpunkte. Mehrpunkt schätzt `unkorrigiert = a × Referenz + b` mittels kleinster Quadrate. Der Korrektursatz ist `Gain = 1/a`, `Offset = −b/a`.
- Die maximale absolute Restabweichung dokumentiert die Fit-Linearität. Eine unabhängige Referenz mit Standardunsicherheit kann mit der Standardunsicherheit des Mittelwerts kombiniert werden. Die erweiterte Unsicherheit verwendet `k = 2`, TUR bezeichnet das Verhältnis von Toleranz zu erweiterter Unsicherheit.

Der Geräte-Sollwert, sein Rücklesewert und ein unabhängig gemessener Referenzwert sind getrennte Felder. Ohne unabhängig gemessene Referenz kennzeichnet Studio die Aussagekraft im Export. Die Simulation erzeugt einen kleinen modellierten Quellfehler und reproduzierbare DAQ-Abweichungen. Rückführungsangaben erfordern die Messung und passende Referenzdokumente.

## Freigabe

Der As-found-Bericht wird beim Abschluss gesichert. In der Vorschau sind ursprüngliche und berechnete Koeffizienten sowie die erwartete Restabweichung sichtbar. Die Freigabe prüft Prüfer-Kennung, Modus, DAQ-Identität und unveränderte Ausgangskoeffizienten. Der Schreibvorgang wird vor dem Transfer protokolliert. Die Bestätigung folgt erst nach Vergleich der zurückgelesenen Werte. Simuliertes Flash überdauert den Neustart, RAM gilt bis zur Trennung oder zum Neustart.


Die Rücksetzung in der Simulation erfordert eine Prüfer-Kennung und eine Bestätigung der ausgewählten Kanäle. Der Vorgang sichert die aktiven Koeffizienten, schreibt Gain 1 und Offset 0 in simuliertes Flash und verifiziert die Rücklesung. Die Historie markiert die Sicherung als Verwaltungsvorgang.
