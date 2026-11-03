@echo off
rem Build UndoUnintendedKeymode.exe with the C# compiler bundled with Windows (.NET Framework 4.x)
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found. Please enable .NET Framework 4.x.
  pause
  exit /b 1
)
cd /d "%~dp0"
"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /out:UndoUnintendedKeymode.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll Program.cs
if errorlevel 1 (
  echo Build FAILED.
  pause
  exit /b 1
)
echo Build OK: UndoUnintendedKeymode.exe
pause
