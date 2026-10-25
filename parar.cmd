@echo off
rem Para el servidor del raton (alternativa al boton "Cerrar servidor" de la pagina)
type nul > "%~dp0parar.flag"
echo Parando el servidor del raton...
timeout /t 3 >nul
