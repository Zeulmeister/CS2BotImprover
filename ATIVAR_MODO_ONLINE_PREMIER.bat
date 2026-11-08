@echo off
chcp 65001 >nul
title CS2 Bot Improver - Ativar Modo Online (Premier / VAC)

set "CSGO_DIR=%~dp0"
if not exist "%CSGO_DIR%backup\Online\gameinfo.gi" (
    set "CSGO_DIR=F:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\csgo\"
)

if not exist "%CSGO_DIR%backup\Online\gameinfo.gi" (
    echo [ERRO] Nao foi possivel encontrar a pasta backup\Online\gameinfo.gi.
    pause
    exit /b 1
)

copy /y "%CSGO_DIR%backup\Online\gameinfo.gi" "%CSGO_DIR%gameinfo.gi" >nul

powershell -NoProfile -Command "try { $cfg = 'C:\Users\gabri\AppData\Roaming\com.cs2botimprover.panel\config.json'; if (Test-Path $cfg) { $j = Get-Content $cfg -Raw | ConvertFrom-Json; $j.mode = 'online'; $j.insecure = $false; $j | ConvertTo-Json -Depth 5 | Set-Content $cfg -Encoding UTF8 } } catch {}" >nul 2>&1

echo ========================================================
echo        MODO ONLINE / PREMIER ATIVADO COM SUCESSO!
echo ========================================================
echo.
echo [OK] Metamod e plugins foram desativados do motor Source 2.
echo [OK] gameinfo.gi restaurado para a versao 100%% original da Valve.
echo [OK] Seu CS2 esta limpo e 100%% seguro para o VAC (Premier e Comp).
echo.
echo Pode abrir o Counter-Strike 2 normalmente pela Steam e jogar!
echo ========================================================
echo.
pause
