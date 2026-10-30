@echo off
rem Builds HdrSwitch.exe with the C# compiler included in Windows (.NET Framework 4.8) - nothing to install.
rem Usage: build.cmd [version]   e.g. build.cmd 1.2.0 (release builds); without a version: 0.0.0, a dev build.
setlocal
set VERSION=%~1
if "%VERSION%"=="" set VERSION=0.0.0
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe

> "%~dp0Version.g.cs" echo [assembly: System.Reflection.AssemblyVersion("%VERSION%.0")]

"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:"%~dp0HdrSwitch.exe" /win32manifest:"%~dp0app.manifest" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  "%~dp0HdrSwitch.cs" "%~dp0Flyout.cs" "%~dp0Resolution.cs" "%~dp0Nvidia.cs" "%~dp0Version.g.cs"
