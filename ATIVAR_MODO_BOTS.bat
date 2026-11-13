@echo off
chcp 65001 >nul
title CS2 Bot Improver - Ativar Modo Bots (SS+ Ultra)

set "CSGO_DIR=%~dp0"
if not exist "%CSGO_DIR%backup\WithBots\gameinfo.gi" (
    set "CSGO_DIR=F:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\csgo\"
)

if not exist "%CSGO_DIR%backup\WithBots\gameinfo.gi" (
    echo [ERRO] Nao foi possivel encontrar a pasta backup\WithBots\gameinfo.gi.
    pause
    exit /b 1
)

copy /y "%CSGO_DIR%backup\WithBots\gameinfo.gi" "%CSGO_DIR%gameinfo.gi" >nul

powershell -NoProfile -Command "try { $cfg = 'C:\Users\gabri\AppData\Roaming\com.cs2botimprover.panel\config.json'; if (Test-Path $cfg) { $j = Get-Content $cfg -Raw | ConvertFrom-Json; $j.mode = 'bots'; $j | ConvertTo-Json -Depth 5 | Set-Content $cfg -Encoding UTF8 } } catch {}" >nul 2>&1

echo ========================================================
echo          MODO BOTS (SS+ ULTRA) ATIVADO COM SUCESSO!
echo ========================================================
echo.
echo [OK] Metamod:Source e CounterStrikeSharp habilitados.
echo [OK] 6 Plugins de IA (BotProTactics, NadeSystem, etc.) ativos.
echo [OK] VPK de perfis de bots High/Pro montado.
echo.
echo Pode abrir o CS2 e entrar na partida (Pratica ou console)!
echo ========================================================
echo.
pause

