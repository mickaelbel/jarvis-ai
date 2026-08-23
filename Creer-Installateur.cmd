@echo off
rem ============================================================================
rem  Jarvis AI - Cree l'installateur commercial (double-clic)
rem  Sortie : dist\installer\JarvisAI-Setup-<version>.exe
rem ============================================================================
title Jarvis AI - Creation de l'installateur
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-installer.ps1" %*
echo.
pause
