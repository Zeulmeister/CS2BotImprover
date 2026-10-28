using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Permissions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace BotBuyPatch;

public sealed class BotBuyPatch : BasePlugin
{
	private readonly Dictionary<int, int> _botUserIdToIndex = new Dictionary<int, int>();

	private int _botIndexCounter;

	private readonly Dictionary<CsTeam, List<CCSPlayerController>> _poorPlayersByTeam = new Dictionary<CsTeam, List<CCSPlayerController>>();

	private readonly Dictionary<int, List<string>> _prevWeapons = new Dictionary<int, List<string>>();

	private readonly Dictionary<int, int> _prevMoney = new Dictionary<int, int>();

	private readonly Dictionary<int, int> _prevArmor = new Dictionary<int, int>();

	private static readonly HashSet<string> PrimaryWeapons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"weapon_ak47", "weapon_m4a1_silencer", "weapon_m4a1", "weapon_galilar", "weapon_famas", "weapon_awp", "weapon_ssg08", "weapon_mac10", "weapon_mp9", "weapon_ump45",
		"weapon_mp7", "weapon_mp5sd", "weapon_aug", "weapon_sg556"
	};

	public override string ModuleName => "BotBuyPatch";

	public override string ModuleVersion => "1.2.0";

	public override string ModuleAuthor => "ed0ard (Fixed & Optimized)";

	public override string ModuleDescription => "Enable bots to take intelligent buy and drop options";

	public override void Load(bool hotReload)
	{
		Logger.LogInformation("[BotBuy] v1.3.0 Loaded! Competitive MR12 Team Economy active.");
	}

	[GameEventHandler(/*Could not decode attribute arguments.*/)]
	public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
	{
		CCSPlayerController userid = @event.Userid;
		if ((CEntityInstance)(object)userid != (CEntityInstance)null && ((CEntityInstance)userid).IsValid && userid.IsBot)
		{
			GetBotIndex(userid);
		}
		return (HookResult)0;
	}

	[GameEventHandler(/*Could not decode attribute arguments.*/)]
	public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
	{
		CCSPlayerController userid = @event.Userid;
		if ((CEntityInstance)(object)userid != (CEntityInstance)null)
		{
			RemoveBot(userid);
		}
		return (HookResult)0;
	}

	[GameEventHandler(/*Could not decode attribute arguments.*/)]
	public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
	{
		CCSPlayerController userid = @event.Userid;
		if ((CEntityInstance)(object)userid != (CEntityInstance)null && ((CEntityInstance)userid).IsValid && userid.IsBot)
		{
			ClearPreviousInventory(userid);
		}
		return (HookResult)0;
	}

	[GameEventHandler(/*Could not decode attribute arguments.*/)]
	public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
	{
		SavePreviousInventory();
		return (HookResult)0;
	}

	[GameEventHandler(/*Could not decode attribute arguments.*/)]
	public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
	{
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0491: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Invalid comparison between Unknown and I4
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cd: Invalid comparison between Unknown and I4
		//IL_01a4: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			if (Server.MapName == "aim_rush")
			{
				return (HookResult)0;
			}
			List<CCSPlayerController> allPlayers = new List<CCSPlayerController>();
			List<CCSPlayerController> allCT = new List<CCSPlayerController>();
			List<CCSPlayerController> list = new List<CCSPlayerController>();
			List<CCSPlayerController> ctBots = new List<CCSPlayerController>();
			List<CCSPlayerController> tBots = new List<CCSPlayerController>();
			foreach (CCSPlayerController item2 in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
			{
				if ((CEntityInstance)(object)item2 == (CEntityInstance)null || !((CEntityInstance)item2).IsValid)
				{
					continue;
				}
				allPlayers.Add(item2);
				if ((int)item2.Team == 3)
				{
					allCT.Add(item2);
					if (item2.IsBot)
					{
						ctBots.Add(item2);
					}
				}
				else if ((int)item2.Team == 2)
				{
					list.Add(item2);
					if (item2.IsBot)
					{
						tBots.Add(item2);
					}
				}
			}
			_poorPlayersByTeam.Clear();
			_poorPlayersByTeam[(CsTeam)3] = allPlayers.Where(delegate(CCSPlayerController p)
			{
				//IL_0009: Unknown result type (might be due to invalid IL or missing references)
				//IL_000f: Invalid comparison between Unknown and I4
				return ((CEntityInstance)p).IsValid && (int)p.Team == 3 && p.InGameMoneyServices != null && p.InGameMoneyServices.Account < 4200;
			}).ToList();
			_poorPlayersByTeam[(CsTeam)2] = allPlayers.Where(delegate(CCSPlayerController p)
			{
				//IL_0009: Unknown result type (might be due to invalid IL or missing references)
				//IL_000f: Invalid comparison between Unknown and I4
				return ((CEntityInstance)p).IsValid && (int)p.Team == 2 && p.InGameMoneyServices != null && p.InGameMoneyServices.Account < 4200;
			}).ToList();
			ConVar val = ConVar.Find("bot_loadout");
			if (val != null && !string.IsNullOrEmpty(val.StringValue))
			{
				return (HookResult)0;
			}
			foreach (CCSPlayerController item3 in allPlayers.Where((CCSPlayerController p) => ((CEntityInstance)p).IsValid && p.IsBot))
			{
				if (Random.Shared.NextSingle() < 0.8f)
				{
					Swap(item3, "weapon_hkp2000", "weapon_usp_silencer");
				}
			}
			foreach (CCSPlayerController item6 in allPlayers.Where((CCSPlayerController p) => ((CEntityInstance)p).IsValid && p.IsBot))
			{
				CCSPlayerController copyPlayer = item6;
				float num = Random.Shared.NextSingle();
				if (num < 0.06f)
				{
					continue;
				}
				if (num < 0.53f)
				{
					((BasePlugin)this).AddTimer(0.4f, (Action)delegate
					{
						if (((CEntityInstance)copyPlayer).IsValid)
						{
							Swap(copyPlayer, "weapon_aug", "weapon_m4a1");
							Swap(copyPlayer, "weapon_scar20", "weapon_m4a1");
							Swap(copyPlayer, "weapon_g3sg1", "weapon_ak47");
						}
					}, (TimerFlags?)null);
					continue;
				}
				((BasePlugin)this).AddTimer(0.4f, (Action)delegate
				{
					if (((CEntityInstance)copyPlayer).IsValid)
					{
						Swap(copyPlayer, "weapon_aug", "weapon_m4a1_silencer");
						Swap(copyPlayer, "weapon_scar20", "weapon_m4a1_silencer");
						Swap(copyPlayer, "weapon_g3sg1", "weapon_ak47");
					}
				}, (TimerFlags?)null);
			}
			((BasePlugin)this).AddTimer(0.4f, (Action)delegate
			{
				foreach (CCSPlayerController item7 in allPlayers)
				{
					if (((CEntityInstance)item7).IsValid)
					{
						Refund(item7, "weapon_negev");
						Refund(item7, "weapon_m249");
						Refund(item7, "weapon_bizon");
						Refund(item7, "weapon_p90");
						Refund(item7, "weapon_xm1014");
						Refund(item7, "weapon_nova");
						Refund(item7, "weapon_sawedoff");
						Refund(item7, "weapon_mag7");
						Refund(item7, "weapon_revolver");
					}
				}
			}, (TimerFlags?)null);
			((BasePlugin)this).AddTimer(0.6f, (Action)delegate
			{
				if (IsFirstRoundOfHalf())
				{
					return;
				}
				ProcessTeamPurchases(allCT, (CsTeam)3);
				ProcessTeamPurchases(list, (CsTeam)2);
			}, (TimerFlags?)null);
			((BasePlugin)this).AddTimer(3f, (Action)delegate
			{
				//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
				foreach (CCSPlayerController item9 in allCT)
				{
					if (((CEntityInstance)item9).IsValid && item9.InGameMoneyServices != null && (!_poorPlayersByTeam.TryGetValue((CsTeam)3, out List<CCSPlayerController> value) || !value.Contains(item9) || (IsFirstRoundOfHalf() && item9.InGameMoneyServices.Account == 500)) && item9.InGameMoneyServices.Account >= 400)
					{
						CCSPlayerPawn value2 = item9.PlayerPawn.Value;
						if ((CEntityInstance)(object)value2 != (CEntityInstance)null && ((CEntityInstance)value2).IsValid && ((CBasePlayerPawn)value2).ItemServices != null && ((NativeObject)((CBasePlayerPawn)value2).ItemServices).Handle != (IntPtr)IntPtr.Zero && !new CCSPlayer_ItemServices(((NativeObject)((CBasePlayerPawn)value2).ItemServices).Handle).HasDefuser)
						{
							Buy(item9, "item_defuser");
						}
					}
				}
			}, (TimerFlags?)null);
			((BasePlugin)this).AddTimer(1f, (Action)delegate
			{
				//IL_009a: Unknown result type (might be due to invalid IL or missing references)
				//IL_00a1: Expected O, but got Unknown
				if (IsFirstRoundOfHalf())
				{
					return;
				}
				foreach (CCSPlayerController item10 in allPlayers)
				{
					if (((CEntityInstance)item10).IsValid && item10.IsBot)
					{
						CCSPlayerPawn value = item10.PlayerPawn.Value;
						if (!((CEntityInstance)(object)value == (CEntityInstance)null) && ((CEntityInstance)value).IsValid && ((CBasePlayerPawn)value).ItemServices != null && ((NativeObject)((CBasePlayerPawn)value).ItemServices).Handle != (IntPtr)IntPtr.Zero)
						{
							int item = PreviousInventory(item10).Armor;
							CCSPlayer_ItemServices val2 = new CCSPlayer_ItemServices(((NativeObject)((CBasePlayerPawn)value).ItemServices).Handle);
							int armorValue = value.ArmorValue;
							if (item > 40 && item <= 99 && armorValue > 99 && val2.HasHelmet)
							{
								Refund(item10, "item_assaultsuit");
								item10.GiveNamedItem("item_assaultsuit");
								value.ArmorValue = item;
								Utilities.SetStateChanged((CBaseEntity)(object)value, "CCSPlayerPawn", "m_ArmorValue", 0);
							}
						}
					}
				}
			}, (TimerFlags?)null);
			((BasePlugin)this).AddTimer(0.4f, (Action)delegate
			{
				//IL_0084: Unknown result type (might be due to invalid IL or missing references)
				//IL_008a: Invalid comparison between Unknown and I4
				//IL_0275: Unknown result type (might be due to invalid IL or missing references)
				//IL_027b: Invalid comparison between Unknown and I4
				//IL_03ff: Unknown result type (might be due to invalid IL or missing references)
				//IL_0405: Invalid comparison between Unknown and I4
				if (!IsFirstRoundOfHalf())
				{
					return;
				}
				foreach (CCSPlayerController item11 in allPlayers)
				{
					if (((CEntityInstance)item11).IsValid && item11.IsBot && item11.InGameMoneyServices != null)
					{
						int account = item11.InGameMoneyServices.Account;
						float num2 = Random.Shared.NextSingle();
						switch (account)
						{
						case 800:
							if ((int)item11.Team == 3)
							{
								if (num2 < 0.75f)
								{
									Buy(item11, "item_kevlar");
								}
								else if (num2 < 0.81f)
								{
									Swap(item11, "weapon_usp_silencer", "weapon_elite");
									Swap(item11, "weapon_hkp2000", "weapon_elite");
								}
								else if (num2 < 0.87f)
								{
									Swap(item11, "weapon_usp_silencer", "weapon_p250");
									Swap(item11, "weapon_hkp2000", "weapon_p250");
								}
								else if (num2 < 0.92f)
								{
									Swap(item11, "weapon_usp_silencer", "weapon_cz75a");
									Swap(item11, "weapon_hkp2000", "weapon_cz75a");
								}
								else if (num2 < 0.96f)
								{
									Swap(item11, "weapon_usp_silencer", "weapon_deagle");
									Swap(item11, "weapon_hkp2000", "weapon_deagle");
								}
								else
								{
									Swap(item11, "weapon_usp_silencer", "weapon_fiveseven");
									Swap(item11, "weapon_hkp2000", "weapon_fiveseven");
								}
							}
							else if (num2 < 0.75f)
							{
								Buy(item11, "item_kevlar");
							}
							else if (num2 < 0.81f)
							{
								Swap(item11, "weapon_glock", "weapon_elite");
							}
							else if (num2 < 0.88f)
							{
								Swap(item11, "weapon_glock", "weapon_p250");
							}
							else if (num2 < 0.94f)
							{
								Swap(item11, "weapon_glock", "weapon_tec9");
							}
							else
							{
								Swap(item11, "weapon_glock", "weapon_deagle");
							}
							break;
						case 1000:
							if ((int)item11.Team == 3)
							{
								if (num2 < 0.20f)
								{
									Swap(item11, "weapon_usp_silencer", "weapon_elite");
									Swap(item11, "weapon_hkp2000", "weapon_elite");
								}
								else if (num2 < 0.50f)
								{
									Swap(item11, "weapon_usp_silencer", "weapon_p250");
									Swap(item11, "weapon_hkp2000", "weapon_p250");
								}
								else if (num2 < 0.75f)
								{
									Swap(item11, "weapon_usp_silencer", "weapon_cz75a");
									Swap(item11, "weapon_hkp2000", "weapon_cz75a");
								}
								else if (num2 < 0.90f)
								{
									Swap(item11, "weapon_usp_silencer", "weapon_deagle");
									Swap(item11, "weapon_hkp2000", "weapon_deagle");
								}
								else
								{
									Swap(item11, "weapon_usp_silencer", "weapon_fiveseven");
									Swap(item11, "weapon_hkp2000", "weapon_fiveseven");
								}
							}
							else if (num2 < 0.2f)
							{
								Swap(item11, "weapon_glock", "weapon_elite");
							}
							else if (num2 < 0.3f)
							{
								Swap(item11, "weapon_glock", "weapon_p250");
							}
							else if (num2 < 0.6f)
							{
								Swap(item11, "weapon_glock", "weapon_deagle");
							}
							else
							{
								Swap(item11, "weapon_glock", "weapon_tec9");
							}
							break;
						case 10000:
							Buy(item11, "item_assaultsuit");
							if ((int)item11.Team == 3)
							{
								if (num2 < 0.35f)
								{
									Buy(item11, "weapon_m4a1");
								}
								else if (num2 < 0.88f)
								{
									Buy(item11, "weapon_m4a1_silencer");
								}
								else
								{
									Buy(item11, "weapon_awp");
								}
							}
							else if (num2 < 0.88f)
							{
								Buy(item11, "weapon_ak47");
							}
							else
							{
								Buy(item11, "weapon_awp");
							}
							break;
						}
					}
				}
			}, (TimerFlags?)null);
			((BasePlugin)this).AddTimer(2f, (Action)delegate
			{
				TryDropWeaponsForTeam(allPlayers);
			}, (TimerFlags?)null);
			((BasePlugin)this).AddTimer(3.8f, (Action)delegate
			{
				TryDropWeaponsForTeam(allPlayers);
			}, (TimerFlags?)null);
		}
		catch
		{
		}
		return (HookResult)0;
	}

	private void TryDropWeaponsForTeam(List<CCSPlayerController> allPlayers)
	{
		if (IsFirstRoundOfHalf())
		{
			return;
		}
		CsTeam[] array = (CsTeam[])(object)new CsTeam[2]
		{
			(CsTeam)3,
			(CsTeam)2
		};
		foreach (CsTeam team in array)
		{
			bool isCT = (int)team == 3;
			int dropThreshold = isCT ? 4800 : 4500;

			List<CCSPlayerController> donors = allPlayers.Where(p =>
				(CEntityInstance)(object)p != (CEntityInstance)null && ((CEntityInstance)p).IsValid &&
				p.Team == team && p.IsBot && p.InGameMoneyServices != null &&
				p.InGameMoneyServices.Account >= dropThreshold && HasPrimaryWeapon(p)
			).OrderByDescending(p => p.InGameMoneyServices.Account).ToList();

			List<CCSPlayerController> recipients = allPlayers.Where(p =>
				(CEntityInstance)(object)p != (CEntityInstance)null && ((CEntityInstance)p).IsValid &&
				p.Team == team && p.InGameMoneyServices != null &&
				!HasPrimaryWeapon(p) && p.InGameMoneyServices.Account < (isCT ? 3000 : 2800)
			).OrderBy(p => p.IsBot ? 1 : 0).ToList();

			if (donors.Count == 0 || recipients.Count == 0) continue;

			int recipientIndex = 0;
			foreach (var donor in donors)
			{
				if (recipientIndex >= recipients.Count) break;
				var recipient = recipients[recipientIndex++];

				int donorMoney = donor.InGameMoneyServices.Account;
				string weaponName = isCT ? "weapon_m4a1_silencer" : "weapon_ak47";
				int weaponCost = isCT ? 2900 : 2700;

				if (donorMoney < weaponCost + 1500)
				{
					weaponName = isCT ? "weapon_famas" : "weapon_galilar";
					weaponCost = isCT ? 1950 : 1800;
					if (donorMoney < weaponCost + 1500) continue;
				}

				recipient.GiveNamedItem(weaponName);
				donor.InGameMoneyServices.Account -= weaponCost;
				Utilities.SetStateChanged((CBaseEntity)(object)donor, "CCSPlayerController", "m_pInGameMoneyServices", 0);

				if (recipient.IsBot && recipient.InGameMoneyServices.Account >= 650)
				{
					Buy(recipient, "item_assaultsuit");
					Buy(recipient, "item_kevlar");
				}

				foreach (var teammate in allPlayers.Where(p => (CEntityInstance)(object)p != (CEntityInstance)null && ((CEntityInstance)p).IsValid && p.Team == team))
				{
					teammate.PrintToChat($"{ChatColors.Green}{((CBasePlayerController)donor).PlayerName}{ChatColors.Yellow}: {((CBasePlayerController)recipient).PlayerName}, I dropped a weapon for ya");
				}
			}
		}
	}

	private void ProcessTeamPurchases(List<CCSPlayerController> teamPlayers, CsTeam team)
	{
		var validPlayers = teamPlayers.Where(p => (CEntityInstance)(object)p != (CEntityInstance)null && ((CEntityInstance)p).IsValid).ToList();
		var bots = validPlayers.Where(p => p.IsBot).ToList();
		if (bots.Count == 0) return;

		bool isCT = (int)team == 3;
		int totalMoney = 0;
		int armedCount = 0;
		int validCount = 0;

		foreach (var p in validPlayers)
		{
			if (p.InGameMoneyServices != null)
			{
				totalMoney += p.InGameMoneyServices.Account;
				validCount++;
			}
			if (HasPrimaryWeapon(p))
			{
				armedCount++;
			}
		}

		float avgMoney = validCount > 0 ? (float)totalMoney / validCount : 0f;
		int fullBuyThreshold = isCT ? 3550 : 3350;

		// Team Eco: If average team money is under $2,400 and fewer than 2 players are armed,
		// the team MUST save to guarantee a full rifle buy with the loss bonus in the next round.
		bool isTeamEco = avgMoney < 2400 && armedCount < 2;

		Logger.LogInformation("[BotBuy] Team {Team} (Avg: ${Avg:F0}, Armed: {Armed}/{Total}): Strategy={Strategy}",
			isCT ? "CT" : "T", avgMoney, armedCount, validCount, isTeamEco ? "TEAM_ECO" : "BUY");

		int awpCount = validPlayers.Count(p => Inventory(p).Weapons.Contains("weapon_awp"));

		foreach (var bot in bots)
		{
			if (bot.InGameMoneyServices == null) continue;
			int money = bot.InGameMoneyServices.Account;
			bool hasPrimary = HasPrimaryWeapon(bot);

			// 1. Bot already has primary weapon (survived previous round)
			if (hasPrimary)
			{
				Buy(bot, "item_assaultsuit");
				Buy(bot, "item_kevlar");

				if (isCT && bot.InGameMoneyServices.Account >= 800)
				{
					BuyDefuserIfNeeded(bot);
				}

				if (bot.InGameMoneyServices.Account >= 4000)
				{
					if (isCT)
					{
						if (!Buy(bot, "weapon_fiveseven")) Buy(bot, "weapon_deagle");
					}
					else
					{
						if (!Buy(bot, "weapon_tec9")) Buy(bot, "weapon_deagle");
					}
				}
				continue;
			}

			// 2. Bot has NO primary weapon: Team Eco Round (Save)
			if (isTeamEco)
			{
				// In Full Eco, broke bots spend $0!
				// Only bots with surplus (> $2600) can buy kevlar and/or sidearm, retaining at least $2000.
				if (money >= 2600)
				{
					Buy(bot, "item_kevlar");
					if (bot.InGameMoneyServices.Account >= 2000)
					{
						if (isCT) Buy(bot, "weapon_fiveseven");
						else Buy(bot, "weapon_tec9");
					}
				}
				// Break the poverty cycle: spend $0 so loss bonus guarantees next round rifle buy!
				continue;
			}

			// 3. Bot can FULL BUY (Rifle + Armor)
			if (money >= fullBuyThreshold)
			{
				// Priority 1: Armor FIRST
				Buy(bot, "item_assaultsuit");
				Buy(bot, "item_kevlar");

				// Priority 2: Primary Weapon
				bool boughtPrimary = false;
				if (awpCount == 0 && bot.InGameMoneyServices.Account >= 4750 && Random.Shared.NextSingle() < 0.25f)
				{
					boughtPrimary = Buy(bot, "weapon_awp");
					if (boughtPrimary) awpCount++;
				}

				if (!boughtPrimary)
				{
					if (isCT)
					{
						boughtPrimary = Buy(bot, "weapon_m4a1_silencer") || Buy(bot, "weapon_m4a1") || Buy(bot, "weapon_famas");
					}
					else
					{
						boughtPrimary = Buy(bot, "weapon_ak47") || Buy(bot, "weapon_galilar");
					}
				}

				// Priority 3: Defuser for CT
				if (isCT && bot.InGameMoneyServices.Account >= 400)
				{
					BuyDefuserIfNeeded(bot);
				}
				continue;
			}

			// 4. FORCE BUY / HALF BUY
			int minHalfBuy = isCT ? 1900 : 1700;
			if (money >= minHalfBuy)
			{
				// Always buy Kevlar FIRST!
				Buy(bot, "item_kevlar");

				bool boughtGun = false;
				if (isCT)
				{
					if (bot.InGameMoneyServices.Account >= 2900)
					{
						boughtGun = Buy(bot, "weapon_m4a1_silencer") || Buy(bot, "weapon_m4a1");
					}
					if (!boughtGun)
					{
						boughtGun = Buy(bot, "weapon_famas") || Buy(bot, "weapon_mp9");
					}
					if (!boughtGun && bot.InGameMoneyServices.Account >= 500)
					{
						boughtGun = Buy(bot, "weapon_fiveseven") || Buy(bot, "weapon_deagle");
					}
				}
				else
				{
					if (bot.InGameMoneyServices.Account >= 2700)
					{
						boughtGun = Buy(bot, "weapon_ak47");
					}
					if (!boughtGun)
					{
						boughtGun = Buy(bot, "weapon_galilar") || Buy(bot, "weapon_mac10");
					}
					if (!boughtGun && bot.InGameMoneyServices.Account >= 500)
					{
						boughtGun = Buy(bot, "weapon_tec9") || Buy(bot, "weapon_deagle");
					}
				}
				continue;
			}

			// 5. Individual Bot broke (< minHalfBuy) when team is buying:
			// DO NOT spend $300-$700 on a naked pistol! Spend $0 and save for next round.
		}
	}

	private void BuyDefuserIfNeeded(CCSPlayerController player)
	{
		if ((CEntityInstance)(object)player == (CEntityInstance)null || !((CEntityInstance)player).IsValid || player.InGameMoneyServices == null || player.InGameMoneyServices.Account < 400) return;
		CCSPlayerPawn value = player.PlayerPawn.Value;
		if ((CEntityInstance)(object)value != (CEntityInstance)null && ((CEntityInstance)value).IsValid && ((CBasePlayerPawn)value).ItemServices != null && ((NativeObject)((CBasePlayerPawn)value).ItemServices).Handle != IntPtr.Zero)
		{
			CCSPlayer_ItemServices itemServices = new CCSPlayer_ItemServices(((NativeObject)((CBasePlayerPawn)value).ItemServices).Handle);
			if (!itemServices.HasDefuser)
			{
				Buy(player, "item_defuser");
			}
		}
	}

	[GameEventHandler(/*Could not decode attribute arguments.*/)]
	public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
	{
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			ConVar val = ConVar.Find("bot_loadout");
			if (val != null && !string.IsNullOrEmpty(val.StringValue))
			{
				return (HookResult)0;
			}
			ConVar val2 = ConVar.Find("bot_eco_limit");
			if (val2 != null)
			{
				val2.SetValue<float>(0f);
			}
		}
		catch
		{
		}
		return (HookResult)0;
	}

	private bool HasPrimaryWeapon(CCSPlayerController player)
	{
		foreach (string item in Inventory(player).Weapons)
		{
			if (PrimaryWeapons.Contains(item))
			{
				return true;
			}
		}
		return false;
	}

	private bool Buy(CCSPlayerController player, string itemName)
	{
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Invalid comparison between Unknown and I4
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Invalid comparison between Unknown and I4
		if (!((CEntityInstance)player).IsValid || !player.IsBot || player.InGameMoneyServices == null)
		{
			return false;
		}
		CCSPlayerPawn value = player.PlayerPawn.Value;
		if ((CEntityInstance)(object)value == (CEntityInstance)null || !((CEntityInstance)value).IsValid)
		{
			return false;
		}
		int account = player.InGameMoneyServices.Account;
		bool flag = (int)player.Team == 3;
		bool flag2 = (int)player.Team == 2;
		int num = 0;
		bool flag3 = true;
		int armorValue = value.ArmorValue;
		switch (itemName)
		{
		case "item_kevlar":
			num = 650;
			break;
		case "item_assaultsuit":
			num = ((armorValue > 99) ? 350 : 1000);
			break;
		case "item_defuser":
			num = 400;
			flag3 = flag;
			break;
		case "weapon_taser":
			num = 200;
			break;
		case "weapon_glock":
			flag3 = flag2;
			break;
		case "weapon_hkp2000":
			flag3 = flag;
			break;
		case "weapon_usp_silencer":
			flag3 = flag;
			break;
		case "weapon_elite":
			num = 300;
			break;
		case "weapon_p250":
			num = 300;
			break;
		case "weapon_tec9":
			num = 500;
			flag3 = flag2;
			break;
		case "weapon_fiveseven":
			num = 500;
			flag3 = flag;
			break;
		case "weapon_deagle":
			num = 700;
			break;
		case "weapon_cz75a":
			num = 500;
			break;
		case "weapon_revolver":
			num = 600;
			break;
		case "weapon_mac10":
			num = 1050;
			flag3 = flag2;
			break;
		case "weapon_mp9":
			num = 1250;
			flag3 = flag;
			break;
		case "weapon_mp7":
			num = 1500;
			break;
		case "weapon_mp5sd":
			num = 1500;
			break;
		case "weapon_ump45":
			num = 1200;
			break;
		case "weapon_bizon":
			num = 1400;
			break;
		case "weapon_p90":
			num = 2350;
			break;
		case "weapon_nova":
			num = 1050;
			break;
		case "weapon_xm1014":
			num = 2000;
			break;
		case "weapon_sawedoff":
			num = 1100;
			flag3 = flag2;
			break;
		case "weapon_mag7":
			num = 1300;
			flag3 = flag;
			break;
		case "weapon_galilar":
			num = 1800;
			flag3 = flag2;
			break;
		case "weapon_ak47":
			num = 2700;
			flag3 = flag2;
			break;
		case "weapon_sg556":
			num = 3000;
			flag3 = flag2;
			break;
		case "weapon_famas":
			num = 1950;
			flag3 = flag;
			break;
		case "weapon_m4a1":
			num = 2900;
			flag3 = flag;
			break;
		case "weapon_m4a1_silencer":
			num = 2900;
			flag3 = flag;
			break;
		case "weapon_aug":
			num = 3300;
			flag3 = flag;
			break;
		case "weapon_ssg08":
			num = 1700;
			break;
		case "weapon_awp":
			num = 4750;
			break;
		case "weapon_scar20":
			num = 5000;
			flag3 = flag;
			break;
		case "weapon_g3sg1":
			num = 5000;
			flag3 = flag2;
			break;
		case "weapon_negev":
			num = 1700;
			break;
		case "weapon_m249":
			num = 5200;
			break;
		default:
			flag3 = false;
			break;
		}
		if (!flag3 || account < num)
		{
			return false;
		}
		player.GiveNamedItem(itemName);
		player.InGameMoneyServices.Account -= num;
		Utilities.SetStateChanged((CBaseEntity)(object)player, "CCSPlayerController", "m_pInGameMoneyServices", 0);
		return true;
	}

	private bool Refund(CCSPlayerController player, string itemName)
	{
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Invalid comparison between Unknown and I4
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_006d: Invalid comparison between Unknown and I4
		if (!((CEntityInstance)player).IsValid || !player.IsBot || player.InGameMoneyServices == null)
		{
			return false;
		}
		CCSPlayerPawn value = player.PlayerPawn.Value;
		if ((CEntityInstance)(object)value == (CEntityInstance)null || !((CEntityInstance)value).IsValid)
		{
			return false;
		}
		if (!CanRefund(player, itemName))
		{
			return false;
		}
		bool flag = false;
		int num = 0;
		bool flag2 = (int)player.Team == 3;
		bool flag3 = (int)player.Team == 2;
		if (itemName.StartsWith("weapon_"))
		{
			flag = ((CBasePlayerPawn)value).WeaponServices != null && ((IEnumerable<CHandle<CBasePlayerWeapon>>)((CBasePlayerPawn)value).WeaponServices.MyWeapons).Any((CHandle<CBasePlayerWeapon> w) => (CEntityInstance)(object)w.Value != (CEntityInstance)null && ((CEntityInstance)w.Value).DesignerName == itemName);
		}
		else if (itemName == "item_assaultsuit" || itemName == "item_kevlar")
		{
			flag = value.ArmorValue > 0;
		}
		if (!flag)
		{
			return false;
		}
		bool flag4 = true;
		switch (itemName)
		{
		case "item_kevlar":
			num = 650;
			break;
		case "item_assaultsuit":
			num = 1000;
			break;
		case "weapon_taser":
			num = 200;
			break;
		case "weapon_glock":
			flag4 = flag3;
			break;
		case "weapon_hkp2000":
			flag4 = flag2;
			break;
		case "weapon_usp_silencer":
			flag4 = flag2;
			break;
		case "weapon_elite":
			num = 300;
			break;
		case "weapon_p250":
			num = 300;
			break;
		case "weapon_tec9":
			num = 500;
			flag4 = flag3;
			break;
		case "weapon_fiveseven":
			num = 500;
			flag4 = flag2;
			break;
		case "weapon_deagle":
			num = 700;
			break;
		case "weapon_cz75a":
			num = 500;
			break;
		case "weapon_revolver":
			num = 600;
			break;
		case "weapon_mac10":
			num = 1050;
			flag4 = flag3;
			break;
		case "weapon_mp9":
			num = 1250;
			flag4 = flag2;
			break;
		case "weapon_mp7":
			num = 1500;
			break;
		case "weapon_mp5sd":
			num = 1500;
			break;
		case "weapon_ump45":
			num = 1200;
			break;
		case "weapon_bizon":
			num = 1400;
			break;
		case "weapon_p90":
			num = 2350;
			break;
		case "weapon_nova":
			num = 1050;
			break;
		case "weapon_xm1014":
			num = 2000;
			break;
		case "weapon_sawedoff":
			num = 1100;
			flag4 = flag3;
			break;
		case "weapon_mag7":
			num = 1300;
			flag4 = flag2;
			break;
		case "weapon_galilar":
			num = 1800;
			flag4 = flag3;
			break;
		case "weapon_ak47":
			num = 2700;
			flag4 = flag3;
			break;
		case "weapon_sg556":
			num = 3000;
			flag4 = flag3;
			break;
		case "weapon_famas":
			num = 1950;
			flag4 = flag2;
			break;
		case "weapon_m4a1":
			num = 2900;
			flag4 = flag2;
			break;
		case "weapon_m4a1_silencer":
			num = 2900;
			flag4 = flag2;
			break;
		case "weapon_aug":
			num = 3300;
			flag4 = flag2;
			break;
		case "weapon_ssg08":
			num = 1700;
			break;
		case "weapon_awp":
			num = 4750;
			break;
		case "weapon_scar20":
			num = 5000;
			flag4 = flag2;
			break;
		case "weapon_g3sg1":
			num = 5000;
			flag4 = flag3;
			break;
		case "weapon_negev":
			num = 1700;
			break;
		case "weapon_m249":
			num = 5200;
			break;
		default:
			return false;
		}
		if (!flag4)
		{
			return false;
		}
		if (itemName.StartsWith("weapon_"))
		{
			Utilities.RemoveItemByDesignerName(player, itemName);
		}
		else if (itemName == "item_assaultsuit" || itemName == "item_kevlar")
		{
			value.ArmorValue = 0;
			Utilities.SetStateChanged((CBaseEntity)(object)value, "CCSPlayerPawn", "m_ArmorValue", 0);
		}
		player.InGameMoneyServices.Account += num;
		ConVar val = ConVar.Find("mp_maxmoney");
		int num2 = ((val != null) ? val.GetPrimitiveValue<int>() : 16000);
		if (player.InGameMoneyServices.Account > num2)
		{
			player.InGameMoneyServices.Account = num2;
		}
		Utilities.SetStateChanged((CBaseEntity)(object)player, "CCSPlayerController", "m_pInGameMoneyServices", 0);
		return true;
	}

	private bool CanRefund(CCSPlayerController player, string itemName)
	{
		if (IsFirstRoundOfHalf())
		{
			return true;
		}
		if (!((CEntityInstance)player).IsValid || !player.IsBot)
		{
			return false;
		}
		return !PreviousInventory(player).Weapons.Contains(itemName);
	}

	private bool Swap(CCSPlayerController player, string oldItem, string newItem)
	{
		if (!((CEntityInstance)player).IsValid || !player.IsBot || player.InGameMoneyServices == null)
		{
			return false;
		}
		CCSPlayerPawn value = player.PlayerPawn.Value;
		if ((CEntityInstance)(object)value == (CEntityInstance)null || !((CEntityInstance)value).IsValid)
		{
			return false;
		}
		if (!Refund(player, oldItem))
		{
			return false;
		}
		if (!Buy(player, newItem))
		{
			Buy(player, oldItem);
			return false;
		}
		return true;
	}

	private bool IsFirstRoundOfHalf()
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
			ConVar val2 = ConVar.Find("mp_maxrounds");
			int num = ((val2 != null) ? val2.GetPrimitiveValue<int>() : 24);
			ConVar val3 = ConVar.Find("mp_overtime_maxrounds");
			int num2 = ((val3 != null) ? val3.GetPrimitiveValue<int>() : 6);
			if (num <= 0)
			{
				num = 24;
			}
			if (num2 <= 0)
			{
				num2 = 6;
			}
			int num3 = num / 2;
			int num4 = num2 / 2;
			return totalRoundsPlayed == 0 || totalRoundsPlayed == num3 || totalRoundsPlayed == num || (totalRoundsPlayed > num && (totalRoundsPlayed - num) % num4 == 0);
		}
		catch
		{
			return false;
		}
	}

	private int GetBotIndex(CCSPlayerController player)
	{
		if (!((CEntityInstance)player).IsValid || !player.IsBot)
		{
			return -1;
		}
		int num = player.UserId ?? (-1);
		if (num == -1)
		{
			return -1;
		}
		if (_botUserIdToIndex.TryGetValue(num, out var value))
		{
			return value;
		}
		int num2 = ++_botIndexCounter;
		_botUserIdToIndex[num] = num2;
		return num2;
	}

	private void RemoveBot(CCSPlayerController player)
	{
		int num = player.UserId ?? (-1);
		if (num != -1 && _botUserIdToIndex.Remove(num, out var value))
		{
			_prevWeapons.Remove(value);
			_prevMoney.Remove(value);
			_prevArmor.Remove(value);
		}
	}

	private void SavePreviousInventory()
	{
		if (IsFirstRoundOfHalf())
		{
			_prevWeapons.Clear();
			_prevMoney.Clear();
			_prevArmor.Clear();
			return;
		}
		foreach (CCSPlayerController item in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
		{
			if ((CEntityInstance)(object)item == (CEntityInstance)null || !((CEntityInstance)item).IsValid || !item.IsBot)
			{
				continue;
			}
			int botIndex = GetBotIndex(item);
			if (botIndex == -1)
			{
				continue;
			}
			CCSPlayerPawn value = item.PlayerPawn.Value;
			if ((CEntityInstance)(object)value == (CEntityInstance)null || !((CEntityInstance)value).IsValid)
			{
				continue;
			}
			List<string> list = new List<string>();
			if (((CBasePlayerPawn)value).WeaponServices != null)
			{
				foreach (CHandle<CBasePlayerWeapon> myWeapon in ((CBasePlayerPawn)value).WeaponServices.MyWeapons)
				{
					CBasePlayerWeapon value2 = myWeapon.Value;
					if ((CEntityInstance)(object)value2 != (CEntityInstance)null)
					{
						string designerName = ((CEntityInstance)value2).DesignerName;
						if (designerName != "item_kevlar" && designerName != "item_assaultsuit" && designerName != "item_defuser")
						{
							list.Add(designerName);
						}
					}
				}
			}
			_prevWeapons[botIndex] = list;
			Dictionary<int, int> prevMoney = _prevMoney;
			CCSPlayerController_InGameMoneyServices inGameMoneyServices = item.InGameMoneyServices;
			prevMoney[botIndex] = ((inGameMoneyServices != null) ? inGameMoneyServices.Account : 0);
			_prevArmor[botIndex] = value.ArmorValue;
		}
	}

	private void ClearPreviousInventory(CCSPlayerController player)
	{
		if (((CEntityInstance)player).IsValid && player.IsBot)
		{
			int botIndex = GetBotIndex(player);
			if (botIndex != -1)
			{
				_prevWeapons.Remove(botIndex);
				_prevArmor.Remove(botIndex);
			}
		}
	}

	private (List<string> Weapons, int Money, int Armor) Inventory(CCSPlayerController player)
	{
		List<string> list = new List<string>();
		if (!((CEntityInstance)player).IsValid)
		{
			return (Weapons: list, Money: 0, Armor: 0);
		}
		CCSPlayerPawn value = player.PlayerPawn.Value;
		if ((CEntityInstance)(object)value == (CEntityInstance)null || !((CEntityInstance)value).IsValid)
		{
			return (Weapons: list, Money: 0, Armor: 0);
		}
		CCSPlayerController_InGameMoneyServices inGameMoneyServices = player.InGameMoneyServices;
		int item = ((inGameMoneyServices != null) ? inGameMoneyServices.Account : 0);
		int armorValue = value.ArmorValue;
		if (((CBasePlayerPawn)value).WeaponServices != null)
		{
			foreach (CHandle<CBasePlayerWeapon> myWeapon in ((CBasePlayerPawn)value).WeaponServices.MyWeapons)
			{
				CBasePlayerWeapon value2 = myWeapon.Value;
				if ((CEntityInstance)(object)value2 != (CEntityInstance)null)
				{
					string designerName = ((CEntityInstance)value2).DesignerName;
					if (designerName != "item_kevlar" && designerName != "item_assaultsuit" && designerName != "item_defuser")
					{
						list.Add(designerName);
					}
				}
			}
		}
		return (Weapons: list, Money: item, Armor: armorValue);
	}

	private (List<string> Weapons, int Money, int Armor) PreviousInventory(CCSPlayerController player)
	{
		List<string> item = new List<string>();
		if (!((CEntityInstance)player).IsValid || !player.IsBot)
		{
			return (Weapons: item, Money: 0, Armor: 0);
		}
		int botIndex = GetBotIndex(player);
		if (botIndex == -1 || IsFirstRoundOfHalf())
		{
			return (Weapons: item, Money: 0, Armor: 0);
		}
		if (_prevWeapons.TryGetValue(botIndex, out List<string> value))
		{
			item = value;
		}
		_prevMoney.TryGetValue(botIndex, out var value2);
		_prevArmor.TryGetValue(botIndex, out var value3);
		return (Weapons: item, Money: value2, Armor: value3);
	}
}
