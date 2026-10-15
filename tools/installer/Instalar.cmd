@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -SourceDirectory "%~dp0." -PrepareComputer
if errorlevel 1 (
  echo.
  echo A instalacao nao foi concluida. Confira a mensagem acima.
  pause
  exit /b 1
)
echo.
echo Abra o TeclaScore pelo atalho na area de trabalho ou no menu Iniciar.
pause
