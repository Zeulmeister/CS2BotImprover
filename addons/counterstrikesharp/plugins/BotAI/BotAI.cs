using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Common;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Memory;
using Microsoft.Extensions.Logging;

namespace BotAI
{
    public record PatchInfo(string Name, nint Address, List<byte> OriginalBytes);

    public static class BotOffsets
    {
        public const int m_gameState = 20768;
        public const int m_isRoundOver = 8;
        public const int m_bombState = 12;
        public const int m_plantedBombsite = 104;
    }

    [MinimumApiVersion(304)]
    public class BotAI : BasePlugin
    {
        private readonly List<PatchInfo> _appliedPatches = new List<PatchInfo>();
        private readonly bool _isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        public override string ModuleName => "Patches - Bot AI";
        public override string ModuleVersion => "1.9.0-Antigravity";
        public override string ModuleAuthor => "K4ryuu & Austin (updated by ed0ard, Misaka17032, XBribo, AmagiReina & Antigravity)";
        public override string ModuleDescription => "Improve and fix bots' behavior comprehensively (Memory Patches)";

        public override void Load(bool hotReload)
        {
            Logger.LogInformation("Bot AI Patches loading...");
            IReadOnlyDictionary<string, (string signature, string patch, string expectedOriginal, int patchOffset)> defs = 
                _isLinux ? LinuxPatchDefinitions.All : WindowsPatchDefinitions.All;

            foreach (string key in defs.Keys)
            {
                if (ApplyPatch(key, _isLinux))
                {
                    Logger.LogInformation($"[BotAI] Patch #{_appliedPatches.Count} '{key}': applied successfully.");
                }
                else
                {
                    Logger.LogError($"[BotAI] Patch '{key}': FAILED to apply.");
                }
            }

            RegisterEventHandler<EventPlayerSpawn>((@event, info) =>
            {
                var player = @event.Userid;
                if (player != null && player.IsValid && player.IsBot)
                {
                    var pawn = player.PlayerPawn.Value;
                    if (pawn != null && pawn.IsValid && (int)player.Team > 1 && pawn.BotAllowActive)
                    {
                        var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
                        if (gameRules != null && !gameRules.BombPlanted)
                        {
                            UpdateBotBombState(pawn, player.PlayerName);
                        }
                    }
                }
                return HookResult.Continue;
            });

            Logger.LogInformation($"[BotAI] Applied {_appliedPatches.Count}/{defs.Count} patches.");
        }

        public override void Unload(bool hotReload)
        {
            Logger.LogInformation("Bot AI Patches unloading...");
            foreach (PatchInfo appliedPatch in _appliedPatches)
            {
                RestorePatch(appliedPatch);
            }
            _appliedPatches.Clear();
            Logger.LogInformation("All Bot AI patches restored.");
        }

        private bool ApplyPatch(string name, bool linux = false)
        {
            try
            {
                var defs = linux ? LinuxPatchDefinitions.All : WindowsPatchDefinitions.All;
                if (!defs.TryGetValue(name, out var patchDef))
                {
                    return false;
                }

                string modulePath = GameUtils.GetModulePath("server");
                nint sigAddress = NativeAPI.FindSignature(modulePath, patchDef.signature);
                if (sigAddress == 0)
                {
                    Logger.LogError($"'{name}': signature not found in module '{modulePath}'.");
                    return false;
                }

                nint patchTarget = sigAddress + patchDef.patchOffset;
                List<byte> patchBytes;
                if (!linux && name == "TBot_BombsiteSearch_UseKnownPlantedSite")
                {
                    const string getKnownPlantedSiteSig = "83 79 0C 02 B8 FF FF FF FF 75 03 8B 41 68 C3";
                    nint targetFunc = NativeAPI.FindSignature(modulePath, getKnownPlantedSiteSig);
                    if (targetFunc == 0)
                    {
                        Logger.LogError($"'{name}': GetKnownPlantedSite signature not found; skipping safely to prevent invalid CALL.");
                        return false;
                    }
                    long rel64 = (long)targetFunc - ((long)patchTarget + 5L);
                    if (Math.Abs(rel64) > 0x04000000L)
                    {
                        Logger.LogError($"'{name}': CALL displacement 0x{rel64:X} out of safe module range; skipping.");
                        return false;
                    }
                    int rel32 = (int)rel64;
                    byte[] relBytes = BitConverter.GetBytes(rel32);
                    patchBytes = new List<byte>(5) { 0xE8, relBytes[0], relBytes[1], relBytes[2], relBytes[3] };
                }
                else
                {
                    patchBytes = ParseHex(patchDef.patch);
                }

                if (patchBytes.Count == 0 || !IsValid(patchTarget))
                {
                    return false;
                }

                List<byte> actualBytes = new List<byte>();
                for (int i = 0; i < patchBytes.Count; i++)
                {
                    actualBytes.Add(Marshal.ReadByte(patchTarget, i));
                }

                if (!ValidateOrig(name, actualBytes, patchDef.expectedOriginal))
                {
                    Logger.LogError($"'{name}': byte mismatch at 0x{patchTarget:X}. Expected [{patchDef.expectedOriginal}] got [{string.Join(" ", actualBytes.Select(b => $"{b:X2}"))}].");
                    return false;
                }

                if (!MemoryPatch.SetMemAccess(patchTarget, patchBytes.Count))
                {
                    Logger.LogError($"'{name}': failed to change memory protection at 0x{patchTarget:X}.");
                    return false;
                }

                for (int i = 0; i < patchBytes.Count; i++)
                {
                    Marshal.WriteByte(patchTarget, i, patchBytes[i]);
                }

                _appliedPatches.Add(new PatchInfo(name, patchTarget, actualBytes));
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"'{name}': exception during patching: {ex.Message}");
                return false;
            }
        }

        private void RestorePatch(PatchInfo p)
        {
            try
            {
                if (IsValid(p.Address) && MemoryPatch.SetMemAccess(p.Address, p.OriginalBytes.Count))
                {
                    for (int i = 0; i < p.OriginalBytes.Count; i++)
                    {
                        Marshal.WriteByte(p.Address, i, p.OriginalBytes[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Restore '{p.Name}': {ex.Message}");
            }
        }

        private bool ValidateOrig(string name, List<byte> actual, string expectedHex)
        {
            try
            {
                string[] parts = expectedHex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (actual.Count != parts.Length)
                {
                    return false;
                }
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i] == "?") continue;
                    if (actual[i] != Convert.ToByte(parts[i], 16))
                    {
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsValid(nint addr)
        {
            if (addr == IntPtr.Zero) return false;
            try
            {
                Marshal.ReadByte(addr);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<byte> ParseHex(string hex)
        {
            return hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                      .Where(t => t != "?")
                      .Select(t => Convert.ToByte(t, 16))
                      .ToList();
        }

        private bool UpdateBotBombState(CCSPlayerPawn pawn, string playerName)
        {
            try
            {
                nint? botHandle = pawn?.Bot?.Handle;
                if (!botHandle.HasValue || botHandle.Value == IntPtr.Zero || !IsValid(botHandle.Value))
                {
                    return false;
                }

                nint gameStateAddr = botHandle.Value + BotOffsets.m_gameState;
                if (!IsValid(gameStateAddr)) return false;

                if (Marshal.ReadByte(gameStateAddr + BotOffsets.m_isRoundOver) != 0)
                {
                    return true;
                }

                nint bombStateAddr = gameStateAddr + BotOffsets.m_bombState;
                if (!IsValid(bombStateAddr)) return false;

                if (!MemoryPatch.SetMemAccess(bombStateAddr, 4))
                {
                    return false;
                }

                if (Marshal.ReadInt32(bombStateAddr) != 0)
                {
                    Marshal.WriteInt32(bombStateAddr, 0);
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"UpdateBotBombState({playerName}): {ex.Message}");
                return false;
            }
        }
    }

    internal static class LinuxPatchDefinitions
    {
        internal static IReadOnlyDictionary<string, (string signature, string patch, string expectedOriginal, int patchOffset)> All { get; } = 
            new Dictionary<string, (string, string, string, int)>
        {
            ["HasVisitedEnemySpawn"] = ("40 88 B7 6C 07 00 00 C3", "C6 87 6C 07 00 00 01", "40 88 B7 6C 07 00 00", 0),
            ["GameState_Reset"] = ("C6 47 08 00 48 89 07 48 C7 47 0C 00 00 00 00 C7 47 14 00 00 00 00 C3", "0F 1F 84 00 00 00 00 00", "48 C7 47 0C 00 00 00 00", 7),
            ["Idle_IsSafeAlwaysFalse"] = ("48 89 DF E8 ? ? ? ? 84 C0 75 B3 48 8B 5D F8 C9 C3", "90 90", "75 B3", 10),
            ["EscapeFromBomb_OnEnter_NoEquipKnife"] = ("C6 83 7C 4F 00 00 00 48 8B 5D F8 C9 E9 ? ? ? ?", "C3 90 90 90 90", "E9 ? ? ? ?", 12),
            ["EscapeFromBomb_OnUpdate_NoEquipKnife"] = ("48 85 C0 0F 84 ? ? ? ? 48 89 DF 49 89 C4 E8 ? ? ? ? 31 F6 48 89 DF E8 ? ? ? ?", "90 90 90 90 90", "E8 ? ? ? ?", 15),
            ["EscapeFromFlames_OnEnter_NoEquipKnife"] = ("C6 83 54 4F 00 00 00 48 89 DF C6 83 7C 4F 00 00 00 E8 ? ? ? ? F3 0F 10 1D", "90 90 90 90 90", "E8 ? ? ? ?", 17),
            ["PlantBombLookAtPriorityLow"] = ("48 8D 55 C8 4C 89 E7 45 31 C9 F3 0F 10 40 08 45 31 C0 B9 02 00 00 00 48 89 5D C8 F3 0F 10 0D ? ? ? ? 48 8D 35 ? ? ? ? F3 0F 11 45 D0", "B9 00 00 00 00", "B9 02 00 00 00", 18),
            ["DefuseBombLookAtPriorityLow"] = ("4C 89 E2 45 31 C9 45 31 C0 F3 0F 10 05 ? ? ? ? B9 02 00 00 00 48 89 DF 48 8D 35 ? ? ? ? E8 ? ? ? ?", "B9 00 00 00 00", "B9 02 00 00 00", 17),
            ["AttackState_SkipSteadyFireShortcut"] = ("BA 01 00 00 00 48 89 DF 48 89 C6 E8 ? ? ? ? 84 C0 0F 84 ? ? ? ? 48 89 DF E8 ? ? ? ?", "90 90 90 90 90 90", "0F 84 ? ? ? ?", 18),
            ["AttackState_SkipZoomFireShortcut"] = ("F3 0F 10 05 ? ? ? ? 48 89 DF E8 ? ? ? ? 84 C0 0F 84 ? ? ? ? 83 BB C8 05 00 00 14", "90 90 90 90 90 90", "0F 84 ? ? ? ?", 18),
            ["AttackState_DodgeChance100_Always"] = ("48 89 DF F3 0F 11 85 48 FE FF FF E8 ? ? ? ? 84 C0 0F 84 ? ? ? ? 44 8B 2D ? ? ? ? 45 89 EE", "90 90 90 90 90 90", "0F 84 ? ? ? ?", 18),
            ["AttackState_RetreatOnSniper_Disable"] = ("48 8B 07 48 8D 15 ? ? ? ? 48 8B 80 38 05 00 00 48 39 D0 0F 85 ? ? ? ? 80 BF B8 05 00 00 00 74 73 4C 8D 35", "EB 73", "74 73", 33),
            ["AttackState_SkipSniperSpreadCheck"] = ("F3 0F 10 8B E0 52 00 00 66 0F EF C0 0F 2F C8 0F 86 ? ? ? ?", "90 90 90 90 90 90", "0F 86 ? ? ? ?", 15),
            ["AllSkill_KeepMoving_WhenSeeSniper"] = ("0F 2F 05 ? ? ? ? 76 0D 80 BB C4 05 00 00 00 0F 85", "90 90", "76 0D", 7),
            ["AttackState_CanStrafe_jne"] = ("BE 01 00 00 00 48 89 DF E8 ? ? ? ? 84 C0 74 5D 80 BB A1 5C 00 00 00 0F 84", "90 90", "74 5D", 15),
            ["AttackState_DodgeDuringReload"] = ("F3 0F 59 40 08 0F 2F C8 41 0F 97 44 24 44 48 81 C4 A8 01 00 00", "41 C6 44 24 44 01", "41 0F 97 44 24 44", 8),
            ["SniperCrouchDodge_jb"] = ("0F 2F F8 66 0F EF C0 41 0F 93 44 24 42 E8 ? ? ? ? 48 8B 43 08", "41 C6 44 24 42 01", "41 0F 93 44 24 42", 7),
            ["SniperDodge_SkipIsSniper_DodgeA"] = ("48 89 DF E8 ? ? ? ? 84 C0 0F 84 ? ? ? ? 44 8B 35 ? ? ? ? F3 0F 10 0D", "90 90 90 90 90 90", "0F 84 ? ? ? ?", 10),
            ["LowSKill_JumpChance0"] = ("4C 0F 2F 05 ? ? ? ? 76 11", "EB 40", "76 11", 8),
            ["Vision_SkipIsMovingGate"] = ("F3 0F 10 83 00 06 00 00 66 0F EF C9 0F 2F C1 7A 08 74 06 0F 87 ? ? ? ?", "90 90 90 90 90 90", "0F 87 ? ? ? ?", 19),
            ["InvestigateNoise_SkipSelfDefenseCheck"] = ("83 BB ? ? 00 00 02 74 1E", "90 90", "74 1E", 7),
            ["TBot_BombsiteSearch_UseKnownPlantedSite"] = ("48 8B BB 08 5E 00 00 E8 ? ? ? ? 4C 89 F7 E8 ? ? ? ? 49 8B 3C 24 31 F6", "E8 6C 1D F6 FF", "E8 5C 20 F6 FF", 15),
            ["OnBombPlanted_AllBotsLearnSite"] = ("48 8B 83 00 51 00 00 48 8B 40 18 80 B8 24 06 00 00 02 0F 84 EF 00 00 00 48 8B 7B 18", "E9 F0 00 00 00 90", "0F 84 EF 00 00 00", 18),
            ["CT_Defuse_EngageAndInvestigate"] = ("48 8B 05 ? ? ? ? BE 02 00 00 00 48 89 DF 48 89 83 C8 05 00 00 E8 ? ? ? ? BA 02 00 00 00 4C 89 EE E9 ? ? ? ?", "BE 00 00 00 00", "BE 02 00 00 00", 7),
            ["DefuseBombState_OnUpdate_EngageAndInvestigate"] = ("55 48 8D BE 00 51 00 00 48 89 E5 41 54 53 48 89 F3 E8 ? ? ? ? BE 02 00 00 00 48 89 DF 49 89 C4 E8 ? ? ? ?", "BE 00 00 00 00", "BE 02 00 00 00", 22),
            ["DefuseBombState_OnEnter_EngageAndInvestigate"] = ("55 48 89 E5 41 54 53 48 89 F3 BE 02 00 00 00 48 89 DF E8 ? ? ? ? 4C 8B A3 08 5E 00 00", "BE 00 00 00 00", "BE 02 00 00 00", 10),
            ["FlashbangAvoidance_Disable"] = ("48 8D 35 ? ? ? ? 4C 89 E7 49 C7 84 24 60 53 00 00 00 00 00 00 0F 5C C2 F3 0F 11 4D A0 F3 0F 10 0D ? ? ? ? 0F 13 45 98 F3 0F 10 05 ? ? ? ? E8 ? ? ? ? 41 C6 84 24 54 5C 00 00 00", "90 90 90 90 90 90 90 90 90 90 90 90 90 90", "E8 ? ? ? ? 41 C6 84 24 54 5C 00 00 00", 50)
        };
    }

    internal static class WindowsPatchDefinitions
    {
        internal static IReadOnlyDictionary<string, (string signature, string patch, string expectedOriginal, int patchOffset)> All { get; } = 
            new Dictionary<string, (string, string, string, int)>
        {
            ["HasVisitedEnemySpawn"] = ("40 88 B7 20 05 00 00", "C6 87 20 05 00 00 01", "40 88 B7 20 05 00 00", 0),
            ["GameState_Reset"] = ("83 7F 0C 00 74 07 C7 47 0C 00 00 00 00", "0F 1F 80 00 00 00 00", "C7 47 0C 00 00 00 00", 6),
            ["Idle_IsSafeAlwaysFalse"] = ("74 28 33 D2 48 8B CE E8 ? ? ? ? 84 C0 75 1A", "EB 28", "74 28", 0),
            ["EscapeFromBomb_OnEnter_NoEquipKnife"] = ("C6 83 ? ? 00 00 00 C6 83 ? ? 00 00 00 48 83 C4 20 5B E9", "C3 90 90 90 90", "E9 ? ? ? ?", 19),
            ["EscapeFromBomb_OnUpdate_NoEquipKnife"] = ("75 0F 48 8B 5C 24 50 48 83 C4 40 5F E9 ? ? ? ? E8 ? ? ? ?", "90 90 90 90 90", "E8 ? ? ? ?", 17),
            ["EscapeFromFlames_OnEnter_NoEquipKnife"] = ("48 8B CB 40 88 BB ? ? 00 00 40 88 BB ? ? 00 00 E8 ? ? ? ? F3 0F 10 0D", "90 90 90 90 90", "E8 ? ? ? ?", 17),
            ["PlantBombLookAtPriorityLow"] = ("41 B9 02 00 00 00 C6 44 24 38 00 F3 0F 10 0D", "41 B9 00 00 00 00", "41 B9 02 00 00 00", 0),
            ["DefuseBombLookAtPriorityLow"] = ("41 B9 02 00 00 00 C6 44 24 38 00 4C 8B C7", "41 B9 00 00 00 00", "41 B9 02 00 00 00", 0),
            ["AttackState_SkipSteadyFireShortcut"] = ("0F B6 F0 84 C0 74 3C 48 8B 4B 18 48 8B 11 FF 92 90 00 00 00", "90 90", "74 3C", 5),
            ["AttackState_SkipZoomFireShortcut"] = ("FF 90 ? ? 00 00 84 C0 74 15 48 8D 8B ? ? 00 00 48 89 AB", "90 90", "74 15", 8),
            ["AttackState_SkipSniperSpreadCheck"] = ("0F 28 CF 0F 57 C0 FF 15 ? ? ? ? F3 0F 10 0D ? ? ? ? 0F 2F C8 0F 86 ? ? ? ? 48 8B 9E ? ? 00 00", "90 90 90 90 90 90", "0F 86 ? ? ? ?", 23),
            ["AttackState_DodgeDuringReload"] = ("E9 ? ? ? ? 0F 2F BB ? 00 00 00 76 74", "EB 74", "76 74", 12),
            ["SniperCrouchDodge_jb"] = ("0F 2F BB ? 00 00 00 0F 28 7C 24 30 76 74", "90 90", "76 74", 12),
            ["LowSKill_JumpChance0"] = ("FF 90 90 00 00 00 0F 2F 05 ? ? ? ? 76 11", "EB 40", "76 11", 13),
            ["AttackState_DodgeChance100_Always"] = ("0F 28 F0 F3 0F 59 35 ? ? ? ? 76 ?", "EB 11", "76 ?", 11),
            ["AttackState_RetreatOnSniper_Disable"] = ("74 ? 48 8B CE E8 ? ? ? ? 84 C0 74 ? 48 8B CE E8 ? ? ? ? 84 C0", "EB", "74", 0),
            ["AllSkill_KeepMoving_WhenSeeSniper"] = ("0F 2F 05 ? ? ? ? 76 0D 80 BF ? ? 00 00 00 0F 85", "90 90", "76 0D", 7),
            ["AttackState_CanStrafe_jne"] = ("0F 2F 8B ? ? 00 00 0F 82 ? ? ? ? 48 8B CB E8 ? ? ? ? 84 C0 74 7B", "90 90", "74 7B", 23),
            ["SniperDodge_SkipIsSniper_DodgeA"] = ("84 F6 75 6A 48 8B 05", "90 90", "75 6A", 2),
            ["Vision_SkipIsMovingGate"] = ("0F 2F 35 ? ? ? ? 77 ? 49 8B ? 48 8B CF E8 ? ? ? ? 84 C0 75 ?", "90 90", "77 ?", 7),
            ["Upkeep_BotCOS_ZeroDrift"] = ("F3 0F 10 35 ? ? ? ? 48 8B 05 ? ? ? ? F3 0F 10 40 30 F3 0F 59 05 ? ? ? ? E8 ? ? ? ? F3 0F 59 C6 F3 0F 58 87 ? ? 00 00 F3 0F 11 87 ? ? 00 00", "0F 57 C0 90 90", "E8 ? ? ? ?", 28),
            ["Upkeep_BotSIN_ZeroDrift"] = ("F3 0F 11 87 ? ? 00 00 48 8B 05 ? ? ? ? F3 0F 10 40 30 F3 0F 59 05 ? ? ? ? E8 ? ? ? ? F3 0F 59 C6 F3 0F 58 87 ? ? 00 00 F3 0F 11 87 ? ? 00 00", "0F 57 C0 90 90", "E8 ? ? ? ?", 28),
            ["InvestigateNoise_SkipSelfDefenseCheck"] = ("83 BB ? ? 00 00 02 74 1E", "90 90", "74 1E", 7),
            ["TBot_BombsiteSearch_UseKnownPlantedSite"] = ("48 8B 8E ? ? 00 00 E8 ? ? ? ? ? 8B ? E8 ? ? ? ? 4C 8B 05 ? ? ? ? 85 C0", "E8 10 57 F9 FF", "E8 ? ? ? ?", 15),
            ["OnBombPlanted_AllBotsLearnSite"] = ("80 BA ? ? 00 00 02 0F 85 ? ? ? ? 48 85 DB 0F 84 ? ? ? ?", "90 90 90 90 90 90", "0F 85 ? ? ? ?", 7),
            ["CT_Defuse_EngageAndInvestigate"] = ("C7 86 ? ? 00 00 03 00 00 00 BA 02 00 00 00 48 8B CE", "BA 00 00 00 00", "BA 02 00 00 00", 10),
            ["DefuseBombState_OnUpdate_EngageAndInvestigate"] = ("48 8D 8A ? ? 00 00 48 8B DA E8 ? ? ? ? BA 02 00 00 00 48 8B CB", "BA 00 00 00 00", "BA 02 00 00 00", 15),
            ["DefuseBombState_OnEnter_EngageAndInvestigate"] = ("48 89 5C 24 08 57 48 83 EC 20 48 8B DA BA 02 00 00 00 48 8B CB", "BA 00 00 00 00", "BA 02 00 00 00", 13),
            ["FlashbangAvoidance_Disable"] = ("E8 ? ? ? ? 49 8B 06 C6 80 ? 5C 00 00 00 32 C0", "90 90 90 90 90 90 90 90 90 90 90 90 90 90 90", "E8 ? ? ? ? 49 8B 06 C6 80 ? 5C 00 00 00", 0)
        };
    }
}

namespace Common
{
    public static class GameUtils
    {
        public static string GetModulePath(string module)
        {
            return module switch
            {
                "server" => Addresses.ServerPath,
                "engine2" => Addresses.EnginePath,
                "tier0" => Addresses.Tier0Path,
                "schemasystem" => Addresses.SchemaSystemPath,
                "vscript" => Addresses.VScriptPath,
                _ => Path.Join(Server.GameDirectory, Constants.RootBinaryPath, Constants.ModulePrefix + module + Constants.ModuleSuffix),
            };
        }
    }

    public static class MemoryPatch
    {
        [DllImport("libc", EntryPoint = "mprotect", ExactSpelling = true)]
        public static extern int MProtect(nint address, int len, int protect);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public unsafe static extern bool VirtualProtect(nint address, int dwSize, int newProtect, int* oldProtect);

        public unsafe static bool SetMemAccess(nint addr, int size)
        {
            if (addr == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(addr));
            }

            int* oldProtect = stackalloc int[1];
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return VirtualProtect(addr, size, 0x40 /* PAGE_EXECUTE_READWRITE */, oldProtect);
            }

            return MProtect(LALIGN(addr), size + LDIFF(addr), 7 /* PROT_READ | PROT_WRITE | PROT_EXEC */) == 0;

            static nint LALIGN(nint a) => a & -4096;
            static int LDIFF(nint a) => (int)(a % 4096);
        }
    }
}

