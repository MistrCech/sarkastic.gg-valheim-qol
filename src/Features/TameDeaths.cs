using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace SarkasticQoL.Features
{
	/*
		What killed the tamed creatures, for the players (!deaths) and the server log. A creature's
		death runs through Character.CheckDeath on its owner -- the server, with Dedicated
		Simulation -- once its health has reached zero, and the last hit applied to it
		(Character.m_lastHit, set in ApplyDamage) says how: the hit's type (a creature's attack, a
		player's, burning, smoke, a fall, drowning, freezing, poison ...) and the attacker's object
		id, which names the creature -- tamed or wild, with its level -- or the player. A tamed
		creature taken out of the world while alive (ZNetScene.Destroy on it: a command, another
		mod) is recorded as well, except a young one growing up, which the game replaces with the
		grown animal that way. The latest DeathsKept are kept in a file per world.
	*/
	internal static class TameDeaths
	{
		private class Entry
		{
			public DateTime at;
			public Vector3 pos;
			public string text;
		}

		private static readonly List<Entry> s_entries = new List<Entry>();

		private static bool Enabled => QoLPlugin.Settings.Enabled.Value && QoLPlugin.Settings.TamesLogDeaths.Value;

		[HarmonyPatch(typeof(Character), "CheckDeath")]
		internal static class CheckDeathPatch
		{
			static void Prefix(Character __instance)
			{
				if (!Enabled || __instance.IsDead() || __instance.GetHealth() > 0f || !__instance.IsTamed())
				{
					return;
				}
				try
				{
					Record(__instance, Cause(__instance.m_lastHit));
				}
				catch (Exception e)
				{
					QoLPlugin.Log.LogWarning($"A tamed creature died, but describing it failed: {e.Message}");
				}
			}
		}

		[HarmonyPatch(typeof(ZNetScene), "Destroy", typeof(GameObject))]
		internal static class DestroyPatch
		{
			static void Prefix(GameObject go)
			{
				if (!Enabled || !go)
				{
					return;
				}
				Character character = go.GetComponent<Character>();
				if (!character || character.IsDead() || character.GetHealth() <= 0f || !character.IsTamed())
				{
					return;
				}
				// A young animal that grew up is replaced by the grown one: Growup destroys it (not a death).
				Growup growup = go.GetComponent<Growup>();
				if (growup && growup.m_baseAI && growup.m_baseAI.GetTimeSinceSpawned().TotalSeconds > growup.m_growTime)
				{
					return;
				}
				Record(character, "taken out of the world alive (a command or another mod)");
			}
		}

		private static void Record(Character character, string cause)
		{
			Entry entry = new Entry { at = DateTime.UtcNow, pos = character.transform.position, text = $"{Describe(character)}: {cause}" };
			s_entries.Add(entry);
			int keep = Mathf.Max(1, QoLPlugin.Settings.TamesDeathsKept.Value);
			if (s_entries.Count > keep)
			{
				s_entries.RemoveRange(0, s_entries.Count - keep);
			}
			QoLPlugin.Log.LogInfo($"Tame died: {entry.text} at ({entry.pos.x:0}, {entry.pos.y:0}, {entry.pos.z:0})");
			Save();
		}

		// "Boar 'Pepa' (lvl 2)"
		private static string Describe(Character character)
		{
			ZDO zdo = character.m_nview ? character.m_nview.GetZDO() : null;
			string name = zdo != null ? zdo.GetString(ZDOVars.s_tamedName) : "";
			return $"{PrefabName(zdo, character.gameObject.name)}{(name.Length > 0 ? $" '{name}'" : "")} (lvl {character.GetLevel()})";
		}

		private static string Cause(HitData hit)
		{
			if (hit == null)
			{
				return "no hit recorded (health set to zero directly)";
			}
			string how = $"{Kind(hit.m_hitType)}, {hit.GetTotalDamage():0} damage";
			if (hit.m_attacker.IsNone())
			{
				return how;
			}
			ZDO attacker = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(hit.m_attacker) : null;
			if (attacker == null)
			{
				return $"{how}, by something no longer in the world";
			}
			string prefab = PrefabName(attacker, null);
			if (prefab == "Player")
			{
				return $"{how}, by player {PlayerName(hit.m_attacker, attacker)}";
			}
			string tamedName = attacker.GetString(ZDOVars.s_tamedName);
			return $"{how}, by {(attacker.GetBool(ZDOVars.s_tamed) ? "tamed " : "")}{World.PrettyName(prefab)}{(tamedName.Length > 0 ? $" '{tamedName}'" : "")} (lvl {attacker.GetInt(ZDOVars.s_level, 1)})";
		}

		private static string Kind(HitData.HitType type)
		{
			switch (type)
			{
				case HitData.HitType.EnemyHit: return "attacked";
				case HitData.HitType.PlayerHit: return "hit by a player";
				case HitData.HitType.Fall: return "fell";
				case HitData.HitType.Drowning: return "drowned";
				case HitData.HitType.Burning: return "burned";
				case HitData.HitType.Freezing: return "froze";
				case HitData.HitType.Poisoned: return "poisoned";
				case HitData.HitType.Smoke: return "smoke";
				case HitData.HitType.Water: return "water";
				case HitData.HitType.Structural: return "crushed by a structure";
				case HitData.HitType.Turret: return "shot by a ballista";
				case HitData.HitType.Cart: return "hit by a cart";
				case HitData.HitType.Tree: return "hit by a tree";
				case HitData.HitType.Boat: return "hit by a boat";
				case HitData.HitType.Catapult: return "catapult";
				case HitData.HitType.CinderFire: case HitData.HitType.AshlandsLava: return "lava";
				default: return type.ToString();
			}
		}

		private static string PlayerName(ZDOID characterId, ZDO zdo)
		{
			if (ZNet.instance)
			{
				foreach (ZNetPeer peer in ZNet.instance.GetPeers())
				{
					if (peer.m_characterID == characterId)
					{
						return peer.m_playerName;
					}
				}
			}
			string name = zdo.GetString(ZDOVars.s_playerName);
			return name.Length > 0 ? name : "(unknown)";
		}

		private static string PrefabName(ZDO zdo, string fallback)
		{
			GameObject prefab = zdo != null && ZNetScene.instance ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
			if (prefab)
			{
				return prefab.name;
			}
			return fallback != null ? fallback.Replace("(Clone)", "") : "?";
		}

		// The latest `count` deaths, newest first, one line each; for a player: how long ago and where.
		public static List<string> Lines(int count)
		{
			List<string> lines = new List<string>();
			DateTime now = DateTime.UtcNow;
			for (int i = s_entries.Count - 1; i >= 0 && lines.Count < count; i--)
			{
				Entry e = s_entries[i];
				lines.Add($"{Ago(now - e.at)}: {e.text}, at ({e.pos.x:0}, {e.pos.z:0})");
			}
			return lines;
		}

		public static int Count => s_entries.Count;

		private static string Ago(TimeSpan span)
		{
			if (span.TotalMinutes < 1) return "just now";
			if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
			if (span.TotalDays < 1) return $"{(int)span.TotalHours} h {span.Minutes} min ago";
			return $"{(int)span.TotalDays} d {span.Hours} h ago";
		}

		// ---- the file ----

		private static string FilePath()
		{
			string world = ZNet.World != null ? ZNet.World.m_name : "world";
			foreach (char c in Path.GetInvalidFileNameChars())
			{
				world = world.Replace(c, '_');
			}
			return Path.Combine(Paths.ConfigPath, $"sarkasticeu.qol.deaths.{world}.txt");
		}

		public static void Load()
		{
			s_entries.Clear();
			string path = FilePath();
			if (!File.Exists(path))
			{
				return;
			}
			CultureInfo ic = CultureInfo.InvariantCulture;
			foreach (string line in File.ReadAllLines(path))
			{
				string[] f = line.Split(new[] { ' ' }, 5, StringSplitOptions.RemoveEmptyEntries);
				if (f.Length < 5 || f[0].StartsWith("#"))
				{
					continue;
				}
				try
				{
					s_entries.Add(new Entry
					{
						at = DateTime.Parse(f[0], ic, DateTimeStyles.RoundtripKind),
						pos = new Vector3(float.Parse(f[1], ic), float.Parse(f[2], ic), float.Parse(f[3], ic)),
						text = f[4],
					});
				}
				catch (Exception) { }
			}
		}

		private static void Save()
		{
			CultureInfo ic = CultureInfo.InvariantCulture;
			StringBuilder text = new StringBuilder("# Sarkastic.gg QoL: deaths of tamed creatures. <utc time> <x> <y> <z> <what happened>\n");
			foreach (Entry e in s_entries)
			{
				text.Append($"{e.at.ToString("o", ic)} {e.pos.x.ToString("R", ic)} {e.pos.y.ToString("R", ic)} {e.pos.z.ToString("R", ic)} {e.text}\n");
			}
			try
			{
				File.WriteAllText(FilePath(), text.ToString());
			}
			catch (Exception e)
			{
				QoLPlugin.Log.LogWarning($"Could not write the deaths file: {e.Message}");
			}
		}
	}
}
