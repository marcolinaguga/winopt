@echo off
rem Compila o projeto sem Visual Studio, usando o compilador C# que ja vem com o .NET Framework 4.8.
setlocal
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist bin mkdir bin

"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ ^
  /win32manifest:app.manifest /win32icon:app.ico /out:bin\Winopt.exe ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.ServiceProcess.dll ^
  Program.cs Form1.cs Contexto.cs Processos.cs Arquivos.cs Etapas.cs

if errorlevel 1 (
  echo.
  echo Falha na compilacao.
  exit /b 1
)

echo.
echo Pronto: bin\Winopt.exe
endlocal
