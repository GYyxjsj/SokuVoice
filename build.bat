@echo off
cd /d "%~dp0"
%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /platform:x86 /target:winexe /win32icon:app.ico /out:SokuVoice.exe SokuVoice.cs
if errorlevel 1 (
  echo 编译失败
  pause
) else (
  echo 编译成功: SokuVoice.exe
)