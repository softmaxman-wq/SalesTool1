@echo off
rem Rebuild with the bundled Roslyn compiler (no Visual Studio needed)
cd /d "%~dp0"
if not exist build mkdir build
if not exist build\fonts mkdir build\fonts
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
tools\roslyn\csc.exe -nologo -noconfig -langversion:7.3 -target:winexe -platform:x64 -optimize -out:build\SalesTool.exe -r:"%FW%\mscorlib.dll" -r:"%FW%\System.dll" -r:"%FW%\System.Core.dll" -r:"%FW%\System.Drawing.dll" -r:"%FW%\System.Windows.Forms.dll" -r:"%FW%\System.Web.Extensions.dll" -r:"%FW%\System.Security.dll" -r:"%FW%\System.Xml.dll" -r:"%FW%\System.Xml.Linq.dll" -r:"%FW%\System.IO.Compression.dll" -r:lib\BCrypt.Net-Next.dll -r:lib\Microsoft.Web.WebView2.Core.dll -r:lib\Microsoft.Web.WebView2.WinForms.dll -resource:src\App\mobile.html,mobile.html -recurse:src\*.cs
if errorlevel 1 exit /b 1
copy /y lib\*.dll build\ >nul
copy /y fonts\* build\fonts\ >nul
