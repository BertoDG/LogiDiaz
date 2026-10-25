@echo off
rem Arranca el servidor del raton (ventana minimizada) y abre la pagina de configuracion
start "" /min powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -WindowStyle Minimized -File "%~dp0raton.ps1"
