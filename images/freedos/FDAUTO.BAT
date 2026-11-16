@echo off
rem Os-x86 freedos image (milestone 0, work item 4; trial E14, decision 214 A): replaces the
rem FDAUTO.BAT of the FreeDOS 1.4 LiteUSB image, which would start the installer. A text console
rem on the VGA screen, read by the terminal of Kazo OS at 33 ms (214 A). FDAPM APMDOS halts the
rem CPU while DOS waits for a key (E14: 0.7 % of a core instead of 100 %).
SET DOSDIR=\FREEDOS
SET PATH=\FREEDOS\BIN
SET DIRCMD=/OGN /Y
SET TEMP=\TEMP
SET TMP=\TEMP
SET OS_NAME=FreeDOS
SET OS_VERSION=1.4
SET LANG=EN
if exist \FREEDOS\BIN\FDAPM.COM FDAPM APMDOS
prompt $p$g
echo.
echo Os-x86 FreeDOS 1.4 guest (image freedos 0.2.0)
echo OSX86-READY
rem Work item 11: x86 run gives one DOS program on a second disk (D:) with OSX86RUN.BAT, which
rem runs it and tells its start and its ERRORLEVEL on COM1 (no agent in this image, 212 A).
if exist D:\OSX86RUN.BAT call D:\OSX86RUN.BAT
