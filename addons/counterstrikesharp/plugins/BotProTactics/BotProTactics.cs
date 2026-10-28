using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using BotControllerApi;
using RayTraceAPI;

namespace BotProTactics
{
    // =========================================================================
    // 1. HIERARCHICAL FSM + UTILITY AI DEFINITIONS
    // =========================================================================

    public enum TacticalState : byte
    {
        PatrolAndPreAim = 0,   // Default navigation with 3D slope & corner-slicing pre-aim
        TacticalHold = 1,      // "No Shiu" passive angle anchor on long sightline
        AngleSlicePeek = 2,    // Contextual peek (ShoulderJiggle, FerrariPrefire, SilentShift)
        CombatDuel = 3,        // Active LOS engagement (ADAD burst, subtick stop, spray control)
        TradeFrag = 4,         // Aggressive refrag swing after nearby teammate death
        TacticalRetreat = 5,   // Survival fallback to cover (low HP / empty primary clip)
        ObjectiveControl = 6   // Post-plant crossfire anchor (T) or Retake/Fake-Defuse (CT)
    }

    public enum WeaponClass : byte
    {
        KnifeOrUtility = 0,
        Pistol = 1,
        SmgOrShotgun = 2,
        Rifle = 3,
        Sniper = 4
    }

    public enum RoundTempo : byte
    {
        DefaultHold = 0,  // 50% chance: Slow default, holding angles for picks ("No Shiu")
        FastRush = 1,     // 25% chance: Coordinated fast entry
        FakeSplit = 2     // 25% chance: Lurkers hold quietly on flanks while pack pressures
    }

    public enum PeekStyle : byte
    {
        None = 0,
        SilentShift = 1,     // Slow silent slice on Shift (Clutch / Lurk / DefaultHold)
        FerrariPrefire = 2,  // Wide swing + subtick counter-strafe + pre-aim anchor
        ShoulderJiggle = 3   // 75ms micro-peek to bait sniper shots
    }

    public enum ThreatType : byte
    {
        Footsteps = 0,
        Gunfire = 1,
        TeammateDeath = 2,
        VisualSight = 3,
        SniperThreat = 4,
        TeamCallout = 5
    }

    public struct BotThreatMemory
    {
        public bool Active;
        public float X;
        public float Y;
        public float Z;
        public float ExpireTime;
        public ThreatType Type;
        public float Priority;
        public bool IsSniper;

        public void Set(Vector pos, float expireTime, ThreatType type, float priority, bool isSniper = false)
        {
            Active = true;
            X = pos.X;
            Y = pos.Y;
            Z = pos.Z;
            ExpireTime = expireTime;
            Type = type;
            Priority = priority;
            IsSniper = isSniper;
        }
    }

    public struct UtilityScores
    {
        public float Patrol;
        public float Hold;
        public float Peek;
        public float Combat;
        public float Trade;
        public float Retreat;
        public float Objective;

        public TacticalState PickHighest(out float bestScore)
        {
            TacticalState best = TacticalState.PatrolAndPreAim;
            bestScore = Patrol;

            if (Hold > bestScore) { bestScore = Hold; best = TacticalState.TacticalHold; }
            if (Peek > bestScore) { bestScore = Peek; best = TacticalState.AngleSlicePeek; }
            if (Objective > bestScore) { bestScore = Objective; best = TacticalState.ObjectiveControl; }
            if (Trade > bestScore) { bestScore = Trade; best = TacticalState.TradeFrag; }
            if (Retreat > bestScore) { bestScore = Retreat; best = TacticalState.TacticalRetreat; }
            if (Combat > bestScore) { bestScore = Combat; best = TacticalState.CombatDuel; }

            return best;
        }
    }

    public sealed class BotAgentContext
    {
        public int Slot;
        public TacticalState CurrentState = TacticalState.PatrolAndPreAim;
        public UtilityScores LastScores;
        public float StateEnteredAt;

        // Combat & Subtick Strafe state
        public long LastCombatMs;
        public long LastShotFiredMs;
        public int StrafeDir = 1;       // 1 = Right, -1 = Left
        public int CombatPhase = 0;     // 0 = Strafe, 1 = CounterStrafe, 2 = AccurateBurstWindow
        public long PhaseStartMs;
        public bool DidCrouchTap;
        public float TargetDistance = 600f;
        public int SprayTicks;

        // Peek state
        public PeekStyle ActivePeek = PeekStyle.None;
        public int PeekStep;
        public long PeekStepStartMs;
        public int PeekDir = 1;
        public float PeekTargetX;
        public float PeekTargetY;
        public float PeekTargetZ;

        // Tactical timers & memory
        public BotThreatMemory Threat;
        public long LastDamageMs;
        public long RefragUntilMs;
        public long RetreatUntilMs;
        public long DoublePeekCooldownMs;
        public float HoldUntilSec;
        public bool IsAnchoredOnSightline;
        public float AnchoredYaw;
        public bool FakeDefuseDone;

        // RayTrace cached clearance & corridor pre-aim
        public bool CanStrafeLeft = true;
        public bool CanStrafeRight = true;
        public bool CanMoveBack = true;
        public float CorridorPreAimYaw;
        public bool HasCorridorPreAim;

        // Weapon & economy snapshot
        public WeaponClass EquippedClass = WeaponClass.Rifle;
        public int PrimaryDefIndex = -1;
        public int SecondaryDefIndex = -1;
        public int Clip1 = 30;
        public int Money = 800;

        public void ResetForRound(int slot)
        {
            Slot = slot;
            CurrentState = TacticalState.PatrolAndPreAim;
            LastScores = default;
            StateEnteredAt = 0f;
            LastCombatMs = 0;
            LastShotFiredMs = 0;
            StrafeDir = (Random.Shared.Next(0, 2) == 0) ? 1 : -1;
            CombatPhase = 0;
            PhaseStartMs = 0;
            DidCrouchTap = false;
            TargetDistance = 600f;
            SprayTicks = 0;
            ActivePeek = PeekStyle.None;
            PeekStep = 0;
            PeekStepStartMs = 0;
            Threat = default;
            LastDamageMs = 0;
            RefragUntilMs = 0;
            RetreatUntilMs = 0;
            DoublePeekCooldownMs = 0;
            HoldUntilSec = 0f;
            IsAnchoredOnSightline = false;
            FakeDefuseDone = false;
            CanStrafeLeft = true;
            CanStrafeRight = true;
            CanMoveBack = true;
            HasCorridorPreAim = false;
            PrimaryDefIndex = -1;
            SecondaryDefIndex = -1;
        }
    }

    // =========================================================================
    // 2. MAIN PLUGIN: BOTPROTATICS v3.1.0 (UTILITY AI + RAYTRACE SUBTICK ENGINE)
    // =========================================================================

    public class BotProTacticsPlugin : BasePlugin
    {
        public override string ModuleName => "BotProTactics";
        public override string ModuleVersion => "3.2.0";
        public override string ModuleAuthor => "Antigravity & ed0ard";
        public override string ModuleDescription => "Tier 1 Utility AI + HFSM, Team Radar Callouts, Anti-Backwards-Run Navigation & Biomechanical Aim";

        // Source 2 Usercmd Button Constants
        public const ulong IN_ATTACK = 1UL;
        public const ulong IN_JUMP = 2UL;
        public const ulong IN_DUCK = 4UL;
        public const ulong IN_FORWARD = 8UL;
        public const ulong IN_BACK = 16UL;
        public const ulong IN_USE = 32UL;
        public const ulong IN_MOVELEFT = 512UL;    // 0x200
        public const ulong IN_MOVERIGHT = 1024UL;  // 0x400
        public const ulong IN_SPEED = 65536UL;     // 0x10000 (Shift-walk)

        private const int MAX_SLOTS = 65;

        private PluginCapability<IBotControllerApi>? _controllerCap;
        private IBotControllerApi? _controllerApi;

        private static readonly PluginCapability<CRayTraceInterface> _rayTraceCap = new("raytrace:craytraceinterface");
        private CRayTraceInterface? _rayTrace;

        // Pre-allocated zero-GC agent contexts indexed by slot [0..64]
        private readonly BotAgentContext[] _agents = new BotAgentContext[MAX_SLOTS];
        private readonly CCSPlayerController?[] _livingPlayersBuffer = new CCSPlayerController?[MAX_SLOTS];
        private readonly CCSPlayerController?[] _livingBotsBuffer = new CCSPlayerController?[MAX_SLOTS];

        // Reusable scratch vectors for RayTrace (lazily initialized after native engine ready)
        private Vector? _traceStart;
        private Vector? _traceEnd;

        public RoundTempo CurrentTempo = RoundTempo.DefaultHold;
        private string _forcedTempoMode = "auto";
        private bool _isBombPlanted = false;
        private float _bombPlantTime = 0f;
        private bool _hasPlantedC4Pos = false;
        private float _plantedC4X;
        private float _plantedC4Y;
        private float _plantedC4Z;
        private int _tickCount = 0;

        // Feature toggles
        public bool EnableUtilityAiBrain = true;
        public bool EnableAntiRushTempos = true;
        public bool EnableSubtickStop = true;
        public bool EnableHumanAimSmoothing = true;
        public bool EnableSprayCompensation = true;
        public bool EnableAdadStrafe = true;
        public bool EnableJumpSuppressionInCombat = true;
        public bool EnableTacticalThreatFacing = true;
        public bool EnableCornerSlicingPreAim = true;
        public bool EnableContextualPeeks = true;
        public bool EnableDistanceCombatDiscipline = true;
        public bool EnableTacticalRetreat = true;
        public bool EnableCoordinatedDoublePeeks = true;
        public bool EnableShiftWalk = true;
        public bool EnableFakeDefuse = true;
        public bool EnableTradeFrags = true;
        public bool EnableTeamCallouts = true;

        public override void Load(bool hotReload)
        {
            for (int i = 0; i < MAX_SLOTS; i++)
            {
                _agents[i] = new BotAgentContext { Slot = i };
            }

            _controllerCap = new PluginCapability<IBotControllerApi>("botcontroller:api");

            RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
            RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
            RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);
            RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
            RegisterEventHandler<EventRoundStart>(OnRoundStart);

            RegisterListener<Listeners.OnTick>(OnTick);

            AddCommand("css_bot_brain", "Inspect real-time Utility AI scores and states of all bots", OnBotBrainCommand);
            AddCommand("css_bot_tempo", "Set round tempo mode: auto, default, rush, split", OnBotTempoCommand);

            Logger.LogInformation("[BotProTactics] v3.2.0 Loaded! Utility AI, Subtick Biomechanics, C4 Retake, Forward Cone Guard active.");
        }

        public override void OnAllPluginsLoaded(bool hotReload)
        {
            GetApi();
            GetRayTrace();
        }

        private IBotControllerApi? GetApi()
        {
            if (_controllerApi != null)
                return _controllerApi;
            try
            {
                if (_controllerCap != null)
                    _controllerApi = _controllerCap.Get();
            }
            catch
            {
                _controllerApi = null;
            }
            return _controllerApi;
        }

        private CRayTraceInterface? GetRayTrace()
        {
            if (_rayTrace != null)
                return _rayTrace;
            try
            {
                _rayTrace = _rayTraceCap.Get();
            }
            catch
            {
                _rayTrace = null;
            }
            return _rayTrace;
        }

        // =========================================================================
        // 3. CONSOLE TELEMETRY & DIAGNOSTIC COMMANDS
        // =========================================================================

        private void OnBotBrainCommand(CCSPlayerController? caller, CommandInfo info)
        {
            Console.WriteLine($"=== [BotProTactics v3.1 Utility AI Telemetry] Tempo={CurrentTempo} BombPlanted={_isBombPlanted} ===");
            foreach (var p in Utilities.GetPlayers())
            {
                if (p == null || !p.IsValid || !p.IsBot || !p.PawnIsAlive) continue;
                int s = p.Slot;
                if (s < 0 || s >= MAX_SLOTS) continue;
                var ctx = _agents[s];
                var sc = ctx.LastScores;
                Console.WriteLine(
                    $"  Bot[{s}] {p.PlayerName,-14} | State={ctx.CurrentState,-16} | Wep={ctx.EquippedClass,-12} | " +
                    $"U(Cmb={sc.Combat:F2} Trd={sc.Trade:F2} Ret={sc.Retreat:F2} Obj={sc.Objective:F2} Pek={sc.Peek:F2} Hld={sc.Hold:F2} Nav={sc.Patrol:F2})");
            }
        }

        private void OnBotTempoCommand(CCSPlayerController? caller, CommandInfo info)
        {
            if (info.ArgCount > 1)
            {
                string mode = info.GetArg(1).Trim().ToLowerInvariant();
                if (mode == "auto" || mode == "default" || mode == "rush" || mode == "split")
                {
                    _forcedTempoMode = mode;
                    if (mode == "default") CurrentTempo = RoundTempo.DefaultHold;
                    else if (mode == "rush") CurrentTempo = RoundTempo.FastRush;
                    else if (mode == "split") CurrentTempo = RoundTempo.FakeSplit;
                }
            }
            Console.WriteLine($"[BotProTactics] Tempo mode = {_forcedTempoMode} (Current active: {CurrentTempo})");
        }

        // =========================================================================
        // 4. GAME EVENT HANDLERS (SENSORY PERCEPTION, CALLOUTS & MEMORY)
        // =========================================================================

        private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
        {
            for (int i = 0; i < MAX_SLOTS; i++)
            {
                _agents[i].ResetForRound(i);
            }

            _isBombPlanted = false;
            _bombPlantTime = 0f;
            _hasPlantedC4Pos = false;

            if (_forcedTempoMode == "default")
            {
                CurrentTempo = RoundTempo.DefaultHold;
            }
            else if (_forcedTempoMode == "rush")
            {
                CurrentTempo = RoundTempo.FastRush;
            }
            else if (_forcedTempoMode == "split")
            {
                CurrentTempo = RoundTempo.FakeSplit;
            }
            else
            {
                int roll = Random.Shared.Next(0, 100);
                if (roll < 50) CurrentTempo = RoundTempo.DefaultHold;
                else if (roll < 75) CurrentTempo = RoundTempo.FastRush;
                else CurrentTempo = RoundTempo.FakeSplit;
            }

            float nowSec = Server.CurrentTime;
            int holdsAssigned = 0;

            if (EnableAntiRushTempos)
            {
                foreach (var player in Utilities.GetPlayers())
                {
                    if (player != null && player.IsValid && player.IsBot && player.Slot >= 0 && player.Slot < MAX_SLOTS)
                    {
                        var ctx = _agents[player.Slot];
                        if (CurrentTempo == RoundTempo.DefaultHold)
                        {
                            int minHold = (player.Team == CsTeam.CounterTerrorist) ? 22 : 16;
                            int maxHold = (player.Team == CsTeam.CounterTerrorist) ? 35 : 28;
                            ctx.HoldUntilSec = nowSec + Random.Shared.Next(minHold, maxHold);
                            holdsAssigned++;
                        }
                        else if (CurrentTempo == RoundTempo.FakeSplit)
                        {
                            if (Random.Shared.Next(0, 2) == 0)
                            {
                                ctx.HoldUntilSec = nowSec + Random.Shared.Next(18, 32);
                                holdsAssigned++;
                            }
                        }
                    }
                }
            }

            Logger.LogInformation("[BotProTactics] Round Start | Tempo={Tempo} | Tactical Holds={Holds}", CurrentTempo, holdsAssigned);
            return HookResult.Continue;
        }

        private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
        {
            _isBombPlanted = true;
            _bombPlantTime = Server.CurrentTime;

            for (int i = 0; i < MAX_SLOTS; i++)
            {
                _agents[i].HoldUntilSec = 0f;
                _agents[i].IsAnchoredOnSightline = false;
            }

            var c4Entities = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4");
            foreach (var c4 in c4Entities)
            {
                if (c4 != null && c4.IsValid && c4.AbsOrigin != null)
                {
                    _plantedC4X = c4.AbsOrigin.X;
                    _plantedC4Y = c4.AbsOrigin.Y;
                    _plantedC4Z = c4.AbsOrigin.Z;
                    _hasPlantedC4Pos = true;
                    break;
                }
            }

            return HookResult.Continue;
        }

        private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
        {
            var shooter = @event.Userid;
            if (shooter == null || !shooter.IsValid || shooter.PlayerPawn == null || !shooter.PlayerPawn.IsValid)
                return HookResult.Continue;

            var shooterPawn = shooter.PlayerPawn.Value;
            if (shooterPawn == null || !shooterPawn.IsValid || shooterPawn.AbsOrigin == null)
                return HookResult.Continue;

            Vector shooterPos = shooterPawn.AbsOrigin;
            int slot = shooter.Slot;
            if (slot < 0 || slot >= MAX_SLOTS)
                return HookResult.Continue;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float nowSec = Server.CurrentTime;

            var shooterCtx = _agents[slot];
            shooterCtx.HoldUntilSec = 0f;
            shooterCtx.IsAnchoredOnSightline = false;

            string weaponName = (@event.Weapon ?? "").ToLowerInvariant();
            bool isSniper = weaponName.Contains("awp") || weaponName.Contains("ssg08") || weaponName.Contains("scar20") || weaponName.Contains("g3sg1");
            bool isSilenced = weaponName.Contains("silencer");
            bool isKnifeOrNade = weaponName.Contains("knife") || weaponName.Contains("bayonet") || weaponName.Contains("grenade") || weaponName.Contains("flashbang") || weaponName.Contains("molotov") || weaponName.Contains("decoy");

            if (shooter.IsBot && !isKnifeOrNade)
            {
                if (nowMs - shooterCtx.LastShotFiredMs > 260)
                {
                    shooterCtx.SprayTicks = 1;
                }
                else
                {
                    shooterCtx.SprayTicks++;
                }

                shooterCtx.LastShotFiredMs = nowMs;
                shooterCtx.LastCombatMs = nowMs;
                if (shooterCtx.PhaseStartMs == 0)
                {
                    shooterCtx.PhaseStartMs = nowMs;
                }
            }

            if (isKnifeOrNade)
                return HookResult.Continue;

            float hearRange = isSilenced ? 750.0f : (isSniper ? 1650.0f : 1250.0f);

            if (EnableTacticalThreatFacing)
            {
                CsTeam shooterTeam = shooter.Team;
                int enemyAliveCount = 0;
                foreach (var p in Utilities.GetPlayers())
                {
                    if (p != null && p.IsValid && p.PawnIsAlive && p.Team == shooterTeam)
                        enemyAliveCount++;
                }

                foreach (var player in Utilities.GetPlayers())
                {
                    if (player == null || !player.IsValid || !player.IsBot || !player.PawnIsAlive || player.Team == shooterTeam || player.Slot == slot)
                        continue;

                    int botSlot = player.Slot;
                    if (botSlot < 0 || botSlot >= MAX_SLOTS) continue;

                    var botPawn = player.PlayerPawn?.Value;
                    if (botPawn == null || !botPawn.IsValid || botPawn.AbsOrigin == null)
                        continue;

                    float dist = Dist3D(shooterPos, botPawn.AbsOrigin);
                    // Only react to gunfire within the same site/sector (<= 900u), unless hunting the last surviving enemy (enemyAliveCount == 1)
                    float effectiveRange = (enemyAliveCount == 1) ? hearRange : MathF.Min(hearRange, 900.0f);

                    if (dist <= effectiveRange)
                    {
                        var botCtx = _agents[botSlot];
                        float priority = isSniper ? 1.35f : 1.05f;

                        if (!botCtx.Threat.Active || botCtx.Threat.Priority <= priority || nowSec > botCtx.Threat.ExpireTime)
                        {
                            botCtx.Threat.Set(shooterPos, nowSec + 1.35f, isSniper ? ThreatType.SniperThreat : ThreatType.Gunfire, priority, isSniper);
                        }

                        if (dist <= 850.0f || enemyAliveCount == 1)
                        {
                            botCtx.HoldUntilSec = 0f;
                            botCtx.IsAnchoredOnSightline = false;
                        }

                        // Bait sniper shot with a wall-safe shoulder jiggle only when in the same sector (< 950u)
                        if (EnableContextualPeeks && isSniper && dist > 350.0f && dist < 950.0f && botCtx.ActivePeek == PeekStyle.None)
                        {
                            int dir = PickBestStrafeDirection(botCtx);
                            StartPeek(botCtx, PeekStyle.ShoulderJiggle, dir, shooterPos.X, shooterPos.Y, shooterPos.Z, nowMs);
                        }
                    }
                }
            }

            return HookResult.Continue;
        }

        private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
        {
            var victim = @event.Userid;
            var attacker = @event.Attacker;
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float nowSec = Server.CurrentTime;

            if (victim != null && victim.IsValid && victim.IsBot && victim.Slot >= 0 && victim.Slot < MAX_SLOTS)
            {
                int victimSlot = victim.Slot;
                var ctx = _agents[victimSlot];
                ctx.LastDamageMs = nowMs;
                ctx.LastCombatMs = nowMs;
                ctx.HoldUntilSec = 0f;
                ctx.IsAnchoredOnSightline = false;

                if (ctx.PhaseStartMs == 0)
                    ctx.PhaseStartMs = nowMs;

                var vPawn = victim.PlayerPawn?.Value;
                int hp = vPawn?.Health ?? 100;

                if (EnableTacticalRetreat && hp <= 28)
                {
                    ctx.RetreatUntilMs = nowMs + 1900;
                }

                if (EnableTacticalThreatFacing && attacker != null && attacker.IsValid && attacker.Team != victim.Team)
                {
                    var attackerPawn = attacker.PlayerPawn?.Value;
                    if (attackerPawn != null && attackerPawn.IsValid && attackerPawn.AbsOrigin != null)
                    {
                        ctx.Threat.Set(attackerPawn.AbsOrigin, nowSec + 1.85f, ThreatType.Gunfire, 1.35f);
                    }
                }
            }

            // Team Callout Broadcast: Share enemy contact with same-site partners (<= 900u),
            // or map-wide ONLY when hunting the last surviving enemy (enemyAlive == 1) or heavy execute!
            if (EnableTeamCallouts && victim != null && victim.IsValid && attacker != null && attacker.IsValid && victim.Team != attacker.Team)
            {
                var vPawn = victim.PlayerPawn?.Value;
                var aPawn = attacker.PlayerPawn?.Value;

                if (aPawn?.AbsOrigin != null)
                {
                    BroadcastTeamCallout(victim.Team, aPawn.AbsOrigin, 1.38f, 2.30f, nowSec);
                }
                if (vPawn?.AbsOrigin != null)
                {
                    BroadcastTeamCallout(attacker.Team, vPawn.AbsOrigin, 1.38f, 2.30f, nowSec);
                }
            }

            return HookResult.Continue;
        }

        private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
        {
            var victim = @event.Userid;
            var attacker = @event.Attacker;
            float nowSec = Server.CurrentTime;
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (victim != null && victim.IsValid && victim.Slot >= 0 && victim.Slot < MAX_SLOTS)
            {
                _agents[victim.Slot].ResetForRound(victim.Slot);
            }

            if (victim != null && victim.IsValid && attacker != null && attacker.IsValid && victim.PlayerPawn != null && victim.PlayerPawn.IsValid)
            {
                var victimPawn = victim.PlayerPawn.Value;
                var attackerPawn = attacker.PlayerPawn?.Value;
                if (victimPawn == null || !victimPawn.IsValid || victimPawn.AbsOrigin == null)
                    return HookResult.Continue;

                Vector victimPos = victimPawn.AbsOrigin;
                Vector? killerPos = (attackerPawn != null && attackerPawn.IsValid) ? attackerPawn.AbsOrigin : null;
                CsTeam victimTeam = victim.Team;

                // Count remaining defenders near the victim's site (<= 900u) and remaining living enemies
                int alliesLeftNearSite = 0;
                int enemyAliveCount = 0;
                foreach (var p in Utilities.GetPlayers())
                {
                    if (p == null || !p.IsValid || !p.PawnIsAlive || p.Slot == victim.Slot) continue;
                    if (p.Team != victimTeam)
                    {
                        enemyAliveCount++;
                    }
                    else
                    {
                        var ap = p.PlayerPawn?.Value;
                        if (ap?.AbsOrigin != null && Dist3D(victimPos, ap.AbsOrigin) <= 900.0f)
                        {
                            alliesLeftNearSite++;
                        }
                    }
                }

                foreach (var player in Utilities.GetPlayers())
                {
                    if (player != null && player.IsValid && player.IsBot && player.PawnIsAlive && player.Team == victimTeam && player.Slot != victim.Slot)
                    {
                        int slot = player.Slot;
                        if (slot < 0 || slot >= MAX_SLOTS) continue;

                        var pPawn = player.PlayerPawn?.Value;
                        if (pPawn == null || !pPawn.IsValid || pPawn.AbsOrigin == null) continue;

                        float dist = Dist3D(victimPos, pPawn.AbsOrigin);
                        var ctx = _agents[slot];

                        // Release TacticalHold ONLY if:
                        // 1) Bot is on the same bombsite/sector (dist <= 950u), OR
                        // 2) Only 1 enemy is left alive (X vs 1 hunt), OR
                        // 3) The bombsite where the teammate died now has 0 defenders left (site lost -> rotate!)
                        if (dist <= 950.0f || enemyAliveCount == 1 || alliesLeftNearSite == 0)
                        {
                            ctx.HoldUntilSec = 0f;
                            ctx.IsAnchoredOnSightline = false;
                        }

                        if (EnableTradeFrags && dist <= 950.0f)
                        {
                            ctx.RefragUntilMs = nowMs + 2200;
                            ctx.LastCombatMs = nowMs;
                            if (ctx.PhaseStartMs == 0) ctx.PhaseStartMs = nowMs;

                            Vector targetSpot = killerPos ?? victimPos;
                            ctx.Threat.Set(targetSpot, nowSec + 2.00f, ThreatType.TeammateDeath, 1.42f);

                            if (EnableContextualPeeks && killerPos != null && dist < 750.0f && ctx.ActivePeek == PeekStyle.None)
                            {
                                int dir = PickBestStrafeDirection(ctx);
                                StartPeek(ctx, PeekStyle.FerrariPrefire, dir, killerPos.X, killerPos.Y, killerPos.Z, nowMs);
                            }
                        }
                    }
                }
            }

            return HookResult.Continue;
        }

        private HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
        {
            if (!EnableFakeDefuse) return HookResult.Continue;

            var defuser = @event.Userid;
            if (defuser == null || !defuser.IsValid || !defuser.IsBot)
                return HookResult.Continue;

            int slot = defuser.Slot;
            if (slot < 0 || slot >= MAX_SLOTS)
                return HookResult.Continue;

            var ctx = _agents[slot];
            if (ctx.FakeDefuseDone)
                return HookResult.Continue;

            int livingCTs = 0;
            int livingTs = 0;
            foreach (var player in Utilities.GetPlayers())
            {
                if (player != null && player.IsValid && player.PawnIsAlive)
                {
                    if (player.Team == CsTeam.CounterTerrorist) livingCTs++;
                    else if (player.Team == CsTeam.Terrorist) livingTs++;
                }
            }

            // In 1v1 or 1v2 clutch with enough C4 timer left, execute a realistic Fake Defuse to bait T peek!
            if (livingCTs == 1 && livingTs >= 1 && livingTs <= 2)
            {
                float c4Elapsed = Server.CurrentTime - _bombPlantTime;
                if (c4Elapsed < 28.0f)
                {
                    ctx.FakeDefuseDone = true;
                    var api = GetApi();
                    if (api != null)
                    {
                        AddTimer(0.24f, () =>
                        {
                            try
                            {
                                if (defuser.IsValid && defuser.PawnIsAlive)
                                {
                                    api.SwitchBotWeapon(slot, 9001); // Tap knife (Slot 3) briefly to cancel defuse
                                    AddTimer(0.10f, () =>
                                    {
                                        try
                                        {
                                            if (defuser.IsValid && defuser.PawnIsAlive)
                                            {
                                                if (ctx.PrimaryDefIndex > 0 && api.SwitchBotWeapon(slot, ctx.PrimaryDefIndex))
                                                {
                                                    return;
                                                }
                                                if (ctx.SecondaryDefIndex > 0)
                                                    api.SwitchBotWeapon(slot, ctx.SecondaryDefIndex);
                                            }
                                        }
                                        catch { }
                                    });
                                }
                            }
                            catch { }
                        });
                    }
                }
            }

            return HookResult.Continue;
        }

        // =========================================================================
        // 5. MAIN 64Hz TICK LOOP (ZERO-GC, STAGGERED RAYTRACE & UTILITY BRAIN)
        // =========================================================================

        private void OnTick()
        {
            try
            {
                if (string.IsNullOrEmpty(Server.MapName)) return;
                OnTickCore();
            }
            catch { }
        }

        private void OnTickCore()
        {
            var api = GetApi();
            var rayTrace = GetRayTrace();
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float nowSec = Server.CurrentTime;
            const float dt = 1.0f / 64.0f;
            _tickCount++;

            int ctAlive = 0;
            int tAlive = 0;
            int livingPlayersCount = 0;
            int livingBotsCount = 0;

            foreach (var p in Utilities.GetPlayers())
            {
                if (p != null && p.IsValid && p.PawnIsAlive && p.Slot >= 0 && p.Slot < MAX_SLOTS)
                {
                    if (p.Team == CsTeam.CounterTerrorist) ctAlive++;
                    else if (p.Team == CsTeam.Terrorist) tAlive++;

                    if (livingPlayersCount < MAX_SLOTS)
                        _livingPlayersBuffer[livingPlayersCount++] = p;
                    if (p.IsBot && livingBotsCount < MAX_SLOTS)
                        _livingBotsBuffer[livingBotsCount++] = p;
                }
            }

            // Refresh planted C4 position every 32 ticks if needed
            if (_isBombPlanted && !_hasPlantedC4Pos && (_tickCount % 32 == 0))
            {
                var c4Entities = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4");
                foreach (var c4 in c4Entities)
                {
                    if (c4 != null && c4.IsValid && c4.BombTicking && c4.AbsOrigin != null)
                    {
                        _plantedC4X = c4.AbsOrigin.X;
                        _plantedC4Y = c4.AbsOrigin.Y;
                        _plantedC4Z = c4.AbsOrigin.Z;
                        _hasPlantedC4Pos = true;
                        break;
                    }
                }
            }

            // 1. Coordinated Double-Peek Spacing (every 12 ticks)
            if (EnableCoordinatedDoublePeeks && livingBotsCount >= 2 && (_tickCount % 12 == 0))
            {
                ProcessDoublePeeks(livingBotsCount, nowMs, nowSec);
            }

            // 2. Footstep Sensory Awareness & Team Radar Callout Sharing (every 6 ticks)
            if (EnableTacticalThreatFacing && (_tickCount % 6 == 0))
            {
                ProcessFootstepSensors(livingPlayersCount, livingBotsCount, nowSec);
                if (EnableTeamCallouts)
                {
                    ProcessTeamRadarAndCallouts(rayTrace, livingPlayersCount, ctAlive, tAlive, nowSec);
                }
            }

            // 3. Per-Bot Hybrid Utility AI + Subtick Biomechanical Execution
            for (int bIdx = 0; bIdx < livingBotsCount; bIdx++)
            {
                var bot = _livingBotsBuffer[bIdx];
                if (bot == null || !bot.IsValid) continue;

                int slot = bot.Slot;
                if (slot < 0 || slot >= MAX_SLOTS) continue;

                var pawn = bot.PlayerPawn?.Value;
                if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null) continue;

                var ctx = _agents[slot];
                Vector botOrigin = pawn.AbsOrigin;
                CCSBot? ccsBot = pawn.Bot;
                int hp = pawn.Health;
                bool isFlashed = (pawn.FlashDuration > 0.40f);

                // Decay spray ticks smoothly when bot stops firing
                if (ctx.SprayTicks > 0 && nowMs - ctx.LastShotFiredMs > 240)
                {
                    ctx.SprayTicks = 0;
                }

                // Update weapon & ammo telemetry
                UpdateWeaponTelemetry(pawn, bot, ctx);

                // Find closest visible enemy and closest overall enemy
                float closestEnemyDist = 9999f;
                float closestVisibleEnemyDist = 9999f;
                int nearbyEnemies = 0;
                int nearbyAllies = 0;

                for (int pIdx = 0; pIdx < livingPlayersCount; pIdx++)
                {
                    var other = _livingPlayersBuffer[pIdx];
                    if (other == null || other.Slot == slot) continue;
                    var oPawn = other.PlayerPawn?.Value;
                    if (oPawn == null || !oPawn.IsValid || oPawn.AbsOrigin == null) continue;

                    float d = Dist3D(botOrigin, oPawn.AbsOrigin);
                    if (other.Team == bot.Team)
                    {
                        if (d < 850f) nearbyAllies++;
                    }
                    else
                    {
                        if (d < 950f) nearbyEnemies++;
                        if (d < closestEnemyDist)
                        {
                            closestEnemyDist = d;
                        }
                    }
                }

                bool hasDirectEnemyInSight = (ccsBot != null && ccsBot.IsEnemyVisible);
                if (hasDirectEnemyInSight && ccsBot?.LastEnemyPosition != null)
                {
                    closestVisibleEnemyDist = Dist3D(botOrigin, ccsBot.LastEnemyPosition);
                    ctx.TargetDistance = closestVisibleEnemyDist;
                }
                else
                {
                    ctx.TargetDistance = closestEnemyDist;
                }

                // Staggered RayTrace Environmental Probes (every 6 ticks per bot -> ~10.6 Hz per bot)
                if (rayTrace != null && ((_tickCount + slot) % 6 == 0))
                {
                    UpdateEnvironmentalRayTraces(rayTrace, pawn, botOrigin, ccsBot, ctx, nowSec);
                }

                // Staggered Utility AI Brain Evaluation (every 4 ticks per bot -> 16 Hz decision rate)
                int teamAlive = (bot.Team == CsTeam.CounterTerrorist) ? ctAlive : tAlive;
                int enemyAlive = (bot.Team == CsTeam.CounterTerrorist) ? tAlive : ctAlive;

                if (EnableUtilityAiBrain && ((_tickCount + slot) % 4 == 0))
                {
                    EvaluateUtilityBrain(bot, pawn, ccsBot, ctx, hp, teamAlive, enemyAlive, nearbyAllies, nearbyEnemies, hasDirectEnemyInSight, nowSec, nowMs);
                }

                // --- EXECUTE ACTIVE STATE & BIOMECHANICS ---

                // A. Suppress Random Jumping in Combat
                bool inCombat = (ctx.CurrentState == TacticalState.CombatDuel ||
                                 ctx.CurrentState == TacticalState.TradeFrag ||
                                 hasDirectEnemyInSight ||
                                 (ccsBot != null && ccsBot.IsAttacking) ||
                                 (nowMs - ctx.LastDamageMs < 2200));

                if (EnableJumpSuppressionInCombat && inCombat)
                {
                    SuppressCombatJump(pawn, ccsBot, nowSec);
                }

                // B. Execute Contextual Peeks (Wall-Safe via RayTrace!)
                if (EnableContextualPeeks && ctx.ActivePeek != PeekStyle.None && api != null)
                {
                    ExecuteContextualPeek(api, ctx, nowMs);
                }

                // C. Execute State-Specific Movement & Weapon Discipline
                if (api != null)
                {
                    ExecuteTacticalStateMovement(api, bot, pawn, ccsBot, ctx, hasDirectEnemyInSight, teamAlive, nowMs, nowSec);
                }

                // D. Execute 3D Biomechanical Aim, Threat Facing, Recoil Control & Corner Slicing
                if (!isFlashed)
                {
                    ExecuteBiomechanicalAim(bot, pawn, ccsBot, ctx, hasDirectEnemyInSight, teamAlive, dt, nowSec, nowMs);
                }
            }
        }

        // =========================================================================
        // 6. UTILITY AI DECISION ENGINE (MATHEMATICAL RESPONSE CURVES)
        // =========================================================================

        private void EvaluateUtilityBrain(
            CCSPlayerController bot,
            CCSPlayerPawn pawn,
            CCSBot? ccsBot,
            BotAgentContext ctx,
            int hp,
            int teamAlive,
            int enemyAlive,
            int nearbyAllies,
            int nearbyEnemies,
            bool hasDirectEnemyInSight,
            float nowSec,
            long nowMs)
        {
            // Man-Advantage Hunt Rule (e.g., 3v1 / 4v1 / 2v1):
            // Never sit in passive spawn TacticalHold when hunting the last surviving enemy!
            if (enemyAlive == 1 && teamAlive >= 2)
            {
                ctx.HoldUntilSec = 0f;
                ctx.IsAnchoredOnSightline = false;
            }

            UtilityScores scores = default;

            // 1. Patrol & Pre-Aim baseline utility
            scores.Patrol = 0.25f;

            // 2. Combat Duel Utility (Logistic response to direct sight, active attack, and recent damage)
            if (hasDirectEnemyInSight || (ccsBot != null && ccsBot.IsAttacking))
            {
                float ammoFactor = (ctx.Clip1 > 0) ? 1.0f : 0.15f;
                scores.Combat = 0.92f * ammoFactor;
                ctx.LastCombatMs = nowMs;
                if (ctx.PhaseStartMs == 0) ctx.PhaseStartMs = nowMs;
            }
            else if (nowMs - ctx.LastCombatMs < 1100)
            {
                float decay = 1.0f - (nowMs - ctx.LastCombatMs) / 1100.0f;
                scores.Combat = 0.68f * decay;
            }

            // 3. Trade Frag Utility (High urgency right after nearby ally dies)
            if (EnableTradeFrags && nowMs < ctx.RefragUntilMs && hp > 25 && ctx.Clip1 > 3)
            {
                float remaining = (ctx.RefragUntilMs - nowMs) / 2200.0f;
                scores.Trade = 0.86f * Math.Clamp(remaining + 0.3f, 0.4f, 1.0f);
            }

            // 4. Tactical Retreat Utility (Exponential survival curve on low HP or empty primary magazine)
            if (EnableTacticalRetreat && ctx.CanMoveBack)
            {
                float hpUrgency = (hp <= 28) ? MathF.Pow(1.0f - (hp / 100.0f), 1.6f) : 0.0f;
                float ammoUrgency = (ctx.Clip1 <= 1 && ctx.EquippedClass >= WeaponClass.SmgOrShotgun) ? 0.90f : 0.0f;
                float outnumberUrgency = (nearbyEnemies >= nearbyAllies + 2 && hp < 55) ? 0.65f : 0.0f;

                float rawRetreat = MathF.Max(hpUrgency, MathF.Max(ammoUrgency, outnumberUrgency));
                if (rawRetreat > 0.50f && (nowMs < ctx.RetreatUntilMs || ctx.Clip1 <= 1))
                {
                    scores.Retreat = Math.Clamp(rawRetreat, 0.0f, 0.95f);
                }
            }

            // 5. Angle Slice & Contextual Peek Utility
            if (EnableContextualPeeks && !hasDirectEnemyInSight)
            {
                if (ctx.ActivePeek != PeekStyle.None)
                {
                    scores.Peek = 0.82f;
                }
                else if (ctx.Threat.Active && nowSec < ctx.Threat.ExpireTime)
                {
                    Vector? origin = pawn.AbsOrigin;
                    if (origin != null)
                    {
                        float dThreat = Dist2D(origin.X, origin.Y, ctx.Threat.X, ctx.Threat.Y);
                        if (dThreat > 260.0f && dThreat < 850.0f && (ctx.CanStrafeLeft || ctx.CanStrafeRight))
                        {
                            scores.Peek = 0.74f;
                        }
                    }
                }
            }

            // 6. Objective Control Utility (Post-Plant T Anchor / CT Retake Stealth)
            if (_isBombPlanted && _hasPlantedC4Pos && !hasDirectEnemyInSight && pawn.AbsOrigin != null)
            {
                float dxC4 = pawn.AbsOrigin.X - _plantedC4X;
                float dyC4 = pawn.AbsOrigin.Y - _plantedC4Y;
                float dzC4 = pawn.AbsOrigin.Z - _plantedC4Z;
                float distToC4 = MathF.Sqrt(dxC4 * dxC4 + dyC4 * dyC4 + dzC4 * dzC4);
                if (distToC4 > 160.0f && distToC4 < 1650.0f)
                {
                    scores.Objective = 0.64f;
                }
            }

            // 7. Tactical Hold ("No Shiu" Passive Angle Anchor)
            if (EnableAntiRushTempos && !_isBombPlanted && !hasDirectEnemyInSight && nowSec < ctx.HoldUntilSec)
            {
                float wepBonus = (ctx.EquippedClass == WeaponClass.Sniper) ? 0.12f : 0.05f;
                scores.Hold = 0.56f + wepBonus;
            }

            ctx.LastScores = scores;
            TacticalState nextState = scores.PickHighest(out _);

            if (nextState != ctx.CurrentState)
            {
                ctx.CurrentState = nextState;
                ctx.StateEnteredAt = nowSec;
            }
        }

        // =========================================================================
        // 7. RAYTRACE ENVIRONMENTAL PROBES (WALL-SAFE STRAFE & PIE SLICING)
        // =========================================================================

        private void UpdateEnvironmentalRayTraces(
            CRayTraceInterface rayTrace,
            CCSPlayerPawn pawn,
            Vector botOrigin,
            CCSBot? ccsBot,
            BotAgentContext ctx,
            float nowSec)
        {
            QAngle? eyeAngles = pawn.EyeAngles;
            if (eyeAngles == null) return;

            float viewZ = pawn.ViewOffset?.Z ?? 64.0f;
            float waistZ = botOrigin.Z + 38.0f;
            float eyeZ = botOrigin.Z + viewZ;

            float yawRad = eyeAngles.Y * (MathF.PI / 180.0f);
            float rightX = MathF.Sin(yawRad);
            float rightY = -MathF.Cos(yawRad);
            float fwdX = MathF.Cos(yawRad);
            float fwdY = MathF.Sin(yawRad);

            // 1. Check Left, Right, and Back movement clearance (58 units) so bot never strafes or retreats into walls!
            const float checkDist = 58.0f;
            ctx.CanStrafeRight = CheckRayClearance(rayTrace, botOrigin.X, botOrigin.Y, waistZ, rightX, rightY, checkDist);
            ctx.CanStrafeLeft = CheckRayClearance(rayTrace, botOrigin.X, botOrigin.Y, waistZ, -rightX, -rightY, checkDist);
            ctx.CanMoveBack = CheckRayClearance(rayTrace, botOrigin.X, botOrigin.Y, waistZ, -fwdX, -fwdY, checkDist + 16.0f);

            // If currently strafing into a blocked wall, flip direction immediately!
            if (ctx.StrafeDir > 0 && !ctx.CanStrafeRight && ctx.CanStrafeLeft)
                ctx.StrafeDir = -1;
            else if (ctx.StrafeDir < 0 && !ctx.CanStrafeLeft && ctx.CanStrafeRight)
                ctx.StrafeDir = 1;

            Vector? vel = pawn.AbsVelocity;
            float speed2D = (vel != null) ? MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y) : 0f;

            // 2. Check forward sightline depth for TacticalHold ("No Shiu" angle anchoring)
            // ONLY anchor when the bot has actually stopped/settled (< 28 u/s), NEVER while sprinting across the map!
            if (ctx.CurrentState == TacticalState.TacticalHold)
            {
                if (!ctx.IsAnchoredOnSightline && speed2D < 28.0f)
                {
                    float fwdDepth = MeasureRayDistance(rayTrace, botOrigin.X, botOrigin.Y, eyeZ, fwdX, fwdY, 950.0f);
                    if (fwdDepth >= 560.0f)
                    {
                        ctx.IsAnchoredOnSightline = true;
                        ctx.AnchoredYaw = eyeAngles.Y;
                    }
                }
                else if (ctx.IsAnchoredOnSightline && speed2D > 38.0f && vel != null)
                {
                    float moveYaw = MathF.Atan2(vel.Y, vel.X) * (180.0f / MathF.PI);
                    if (MathF.Abs(NormalizeAngle(moveYaw - ctx.AnchoredYaw)) > 40.0f)
                    {
                        ctx.IsAnchoredOnSightline = false;
                        ctx.HoldUntilSec = 0f;
                    }
                }
            }
            else
            {
                ctx.IsAnchoredOnSightline = false;
            }

            // 3. 3-Ray Fan Probe for Slicing-the-Pie Pre-Aim during navigation
            if (EnableCornerSlicingPreAim && vel != null)
            {
                if (speed2D > 42.0f)
                {
                    float moveYaw = MathF.Atan2(vel.Y, vel.X) * (180.0f / MathF.PI);
                    float dCenter = MeasureYawDistance(rayTrace, botOrigin.X, botOrigin.Y, eyeZ, moveYaw, 550.0f);

                    // If approaching a wall/corner ahead (< 260u), probe left (+28 deg) and right (-28 deg) to pre-aim the open corner!
                    if (dCenter < 260.0f)
                    {
                        float yawLeft = NormalizeAngle(moveYaw + 28.0f);
                        float yawRight = NormalizeAngle(moveYaw - 28.0f);
                        float dLeft = MeasureYawDistance(rayTrace, botOrigin.X, botOrigin.Y, eyeZ, yawLeft, 600.0f);
                        float dRight = MeasureYawDistance(rayTrace, botOrigin.X, botOrigin.Y, eyeZ, yawRight, 600.0f);

                        if (dLeft > dCenter + 90.0f && dLeft >= dRight)
                        {
                            ctx.CorridorPreAimYaw = yawLeft;
                            ctx.HasCorridorPreAim = true;
                        }
                        else if (dRight > dCenter + 90.0f && dRight > dLeft)
                        {
                            ctx.CorridorPreAimYaw = yawRight;
                            ctx.HasCorridorPreAim = true;
                        }
                        else
                        {
                            ctx.HasCorridorPreAim = false;
                        }
                    }
                    else
                    {
                        ctx.HasCorridorPreAim = false;
                    }
                }
                else
                {
                    ctx.HasCorridorPreAim = false;
                }
            }
        }

        private bool CheckRayClearance(CRayTraceInterface rayTrace, float sx, float sy, float sz, float dirX, float dirY, float dist)
        {
            try
            {
                _traceStart ??= new Vector(0f, 0f, 0f);
                _traceEnd ??= new Vector(0f, 0f, 0f);
                _traceStart.X = sx;
                _traceStart.Y = sy;
                _traceStart.Z = sz;
                _traceEnd.X = sx + dirX * dist;
                _traceEnd.Y = sy + dirY * dist;
                _traceEnd.Z = sz;

                var opts = new RayTraceAPI.TraceOptions(InteractionLayers.MASK_PLAYER_MOVE, InteractionLayers.None, false);
                if (!rayTrace.TraceEndShape(_traceStart, _traceEnd, null, opts, out RayTraceAPI.TraceResult res))
                    return true;
                return res.Fraction >= 0.85f;
            }
            catch
            {
                return true;
            }
        }

        private float MeasureRayDistance(CRayTraceInterface rayTrace, float sx, float sy, float sz, float dirX, float dirY, float maxDist)
        {
            try
            {
                _traceStart ??= new Vector(0f, 0f, 0f);
                _traceEnd ??= new Vector(0f, 0f, 0f);
                _traceStart.X = sx;
                _traceStart.Y = sy;
                _traceStart.Z = sz;
                _traceEnd.X = sx + dirX * maxDist;
                _traceEnd.Y = sy + dirY * maxDist;
                _traceEnd.Z = sz;

                var opts = new RayTraceAPI.TraceOptions(InteractionLayers.MASK_WORLD_ONLY, InteractionLayers.None, false);
                if (!rayTrace.TraceEndShape(_traceStart, _traceEnd, null, opts, out RayTraceAPI.TraceResult res))
                    return maxDist;
                return res.Fraction * maxDist;
            }
            catch
            {
                return maxDist;
            }
        }

        private float MeasureYawDistance(CRayTraceInterface rayTrace, float sx, float sy, float sz, float yawDeg, float maxDist)
        {
            float rad = yawDeg * (MathF.PI / 180.0f);
            return MeasureRayDistance(rayTrace, sx, sy, sz, MathF.Cos(rad), MathF.Sin(rad), maxDist);
        }

        // =========================================================================
        // 8. STATE MOVEMENT & SUBTICK COUNTER-STRAFE EXECUTION
        // =========================================================================

        private void ExecuteTacticalStateMovement(
            IBotControllerApi api,
            CCSPlayerController bot,
            CCSPlayerPawn pawn,
            CCSBot? ccsBot,
            BotAgentContext ctx,
            bool hasDirectEnemyInSight,
            int teamAlive,
            long nowMs,
            float nowSec)
        {
            int slot = ctx.Slot;

            // 1. TACTICAL RETREAT (Low HP or Empty Primary Clip -> Switch to Secondary Pistol, Never Knife!)
            if (ctx.CurrentState == TacticalState.TacticalRetreat)
            {
                if (ctx.CanMoveBack)
                {
                    api.InjectUsercmd(slot, IN_BACK | IN_SPEED, 55);
                }
                if (ctx.Clip1 <= 0 && ctx.SecondaryDefIndex > 0)
                {
                    api.SwitchBotWeapon(slot, ctx.SecondaryDefIndex);
                }
                return;
            }

            // If primary weapon ran dry in the middle of a duel and enemy is < 650u, fast-draw secondary pistol!
            if (hasDirectEnemyInSight && ctx.Clip1 <= 0 && ctx.TargetDistance < 650.0f && ctx.SecondaryDefIndex > 0)
            {
                api.SwitchBotWeapon(slot, ctx.SecondaryDefIndex);
            }

            // 2. COMBAT DUEL & TRADE FRAG (Wall-Safe ADAD Burst + Subtick Counter-Strafe < 72 u/s)
            if (EnableAdadStrafe && (ctx.CurrentState == TacticalState.CombatDuel || ctx.CurrentState == TacticalState.TradeFrag))
            {
                long elapsed = nowMs - ctx.PhaseStartMs;
                float dist = ctx.TargetDistance;

                // Close Range (< 420u): Controlled crouch commit + full-auto spray / rapid pistol cadence
                if (EnableDistanceCombatDiscipline && dist < 420.0f && hasDirectEnemyInSight)
                {
                    if (ccsBot != null)
                    {
                        ccsBot.IsRapidFiring = (ctx.EquippedClass == WeaponClass.Pistol);
                        ccsBot.FireWeaponTimestamp = 0f;
                    }
                    if (dist < 310.0f)
                    {
                        api.InjectUsercmd(slot, IN_DUCK, 55);
                        return;
                    }
                }

                // If both left and right are blocked (tight alcove), hold stationary accuracy
                if (!ctx.CanStrafeLeft && !ctx.CanStrafeRight)
                {
                    api.InjectUsercmd(slot, 0UL, 50);
                    return;
                }

                Vector? vel = pawn.AbsVelocity;
                float speed2D = (vel != null) ? MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y) : 0f;

                // Long Range (> 900u): Disciplined strafe -> subtick stop -> 1-tap / 2-tap headshot cadence
                if (EnableDistanceCombatDiscipline && dist > 900.0f)
                {
                    if (ccsBot != null)
                    {
                        ccsBot.IsRapidFiring = false;
                    }
                    if (ctx.CombatPhase == 0)
                    {
                        // Hold fire while wide-strafing at long range if we already fired our 2-tap burst
                        if (ccsBot != null && ctx.SprayTicks >= 2)
                        {
                            ccsBot.FireWeaponTimestamp = nowSec + 0.16f;
                        }
                        ulong moveBtn = (ctx.StrafeDir > 0) ? IN_MOVERIGHT : IN_MOVELEFT;
                        api.InjectUsercmd(slot, moveBtn, 40);
                        if (elapsed >= 155)
                        {
                            ctx.CombatPhase = 1;
                            ctx.PhaseStartMs = nowMs;
                        }
                    }
                    else if (ctx.CombatPhase == 1)
                    {
                        ulong counterBtn = (ctx.StrafeDir > 0) ? IN_MOVELEFT : IN_MOVERIGHT;
                        api.InjectUsercmd(slot, counterBtn, 38);
                        if ((EnableSubtickStop && speed2D < 72.0f) || elapsed >= 45)
                        {
                            ctx.CombatPhase = 2;
                            ctx.PhaseStartMs = nowMs;
                            ctx.SprayTicks = 0;
                            if (ccsBot != null) ccsBot.FireWeaponTimestamp = 0f;
                        }
                    }
                    else
                    {
                        if (ccsBot != null && ctx.SprayTicks < 2)
                        {
                            ccsBot.FireWeaponTimestamp = 0f;
                        }
                        api.InjectUsercmd(slot, 0UL, 55);
                        if (elapsed >= 120)
                        {
                            ctx.StrafeDir = PickNextStrafeDirection(ctx);
                            ctx.CombatPhase = 0;
                            ctx.PhaseStartMs = nowMs;
                        }
                    }
                    return;
                }

                // Mid Range (310 - 900u): Pro ADAD 4-Bullet Burst + Subtick Stop + 20% Crouch-Tap Evasion
                if (ctx.CombatPhase == 0)
                {
                    // Reset recoil during lateral strafe if 4+ bullets were already fired in the previous burst
                    if (ccsBot != null && dist >= 450.0f && ctx.SprayTicks >= 4)
                    {
                        ccsBot.IsRapidFiring = false;
                        ccsBot.FireWeaponTimestamp = nowSec + 0.13f;
                    }
                    ulong moveBtn = (ctx.StrafeDir > 0) ? IN_MOVERIGHT : IN_MOVELEFT;
                    api.InjectUsercmd(slot, moveBtn, 42);
                    if (elapsed >= 175)
                    {
                        ctx.CombatPhase = 1;
                        ctx.PhaseStartMs = nowMs;
                    }
                }
                else if (ctx.CombatPhase == 1)
                {
                    ulong counterBtn = (ctx.StrafeDir > 0) ? IN_MOVELEFT : IN_MOVERIGHT;
                    api.InjectUsercmd(slot, counterBtn, 38);
                    if ((EnableSubtickStop && speed2D < 72.0f) || elapsed >= 45)
                    {
                        ctx.CombatPhase = 2;
                        ctx.PhaseStartMs = nowMs;
                        ctx.DidCrouchTap = false;
                        ctx.SprayTicks = 0;
                        if (ccsBot != null) ccsBot.FireWeaponTimestamp = 0f;
                    }
                }
                else
                {
                    if (ccsBot != null && ctx.SprayTicks < 5)
                    {
                        ccsBot.FireWeaponTimestamp = 0f;
                    }
                    if (!ctx.DidCrouchTap && hasDirectEnemyInSight && Random.Shared.Next(0, 100) < 20)
                    {
                        api.InjectUsercmd(slot, IN_DUCK, 75);
                        ctx.DidCrouchTap = true;
                    }

                    if (elapsed >= 150)
                    {
                        ctx.StrafeDir = PickNextStrafeDirection(ctx);
                        ctx.CombatPhase = 0;
                        ctx.PhaseStartMs = nowMs;
                    }
                }
                return;
            }

            // 3. TACTICAL HOLD ("No Shiu" Silent Approach & Sightline Anchor)
            if (ctx.CurrentState == TacticalState.TacticalHold && ctx.ActivePeek == PeekStyle.None)
            {
                api.InjectUsercmd(slot, IN_SPEED, 55);

                if (ctx.IsAnchoredOnSightline && pawn.AbsVelocity != null)
                {
                    float s2d = MathF.Sqrt(pawn.AbsVelocity.X * pawn.AbsVelocity.X + pawn.AbsVelocity.Y * pawn.AbsVelocity.Y);
                    if (s2d < 40.0f)
                    {
                        pawn.AbsVelocity.X *= 0.35f;
                        pawn.AbsVelocity.Y *= 0.35f;
                    }
                }
                return;
            }

            // 4. OBJECTIVE CONTROL & 1vX CLUTCH STEALTH (With C4 Clock-Aware Retake Urgency!)
            if (EnableShiftWalk && ctx.ActivePeek == PeekStyle.None)
            {
                if (_isBombPlanted && bot.Team == CsTeam.CounterTerrorist)
                {
                    float c4TimeLeft = 40.0f - (nowSec - _bombPlantTime);
                    float distToC4 = (_hasPlantedC4Pos && pawn.AbsOrigin != null)
                        ? Dist2D(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, _plantedC4X, _plantedC4Y)
                        : 500.0f;

                    // Urgent Retake: If < 18s left on C4 OR still > 750u away from the planted site, SPRINT! Never shift-walk!
                    if (c4TimeLeft < 18.0f || distToC4 > 750.0f)
                    {
                        if (ccsBot != null)
                        {
                            ccsBot.IsRunning = true;
                            var hurry = ccsBot.HurryTimer;
                            if (hurry != null)
                            {
                                hurry.Duration = 4.0f;
                                hurry.Timestamp = nowSec + 4.0f;
                            }
                        }
                        return;
                    }
                }

                if (teamAlive == 1 || ctx.CurrentState == TacticalState.ObjectiveControl)
                {
                    api.InjectUsercmd(slot, IN_SPEED, 55);
                }
            }
        }

        // =========================================================================
        // 9. 3D BIOMECHANICAL AIM, RECOIL COMPENSATION & PIE-SLICING PRE-AIM
        // =========================================================================

        private void ExecuteBiomechanicalAim(
            CCSPlayerController bot,
            CCSPlayerPawn pawn,
            CCSBot? ccsBot,
            BotAgentContext ctx,
            bool hasDirectEnemyInSight,
            int teamAlive,
            float dt,
            float nowSec,
            long nowMs)
        {
            Vector? botOrigin = pawn.AbsOrigin;
            QAngle? eyeAngles = pawn.EyeAngles;
            if (botOrigin == null || eyeAngles == null) return;

            Vector? moveVel = pawn.AbsVelocity;
            float speed2D = (moveVel != null) ? MathF.Sqrt(moveVel.X * moveVel.X + moveVel.Y * moveVel.Y) : 0f;

            // CASE 1: Direct Enemy Visible -> Record visual threat & apply progressive recoil pull-down
            if (hasDirectEnemyInSight && ccsBot != null)
            {
                if (ccsBot.LastEnemyPosition != null)
                {
                    ctx.Threat.Set(ccsBot.LastEnemyPosition, nowSec + 2.10f, ThreatType.VisualSight, 1.50f);
                }

                if (EnableSprayCompensation &&
                    ctx.TargetDistance < 850.0f &&
                    ctx.SprayTicks >= 2 &&
                    (ctx.EquippedClass == WeaponClass.Rifle || ctx.EquippedClass == WeaponClass.SmgOrShotgun))
                {
                    float pullDownDegPerSec = (ctx.SprayTicks <= 12) ? 8.8f : 3.1f;
                    eyeAngles.X = Math.Clamp(eyeAngles.X + pullDownDegPerSec * dt, -52.0f, 52.0f);
                    if (pawn.V_angle != null)
                    {
                        pawn.V_angle.X = eyeAngles.X;
                    }
                }
                return;
            }

            // CASE 2: Tactical Threat Memory & Team Callout Facing (Head-height pre-aim Z + 61.5u)
            if (EnableTacticalThreatFacing && ctx.Threat.Active)
            {
                if (nowSec < ctx.Threat.ExpireTime)
                {
                    float viewZ = pawn.ViewOffset?.Z ?? 64.0f;
                    float dx = ctx.Threat.X - botOrigin.X;
                    float dy = ctx.Threat.Y - botOrigin.Y;
                    float dz = (ctx.Threat.Z + 61.5f) - (botOrigin.Z + viewZ);
                    float dist2D = MathF.Sqrt(dx * dx + dy * dy);

                    // Only pre-aim directly at a remembered threat when within the same sector/approach zone (<= 950u)!
                    // If the bot is still far away (> 950u), use normal forward path navigation until it enters the zone.
                    if (dist2D > 24.0f && dist2D <= 950.0f)
                    {
                        float targetYaw = MathF.Atan2(dy, dx) * (180.0f / MathF.PI);
                        float targetPitch = Math.Clamp(-MathF.Atan2(dz, dist2D) * (180.0f / MathF.PI), -52.0f, 52.0f);

                        // CRITICAL ANTI-BACKWARDS-RUN GUARD:
                        // When no enemy is directly visible and the bot is moving along its path or starting to move,
                        // NEVER twist the bot's view > 70 deg away from its movement/goal vector!
                        // (In Source 2 CCSBot::Update, looking > 90 deg away from path direction causes the engine to hold IN_BACK and run backwards!)
                        if (ctx.CurrentState != TacticalState.TacticalRetreat)
                        {
                            float desiredMoveYaw = float.NaN;
                            if (moveVel != null && speed2D > 36.0f)
                            {
                                desiredMoveYaw = MathF.Atan2(moveVel.Y, moveVel.X) * (180.0f / MathF.PI);
                            }
                            else if (ccsBot?.GoalPosition != null && (ccsBot.GoalPosition.X != 0f || ccsBot.GoalPosition.Y != 0f))
                            {
                                float gdx = ccsBot.GoalPosition.X - botOrigin.X;
                                float gdy = ccsBot.GoalPosition.Y - botOrigin.Y;
                                if (gdx * gdx + gdy * gdy > 400.0f)
                                {
                                    desiredMoveYaw = MathF.Atan2(gdy, gdx) * (180.0f / MathF.PI);
                                }
                            }

                            if (!float.IsNaN(desiredMoveYaw))
                            {
                                float yawDiffFromMove = MathF.Abs(NormalizeAngle(targetYaw - desiredMoveYaw));
                                if (yawDiffFromMove > 70.0f)
                                {
                                    if (ctx.Threat.Type == ThreatType.Footsteps)
                                    {
                                        ctx.Threat.Active = false;
                                    }
                                    goto HandleNavigationPreAim;
                                }
                            }
                        }

                        ApplySmoothHumanAim(pawn, ccsBot, eyeAngles, targetYaw, targetPitch, 410.0f, 3.8f, dt, nowSec);

                        // Trigger Contextual Peek when approaching threat in medium range
                        if (EnableContextualPeeks &&
                            ctx.ActivePeek == PeekStyle.None &&
                            dist2D > 280.0f && dist2D < 680.0f &&
                            ctx.CurrentState != TacticalState.TacticalRetreat &&
                            ctx.CurrentState != TacticalState.TacticalHold &&
                            (ctx.CanStrafeLeft || ctx.CanStrafeRight))
                        {
                            int dir = PickBestStrafeDirection(ctx);
                            PeekStyle style = (teamAlive == 1 || CurrentTempo == RoundTempo.DefaultHold)
                                ? PeekStyle.SilentShift
                                : (ctx.Threat.IsSniper ? PeekStyle.ShoulderJiggle : PeekStyle.FerrariPrefire);

                            StartPeek(ctx, style, dir, ctx.Threat.X, ctx.Threat.Y, ctx.Threat.Z, nowMs);
                        }
                        return;
                    }
                }
                else
                {
                    ctx.Threat.Active = false;
                }
            }

        HandleNavigationPreAim:
            // CASE 3: Sightline Anchor during TacticalHold ("No Shiu") — ONLY when stationary (< 36 u/s)!
            if (ctx.CurrentState == TacticalState.TacticalHold && ctx.IsAnchoredOnSightline)
            {
                if (speed2D < 36.0f)
                {
                    ApplySmoothHumanAim(pawn, ccsBot, eyeAngles, ctx.AnchoredYaw, 0.0f, 240.0f, 2.5f, dt, nowSec);
                    return;
                }
                else
                {
                    // Bot started moving along NavMesh — release anchor immediately so it never walks backwards!
                    ctx.IsAnchoredOnSightline = false;
                    ctx.HoldUntilSec = 0f;
                }
            }

            // CASE 4: 3D Slicing-the-Pie & Threat-Biased Forward Crosshair Placement during Navigation
            if (EnableCornerSlicingPreAim && ctx.CurrentState != TacticalState.TacticalRetreat && !pawn.IsDefusing)
            {
                if (moveVel != null && speed2D > 40.0f)
                {
                    float moveYaw = MathF.Atan2(moveVel.Y, moveVel.X) * (180.0f / MathF.PI);
                    float desiredYaw = ctx.HasCorridorPreAim ? ctx.CorridorPreAimYaw : moveYaw;

                    // If moving toward an active threat/TeamCallout around a corner (< 950u), bias forward pre-aim
                    // up to +-42 deg toward the threat so the bot slices the corner into Mid while still facing forward!
                    if (ctx.Threat.Active && nowSec < ctx.Threat.ExpireTime)
                    {
                        float tdx = ctx.Threat.X - botOrigin.X;
                        float tdy = ctx.Threat.Y - botOrigin.Y;
                        float tDist = MathF.Sqrt(tdx * tdx + tdy * tdy);
                        if (tDist > 40.0f && tDist < 950.0f)
                        {
                            float threatYaw = MathF.Atan2(tdy, tdx) * (180.0f / MathF.PI);
                            float relThreatYaw = NormalizeAngle(threatYaw - moveYaw);
                            desiredYaw = NormalizeAngle(moveYaw + Math.Clamp(relThreatYaw, -42.0f, 42.0f));
                        }
                    }

                    // Compute natural vertical pitch from stairs/ramps velocity slope (-28 to +28 deg)
                    float slopePitch = Math.Clamp(-MathF.Atan2(moveVel.Z, speed2D) * (180.0f / MathF.PI) * 0.65f, -28.0f, 28.0f);

                    float angleDiff = MathF.Abs(NormalizeAngle(eyeAngles.Y - desiredYaw));
                    if (angleDiff > 8.0f || MathF.Abs(eyeAngles.X - slopePitch) > 4.0f)
                    {
                        // Fast turn recovery (540 deg/s) if view was facing > 65 deg away from movement vector
                        float turnSpeed = (angleDiff > 65.0f) ? 540.0f : 310.0f;
                        ApplySmoothHumanAim(pawn, ccsBot, eyeAngles, desiredYaw, slopePitch, turnSpeed, 2.6f, dt, nowSec);
                    }
                    return;
                }
            }

            // Restore native pathfinder look control when stationary and not overriding aim
            if (ccsBot != null && !ctx.Threat.Active && !ctx.IsAnchoredOnSightline)
            {
                ccsBot.EyeAnglesUnderPathFinderControl = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplySmoothHumanAim(
            CCSPlayerPawn pawn,
            CCSBot? ccsBot,
            QAngle eyeAngles,
            float targetYaw,
            float targetPitch,
            float maxYawSpeedDegPerSec,
            float maxPitchStepDeg,
            float dt,
            float nowSec)
        {
            if (ccsBot != null)
            {
                ccsBot.EyeAnglesUnderPathFinderControl = false;
                ccsBot.InhibitLookAroundTimestamp = nowSec + 0.55f;
                ccsBot.LookAtSpotDuration = 0.0f;
            }

            float currentYaw = eyeAngles.Y;
            float deltaYaw = NormalizeAngle(targetYaw - currentYaw);
            float absDelta = MathF.Abs(deltaYaw);

            // Minimum-jerk sigmoidal wrist curve: fast mid-flick, smooth deceleration on target landing
            float speedFactor;
            if (!EnableHumanAimSmoothing || absDelta > 55.0f)
            {
                speedFactor = 1.0f;
            }
            else
            {
                float norm = absDelta / 55.0f;
                speedFactor = 0.24f + 0.76f * MathF.Sin(norm * MathF.PI * 0.5f);
            }

            float maxTurn = maxYawSpeedDegPerSec * speedFactor * dt;
            float yawStep = Math.Clamp(deltaYaw, -maxTurn, maxTurn);
            float newYaw = NormalizeAngle(currentYaw + yawStep);
            eyeAngles.Y = newYaw;

            float currentPitch = eyeAngles.X;
            float deltaPitch = Math.Clamp(targetPitch - currentPitch, -maxPitchStepDeg, maxPitchStepDeg);
            float newPitch = Math.Clamp(currentPitch + deltaPitch, -52.0f, 52.0f);
            eyeAngles.X = newPitch;

            // Keep CCSPlayerPawn.V_angle synchronized with EyeAngles so CCSBot::UpdateLookAngles (0x2F05D0) never fights it!
            QAngle? vAngle = pawn.V_angle;
            if (vAngle != null)
            {
                vAngle.Y = newYaw;
                vAngle.X = newPitch;
            }

            if (ccsBot != null)
            {
                ccsBot.LookYaw = newYaw;
                ccsBot.LookPitch = newPitch;
                var aimGoal = ccsBot.AimGoal;
                if (aimGoal != null)
                {
                    aimGoal.X = newPitch;
                    aimGoal.Y = newYaw;
                }
            }
        }

        // =========================================================================
        // 10. CONTEXTUAL PEEKS, TEAM RADAR CALLOUTS & HELPER ROUTINES
        // =========================================================================

        private void BroadcastTeamCallout(CsTeam team, Vector enemyPos, float priority, float durationSec, float nowSec)
        {
            int enemyAlive = 0;
            int enemiesNearSpot = 0;

            foreach (var p in Utilities.GetPlayers())
            {
                if (p == null || !p.IsValid || !p.PawnIsAlive || p.Team == team) continue;
                if (p.Team == CsTeam.CounterTerrorist || p.Team == CsTeam.Terrorist)
                {
                    enemyAlive++;
                    var ep = p.PlayerPawn?.Value;
                    if (ep?.AbsOrigin != null && Dist3D(ep.AbsOrigin, enemyPos) <= 750.0f)
                    {
                        enemiesNearSpot++;
                    }
                }
            }

            // Full Rotation vs. Site Anchor Discipline:
            // - Rotate from the other bombsite ONLY if:
            //   1) Last enemy alive (enemyAlive == 1, e.g. 3v1 / 2v1 hunt — impossible to be a fake on B!), OR
            //   2) C4 is already planted (_isBombPlanted), OR
            //   3) Heavy execute confirmed (3+ enemies spotted rushing the site: enemiesNearSpot >= 3).
            // - Otherwise (e.g. 1 or 2 enemies spotted at A while other enemies are still alive in a 5v5/4v4),
            //   ONLY bots on the SAME site/connector (dist <= 900u) react! The anchor on B stays on B!
            bool isFullRotateTrigger = (enemyAlive == 1 || _isBombPlanted || enemiesNearSpot >= 3);

            foreach (var p in Utilities.GetPlayers())
            {
                if (p == null || !p.IsValid || !p.IsBot || !p.PawnIsAlive || p.Team != team)
                    continue;

                int s = p.Slot;
                if (s < 0 || s >= MAX_SLOTS) continue;

                var bPawn = p.PlayerPawn?.Value;
                if (bPawn?.AbsOrigin == null) continue;

                float dist = Dist3D(bPawn.AbsOrigin, enemyPos);
                var ctx = _agents[s];

                if (dist <= 900.0f)
                {
                    // Same bombsite / adjacent connector partner: release hold & pre-aim to help teammate!
                    ctx.HoldUntilSec = 0f;
                    ctx.IsAnchoredOnSightline = false;

                    if (!ctx.Threat.Active || ctx.Threat.Priority <= priority || nowSec > ctx.Threat.ExpireTime)
                    {
                        ctx.Threat.Set(enemyPos, nowSec + durationSec, ThreatType.TeamCallout, priority);
                    }
                }
                else if (isFullRotateTrigger)
                {
                    // Confirmed last enemy (X vs 1) or full site execute: release passive hold on other site & hurry rotation!
                    ctx.HoldUntilSec = 0f;
                    ctx.IsAnchoredOnSightline = false;

                    if (bPawn.Bot != null)
                    {
                        var hurry = bPawn.Bot.HurryTimer;
                        if (hurry != null)
                        {
                            hurry.Duration = 8.0f;
                            hurry.Timestamp = nowSec + 8.0f;
                        }
                    }

                    if (dist <= 1450.0f && (!ctx.Threat.Active || ctx.Threat.Priority <= priority || nowSec > ctx.Threat.ExpireTime))
                    {
                        ctx.Threat.Set(enemyPos, nowSec + durationSec, ThreatType.TeamCallout, priority);
                    }
                }
            }
        }

        private void ProcessTeamRadarAndCallouts(
            CRayTraceInterface? rayTrace,
            int livingPlayersCount,
            int ctAlive,
            int tAlive,
            float nowSec)
        {
            for (int i = 0; i < livingPlayersCount; i++)
            {
                var observer = _livingPlayersBuffer[i];
                if (observer == null || !observer.IsValid) continue;

                var obsPawn = observer.PlayerPawn?.Value;
                if (obsPawn == null || !obsPawn.IsValid || obsPawn.AbsOrigin == null) continue;

                CsTeam obsTeam = observer.Team;

                // 1. If observer is a Bot with direct visual contact on an enemy, share with same-site allies (or full team if 3v1/execute)
                if (observer.IsBot)
                {
                    var obsBot = obsPawn.Bot;
                    if (obsBot != null && obsBot.IsEnemyVisible && obsBot.LastEnemyPosition != null)
                    {
                        BroadcastTeamCallout(obsTeam, obsBot.LastEnemyPosition, 1.34f, 2.00f, nowSec);
                    }
                    continue;
                }

                // 2. If observer is a Human Player: check if an enemy is within their forward field-of-view & LOS!
                QAngle? obsAngles = obsPawn.EyeAngles;
                if (obsAngles == null) continue;

                float obsEyeZ = obsPawn.AbsOrigin.Z + (obsPawn.ViewOffset?.Z ?? 64.0f);

                for (int j = 0; j < livingPlayersCount; j++)
                {
                    var target = _livingPlayersBuffer[j];
                    if (target == null || target.Team == obsTeam) continue;

                    var tPawn = target.PlayerPawn?.Value;
                    if (tPawn == null || !tPawn.IsValid || tPawn.AbsOrigin == null) continue;

                    float dx = tPawn.AbsOrigin.X - obsPawn.AbsOrigin.X;
                    float dy = tPawn.AbsOrigin.Y - obsPawn.AbsOrigin.Y;
                    float dist2D = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist2D > 2000.0f) continue;

                    float yawToEnemy = MathF.Atan2(dy, dx) * (180.0f / MathF.PI);
                    if (MathF.Abs(NormalizeAngle(obsAngles.Y - yawToEnemy)) <= 45.0f)
                    {
                        bool hasLos = true;
                        if (rayTrace != null)
                        {
                            float tHeadZ = tPawn.AbsOrigin.Z + 60.0f;
                            hasLos = CheckPointToPointLos(rayTrace, obsPawn.AbsOrigin.X, obsPawn.AbsOrigin.Y, obsEyeZ, tPawn.AbsOrigin.X, tPawn.AbsOrigin.Y, tHeadZ);
                        }

                        if (hasLos)
                        {
                            BroadcastTeamCallout(obsTeam, tPawn.AbsOrigin, 1.38f, 2.40f, nowSec);
                        }
                    }
                }
            }
        }

        private bool CheckPointToPointLos(CRayTraceInterface rayTrace, float sx, float sy, float sz, float ex, float ey, float ez)
        {
            try
            {
                _traceStart ??= new Vector(0f, 0f, 0f);
                _traceEnd ??= new Vector(0f, 0f, 0f);
                _traceStart.X = sx;
                _traceStart.Y = sy;
                _traceStart.Z = sz;
                _traceEnd.X = ex;
                _traceEnd.Y = ey;
                _traceEnd.Z = ez;

                var opts = new RayTraceAPI.TraceOptions(InteractionLayers.MASK_WORLD_ONLY, InteractionLayers.None, false);
                if (!rayTrace.TraceEndShape(_traceStart, _traceEnd, null, opts, out RayTraceAPI.TraceResult res))
                    return true;
                return res.Fraction >= 0.96f;
            }
            catch
            {
                return false;
            }
        }

        private void ExecuteContextualPeek(IBotControllerApi api, BotAgentContext ctx, long nowMs)
        {
            int slot = ctx.Slot;
            long elapsed = nowMs - ctx.PeekStepStartMs;

            // Abort peek immediately if wall clearance is blocked in peek direction
            if ((ctx.PeekDir > 0 && !ctx.CanStrafeRight) || (ctx.PeekDir < 0 && !ctx.CanStrafeLeft))
            {
                if (ctx.PeekStep == 0)
                {
                    ctx.ActivePeek = PeekStyle.None;
                    return;
                }
            }

            if (ctx.ActivePeek == PeekStyle.ShoulderJiggle)
            {
                if (ctx.PeekStep == 0)
                {
                    ulong btn = (ctx.PeekDir > 0) ? IN_MOVERIGHT : IN_MOVELEFT;
                    api.InjectUsercmd(slot, btn, 38);
                    if (elapsed >= 72)
                    {
                        ctx.PeekStep = 1;
                        ctx.PeekStepStartMs = nowMs;
                    }
                }
                else
                {
                    ulong counterBtn = (ctx.PeekDir > 0) ? IN_MOVELEFT : IN_MOVERIGHT;
                    api.InjectUsercmd(slot, counterBtn, 42);
                    if (elapsed >= 75)
                    {
                        ctx.ActivePeek = PeekStyle.None;
                    }
                }
            }
            else if (ctx.ActivePeek == PeekStyle.FerrariPrefire)
            {
                if (ctx.PeekStep == 0)
                {
                    ulong btn = (ctx.PeekDir > 0) ? IN_MOVERIGHT : IN_MOVELEFT;
                    api.InjectUsercmd(slot, btn, 42);
                    if (elapsed >= 170)
                    {
                        ctx.PeekStep = 1;
                        ctx.PeekStepStartMs = nowMs;
                    }
                }
                else if (ctx.PeekStep == 1)
                {
                    ulong counterBtn = (ctx.PeekDir > 0) ? IN_MOVELEFT : IN_MOVERIGHT;
                    api.InjectUsercmd(slot, counterBtn, 38);
                    if (elapsed >= 45)
                    {
                        ctx.PeekStep = 2;
                        ctx.PeekStepStartMs = nowMs;
                    }
                }
                else
                {
                    api.InjectUsercmd(slot, 0UL, 80);
                    if (elapsed >= 115)
                    {
                        ctx.ActivePeek = PeekStyle.None;
                    }
                }
            }
            else if (ctx.ActivePeek == PeekStyle.SilentShift)
            {
                ulong btn = ((ctx.PeekDir > 0) ? IN_MOVERIGHT : IN_MOVELEFT) | IN_SPEED;
                api.InjectUsercmd(slot, btn, 45);
                if (elapsed >= 230)
                {
                    ctx.ActivePeek = PeekStyle.None;
                }
            }
        }

        private void ProcessDoublePeeks(int livingBotsCount, long nowMs, float nowSec)
        {
            for (int i = 0; i < livingBotsCount; i++)
            {
                var b1 = _livingBotsBuffer[i];
                if (b1 == null) continue;
                int s1 = b1.Slot;
                if (s1 < 0 || s1 >= MAX_SLOTS) continue;
                var ctx1 = _agents[s1];
                if (!ctx1.Threat.Active || nowSec > ctx1.Threat.ExpireTime || nowMs < ctx1.DoublePeekCooldownMs)
                    continue;

                var p1 = b1.PlayerPawn?.Value;
                if (p1 == null || p1.AbsOrigin == null) continue;

                for (int j = i + 1; j < livingBotsCount; j++)
                {
                    var b2 = _livingBotsBuffer[j];
                    if (b2 == null || b1.Team != b2.Team) continue;
                    int s2 = b2.Slot;
                    if (s2 < 0 || s2 >= MAX_SLOTS) continue;
                    var ctx2 = _agents[s2];
                    if (!ctx2.Threat.Active || nowSec > ctx2.Threat.ExpireTime || nowMs < ctx2.DoublePeekCooldownMs)
                        continue;

                    var p2 = b2.PlayerPawn?.Value;
                    if (p2 == null || p2.AbsOrigin == null) continue;

                    float distBetween = Dist3D(p1.AbsOrigin, p2.AbsOrigin);
                    if (distBetween < 500.0f)
                    {
                        float distToThreat = Dist2D(p1.AbsOrigin.X, p1.AbsOrigin.Y, ctx1.Threat.X, ctx1.Threat.Y);
                        if (distToThreat > 260.0f && distToThreat < 850.0f)
                        {
                            ctx1.DoublePeekCooldownMs = nowMs + 2400;
                            ctx2.DoublePeekCooldownMs = nowMs + 2400;

                            int dir1 = PickBestStrafeDirection(ctx1);
                            StartPeek(ctx1, PeekStyle.FerrariPrefire, dir1, ctx1.Threat.X, ctx1.Threat.Y, ctx1.Threat.Z, nowMs);

                            int dir2 = PickBestStrafeDirection(ctx2);
                            AddTimer(0.22f, () =>
                            {
                                try
                                {
                                    if (b2.IsValid && b2.PawnIsAlive && ctx2.ActivePeek == PeekStyle.None)
                                    {
                                        StartPeek(ctx2, PeekStyle.FerrariPrefire, dir2, ctx2.Threat.X, ctx2.Threat.Y, ctx2.Threat.Z, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                                    }
                                }
                                catch { }
                            });
                            break;
                        }
                    }
                }
            }
        }

        private void ProcessFootstepSensors(int livingPlayersCount, int livingBotsCount, float nowSec)
        {
            for (int eIdx = 0; eIdx < livingPlayersCount; eIdx++)
            {
                var enemy = _livingPlayersBuffer[eIdx];
                if (enemy == null) continue;

                var ePawn = enemy.PlayerPawn?.Value;
                if (ePawn == null || !ePawn.IsValid || ePawn.AbsOrigin == null || ePawn.AbsVelocity == null)
                    continue;

                Vector ePos = ePawn.AbsOrigin;
                Vector eVel = ePawn.AbsVelocity;
                float speed2D = MathF.Sqrt(eVel.X * eVel.X + eVel.Y * eVel.Y);
                if (speed2D <= 138.0f) continue;

                bool isSilent = false;
                var moveServices = ePawn.MovementServices;
                if (moveServices != null && moveServices.Buttons != null)
                {
                    var btns = moveServices.Buttons.ButtonStates;
                    if (btns.Length > 0 && ((btns[0] & (IN_SPEED | IN_DUCK)) != 0))
                    {
                        isSilent = true;
                    }
                }
                if (isSilent) continue;

                for (int bIdx = 0; bIdx < livingBotsCount; bIdx++)
                {
                    var bot = _livingBotsBuffer[bIdx];
                    if (bot == null || bot.Team == enemy.Team || bot.Slot == enemy.Slot) continue;

                    int botSlot = bot.Slot;
                    if (botSlot < 0 || botSlot >= MAX_SLOTS) continue;

                    var bPawn = bot.PlayerPawn?.Value;
                    if (bPawn == null || !bPawn.IsValid || bPawn.AbsOrigin == null) continue;

                    float dist = Dist3D(ePos, bPawn.AbsOrigin);
                    if (dist < 620.0f)
                    {
                        var ctx = _agents[botSlot];
                        if (!ctx.Threat.Active || ctx.Threat.Priority <= 0.75f || nowSec > ctx.Threat.ExpireTime)
                        {
                            ctx.Threat.Set(ePos, nowSec + 0.95f, ThreatType.Footsteps, 0.75f);
                        }
                        if (dist < 550.0f)
                        {
                            ctx.HoldUntilSec = 0f;
                            ctx.IsAnchoredOnSightline = false;
                        }
                    }
                }
            }
        }

        private static void UpdateWeaponTelemetry(CCSPlayerPawn pawn, CCSPlayerController bot, BotAgentContext ctx)
        {
            ctx.Money = bot.InGameMoneyServices?.Account ?? 800;
            var weaponServices = pawn.WeaponServices;
            if (weaponServices == null) return;

            var activeWeapon = weaponServices.ActiveWeapon?.Value;
            if (activeWeapon != null && activeWeapon.IsValid)
            {
                ctx.Clip1 = activeWeapon.Clip1;
                string name = (activeWeapon.DesignerName ?? "").ToLowerInvariant();
                if (name.Contains("awp") || name.Contains("ssg08") || name.Contains("scar20") || name.Contains("g3sg1"))
                    ctx.EquippedClass = WeaponClass.Sniper;
                else if (name.Contains("ak47") || name.Contains("m4a1") || name.Contains("galil") || name.Contains("famas") || name.Contains("aug") || name.Contains("sg556"))
                    ctx.EquippedClass = WeaponClass.Rifle;
                else if (name.Contains("mp9") || name.Contains("mac10") || name.Contains("ump") || name.Contains("p90") || name.Contains("mp7") || name.Contains("mp5") || name.Contains("xm1014") || name.Contains("mag7") || name.Contains("nova"))
                    ctx.EquippedClass = WeaponClass.SmgOrShotgun;
                else if (name.Contains("glock") || name.Contains("usp") || name.Contains("hkp2000") || name.Contains("deagle") || name.Contains("p250") || name.Contains("tec9") || name.Contains("fiveseven") || name.Contains("elite") || name.Contains("cz75"))
                    ctx.EquippedClass = WeaponClass.Pistol;
                else
                    ctx.EquippedClass = WeaponClass.KnifeOrUtility;
            }

            if (weaponServices.MyWeapons != null)
            {
                foreach (var handle in weaponServices.MyWeapons)
                {
                    var wep = handle.Value;
                    if (wep == null || !wep.IsValid) continue;
                    var vData = new CCSWeaponBase(wep.Handle).VData;
                    if (vData != null)
                    {
                        int gearSlot = (int)vData.GearSlot;
                        int itemDef = wep.AttributeManager.Item.ItemDefinitionIndex;
                        if (gearSlot == 0 && itemDef > 0)
                        {
                            ctx.PrimaryDefIndex = itemDef;
                        }
                        else if (gearSlot == 1 && itemDef > 0 && ctx.SecondaryDefIndex <= 0)
                        {
                            ctx.SecondaryDefIndex = itemDef;
                        }
                    }
                }
            }
        }

        private static void SuppressCombatJump(CCSPlayerPawn pawn, CCSBot? ccsBot, float nowSec)
        {
            var moveServices = pawn.MovementServices;
            if (moveServices != null)
            {
                var btns = moveServices.Buttons;
                if (btns != null)
                {
                    var states = btns.ButtonStates;
                    if (states.Length > 0) states[0] &= ~IN_JUMP;
                    if (states.Length > 1) states[1] &= ~IN_JUMP;
                }
                moveServices.QueuedButtonDownMask &= ~IN_JUMP;
            }

            if (ccsBot != null)
            {
                ccsBot.ButtonFlags &= ~IN_JUMP;
                ccsBot.JumpTimestamp = 0f;
                var stuckTimer = ccsBot.StuckJumpTimer;
                if (stuckTimer != null)
                {
                    stuckTimer.Duration = 0f;
                    stuckTimer.Timestamp = nowSec;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StartPeek(BotAgentContext ctx, PeekStyle style, int dir, float tx, float ty, float tz, long nowMs)
        {
            ctx.ActivePeek = style;
            ctx.PeekStep = 0;
            ctx.PeekStepStartMs = nowMs;
            ctx.PeekDir = dir;
            ctx.PeekTargetX = tx;
            ctx.PeekTargetY = ty;
            ctx.PeekTargetZ = tz;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PickBestStrafeDirection(BotAgentContext ctx)
        {
            if (ctx.CanStrafeRight && !ctx.CanStrafeLeft) return 1;
            if (ctx.CanStrafeLeft && !ctx.CanStrafeRight) return -1;
            return (Random.Shared.Next(0, 2) == 0) ? 1 : -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PickNextStrafeDirection(BotAgentContext ctx)
        {
            int flipped = -ctx.StrafeDir;
            if (flipped > 0 && ctx.CanStrafeRight) return 1;
            if (flipped < 0 && ctx.CanStrafeLeft) return -1;
            return ctx.StrafeDir;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Dist2D(float x1, float y1, float x2, float y2)
        {
            float dx = x1 - x2;
            float dy = y1 - y2;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Dist3D(Vector a, Vector b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float NormalizeAngle(float angle)
        {
            angle %= 360.0f;
            if (angle > 180.0f) angle -= 360.0f;
            if (angle < -180.0f) angle += 360.0f;
            return angle;
        }
    }
}
