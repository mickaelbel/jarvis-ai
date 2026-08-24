@echo off
REM Installe puis lance le serveur XTTS-v2 (voix clonee) sur GPU, port 17003.
setlocal
where py >nul 2>nul && (set PY=py) || (set PY=python)

echo ==^> Verification torch+CUDA...
%PY% -c "import torch; assert torch.cuda.is_available()" >nul 2>nul
if errorlevel 1 (
    echo    Installation de PyTorch CUDA...
    %PY% -m pip install --upgrade pip
    %PY% -m pip install torch --index-url https://download.pytorch.org/whl/cu121 || goto :echec
)

echo ==^> Installation TTS + serveur...
%PY% -c "import TTS" >nul 2>nul || %PY% -m pip install TTS fastapi uvicorn python-multipart || goto :echec

echo ==^> Demarrage du serveur XTTS sur http://127.0.0.1:17003 ...
echo    Pour la voix clonnee : place un extrait de 6-30 s dans scripts\ref.wav
%PY% "%~dp0tts_xtts_server.py"
goto :fin

:echec
echo ERREUR d'installation - verifie Python et ta connexion.
exit /b 1

:fin
endlocal
