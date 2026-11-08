using System;
using System.Collections.Generic;
// using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Permissions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;
using BotControllerApi;

namespace NadeSystem;

public class Vec3
{
	[JsonPropertyName("x")]
	public float X { get; set; }

	[JsonPropertyName("y")]
	public float Y { get; set; }

	[JsonPropertyName("z")]
	public float Z { get; set; }
}
public class GrenadeData
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = "";

	[JsonPropertyName("mapName")]
	public string MapName { get; set; } = "";

	[JsonPropertyName("grenadeType")]
	public string GrenadeType { get; set; } = "";

	[JsonPropertyName("projectilePosition")]
	public Vec3 ProjectilePosition { get; set; } = new Vec3();

	[JsonPropertyName("projectileVelocity")]
	public Vec3 ProjectileVelocity { get; set; } = new Vec3();

	[JsonPropertyName("landingPosition")]
	public Vec3 LandingPosition { get; set; } = new Vec3();

	[JsonPropertyName("description")]
	public string Description { get; set; } = "";

	[JsonIgnore]
	public string TeamTag { get; set; } = "";

	[JsonIgnore]
	public bool AutoTagged { get; set; }

	[JsonIgnore]
	public float ZoneX => ProjectilePosition.X;

	[JsonIgnore]
	public float ZoneY => ProjectilePosition.Y;

	[JsonIgnore]
	public float ZoneZ => ProjectilePosition.Z;

	[JsonIgnore]
	public float ZoneRadius
	{
		get
		{
			if (string.Equals(GrenadeType, "smoke", StringComparison.OrdinalIgnoreCase))
			{
				return 115f;
			}
			return 95f;
		}
	}
}
public class CooldownEntry
{
	public string GrenadeId { get; set; } = "";

	public float ExpiresAt { get; set; }
}
public class RoundCounter
{
	public int Flash { get; set; }

	public int Smoke { get; set; }

	public int HE { get; set; }

	public int Molotov { get; set; }
}
public class NadeSystemPlugin : BasePlugin
{
	private readonly record struct SoundPoint(float X, float Y, float Z, float Timestamp, bool IsSniper);

	private readonly record struct ActiveWindup(int Slot, float Yaw, float Pitch, float ExpiresAt);

	private readonly Dictionary<int, ActiveWindup> _activeWindups = new Dictionary<int, ActiveWindup>();

	private const float GRENADE_EFF_GRAVITY = 360f;

	private const float MAX_THROW_SPEED = 1040f;

	private const float MIN_THROW_SPEED = 240f;

	private List<GrenadeData> _mapNades = new List<GrenadeData>();

	private string _botNadesMode = "normal";

	private readonly List<GrenadeData> _db = new List<GrenadeData>();

	private readonly List<CooldownEntry> _cooldowns = new List<CooldownEntry>();

	private readonly HashSet<uint> _replayBots = new HashSet<uint>();

	private readonly Dictionary<string, float> _pendingWindupLineups = new Dictionary<string, float>();

	private readonly HashSet<uint> _smokeCooldownBots = new HashSet<uint>();

	private int _tick;

	private bool _roundOver;

	private float _freezeEndTime;

	private readonly Dictionary<uint, int> _roundSpendPerBot = new Dictionary<uint, int>();

	private readonly HashSet<uint> _poorBots = new HashSet<uint>();

	private readonly Dictionary<string, float> _probFailCooldown = new Dictionary<string, float>();

	private readonly Dictionary<uint, float> _botFlashImmunityUntil = new Dictionary<uint, float>();

	private readonly Dictionary<uint, float> _dynamicNadeCooldownByBot = new Dictionary<uint, float>();

	private static readonly PluginCapability<CRayTraceInterface> _rayTraceCapability = new PluginCapability<CRayTraceInterface>("raytrace:craytraceinterface");

	private static readonly PluginCapability<IBotControllerApi> _controllerCap = new PluginCapability<IBotControllerApi>("botcontroller:api");

	private IBotControllerApi? GetControllerApi()
	{
		try
		{
			return _controllerCap.Get();
		}
		catch
		{
			return null;
		}
	}

	private Vector _tSpawnCenter = new Vector(0f, 0f, 0f);
	private Vector _ctSpawnCenter = new Vector(0f, 0f, 0f);
	private bool _spawnsResolved = false;
	private bool _spawnsFromEntities = false;

	private void ResolveMapSpawns()
	{
		_spawnsResolved = false;
		_spawnsFromEntities = false;
		try
		{
			var tSpawns = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_terrorist").ToList();
			var ctSpawns = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_counterterrorist").ToList();

			if (tSpawns.Count > 0 && ctSpawns.Count > 0)
			{
				float tx = 0, ty = 0, tz = 0;
				foreach (var s in tSpawns)
				{
					if (s.AbsOrigin != null) { tx += s.AbsOrigin.X; ty += s.AbsOrigin.Y; tz += s.AbsOrigin.Z; }
				}
				_tSpawnCenter = new Vector(tx / tSpawns.Count, ty / tSpawns.Count, tz / tSpawns.Count);

				float cx = 0, cy = 0, cz = 0;
				foreach (var s in ctSpawns)
				{
					if (s.AbsOrigin != null) { cx += s.AbsOrigin.X; cy += s.AbsOrigin.Y; cz += s.AbsOrigin.Z; }
				}
				_ctSpawnCenter = new Vector(cx / ctSpawns.Count, cy / ctSpawns.Count, cz / ctSpawns.Count);
				_spawnsResolved = true;
				_spawnsFromEntities = true;
				Logger.LogInformation("[NadeSystem] Dynamic map spawns resolved: T({TX:F0},{TY:F0}) CT({CX:F0},{CY:F0})", _tSpawnCenter.X, _tSpawnCenter.Y, _ctSpawnCenter.X, _ctSpawnCenter.Y);
				return;
			}
		}
		catch { }

		string map = (Server.MapName ?? "").ToLowerInvariant();
		if (map.Contains("mirage"))
		{
			_tSpawnCenter = new Vector(1200f, -200f, -100f);
			_ctSpawnCenter = new Vector(-1680f, -1950f, -160f);
			_spawnsResolved = true;
		}
		else if (map.Contains("inferno"))
		{
			_tSpawnCenter = new Vector(-1800f, 380f, 50f);
			_ctSpawnCenter = new Vector(2400f, 1800f, 160f);
			_spawnsResolved = true;
		}
		else if (map.Contains("dust2"))
		{
			_tSpawnCenter = new Vector(0f, -700f, 100f);
			_ctSpawnCenter = new Vector(-1000f, 2200f, 50f);
			_spawnsResolved = true;
		}
		else if (map.Contains("anubis"))
		{
			_tSpawnCenter = new Vector(-700f, -1500f, 50f);
			_ctSpawnCenter = new Vector(200f, 2200f, 50f);
			_spawnsResolved = true;
		}
		else if (map.Contains("ancient"))
		{
			_tSpawnCenter = new Vector(-2000f, 0f, 50f);
			_ctSpawnCenter = new Vector(1800f, 0f, 50f);
			_spawnsResolved = true;
		}
		else if (map.Contains("nuke"))
		{
			_tSpawnCenter = new Vector(-3200f, -900f, -400f);
			_ctSpawnCenter = new Vector(3200f, -900f, -400f);
			_spawnsResolved = true;
		}
		else if (map.Contains("vertigo"))
		{
			_tSpawnCenter = new Vector(-1400f, -1200f, 11700f);
			_ctSpawnCenter = new Vector(-1000f, 600f, 11700f);
			_spawnsResolved = true;
		}
		else if (map.Contains("overpass"))
		{
			_tSpawnCenter = new Vector(-2200f, 400f, 480f);
			_ctSpawnCenter = new Vector(-2600f, 5200f, 450f);
			_spawnsResolved = true;
		}
	}

	private static bool HasGrenadeInInventory(CCSPlayerPawn pawn, string gtype, out string weaponName, out int itemDefIndex)
	{
		weaponName = "";
		itemDefIndex = 0;
		if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) return false;

		string targetName = gtype.ToLowerInvariant() switch
		{
			"smoke" => "weapon_smokegrenade",
			"flash" => "weapon_flashbang",
			"he" => "weapon_hegrenade",
			"molotov" => "weapon_molotov",
			"incgrenade" => "weapon_incgrenade",
			"decoy" => "weapon_decoy",
			_ => ""
		};

		foreach (var handle in pawn.WeaponServices.MyWeapons)
		{
			var weapon = handle.Value;
			if (weapon == null || !weapon.IsValid) continue;
			string designer = weapon.DesignerName ?? "";
			if (string.Equals(designer, targetName, StringComparison.OrdinalIgnoreCase) ||
				(targetName == "weapon_molotov" && string.Equals(designer, "weapon_incgrenade", StringComparison.OrdinalIgnoreCase)) ||
				(targetName == "weapon_incgrenade" && string.Equals(designer, "weapon_molotov", StringComparison.OrdinalIgnoreCase)))
			{
				if (string.Equals(designer, "weapon_flashbang", StringComparison.OrdinalIgnoreCase))
				{
					byte? ammoType = weapon.VData?.PrimaryAmmoType;
					if (ammoType.HasValue && ammoType.Value < 32 && pawn.WeaponServices.Ammo[ammoType.Value] <= 0)
					{
						continue;
					}
				}
				weaponName = designer;
				itemDefIndex = designer switch
				{
					"weapon_flashbang" => 43,
					"weapon_hegrenade" => 44,
					"weapon_smokegrenade" => 45,
					"weapon_molotov" => 46,
					"weapon_decoy" => 47,
					"weapon_incgrenade" => 48,
					_ => 45
				};
				return true;
			}
		}
		return false;
	}

	private static void ConsumeGrenade(CCSPlayerController bot, string weaponDesignerName)
	{
		CCSPlayerPawn? pawn = bot.PlayerPawn?.Value;
		if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) return;

		foreach (var handle in pawn.WeaponServices.MyWeapons)
		{
			var weapon = handle.Value;
			if (weapon == null || !weapon.IsValid) continue;
			if (string.Equals(weapon.DesignerName, weaponDesignerName, StringComparison.OrdinalIgnoreCase))
			{
				if (string.Equals(weaponDesignerName, "weapon_flashbang", StringComparison.OrdinalIgnoreCase))
				{
					byte? ammoType = weapon.VData?.PrimaryAmmoType;
					if (ammoType.HasValue && ammoType.Value < 32)
					{
						Span<ushort> ammo = pawn.WeaponServices.Ammo;
						ushort currentAmmo = ammo[ammoType.Value];
						if (currentAmmo > 1)
						{
							ammo[ammoType.Value] = (ushort)(currentAmmo - 1);
							Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pWeaponServices", Schema.GetSchemaOffset("CPlayer_WeaponServices", "m_iAmmo"));
							return;
						}
					}
					else if (weapon.Clip1 > 1)
					{
						weapon.Clip1--;
						Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_iClip1");
						return;
					}
				}
				Utilities.RemoveItemByDesignerName(bot, weaponDesignerName);
				return;
			}
		}
	}

	private bool HasForwardClearance(Vector eyePos, float yawDeg, float pitchDeg, float checkDist = 36f)
	{
		try
		{
			CRayTraceInterface rt = _rayTraceCapability.Get();
			if (rt == null) return true;
			float yawRad = yawDeg * (MathF.PI / 180f);
			float pitchRad = -pitchDeg * (MathF.PI / 180f);
			float cosP = MathF.Cos(pitchRad);
			float dx = cosP * MathF.Cos(yawRad);
			float dy = cosP * MathF.Sin(yawRad);
			float dz = MathF.Sin(pitchRad);
			_traceA ??= new Vector(0f, 0f, 0f);
			_traceB ??= new Vector(0f, 0f, 0f);
			_traceA.X = eyePos.X; _traceA.Y = eyePos.Y; _traceA.Z = eyePos.Z;
			_traceB.X = eyePos.X + dx * checkDist;
			_traceB.Y = eyePos.Y + dy * checkDist;
			_traceB.Z = eyePos.Z + dz * checkDist;
			RayTraceAPI.TraceOptions opt = new RayTraceAPI.TraceOptions((InteractionLayers)2109441, (InteractionLayers)0, false);
			if (rt.TraceEndShape(_traceA, _traceB, null, opt, out RayTraceAPI.TraceResult res))
			{
				return res.Fraction >= 0.85f;
			}
			return true;
		}
		catch { return true; }
	}

	private void ReleaseBotThrowLocks(int slot, bool unlockWeapon = true)
	{
		_activeWindups.Remove(slot);
		if (slot < 0) return;
		var api = GetControllerApi();
		if (api != null)
		{
			api.Unlock(slot, LockKind.Aim);
			if (unlockWeapon)
			{
				api.Unlock(slot, LockKind.Weapon);
			}
		}
	}

	private void ReleaseAllThrowLocks()
	{
		if (_activeWindups.Count == 0) return;
		var api = GetControllerApi();
		if (api != null)
		{
			foreach (int s in _activeWindups.Keys)
			{
				if (s >= 0)
				{
					api.Unlock(s, LockKind.Aim);
					api.Unlock(s, LockKind.Weapon);
				}
			}
		}
		_activeWindups.Clear();
	}

	private void ProcessActiveWindups()
	{
		if (_activeWindups.Count == 0) return;
		float now = Server.CurrentTime;
		List<int>? expired = null;

		foreach (var kvp in _activeWindups)
		{
			ActiveWindup w = kvp.Value;
			if (now >= w.ExpiresAt)
			{
				expired ??= new List<int>(4);
				expired.Add(kvp.Key);
				continue;
			}
			CCSPlayerController? bot = Utilities.GetPlayerFromSlot(w.Slot);
			CCSPlayerPawn? pawn = bot?.PlayerPawn?.Value;
			if (bot == null || !bot.IsValid || !bot.PawnIsAlive || pawn == null || !pawn.IsValid)
			{
				expired ??= new List<int>(4);
				expired.Add(kvp.Key);
				continue;
			}
			if (pawn.AbsVelocity != null)
			{
				pawn.AbsVelocity.X *= 0.25f;
				pawn.AbsVelocity.Y *= 0.25f;
			}
			if (pawn.EyeAngles != null)
			{
				pawn.EyeAngles.Y = w.Yaw;
				pawn.EyeAngles.X = w.Pitch;
			}
			if (pawn.V_angle != null)
			{
				pawn.V_angle.Y = w.Yaw;
				pawn.V_angle.X = w.Pitch;
			}
			if (pawn.Bot != null)
			{
				pawn.Bot.ForwardSpeed = 0f;
				pawn.Bot.LeftSpeed = 0f;
				pawn.Bot.LookYaw = w.Yaw;
				pawn.Bot.LookPitch = w.Pitch;
			}
		}

		if (expired != null)
		{
			for (int i = 0; i < expired.Count; i++)
			{
				ReleaseBotThrowLocks(expired[i], unlockWeapon: true);
			}
		}
	}

	private bool HasReleaseLoS(float eyeX, float eyeY, float eyeZ, Vec3 release)
	{
		try
		{
			CRayTraceInterface rt = _rayTraceCapability.Get();
			if (rt == null) return true;
			_traceA ??= new Vector(0f, 0f, 0f);
			_traceB ??= new Vector(0f, 0f, 0f);
			_traceA.X = eyeX; _traceA.Y = eyeY; _traceA.Z = eyeZ;
			_traceB.X = release.X; _traceB.Y = release.Y; _traceB.Z = release.Z;
			RayTraceAPI.TraceOptions opt = new RayTraceAPI.TraceOptions((InteractionLayers)2109441, (InteractionLayers)0, false);
			RayTraceAPI.TraceResult res = default;
			if (rt.TraceEndShape(_traceA, _traceB, null, opt, out res))
			{
				return res.Fraction >= 0.88f;
			}
			return true;
		}
		catch { return true; }
	}

	private bool _defuseSmokeUsed;

	private bool _defuseFlashUsed;

	private bool _plantSmokeUsed;

	private readonly Dictionary<int, RoundCounter> _roundCountByTeam = new Dictionary<int, RoundCounter>();

	private readonly Dictionary<uint, RoundCounter> _roundCountByBot = new Dictionary<uint, RoundCounter>();

	private readonly Dictionary<uint, float> _botMolotovDmgStart = new Dictionary<uint, float>();

	private readonly Dictionary<int, float> _molotovEscapeSmokeCooldown = new Dictionary<int, float>();

	private readonly Dictionary<int, float> _retaliationCooldown = new Dictionary<int, float>();

	private readonly Dictionary<int, int> _earlySmokeCountByTeam = new Dictionary<int, int>();

	private readonly Dictionary<uint, HashSet<string>> _botInFlashZone = new Dictionary<uint, HashSet<string>>();

	private readonly Dictionary<uint, (float ExpiresAt, float Ratio)> _botFlashRatioWindow = new Dictionary<uint, (float, float)>();

	private readonly Dictionary<uint, List<SoundPoint>> _soundPoints = new Dictionary<uint, List<SoundPoint>>();

	private readonly Dictionary<uint, float> _botLastFireTime = new Dictionary<uint, float>();

	private readonly Dictionary<uint, float> _botLastHurtTime = new Dictionary<uint, float>();

	private Vector? _traceA;

	private Vector? _traceB;

	private string _loadedMapName = "";

	private static readonly float[] CandidateFlightTimes = new float[7] { 0.65f, 0.85f, 1.05f, 1.28f, 1.52f, 1.85f, 2.25f };

	private static readonly Dictionary<string, float> CooldownSec = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
	{
		["smoke"] = 18f,
		["flash"] = 4.5f,
		["he"] = 5.5f,
		["molotov"] = 10f,
		["decoy"] = 600f
	};

	private static readonly Dictionary<string, int> CostT = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
	{
		["flash"] = 200,
		["smoke"] = 300,
		["he"] = 300,
		["molotov"] = 400,
		["incgrenade"] = 400,
		["decoy"] = 0
	};

	private static readonly Dictionary<string, int> CostCT = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
	{
		["flash"] = 200,
		["smoke"] = 300,
		["he"] = 300,
		["molotov"] = 500,
		["incgrenade"] = 500,
		["decoy"] = 0
	};

	private static readonly MemoryFunctionWithReturn<nint, nint, nint, nint, nint, int, int, CSmokeGrenadeProjectile> _smokeCreate = new MemoryFunctionWithReturn<nint, nint, nint, nint, nint, int, int, CSmokeGrenadeProjectile>(RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "55 4C 89 C1 48 89 E5 41 57 49 89 FF 41 56 45 89 CE 41 55" : "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 56 41 57 48 83 EC 50 48 8B B4 24 90 00 00 00 4D 8B F8 4C 8B C2");

	private static readonly MemoryFunctionWithReturn<nint, nint, nint, nint, nint, int, CHEGrenadeProjectile> _heCreate = new MemoryFunctionWithReturn<nint, nint, nint, nint, nint, int, CHEGrenadeProjectile>(RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "55 4C 89 C1 48 89 E5 41 57 49 89 FF 41 56 49 89 D6 48 89 F2 48 89 FE 41 55" : "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 50 48 8B AC 24 80 00 00 00 49 8B F8 4C 8B C2");

	private static readonly MemoryFunctionWithReturn<nint, nint, nint, nint, nint, int, CMolotovProjectile> _molotovCreate = new MemoryFunctionWithReturn<nint, nint, nint, nint, nint, int, CMolotovProjectile>(RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "55 48 8D 05 ? ? ? ? 48 89 E5 41 57 41 56 41 55 41 54 49 89 FC 53 48 81 EC ? ? ? ? 4C 8D 35" : "48 8B C4 48 89 58 08 48 89 70 10 48 89 78 20 4C 89 40 18 55 41 54 41 55 41 56 41 57 48 8D 6C 24 90 48 81 EC 70 01 00 00");

	private static readonly FieldInfo? _serverMaxPlayersField =
		typeof(Server).GetField("_maxPlayers", BindingFlags.NonPublic | BindingFlags.Static);

	private static void ResetServerMaxPlayersCache()
	{
		try
		{
			_serverMaxPlayersField?.SetValue(null, null);
		}
		catch
		{
		}
	}

	public override string ModuleName => "NadeSystem";

	public override string ModuleVersion => "3.0.0";

	public override string ModuleAuthor => "Antigravity & ed0ard";

	private string DataDir => Path.Combine(((BasePlugin)this).ModuleDirectory, "grenades");

	public override void Load(bool hotReload)
	{
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Expected O, but got Unknown
		//IL_0123: Unknown result type (might be due to invalid IL or missing references)
		//IL_012d: Expected O, but got Unknown
		//IL_013f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0149: Expected O, but got Unknown
		Directory.CreateDirectory(DataDir);
		LoadDbFilesOnly();
		((BasePlugin)this).RegisterEventHandler<EventRoundStart>((GameEventHandler<EventRoundStart>)OnRoundStart, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventRoundFreezeEnd>((GameEventHandler<EventRoundFreezeEnd>)OnFreezeEnd, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventRoundEnd>((GameEventHandler<EventRoundEnd>)OnRoundEnd, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventPlayerDeath>((GameEventHandler<EventPlayerDeath>)OnPlayerDeath, (HookMode)1);
		((BasePlugin)this).RegisterListener<Listeners.OnTick>(OnTick);
		((BasePlugin)this).RegisterEventHandler<EventBombBegindefuse>((GameEventHandler<EventBombBegindefuse>)OnBombBeginDefuse, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventBombBeginplant>((GameEventHandler<EventBombBeginplant>)OnBombBeginPlant, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventPlayerHurt>((GameEventHandler<EventPlayerHurt>)OnPlayerHurt, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventPlayerBlind>((GameEventHandler<EventPlayerBlind>)OnPlayerBlind, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventWeaponFire>((GameEventHandler<EventWeaponFire>)OnWeaponFire, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventWeaponReload>((GameEventHandler<EventWeaponReload>)OnWeaponReload, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventWeaponZoom>((GameEventHandler<EventWeaponZoom>)OnWeaponZoom, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventGrenadeThrown>((GameEventHandler<EventGrenadeThrown>)OnGrenadeThrown, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventPlayerJump>((GameEventHandler<EventPlayerJump>)OnPlayerJump, (HookMode)1);
		((BasePlugin)this).RegisterListener<Listeners.OnMapStart>(delegate(string mapName)
		{
			ResetServerMaxPlayersCache();
			_db.Clear();
			LoadDbFilesOnly();
			RefreshMapNades(mapName);
			_cooldowns.Clear();
			_pendingWindupLineups.Clear();
			_roundCountByTeam.Clear();
			_replayBots.Clear();
		});
		((BasePlugin)this).AddCommand("bot_nades", "Control bots' nade throw mode (off/less/normal/more/max)", CmdBotNades);
		RefreshMapNades(Server.MapName);
		Logger.LogInformation("[NadeSystem] v2.2.0 Loaded - {DbCount} map lineups in DB. Active map '{Map}' has {Count} lineups ready.", _db.Count, _loadedMapName, _mapNades?.Count ?? 0);
	}

	private void LoadDbFilesOnly()
	{
		int num = 0;
		if (!Directory.Exists(DataDir))
		{
			return;
		}
		string[] files = Directory.GetFiles(DataDir, "*.json");
		foreach (string path in files)
		{
			try
			{
				List<GrenadeData> list = JsonSerializer.Deserialize<List<GrenadeData>>(File.ReadAllText(path));
				if (list == null)
				{
					continue;
				}
				foreach (GrenadeData item in list)
				{
					GrenadeData grenadeData = item;
					if (grenadeData.Description == null)
					{
						string text = (grenadeData.Description = "");
					}
					item.GrenadeType = (item.GrenadeType ?? "").ToLowerInvariant();
					if (item.Description.Contains("decoy", StringComparison.OrdinalIgnoreCase))
					{
						item.GrenadeType = "decoy";
					}
					if (item.Description.StartsWith("CT", StringComparison.OrdinalIgnoreCase))
					{
						item.TeamTag = "CT";
					}
					else if (item.Description.StartsWith("T", StringComparison.OrdinalIgnoreCase))
					{
						item.TeamTag = "T";
					}
					else
					{
						item.TeamTag = "";
					}
				}
				_db.AddRange(list);
				num += list.Count;
			}
			catch (Exception ex)
			{
				Console.WriteLine("[NadeSystem] Failed to load " + Path.GetFileName(path) + ": " + ex.Message);
			}
		}
	}

	private void RefreshMapNades(string? mapName)
	{
		string targetMap = (string.IsNullOrEmpty(mapName) ? (Server.MapName ?? "") : mapName);
		if (!string.IsNullOrEmpty(targetMap))
		{
			_loadedMapName = targetMap;
			_mapNades = _db.Where((GrenadeData g) => string.Equals(g.MapName, targetMap, StringComparison.OrdinalIgnoreCase)).ToList();

			ResolveMapSpawns();
			if (_spawnsResolved)
			{
				float teamAxisX = _ctSpawnCenter.X - _tSpawnCenter.X;
				float teamAxisY = _ctSpawnCenter.Y - _tSpawnCenter.Y;

				int autoTagged = 0;
				foreach (var g in _mapNades)
				{
					if (string.IsNullOrEmpty(g.TeamTag) || g.AutoTagged)
					{
						float distT = Dist3D(g.ProjectilePosition.X, g.ProjectilePosition.Y, g.ProjectilePosition.Z, _tSpawnCenter.X, _tSpawnCenter.Y, _tSpawnCenter.Z);
						float distCT = Dist3D(g.ProjectilePosition.X, g.ProjectilePosition.Y, g.ProjectilePosition.Z, _ctSpawnCenter.X, _ctSpawnCenter.Y, _ctSpawnCenter.Z);

						if (distT < distCT * 0.85f)
						{
							g.TeamTag = "T";
						}
						else if (distCT < distT * 0.85f)
						{
							g.TeamTag = "CT";
						}
						else
						{
							float throwDirX = g.LandingPosition.X - g.ProjectilePosition.X;
							float throwDirY = g.LandingPosition.Y - g.ProjectilePosition.Y;
							float dot = throwDirX * teamAxisX + throwDirY * teamAxisY;
							g.TeamTag = dot >= 0 ? "T" : "CT";
						}
						g.AutoTagged = true;
						autoTagged++;
					}
				}
				Logger.LogInformation("[NadeSystem] Active map '{Map}': {Count} lineups ready ({Auto} auto-classified with team tags).", targetMap, _mapNades.Count, autoTagged);
			}
			else
			{
				Logger.LogInformation("[NadeSystem] Active map '{Map}': {Count} lineups ready.", targetMap, _mapNades.Count);
			}
		}
	}

	public bool TrySolveBallisticArc(Vector startPos, Vector targetPos, bool allowWallBounce, out Vector launchOrigin, out Vector launchVelocity)
	{
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Expected O, but got Unknown
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Expected O, but got Unknown
		//IL_0271: Unknown result type (might be due to invalid IL or missing references)
		//IL_027b: Expected O, but got Unknown
		//IL_0366: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ac: Expected O, but got Unknown
		//IL_0380: Unknown result type (might be due to invalid IL or missing references)
		//IL_0391: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_03c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_03ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_03e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_03eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0405: Unknown result type (might be due to invalid IL or missing references)
		//IL_045a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0468: Unknown result type (might be due to invalid IL or missing references)
		//IL_0488: Unknown result type (might be due to invalid IL or missing references)
		//IL_04a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_04c2: Unknown result type (might be due to invalid IL or missing references)
		launchOrigin = new Vector((float?)startPos.X, (float?)startPos.Y, (float?)startPos.Z);
		launchVelocity = new Vector((float?)0f, (float?)0f, (float?)0f);
		CRayTraceInterface val = null;
		try
		{
			val = _rayTraceCapability.Get();
		}
		catch
		{
		}
		float num = targetPos.X - startPos.X;
		float num2 = targetPos.Y - startPos.Y;
		float num3 = targetPos.Z - startPos.Z;
		float num4 = MathF.Sqrt(num * num + num2 * num2);
		if (num4 < 180f || num4 > 2850f)
		{
			return false;
		}
		float num5 = 1f / num4;
		float num6 = num * num5;
		float num7 = num2 * num5;
		launchOrigin.X = startPos.X + num6 * 22f;
		launchOrigin.Y = startPos.Y + num7 * 22f;
		launchOrigin.Z = startPos.Z;
		num = targetPos.X - launchOrigin.X;
		num2 = targetPos.Y - launchOrigin.Y;
		num3 = targetPos.Z - launchOrigin.Z;
		float[] candidateFlightTimes = CandidateFlightTimes;
		foreach (float num8 in candidateFlightTimes)
		{
			float num9 = num / num8;
			float num10 = num2 / num8;
			float num11 = (num3 + 180f * num8 * num8) / num8;
			float num12 = MathF.Sqrt(num9 * num9 + num10 * num10 + num11 * num11);
			if (!(num12 < 240f) && !(num12 > 1040f) && (val == null || VerifyParabolicArcClear(val, launchOrigin, num9, num10, num11, num8, 7)))
			{
				launchVelocity.X = num9;
				launchVelocity.Y = num10;
				launchVelocity.Z = num11;
				return true;
			}
		}
		if (allowWallBounce && val != null && num4 < 1100f)
		{
			float num13 = MathF.Atan2(num2, num);
			candidateFlightTimes = new float[2] { 0.55f, -0.55f };
			RayTraceAPI.TraceOptions val2 = new RayTraceAPI.TraceOptions((InteractionLayers)12289, (InteractionLayers)0, false);
			RayTraceAPI.TraceResult val3 = default;
			RayTraceAPI.TraceResult val4 = default;
			foreach (float num14 in candidateFlightTimes)
			{
				float x = num13 + num14;
				float num15 = MathF.Cos(x);
				float num16 = MathF.Sin(x);
				if (_traceA == null)
				{
					_traceA = new Vector((float?)0f, (float?)0f, (float?)0f);
				}
				if (_traceB == null)
				{
					_traceB = new Vector((float?)0f, (float?)0f, (float?)0f);
				}
				_traceA.X = launchOrigin.X;
				_traceA.Y = launchOrigin.Y;
				_traceA.Z = launchOrigin.Z;
				_traceB.X = launchOrigin.X + num15 * (num4 * 0.7f);
				_traceB.Y = launchOrigin.Y + num16 * (num4 * 0.7f);
				_traceB.Z = launchOrigin.Z + 25f;
				val2 = new RayTraceAPI.TraceOptions((InteractionLayers)2109441, (InteractionLayers)0, false);
				if (!val.TraceEndShape(_traceA, _traceB, (CEntityInstance)null, val2, out val3) || !val3.DidHit || !(val3.Fraction > 0.25f) || !(val3.Fraction < 0.92f) || !(MathF.Abs(val3.NormalZ) < 0.35f))
				{
					continue;
				}
				_traceA.X = val3.EndPosX + val3.NormalX * 8f;
				_traceA.Y = val3.EndPosY + val3.NormalY * 8f;
				_traceA.Z = val3.EndPosZ;
				_traceB.X = targetPos.X;
				_traceB.Y = targetPos.Y;
				_traceB.Z = targetPos.Z + 18f;
				if (val.TraceEndShape(_traceA, _traceB, (CEntityInstance)null, val2, out val4) && val4.Fraction >= 0.88f)
				{
					float num17 = 0.55f;
					launchVelocity.X = (val3.EndPosX - launchOrigin.X) / num17;
					launchVelocity.Y = (val3.EndPosY - launchOrigin.Y) / num17;
					launchVelocity.Z = (val3.EndPosZ - launchOrigin.Z + 180f * num17 * num17) / num17;
					float num18 = MathF.Sqrt(launchVelocity.X * launchVelocity.X + launchVelocity.Y * launchVelocity.Y + launchVelocity.Z * launchVelocity.Z);
					if (num18 >= 240f && num18 <= 1040f)
					{
						return true;
					}
				}
			}
		}
		return false;
	}

	private bool VerifyParabolicArcClear(CRayTraceInterface rt, Vector origin, float vx, float vy, float vz, float totalTime, int segments)
	{
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Expected O, but got Unknown
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Expected O, but got Unknown
		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
		//IL_014a: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			if (_traceA == null)
			{
				_traceA = new Vector((float?)0f, (float?)0f, (float?)0f);
			}
			if (_traceB == null)
			{
				_traceB = new Vector((float?)0f, (float?)0f, (float?)0f);
			}
			RayTraceAPI.TraceOptions val = new RayTraceAPI.TraceOptions((InteractionLayers)2109441, (InteractionLayers)0, false);
			float num = origin.X;
			float num2 = origin.Y;
			float num3 = origin.Z;
			RayTraceAPI.TraceResult val2 = default;
			for (int i = 1; i <= segments; i++)
			{
				float num4 = totalTime * 0.92f * ((float)i / (float)segments);
				float num5 = origin.X + vx * num4;
				float num6 = origin.Y + vy * num4;
				float num7 = origin.Z + vz * num4 - 180f * num4 * num4;
				_traceA.X = num;
				_traceA.Y = num2;
				_traceA.Z = num3;
				_traceB.X = num5;
				_traceB.Y = num6;
				_traceB.Z = num7;
				if (!rt.TraceEndShape(_traceA, _traceB, (CEntityInstance)null, val, out val2))
				{
					return true;
				}
				if (val2.Fraction < 0.97f)
				{
					return false;
				}
				num = num5;
				num2 = num6;
				num3 = num7;
			}
			return true;
		}
		catch
		{
			return true;
		}
	}

	private bool TryDynamicTacticalNade(CCSPlayerController bot, CCSPlayerPawn pawn, List<CCSPlayerController> allControllers)
	{
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ca: Expected O, but got Unknown
		//IL_0268: Unknown result type (might be due to invalid IL or missing references)
		//IL_026f: Expected O, but got Unknown
		//IL_044a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0451: Expected O, but got Unknown
		if (_botNadesMode == "off")
		{
			return false;
		}
		uint index = ((CEntityInstance)bot).Index;
		float currentTime = Server.CurrentTime;
		if (_dynamicNadeCooldownByBot.TryGetValue(index, out var value) && currentTime < value)
		{
			return false;
		}
		if (pawn.Bot != null && (pawn.Bot.IsEnemyVisible || pawn.Bot.IsAttacking))
		{
			return false;
		}
		if (FiredRecently(bot, 2.2f) || HurtRecently(bot, 1.5f))
		{
			return false;
		}
		Vector absOrigin = ((CBaseEntity)pawn).AbsOrigin;
		if (absOrigin == null)
		{
			return false;
		}
		float? num = absOrigin.X;
		float? num2 = absOrigin.Y;
		float z = absOrigin.Z;
		CNetworkViewOffsetVector viewOffset = ((CBaseModelEntity)pawn).ViewOffset;
		Vector startPos = new Vector(num, num2, (float?)(z + ((viewOffset != null) ? viewOffset.Z : 64f)));
		int teamNum = ((CBaseEntity)bot).TeamNum;
		foreach (CCSPlayerController allController in allControllers)
		{
			if (!((CEntityInstance)allController).IsValid || !allController.PawnIsAlive || ((CBaseEntity)allController).TeamNum == teamNum || !TeamHasInformationOn(bot, allController, allControllers))
			{
				continue;
			}
			CCSPlayerPawn activeLivePawn = GetActiveLivePawn(allController);
			Vector val = ((activeLivePawn != null) ? ((CBaseEntity)activeLivePawn).AbsOrigin : null);
			if (val == null)
			{
				continue;
			}
			float num3 = Dist3D(absOrigin.X, absOrigin.Y, absOrigin.Z, val.X, val.Y, val.Z);
			if (num3 < 360f || num3 > 1750f)
			{
				continue;
			}
			object obj;
			if (activeLivePawn == null)
			{
				obj = null;
			}
			else
			{
				CPlayer_WeaponServices weaponServices = ((CBasePlayerPawn)activeLivePawn).WeaponServices;
				if (weaponServices == null)
				{
					obj = null;
				}
				else
				{
					CHandle<CBasePlayerWeapon> activeWeapon = weaponServices.ActiveWeapon;
					if (activeWeapon == null)
					{
						obj = null;
					}
					else
					{
						CBasePlayerWeapon value2 = activeWeapon.Value;
						obj = ((value2 != null) ? ((CEntityInstance)value2).DesignerName : null);
					}
				}
			}
			if (obj == null)
			{
				obj = "";
			}
			string text = (string)obj;
			if ((text.Contains("awp") || text.Contains("ssg08") || num3 > 950f) && CanTeamAffordAndAllow(bot, "smoke", allControllers))
			{
				Vector val2 = new Vector((float?)(absOrigin.X + (val.X - absOrigin.X) * 0.58f), (float?)(absOrigin.Y + (val.Y - absOrigin.Y) * 0.58f), (float?)(val.Z + 12f));
				if (IsNoActiveSmokeNear(val2.X, val2.Y, val2.Z, 260f) && TrySolveBallisticArc(startPos, val2, allowWallBounce: false, out Vector launchOrigin, out Vector launchVelocity))
				{
					GrenadeData g = new GrenadeData
					{
						Id = $"dyn_smoke_{index}_{(int)currentTime}",
						MapName = Server.MapName,
						GrenadeType = "smoke",
						ProjectilePosition = new Vec3
						{
							X = launchOrigin.X,
							Y = launchOrigin.Y,
							Z = launchOrigin.Z
						},
						ProjectileVelocity = new Vec3
						{
							X = launchVelocity.X,
							Y = launchVelocity.Y,
							Z = launchVelocity.Z
						},
						LandingPosition = new Vec3
						{
							X = val2.X,
							Y = val2.Y,
							Z = val2.Z
						},
						Description = "DynamicBallisticSmoke"
					};
					_dynamicNadeCooldownByBot[index] = currentTime + 14f;
					TryReplay(bot, g, allControllers);
					return true;
				}
			}
			if (!(num3 >= 380f) || !(num3 <= 1050f))
			{
				continue;
			}
			string text2 = ((Random.Shared.Next(0, 100) < 55) ? "molotov" : "he");
			if (CanTeamAffordAndAllow(bot, text2, allControllers))
			{
				Vector val3 = new Vector((float?)val.X, (float?)val.Y, (float?)(val.Z + 20f));
				if (TrySolveBallisticArc(startPos, val3, allowWallBounce: true, out Vector launchOrigin2, out Vector launchVelocity2))
				{
					GrenadeData g2 = new GrenadeData
					{
						Id = $"dyn_{text2}_{index}_{(int)currentTime}",
						MapName = Server.MapName,
						GrenadeType = text2,
						ProjectilePosition = new Vec3
						{
							X = launchOrigin2.X,
							Y = launchOrigin2.Y,
							Z = launchOrigin2.Z
						},
						ProjectileVelocity = new Vec3
						{
							X = launchVelocity2.X,
							Y = launchVelocity2.Y,
							Z = launchVelocity2.Z
						},
						LandingPosition = new Vec3
						{
							X = val3.X,
							Y = val3.Y,
							Z = val3.Z
						},
						Description = "DynamicBallisticFlush"
					};
					_dynamicNadeCooldownByBot[index] = currentTime + 11f;
					TryReplay(bot, g2, allControllers);
					return true;
				}
			}
		}
		return false;
	}

	private bool CanTeamAffordAndAllow(CCSPlayerController bot, string gtype, List<CCSPlayerController> allControllers)
	{
		CCSPlayerPawn? pawn = bot.PlayerPawn?.Value;
		if (pawn == null || !pawn.IsValid || !HasGrenadeInInventory(pawn, gtype, out _, out _))
		{
			return false;
		}
		if (_botNadesMode == "less" && !LessModeAllows(gtype, ((CEntityInstance)bot).Index))
		{
			return false;
		}
		return true;
	}

	private bool IsNoActiveSmokeNear(float x, float y, float z, float minRadius)
	{
		float currentTime = Server.CurrentTime;
		foreach (CooldownEntry c in _cooldowns)
		{
			if (!(c.ExpiresAt <= currentTime))
			{
				GrenadeData grenadeData = _mapNades.FirstOrDefault((GrenadeData n) => n.Id == c.GrenadeId && string.Equals(n.GrenadeType, "smoke", StringComparison.OrdinalIgnoreCase));
				if (grenadeData != null && Dist3D(x, y, z, grenadeData.LandingPosition.X, grenadeData.LandingPosition.Y, grenadeData.LandingPosition.Z) < minRadius)
				{
					return false;
				}
			}
		}
		return true;
	}

	private void CheckBotZones()
	{
		CCSGameRulesProxy? obj = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
		if (obj != null)
		{
			CCSGameRules gameRules = obj.GameRules;
			if (((gameRules != null) ? new bool?(gameRules.FreezePeriod) : ((bool?)null)) == true)
			{
				return;
			}
		}
		if (_roundOver)
		{
			return;
		}
		List<CCSPlayerController> list = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").ToList();
		bool flag = HasLiveEnemyForTeam(2, list);
		bool flag2 = HasLiveEnemyForTeam(3, list);
		foreach (CCSPlayerController bot in list)
		{
			if (!((CEntityInstance)bot).IsValid || !bot.IsBot)
			{
				continue;
			}
			CCSPlayerPawn val = bot.PlayerPawn?.Value;
			if ((CEntityInstance)(object)val == (CEntityInstance)null || !((CEntityInstance)val).IsValid || val.Bot == null || bot.HasBeenControlledByPlayerThisRound || !bot.PawnIsAlive || _replayBots.Contains(((CEntityInstance)bot).Index) || val.Bot.IsEnemyVisible || val.Bot.IsAttacking || HurtRecently(bot, 1.2f))
			{
				continue;
			}
			Vector pos = ((CBaseEntity)val).AbsOrigin;
			if (pos == null)
			{
				continue;
			}
			bool flag3 = false;
			foreach (GrenadeData mapNade in _mapNades)
			{
				string grenadeType = mapNade.GrenadeType;
				float num = 64f;
				float num2 = pos.X - mapNade.ZoneX;
				float num3 = pos.Y - mapNade.ZoneY;
				float x = pos.Z + num - mapNade.ProjectilePosition.Z;
				if (grenadeType == "decoy")
				{
					if (_botNadesMode != "off" && !IsOnCooldown(mapNade.Id) && !IsLineupPending(mapNade.Id) && num2 * num2 + num3 * num3 <= 2500f && MathF.Abs(x) <= 75f && HasGrenadeInInventory(val, "decoy", out string decWName, out int decDef) && HasReleaseLoS(pos.X, pos.Y, pos.Z + num, mapNade.ProjectilePosition))
					{
						RegisterCooldown(mapNade.Id, "decoy");
						ExecuteRealisticThrow(bot, mapNade, decWName, decDef);
						flag3 = true;
						break;
					}
					continue;
				}
				if (num2 * num2 + num3 * num3 > mapNade.ZoneRadius * mapNade.ZoneRadius || MathF.Abs(x) > 75f || IsOnCooldown(mapNade.Id) || IsLineupPending(mapNade.Id) || ((grenadeType == "he" || grenadeType == "molotov" || grenadeType == "flash") && IsOnProbFailCooldown(mapNade.Id)) || (grenadeType == "smoke" && _smokeCooldownBots.Contains(((CEntityInstance)bot).Index)) || (grenadeType == "smoke" && !IsNoActiveSmokeNear(mapNade.LandingPosition.X, mapNade.LandingPosition.Y, mapNade.LandingPosition.Z, 120f)) || !((((CBaseEntity)bot).TeamNum == 2) ? flag : flag2))
				{
					continue;
				}
				if (!string.IsNullOrEmpty(mapNade.TeamTag) && mapNade.TeamTag != ((((CBaseEntity)bot).TeamNum == 3) ? "CT" : "T"))
				{
					continue;
				}
				if (!HasGrenadeInInventory(val, grenadeType, out _, out _))
				{
					continue;
				}
				if (!HasReleaseLoS(pos.X, pos.Y, pos.Z + num, mapNade.ProjectilePosition))
				{
					continue;
				}
				if (_botNadesMode == "max")
				{
					if ((grenadeType == "flash" && CanBlindAnyEnemy(bot, mapNade, list).Count == 0) || FiredRecently(bot, 2f))
					{
						continue;
					}
					if (grenadeType == "he" || grenadeType == "molotov")
					{
						float lx2 = mapNade.LandingPosition.X;
						float ly2 = mapNade.LandingPosition.Y;
						float lz2 = mapNade.LandingPosition.Z;
						if (!list.Any(delegate(CCSPlayerController p)
						{
							if (!((CEntityInstance)p).IsValid || ((CBaseEntity)p).TeamNum == ((CBaseEntity)bot).TeamNum)
							{
								return false;
							}
							CCSPlayerPawn? activeLivePawn = GetActiveLivePawn(p);
							Vector val2 = ((activeLivePawn != null) ? ((CBaseEntity)activeLivePawn).AbsOrigin : null);
							if (val2 == null)
							{
								return false;
							}
							float num5 = val2.X - lx2;
							float num6 = val2.Y - ly2;
							float num7 = val2.Z - lz2;
							return num5 * num5 + num6 * num6 + num7 * num7 <= 90000f;
						}) || (grenadeType == "molotov" && !IsNoActiveSmokeNear(lx2, ly2, lz2, 200f)))
						{
							continue;
						}
					}
					TryReplay(bot, mapNade, list);
					flag3 = true;
					break;
				}
				if (grenadeType == "flash")
				{
					uint index = ((CEntityInstance)bot).Index;
					if (!_botInFlashZone.TryGetValue(index, out HashSet<string> value))
					{
						value = new HashSet<string>();
						_botInFlashZone[index] = value;
					}
					if (value.Contains(mapNade.Id))
					{
						continue;
					}
					value.Add(mapNade.Id);
					if (!_botFlashRatioWindow.TryGetValue(index, out (float, float) value2) || !(Server.CurrentTime < value2.Item1) || !(value2.Item2 < 1f) || !(Random.Shared.NextDouble() >= (double)value2.Item2))
					{
						bool hasPushingTeammate = HasTeammateNearby(bot, val, list, 900f);
						(int blindable, int total) tuple = CountBlindableEnemies(bot, mapNade, list);
						float flashRatioThreshold = GetFlashRatioThreshold(tuple.blindable, tuple.total);
						// Proactive Entry Flash: If pushing with a teammate in the first 45s, allow entry flash without reading hidden enemy angles!
						if (flashRatioThreshold <= 0f && hasPushingTeammate && _freezeEndTime > 0f && Server.CurrentTime - _freezeEndTime < 45f)
						{
							flashRatioThreshold = 0.65f;
						}
						if (flashRatioThreshold > 0f)
						{
							_botFlashRatioWindow[index] = (Server.CurrentTime + 10f, flashRatioThreshold);
							TryConditionalReplay(bot, mapNade, list);
							flag3 = true;
						}
					}
					break;
				}
				TryConditionalReplay(bot, mapNade, list);
				flag3 = true;
				break;
			}
			if (!flag3 && (_tick + (int)((CEntityInstance)bot).Index) % 16 == 0)
			{
				TryDynamicTacticalNade(bot, val, list);
			}
			if (!_botInFlashZone.TryGetValue(((CEntityInstance)bot).Index, out HashSet<string> value3))
			{
				continue;
			}
			value3.RemoveWhere(delegate(string gid)
			{
				GrenadeData grenadeData = _mapNades.FirstOrDefault((GrenadeData g) => g.Id == gid && string.Equals(g.GrenadeType, "flash", StringComparison.OrdinalIgnoreCase));
				if (grenadeData == null)
				{
					return true;
				}
				float num5 = pos.X - grenadeData.ZoneX;
				float num6 = pos.Y - grenadeData.ZoneY;
				float x2 = pos.Z + 64f - grenadeData.ProjectilePosition.Z;
				return num5 * num5 + num6 * num6 > grenadeData.ZoneRadius * grenadeData.ZoneRadius || MathF.Abs(x2) > 85f;
			});
		}
	}

	private static bool HasLiveEnemyForTeam(int teamNum, List<CCSPlayerController> allControllers)
	{
		return allControllers.Any((CCSPlayerController p) => ((CEntityInstance)p).IsValid && p.PawnIsAlive && (((CBaseEntity)p).TeamNum == 2 || ((CBaseEntity)p).TeamNum == 3) && ((CBaseEntity)p).TeamNum != teamNum);
	}

	private static bool FacesThrowDirection(CCSPlayerPawn pawn, GrenadeData g)
	{
		QAngle eyeAngles = pawn.EyeAngles;
		if (eyeAngles == null)
		{
			return true;
		}
		float x = eyeAngles.Y * ((float)Math.PI / 180f);
		float num = MathF.Cos(x);
		float num2 = MathF.Sin(x);
		float x2 = g.ProjectileVelocity.X;
		float y = g.ProjectileVelocity.Y;
		float num3 = MathF.Sqrt(x2 * x2 + y * y);
		if (num3 <= 0f)
		{
			return true;
		}
		return num * (x2 / num3) + num2 * (y / num3) >= -0.5f;
	}

	private static CCSPlayerPawn? GetActiveLivePawn(CCSPlayerController p)
	{
		if (!((CEntityInstance)p).IsValid)
		{
			return null;
		}
		CBasePlayerPawn val = ((CBasePlayerController)p).Pawn?.Value;
		if ((CEntityInstance)(object)val == (CEntityInstance)null || !((CEntityInstance)val).IsValid || ((CBaseEntity)val).LifeState != 0 || ((CBaseEntity)val).Health <= 0)
		{
			return null;
		}
		return ((NativeObject)val).As<CCSPlayerPawn>();
	}

	private void RecordSoundPoint(CCSPlayerController? player, List<CCSPlayerController>? allPlayers = null, bool isSniper = false)
	{
		if ((CEntityInstance)(object)player == (CEntityInstance)null || !((CEntityInstance)player).IsValid)
		{
			return;
		}
		CCSPlayerPawn? activeLivePawn = GetActiveLivePawn(player);
		Vector val = ((activeLivePawn != null) ? ((CBaseEntity)activeLivePawn).AbsOrigin : null);
		if (val == null)
		{
			return;
		}
		float ox = val.X;
		float oy = val.Y;
		float oz = val.Z;
		if (!(allPlayers ?? Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").ToList()).Any(delegate(CCSPlayerController e)
		{
			if (!((CEntityInstance)e).IsValid || ((CBaseEntity)e).TeamNum == ((CBaseEntity)player).TeamNum)
			{
				return false;
			}
			CCSPlayerPawn? activeLivePawn2 = GetActiveLivePawn(e);
			Vector val2 = ((activeLivePawn2 != null) ? ((CBaseEntity)activeLivePawn2).AbsOrigin : null);
			if (val2 == null)
			{
				return false;
			}
			float num4 = val2.X - ox;
			float num5 = val2.Y - oy;
			float num6 = val2.Z - oz;
			return num4 * num4 + num5 * num5 + num6 * num6 <= 1200000f;
		}))
		{
			return;
		}
		uint index = ((CEntityInstance)player).Index;
		if (!_soundPoints.TryGetValue(index, out List<SoundPoint> value))
		{
			value = new List<SoundPoint>();
			_soundPoints[index] = value;
		}
		if (value.Count > 0)
		{
			SoundPoint soundPoint = value[value.Count - 1];
			float num = ox - soundPoint.X;
			float num2 = oy - soundPoint.Y;
			float num3 = oz - soundPoint.Z;
			if (num * num + num2 * num2 + num3 * num3 < 16f && !isSniper)
			{
				return;
			}
		}
		value.Add(new SoundPoint(ox, oy, oz, Server.CurrentTime, isSniper));
	}

	private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
	{
		CCSPlayerController userid = @event.Userid;
		if ((CEntityInstance)(object)userid != (CEntityInstance)null && ((CEntityInstance)userid).IsValid)
		{
			_botLastFireTime[((CEntityInstance)userid).Index] = Server.CurrentTime;
		}
		string text = (@event.Weapon ?? "").ToLowerInvariant();
		bool isSniper = text.Contains("awp") || text.Contains("ssg08");
		RecordSoundPoint(userid, null, isSniper);
		return (HookResult)0;
	}

	private HookResult OnWeaponReload(EventWeaponReload @event, GameEventInfo info)
	{
		RecordSoundPoint(@event.Userid);
		return (HookResult)0;
	}

	private HookResult OnWeaponZoom(EventWeaponZoom @event, GameEventInfo info)
	{
		RecordSoundPoint(@event.Userid);
		return (HookResult)0;
	}

	private HookResult OnGrenadeThrown(EventGrenadeThrown @event, GameEventInfo info)
	{
		RecordSoundPoint(@event.Userid);
		return (HookResult)0;
	}

	private HookResult OnPlayerJump(EventPlayerJump @event, GameEventInfo info)
	{
		RecordSoundPoint(@event.Userid);
		return (HookResult)0;
	}

	private void UpdateSoundTrails(bool recordFootsteps)
	{
		float now = Server.CurrentTime;
		List<CCSPlayerController> list = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").ToList();
		foreach (CCSPlayerController item in list)
		{
			if (!((CEntityInstance)item).IsValid)
			{
				continue;
			}
			uint index = ((CEntityInstance)item).Index;
			CCSPlayerPawn activeLivePawn = GetActiveLivePawn(item);
			if ((CEntityInstance)(object)activeLivePawn == (CEntityInstance)null)
			{
				_soundPoints.Remove(index);
				continue;
			}
			Vector absOrigin = ((CBaseEntity)activeLivePawn).AbsOrigin;
			if (absOrigin == null)
			{
				continue;
			}
			float cx = absOrigin.X;
			float cy = absOrigin.Y;
			float cz = absOrigin.Z;
			if (_soundPoints.TryGetValue(index, out List<SoundPoint> value) && value.Count > 0)
			{
				value.RemoveAll(delegate(SoundPoint pt)
				{
					if (now - pt.Timestamp > 8f)
					{
						return true;
					}
					float num = pt.X - cx;
					float num2 = pt.Y - cy;
					float num3 = pt.Z - cz;
					return num * num + num2 * num2 + num3 * num3 > 14400f;
				});
			}
			if (recordFootsteps)
			{
				Vector absVelocity = ((CBaseEntity)activeLivePawn).AbsVelocity;
				if (absVelocity != null)
				{
					float speedSq = absVelocity.X * absVelocity.X + absVelocity.Y * absVelocity.Y;
					bool holdingSilentMove = false;
					var moveSvc = activeLivePawn.MovementServices;
					if (moveSvc?.Buttons != null)
					{
						var btnStates = moveSvc.Buttons.ButtonStates;
						if (btnStates.Length > 0 && (btnStates[0] & ((1UL << 16) | (1UL << 2))) != 0)
						{
							holdingSilentMove = true;
						}
					}
					if (!holdingSilentMove && speedSq > 21000f)
					{
						RecordSoundPoint(item, list);
					}
				}
			}
		}
	}

	private bool PlayerMadeAudibleSound(CCSPlayerController player)
	{
		if (!((CEntityInstance)player).IsValid)
		{
			return false;
		}
		if (_soundPoints.TryGetValue(((CEntityInstance)player).Index, out List<SoundPoint> value))
		{
			return value.Count > 0;
		}
		return false;
	}

	private bool EnemySeesTarget(CCSPlayerController enemy, CCSPlayerController target)
	{
		if (!((CEntityInstance)enemy).IsValid || !((CEntityInstance)target).IsValid)
		{
			return false;
		}
		CCSPlayerPawn activeLivePawn = GetActiveLivePawn(target);
		if ((CEntityInstance)(object)activeLivePawn == (CEntityInstance)null || !((CEntityInstance)activeLivePawn).IsValid)
		{
			return false;
		}
		try
		{
			EntitySpottedState_t entitySpottedState = activeLivePawn.EntitySpottedState;
			if (entitySpottedState != null)
			{
				int slot = enemy.Slot;
				if (slot >= 0)
				{
					Span<uint> spottedByMask = entitySpottedState.SpottedByMask;
					int num = slot / 32;
					int num2 = slot % 32;
					if (num >= 0 && num < spottedByMask.Length)
					{
						return (spottedByMask[num] & (uint)(1 << num2)) != 0;
					}
				}
			}
		}
		catch
		{
		}
		return EnemySeesTargetGeometric(enemy, target);
	}

	private bool EnemySeesTargetGeometric(CCSPlayerController enemy, CCSPlayerController target)
	{
		CCSPlayerPawn activeLivePawn = GetActiveLivePawn(enemy);
		CCSPlayerPawn activeLivePawn2 = GetActiveLivePawn(target);
		if (((activeLivePawn != null) ? ((CBaseEntity)activeLivePawn).AbsOrigin : null) == null || activeLivePawn.EyeAngles == null || ((activeLivePawn2 != null) ? ((CBaseEntity)activeLivePawn2).AbsOrigin : null) == null)
		{
			return false;
		}
		float x = ((CBaseEntity)activeLivePawn).AbsOrigin.X;
		float y = ((CBaseEntity)activeLivePawn).AbsOrigin.Y;
		float num = ((CBaseEntity)activeLivePawn).AbsOrigin.Z + 64f;
		float x2 = ((CBaseEntity)activeLivePawn2).AbsOrigin.X;
		float y2 = ((CBaseEntity)activeLivePawn2).AbsOrigin.Y;
		float num2 = ((CBaseEntity)activeLivePawn2).AbsOrigin.Z + 64f;
		float num3 = x2 - x;
		float num4 = y2 - y;
		float num5 = num2 - num;
		if (num3 * num3 + num4 * num4 + num5 * num5 > 1690000f)
		{
			return false;
		}
		float x3 = activeLivePawn.EyeAngles.Y * ((float)Math.PI / 180f);
		float x4 = (0f - activeLivePawn.EyeAngles.X) * ((float)Math.PI / 180f);
		float num6 = MathF.Cos(x4) * MathF.Cos(x3);
		float num7 = MathF.Cos(x4) * MathF.Sin(x3);
		float y3 = MathF.Sin(x4);
		float num8 = MathF.Atan2(num4, num3);
		float num9 = MathF.Atan2(num7, num6);
		float num10 = MathF.Abs(MathF.Atan2(MathF.Sin(num8 - num9), MathF.Cos(num8 - num9)));
		float num11 = MathF.Atan2(num5, MathF.Sqrt(num3 * num3 + num4 * num4));
		float num12 = MathF.Atan2(y3, MathF.Sqrt(num6 * num6 + num7 * num7));
		float num13 = MathF.Abs(num11 - num12);
		if (num10 <= 0.927f && num13 <= (float)Math.PI / 4f)
		{
			return FlashHasLoS(new Vec3
			{
				X = x2,
				Y = y2,
				Z = num2
			}, x, y, num);
		}
		return false;
	}

	private bool HasInformationOn(CCSPlayerController observer, CCSPlayerController target)
	{
		if (EnemySeesTarget(observer, target))
		{
			return true;
		}
		if (!PlayerMadeAudibleSound(target))
		{
			return false;
		}
		CCSPlayerPawn? obsPawn = GetActiveLivePawn(observer);
		CCSPlayerPawn? tgtPawn = GetActiveLivePawn(target);
		if (obsPawn?.AbsOrigin == null || tgtPawn?.AbsOrigin == null)
		{
			return false;
		}
		return Dist3D(obsPawn.AbsOrigin.X, obsPawn.AbsOrigin.Y, obsPawn.AbsOrigin.Z, tgtPawn.AbsOrigin.X, tgtPawn.AbsOrigin.Y, tgtPawn.AbsOrigin.Z) <= 1180f;
	}

	private bool TeamHasInformationOn(CCSPlayerController bot, CCSPlayerController enemy, List<CCSPlayerController> allControllers)
	{
		if (HasInformationOn(bot, enemy))
		{
			return true;
		}
		int botTeam = ((CBaseEntity)bot).TeamNum;
		for (int i = 0; i < allControllers.Count; i++)
		{
			CCSPlayerController ally = allControllers[i];
			if (((CEntityInstance)ally).IsValid && ally.PawnIsAlive && ((CBaseEntity)ally).TeamNum == botTeam && ally.Slot != bot.Slot)
			{
				if (HasInformationOn(ally, enemy))
				{
					return true;
				}
			}
		}
		return false;
	}

	private bool FiredRecently(CCSPlayerController player, float seconds)
	{
		if (_botLastFireTime.TryGetValue(((CEntityInstance)player).Index, out var value))
		{
			return Server.CurrentTime - value < seconds;
		}
		return false;
	}

	private bool HurtRecently(CCSPlayerController player, float seconds)
	{
		if (_botLastHurtTime.TryGetValue(((CEntityInstance)player).Index, out var value))
		{
			return Server.CurrentTime - value < seconds;
		}
		return false;
	}

	private void TryReplay(CCSPlayerController bot, GrenadeData g, List<CCSPlayerController> allControllers)
	{
		if (_botNadesMode == "off" || bot.HasBeenControlledByPlayerThisRound)
		{
			return;
		}
		string grenadeType = g.GrenadeType;
		if (_botNadesMode == "less")
		{
			if (!LessModeAllows(grenadeType, ((CEntityInstance)bot).Index))
			{
				return;
			}
		}
		else if (_botNadesMode == "normal")
		{
			int teamNum = ((CBaseEntity)bot).TeamNum;
			int num = Math.Max(1, allControllers.Count((CCSPlayerController p) => ((CEntityInstance)p).IsValid && p.IsBot && ((CBaseEntity)p).TeamNum == teamNum));
			if (!_roundCountByTeam.TryGetValue(teamNum, out RoundCounter value))
			{
				value = new RoundCounter();
			}
			if ((grenadeType == "flash" || grenadeType == "he" || grenadeType == "molotov") && value.Flash + value.HE + value.Molotov >= 3 * num)
			{
				return;
			}
			switch (grenadeType)
			{
			default:
				return;
			case "flash":
			{
				ConVar obj = ConVar.Find("ammo_grenade_limit_flashbang");
				int num2 = ((obj != null) ? obj.GetPrimitiveValue<int>() : 2) * num;
				if (value.Flash >= num2)
				{
					return;
				}
				break;
			}
			case "smoke":
				if (value.Smoke >= num)
				{
					return;
				}
				break;
			case "he":
				if (value.HE >= num)
				{
					return;
				}
				break;
			case "molotov":
				if (value.Molotov >= num)
				{
					return;
				}
				break;
			}
		}
		CCSPlayerPawn? pPawn = bot.PlayerPawn?.Value;
		if (pPawn == null || !pPawn.IsValid || !HasGrenadeInInventory(pPawn, grenadeType, out string wName, out int itemDef))
		{
			return;
		}

		uint index = ((CEntityInstance)bot).Index;
		_replayBots.Add(index);
		_pendingWindupLineups[g.Id] = Server.CurrentTime + 1.2f;
		ExecuteRealisticThrow(bot, g, wName, itemDef);
		((BasePlugin)this).AddTimer(1f, (Action)delegate
		{
			_replayBots.Remove(index);
		}, (TimerFlags?)null);
	}

	private void ExecuteRealisticThrow(CCSPlayerController bot, GrenadeData g, string weaponDesignerName, int itemDefIndex)
	{
		CCSPlayerPawn? pawn = bot.PlayerPawn?.Value;
		if (pawn == null || !pawn.IsValid || !bot.PawnIsAlive) return;

		int slot = bot.Slot;
		var api = GetControllerApi();

		// 1. Align view angles towards throw direction and register continuous 350ms wind-up brake
		float vx = g.ProjectileVelocity.X;
		float vy = g.ProjectileVelocity.Y;
		float vz = g.ProjectileVelocity.Z;
		float yaw = MathF.Atan2(vy, vx) * (180f / MathF.PI);
		float hDist = MathF.Sqrt(vx * vx + vy * vy);
		float pitch = (0f - MathF.Atan2(vz, hDist)) * (180f / MathF.PI);

		if (pawn.AbsVelocity != null)
		{
			pawn.AbsVelocity.X *= 0.25f;
			pawn.AbsVelocity.Y *= 0.25f;
		}

		if (api != null && slot >= 0)
		{
			api.SwitchBotWeapon(slot, itemDefIndex);
			api.Lock(slot, LockTarget.Slot4);
			api.Lock(slot, LockKind.Aim);
		}

		if (slot >= 0)
		{
			_activeWindups[slot] = new ActiveWindup(slot, yaw, pitch, Server.CurrentTime + 0.38f);
		}

		if (pawn.EyeAngles != null) { pawn.EyeAngles.Y = yaw; pawn.EyeAngles.X = pitch; }
		if (pawn.V_angle != null) { pawn.V_angle.Y = yaw; pawn.V_angle.X = pitch; }
		if (pawn.Bot != null)
		{
			pawn.Bot.ForwardSpeed = 0f;
			pawn.Bot.LeftSpeed = 0f;
			pawn.Bot.LookYaw = yaw;
			pawn.Bot.LookPitch = pitch;
		}

		// 2. 350ms throw animation wind-up -> release from bot's exact eye/hand position
		AddTimer(0.35f, () =>
		{
			try
			{
				_activeWindups.Remove(slot);
				if (api != null && slot >= 0)
				{
					api.Unlock(slot, LockKind.Aim);
				}

				if (bot == null || !bot.IsValid || !bot.PawnIsAlive)
				{
					_pendingWindupLineups.Remove(g.Id);
					if (api != null && slot >= 0) api.Unlock(slot, LockKind.Weapon);
					return;
				}

				CCSPlayerPawn? livePawn = bot.PlayerPawn?.Value;
				if (livePawn == null || !livePawn.IsValid || livePawn.AbsOrigin == null)
				{
					_pendingWindupLineups.Remove(g.Id);
					if (api != null && slot >= 0) api.Unlock(slot, LockKind.Weapon);
					return;
				}

				// If an enemy swung directly into sight during wind-up of a smoke, cancel throw and pull rifle back out!
				if (g.GrenadeType == "smoke" && livePawn.Bot != null && livePawn.Bot.IsEnemyVisible)
				{
					_pendingWindupLineups.Remove(g.Id);
					if (api != null && slot >= 0) api.Unlock(slot, LockKind.Weapon);
					return;
				}

				Vector currentEyePos = new Vector(livePawn.AbsOrigin.X, livePawn.AbsOrigin.Y, livePawn.AbsOrigin.Z + (livePawn.ViewOffset?.Z ?? 64f));

				// Re-verify wall clearance right in front of the bot's face at release moment!
				if (!HasForwardClearance(currentEyePos, yaw, pitch, 34f))
				{
					_pendingWindupLineups.Remove(g.Id);
					if (api != null && slot >= 0) api.Unlock(slot, LockKind.Weapon);
					return;
				}

				Vector targetPos = new Vector(g.LandingPosition.X, g.LandingPosition.Y, g.LandingPosition.Z);
				Vector actualOrigin;
				Vector actualVelocity;

				if (TrySolveBallisticArc(currentEyePos, targetPos, allowWallBounce: false, out Vector solvedOrigin, out Vector solvedVelocity))
				{
					actualOrigin = solvedOrigin;
					actualVelocity = solvedVelocity;
				}
				else if (TrySolveBallisticArc(currentEyePos, targetPos, allowWallBounce: true, out Vector bounceOrigin, out Vector bounceVelocity))
				{
					actualOrigin = bounceOrigin;
					actualVelocity = bounceVelocity;
				}
				else
				{
					// Recorded multi-bank / skybox wall-bounce lineup:
					// Trace recorded initial bounce point B from g.ProjectilePosition along g.ProjectileVelocity,
					// and aim from bot's real hands directly at B so wall-bounce lineups remain accurate!
					float yawRad = yaw * (MathF.PI / 180f);
					actualOrigin = new Vector(currentEyePos.X + MathF.Cos(yawRad) * 18f, currentEyePos.Y + MathF.Sin(yawRad) * 18f, currentEyePos.Z);
					Vector origVel = new Vector(g.ProjectileVelocity.X, g.ProjectileVelocity.Y, g.ProjectileVelocity.Z);
					float origSpeed = MathF.Sqrt(origVel.X * origVel.X + origVel.Y * origVel.Y + origVel.Z * origVel.Z);

					bool aimedAtRecordedBounce = false;
					actualVelocity = origVel;

					try
					{
						CRayTraceInterface rt = _rayTraceCapability.Get();
						if (rt != null && origSpeed > 100f)
						{
							_traceA ??= new Vector(0f, 0f, 0f);
							_traceB ??= new Vector(0f, 0f, 0f);
							_traceA.X = g.ProjectilePosition.X; _traceA.Y = g.ProjectilePosition.Y; _traceA.Z = g.ProjectilePosition.Z;
							_traceB.X = g.ProjectilePosition.X + origVel.X * 0.65f;
							_traceB.Y = g.ProjectilePosition.Y + origVel.Y * 0.65f;
							_traceB.Z = g.ProjectilePosition.Z + origVel.Z * 0.65f - 180f * 0.65f * 0.65f;
							RayTraceAPI.TraceOptions opt = new RayTraceAPI.TraceOptions((InteractionLayers)2109441, (InteractionLayers)0, false);
							if (rt.TraceEndShape(_traceA, _traceB, null, opt, out RayTraceAPI.TraceResult bRes) && bRes.DidHit && bRes.Fraction > 0.12f && bRes.Fraction < 0.98f)
							{
								Vec3 bouncePt = new Vec3 { X = bRes.EndPosX, Y = bRes.EndPosY, Z = bRes.EndPosZ };
								if (HasReleaseLoS(actualOrigin.X, actualOrigin.Y, actualOrigin.Z, bouncePt))
								{
									float tHit = Math.Max(0.12f, 0.65f * bRes.Fraction);
									actualVelocity = new Vector(
										(bouncePt.X - actualOrigin.X) / tHit,
										(bouncePt.Y - actualOrigin.Y) / tHit,
										(bouncePt.Z - actualOrigin.Z + 180f * tHit * tHit) / tHit);
									aimedAtRecordedBounce = true;
								}
							}
						}
					}
					catch { }

					if (!aimedAtRecordedBounce)
					{
						float driftFromRecorded = Dist3D(currentEyePos.X, currentEyePos.Y, currentEyePos.Z, g.ProjectilePosition.X, g.ProjectilePosition.Y, g.ProjectilePosition.Z);
						bool initialArcClear = true;
						try
						{
							CRayTraceInterface rt2 = _rayTraceCapability.Get();
							if (rt2 != null && origSpeed > 100f)
							{
								initialArcClear = VerifyParabolicArcClear(rt2, actualOrigin, origVel.X, origVel.Y, origVel.Z, 0.70f, 4);
							}
						}
						catch { }

						if (driftFromRecorded > 145f || origSpeed <= 100f || !initialArcClear)
						{
							_pendingWindupLineups.Remove(g.Id);
							// Too far from recorded lineup or blocked by nearby wall -> abort cleanly instead of missing badly!
							if (api != null && slot >= 0) api.Unlock(slot, LockKind.Weapon);
							return;
						}
					}
				}

				float finalYaw = MathF.Atan2(actualVelocity.Y, actualVelocity.X) * (180f / MathF.PI);
				float finalHDist = MathF.Sqrt(actualVelocity.X * actualVelocity.X + actualVelocity.Y * actualVelocity.Y);
				float finalPitch = (0f - MathF.Atan2(actualVelocity.Z, finalHDist)) * (180f / MathF.PI);
				if (livePawn.EyeAngles != null) { livePawn.EyeAngles.Y = finalYaw; livePawn.EyeAngles.X = finalPitch; }
				if (livePawn.V_angle != null) { livePawn.V_angle.Y = finalYaw; livePawn.V_angle.X = finalPitch; }
				if (livePawn.Bot != null) { livePawn.Bot.LookYaw = finalYaw; livePawn.Bot.LookPitch = finalPitch; }

				// Spawn projectile from real eye/hand release position
				SpawnProjectileExplicit(bot, g.GrenadeType, actualOrigin, actualVelocity);
				_pendingWindupLineups.Remove(g.Id);
				RegisterCooldown(g.Id, g.GrenadeType);
				IncrementCount(g.GrenadeType, ((CBaseEntity)bot).TeamNum);
				if (_botNadesMode == "less")
				{
					IncrementBotCount(g.GrenadeType, ((CEntityInstance)bot).Index);
				}
				if ((_botNadesMode == "normal" || _botNadesMode == "less") && g.GrenadeType == "smoke" && _freezeEndTime > 0f && Server.CurrentTime - _freezeEndTime < 25f)
				{
					_earlySmokeCountByTeam.TryGetValue(((CBaseEntity)bot).TeamNum, out var value4);
					_earlySmokeCountByTeam[((CBaseEntity)bot).TeamNum] = value4 + 1;
				}

				// Remove / decrement physical item from inventory
				if (!string.IsNullOrEmpty(weaponDesignerName))
				{
					ConsumeGrenade(bot, weaponDesignerName);
				}

				// After 120ms follow-through, release weapon lock so bot re-equips best gun
				AddTimer(0.12f, () =>
				{
					if (api != null && slot >= 0)
					{
						api.Unlock(slot, LockKind.Weapon);
					}
				});
			}
			catch (Exception ex)
			{
				_pendingWindupLineups.Remove(g.Id);
				ReleaseBotThrowLocks(slot, unlockWeapon: true);
				Logger.LogError("[NadeSystem] Realistic throw error: {Msg}", ex.Message);
			}
		});
	}

	private void SpawnProjectile(CCSPlayerController bot, GrenadeData g)
	{
		Vector origin = new Vector(g.ProjectilePosition.X, g.ProjectilePosition.Y, g.ProjectilePosition.Z);
		Vector velocity = new Vector(g.ProjectileVelocity.X, g.ProjectileVelocity.Y, g.ProjectileVelocity.Z);
		SpawnProjectileExplicit(bot, g.GrenadeType, origin, velocity);
	}

	private void SpawnProjectileExplicit(CCSPlayerController bot, string gtype, Vector origin, Vector velocity)
	{
		gtype = gtype.ToLowerInvariant();
		float value = MathF.Atan2(velocity.Y, velocity.X) * (180f / (float)Math.PI);
		float x = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
		float value2 = (0f - MathF.Atan2(velocity.Z, x)) * (180f / (float)Math.PI);
		QAngle angles = new QAngle((float?)value2, (float?)value, (float?)0f);
		byte teamNum = ((CBaseEntity)bot).TeamNum;
		int itemDef = GetItemIndex(gtype);
		Server.NextFrame((Action)delegate
		{
			try
			{
				CCSPlayerPawn val = bot.PlayerPawn?.Value;
				if (!((CEntityInstance)(object)val == (CEntityInstance)null) && ((CEntityInstance)val).IsValid)
				{
					if (gtype == "flash")
					{
						CFlashbangProjectile val2 = Utilities.CreateEntityByName<CFlashbangProjectile>("flashbang_projectile");
						if ((CEntityInstance)(object)val2 != (CEntityInstance)null)
						{
							((CBaseEntity)val2).TeamNum = teamNum;
							((CBaseGrenade)val2).Thrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
							((CBaseGrenade)val2).OriginalThrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
							((CBaseEntity)val2).OwnerEntity.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
							((CBaseCSGrenadeProjectile)val2).InitialPosition.X = origin.X;
							((CBaseCSGrenadeProjectile)val2).InitialPosition.Y = origin.Y;
							((CBaseCSGrenadeProjectile)val2).InitialPosition.Z = origin.Z;
							((CBaseCSGrenadeProjectile)val2).InitialVelocity.X = velocity.X;
							((CBaseCSGrenadeProjectile)val2).InitialVelocity.Y = velocity.Y;
							((CBaseCSGrenadeProjectile)val2).InitialVelocity.Z = velocity.Z;
							((CBaseEntity)val2).Elasticity = 0.33f;
							((CBaseEntity)val2).Teleport(origin, angles, velocity);
							((CBaseEntity)val2).DispatchSpawn();
							((CBaseEntity)val2).Teleport(origin, angles, velocity);
							float value3 = Server.CurrentTime + 2f;
							{
								foreach (CCSPlayerController item in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
								{
									if (((CEntityInstance)item).IsValid && item.IsBot && ((CBaseEntity)item).TeamNum == ((CBaseEntity)bot).TeamNum)
									{
										_botFlashImmunityUntil[((CEntityInstance)item).Index] = value3;
									}
								}
								return;
							}
						}
					}
					else if (gtype == "decoy")
					{
						CFlashbangProjectile val3 = Utilities.CreateEntityByName<CFlashbangProjectile>("flashbang_projectile");
						if ((CEntityInstance)(object)val3 != (CEntityInstance)null)
						{
							((CBaseEntity)val3).TeamNum = teamNum;
							((CBaseGrenade)val3).Thrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
							((CBaseGrenade)val3).OriginalThrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
							((CBaseEntity)val3).OwnerEntity.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
							((CBaseCSGrenadeProjectile)val3).InitialPosition.X = origin.X;
							((CBaseCSGrenadeProjectile)val3).InitialPosition.Y = origin.Y;
							((CBaseCSGrenadeProjectile)val3).InitialPosition.Z = origin.Z;
							((CBaseCSGrenadeProjectile)val3).InitialVelocity.X = velocity.X;
							((CBaseCSGrenadeProjectile)val3).InitialVelocity.Y = velocity.Y;
							((CBaseCSGrenadeProjectile)val3).InitialVelocity.Z = velocity.Z;
							((CBaseEntity)val3).Elasticity = 0.33f;
							((CBaseEntity)val3).Teleport(origin, angles, velocity);
							((CBaseEntity)val3).DispatchSpawn();
							((CBaseEntity)val3).Teleport(origin, angles, velocity);
							StartDecoyFlashLoop(bot, val3, teamNum, angles);
						}
					}
					else if (gtype == "smoke")
					{
						Vector zeroAng = new Vector(0f, 0f, 0f); CSmokeGrenadeProjectile val4 = (((NativeObject)_smokeCreate).Handle != IntPtr.Zero) ? _smokeCreate.Invoke((nint)((NativeObject)origin).Handle, (nint)((NativeObject)angles).Handle, (nint)((NativeObject)velocity).Handle, (nint)((NativeObject)zeroAng).Handle, (nint)((NativeEntity)val).Handle, itemDef, (int)teamNum) : null;
						if ((CEntityInstance)(object)val4 != (CEntityInstance)null && ((CEntityInstance)val4).IsValid)
						{
							((CBaseEntity)val4).TeamNum = teamNum;
							((CBaseGrenade)val4).Thrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
							((CBaseGrenade)val4).OriginalThrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
							((CBaseEntity)val4).OwnerEntity.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
						}
						else
						{
							CSmokeGrenadeProjectile fallbackSmoke = Utilities.CreateEntityByName<CSmokeGrenadeProjectile>("smokegrenade_projectile");
							if (fallbackSmoke != null && fallbackSmoke.IsValid)
							{
								fallbackSmoke.TeamNum = (byte)teamNum;
								fallbackSmoke.Thrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
								fallbackSmoke.OriginalThrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
								fallbackSmoke.OwnerEntity.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
								fallbackSmoke.Teleport(origin, angles, velocity);
								fallbackSmoke.DispatchSpawn();
							}
						}
					}
					else if (gtype == "he")
					{
						Vector zeroAng = new Vector(0f, 0f, 0f); CHEGrenadeProjectile val5 = (((NativeObject)_heCreate).Handle != IntPtr.Zero) ? _heCreate.Invoke((nint)((NativeObject)origin).Handle, (nint)((NativeObject)angles).Handle, (nint)((NativeObject)velocity).Handle, (nint)((NativeObject)zeroAng).Handle, (nint)((NativeEntity)val).Handle, itemDef) : null;
						if ((CEntityInstance)(object)val5 != (CEntityInstance)null && ((CEntityInstance)val5).IsValid)
						{
							((CBaseEntity)val5).TeamNum = teamNum;
							((CBaseGrenade)val5).Thrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
							((CBaseGrenade)val5).OriginalThrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
							((CBaseEntity)val5).OwnerEntity.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
						}
						else
						{
							CHEGrenadeProjectile fallbackHe = Utilities.CreateEntityByName<CHEGrenadeProjectile>("hegrenade_projectile");
							if (fallbackHe != null && fallbackHe.IsValid)
							{
								fallbackHe.TeamNum = (byte)teamNum;
								fallbackHe.Thrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
								fallbackHe.OriginalThrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
								fallbackHe.OwnerEntity.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
								fallbackHe.Teleport(origin, angles, velocity);
								fallbackHe.DispatchSpawn();
							}
						}
					}
					else if (gtype == "molotov" || gtype == "incgrenade")
					{
						int num = ((teamNum == 3) ? 48 : 46);
						Vector zeroAng = new Vector(0f, 0f, 0f); CMolotovProjectile val6 = (((NativeObject)_molotovCreate).Handle != IntPtr.Zero) ? _molotovCreate.Invoke((nint)((NativeObject)origin).Handle, (nint)((NativeObject)angles).Handle, (nint)((NativeObject)velocity).Handle, (nint)((NativeObject)zeroAng).Handle, (nint)((NativeEntity)val).Handle, num) : null;
						if ((CEntityInstance)(object)val6 != (CEntityInstance)null && ((CEntityInstance)val6).IsValid)
						{
							((CBaseEntity)val6).TeamNum = teamNum;
							((CBaseGrenade)val6).Thrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
							((CBaseGrenade)val6).OriginalThrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
							((CBaseEntity)val6).OwnerEntity.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
						}
						else
						{
							CMolotovProjectile fallbackMolotov = Utilities.CreateEntityByName<CMolotovProjectile>("molotov_projectile");
							if (fallbackMolotov != null && fallbackMolotov.IsValid)
							{
								fallbackMolotov.TeamNum = (byte)teamNum;
								fallbackMolotov.Thrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
								fallbackMolotov.OriginalThrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
								fallbackMolotov.OwnerEntity.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
								fallbackMolotov.Teleport(origin, angles, velocity);
								fallbackMolotov.DispatchSpawn();
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				Server.PrintToConsole("[NadeSystem] SpawnProjectile error: " + ex.Message);
			}
		});
		static ushort GetItemIndex(string t)
		{
			return t switch
			{
				"smoke" => 45, 
				"flash" => 43, 
				"he" => 44, 
				_ => 45, 
			};
		}
	}

	private void StartDecoyFlashLoop(CCSPlayerController bot, CFlashbangProjectile flash, int teamNum, QAngle angles)
	{
		((BasePlugin)this).AddTimer(1f, (Action)delegate
		{
			//IL_00dd: Unknown result type (might be due to invalid IL or missing references)
			//IL_00e3: Expected O, but got Unknown
			//IL_0107: Unknown result type (might be due to invalid IL or missing references)
			//IL_010e: Expected O, but got Unknown
			if (((CEntityInstance)flash).IsValid)
			{
				Vector absOrigin = ((CBaseEntity)flash).AbsOrigin;
				Vector absVelocity = ((CBaseEntity)flash).AbsVelocity;
				if (absOrigin != null && absVelocity != null)
				{
					float num = MathF.Sqrt(absVelocity.X * absVelocity.X + absVelocity.Y * absVelocity.Y + absVelocity.Z * absVelocity.Z);
					((CEntityInstance)flash).AcceptInput("Kill", (CEntityInstance)null, (CEntityInstance)null, "", 0);
					if (num >= 5f)
					{
						CCSPlayerPawn val = bot.PlayerPawn?.Value;
						if ((CEntityInstance)(object)val != (CEntityInstance)null && ((CEntityInstance)val).IsValid)
						{
							Vector val2 = new Vector((float?)absOrigin.X, (float?)absOrigin.Y, (float?)absOrigin.Z);
							Vector val3 = new Vector((float?)absVelocity.X, (float?)absVelocity.Y, (float?)absVelocity.Z);
							CFlashbangProjectile val4 = Utilities.CreateEntityByName<CFlashbangProjectile>("flashbang_projectile");
							if ((CEntityInstance)(object)val4 != (CEntityInstance)null)
							{
								((CBaseEntity)val4).TeamNum = (byte)teamNum;
								((CBaseGrenade)val4).Thrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
								((CBaseGrenade)val4).OriginalThrower.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
								((CBaseEntity)val4).OwnerEntity.Raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)val).EntityHandle).Raw;
								((CBaseEntity)val4).Elasticity = 0.33f;
								((CBaseEntity)val4).Teleport(val2, angles, val3);
								((CBaseEntity)val4).DispatchSpawn();
								((CBaseEntity)val4).Teleport(val2, angles, val3);
								StartDecoyFlashLoop(bot, val4, teamNum, angles);
							}
						}
					}
				}
			}
		}, (TimerFlags?)null);
	}

	private bool IsLineupPending(string id)
	{
		return _pendingWindupLineups.TryGetValue(id, out float expires) && expires > Server.CurrentTime;
	}

	private bool IsOnCooldown(string id)
	{
		return _cooldowns.Any((CooldownEntry c) => c.GrenadeId == id && c.ExpiresAt > Server.CurrentTime);
	}

	private void RegisterCooldown(string id, string gtype)
	{
		_cooldowns.RemoveAll((CooldownEntry c) => c.GrenadeId == id);
		float num = (CooldownSec.TryGetValue(gtype, out var value) ? value : 10f);
		_cooldowns.Add(new CooldownEntry
		{
			GrenadeId = id,
			ExpiresAt = Server.CurrentTime + num
		});
	}

	private void PruneCooldowns()
	{
		float now = Server.CurrentTime;
		_cooldowns.RemoveAll((CooldownEntry c) => c.ExpiresAt <= now);
	}

	private bool IsOnProbFailCooldown(string id)
	{
		if (_probFailCooldown.TryGetValue(id, out var value))
		{
			return value > Server.CurrentTime;
		}
		return false;
	}

	private void RegisterProbFailCooldown(string id)
	{
		_probFailCooldown[id] = Server.CurrentTime + 3f;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static float Dist3D(float x1, float y1, float z1, float x2, float y2, float z2)
	{
		float num = x1 - x2;
		float num2 = y1 - y2;
		float num3 = z1 - z2;
		return MathF.Sqrt(num * num + num2 * num2 + num3 * num3);
	}

	private void IncrementCount(string gtype, int teamNum)
	{
		if (!_roundCountByTeam.TryGetValue(teamNum, out RoundCounter value))
		{
			value = new RoundCounter();
		}
		switch (gtype.ToLowerInvariant())
		{
		case "flash":
			value.Flash++;
			break;
		case "smoke":
			value.Smoke++;
			break;
		case "he":
			value.HE++;
			break;
		case "molotov":
			value.Molotov++;
			break;
		}
		_roundCountByTeam[teamNum] = value;
	}

	private void IncrementBotCount(string gtype, uint botIdx)
	{
		if (gtype == "incgrenade")
		{
			gtype = "molotov";
		}
		if (!_roundCountByBot.TryGetValue(botIdx, out RoundCounter value))
		{
			value = new RoundCounter();
		}
		switch (gtype.ToLowerInvariant())
		{
		case "flash":
			value.Flash++;
			break;
		case "smoke":
			value.Smoke++;
			break;
		case "he":
			value.HE++;
			break;
		case "molotov":
			value.Molotov++;
			break;
		}
		_roundCountByBot[botIdx] = value;
	}

	private bool LessModeAllows(string gtype, uint botIdx)
	{
		if (gtype == "incgrenade")
		{
			gtype = "molotov";
		}
		if (!_roundCountByBot.TryGetValue(botIdx, out RoundCounter value))
		{
			value = new RoundCounter();
		}
		if (value.Flash + value.Smoke + value.HE + value.Molotov >= 4)
		{
			return false;
		}
		switch (gtype)
		{
		case "flash":
		{
			int flash = value.Flash;
			ConVar obj = ConVar.Find("ammo_grenade_limit_flashbang");
			return flash < ((obj != null) ? obj.GetPrimitiveValue<int>() : 2);
		}
		case "smoke":
			return value.Smoke < 1;
		case "he":
			return value.HE < 1;
		case "molotov":
			return value.Molotov < 1;
		default:
			return false;
		}
	}

	private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
	{
		ResetServerMaxPlayersCache();
		ReleaseAllThrowLocks();
		_roundOver = false;
		_freezeEndTime = 0f;
		if (_mapNades == null || _mapNades.Count == 0 || !_spawnsFromEntities || !string.Equals(_loadedMapName, Server.MapName, StringComparison.OrdinalIgnoreCase))
		{
			RefreshMapNades(Server.MapName);
		}
		_roundCountByTeam.Clear();
		_roundCountByBot.Clear();
		_cooldowns.Clear();
		_pendingWindupLineups.Clear();
		_replayBots.Clear();
		_smokeCooldownBots.Clear();
		_roundSpendPerBot.Clear();
		_defuseSmokeUsed = false;
		_defuseFlashUsed = false;
		_plantSmokeUsed = false;
		_botMolotovDmgStart.Clear();
		_earlySmokeCountByTeam.Clear();
		_botInFlashZone.Clear();
		_botFlashRatioWindow.Clear();
		_botFlashImmunityUntil.Clear();
		_molotovEscapeSmokeCooldown.Clear();
		_retaliationCooldown.Clear();
		_soundPoints.Clear();
		_botLastFireTime.Clear();
		_botLastHurtTime.Clear();
		_dynamicNadeCooldownByBot.Clear();
		foreach (string item in (from kv in _probFailCooldown
			where kv.Value <= Server.CurrentTime
			select kv.Key).ToList())
		{
			_probFailCooldown.Remove(item);
		}
		_poorBots.Clear();
		if (!IsPistolRound())
		{
			foreach (CCSPlayerController item2 in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
			{
				if (((CEntityInstance)item2).IsValid && item2.IsBot && item2.InGameMoneyServices != null && item2.InGameMoneyServices.Account < 2800)
				{
					_poorBots.Add(((CEntityInstance)item2).Index);
				}
			}
		}
		return (HookResult)0;
	}

	private HookResult OnFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
	{
		_freezeEndTime = Server.CurrentTime;
		return (HookResult)0;
	}

	private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
	{
		ReleaseAllThrowLocks();
		_roundOver = true;
		return (HookResult)0;
	}

	private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
	{
		CCSPlayerController userid = @event.Userid;
		if ((CEntityInstance)(object)userid != (CEntityInstance)null && ((CEntityInstance)userid).IsValid)
		{
			_soundPoints.Remove(((CEntityInstance)userid).Index);
		}
		return (HookResult)0;
	}

	private static bool IsPistolRound()
	{
		try
		{
			CCSGameRulesProxy? obj = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
			CCSGameRules val = ((obj != null) ? obj.GameRules : null);
			if (val == null)
			{
				return false;
			}
			int totalRoundsPlayed = val.TotalRoundsPlayed;
			ConVar obj2 = ConVar.Find("mp_maxrounds");
			int num = ((obj2 != null) ? obj2.GetPrimitiveValue<int>() : 24);
			if (num <= 0)
			{
				num = 24;
			}
			return totalRoundsPlayed == 0 || totalRoundsPlayed == num / 2;
		}
		catch
		{
			return false;
		}
	}

	private int GetRoundSpendCap(bool isCT, bool isPoor)
	{
		if (IsPistolRound())
		{
			return 800;
		}
		if (isPoor)
		{
			return 500;
		}
		Dictionary<string, int> dictionary = (isCT ? CostCT : CostT);
		return dictionary["flash"] + dictionary["smoke"] + dictionary["he"] + dictionary["molotov"];
	}

	private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
	{
		// Realism: Do NOT artificially reduce flashbang duration on bots!
		// Bots should be blinded fairly like any human player when hit by a flashbang.
		return (HookResult)0;
	}

	private void CmdBotNades(CCSPlayerController? player, CommandInfo info)
	{
		if (info.ArgCount < 2)
		{
			Server.PrintToConsole("[NadeSystem] bot_nades = " + _botNadesMode);
			return;
		}
		string text = info.GetArg(1).ToLowerInvariant();
		if (text != "off" && text != "less" && text != "normal" && text != "more" && text != "max")
		{
			Server.PrintToConsole("[NadeSystem] Usage: bot_nades <off|less|normal|more|max>");
			return;
		}
		_botNadesMode = text;
		Server.PrintToConsole("[NadeSystem] bot_nades set to " + _botNadesMode);
	}

	private void TryConditionalReplay(CCSPlayerController bot, GrenadeData g, List<CCSPlayerController> allControllers)
	{
		CCSPlayerPawn val = bot.PlayerPawn?.Value;
		if ((CEntityInstance)(object)val != (CEntityInstance)null && ((CEntityInstance)val).IsValid && !FiredRecently(bot, 2f) && PassesSituationalCheck(bot, val, g, g.GrenadeType, allControllers))
		{
			TryReplay(bot, g, allControllers);
		}
	}

	private static bool HasTeammateNearby(CCSPlayerController bot, CCSPlayerPawn? botPawn, List<CCSPlayerController> allControllers, float radius)
	{
		Vector? bPos = (botPawn != null) ? ((CBaseEntity)botPawn).AbsOrigin : null;
		if (bPos == null) return false;
		float rSq = radius * radius;
		foreach (var p in allControllers)
		{
			if (!((CEntityInstance)p).IsValid || !p.PawnIsAlive || ((CBaseEntity)p).TeamNum != ((CBaseEntity)bot).TeamNum || ((CEntityInstance)p).Index == ((CEntityInstance)bot).Index)
				continue;
			CCSPlayerPawn? tmPawn = GetActiveLivePawn(p);
			Vector? tPos = (tmPawn != null) ? ((CBaseEntity)tmPawn).AbsOrigin : null;
			if (tPos == null) continue;
			float dx = tPos.X - bPos.X;
			float dy = tPos.Y - bPos.Y;
			float dz = tPos.Z - bPos.Z;
			if (dx * dx + dy * dy + dz * dz <= rSq)
				return true;
		}
		return false;
	}

	private static bool HasTeammateNearSpot(CCSPlayerController bot, float lx, float ly, float lz, List<CCSPlayerController> allControllers, float safeRadius)
	{
		float rSq = safeRadius * safeRadius;
		foreach (var p in allControllers)
		{
			if (!((CEntityInstance)p).IsValid || !p.PawnIsAlive || ((CBaseEntity)p).TeamNum != ((CBaseEntity)bot).TeamNum)
				continue;
			CCSPlayerPawn? tmPawn = GetActiveLivePawn(p);
			Vector? tPos = (tmPawn != null) ? ((CBaseEntity)tmPawn).AbsOrigin : null;
			if (tPos == null) continue;
			float dx = tPos.X - lx;
			float dy = tPos.Y - ly;
			float dz = tPos.Z - lz;
			if (dx * dx + dy * dy + dz * dz <= rSq)
				return true;
		}
		return false;
	}

	private bool PassesSituationalCheck(CCSPlayerController bot, CCSPlayerPawn pawn, GrenadeData g, string gtype, List<CCSPlayerController> allControllers)
	{
		if (!HasLiveEnemyForTeam(((CBaseEntity)bot).TeamNum, allControllers))
		{
			return false;
		}
		if (gtype == "he" || gtype == "molotov")
		{
			if (FiredRecently(bot, 1f))
			{
				return false;
			}
			float lx = g.LandingPosition.X;
			float ly = g.LandingPosition.Y;
			float lz = g.LandingPosition.Z;
			if (HasTeammateNearSpot(bot, lx, ly, lz, allControllers, 280f))
			{
				return false;
			}
			if (gtype == "molotov" && !IsNoActiveSmokeNear(lx, ly, lz, 200f))
			{
				return false;
			}
			List<CCSPlayerController> knownEnemiesAtSpot = allControllers.Where(delegate(CCSPlayerController p)
			{
				if (!((CEntityInstance)p).IsValid || ((CBaseEntity)p).TeamNum == ((CBaseEntity)bot).TeamNum)
				{
					return false;
				}
				CCSPlayerPawn? activeLivePawn = GetActiveLivePawn(p);
				Vector val2 = ((activeLivePawn != null) ? ((CBaseEntity)activeLivePawn).AbsOrigin : null);
				if (val2 == null)
				{
					return false;
				}
				float num3 = val2.X - lx;
				float num4 = val2.Y - ly;
				float num5 = val2.Z - lz;
				return num3 * num3 + num4 * num4 + num5 * num5 <= 48400f && TeamHasInformationOn(bot, p, allControllers);
			}).ToList();

			if (knownEnemiesAtSpot.Count > 0)
			{
				return true;
			}

			// Proactive Map Molotov (Zero Radar-Hack!):
			// When pushing with a teammate (<= 900u) during the first 45s of the round, allow clearing default corners
			// (e.g. Under Palace, Sandwich, Car) based strictly on team presence, WITHOUT reading hidden enemy coordinates!
			if (gtype == "molotov" && _freezeEndTime > 0f && Server.CurrentTime - _freezeEndTime < 45f && HasTeammateNearby(bot, pawn, allControllers, 900f))
			{
				return true;
			}
			return false;
		}
		if (gtype == "flash")
		{
			if (!PassesTeamAndScheduleCheck(bot, g, allControllers))
			{
				return false;
			}
			List<CCSPlayerController> list2 = CanBlindAnyEnemy(bot, g, allControllers);
			if (list2.Count > 0 && list2.Any((CCSPlayerController e) => TeamHasInformationOn(bot, e, allControllers)))
			{
				return true;
			}
			// Proactive Entry Flash (Zero Radar-Hack!):
			// When executing/pushing with a teammate (<= 900u) during the first 45s, throw the recorded entry flash!
			if (_freezeEndTime > 0f && Server.CurrentTime - _freezeEndTime < 45f &&
			    HasTeammateNearby(bot, pawn, allControllers, 900f) &&
			    !HasTeammateNearSpot(bot, g.LandingPosition.X, g.LandingPosition.Y, g.LandingPosition.Z, allControllers, 320f))
			{
				return true;
			}
			return false;
		}
		if (gtype == "smoke")
		{
			if (!PassesTeamAndScheduleCheck(bot, g, allControllers))
			{
				return false;
			}
			float lx2 = g.LandingPosition.X;
			float ly2 = g.LandingPosition.Y;
			float lz2 = g.LandingPosition.Z;
			if (!IsNoActiveSmokeNear(lx2, ly2, lz2, 250f))
			{
				return false;
			}
			if ((_botNadesMode == "normal" || _botNadesMode == "less") && _freezeEndTime > 0f && Server.CurrentTime - _freezeEndTime < 15f)
			{
				_earlySmokeCountByTeam.TryGetValue(((CBaseEntity)bot).TeamNum, out var value);
				int maxEarlySmokes = (((CBaseEntity)bot).TeamNum == 2) ? 3 : 2;
				if (value >= maxEarlySmokes)
				{
					return false;
				}
			}
		}
		return true;
	}

	private bool PassesTeamAndScheduleCheck(CCSPlayerController bot, GrenadeData g, List<CCSPlayerController> allControllers)
	{
		if (!string.IsNullOrEmpty(g.TeamTag))
		{
			string text = ((((CBaseEntity)bot).TeamNum == 3) ? "CT" : "T");
			if (g.TeamTag != text)
			{
				return false;
			}
		}
		if (_freezeEndTime <= 0f)
		{
			return false;
		}
		return HasLiveEnemyForTeam(((CBaseEntity)bot).TeamNum, allControllers);
	}

	private List<CCSPlayerController> CanBlindAnyEnemy(CCSPlayerController bot, GrenadeData g, List<CCSPlayerController> allControllers)
	{
		List<CCSPlayerController> list = new List<CCSPlayerController>();
		float x = g.LandingPosition.X;
		float y = g.LandingPosition.Y;
		float z = g.LandingPosition.Z;
		foreach (CCSPlayerController allController in allControllers)
		{
			if (!((CEntityInstance)allController).IsValid || ((CBaseEntity)allController).TeamNum == ((CBaseEntity)bot).TeamNum)
			{
				continue;
			}
			CCSPlayerPawn activeLivePawn = GetActiveLivePawn(allController);
			if (((activeLivePawn != null) ? ((CBaseEntity)activeLivePawn).AbsOrigin : null) == null || activeLivePawn.EyeAngles == null)
			{
				continue;
			}
			float x2 = ((CBaseEntity)activeLivePawn).AbsOrigin.X;
			float y2 = ((CBaseEntity)activeLivePawn).AbsOrigin.Y;
			float num = ((CBaseEntity)activeLivePawn).AbsOrigin.Z + 64f;
			float num2 = x - x2;
			float num3 = y - y2;
			float num4 = z - num;
			if (!(num2 * num2 + num3 * num3 + num4 * num4 > 1690000f))
			{
				float x3 = activeLivePawn.EyeAngles.Y * ((float)Math.PI / 180f);
				float x4 = (0f - activeLivePawn.EyeAngles.X) * ((float)Math.PI / 180f);
				float num5 = MathF.Cos(x4) * MathF.Cos(x3);
				float num6 = MathF.Cos(x4) * MathF.Sin(x3);
				float y3 = MathF.Sin(x4);
				float num7 = MathF.Atan2(num3, num2);
				float num8 = MathF.Atan2(num6, num5);
				float num9 = MathF.Abs(MathF.Atan2(MathF.Sin(num7 - num8), MathF.Cos(num7 - num8)));
				float num10 = MathF.Atan2(num4, MathF.Sqrt(num2 * num2 + num3 * num3));
				float num11 = MathF.Atan2(y3, MathF.Sqrt(num5 * num5 + num6 * num6));
				float num12 = MathF.Abs(num10 - num11);
				if (num9 <= 0.927f && num12 <= (float)Math.PI / 4f && FlashHasLoS(g.LandingPosition, x2, y2, num))
				{
					list.Add(allController);
				}
			}
		}
		return list;
	}

	private (int blindable, int total) CountBlindableEnemies(CCSPlayerController bot, GrenadeData g, List<CCSPlayerController> allControllers)
	{
		float x = g.LandingPosition.X;
		float y = g.LandingPosition.Y;
		float z = g.LandingPosition.Z;
		int num = 0;
		int num2 = 0;
		foreach (CCSPlayerController allController in allControllers)
		{
			if (!((CEntityInstance)allController).IsValid || ((CBaseEntity)allController).TeamNum == ((CBaseEntity)bot).TeamNum)
			{
				continue;
			}
			CCSPlayerPawn activeLivePawn = GetActiveLivePawn(allController);
			if ((CEntityInstance)(object)activeLivePawn == (CEntityInstance)null)
			{
				continue;
			}
			num2++;
			if (((CBaseEntity)activeLivePawn).AbsOrigin == null || activeLivePawn.EyeAngles == null)
			{
				continue;
			}
			float x2 = ((CBaseEntity)activeLivePawn).AbsOrigin.X;
			float y2 = ((CBaseEntity)activeLivePawn).AbsOrigin.Y;
			float num3 = ((CBaseEntity)activeLivePawn).AbsOrigin.Z + 64f;
			float num4 = x - x2;
			float num5 = y - y2;
			float num6 = z - num3;
			if (!(num4 * num4 + num5 * num5 + num6 * num6 > 1690000f))
			{
				float x3 = activeLivePawn.EyeAngles.Y * ((float)Math.PI / 180f);
				float x4 = (0f - activeLivePawn.EyeAngles.X) * ((float)Math.PI / 180f);
				float num7 = MathF.Cos(x4) * MathF.Cos(x3);
				float num8 = MathF.Cos(x4) * MathF.Sin(x3);
				float y3 = MathF.Sin(x4);
				float num9 = MathF.Atan2(num5, num4);
				float num10 = MathF.Atan2(num8, num7);
				float num11 = MathF.Abs(MathF.Atan2(MathF.Sin(num9 - num10), MathF.Cos(num9 - num10)));
				float num12 = MathF.Atan2(num6, MathF.Sqrt(num4 * num4 + num5 * num5));
				float num13 = MathF.Atan2(y3, MathF.Sqrt(num7 * num7 + num8 * num8));
				float num14 = MathF.Abs(num12 - num13);
				if (num11 <= 0.927f && num14 <= (float)Math.PI / 4f && FlashHasLoS(g.LandingPosition, x2, y2, num3))
				{
					num++;
				}
			}
		}
		return (blindable: num, total: num2);
	}

	private bool FlashHasLoS(Vec3 landing, float eyeX, float eyeY, float eyeZ)
	{
		//IL_0091: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			CRayTraceInterface val = _rayTraceCapability.Get();
			if (val == null)
			{
				return true;
			}
			_traceA ??= new Vector(0f, 0f, 0f);
			_traceB ??= new Vector(0f, 0f, 0f);
			_traceA.X = landing.X;
			_traceA.Y = landing.Y;
			_traceA.Z = landing.Z;
			_traceB.X = eyeX;
			_traceB.Y = eyeY;
			_traceB.Z = eyeZ;
			RayTraceAPI.TraceOptions val2 = new RayTraceAPI.TraceOptions((InteractionLayers)12289, (InteractionLayers)0, false);
			RayTraceAPI.TraceResult val3 = default;
			val.TraceEndShape(_traceA, _traceB, (CEntityInstance)null, val2, out val3);
			return val3.Fraction >= 0.99f;
		}
		catch
		{
			return true;
		}
	}

	private static float GetFlashRatioThreshold(int blindable, int total)
	{
		if (total == 0 || blindable <= 0)
		{
			return 0f;
		}
		if (blindable == total || (blindable == 4 && total == 5))
		{
			return 1f;
		}
		if (blindable == 3 && total == 4)
		{
			return 0.9f;
		}
		if (blindable == 2 && total == 3)
		{
			return 0.8f;
		}
		if (blindable == 3 && total == 5)
		{
			return 0.7f;
		}
		if ((blindable == 2 && total == 4) || (blindable == 1 && total == 2))
		{
			return 0.6f;
		}
		if (blindable == 2 && total == 5)
		{
			return 0.5f;
		}
		if (blindable == 1 && total == 3)
		{
			return 0.35f;
		}
		if (blindable == 1 && total == 4)
		{
			return 0.25f;
		}
		if (blindable == 1 && total == 5)
		{
			return 0.15f;
		}
		return 0f;
	}

	private void TrySpawnInstantGrenade(CCSPlayerController bot, Vector spawnPos, string gtype, Vector? velocity = null, bool switchHandBriefly = false)
	{
		if (_botNadesMode == "off" || bot.HasBeenControlledByPlayerThisRound || !Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").Any((CCSPlayerController p) => ((CEntityInstance)p).IsValid && p.PawnIsAlive && (((CBaseEntity)p).TeamNum == 2 || ((CBaseEntity)p).TeamNum == 3) && ((CBaseEntity)p).TeamNum != ((CBaseEntity)bot).TeamNum))
		{
			return;
		}
		CCSPlayerPawn? val = bot.PlayerPawn?.Value;
		if (val == null || !val.IsValid || !bot.PawnIsAlive || val.AbsOrigin == null)
		{
			return;
		}

		if (!HasGrenadeInInventory(val, gtype, out string wName, out int itemDef))
		{
			return;
		}

		float yawRad = ((val.EyeAngles?.Y ?? 0f) * (MathF.PI / 180f));
		float fx = MathF.Cos(yawRad);
		float fy = MathF.Sin(yawRad);
		Vector handOrigin = new Vector(val.AbsOrigin.X + fx * 14f, val.AbsOrigin.Y + fy * 14f, val.AbsOrigin.Z + 44f);
		Vector underhandVel = velocity ?? new Vector(fx * 45f, fy * 45f, -135f);

		if (switchHandBriefly && !val.IsDefusing && bot.Slot >= 0)
		{
			int slot = bot.Slot;
			var api = GetControllerApi();
			if (api != null)
			{
				api.SwitchBotWeapon(slot, itemDef);
				api.Lock(slot, LockTarget.Slot4);
				AddTimer(0.18f, () =>
				{
					try
					{
						api.Unlock(slot, LockKind.Weapon);
						if (bot.IsValid && bot.PawnIsAlive)
						{
							CCSPlayerPawn? lp = bot.PlayerPawn?.Value;
							if (lp != null && lp.IsValid && lp.AbsOrigin != null && HasGrenadeInInventory(lp, gtype, out string lwName, out _))
							{
								Vector dropPos = new Vector(lp.AbsOrigin.X + fx * 14f, lp.AbsOrigin.Y + fy * 14f, lp.AbsOrigin.Z + 44f);
								ConsumeGrenade(bot, lwName);
								SpawnProjectileExplicit(bot, gtype, dropPos, underhandVel);
							}
						}
					}
					catch { api.Unlock(slot, LockKind.Weapon); }
				});
				return;
			}
		}

		ConsumeGrenade(bot, wName);
		SpawnProjectileExplicit(bot, gtype, handOrigin, underhandVel);
	}

	private HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
	{
		//IL_00bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Expected O, but got Unknown
		//IL_0190: Unknown result type (might be due to invalid IL or missing references)
		//IL_019a: Expected O, but got Unknown
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		RecordSoundPoint(@event.Userid);
		CCSPlayerController userid = @event.Userid;
		if ((CEntityInstance)(object)userid != (CEntityInstance)null && ((CEntityInstance)userid).IsValid && userid.IsBot && !userid.HasBeenControlledByPlayerThisRound)
		{
			CCSPlayerPawn val = userid.PlayerPawn?.Value;
			if ((CEntityInstance)(object)val != (CEntityInstance)null && ((CEntityInstance)val).IsValid && userid.PawnIsAlive && ((CBaseEntity)val).AbsOrigin != null)
			{
				Vector spawnPos = new Vector((float?)((CBaseEntity)val).AbsOrigin.X, (float?)((CBaseEntity)val).AbsOrigin.Y, (float?)(((CBaseEntity)val).AbsOrigin.Z + 5f));
				if (!_defuseSmokeUsed && ((((CBasePlayerPawn)val).ItemServices != null && ((NativeObject)((CBasePlayerPawn)val).ItemServices).Handle != (IntPtr)IntPtr.Zero && new CCSPlayer_ItemServices(((NativeObject)((CBasePlayerPawn)val).ItemServices).Handle).HasDefuser) || Random.Shared.NextDouble() < 0.45))
				{
					_defuseSmokeUsed = true;
					TrySpawnInstantGrenade(userid, spawnPos, "smoke");
				}
				if (!_defuseFlashUsed && Random.Shared.NextDouble() < 0.25)
				{
					_defuseFlashUsed = true;
					_botFlashImmunityUntil[((CEntityInstance)userid).Index] = Server.CurrentTime + 2f;
					TrySpawnInstantGrenade(userid, spawnPos, "flash", new Vector((float?)0f, (float?)0f, (float?)(-800f)));
				}
			}
		}
		return (HookResult)0;
	}

	private HookResult OnBombBeginPlant(EventBombBeginplant @event, GameEventInfo info)
	{
		//IL_00dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ed: Expected O, but got Unknown
		RecordSoundPoint(@event.Userid);
		if (!_plantSmokeUsed)
		{
			CCSPlayerController userid = @event.Userid;
			if ((CEntityInstance)(object)userid != (CEntityInstance)null && ((CEntityInstance)userid).IsValid && userid.IsBot && !userid.HasBeenControlledByPlayerThisRound && Random.Shared.NextDouble() < 0.35)
			{
				CCSPlayerPawn val = userid.PlayerPawn?.Value;
				if ((CEntityInstance)(object)val != (CEntityInstance)null && ((CEntityInstance)val).IsValid && userid.PawnIsAlive && ((CBaseEntity)val).AbsOrigin != null)
				{
					_plantSmokeUsed = true;
					TrySpawnInstantGrenade(userid, new Vector((float?)((CBaseEntity)val).AbsOrigin.X, (float?)((CBaseEntity)val).AbsOrigin.Y, (float?)(((CBaseEntity)val).AbsOrigin.Z + 5f)), "smoke");
				}
			}
		}
		return (HookResult)0;
	}

	private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
	{
		CCSPlayerController userid = @event.Userid;
		if ((CEntityInstance)(object)userid != (CEntityInstance)null && ((CEntityInstance)userid).IsValid)
		{
			_botLastHurtTime[((CEntityInstance)userid).Index] = Server.CurrentTime;
		}
		HandleMolotovEscape(@event);
		HandleRetaliationHE(@event);
		return (HookResult)0;
	}

	private void HandleMolotovEscape(EventPlayerHurt @event)
	{
		//IL_017b: Unknown result type (might be due to invalid IL or missing references)
		//IL_018b: Expected O, but got Unknown
		if (_botNadesMode == "off")
		{
			return;
		}
		CCSPlayerController userid = @event.Userid;
		if ((CEntityInstance)(object)userid == (CEntityInstance)null || !((CEntityInstance)userid).IsValid || !userid.IsBot || userid.HasBeenControlledByPlayerThisRound)
		{
			return;
		}
		CCSPlayerPawn val = userid.PlayerPawn?.Value;
		if ((CEntityInstance)(object)val == (CEntityInstance)null || !((CEntityInstance)val).IsValid || !userid.PawnIsAlive)
		{
			return;
		}
		string text = @event.Weapon ?? "";
		if (!text.Contains("inferno", StringComparison.OrdinalIgnoreCase) && !text.Contains("molotov", StringComparison.OrdinalIgnoreCase) && !text.Contains("incgrenade", StringComparison.OrdinalIgnoreCase))
		{
			_botMolotovDmgStart.Remove(((CEntityInstance)userid).Index);
			return;
		}
		int teamNum = ((CBaseEntity)userid).TeamNum;
		if (_molotovEscapeSmokeCooldown.TryGetValue(teamNum, out var value) && Server.CurrentTime < value)
		{
			return;
		}
		uint index = ((CEntityInstance)userid).Index;
		float currentTime = Server.CurrentTime;
		if (!_botMolotovDmgStart.TryGetValue(index, out var value2))
		{
			_botMolotovDmgStart[index] = currentTime;
		}
		else if (currentTime - value2 >= 0.25f)
		{
			_botMolotovDmgStart.Remove(index);
			_molotovEscapeSmokeCooldown[teamNum] = currentTime + 18f;
			if (((CBaseEntity)val).AbsOrigin != null)
			{
				TrySpawnInstantGrenade(userid, new Vector((float?)((CBaseEntity)val).AbsOrigin.X, (float?)((CBaseEntity)val).AbsOrigin.Y, (float?)(((CBaseEntity)val).AbsOrigin.Z + 44f)), "smoke", switchHandBriefly: true);
			}
		}
	}

	private void HandleRetaliationHE(EventPlayerHurt @event)
	{
		if (_botNadesMode == "off" || _roundOver)
		{
			return;
		}
		CCSPlayerController userid = @event.Userid;
		if ((CEntityInstance)(object)userid == (CEntityInstance)null || !((CEntityInstance)userid).IsValid || !userid.IsBot || userid.HasBeenControlledByPlayerThisRound)
		{
			return;
		}
		CCSPlayerPawn victimPawn = userid.PlayerPawn?.Value;
		if ((CEntityInstance)(object)victimPawn == (CEntityInstance)null || !((CEntityInstance)victimPawn).IsValid || !userid.PawnIsAlive || ((CBaseEntity)victimPawn).AbsOrigin == null)
		{
			return;
		}
		CCSPlayerController attacker = @event.Attacker;
		if ((CEntityInstance)(object)attacker == (CEntityInstance)null || !((CEntityInstance)attacker).IsValid || !attacker.PawnIsAlive || ((CBaseEntity)attacker).TeamNum == ((CBaseEntity)userid).TeamNum || FiredRecently(userid, 1f))
		{
			return;
		}
		string text = @event.Weapon ?? "";
		if (!text.Contains("hegrenade", StringComparison.OrdinalIgnoreCase) && !text.Contains("molotov", StringComparison.OrdinalIgnoreCase) && !text.Contains("incgrenade", StringComparison.OrdinalIgnoreCase) && !text.Contains("inferno", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		string retalWeapon = "";
		int retalItemDef = 0;
		if (!HasGrenadeInInventory(victimPawn, "he", out retalWeapon, out retalItemDef) &&
		    !HasGrenadeInInventory(victimPawn, "molotov", out retalWeapon, out retalItemDef))
		{
			return;
		}

		CCSPlayerPawn? activeLivePawn = GetActiveLivePawn(attacker);
		Vector atkPos = ((activeLivePawn != null) ? ((CBaseEntity)activeLivePawn).AbsOrigin : null);
		if (atkPos == null)
		{
			return;
		}
		int teamNum = ((CBaseEntity)userid).TeamNum;
		if ((_botNadesMode == "normal" || _botNadesMode == "more" || _botNadesMode == "less") && _retaliationCooldown.TryGetValue(teamNum, out var value) && Server.CurrentTime < value)
		{
			return;
		}

		Vector vPos = ((CBaseEntity)victimPawn).AbsOrigin;
		List<GrenadeData> list = (from g in _mapNades.Where(delegate(GrenadeData g)
			{
				if (g.GrenadeType != "he" && g.GrenadeType != "molotov" && g.GrenadeType != "incgrenade")
				{
					return false;
				}
				if (Dist3D(vPos.X, vPos.Y, vPos.Z, g.ProjectilePosition.X, g.ProjectilePosition.Y, g.ProjectilePosition.Z) > 80f)
				{
					return false;
				}
				return !(Dist3D(atkPos.X, atkPos.Y, atkPos.Z, g.LandingPosition.X, g.LandingPosition.Y, g.LandingPosition.Z) > 260f) && !IsOnCooldown(g.Id);
			})
			orderby FacesThrowDirection(victimPawn, g) ? 1 : 0 descending, Dist3D(vPos.X, vPos.Y, vPos.Z, g.ProjectilePosition.X, g.ProjectilePosition.Y, g.ProjectilePosition.Z)
			select g).ToList();

		uint index = ((CEntityInstance)userid).Index;
		if (list.Count > 0)
		{
			GrenadeData grenadeData = list[0];
			string grenadeType = grenadeData.GrenadeType;
			if (HasGrenadeInInventory(victimPawn, grenadeType, out retalWeapon, out retalItemDef))
			{
				RegisterCooldown(grenadeData.Id, grenadeType);
				ExecuteRealisticThrow(userid, grenadeData, retalWeapon, retalItemDef);
				IncrementCount(grenadeType, ((CBaseEntity)userid).TeamNum);
				if (_botNadesMode == "less")
				{
					IncrementBotCount(grenadeType, index);
				}
				_retaliationCooldown[teamNum] = Server.CurrentTime + 7f;
			}
		}
		else
		{
			Vector eyePos = new Vector(vPos.X, vPos.Y, vPos.Z + 64f);
			if (TrySolveBallisticArc(eyePos, atkPos, allowWallBounce: true, out Vector launchOrigin, out Vector launchVelocity))
			{
				string gType = retalWeapon.Contains("molotov") || retalWeapon.Contains("incgrenade") ? "molotov" : "he";
				GrenadeData dynRetal = new GrenadeData
				{
					Id = $"retal_{index}_{(int)Server.CurrentTime}",
					MapName = Server.MapName,
					GrenadeType = gType,
					ProjectilePosition = new Vec3 { X = launchOrigin.X, Y = launchOrigin.Y, Z = launchOrigin.Z },
					ProjectileVelocity = new Vec3 { X = launchVelocity.X, Y = launchVelocity.Y, Z = launchVelocity.Z },
					LandingPosition = new Vec3 { X = atkPos.X, Y = atkPos.Y, Z = atkPos.Z },
					Description = "RetaliationArc"
				};
				ExecuteRealisticThrow(userid, dynRetal, retalWeapon, retalItemDef);
				IncrementCount(gType, ((CBaseEntity)userid).TeamNum);
				if (_botNadesMode == "less")
				{
					IncrementBotCount(gType, index);
				}
				_retaliationCooldown[teamNum] = Server.CurrentTime + 7f;
			}
		}
	}

	private void OnTick()
	{
		try
		{
			string mapName = Server.MapName;
			if (!string.IsNullOrEmpty(mapName) && !string.Equals(mapName, "<empty>", StringComparison.OrdinalIgnoreCase))
			{
				if (!string.Equals(_loadedMapName, mapName, StringComparison.OrdinalIgnoreCase))
				{
					RefreshMapNades(mapName);
				}
				_tick++;
				ProcessActiveWindups();
				UpdateSoundTrails(_tick % 4 == 0);
				if (_tick % 4 == 0)
				{
					CheckBotZones();
				}
				if (_tick % 256 == 0)
				{
					PruneCooldowns();
				}
			}
		}
		catch
		{
		}
	}
}
