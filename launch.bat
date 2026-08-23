@echo off
title Jarvis AI
color 0A

echo ========================================
echo        JARVIS AI - Starting...
echo ========================================
echo.

set "PATH=C:\Program Files\dotnet;%PATH%"

cd /d "%~dp0"

echo Open http://localhost:5000 in your browser
echo Press Ctrl+C to stop the server
echo.

start "" "http://localhost:5000"

dotnet run --project src\Web\JarvisAI.Web\JarvisAI.Web.csproj -c Release --urls "http://localhost:5000"
