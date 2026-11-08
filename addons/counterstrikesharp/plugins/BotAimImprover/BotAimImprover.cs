using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using RayTraceAPI;

namespace BotAimImprover;

[MinimumApiVersion(305)]
public class BotAimImprover : BasePlugin
{
    private readonly struct AimPoint(string n, float f, float lat, bool feetAbs = false)
    {
        public readonly string Name = n;
        public readonly float Frac = f;
        public readonly float Lateral = lat;
        public readonly bool FeetAbs = feetAbs;
    }

    private readonly struct Offsets(int ts, int en, int vis, int pbot, string sig)
    {
        public readonly int TargetSpot = ts;
        public readonly int Enemy = en;
        public readonly int IsVisible = vis;
        public readonly int PBot = pbot;
        public readonly string Sig = sig;
    }

    private enum AimMode
    {
        MIXED,
        HEAD,
        BODY
    }

    private static readonly AimPoint[] _aimPoints = new AimPoint[17]
    {
        new AimPoint("HEAD", 0.985f, 0f),
        new AimPoint("NECK", 0.95f, 0f),
        new AimPoint("JAW", 0.92f, 0f),
        new AimPoint("CHEST", 0.82f, 0f),
        new AimPoint("GUT", 0.67f, 0f),
        new AimPoint("PELVIS", 0.6f, 0f),
        new AimPoint("LEFT_CHEST", 0.82f, -8f),
        new AimPoint("RIGHT_CHEST", 0.82f, 8f),
        new AimPoint("LEFT_SHOULDER", 0.92f, -8f),
        new AimPoint("RIGHT_SHOULDER", 0.92f, 8f),
        new AimPoint("LEFT_GUT", 0.67f, -7f),
        new AimPoint("RIGHT_GUT", 0.67f, 7f),
        new AimPoint("LEFT_THIGH", 0.38f, -5f),
        new AimPoint("RIGHT_THIGH", 0.38f, 5f),
        new AimPoint("LEFT_SHIN", 0.15f, -5f),
        new AimPoint("RIGHT_SHIN", 0.15f, 5f),
        new AimPoint("FEET", 5f, 0f, feetAbs: true)
    };

    private static readonly int[] _priorityHead = new int[17]
    {
        0, 1, 2, 3, 4, 5, 6, 7, 10, 11,
        8, 9, 12, 13, 14, 15, 16
    };

    private static readonly int[] _priorityJaw = new int[17]
    {
        0, 1, 2, 3, 6, 7, 4, 5, 8, 9,
        10, 11, 12, 13, 14, 15, 16
    };

    private static readonly int[] _priorityBody = new int[17]
    {
        3, 4, 5, 6, 7, 10, 11, 8, 9, 2,
        1, 0, 12, 13, 14, 15, 16
    };

    private static readonly Offsets LinuxOffsets = new Offsets(22900, 23008, 23012, 5520, "55 48 89 E5 41 55 41 54 53 48 89 FB 48 83 EC 58 8B 8F E0 59 00 00 83 F9 FF");
    private static readonly Offsets WindowsOffsets = new Offsets(22932, 23040, 23044, 5024, "48 8B C4 55 57 48 8D 68 ? 48 81 EC ? ? ? ? 48 8B F9 0F 29 70 ? 8B 89 ? ? ? ? 83 F9 FF");

    private Offsets _off;
    private MemoryFunctionVoid<nint>? _pickNewAimSpot;

    private static readonly PluginCapability<CRayTraceInterface> _rayTraceCapability = new PluginCapability<CRayTraceInterface>("raytrace:craytraceinterface");
    private CRayTraceInterface? _cachedRayTrace;

    private Vector? _scratchEye;
    private Vector? _scratchTarget;

    private readonly ConcurrentDictionary<nint, int> _botToControllerSlot = new ConcurrentDictionary<nint, int>();

    private AimMode _aimMode = AimMode.MIXED;
    private string _botChallengeMode = "hard";
    private byte _cachedHumanTeam;

    private byte GetHumanTeam()
    {
        if (_cachedHumanTeam == 2 || _cachedHumanTeam == 3)
            return _cachedHumanTeam;
        for (int s = 0; s < 64; s++)
        {
            CCSPlayerController? p = Utilities.GetPlayerFromSlot(s);
            if (p != null && p.IsValid && !p.IsBot && !p.IsHLTV && (p.TeamNum == 2 || p.TeamNum == 3))
            {
                _cachedHumanTeam = p.TeamNum;
                return _cachedHumanTeam;
            }
        }
        return 0;
    }

    // Only AWP, shotguns, P90/Bizon use body-first; Scout (ssg08), Deagle, Rifles, Pistols and SMGs aim for HEAD/JAW first!
    private static readonly HashSet<string> _bodyFirstWeapons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_awp", "weapon_p90", "weapon_bizon",
        "weapon_nova", "weapon_xm1014", "weapon_sawedoff", "weapon_mag7"
    };

    private bool _firstOverrideLogged;
    private static FieldInfo? _serverMaxPlayersField;

    public override string ModuleName => "BotAimImprover";
    public override string ModuleVersion => "2.3.0";
    public override string ModuleAuthor => "ed0ard & htfy96 & XBribo & Antigravity";
    public override string ModuleDescription => "Restores intelligent headshot/jaw aim part selection for CS2 bots with slot-safe resolution.";

    private static void ResetServerMaxPlayersCache()
    {
        try
        {
            _serverMaxPlayersField ??= typeof(Server).GetField("_maxPlayers", BindingFlags.NonPublic | BindingFlags.Static);
            _serverMaxPlayersField?.SetValue(null, null);
        }
        catch
        {
        }
    }

    public override void Load(bool hotReload)
    {
        ResetServerMaxPlayersCache();

        bool isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        _off = isWin ? WindowsOffsets : LinuxOffsets;

        try
        {
            _pickNewAimSpot = new MemoryFunctionVoid<nint>(_off.Sig);
            long addr = _pickNewAimSpot.Handle.ToInt64();
            if (addr == 0L)
            {
                throw new InvalidOperationException("PickNewAimSpot signature resolved to zero address.");
            }
            _pickNewAimSpot.Hook(OnPickNewAimSpotPost, HookMode.Post);
            Logger.LogInformation("[BotAimImprover] v2.3.0 Loaded ({Plat}). PickNewAimSpot=0x{Pna:X16}", isWin ? "Windows" : "Linux", addr);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BotAimImprover] Fatal error during Load() (signature broken?). Plugin inactive.");
            return;
        }

        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            ResetServerMaxPlayersCache();
            _botToControllerSlot.Clear();
            _firstOverrideLogged = false;
        });

        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            ResetServerMaxPlayersCache();
            _botToControllerSlot.Clear();
            _cachedHumanTeam = 0;
            return HookResult.Continue;
        }, HookMode.Post);

        RegisterEventHandler<EventPlayerDisconnect>((ev, _) =>
        {
            try
            {
                CCSPlayerController? userid = ev.Userid;
                if (userid != null && userid.IsValid)
                {
                    int slot = userid.Slot;
                    foreach (KeyValuePair<nint, int> item in _botToControllerSlot)
                    {
                        if (item.Value == slot)
                        {
                            _botToControllerSlot.TryRemove(item.Key, out int _);
                        }
                    }
                }
            }
            catch
            {
            }
            return HookResult.Continue;
        }, HookMode.Post);

        AddCommand("bot_aim", "Set bot aim mode: head, body, mixed", (_, info) =>
        {
            string arg = (info.ArgCount > 1) ? info.GetArg(1).Trim().ToLowerInvariant() : "";
            string reply = arg switch
            {
                "head" => (_aimMode = AimMode.HEAD, "[BotAimImprover] aim mode -> HEAD (always head-first)").Item2,
                "body" => (_aimMode = AimMode.BODY, "[BotAimImprover] aim mode -> BODY (always body-first)").Item2,
                "mixed" => (_aimMode = AimMode.MIXED, "[BotAimImprover] aim mode -> MIXED (default)").Item2,
                _ => $"[BotAimImprover] Current aim mode: {_aimMode}. Valid values: head, body, mixed"
            };
            Server.PrintToConsole(reply);
        });

        AddCommand("bot_challenge", "Set bot challenge mode (off/normal/hard/easy)", (_, info) =>
        {
            if (info.ArgCount > 1)
            {
                string arg = info.GetArg(1).Trim().ToLowerInvariant();
                if (arg == "off" || arg == "normal" || arg == "hard" || arg == "easy")
                {
                    _botChallengeMode = arg;
                }
            }
            Server.PrintToConsole($"[BotAimImprover] bot_challenge is {_botChallengeMode}");
        });

        AddCommand("css_bot_challenge", "Set bot challenge mode (off/normal/hard/easy)", (_, info) =>
        {
            if (info.ArgCount > 1)
            {
                string arg = info.GetArg(1).Trim().ToLowerInvariant();
                if (arg == "off" || arg == "normal" || arg == "hard" || arg == "easy")
                {
                    _botChallengeMode = arg;
                }
            }
            Server.PrintToConsole($"[BotAimImprover] bot_challenge is {_botChallengeMode}");
        });
    }

    public override void Unload(bool hotReload)
    {
        try
        {
            _pickNewAimSpot?.Unhook(OnPickNewAimSpotPost, HookMode.Post);
        }
        catch
        {
        }
    }

    private unsafe HookResult OnPickNewAimSpotPost(DynamicHook hook)
    {
        try
        {
            nint pCCSBot = hook.GetParam<nint>(0);
            if (pCCSBot == IntPtr.Zero)
            {
                return HookResult.Continue;
            }
            if (ReadByte(pCCSBot + _off.IsVisible) == 0)
            {
                return HookResult.Continue;
            }
            int enemyHandle = ReadInt32(pCCSBot + _off.Enemy);
            if (enemyHandle == -1)
            {
                return HookResult.Continue;
            }
            int enemyEntIndex = enemyHandle & 0x7FFF;
            if (enemyEntIndex <= 0 || enemyEntIndex >= 4096)
            {
                return HookResult.Continue;
            }
            CCSPlayerPawn? enemyPawn = Utilities.GetEntityFromIndex<CCSPlayerPawn>(enemyEntIndex);
            if (enemyPawn == null || !enemyPawn.IsValid || enemyPawn.Handle == IntPtr.Zero)
            {
                return HookResult.Continue;
            }

            CCSPlayerController? botController = ResolveBotController(pCCSBot);
            if (botController == null || !TryGetBotEyePosition(botController, out CCSPlayerPawn? botPawn, out Vector eye))
            {
                return HookResult.Continue;
            }

            string? weaponName = botPawn?.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
            bool isBodyWeapon = weaponName != null && _bodyFirstWeapons.Contains(weaponName);
            int shotsFired = botPawn?.ShotsFired ?? 0;
            byte humanTeam = GetHumanTeam();
            bool isEnemyOfHuman = (humanTeam != 0 && botController.TeamNum != humanTeam);
            bool isAllyInHardMode = (humanTeam != 0 && botController.TeamNum == humanTeam && _botChallengeMode == "hard");

            // Realism: Enemy bots hold head aim for first 4 shots; Ally bots in hard mode transition to chest/body after 2 shots!
            int sprayPullDownShot = isAllyInHardMode ? 2 : 4;
            bool isSpraying = shotsFired >= sprayPullDownShot && weaponName != "weapon_awp" && weaponName != "weapon_ssg08";

            int[] priorityList;
            if (_aimMode == AimMode.HEAD)
            {
                priorityList = (weaponName == "weapon_awp" || isSpraying) ? _priorityBody : _priorityHead;
            }
            else if (_aimMode == AimMode.BODY)
            {
                priorityList = _priorityBody;
            }
            else
            {
                if (isEnemyOfHuman && _botChallengeMode != "easy" && _botChallengeMode != "off")
                {
                    // Enemy bots challenge the human player with lethal head-first priority on rifles/pistols
                    priorityList = (isBodyWeapon || isSpraying) ? _priorityBody : _priorityHead;
                }
                else
                {
                    // Ally bots or easy/off mode: use balanced priorityJaw / priorityBody so allies don't steal all kills
                    priorityList = (isBodyWeapon || isSpraying) ? _priorityBody : _priorityJaw;
                }
            }

            int chosenIdx = -1;
            float bestX = 0f, bestY = 0f, bestZ = 0f;

            foreach (int pointIdx in priorityList)
            {
                if (TryComputePartPos(enemyPawn, pointIdx, out float x, out float y, out float z) &&
                    PointVisibleFromEye(botPawn, eye, x, y, z))
                {
                    chosenIdx = pointIdx;
                    bestX = x;
                    bestY = y;
                    bestZ = z;
                    break;
                }
            }

            if (chosenIdx < 0)
            {
                return HookResult.Continue;
            }

            float* pTargetSpot = (float*)(pCCSBot + _off.TargetSpot).ToPointer();
            pTargetSpot[0] = bestX;
            pTargetSpot[1] = bestY;
            pTargetSpot[2] = bestZ;

            if (!_firstOverrideLogged)
            {
                _firstOverrideLogged = true;
                Logger.LogInformation("[BotAimImprover] Active: first override (weapon={W} point={P}).", weaponName ?? "(null)", _aimPoints[chosenIdx].Name);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BotAimImprover] Exception in PostHook");
        }
        return HookResult.Continue;
    }

    private CCSPlayerController? ResolveBotController(nint pCCSBot)
    {
        if (_botToControllerSlot.TryGetValue(pCCSBot, out int cachedSlot) && cachedSlot >= 0 && cachedSlot < 64)
        {
            CCSPlayerController? cachedPlayer = Utilities.GetPlayerFromSlot(cachedSlot);
            if (cachedPlayer != null && cachedPlayer.IsValid)
            {
                return cachedPlayer;
            }
            _botToControllerSlot.TryRemove(pCCSBot, out int _);
        }

        nint pawnPtrFromBot = IntPtr.Zero;
        try
        {
            pawnPtrFromBot = ReadIntPtr(pCCSBot + 24);
        }
        catch
        {
        }

        // Direct slot scan [0..63] — completely immune to Server.MaxPlayers == 1 caching bug!
        for (int slot = 0; slot < 64; slot++)
        {
            CCSPlayerController? player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid)
            {
                continue;
            }
            CCSPlayerPawn? pawn = player.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.Handle == IntPtr.Zero)
            {
                continue;
            }

            bool matched = false;
            try
            {
                if (pawn.Bot != null && pawn.Bot.Handle == pCCSBot)
                {
                    matched = true;
                }
                else if (pawnPtrFromBot != IntPtr.Zero && pawn.Handle == pawnPtrFromBot)
                {
                    matched = true;
                }
                else if (ReadIntPtr(pawn.Handle + _off.PBot) == pCCSBot)
                {
                    matched = true;
                }
            }
            catch
            {
                continue;
            }

            if (matched)
            {
                _botToControllerSlot[pCCSBot] = slot;
                return player;
            }
        }

        return null;
    }

    private bool TryGetBotEyePosition(CCSPlayerController bot, out CCSPlayerPawn? pawn, out Vector eye)
    {
        _scratchEye ??= new Vector(0f, 0f, 0f);
        eye = _scratchEye;
        pawn = bot.PlayerPawn?.Value;
        Vector? absOrigin = pawn?.AbsOrigin;
        if (pawn == null || absOrigin == null)
        {
            return false;
        }
        CNetworkViewOffsetVector? viewOffset = pawn.ViewOffset;
        float zOff = (viewOffset != null && viewOffset.Z > 10f) ? viewOffset.Z : 64f;
        eye.X = absOrigin.X;
        eye.Y = absOrigin.Y;
        eye.Z = absOrigin.Z + zOff;
        return true;
    }

    private static bool TryComputePartPos(CCSPlayerPawn enemyPawn, int idx, out float x, out float y, out float z)
    {
        x = y = z = 0f;
        if (idx < 0 || idx >= _aimPoints.Length)
        {
            return false;
        }
        Vector? absOrigin = enemyPawn.AbsOrigin;
        if (absOrigin == null)
        {
            return false;
        }
        ref readonly AimPoint pt = ref _aimPoints[idx];
        float ox = absOrigin.X;
        float oy = absOrigin.Y;
        float oz = absOrigin.Z;
        CNetworkViewOffsetVector? viewOffset = enemyPawn.ViewOffset;
        float eyeHeight = (viewOffset != null && viewOffset.Z > 10f) ? viewOffset.Z : 64f;
        QAngle? eyeAngles = enemyPawn.EyeAngles;
        double yawRad = ((eyeAngles != null) ? eyeAngles.Y : 0f) * Math.PI / 180.0;
        float rightX = (float)Math.Sin(yawRad);
        float rightY = (float)(0.0 - Math.Cos(yawRad));

        if (pt.FeetAbs)
        {
            x = ox;
            y = oy;
            z = oz + pt.Frac;
        }
        else
        {
            x = ox + rightX * pt.Lateral;
            y = oy + rightY * pt.Lateral;
            z = oz + eyeHeight * pt.Frac;
        }

        return !float.IsNaN(x) && !float.IsNaN(y) && !float.IsNaN(z) &&
               !float.IsInfinity(x) && !float.IsInfinity(y) && !float.IsInfinity(z);
    }

    private bool PointVisibleFromEye(CCSPlayerPawn? botPawn, Vector eye, float tx, float ty, float tz)
    {
        try
        {
            _cachedRayTrace ??= _rayTraceCapability.Get();
            if (_cachedRayTrace == null)
            {
                return true;
            }
            _scratchTarget ??= new Vector(0f, 0f, 0f);
            _scratchTarget.X = tx;
            _scratchTarget.Y = ty;
            _scratchTarget.Z = tz;

            RayTraceAPI.TraceOptions opts = new RayTraceAPI.TraceOptions((InteractionLayers)12289, (InteractionLayers)0, false);
            _cachedRayTrace.TraceEndShape(eye, _scratchTarget, botPawn, opts, out RayTraceAPI.TraceResult res);
            return res.Fraction >= 0.94f;
        }
        catch
        {
            return true;
        }
    }

    private static unsafe byte ReadByte(nint addr) => *(byte*)addr.ToPointer();
    private static unsafe int ReadInt32(nint addr) => *(int*)addr.ToPointer();
    private static unsafe nint ReadIntPtr(nint addr) => *(nint*)addr.ToPointer();
}

