@echo off
rem ============================================================================
rem  DebugProbe launcher.  ASCII ONLY on purpose.
rem
rem  What it solves
rem  --------------
rem  The repo directory C:\...\Desktop\project\Keeper carries two EXPLICIT
rem  (non-inherited) ACEs that grant write rights to Everyone and to a
rem  capability SID (S-1-4-963651649-261417728).  Windows treats a directory
rem  writable by low-privileged principals as an untrusted location, and an
rem  exe launched from there is downgraded to LOW integrity.
rem
rem  Measurement: the SAME binary answers RID 0x2000 (medium) when it sits
rem  under %TEMP%, and RID 0x1000 (low) when it sits under bin\.  Nothing to
rem  do with the program itself - the ACL on the path decides.
rem
rem  Why that matters: a low-integrity process cannot read explorer.exe's icon
rem  layer (LVM_GETITEMCOUNT error 5, UI Automation 0 items, OpenProcess error
rem  5).  The tool used to print those "cannot read" lines anyway, and they
rem  look exactly like "this approach does not work" - they were false
rem  readings.  The tool now refuses the run outright.
rem
rem  The Chinese version of all of the above, and the full reading, live in:
rem    - Relaunch.cs  (this folder)
rem    - docs/research/desktop-layer-and-window-measurements.md
rem
rem  Usage:  run.cmd <subcommand> [args...]     e.g.  run.cmd drag --rounds 3
rem
rem  This run keeps the CALLER's integrity level.  Launch it from PowerShell
rem  (medium) and the tool runs medium.
rem
rem  Why this file is ASCII: cmd.exe reads a .cmd using the OEM code page
rem  (936 / GBK here).  UTF-8 Chinese bytes get mis-decoded and some pairs
rem  decode to a byte that looks like a line-continuation backslash, which
rem  splits "rem ..." into commands.  Measured: the script fails with
rem  "'...' is not recognized as an internal or external command".
rem  Adding "chcp 65001" does NOT fix it - cmd.exe then rewinds the file
rem  offset by CHARACTER count instead of bytes and lands mid-line.
rem  Both variants were run and failed.  ASCII only is the reliable form.
rem ============================================================================

set "HERE=%~dp0"
set "BIN=%HERE%bin\Debug\net8.0-windows"
set "DEST=%TEMP%\keeper-debugprobe"

if not exist "%BIN%\DebugProbe.exe" (
  echo NOT BUILT.  Run "dotnet build" in %HERE% first.
  exit /b 2
)

if not exist "%DEST%" mkdir "%DEST%"

rem The copy must happen on the MEDIUM side.  A low-integrity process cannot
rem write into a medium-integrity directory.
rem The exe name must not change: the .NET apphost bakes its own dll name in.
copy /y "%BIN%\DebugProbe.exe" "%DEST%\" >nul || goto :failed
copy /y "%BIN%\DebugProbe.dll" "%DEST%\" >nul || goto :failed
copy /y "%BIN%\DebugProbe.runtimeconfig.json" "%DEST%\" >nul || goto :failed

rem The tool finds the repo by walking up from the CURRENT DIRECTORY
rem (FrameShot.DefaultOutDir).  The exe now lives under %TEMP%, so its own
rem path no longer points at the repo - the working directory has to.
rem %HERE% is  <repo>\src\tools\DebugProbe\  ->  three levels up is the repo.
set "REPO=%HERE%..\..\.."
if not exist "%REPO%\.scratch\debug-tool\SPEC.md" (
  echo REPO NOT FOUND at %REPO%.  Run this script from the repo checkout.
  exit /b 2
)

pushd "%REPO%"
"%DEST%\DebugProbe.exe" %*
set "RC=%ERRORLEVEL%"
popd
exit /b %RC%

:failed
echo COPY FAILED.  Could not place the tool in %DEST%.
echo   The caller is probably running at LOW integrity already.
echo   Run this script from PowerShell instead.
exit /b 2