@echo off
rem Build DerethWatch.dll (v2, all modules) against local Decal + Virindi View Service
cd /d "%~dp0src"
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set DECAL=C:\Program Files (x86)\Decal 3.0
set VVS=C:\Games\VirindiPlugins\VirindiViewService
if not exist "..\bin" mkdir "..\bin"
"%CSC%" /nologo /target:library /platform:x86 /out:..\bin\DerethWatch.dll /res:view.xml,DerethWatch.ViewXML.xml /res:rares.tsv,DerethWatch.rares.tsv ^
  /r:"%DECAL%\Decal.Adapter.dll" /r:"%DECAL%\.NET 4.0 PIA\Decal.Interop.Inject.dll" ^
  /r:"%VVS%\VirindiViewService.dll" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll *.cs
if errorlevel 1 ( echo BUILD FAILED ) else ( echo BUILD OK: bin\DerethWatch.dll )
