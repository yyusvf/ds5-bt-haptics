@echo off
title DS5 Bridge (Strg+C zum Beenden)
cd /d "%~dp0ds5-bt-haptics"
echo DualSense per Bluetooth einschalten, dann laeuft hier der Software-Dongle.
echo Fenster schliessen oder Strg+C beendet ihn.
echo.
ds5-bt-haptics.exe bridge
pause
