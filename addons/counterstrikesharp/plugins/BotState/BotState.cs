using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using RayTraceAPI;

namespace BotState;

public class BotState : BasePlugin
{
    private struct FlashDecision
    {
        public float FirstSeen;
        public float LastSeen;
        public float DetonateAt;
        public bool Avoided;
        public bool TurnedAway;
        public bool RestoredYaw;
        public float SavedYaw;
    }

    private static class BotControllerBridge
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object? TryGet()
        {
            return new PluginCapability<IBotControllerApi>("botcontroller:api").Get();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool SwitchBotWeapon(object api, int slot, int defIndex)
        {
            return ((IBotControllerApi)api).SwitchBotWeapon(slot, defIndex);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long InjectUsercmd(object api, int slot, ulong buttonMask, int durationMs = 0)
        {
            return ((IBotControllerApi)api).InjectUsercmd(slot, buttonMask, durationMs);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool LockKnife(object api, int slot)
        {
            return ((IBotControllerApi)api).Lock(slot, (LockTarget)3);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsKnifeLocked(object api, int slot)
        {
            return (int)((IBotControllerApi)api).GetWeaponLock(slot) == 3;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool UnlockWeapon(object api, int slot)
        {
            return ((IBotControllerApi)api).Unlock(slot, (LockKind)2);
        }
    }

    private const int KnifeDefinitionIndex = 9001;
    private const float ReloadInterruptCooldown = 0.75f;
    private const ulong InspectButtonMask = 34359738368uL;
    private const float FlashFuseSeconds = 1.45f;
    private const float FlashFovHorizDeg = 115f;
    private const float FlashFovVertDeg = 90f;

    private ConVar? _smokeConVar;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _gunReequipTimer;
    private readonly Random _random = new Random();

    private readonly Dictionary<int, float> _doorEventCooldown = new Dictionary<int, float>();
    private readonly Dictionary<int, float> _stuckStartTime = new Dictionary<int, float>();
    private readonly Dictionary<int, (float X, float Y, float Z)> _stuckStartPos = new Dictionary<int, (float, float, float)>();
    private readonly Dictionary<int, bool> _stuckJumpDone = new Dictionary<int, bool>();
    private readonly Dictionary<int, int> _stuckJumpCount = new Dictionary<int, int>();
    private readonly Dictionary<int, float> _stuckMaxSpeed = new Dictionary<int, float>();
    private readonly Dictionary<int, float> _idleStartTime = new Dictionary<int, float>();
    private readonly Dictionary<int, float> _lastRepathTime = new Dictionary<int, float>();
    private readonly Dictionary<int, float> _reloadInterruptCooldown = new Dictionary<int, float>();

    private bool _isFreezeTime;

    private static readonly PluginCapability<CRayTraceInterface> RayTraceCap = new PluginCapability<CRayTraceInterface>("raytrace:craytraceinterface");
    private CRayTraceInterface? _rayTrace;
    private Vector? _scratchEye;
    private Vector? _traceEnd;

    private readonly HashSet<int> _knifeLockedBotSlots = new HashSet<int>();
    private object? _botController;
    private bool _eliminationHandled;

    // Event-driven flashbang tracking (zero FindAllEntitiesByDesignerName GC overhead on tick)
    private readonly List<CBaseEntity> _activeFlashEntities = new List<CBaseEntity>(16);
    private readonly Dictionary<uint, float> _flashThrownAt = new Dictionary<uint, float>();
    private readonly Dictionary<int, HashSet<uint>> _flashRolledByBot = new Dictionary<int, HashSet<uint>>();
    private readonly Dictionary<(int bot, uint flash), FlashDecision> _flashDecisions = new Dictionary<(int, uint), FlashDecision>();
    private bool _debugFlash;

    // Cached bot controllers updated every 4 ticks to avoid per-tick entity enumeration
    private readonly List<CCSPlayerController> _cachedControllers = new List<CCSPlayerController>(16);
    private int _tickCounter;

    public override string ModuleName => "Smarter-Bot";
    public override string ModuleVersion => "2.1.0";
    public override string ModuleAuthor => "Antigravity, ed0ard & XBribo";
    public override string ModuleDescription => "Synchronized with BotProTactics: Zero-GC Flash Dodge with Yaw Restore, 8-Way RayTrace Unstuck & Reload Cancel";

    public override void Load(bool hotReload)
    {
        _smokeConVar = ConVar.Find("bot_max_visible_smoke_length");
        RegisterEventHandler<EventRoundStart>(OnRoundStart, HookMode.Post);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt, HookMode.Post);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn, HookMode.Post);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd, HookMode.Post);
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind, HookMode.Pre);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted, HookMode.Post);
        RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse, HookMode.Post);
        RegisterEventHandler<EventDoorOpen>(OnDoorOpen, HookMode.Post);
        RegisterEventHandler<EventDoorClose>(OnDoorClose, HookMode.Post);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire, HookMode.Post);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Post);
        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        RegisterListener<Listeners.OnTick>(OnTick);
        _gunReequipTimer = AddTimer(1f, ReequipGunForActiveBots, TimerFlags.REPEAT);
        Logger.LogInformation("[BotState] v2.1.0 Loaded! Zero-GC Flash Dodge, RayTrace Anti-Stuck, Crosshair Anchor active.");
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try
        {
            _rayTrace = RayTraceCap.Get();
        }
        catch
        {
            _rayTrace = null;
        }
        Logger.LogInformation("[BotState] RayTrace API: {Status}", _rayTrace != null ? "Connected" : "Unavailable");
        if (_rayTrace == null)
        {
            Console.WriteLine("[Smarter-Bot] Ray-Trace not available");
        }
        else
        {
            _scratchEye = new Vector(0f, 0f, 0f);
            _traceEnd = new Vector(0f, 0f, 0f);
        }

        try
        {
            _botController = BotControllerBridge.TryGet();
        }
        catch
        {
            _botController = null;
        }
        if (_botController == null)
        {
            Console.WriteLine("[Smarter-Bot] BotController API not available");
        }
    }

    private void OnEntitySpawned(CEntityInstance entity)
    {
        if (entity == null || !entity.IsValid)
        {
            return;
        }
        if (entity.DesignerName == "flashbang_projectile")
        {
            CBaseEntity baseEnt = entity.As<CBaseEntity>();
            if (baseEnt != null && baseEnt.IsValid)
            {
                _activeFlashEntities.Add(baseEnt);
                _flashThrownAt[entity.Index] = Server.CurrentTime;
            }
        }
    }

    [ConsoleCommand("css_botstate_flashdebug", "Toggle Smarter-Bot flashbang debug log")]
    public void OnFlashDebugCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string arg = cmd.GetArg(1);
            _debugFlash = arg == "1" || arg.Equals("true", StringComparison.OrdinalIgnoreCase) || arg.Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            _debugFlash = !_debugFlash;
        }
        cmd.ReplyToCommand($"[Smarter-Bot] flash debug = {_debugFlash}, raytrace = {_rayTrace != null}");
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo _)
    {
        return HookResult.Continue;
    }

    private void SetSmokeLength(float value)
    {
        try
        {
            if (_smokeConVar == null)
            {
                _smokeConVar = ConVar.Find("bot_max_visible_smoke_length");
            }
            if (_smokeConVar != null)
            {
                _smokeConVar.SetValue(value);
            }
        }
        catch
        {
        }
    }

    public override void Unload(bool hotReload)
    {
        ReleaseKnifeLocks();
        SetSmokeLength(0f);
        _gunReequipTimer?.Kill();
    }

    private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        CCSPlayerController? userid = @event.Userid;
        if (userid != null && userid.IsValid && userid.IsBot && !userid.HasBeenControlledByPlayerThisRound)
        {
            int index = (int)userid.Index;
            float currentTime = Server.CurrentTime;
            (int, uint)? matchedKey = null;
            FlashDecision matchedDecision = default;
            float bestDiff = float.MaxValue;

            foreach (KeyValuePair<(int, uint), FlashDecision> kvp in _flashDecisions)
            {
                if (kvp.Key.Item1 == index)
                {
                    float diff = Math.Abs(kvp.Value.DetonateAt - currentTime);
                    if (diff < bestDiff && diff < 0.30f)
                    {
                        bestDiff = diff;
                        matchedKey = kvp.Key;
                        matchedDecision = kvp.Value;
                    }
                }
            }

            bool avoided = (_rayTrace == null) ? (_random.NextDouble() <= 0.55) : (matchedKey.HasValue && matchedDecision.Avoided);
            if (avoided)
            {
                @event.BlindDuration = 0f;
                CCSPlayerPawn? pawn = userid.PlayerPawn?.Value;
                if (pawn != null && pawn.IsValid)
                {
                    pawn.BlindStartTime = 0f;
                    pawn.BlindUntilTime = 0f;
                    pawn.FlashDuration = 0f;
                    pawn.FlashMaxAlpha = 0f;
                }
            }
        }
        return HookResult.Continue;
    }

    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        CCSPlayerController? player = @event.Userid;
        if (player != null && player.IsValid && player.IsBot)
        {
            Server.NextFrame(() =>
            {
                if (player.IsValid)
                {
                    ApplyBotState(player);
                }
            });
        }
        return HookResult.Continue;
    }

    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        _isFreezeTime = false;
        SetSmokeLength(0f);
        foreach (CCSPlayerController item in Utilities.GetPlayers())
        {
            if (item != null && item.IsValid && item.IsBot)
            {
                ApplyBotState(item);
            }
        }
        return HookResult.Continue;
    }

    private void OnTick()
    {
        try
        {
            if (!string.IsNullOrEmpty(Server.MapName))
            {
                OnTickCore();
            }
        }
        catch
        {
        }
    }

    private void RefreshBotControllersIfNeeded()
    {
        if (_tickCounter % 4 == 0 || _cachedControllers.Count == 0)
        {
            _cachedControllers.Clear();
            foreach (CCSPlayerController p in Utilities.GetPlayers())
            {
                if (p != null && p.IsValid && p.IsBot)
                {
                    _cachedControllers.Add(p);
                }
            }
        }
        _tickCounter++;
    }

    private void OnTickCore()
    {
        RefreshBotControllersIfNeeded();
        ProcessFlashbangAvoidance();

        float currentTime = Server.CurrentTime;
        string mapName = Server.MapName ?? "";
        bool isInferno = string.Equals(mapName, "de_inferno", StringComparison.OrdinalIgnoreCase);

        for (int i = 0; i < _cachedControllers.Count; i++)
        {
            CCSPlayerController item = _cachedControllers[i];
            if (item == null || !item.IsValid || !item.IsBot || item.HasBeenControlledByPlayerThisRound)
            {
                continue;
            }
            CCSPlayerPawn? pawn = item.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.LifeState != 0)
            {
                continue;
            }
            CCSBot? bot = pawn.Bot;
            if (bot == null)
            {
                continue;
            }

            int index = (int)item.Index;
            InterruptReload(item, pawn, bot, currentTime);

            // Keep core alertness & responsiveness active without overriding BotProTactics fire cadence or movement physics
            bot.IsSleeping = false;
            bot.AllowActive = true;
            bot.PeripheralTimestamp = 0f;

            CountdownTimer alertTimer = bot.AlertTimer;
            alertTimer.Duration = 600f;
            alertTimer.Timestamp = currentTime + 600f;
            alertTimer.Timescale = 1f;

            CountdownTimer ignoreEnemiesTimer = bot.IgnoreEnemiesTimer;
            ignoreEnemiesTimer.Duration = 0f;
            ignoreEnemiesTimer.Timestamp = 0f;
            ignoreEnemiesTimer.Timescale = 1f;

            CountdownTimer panicTimer = bot.PanicTimer;
            panicTimer.Duration = 0f;
            panicTimer.Timestamp = 0f;
            panicTimer.Timescale = 1f;

            CountdownTimer surpriseTimer = bot.SurpriseTimer;
            surpriseTimer.Duration = 0f;
            surpriseTimer.Timestamp = 0f;
            surpriseTimer.Timescale = 1f;

            bot.IsWaitingBehindFriend = false;
            CountdownTimer politeTimer = bot.PoliteTimer;
            politeTimer.Duration = 0f;
            politeTimer.Timestamp = 0f;
            politeTimer.Timescale = 1f;

            if (bot.IsAimingAtEnemy && !bot.IsAttacking)
            {
                bot.IsAttacking = true;
            }

            bool doorCooldown = _doorEventCooldown.TryGetValue(index, out float cdUntil) && currentTime < cdUntil;
            if (doorCooldown)
            {
                continue;
            }

            // 8-Way RayTrace Unstuck Solver (only when genuinely stuck on geometry)
            if (bot.IsStuck)
            {
                bot.IsRunning = true;
                bot.JumpTimestamp = 0f;
                CountdownTimer stuckJumpTimer = bot.StuckJumpTimer;
                stuckJumpTimer.Duration = 0f;
                stuckJumpTimer.Timestamp = currentTime;
                stuckJumpTimer.Timescale = 1f;

                float speed2D = MathF.Sqrt(pawn.AbsVelocity.X * pawn.AbsVelocity.X + pawn.AbsVelocity.Y * pawn.AbsVelocity.Y);
                Vector? absOrigin = pawn.AbsOrigin;
                if (absOrigin != null)
                {
                    if (!_stuckStartTime.ContainsKey(index))
                    {
                        _stuckStartTime[index] = currentTime;
                        _stuckStartPos[index] = (absOrigin.X, absOrigin.Y, absOrigin.Z);
                        _stuckJumpDone[index] = false;
                        _stuckMaxSpeed[index] = 0f;
                    }
                    if (speed2D > _stuckMaxSpeed.GetValueOrDefault(index))
                    {
                        _stuckMaxSpeed[index] = speed2D;
                    }
                    float stuckElapsed = currentTime - _stuckStartTime.GetValueOrDefault(index);
                    var startPos = _stuckStartPos.GetValueOrDefault(index, (absOrigin.X, absOrigin.Y, absOrigin.Z));
                    float movedDist = MathF.Sqrt(
                        (absOrigin.X - startPos.Item1) * (absOrigin.X - startPos.Item1) +
                        (absOrigin.Y - startPos.Item2) * (absOrigin.Y - startPos.Item2));
                    float maxSpd = _stuckMaxSpeed.GetValueOrDefault(index);

                    bool hardStuck = stuckElapsed >= 0.65f && maxSpd <= 15f;
                    bool softStuck = stuckElapsed >= 1.6f && movedDist < 65f;
                    if ((hardStuck || softStuck) && !_stuckJumpDone.GetValueOrDefault(index))
                    {
                        bot.IsCrouching = false;
                        _stuckJumpDone[index] = true;
                        int attempt = _stuckJumpCount.GetValueOrDefault(index);
                        _stuckJumpCount[index] = attempt + 1;
                        float escapeYawRad = FindBestEscapeYawRad(absOrigin, pawn.EyeAngles.Y, attempt);
                        pawn.AbsVelocity.X = MathF.Cos(escapeYawRad) * 185f;
                        pawn.AbsVelocity.Y = MathF.Sin(escapeYawRad) * 185f;

                        CountdownTimer repathTimer = bot.RepathTimer;
                        repathTimer.Duration = 0f;
                        repathTimer.Timestamp = currentTime;
                        repathTimer.Timescale = 1f;

                        _stuckStartTime[index] = currentTime;
                        _stuckStartPos[index] = (absOrigin.X, absOrigin.Y, absOrigin.Z);
                        _stuckMaxSpeed[index] = 0f;
                    }
                }
            }
            else
            {
                _stuckStartTime.Remove(index);
                _stuckStartPos.Remove(index);
                _stuckJumpDone.Remove(index);
                _stuckMaxSpeed.Remove(index);

                float speed2D = MathF.Sqrt(pawn.AbsVelocity.X * pawn.AbsVelocity.X + pawn.AbsVelocity.Y * pawn.AbsVelocity.Y);
                if (speed2D < 5f)
                {
                    if (!_idleStartTime.ContainsKey(index))
                    {
                        _idleStartTime[index] = currentTime;
                    }
                    float idleTime = currentTime - _idleStartTime[index];
                    float lastRepath = _lastRepathTime.GetValueOrDefault(index, -999f);
                    if (idleTime >= 38f && currentTime - lastRepath >= 15f && !bot.IsAttacking && !pawn.IsDefusing)
                    {
                        bot.IsCrouching = false;
                        _lastRepathTime[index] = currentTime;
                        CountdownTimer repathTimer2 = bot.RepathTimer;
                        repathTimer2.Duration = 0f;
                        repathTimer2.Timestamp = currentTime;
                        repathTimer2.Timescale = 1f;
                        ResetLookAroundForBot(item);
                    }
                }
                else
                {
                    _idleStartTime.Remove(index);
                }
            }

            if (isInferno && pawn.AbsOrigin != null)
            {
                Vector absOrigin2 = pawn.AbsOrigin;
                float dx = absOrigin2.X - 285f;
                float dy = absOrigin2.Y - 450f;
                if (dx * dx + dy * dy < 2500f)
                {
                    CountdownTimer repathTimer3 = bot.RepathTimer;
                    repathTimer3.Duration = 0f;
                    repathTimer3.Timestamp = currentTime;
                    repathTimer3.Timescale = 1f;
                }
            }
        }
    }

    private float FindBestEscapeYawRad(Vector origin, float currentYawDeg, int attemptIndex)
    {
        if (_rayTrace != null && _scratchEye != null && _traceEnd != null)
        {
            try
            {
                _scratchEye.X = origin.X;
                _scratchEye.Y = origin.Y;
                _scratchEye.Z = origin.Z + 38f;
                RayTraceAPI.TraceOptions opts = new RayTraceAPI.TraceOptions((InteractionLayers)12305, (InteractionLayers)0, false);
                float bestFrac = -1f;
                float result = (currentYawDeg + 180f) * (MathF.PI / 180f);
                RayTraceAPI.TraceResult res = default;
                for (int i = 0; i < 8; i++)
                {
                    int idx = (i + attemptIndex * 3) % 8;
                    float candidateRad = (currentYawDeg + 90f + idx * 45f) * (MathF.PI / 180f);
                    _traceEnd.X = origin.X + MathF.Cos(candidateRad) * 145f;
                    _traceEnd.Y = origin.Y + MathF.Sin(candidateRad) * 145f;
                    _traceEnd.Z = origin.Z + 38f;
                    if (_rayTrace.TraceEndShape(_scratchEye, _traceEnd, null, opts, out res) && res.Fraction > bestFrac)
                    {
                        bestFrac = res.Fraction;
                        result = candidateRad;
                    }
                }
                return result;
            }
            catch
            {
            }
        }
        float dirSign = (attemptIndex % 2 == 0) ? 1f : -1f;
        return currentYawDeg * (MathF.PI / 180f) + MathF.PI + (MathF.PI / 4f) * dirSign;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ReleaseKnifeLocks();
        SetSmokeLength(0f);
        _eliminationHandled = false;
        _isFreezeTime = true;
        _doorEventCooldown.Clear();
        _stuckStartTime.Clear();
        _stuckStartPos.Clear();
        _stuckJumpDone.Clear();
        _stuckJumpCount.Clear();
        _stuckMaxSpeed.Clear();
        _idleStartTime.Clear();
        _lastRepathTime.Clear();
        _reloadInterruptCooldown.Clear();
        _activeFlashEntities.Clear();
        _flashThrownAt.Clear();
        _flashRolledByBot.Clear();
        _flashDecisions.Clear();
        Logger.LogInformation("[BotState] Round Start - bot state caches and anti-stuck tracking reset.");
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (_botController != null)
        {
            CCSPlayerController? userid = @event.Userid;
            if (userid != null && userid.IsValid)
            {
                CsTeam victimTeam = (CsTeam)userid.TeamNum;
                if (victimTeam != CsTeam.Terrorist && victimTeam != CsTeam.CounterTerrorist)
                {
                    return HookResult.Continue;
                }
                bool prevEliminated = _eliminationHandled;
                HandleTeamElimination(userid.Slot, victimTeam);
                if (prevEliminated || !_eliminationHandled)
                {
                    MaybeInspectOnKill(@event.Attacker);
                }
            }
        }
        return HookResult.Continue;
    }

    private void MaybeInspectOnKill(CCSPlayerController? attacker)
    {
        if (_botController != null && attacker != null && attacker.IsValid && attacker.IsBot && attacker.PawnIsAlive && !attacker.HasBeenControlledByPlayerThisRound && _random.NextDouble() < 0.1)
        {
            QueueInspectInjection(attacker.Slot);
        }
    }

    private void HandleTeamElimination(int victimSlot, CsTeam victimTeam)
    {
        if (_eliminationHandled || _botController == null)
        {
            return;
        }
        List<CCSPlayerController> players = Utilities.GetPlayers()
            .Where(p => p != null && p.IsValid && !p.IsHLTV && (p.TeamNum == 2 || p.TeamNum == 3))
            .ToList();

        if (players.Any(p => p.Slot != victimSlot && p.TeamNum == (int)victimTeam && p.PawnIsAlive))
        {
            return;
        }

        CsTeam winningTeam = (victimTeam == CsTeam.Terrorist) ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
        if (!players.Any(p => p.TeamNum == (int)winningTeam && p.PawnIsAlive))
        {
            return;
        }

        _eliminationHandled = true;
        foreach (CCSPlayerController bot in players.Where(p => p.IsBot && p.PawnIsAlive && !p.HasBeenControlledByPlayerThisRound && p.TeamNum == (int)winningTeam))
        {
            bool switched = BotControllerBridge.SwitchBotWeapon(_botController, bot.Slot, KnifeDefinitionIndex);
            bool locked = BotControllerBridge.LockKnife(_botController, bot.Slot);
            if (switched)
            {
                QueueInspectInjection(bot.Slot);
            }
            if (locked)
            {
                _knifeLockedBotSlots.Add(bot.Slot);
            }
        }
    }

    private void QueueInspectInjection(int slot)
    {
        Server.NextFrame(() =>
        {
            if (_botController != null)
            {
                CCSPlayerController? playerFromSlot = Utilities.GetPlayerFromSlot(slot);
                if (playerFromSlot != null && playerFromSlot.IsValid && playerFromSlot.IsBot && playerFromSlot.PawnIsAlive && !playerFromSlot.HasBeenControlledByPlayerThisRound)
                {
                    BotControllerBridge.InjectUsercmd(_botController, slot, InspectButtonMask);
                }
            }
        });
    }

    private void ReleaseKnifeLocks()
    {
        if (_botController != null)
        {
            foreach (int slot in _knifeLockedBotSlots)
            {
                if (BotControllerBridge.IsKnifeLocked(_botController, slot))
                {
                    BotControllerBridge.UnlockWeapon(_botController, slot);
                }
            }
        }
        _knifeLockedBotSlots.Clear();
    }

    private HookResult OnDoorOpen(EventDoorOpen @event, GameEventInfo info)
    {
        CCSPlayerController? userid = @event.Userid;
        if (userid != null && userid.IsValid && userid.IsBot)
        {
            _doorEventCooldown[(int)userid.Index] = Server.CurrentTime + 1f;
        }
        return HookResult.Continue;
    }

    private HookResult OnDoorClose(EventDoorClose @event, GameEventInfo info)
    {
        CCSPlayerController? userid = @event.Userid;
        if (userid != null && userid.IsValid && userid.IsBot)
        {
            _doorEventCooldown[(int)userid.Index] = Server.CurrentTime + 1f;
        }
        return HookResult.Continue;
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        CCSPlayerController? userid = @event.Userid;
        if (userid != null && userid.IsValid && userid.IsBot)
        {
            CCSPlayerPawn? pawn = userid.PlayerPawn?.Value;
            if (pawn != null && pawn.IsValid && pawn.Bot != null)
            {
                // Do NOT zero or clamp AbsVelocity here; BotProTactics manages counter-strafing via usercmd!
                if (!pawn.IsDefusing && pawn.Bot.IsAttacking)
                {
                    CountdownTimer sneakTimer = pawn.Bot.SneakTimer;
                    sneakTimer.Duration = 0f;
                    sneakTimer.Timestamp = 0f;
                    sneakTimer.Timescale = 1f;
                }
            }
        }
        return HookResult.Continue;
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        foreach (CCSPlayerController item in Utilities.GetPlayers())
        {
            if (item != null && item.IsValid && item.IsBot && !item.HasBeenControlledByPlayerThisRound)
            {
                CCSPlayerPawn? pawn = item.PlayerPawn?.Value;
                if (pawn != null && pawn.IsValid && pawn.Bot != null)
                {
                    CountdownTimer hurryTimer = pawn.Bot.HurryTimer;
                    hurryTimer.Duration = 12f;
                    hurryTimer.Timestamp = Server.CurrentTime + 12f;
                    hurryTimer.Timescale = 1f;
                }
            }
        }
        return HookResult.Continue;
    }

    private HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        ResetLookAroundForBot(@event.Userid);
        return HookResult.Continue;
    }

    private static void ResetLookAroundForBot(CCSPlayerController? player)
    {
        if (player != null && player.IsValid && player.IsBot)
        {
            CCSBot? bot = player.PlayerPawn?.Value?.Bot;
            if (bot != null)
            {
                bot.CheckedHidingSpotCount = 0;
                bot.LookAroundStateTimestamp = 0f;
            }
        }
    }

    private static void ApplyBotState(CCSPlayerController player)
    {
        CCSBot? bot = player.PlayerPawn?.Value?.Bot;
        if (bot != null)
        {
            bot.SafeTime = 0f;
            bot.HasVisitedEnemySpawn = true;
        }
    }

    private void InterruptReload(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, float now)
    {
        if (_botController == null || !bot.IsEnemyVisible || !IsReloading(player))
        {
            return;
        }
        int slot = player.Slot;
        if ((!_reloadInterruptCooldown.TryGetValue(slot, out float cdUntil) || now >= cdUntil) &&
            GetReloadInterruptWeapon(pawn, out int weaponDefIndex) &&
            BotControllerBridge.SwitchBotWeapon(_botController, slot, KnifeDefinitionIndex))
        {
            _reloadInterruptCooldown[slot] = now + ReloadInterruptCooldown;
            Server.NextFrame(() => SwitchBackAfterReloadInterrupt(slot, weaponDefIndex));
        }
    }

    private static bool GetReloadInterruptWeapon(CCSPlayerPawn pawn, out int weaponDefIndex)
    {
        weaponDefIndex = -1;
        int pistolDefIndex = -1;
        var myWeapons = pawn.WeaponServices?.MyWeapons;
        if (myWeapons == null)
        {
            return false;
        }
        foreach (CHandle<CBasePlayerWeapon> handle in myWeapons)
        {
            CBasePlayerWeapon? value = handle.Value;
            if (value == null || !value.IsValid)
            {
                continue;
            }
            CCSWeaponBase weapon = new CCSWeaponBase(value.Handle);
            CCSWeaponBaseVData? vData = weapon.VData;
            if (vData == null)
            {
                continue;
            }
            int itemDef = weapon.AttributeManager.Item.ItemDefinitionIndex;
            if (itemDef > 0)
            {
                if ((int)vData.GearSlot == 0 && weapon.Clip1 > 4)
                {
                    weaponDefIndex = itemDef;
                    return true;
                }
                if (pistolDefIndex < 0 && (int)vData.GearSlot == 1 && weapon.Clip1 > 0)
                {
                    pistolDefIndex = itemDef;
                }
            }
        }
        weaponDefIndex = pistolDefIndex;
        return weaponDefIndex >= 0;
    }

    private void SwitchBackAfterReloadInterrupt(int slot, int weaponDefIndex)
    {
        if (_botController != null)
        {
            CCSPlayerController? playerFromSlot = Utilities.GetPlayerFromSlot(slot);
            if (playerFromSlot != null && playerFromSlot.IsValid && playerFromSlot.IsBot && playerFromSlot.PawnIsAlive && !playerFromSlot.HasBeenControlledByPlayerThisRound)
            {
                BotControllerBridge.SwitchBotWeapon(_botController, slot, weaponDefIndex);
            }
        }
    }

    private void ReequipGunForActiveBots()
    {
        if (_botController == null || _isFreezeTime)
        {
            return;
        }
        List<CCSPlayerController> players = Utilities.GetPlayers();
        int aliveT = 0;
        int aliveCT = 0;
        foreach (CCSPlayerController item in players)
        {
            if (item != null && item.IsValid && item.PawnIsAlive)
            {
                if (item.TeamNum == 2) aliveT++;
                else if (item.TeamNum == 3) aliveCT++;
            }
        }
        if (aliveT == 0 && aliveCT == 0)
        {
            return;
        }
        foreach (CCSPlayerController bot in players)
        {
            if (bot == null || !bot.IsValid || !bot.IsBot || !bot.PawnIsAlive || bot.HasBeenControlledByPlayerThisRound)
            {
                continue;
            }
            int enemiesAlive = (bot.TeamNum == 2) ? aliveCT : ((bot.TeamNum == 3) ? aliveT : 0);
            if (enemiesAlive == 0)
            {
                continue;
            }
            CCSPlayerPawn? pawn = bot.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.IsDefusing)
            {
                continue;
            }
            CBasePlayerWeapon? activeWeapon = pawn.WeaponServices?.ActiveWeapon?.Value;
            if (activeWeapon != null && activeWeapon.IsValid)
            {
                CCSWeaponBaseVData? vData = new CCSWeaponBase(activeWeapon.Handle).VData;
                if (vData != null && (int)vData.GearSlot == 2 && GetReequipWeapon(pawn, out int weaponDefIndex))
                {
                    BotControllerBridge.SwitchBotWeapon(_botController, bot.Slot, weaponDefIndex);
                }
            }
        }
    }

    private static bool GetReequipWeapon(CCSPlayerPawn pawn, out int weaponDefIndex)
    {
        weaponDefIndex = -1;
        int pistolDef = -1;
        var myWeapons = pawn.WeaponServices?.MyWeapons;
        if (myWeapons == null)
        {
            return false;
        }
        foreach (CHandle<CBasePlayerWeapon> handle in myWeapons)
        {
            CBasePlayerWeapon? value = handle.Value;
            if (value == null || !value.IsValid)
            {
                continue;
            }
            CCSWeaponBase weapon = new CCSWeaponBase(value.Handle);
            CCSWeaponBaseVData? vData = weapon.VData;
            if (vData == null)
            {
                continue;
            }
            int itemDef = weapon.AttributeManager.Item.ItemDefinitionIndex;
            if (itemDef > 0)
            {
                if ((int)vData.GearSlot == 0)
                {
                    weaponDefIndex = itemDef;
                    return true;
                }
                if (pistolDef < 0 && (int)vData.GearSlot == 1)
                {
                    pistolDef = itemDef;
                }
            }
        }
        weaponDefIndex = pistolDef;
        return weaponDefIndex >= 0;
    }

    private static bool IsReloading(CCSPlayerController player)
    {
        CBasePlayerWeapon? activeWeapon = player.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
        if (activeWeapon == null || !activeWeapon.IsValid)
        {
            return false;
        }
        return Schema.GetRef<bool>(activeWeapon.Handle, "CCSWeaponBase", "m_bInReload");
    }

    private static void SetBotYaw(CCSPlayerPawn pawn, float newYaw)
    {
        if (pawn.EyeAngles != null)
        {
            pawn.EyeAngles.Y = newYaw;
        }
        if (pawn.V_angle != null)
        {
            pawn.V_angle.Y = newYaw;
        }
        if (pawn.Bot != null)
        {
            pawn.Bot.LookYaw = newYaw;
            pawn.Bot.LookAroundStateTimestamp = Server.CurrentTime + 0.35f;
        }
    }

    private void ProcessFlashbangAvoidance()
    {
        if (_rayTrace == null || _scratchEye == null)
        {
            return;
        }
        float now = Server.CurrentTime;

        // Clean up expired or invalid flash entities
        for (int i = _activeFlashEntities.Count - 1; i >= 0; i--)
        {
            CBaseEntity ent = _activeFlashEntities[i];
            if (ent == null || !ent.IsValid || ent.AbsOrigin == null)
            {
                _activeFlashEntities.RemoveAt(i);
            }
        }

        // First: check if any bot needs its SavedYaw restored after a flashbang popped!
        if (_flashDecisions.Count > 0)
        {
            List<(int bot, uint flash)> keysToRemove = new List<(int bot, uint flash)>();
            List<((int bot, uint flash) key, FlashDecision val)> keysToUpdate = new List<((int, uint), FlashDecision)>();

            foreach (var kvp in _flashDecisions)
            {
                FlashDecision dec = kvp.Value;
                if (dec.TurnedAway && !dec.RestoredYaw && now >= dec.DetonateAt + 0.05f)
                {
                    dec.RestoredYaw = true;
                    keysToUpdate.Add((kvp.Key, dec));

                    for (int b = 0; b < _cachedControllers.Count; b++)
                    {
                        CCSPlayerController botCtrl = _cachedControllers[b];
                        if (botCtrl != null && botCtrl.IsValid && (int)botCtrl.Index == kvp.Key.Item1)
                        {
                            CCSPlayerPawn? pawn = botCtrl.PlayerPawn?.Value;
                            if (pawn != null && pawn.IsValid && pawn.LifeState == 0 && (pawn.Bot == null || !pawn.Bot.IsEnemyVisible))
                            {
                                SetBotYaw(pawn, dec.SavedYaw);
                            }
                            break;
                        }
                    }
                }
                if (now - dec.DetonateAt > 1.5f)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            for (int u = 0; u < keysToUpdate.Count; u++)
            {
                _flashDecisions[keysToUpdate[u].key] = keysToUpdate[u].val;
            }
            for (int r = 0; r < keysToRemove.Count; r++)
            {
                _flashDecisions.Remove(keysToRemove[r]);
            }
        }

        if (_activeFlashEntities.Count == 0)
        {
            return;
        }

        for (int b = 0; b < _cachedControllers.Count; b++)
        {
            CCSPlayerController botCtrl = _cachedControllers[b];
            if (botCtrl == null || !botCtrl.IsValid || !botCtrl.IsBot || botCtrl.HasBeenControlledByPlayerThisRound)
            {
                continue;
            }
            CCSPlayerPawn? pawn = botCtrl.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.LifeState != 0)
            {
                continue;
            }
            // Don't twist head backwards while sprinting at full speed; only dodge if stationary or slow-moving
            float speed2D = MathF.Sqrt(pawn.AbsVelocity.X * pawn.AbsVelocity.X + pawn.AbsVelocity.Y * pawn.AbsVelocity.Y);
            if (speed2D > 135f)
            {
                continue;
            }

            int botIdx = (int)botCtrl.Index;
            if (!_flashRolledByBot.TryGetValue(botIdx, out HashSet<uint>? rolledSet))
            {
                rolledSet = new HashSet<uint>();
                _flashRolledByBot[botIdx] = rolledSet;
            }

            for (int f = 0; f < _activeFlashEntities.Count; f++)
            {
                CBaseEntity flashEnt = _activeFlashEntities[f];
                uint flashIdx = flashEnt.Index;
                Vector? flashOrigin = flashEnt.AbsOrigin;
                if (flashOrigin == null)
                {
                    continue;
                }
                if (!_flashThrownAt.TryGetValue(flashIdx, out float thrownAt))
                {
                    thrownAt = now;
                    _flashThrownAt[flashIdx] = thrownAt;
                }
                float detonateAt = thrownAt + FlashFuseSeconds;
                if (now > detonateAt)
                {
                    continue;
                }

                (int, uint) key = (botIdx, flashIdx);
                if (rolledSet.Contains(flashIdx))
                {
                    if (!_flashDecisions.TryGetValue(key, out FlashDecision dec))
                    {
                        continue;
                    }
                    dec.LastSeen = now;
                    if (dec.Avoided && !dec.TurnedAway && detonateAt - now <= 0.24f)
                    {
                        dec.TurnedAway = true;
                        dec.SavedYaw = pawn.EyeAngles.Y;
                        float dodgeYaw = (float)NormalizeAngleDeg(dec.SavedYaw + 150.0);
                        SetBotYaw(pawn, dodgeYaw);
                    }
                    _flashDecisions[key] = dec;
                }
                else
                {
                    if (!IsInFov(pawn, flashOrigin, FlashFovHorizDeg, FlashFovVertDeg, out _, out _) || !BotCanSee(pawn, flashOrigin))
                    {
                        continue;
                    }
                    rolledSet.Add(flashIdx);
                    float msLeft = (detonateAt - now) * 1000f;
                    double dodgeProb = (msLeft <= 160f) ? 0.15 : ((msLeft <= 260f) ? 0.45 : ((msLeft <= 420f) ? 0.72 : 0.88));
                    if (pawn.Bot != null && pawn.Bot.IsEnemyVisible)
                    {
                        dodgeProb *= 0.25;
                    }
                    bool willAvoid = _random.NextDouble() <= dodgeProb;
                    bool turnNow = willAvoid && (detonateAt - now <= 0.24f);
                    float savedYaw = pawn.EyeAngles.Y;
                    if (turnNow)
                    {
                        float dodgeYaw = (float)NormalizeAngleDeg(savedYaw + 150.0);
                        SetBotYaw(pawn, dodgeYaw);
                    }
                    _flashDecisions[key] = new FlashDecision
                    {
                        FirstSeen = now,
                        LastSeen = now,
                        DetonateAt = detonateAt,
                        Avoided = willAvoid,
                        TurnedAway = turnNow,
                        RestoredYaw = false,
                        SavedYaw = savedYaw
                    };
                }
            }
        }
    }

    private static bool IsInFov(CCSPlayerPawn pawn, Vector target, float horizDeg, float vertDeg, out float outDeltaYaw, out float outDeltaPitch)
    {
        outDeltaYaw = 0f;
        outDeltaPitch = 0f;
        Vector? absOrigin = pawn.AbsOrigin;
        if (absOrigin == null)
        {
            return false;
        }
        float eyeZ = absOrigin.Z + pawn.ViewOffset.Z;
        double dx = target.X - absOrigin.X;
        double dy = target.Y - absOrigin.Y;
        double dz = target.Z - eyeZ;
        double dist2D = Math.Sqrt(dx * dx + dy * dy);
        if (dist2D < 0.001 && Math.Abs(dz) < 0.001)
        {
            return true;
        }
        double yawToTarget = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        double pitchToTarget = (0.0 - Math.Atan2(dz, dist2D)) * 180.0 / Math.PI;
        double deltaYaw = NormalizeAngleDeg(yawToTarget - pawn.EyeAngles.Y);
        double deltaPitch = NormalizeAngleDeg(pitchToTarget - pawn.EyeAngles.X);
        outDeltaYaw = (float)deltaYaw;
        outDeltaPitch = (float)deltaPitch;
        return Math.Abs(deltaYaw) <= horizDeg * 0.5 && Math.Abs(deltaPitch) <= vertDeg * 0.5;
    }

    private static double NormalizeAngleDeg(double a)
    {
        a %= 360.0;
        if (a > 180.0) a -= 360.0;
        if (a < -180.0) a += 360.0;
        return a;
    }

    private bool BotCanSee(CCSPlayerPawn pawn, Vector target)
    {
        if (_rayTrace == null || _scratchEye == null || pawn.AbsOrigin == null)
        {
            return false;
        }
        _scratchEye.X = pawn.AbsOrigin.X;
        _scratchEye.Y = pawn.AbsOrigin.Y;
        _scratchEye.Z = pawn.AbsOrigin.Z + pawn.ViewOffset.Z;
        RayTraceAPI.TraceOptions opts = new RayTraceAPI.TraceOptions((InteractionLayers)12289, (InteractionLayers)0, false);
        RayTraceAPI.TraceResult res = default;
        _rayTrace.TraceEndShape(_scratchEye, target, pawn, opts, out res);
        return res.Fraction >= 0.999f;
    }
}
