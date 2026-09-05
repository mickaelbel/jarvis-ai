@echo off
rem ============================================================================
rem  Jarvis AI - Publie le dernier installateur sur GitHub Releases
rem ============================================================================
title Jarvis AI - Publication GitHub
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\publier-release.ps1" %*
echo.
if errorlevel 1 (
  echo.
  echo ECHEC : verifie le message d'erreur ci-dessus.
  echo   - Sois connecte a GitHub (gh auth login), ou
  echo   - deposes un Personal Access Token dans scripts\.github-token
  pause
  exit /b 1
)
pause
