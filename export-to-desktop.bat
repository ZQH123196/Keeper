@echo off
chcp 65001 >nul
title Keeper 导出
setlocal
cd /d "%~dp0"

rem 这个脚本把 docs\packaging.md 那七步包成一次双击。
rem 产物是单个 exe，落到桌面上；桌面上有同名就覆盖。
rem 判据一：日志里得有「Running dotnet publish」—— 证 dotnet publish 真跑过。
rem 判据二：savepack 得收「mono/publish/x86_64/Keeper.dll」—— 证 .NET 进了包。
rem 哪一条缺都不往桌面拷，那种产物是一份没有 C# 的 Keeper，而退出码照样是 0。
rem
rem 屏幕上每 2 秒半重画一次：一条计时器、引擎当前那一步、一根进度条。
rem 计时器的 0 点是 STAMP 的写入时间，第一发探测时落的，数的是现实时间，
rem 不是数 ping 了几下（那会少算 PowerShell 自己花掉的那几秒）。
rem 探测（:probe）一发 PowerShell 干四件事：去掉 ANSI 码落一份干净日志、
rem 报 savepack 打完没有、报引擎进程还在不在、报当前那一步和进度条。
rem 打完引擎常常立刻就退，所以先问「打完没有」，再问「进程还在吗」——
rem 次序反了会把成功那一发报成失败（实测结果在 docs\research\export-single-file.md）。
rem 判据那两条搜索词一个字没动，仍然在原始日志上搜。

set "GODOT=%USERPROFILE%\Desktop\software\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe"
set "OUT=%CD%\.scratch\build"
set "ARTIFACT=%OUT%\Keeper.exe"
rem 三份日志落仓库根的 .tmp\，不落系统临时目录 —— 那一份会被自动清理掉，
rem 判据就没得查了。落点在 CODING_STANDARDS.md「临时产出的落点」那一节。
rem 脚本开头已经 cd 到本文件所在的仓库根，所以 %CD% 就是那个根。
set "LOG=%CD%\.tmp\keeper-export.log"
set "CLEAN=%CD%\.tmp\keeper-export-clean.log"
set "STAMP=%CD%\.tmp\keeper-export-start.txt"
set "LIMIT=900"
set "DONE=RUN"
set "ALIVE=ALIVE"
rem 探测失灵时 ELAPSED 得是个数，不然下面那个 GEQ 比较整个报错。
set "ELAPSED=0"
set "PCT=0"
set "STEP=-"
set "STEPCN=引擎还没动"
set "BAR="
set "DESC="
rem 百分号单独放一个变量：行尾直接写 %% 会被当成变量名的开头。
set "PC=%%"
set "PHASE=none"
set "SIZE=还没有"
set "S1=还没查"
set "S2=还没查"
set "S3=还没查"
set "S4=还没到"
set "S5=还没到"

rem 1 带 --headless 的只有 _console 那一支。
if not exist "%GODOT%" goto no-engine
for %%F in ("%GODOT%") do set "CONSOLE=%%~nF"
set "S1=在"

rem 2 缺预设时导出直接报错。缺解决方案时导出会静默不编译 C#。
if not exist "export_presets.cfg" goto no-preset
if not exist "Keeper.sln" goto no-sln
set "S2=两份都在"

rem 3 正在跑的那一发占着文件，盖的时候会失败。先问一句，别让人白等。
rem 这个名字短（Keeper.exe），tasklist 不会截断，所以这里用 tasklist 没事。
tasklist /fi "imagename eq Keeper.exe" /nh 2>nul | find /i "Keeper.exe" >nul
if not errorlevel 1 goto running
set "S3=没有"

rem 探测那一发 PowerShell 的代码，在这里赋值：CONSOLE 上面才定下来，写在顶上会取到空的。
rem 批处理从上往下跑，所以它必须在这三样查完之后、第一次调用 :probe 之前。
set "P_A=$e=[char]27; $clean=@(Get-Content -LiteralPath '%LOG%' -Encoding UTF8 -ErrorAction SilentlyContinue | ForEach-Object { $_ -replace ($e+'\[[0-9;]*m'),'' }); Set-Content -LiteralPath '%CLEAN%' -Value $clean -Encoding UTF8 -ErrorAction SilentlyContinue; $t=[string]::Join([char]10,$clean);"
set "P_B=if (-not (Test-Path -LiteralPath '%STAMP%')) { New-Item -ItemType File -Path '%STAMP%' -Force | Out-Null }; $el=[int]((Get-Date) - (Get-Item -LiteralPath '%STAMP%').LastWriteTime).TotalSeconds; if ($el -lt 0) { $el=0 }; $dn='RUN'; if ($t -match '\[ DONE \] savepack') { $dn='DONE' }; $al='GONE'; if (Get-Process -Name '%CONSOLE%' -ErrorAction SilentlyContinue) { $al='ALIVE' };"
set "P_C=$m=[regex]::Matches($t,'(?m)^\[\s*(\d+)\x25\s*\]\s*([A-Za-z0-9_]+)\s*\|(.*)$'); $pct=0; $st='-'; $ds=''; if ($m.Count -gt 0) { $g=$m[$m.Count-1].Groups; $pct=[int]$g[1].Value; $st=$g[2].Value; $ds=$g[3].Value.Trim() }; $cn=$st; $nm=@{first_scan_filesystem='扫文件结构';update_scripts_classes='注册全局类';_update_scan_actions='扫动作';dotnet_publish_project='编译 C#';savepack='打包'}; if ($nm.ContainsKey($st)) { $cn=$nm[$st] }; $f=[int]($pct*24/100); if ($f -gt 24) { $f=24 }; $bar='['+('#'*$f)+('-'*(24-$f))+']'; $ds=$ds -replace '[&|<>]',''; Write-Output ([string]::Join('|',@($dn,$al,$el,$pct,$st,$cn,$bar,$ds)))"
call :screen

rem 4 先清干净产物目录。目录清不掉，就是有文件被占着。
rem .tmp\ 是那三份日志的落点，仓库里有两个 .gitkeep 留着它；缺了先补上，
rem 不然 > "%LOG%" 那一条重定向会自己失败，报出来的话却跟产物没关系。
if not exist "%CD%\.tmp" mkdir "%CD%\.tmp"
if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%OUT%" goto locked
mkdir "%OUT%"
if not exist "%OUT%" goto no-out
if exist "%CLEAN%" del /q "%CLEAN%"
if exist "%STAMP%" del /q "%STAMP%"
set "S4=进行中"
set "PHASE=export"

rem 起它，不等它退：有一类那一发 savepack 打完它挂着不退（0% CPU），
rem 等下去是永远等。改成两样当它做完：日志里 savepack 打完，
rem 产物的字节数连着三次一样。
start "" /b "%GODOT%" --headless --path . --export-release "win-x64-single" "%ARTIFACT%" > "%LOG%" 2>&1

:wait-export
call :probe
if not defined DONE goto probe-failed
if "%DONE%"=="DONE" goto settle
if "%ALIVE%"=="GONE" goto no-done
if %ELAPSED% GEQ %LIMIT% goto export-timeout
call :screen
ping -n 3 127.0.0.1 >nul
goto wait-export

rem savepack 打完之后还要等字节数稳住。每一轮大约 4 秒。
:settle
set "PHASE=settle"
set "S4=等字节数稳住"
set /a SZTICK=0

:wait-settle
call :probe
if not defined DONE goto probe-failed
for %%A in ("%ARTIFACT%") do set "SZ0=%%~zA"
ping -n 3 127.0.0.1 >nul
for %%A in ("%ARTIFACT%") do set "SZ1=%%~zA"
call :screen
if not "%SZ0%"=="%SZ1%" goto settle-again
ping -n 3 127.0.0.1 >nul
for %%A in ("%ARTIFACT%") do set "SZ2=%%~zA"
if not "%SZ1%"=="%SZ2%" goto settle-again
goto export-done

:settle-again
set /a SZTICK+=1
if %SZTICK% GEQ 75 goto export-timeout
goto wait-settle

rem 打完它还挂着的，就在这里停掉。只停 _console 那一支 ——
rem 编辑器和日常那个 exe 不叫这个名字。
:export-done
taskkill /f /im %CONSOLE%.exe >nul 2>&1
set "S4=完了"

rem 5 那两条判据在原始日志里都是连着的，直接搜。
set "PHASE=criteria"
findstr /c:"Running dotnet publish" "%LOG%" >nul
if errorlevel 1 goto no-publish
findstr /c:"mono/publish/x86_64/Keeper.dll" "%LOG%" >nul
if errorlevel 1 goto no-embed
if not exist "%ARTIFACT%" goto no-artifact
set "S5=两条都过了"
for %%A in ("%ARTIFACT%") do set "SIZE=%%~zA 字节"

rem 桌面问系统要，不写死 %USERPROFILE%\Desktop（那一份可能被搬到 OneDrive）。
set "DESKTOP="
for /f "delims=" %%D in ('powershell -NoProfile -Command "[Environment]::GetFolderPath('Desktop')"') do set "DESKTOP=%%D"
if not defined DESKTOP goto no-desktop

rem 产物不小，那一次拷贝要几秒，所以先说一声，别以为死了。
set "PHASE=copy"
set "S5=正在拷到桌面"
call :screen
copy /y "%ARTIFACT%" "%DESKTOP%\Keeper.exe" > nul
if errorlevel 1 goto no-copy

rem 结尾这一屏：东西在哪、多大、跑了多久，全部一次给全。
call :probe
cls
title Keeper 导出 - 好了
echo.
echo   好了。桌面上那一份是刚才这一版。
echo.
for %%A in ("%DESKTOP%\Keeper.exe") do echo   文件    %%~zA 字节  ／  %%~tA
echo   位置    %DESKTOP%\Keeper.exe
echo   耗时    第 %ELAPSED% 秒
echo   判据    dotnet publish 跑过，.NET 那一半进了包
echo   日志    %LOG%
echo.
echo   桌面上那一份可以直接双击，不用装 .NET。
echo.
pause
if exist "%STAMP%" del /q "%STAMP%"
exit /b 0

rem ---------------------------------------------------------------- 探测
rem 一发 PowerShell 吐一行，七个定长字段加一句说明，中间用 | 隔开：
rem   打完没有|引擎还在不在|过了几秒|百分比|步名|那一步的中文|进度条|说明
rem 说明那一段可能自己带 |，所以放在最后，批处理那边用 tokens=n* 取剩下的。
rem 步名到中文那个对照表只为好看：表里没有的名字原样打出来，不猜。
rem 代码本身在顶上 —— 批处理从上往下跑，写在这儿第一次调用时它还是空的。
rem 代码里一个百分号都不能有：子 cmd 会把它当成变量引用的开头，从那儿往后
rem 一整段被吃掉（实测结果在 docs\research\export-script-screen.md）。
rem 那个百分号用正则的 \x25 写。

:probe
rem 先把 DONE 清掉：探测那一发 PowerShell 起不来时 for 一行都不吐，计时器就
rem 不会往前跳。失灵在两个调用点上判，不在这里 goto —— 从 call 进来的
rem goto 跳出去之后，:fail 那个 exit /b 只退得掉这一层，外面的循环会接着跑。
set "DONE="
for /f "tokens=1-7,8* delims=|" %%A in ('powershell -NoProfile -Command "%P_A%%P_B%%P_C%"') do (
  set "DONE=%%A"
  set "ALIVE=%%B"
  set "ELAPSED=%%C"
  set "PCT=%%D"
  set "STEP=%%E"
  set "STEPCN=%%F"
  set "BAR=%%G"
  set "DESC=%%H"
)
goto :eof

rem ---------------------------------------------------------------- 屏幕
rem 状态那一列靠空格对齐：中文一个字占两格，宽度是手算的，不是变量拼的。
:screen
cls
title Keeper 导出 - 第 %ELAPSED% 秒
echo.
echo   Keeper：把最新版本导出到桌面
echo.
echo   1  引擎                 %S1%
echo   2  预设和解决方案        %S2%
echo   3  正在跑的 Keeper       %S3%
echo   4  导出                  %S4%
echo   5  判产物                %S5%
echo.
if "%PHASE%"=="export"   goto scr-export
if "%PHASE%"=="settle"   goto scr-settle
if "%PHASE%"=="copy"     goto scr-copy
goto scr-note

:scr-export
echo   引擎现在这一步：%STEPCN%  %PCT%%PC%
echo   %BAR%
if defined DESC echo   %DESC%
echo.
set /a LIMMIN=%LIMIT%/60
echo   已经过 %ELAPSED% 秒，最多等 %LIMMIN% 分钟。
echo   别关这个窗口。要中止就把它关掉。
echo   日志 %LOG%
goto scr-note

:scr-settle
echo   打包完了。等产物的字节数稳住。
echo   引擎有的时候打完了不退，所以还得再等几秒。
echo.
echo   已经过 %ELAPSED% 秒。
goto scr-note

:scr-copy
echo   产物 %SIZE%
echo   正在拷到桌面。文件不小，那一下要几秒。
goto scr-note

:scr-note
echo   平常二十秒上下。引擎要重扫资源时会慢到几分钟。
goto :eof

rem ---------------------------------------------------------------- 失败
:no-engine
echo   1 失败：没找到引擎。
echo      找的是这一支：%GODOT%
goto fail

:no-preset
echo   2 失败：仓库里没有 export_presets.cfg。
echo      那份文件的内容在 docs\packaging.md 第 5 步。
goto fail

:no-sln
echo   2 失败：仓库里没有 Keeper.sln。
echo      缺它导出会静默不编译 C#。做法在 docs\packaging.md 第 2 步。
goto fail

:running
echo   3 失败：有一发 Keeper 正在跑。
echo      先从托盘退出它，再双击本文件。
goto fail

:locked
echo   4 失败：.scratch\build 里有个文件被占着。
echo      关掉从那儿跑起来的那一发，再双击本文件。
goto fail

:no-out
echo   4 失败：建不出 .scratch\build 目录。
goto fail

:no-done
echo   4 失败：引擎自己退了，日志里没有 savepack 打完那一行。
goto tail

:export-timeout
echo   4 失败：等了很久它也没打完。
goto tail

rem 探测失灵和引擎自己退了不是一回事，所以分开报：那一条连日志末尾都读不出来。
:probe-failed
echo   4 失败：探测失灵 —— 那一发 PowerShell 没起来。
echo      屏幕上的秒数不再走，所以立刻就停了，不再空等。
echo      查 powershell 还能不能跑：powershell -NoProfile -Command 1
goto fail

:no-publish
echo   5 失败：这一发没编译 C#，产物是坏的。没往桌面拷。
goto tail

:no-embed
echo   5 失败：.NET 那一半没进包，产物起来就崩。没往桌面拷。
echo      查 export_presets.cfg 里 dotnet/embed_build_outputs 是不是 true。
goto tail

:no-artifact
echo   5 失败：产物不在 .scratch\build\Keeper.exe。没往桌面拷。
goto tail

rem 日志末尾读干净那份（探测每轮已经把 ANSI 码去掉了）。
:tail
echo      日志末尾 25 行：
powershell -NoProfile -Command "Get-Content -LiteralPath '%CLEAN%' -Encoding UTF8 -Tail 25 -ErrorAction SilentlyContinue"
goto fail

:no-desktop
echo   失败：问不到桌面在哪。没往桌面拷。
goto fail

:no-copy
echo   失败：盖不上桌面上那一份。
echo      多半是那一份正在跑：先从托盘退出它，再双击本文件。
goto fail

:fail
echo.
rem 用 STAMP 判「导出到底起过没有」：它第一发探测时才落，
rem 前置检查就挂掉的时候它不在，那种情形不该报「用了 0 秒」。
if exist "%STAMP%" echo   跑到这里用了 %ELAPSED% 秒。日志在 %LOG%
if exist "%STAMP%" del /q "%STAMP%"
pause
exit /b 1