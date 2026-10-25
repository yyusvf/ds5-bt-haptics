# DS5 BT Haptics (Deutsch)

DualSense-HD-Haptik über Bluetooth unter Windows, ohne Dongle. Eine reine Software-Version von
[DS5Dongle](https://github.com/awalol/DS5Dongle): Das Programm stellt Windows einen virtuellen
USB-DualSense bereit (über usbip-win2) und leitet alles an den Bluetooth-Controller weiter,
auch die Haptik, die Spiele nur per Kabel senden.

## Einrichtung

1. [usbip-win2](https://github.com/vadimgrn/usbip-win2/releases) installieren (vorher Wiederherstellungspunkt).
2. Im **HidHide Configuration Client**:
   - *Applications*: `ds5-bt-haptics.exe` hinzufügen
   - *Devices*: den **Bluetooth**-DualSense verstecken, den virtuellen USB-DualSense **nicht**
3. DSX, DS4Windows u. ä. nicht gleichzeitig laufen lassen.

## Benutzung

Doppelklick auf `DS5 Bridge starten.cmd` (keine Adminrechte nötig). **Das Spiel nach der Bridge
starten.** Bleibt ein Spiel stumm: Steam Input für das Spiel ausschalten und neu starten.

| Befehl | Funktion |
|---|---|
| `bridge` | Software-Dongle mit Spiel-Haptik, adaptiven Triggern, Lightbar |
| `devtest` | Testmuster über das Audiogerät des virtuellen Controllers (während `bridge` läuft) |
| `loopback` | Windows-Sound live als Haptik, ohne Treiber |
| `test` | Testmuster direkt per Bluetooth |
| `wav <datei>` | Audiodatei als Haptik |
| `list` / `selftest` | Controller anzeigen / interne Prüfungen |

Noch nicht enthalten: Lautsprecher-/Kopfhörer-Ton und Controller-Mikrofon.

Technische Details, Erkenntnisse und Lizenz: siehe [README.md](README.md).
