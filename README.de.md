# DS5 BT Haptics (Deutsch)

DualSense-HD-Haptik über Bluetooth unter Windows, ohne Dongle. Eine reine Software-Version von
[DS5Dongle](https://github.com/awalol/DS5Dongle): Das Programm stellt Windows einen virtuellen
USB-DualSense bereit (über usbip-win2) und leitet alles an den Bluetooth-Controller weiter,
auch die Haptik, die Spiele nur per Kabel senden.

## Installation

1. **`DS5BtHaptics-Setup.exe`** aus dem [neuesten Release](https://github.com/yyusvf/ds5-bt-haptics/releases/latest)
   laden und ausführen (für den aktuellen Benutzer, ohne Adminrechte; die .NET-8-Desktop-Runtime
   wird bei Bedarf geladen).
2. Die App öffnet sich neben der Uhr und führt Schritt für Schritt durch das, was fehlt:
   usbip-win2 installieren, HidHide installieren, HidHide einrichten (einmal Adminrechte).
   Beide Treiber kommen aus ihren offiziellen GitHub-Releases, Prüfsumme und Signatur werden geprüft.
3. Controller einschalten (PS-Taste). Fertig: Die App startet mit Windows und lebt im Infobereich.

DSX, DS4Windows u. ä. nicht gleichzeitig laufen lassen. Beim Deinstallieren lässt sich der
Bluetooth-Controller in HidHide wieder für Spiele sichtbar machen.

## Benutzung

Klick auf das Tray-Symbol öffnet das Flyout: Bridge an/aus, Status von Controller (Akku),
virtuellem USB-Controller und Haptik, **Testen** spielt ein Testmuster wie ein Spiel. In den
Einstellungen: Haptik-Stärke (sofort wirksam), geringere Latenz, Autostart, Darstellung.

**Spiele mit eigener Bluetooth-Haptik** (z. B. *Cyberpunk 2077*, wird in Steam-Bibliotheken
automatisch gefunden, weitere lassen sich hinzufügen): Solange eines läuft, pausiert die Bridge und
das Spiel steuert den Bluetooth-Controller direkt, sonst kommen sich beide in die Quere. Das Flyout
fragt einmal nach der HidHide-Freigabe für diese Spiele.

Punkt am Tray-Symbol: grün = bereit, gelb = wartet auf Controller, rot = Einrichtung nötig. Nach
dem Aus- und Einschalten des Controllers verbindet die Bridge selbst neu. **Spiele nach dem
Verbinden starten.** Bleibt ein Spiel stumm: Steam Input für das Spiel ausschalten und neu starten.

Kommandozeile: `cli\ds5-bt-haptics.exe` im Installationsordner (`bridge`, `devtest`, `loopback`,
`test`, `wav <datei>`, `list`, `selftest`), Details in [README.md](README.md).

Noch nicht enthalten: Lautsprecher-/Kopfhörer-Ton und Controller-Mikrofon.

Technische Details, Erkenntnisse und Lizenz: siehe [README.md](README.md).
