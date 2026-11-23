@echo off
setlocal
cd /d "%~dp0..\.."

if not exist build\SalesTool.exe (
    echo [INFO] build\SalesTool.exe not found. Running build.bat...
    call build.bat
    if %ERRORLEVEL% NEQ 0 (
        echo [ERROR] build.bat failed with code %ERRORLEVEL%
        exit /b %ERRORLEVEL%
    )
)

set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
echo [INFO] Compiling VerifyKeywords.cs...
tools\roslyn\csc.exe -nologo -noconfig -langversion:7.3 -target:exe -platform:x64 -optimize -out:build\VerifyKeywords.exe -r:"%FW%\mscorlib.dll" -r:"%FW%\System.dll" -r:"%FW%\System.Core.dll" -r:"%FW%\System.Web.Extensions.dll" -r:lib\Microsoft.Web.WebView2.Core.dll -r:lib\Microsoft.Web.WebView2.WinForms.dll -r:build\SalesTool.exe tools\verify\VerifyKeywords.cs

if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Compilation of VerifyKeywords failed with error code %ERRORLEVEL%
    exit /b %ERRORLEVEL%
)

echo [INFO] Executing VerifyKeywords.exe...
cd build
VerifyKeywords.exe
set TEST_EXIT=%ERRORLEVEL%
cd ..

if %TEST_EXIT% NEQ 0 (
    echo [ERROR] Verification tests failed with exit code %TEST_EXIT%
    exit /b %TEST_EXIT%
)

echo.
echo [SUCCESS] All verification tests passed successfully with exit code 0.
exit /b 0