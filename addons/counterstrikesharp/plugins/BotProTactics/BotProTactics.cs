using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Utils;
using BotControllerApi;

namespace BotProTactics
{
    public class BotCombatMovement
    {
        public int Slot;
        public long LastCombatTime;
        public int Direction; // 1 = Right, -1 = Left
        public int Phase; // 0 = Strafe, 1 = CounterStrafe, 2 = StopAndShoot, 3 = SprayCommit
        public long PhaseStart;
        public bool DidCrouchTap;
        public long CurrentInjectionId;
        public float TargetDistance;
        public int SprayTicks;

        public BotCombatMovement(int slot, float targetDistance = 600.0f)
        {
            Slot = slot;
            LastCombatTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Direction = (Random.Shared.Next(0, 2) == 0) ? 1 : -1;
            Phase = 0;
            PhaseStart = LastCombatTime;
            DidCrouchTap = false;
            CurrentInjectionId = 0;
            TargetDistance = targetDistance;
            SprayTicks = 0;
        }
    }

    public enum ThreatType
    {
        Gunfire,
        Footsteps,
        TeammateDeath,
        VisualSight,
        SniperThreat
    }

    public class BotThreatMemory
    {
        public Vector Position;
        public float ExpireTime;
        public ThreatType Type;
        public float Priority;
        public bool IsSniper;

        public BotThreatMemory(Vector pos, float expireTime, ThreatType type, float priority, bool isSniper = false)
        {
            Position = new Vector(pos.X, pos.Y, pos.Z);
            ExpireTime = expireTime;
            Type = type;
            Priority = priority;
            IsSniper = isSniper;
        }
    }

    public enum PeekStyle
    {
        None,
        SilentShift,     // Slow silent slice on Shift (Clutch / Lurk)
        FerrariPrefire,  // Aggressive wide swing + instant counter-strafe + prefire
        ShoulderJiggle   // 75ms micro peek to bait sniper shots
    }

    public class BotPeekState
    {
        public int Slot;
        public PeekStyle Style;
        public int Step;
        public long StepStart;
        public int Direction; // -1 = Left, 1 = Right
        public Vector TargetSpot;
        public long LastPeekTime;

        public BotPeekState(int slot, PeekStyle style, int direction, Vector targetSpot)
        {
            Slot = slot;
            Style = style;
            Step = 0;
            StepStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Direction = direction;
            TargetSpot = new Vector(targetSpot.X, targetSpot.Y, targetSpot.Z);
            LastPeekTime = StepStart;
        }
    }

    public enum RoundTempo
    {
        FastRush,     // 25% chance: Rapid coordinated entry
        DefaultHold,  // 50% chance: "No Shiu" - slow shift default, holding angles for picks
        FakeSplit     // 25% chance: Lurkers hold quietly on flanks
    }

    public class BotProTacticsPlugin : BasePlugin
    {
        public override string ModuleName => "BotProTactics";
        public override string ModuleVersion => "1.5.0";
        public override string ModuleAuthor => "Antigravity & ed0ard";
        public override string ModuleDescription => "Tier 1 Pro AI: Anti-rush 'No Shiu' tempos, subtick stop, human aim curves, spray control, contextual peeks";

        // Button constants for Source 2
        public const ulong IN_ATTACK = 1UL;
        public const ulong IN_JUMP = 2UL;
        public const ulong IN_DUCK = 4UL;
        public const ulong IN_FORWARD = 8UL;
        public const ulong IN_BACK = 16UL;
        public const ulong IN_USE = 32UL;
        public const ulong IN_MOVELEFT = 512UL;    // 0x200
        public const ulong IN_MOVERIGHT = 1024UL;  // 0x400
        public const ulong IN_SPEED = 65536UL;     // 0x10000 (Shift-walk)

        private PluginCapability<IBotControllerApi>? _controllerCap;
        private IBotControllerApi? _controllerApi;

        // Tracking dictionaries
        private readonly Dictionary<int, BotCombatMovement> _combatStates = new();
        private readonly Dictionary<int, bool> _fakeDefuseDone = new();
        private readonly Dictionary<int, long> _refragAggressionUntil = new();
        private readonly Dictionary<int, BotThreatMemory> _threatMemories = new();
        private readonly Dictionary<int, long> _lastDamageTime = new();
        private readonly Dictionary<int, BotPeekState> _peekStates = new();
        private readonly Dictionary<int, long> _retreatUntil = new();
        private readonly Dictionary<int, long> _doublePeekAssigned = new();
        private readonly Dictionary<int, float> _holdUntilSec = new(); // Anti-rush "No Shiu" holds

        public RoundTempo CurrentTempo = RoundTempo.DefaultHold;
        private bool _isBombPlanted = false;
        private int _tickCount = 0;

        // Feature toggles
        public bool EnableAntiRushTempos = true;
        public bool EnableSubtickStop = true;
        public bool EnableHumanAimSmoothing = true;
        public bool EnableSprayCompensation = true;
        public bool EnableAdadStrafe = true;
        public bool EnableJumpSuppressionInCombat = true;
        public bool EnableTacticalThreatFacing = true;
        public bool EnableMovementHeading = true;
        public bool EnableContextualPeeks = true;
        public bool EnableDistanceCombatDiscipline = true;
        public bool EnableTacticalRetreat = true;
        public bool EnableCoordinatedDoublePeeks = true;
        public bool EnableShiftWalk = true;
        public bool EnableFakeDefuse = true;
        public bool EnableTradeFrags = true;

        public override void Load(bool hotReload)
        {
            _controllerCap = new PluginCapability<IBotControllerApi>("botcontroller:api");

            RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
            RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
            RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);
            RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
            RegisterEventHandler<EventRoundStart>(OnRoundStart);

            RegisterListener<Listeners.OnTick>(OnTick);

            Console.WriteLine("[BotProTactics] v1.5.0 Loaded! Anti-Rush 'No Shiu' tempos, Subtick stops & Human Aim curves active.");
        }

        private IBotControllerApi? GetApi()
        {
            if (_controllerApi != null)
                return _controllerApi;

            try
            {
                if (_controllerCap != null)
                {
                    _controllerApi = _controllerCap.Get();
                }
            }
            catch
            {
                _controllerApi = null;
            }

            return _controllerApi;
        }

        private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
        {
            _combatStates.Clear();
            _fakeDefuseDone.Clear();
            _refragAggressionUntil.Clear();
            _threatMemories.Clear();
            _lastDamageTime.Clear();
            _peekStates.Clear();
            _retreatUntil.Clear();
            _doublePeekAssigned.Clear();
            _holdUntilSec.Clear();
            _isBombPlanted = false;

            // Roll organic round pacing: 50% DefaultHold ("No Shiu"), 25% FastRush, 25% FakeSplit
            int roll = Random.Shared.Next(0, 100);
            if (roll < 50)
            {
                CurrentTempo = RoundTempo.DefaultHold;
            }
            else if (roll < 75)
            {
                CurrentTempo = RoundTempo.FastRush;
            }
            else
            {
                CurrentTempo = RoundTempo.FakeSplit;
            }

            float nowSec = Server.CurrentTime;

            // Assign tactical hold durations ("No Shiu") at round start
            if (EnableAntiRushTempos)
            {
                foreach (var player in Utilities.GetPlayers())
                {
                    if (player != null && player.IsValid && player.IsBot)
                    {
                        if (CurrentTempo == RoundTempo.DefaultHold)
                        {
                            // 22s - 38s slow default hold
                            _holdUntilSec[player.Slot] = nowSec + Random.Shared.Next(22, 39);
                        }
                        else if (CurrentTempo == RoundTempo.FakeSplit)
                        {
                            // 50% of the team holds quietly on lurk / fake
                            if (Random.Shared.Next(0, 2) == 0)
                            {
                                _holdUntilSec[player.Slot] = nowSec + Random.Shared.Next(26, 44);
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"[BotProTactics] New Round! Tempo rolled: {CurrentTempo}. Active Holds: {_holdUntilSec.Count}");
            return HookResult.Continue;
        }

        private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
        {
            _isBombPlanted = true;
            // When bomb is planted, cancel all passive holds immediately
            _holdUntilSec.Clear();
            return HookResult.Continue;
        }

        private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
        {
            var shooter = @event.Userid;
            if (shooter == null || !shooter.IsValid || shooter.PlayerPawn == null || !shooter.PlayerPawn.IsValid)
                return HookResult.Continue;

            var shooterPawn = shooter.PlayerPawn.Value;
            if (shooterPawn == null || !shooterPawn.IsValid)
                return HookResult.Continue;

            Vector? shooterPos = shooterPawn.AbsOrigin;
            if (shooterPos == null)
                return HookResult.Continue;

            int slot = shooter.Slot;
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float nowSec = Server.CurrentTime;

            // Firing a weapon breaks passive hold for the shooter
            _holdUntilSec.Remove(slot);

            string weaponName = (@event.Weapon ?? "").ToLowerInvariant();
            bool isSniper = weaponName.Contains("awp") || weaponName.Contains("ssg08") || weaponName.Contains("scar20") || weaponName.Contains("g3sg1");
            bool isSilenced = weaponName.Contains("silencer");
            float hearRange = isSilenced ? 900.0f : 3200.0f;

            if (shooter.IsBot && EnableAdadStrafe)
            {
                if (!_combatStates.TryGetValue(slot, out var state))
                {
                    _combatStates[slot] = new BotCombatMovement(slot);
                }
                else
                {
                    state.LastCombatTime = nowMs;
                }
            }

            // Tactical Sound Awareness
            if (EnableTacticalThreatFacing)
            {
                CsTeam shooterTeam = shooter.Team;

                foreach (var player in Utilities.GetPlayers())
                {
                    if (player == null || !player.IsValid || !player.IsBot || !player.PawnIsAlive || player.Team == shooterTeam || player.Slot == slot)
                        continue;

                    var botPawn = player.PlayerPawn?.Value;
                    if (botPawn == null || !botPawn.IsValid)
                        continue;

                    Vector? botPos = botPawn.AbsOrigin;
                    if (botPos == null)
                        continue;

                    float dist = (shooterPos - botPos).Length();
                    if (dist <= hearRange)
                    {
                        int botSlot = player.Slot;
                        float priority = isSniper ? 1.3f : 1.0f;
                        if (!_threatMemories.TryGetValue(botSlot, out var existing) || existing.Priority <= priority || nowSec > existing.ExpireTime)
                        {
                            _threatMemories[botSlot] = new BotThreatMemory(shooterPos, nowSec + 3.8f, isSniper ? ThreatType.SniperThreat : ThreatType.Gunfire, priority, isSniper);
                        }

                        // Close gunfire (< 1100u) breaks passive hold
                        if (dist < 1100.0f)
                        {
                            _holdUntilSec.Remove(botSlot);
                        }

                        if (EnableContextualPeeks && isSniper && dist < 1600.0f && !_peekStates.ContainsKey(botSlot))
                        {
                            int peekDir = (Random.Shared.Next(0, 2) == 0) ? 1 : -1;
                            _peekStates[botSlot] = new BotPeekState(botSlot, PeekStyle.ShoulderJiggle, peekDir, shooterPos);
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

            if (victim != null && victim.IsValid && victim.IsBot)
            {
                int victimSlot = victim.Slot;
                _lastDamageTime[victimSlot] = nowMs;
                _holdUntilSec.Remove(victimSlot); // Taking damage breaks passive hold

                var vPawn = victim.PlayerPawn?.Value;
                int hp = vPawn?.Health ?? 100;

                if (EnableTacticalRetreat && hp <= 25)
                {
                    _retreatUntil[victimSlot] = nowMs + 1800;
                }

                if (EnableAdadStrafe)
                {
                    if (!_combatStates.TryGetValue(victimSlot, out var state))
                    {
                        _combatStates[victimSlot] = new BotCombatMovement(victimSlot);
                    }
                    else
                    {
                        state.LastCombatTime = nowMs;
                    }
                }

                if (EnableTacticalThreatFacing && attacker != null && attacker.IsValid && attacker.PlayerPawn != null && attacker.PlayerPawn.IsValid)
                {
                    var attackerPawn = attacker.PlayerPawn.Value;
                    if (attackerPawn != null && attackerPawn.IsValid && attackerPawn.AbsOrigin != null)
                    {
                        _threatMemories[victimSlot] = new BotThreatMemory(attackerPawn.AbsOrigin, nowSec + 4.0f, ThreatType.Gunfire, 1.2f);
                    }
                }
            }

            return HookResult.Continue;
        }

        private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
        {
            var victim = @event.Userid;
            var attacker = @event.Attacker;
            float nowSec = Server.CurrentTime;

            if (victim != null && victim.IsValid)
            {
                _combatStates.Remove(victim.Slot);
                _fakeDefuseDone.Remove(victim.Slot);
                _threatMemories.Remove(victim.Slot);
                _lastDamageTime.Remove(victim.Slot);
                _peekStates.Remove(victim.Slot);
                _retreatUntil.Remove(victim.Slot);
                _doublePeekAssigned.Remove(victim.Slot);
                _holdUntilSec.Remove(victim.Slot);
            }

            if (victim != null && victim.IsValid && attacker != null && attacker.IsValid && victim.PlayerPawn != null && victim.PlayerPawn.IsValid)
            {
                var victimPawn = victim.PlayerPawn.Value;
                var attackerPawn = attacker.PlayerPawn?.Value;
                if (victimPawn == null || !victimPawn.IsValid) return HookResult.Continue;

                Vector? victimPos = victimPawn.AbsOrigin;
                Vector? killerPos = (attackerPawn != null && attackerPawn.IsValid) ? attackerPawn.AbsOrigin : null;
                if (victimPos == null) return HookResult.Continue;

                CsTeam victimTeam = victim.Team;
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                foreach (var player in Utilities.GetPlayers())
                {
                    if (player != null && player.IsValid && player.IsBot && player.PawnIsAlive && player.Team == victimTeam && player.Slot != victim.Slot)
                    {
                        var pPawn = player.PlayerPawn?.Value;
                        if (pPawn != null && pPawn.IsValid && pPawn.AbsOrigin != null)
                        {
                            float dist = (victimPos - pPawn.AbsOrigin).Length();

                            if (dist < 1500.0f)
                            {
                                _holdUntilSec.Remove(player.Slot); // Nearby death breaks passive hold
                            }

                            if (EnableTradeFrags && dist < 1400.0f)
                            {
                                _refragAggressionUntil[player.Slot] = nowMs + 1800;
                                if (!_combatStates.ContainsKey(player.Slot))
                                {
                                    _combatStates[player.Slot] = new BotCombatMovement(player.Slot);
                                }

                                if (EnableContextualPeeks && killerPos != null && !_peekStates.ContainsKey(player.Slot))
                                {
                                    int dir = (Random.Shared.Next(0, 2) == 0) ? 1 : -1;
                                    _peekStates[player.Slot] = new BotPeekState(player.Slot, PeekStyle.FerrariPrefire, dir, killerPos);
                                }
                            }

                            if (EnableTacticalThreatFacing && dist < 2000.0f)
                            {
                                Vector threatSpot = killerPos ?? victimPos;
                                _threatMemories[player.Slot] = new BotThreatMemory(threatSpot, nowSec + 4.5f, ThreatType.TeammateDeath, 1.2f);
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
            if (_fakeDefuseDone.TryGetValue(slot, out bool done) && done)
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

            if (livingCTs == 1 && livingTs <= 2)
            {
                _fakeDefuseDone[slot] = true;
                var api = GetApi();
                if (api != null)
                {
                    AddTimer(0.26f, () =>
                    {
                        try
                        {
                            if (defuser.IsValid && defuser.PawnIsAlive)
                            {
                                api.SwitchBotWeapon(slot, 42);
                                AddTimer(0.12f, () =>
                                {
                                    try
                                    {
                                        if (defuser.IsValid && defuser.PawnIsAlive)
                                        {
                                            api.SwitchBotWeapon(slot, 16);
                                            api.SwitchBotWeapon(slot, 60);
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

            return HookResult.Continue;
        }

        private void OnTick()
        {
            var api = GetApi();
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float nowSec = Server.CurrentTime;
            const float dt = 1.0f / 64.0f;
            _tickCount++;

            int ctAlive = 0;
            int tAlive = 0;
            var livingBots = new List<CCSPlayerController>();
            var livingPlayers = new List<CCSPlayerController>();

            foreach (var p in Utilities.GetPlayers())
            {
                if (p != null && p.IsValid && p.PawnIsAlive)
                {
                    if (p.Team == CsTeam.CounterTerrorist) ctAlive++;
                    else if (p.Team == CsTeam.Terrorist) tAlive++;

                    livingPlayers.Add(p);
                    if (p.IsBot) livingBots.Add(p);
                }
            }

            // 1. Double Peek Coordination
            if (EnableCoordinatedDoublePeeks && livingBots.Count >= 2 && (_tickCount % 12 == 0))
            {
                for (int i = 0; i < livingBots.Count; i++)
                {
                    for (int j = i + 1; j < livingBots.Count; j++)
                    {
                        var b1 = livingBots[i];
                        var b2 = livingBots[j];
                        if (b1.Team != b2.Team) continue;

                        var p1 = b1.PlayerPawn?.Value;
                        var p2 = b2.PlayerPawn?.Value;
                        if (p1 == null || p2 == null || p1.AbsOrigin == null || p2.AbsOrigin == null) continue;

                        float distBetweenBots = (p1.AbsOrigin - p2.AbsOrigin).Length();
                        if (distBetweenBots < 520.0f)
                        {
                            if (_threatMemories.TryGetValue(b1.Slot, out var threat1) && _threatMemories.TryGetValue(b2.Slot, out var threat2))
                            {
                                float distToThreat = (p1.AbsOrigin - threat1.Position).Length();
                                if (distToThreat > 250.0f && distToThreat < 1100.0f && !_doublePeekAssigned.ContainsKey(b1.Slot) && !_doublePeekAssigned.ContainsKey(b2.Slot))
                                {
                                    _doublePeekAssigned[b1.Slot] = nowMs + 2000;
                                    _doublePeekAssigned[b2.Slot] = nowMs + 2000;

                                    _peekStates[b1.Slot] = new BotPeekState(b1.Slot, PeekStyle.FerrariPrefire, 1, threat1.Position);

                                    AddTimer(0.24f, () =>
                                    {
                                        try
                                        {
                                            if (b2.IsValid && b2.PawnIsAlive && !_peekStates.ContainsKey(b2.Slot))
                                            {
                                                _peekStates[b2.Slot] = new BotPeekState(b2.Slot, PeekStyle.FerrariPrefire, -1, threat2.Position);
                                            }
                                        }
                                        catch { }
                                    });
                                }
                            }
                        }
                    }
                }
            }

            // 2. Footstep Sound Awareness
            if (EnableTacticalThreatFacing && (_tickCount % 6 == 0))
            {
                foreach (var enemy in livingPlayers)
                {
                    var ePawn = enemy.PlayerPawn?.Value;
                    if (ePawn == null || !ePawn.IsValid) continue;

                    Vector? ePos = ePawn.AbsOrigin;
                    Vector? eVel = ePawn.AbsVelocity;
                    if (ePos == null || eVel == null) continue;

                    float speed = MathF.Sqrt(eVel.X * eVel.X + eVel.Y * eVel.Y);
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

                    if (!isSilent && speed > 140.0f)
                    {
                        foreach (var bot in livingBots)
                        {
                            if (bot.Team == enemy.Team || bot.Slot == enemy.Slot) continue;

                            var bPawn = bot.PlayerPawn?.Value;
                            if (bPawn == null || !bPawn.IsValid || bPawn.AbsOrigin == null) continue;

                            float dist = (ePos - bPawn.AbsOrigin).Length();
                            if (dist < 720.0f)
                            {
                                int botSlot = bot.Slot;
                                if (!_threatMemories.TryGetValue(botSlot, out var existing) || existing.Priority <= 0.7f || nowSec > existing.ExpireTime)
                                {
                                    _threatMemories[botSlot] = new BotThreatMemory(ePos, nowSec + 2.0f, ThreatType.Footsteps, 0.7f);
                                }

                                if (dist < 600.0f)
                                {
                                    _holdUntilSec.Remove(botSlot); // Close footsteps break passive hold
                                }
                            }
                        }
                    }
                }
            }

            // 3. Process Contextual Peeks
            if (EnableContextualPeeks && _peekStates.Count > 0 && api != null)
            {
                var peekFinished = new List<int>();
                foreach (var kvp in _peekStates)
                {
                    int slot = kvp.Key;
                    var peek = kvp.Value;
                    long elapsed = nowMs - peek.StepStart;

                    if (peek.Style == PeekStyle.ShoulderJiggle)
                    {
                        if (peek.Step == 0)
                        {
                            ulong btn = (peek.Direction > 0) ? IN_MOVERIGHT : IN_MOVELEFT;
                            api.InjectUsercmd(slot, btn, 40);
                            if (elapsed >= 75)
                            {
                                peek.Step = 1;
                                peek.StepStart = nowMs;
                            }
                        }
                        else if (peek.Step == 1)
                        {
                            ulong counterBtn = (peek.Direction > 0) ? IN_MOVELEFT : IN_MOVERIGHT;
                            api.InjectUsercmd(slot, counterBtn, 45);
                            if (elapsed >= 75)
                            {
                                peekFinished.Add(slot);
                            }
                        }
                    }
                    else if (peek.Style == PeekStyle.FerrariPrefire)
                    {
                        if (peek.Step == 0)
                        {
                            ulong btn = (peek.Direction > 0) ? IN_MOVERIGHT : IN_MOVELEFT;
                            api.InjectUsercmd(slot, btn, 45);
                            if (elapsed >= 180)
                            {
                                peek.Step = 1;
                                peek.StepStart = nowMs;
                            }
                        }
                        else if (peek.Step == 1)
                        {
                            ulong counterBtn = (peek.Direction > 0) ? IN_MOVELEFT : IN_MOVERIGHT;
                            api.InjectUsercmd(slot, counterBtn, 40);
                            if (elapsed >= 45)
                            {
                                peek.Step = 2;
                                peek.StepStart = nowMs;
                            }
                        }
                        else if (peek.Step == 2)
                        {
                            api.InjectUsercmd(slot, IN_ATTACK, 90);
                            if (elapsed >= 120)
                            {
                                peekFinished.Add(slot);
                            }
                        }
                    }
                    else if (peek.Style == PeekStyle.SilentShift)
                    {
                        api.InjectUsercmd(slot, IN_SPEED, 60);
                        ulong btn = (peek.Direction > 0) ? IN_MOVERIGHT : IN_MOVELEFT;
                        api.InjectUsercmd(slot, btn, 45);
                        if (elapsed >= 250)
                        {
                            peekFinished.Add(slot);
                        }
                    }
                }

                foreach (int s in peekFinished)
                {
                    _peekStates.Remove(s);
                }
            }

            // 4. Process Distance-Based Combat Discipline & Refined Subtick Counter-Strafing
            if (EnableAdadStrafe && _combatStates.Count > 0 && api != null)
            {
                var toRemove = new List<int>();
                var slots = new List<int>(_combatStates.Keys);

                foreach (int slot in slots)
                {
                    var state = _combatStates[slot];

                    if (nowMs - state.LastCombatTime > 2200)
                    {
                        toRemove.Add(slot);
                        continue;
                    }

                    long elapsed = nowMs - state.PhaseStart;
                    float dist = state.TargetDistance;

                    // A: CLOSE RANGE (< 400 units): SPRAY COMMIT WITH RECOIL COMPENSATION
                    if (EnableDistanceCombatDiscipline && dist < 400.0f)
                    {
                        state.SprayTicks++;
                        api.InjectUsercmd(slot, IN_DUCK | IN_ATTACK, 60);
                        continue;
                    }

                    // Get bot pawn velocity for subtick speed cutoff
                    float speed2D = 250.0f;
                    foreach (var b in livingBots)
                    {
                        if (b.Slot == slot)
                        {
                            var bPawn = b.PlayerPawn?.Value;
                            if (bPawn != null && bPawn.AbsVelocity != null)
                            {
                                Vector v = bPawn.AbsVelocity;
                                speed2D = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
                            }
                            break;
                        }
                    }

                    // B: LONG RANGE (> 900 units): 2-SHOT TAP & FAST COUNTER-STRAFE
                    if (EnableDistanceCombatDiscipline && dist > 900.0f)
                    {
                        if (state.Phase == 0) // Strafe
                        {
                            ulong moveBtn = (state.Direction > 0) ? IN_MOVERIGHT : IN_MOVELEFT;
                            api.InjectUsercmd(slot, moveBtn, 45);
                            if (elapsed >= 160)
                            {
                                state.Phase = 1;
                                state.PhaseStart = nowMs;
                            }
                        }
                        else if (state.Phase == 1) // Counter-strafe with Subtick Velocity Cutoff
                        {
                            ulong counterBtn = (state.Direction > 0) ? IN_MOVELEFT : IN_MOVERIGHT;
                            api.InjectUsercmd(slot, counterBtn, 40);

                            // Subtick Stop: cut counter-strafe when horizontal speed < 78 u/s (< 34% maxspeed)
                            if (EnableSubtickStop && speed2D < 78.0f || elapsed >= 45)
                            {
                                state.Phase = 2;
                                state.PhaseStart = nowMs;
                            }
                        }
                        else if (state.Phase == 2) // Quick stationary tap
                        {
                            api.InjectUsercmd(slot, IN_ATTACK, 60);
                            if (elapsed >= 100)
                            {
                                state.Direction = -state.Direction;
                                state.Phase = 0;
                                state.PhaseStart = nowMs;
                            }
                        }
                        continue;
                    }

                    // C: MID RANGE (400 - 900 units): DONK-STYLE ADAD BURST + SUBTICK STOP
                    if (state.Phase == 0)
                    {
                        ulong moveBtn = (state.Direction > 0) ? IN_MOVERIGHT : IN_MOVELEFT;
                        api.InjectUsercmd(slot, moveBtn, 45);

                        if (elapsed >= 190)
                        {
                            state.Phase = 1;
                            state.PhaseStart = nowMs;
                        }
                    }
                    else if (state.Phase == 1)
                    {
                        ulong counterBtn = (state.Direction > 0) ? IN_MOVELEFT : IN_MOVERIGHT;
                        api.InjectUsercmd(slot, counterBtn, 40);

                        // Subtick Stop Cutoff: < 78 units/s instant transition
                        if (EnableSubtickStop && speed2D < 78.0f || elapsed >= 45)
                        {
                            state.Phase = 2;
                            state.PhaseStart = nowMs;
                            state.DidCrouchTap = false;
                        }
                    }
                    else if (state.Phase == 2)
                    {
                        if (!state.DidCrouchTap && Random.Shared.Next(0, 100) < 25)
                        {
                            api.InjectUsercmd(slot, IN_DUCK, 80);
                            state.DidCrouchTap = true;
                        }

                        if (elapsed >= 140)
                        {
                            state.Direction = -state.Direction;
                            state.Phase = 0;
                            state.PhaseStart = nowMs;
                        }
                    }
                }

                foreach (int s in toRemove)
                {
                    _combatStates.Remove(s);
                }
            }

            // 5. Per-Bot Loop: Anti-Rush Holds, Jump Suppression, Aim Curves & Spray Compensation
            foreach (var bot in livingBots)
            {
                int slot = bot.Slot;
                var pawn = bot.PlayerPawn?.Value;
                if (pawn == null || !pawn.IsValid)
                    continue;

                Vector? botOrigin = pawn.AbsOrigin;
                if (botOrigin == null)
                    continue;

                CCSBot? ccsBot = pawn.Bot;
                int hp = pawn.Health;

                // Check flashbang status (Zero-conflict coordination with BotState flash avoidance)
                bool isFlashed = (pawn.FlashDuration > 0.35f);

                // Check active weapon ammo
                int clip1 = 30;
                var weaponServices = pawn.WeaponServices;
                if (weaponServices != null && weaponServices.ActiveWeapon != null && weaponServices.ActiveWeapon.IsValid)
                {
                    var curWeapon = weaponServices.ActiveWeapon.Value;
                    if (curWeapon != null && curWeapon.IsValid)
                    {
                        clip1 = curWeapon.Clip1;
                    }
                }

                // Check distance to closest visible enemy
                float closestEnemyDist = 9999f;
                Vector? closestEnemyPos = null;
                foreach (var enemy in livingPlayers)
                {
                    if (enemy.Team == bot.Team || enemy.Slot == bot.Slot) continue;
                    var ePawn = enemy.PlayerPawn?.Value;
                    if (ePawn != null && ePawn.IsValid && ePawn.AbsOrigin != null)
                    {
                        float d = (ePawn.AbsOrigin - botOrigin).Length();
                        if (d < closestEnemyDist)
                        {
                            closestEnemyDist = d;
                            closestEnemyPos = ePawn.AbsOrigin;
                        }
                    }
                }

                if (_combatStates.TryGetValue(slot, out var combatState))
                {
                    combatState.TargetDistance = closestEnemyDist;
                }

                // --- FEATURE 1: ANTI-RUSH "NO SHIU" HOLD HANDLING ---
                bool isHolding = false;
                if (EnableAntiRushTempos && _holdUntilSec.TryGetValue(slot, out float holdUntil))
                {
                    if (nowSec < holdUntil && !_isBombPlanted)
                    {
                        // Check if hold should break immediately: enemy spotted or in combat
                        if ((ccsBot != null && ccsBot.IsEnemyVisible) || _combatStates.ContainsKey(slot))
                        {
                            _holdUntilSec.Remove(slot);
                        }
                        else
                        {
                            isHolding = true;
                            if (api != null)
                            {
                                // Bot holds angle silently in Shift, damping forward sprint
                                api.InjectUsercmd(slot, IN_SPEED, 60);
                            }
                        }
                    }
                    else
                    {
                        _holdUntilSec.Remove(slot);
                    }
                }

                // --- FEATURE 2: COMBAT JUMP SUPPRESSION ---
                bool inCombat = _combatStates.ContainsKey(slot);
                if (!inCombat && ccsBot != null)
                {
                    inCombat = ccsBot.IsAttacking || ccsBot.IsEnemyVisible;
                }
                if (!inCombat && _lastDamageTime.TryGetValue(slot, out long lastDmg))
                {
                    inCombat = (nowMs - lastDmg < 2500);
                }

                if (EnableJumpSuppressionInCombat && inCombat)
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

                // --- FEATURE 3: TACTICAL RETREAT (Low HP / Empty Clip) ---
                bool isRetreating = false;
                if (EnableTacticalRetreat && (hp <= 25 || clip1 <= 1) && closestEnemyDist > 300.0f)
                {
                    if (_retreatUntil.TryGetValue(slot, out long retreatUntil) && nowMs < retreatUntil)
                    {
                        isRetreating = true;
                        if (api != null)
                        {
                            api.InjectUsercmd(slot, IN_BACK | IN_SPEED, 60);
                            if (clip1 <= 0)
                            {
                                api.SwitchBotWeapon(slot, 9001);
                            }
                        }
                    }
                }

                // --- FEATURE 4: TACTICAL THREAT FACING WITH HUMAN AIM SMOOTHING (EASE-OUT) ---
                bool hasDirectEnemyInSight = (ccsBot != null && ccsBot.IsEnemyVisible);
                bool didFaceThreat = false;

                // Only adjust aim if bot is not flash-blinded (respects BotState flash avoidance)
                if (!isFlashed)
                {
                    if (hasDirectEnemyInSight && ccsBot != null)
                    {
                        Vector? lastEnemy = ccsBot.LastEnemyPosition;
                        if (lastEnemy != null)
                        {
                            _threatMemories[slot] = new BotThreatMemory(lastEnemy, nowSec + 4.0f, ThreatType.VisualSight, 0.9f);
                        }

                        // Close-quarters spray compensation on visible target
                        if (EnableSprayCompensation && closestEnemyDist < 400.0f && _combatStates.TryGetValue(slot, out var cState))
                        {
                            QAngle? eyeAngles = pawn.EyeAngles;
                            if (eyeAngles != null && cState.SprayTicks > 3)
                            {
                                float pullDown = (cState.SprayTicks < 14) ? 3.6f : 1.2f;
                                eyeAngles.X = Math.Clamp(eyeAngles.X + pullDown * dt, -20.0f, 22.0f);
                            }
                        }
                    }
                    else if (EnableTacticalThreatFacing && _threatMemories.TryGetValue(slot, out var threat))
                    {
                        if (nowSec < threat.ExpireTime)
                        {
                            Vector botEye = new Vector(botOrigin.X, botOrigin.Y, botOrigin.Z + (pawn.ViewOffset?.Z ?? 64.0f));
                            float dx = threat.Position.X - botEye.X;
                            float dy = threat.Position.Y - botEye.Y;
                            float dz = (threat.Position.Z + 55.0f) - botEye.Z;
                            float dist2D = MathF.Sqrt(dx * dx + dy * dy);

                            if (dist2D > 20.0f)
                            {
                                float targetYaw = MathF.Atan2(dy, dx) * 180.0f / MathF.PI;
                                float targetPitch = -MathF.Atan2(dz, dist2D) * 180.0f / MathF.PI;
                                targetPitch = Math.Clamp(targetPitch, -22.0f, 22.0f);

                                if (ccsBot != null)
                                {
                                    ccsBot.EyeAnglesUnderPathFinderControl = false;
                                    ccsBot.InhibitLookAroundTimestamp = nowSec + 0.35f;

                                    var lookSpot = ccsBot.LookAtSpot;
                                    if (lookSpot != null)
                                    {
                                        lookSpot.X = threat.Position.X;
                                        lookSpot.Y = threat.Position.Y;
                                        lookSpot.Z = threat.Position.Z + 55.0f;
                                    }
                                    ccsBot.LookAtSpotDuration = 0.35f;
                                    ccsBot.LookAtSpotTimestamp = nowSec;
                                    ccsBot.LookPitch = targetPitch;
                                    ccsBot.LookYaw = targetYaw;

                                    var aimGoal = ccsBot.AimGoal;
                                    if (aimGoal != null)
                                    {
                                        aimGoal.X = targetPitch;
                                        aimGoal.Y = targetYaw;
                                    }
                                }

                                // HUMAN AIM SMOOTHING: Ease-Out Sigmoidal Curve (Fast flick, smooth deceleration onto target)
                                QAngle? eyeAngles = pawn.EyeAngles;
                                if (eyeAngles != null)
                                {
                                    float currentYaw = eyeAngles.Y;
                                    float deltaYaw = NormalizeAngle(targetYaw - currentYaw);
                                    float absDelta = Math.Abs(deltaYaw);

                                    float speedFactor;
                                    if (!EnableHumanAimSmoothing || absDelta > 50.0f)
                                    {
                                        speedFactor = 1.0f; // Fast initial turn
                                    }
                                    else
                                    {
                                        // Ease-out deceleration curve
                                        float norm = absDelta / 50.0f;
                                        speedFactor = 0.35f + 0.65f * MathF.Sin(norm * MathF.PI * 0.5f);
                                    }

                                    float maxTurn = 520.0f * speedFactor * dt;
                                    float step = Math.Clamp(deltaYaw, -maxTurn, maxTurn);
                                    eyeAngles.Y = NormalizeAngle(currentYaw + step);

                                    float currentPitch = eyeAngles.X;
                                    float deltaPitch = Math.Clamp(targetPitch - currentPitch, -160.0f * dt, 160.0f * dt);
                                    eyeAngles.X = Math.Clamp(currentPitch + deltaPitch, -25.0f, 25.0f);
                                }

                                didFaceThreat = true;

                                // Organic Contextual Peek Selection
                                if (EnableContextualPeeks && !_peekStates.ContainsKey(slot) && dist2D > 320.0f && dist2D < 680.0f && !isRetreating && !isHolding)
                                {
                                    int teamAlive = (bot.Team == CsTeam.CounterTerrorist) ? ctAlive : tAlive;
                                    int dir = (Random.Shared.Next(0, 2) == 0) ? 1 : -1;

                                    if (teamAlive == 1 || CurrentTempo == RoundTempo.DefaultHold || Random.Shared.Next(0, 100) < 35)
                                    {
                                        _peekStates[slot] = new BotPeekState(slot, PeekStyle.SilentShift, dir, threat.Position);
                                    }
                                    else
                                    {
                                        _peekStates[slot] = new BotPeekState(slot, PeekStyle.FerrariPrefire, dir, threat.Position);
                                    }
                                }
                            }
                        }
                        else
                        {
                            _threatMemories.Remove(slot);
                        }
                    }

                    // --- FEATURE 5: MOVEMENT HEADING ALIGNMENT (No Walking Backwards) ---
                    if (!didFaceThreat && !hasDirectEnemyInSight && !isRetreating && EnableMovementHeading)
                    {
                        Vector? vel = pawn.AbsVelocity;
                        if (vel != null)
                        {
                            float speed2D = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y);
                            if (speed2D > 65.0f && !pawn.IsDefusing)
                            {
                                float moveYaw = MathF.Atan2(vel.Y, vel.X) * 180.0f / MathF.PI;
                                QAngle? eyeAngles = pawn.EyeAngles;
                                if (eyeAngles != null)
                                {
                                    float currentYaw = eyeAngles.Y;
                                    float angleDiff = Math.Abs(NormalizeAngle(currentYaw - moveYaw));

                                    if (angleDiff > 65.0f)
                                    {
                                        if (ccsBot != null)
                                        {
                                            ccsBot.InhibitLookAroundTimestamp = nowSec + 0.25f;
                                        }

                                        float deltaYaw = NormalizeAngle(moveYaw - currentYaw);
                                        float step = Math.Clamp(deltaYaw, -320.0f * dt, 320.0f * dt);
                                        eyeAngles.Y = NormalizeAngle(currentYaw + step);

                                        if (ccsBot != null)
                                        {
                                            ccsBot.LookYaw = eyeAngles.Y;
                                            var aimGoal = ccsBot.AimGoal;
                                            if (aimGoal != null) aimGoal.Y = eyeAngles.Y;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 6. Tactical Silent Walk (Shift-Walk)
            if (EnableShiftWalk && api != null)
            {
                Vector? c4Pos = null;
                var c4Entities = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4");
                foreach (var c4 in c4Entities)
                {
                    if (c4 != null && c4.IsValid && c4.BombTicking)
                    {
                        c4Pos = c4.AbsOrigin;
                        break;
                    }
                }

                foreach (var bot in livingBots)
                {
                    var pPawn = bot.PlayerPawn?.Value;
                    if (pPawn == null || !pPawn.IsValid || pPawn.AbsOrigin == null)
                        continue;

                    int slot = bot.Slot;

                    if (_combatStates.ContainsKey(slot) || _peekStates.ContainsKey(slot))
                        continue;

                    bool shouldShift = false;
                    int teamAlive = (bot.Team == CsTeam.CounterTerrorist) ? ctAlive : tAlive;
                    if (teamAlive == 1)
                    {
                        shouldShift = true;
                    }

                    if (!shouldShift && bot.Team == CsTeam.CounterTerrorist && c4Pos != null)
                    {
                        float distToC4 = (pPawn.AbsOrigin - c4Pos).Length();
                        if (distToC4 > 350.0f && distToC4 < 1600.0f)
                        {
                            shouldShift = true;
                        }
                    }

                    if (shouldShift)
                    {
                        api.InjectUsercmd(slot, IN_SPEED, 60);
                    }
                }
            }
        }

        private static float NormalizeAngle(float angle)
        {
            angle %= 360.0f;
            if (angle > 180.0f)
            {
                angle -= 360.0f;
            }
            if (angle < -180.0f)
            {
                angle += 360.0f;
            }
            return angle;
        }
    }
}
