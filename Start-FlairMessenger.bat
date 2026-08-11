@echo off
title Flair Messenger
echo Starting app, one moment...
set "APPDIR=%~dp0"
start "" wscript.exe "%APPDIR%Start-FlairMessenger.vbs"
timeout /t 2 /nobreak >nul
exit
