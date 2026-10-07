using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace SarkasticQoL.Features
{
	/*
		Field overrides for whole kinds of pieces, from a text file: one line per override,
		"<prefab> <Component>.<field> <value>", e.g. "piece_workbench CraftingStation.m_rangeBuild 20".
		Applied to every player-built object of that prefab the scan meets, through the same
		per-object overrides the game reads when it creates the object (Fields); a loaded object
		is created afresh so the change shows at once. An override taken out of the file is not
		undone by itself: put the game's own value there instead.
	*/
	internal class PrefabFields : IFeature
	{
		private class Rule
		{
			public string Prefab, Component, Field, Raw;
			public FieldInfo Info;
			public object Value;
		}

		private readonly Dictionary<int, List<Rule>> rules = new Dictionary<int, List<Rule>>();
		private readonly HashSet<ZDOID> done = new HashSet<ZDOID>();

		public World.Kind Kinds => World.Kind.Piece;

		public void Start()
		{
			Load();
		}

		public void Stop()
		{
			rules.Clear();
			done.Clear();
		}

		public string Load()
		{
			rules.Clear();
			done.Clear();
			string path = Path.Combine(Paths.ConfigPath, QoLPlugin.Settings.PrefabsFile.Value);
			if (!File.Exists(path))
			{
				File.WriteAllLines(path, new[]
				{
					"# Sarkastic.gg QoL: field overrides for player-built pieces. One per line:",
					"# <prefab> <Component>.<field> <value>",
					"# e.g.  piece_workbench CraftingStation.m_rangeBuild 20",
					"#       fire_pit Fireplace.m_secPerFuel 0",
					"# `qol reload` on the server console re-reads this file.",
				});
				return "created " + path;
			}
			int count = 0, bad = 0;
			foreach (string raw in File.ReadAllLines(path))
			{
				string line = raw.Trim();
				if (line.Length == 0 || line[0] == '#')
				{
					continue;
				}
				Rule rule = Parse(line, out string problem);
				if (rule == null)
				{
					bad++;
					QoLPlugin.Log.LogWarning($"Prefabs: ignoring '{line}': {problem}");
					continue;
				}
				int hash = rule.Prefab.GetStableHashCode();
				if (!rules.TryGetValue(hash, out List<Rule> list))
				{
					rules[hash] = list = new List<Rule>();
				}
				list.Add(rule);
				count++;
			}
			return $"{count} override(s) from {path}" + (bad > 0 ? $", {bad} ignored (see log)" : "");
		}

		private static Rule Parse(string line, out string problem)
		{
			problem = null;
			string[] words = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
			if (words.Length < 3 || words[1].IndexOf('.') <= 0)
			{
				problem = "expected <prefab> <Component>.<field> <value>";
				return null;
			}
			Rule rule = new Rule { Prefab = words[0], Raw = string.Join(" ", words, 2, words.Length - 2) };
			int dot = words[1].IndexOf('.');
			rule.Component = words[1].Substring(0, dot);
			rule.Field = words[1].Substring(dot + 1);
			GameObject prefab = ZNetScene.instance.GetPrefab(rule.Prefab);
			if (!prefab)
			{
				problem = $"no prefab '{rule.Prefab}'";
				return null;
			}
			Type type = AccessTools.TypeByName(rule.Component);
			if (type == null || !prefab.GetComponent(type))
			{
				problem = $"'{rule.Prefab}' has no component '{rule.Component}'";
				return null;
			}
			rule.Info = type.GetField(rule.Field, BindingFlags.Instance | BindingFlags.Public);
			if (rule.Info == null)
			{
				problem = $"'{rule.Component}' has no public field '{rule.Field}'";
				return null;
			}
			Type ft = rule.Info.FieldType;
			try
			{
				if (ft == typeof(int)) rule.Value = int.Parse(rule.Raw, CultureInfo.InvariantCulture);
				else if (ft == typeof(float)) rule.Value = float.Parse(rule.Raw, CultureInfo.InvariantCulture);
				else if (ft == typeof(bool)) rule.Value = bool.Parse(rule.Raw);
				else if (ft == typeof(string)) rule.Value = rule.Raw;
				else if (ft == typeof(Vector3))
				{
					string[] v = rule.Raw.Split(',');
					rule.Value = new Vector3(float.Parse(v[0], CultureInfo.InvariantCulture), float.Parse(v[1], CultureInfo.InvariantCulture), float.Parse(v[2], CultureInfo.InvariantCulture));
				}
				else if (ft == typeof(GameObject) || ft == typeof(ItemDrop))
				{
					if (!ZNetScene.instance.GetPrefab(rule.Raw))
					{
						problem = $"no prefab '{rule.Raw}' for {rule.Component}.{rule.Field}";
						return null;
					}
					rule.Value = rule.Raw;
				}
				else
				{
					problem = $"field type {ft.Name} cannot be overridden (int, float, bool, string, Vector3 x,y,z, prefab name)";
					return null;
				}
			}
			catch (Exception)
			{
				problem = $"'{rule.Raw}' is not a {ft.Name}";
				return null;
			}
			return rule;
		}

		public void Visit(ZDO zdo, World.Kind kind, List<ZNetPeer> peers)
		{
			if (!QoLPlugin.Settings.PrefabsEnabled.Value || done.Contains(zdo.m_uid) || zdo.GetLong(ZDOVars.s_creator) == 0L
				|| !rules.TryGetValue(zdo.GetPrefab(), out List<Rule> list))
			{
				return;
			}
			done.Add(zdo.m_uid);
			bool changed = false;
			foreach (Rule rule in list)
			{
				int key = Fields.Key(rule.Component, rule.Field);
				switch (rule.Value)
				{
					case int i when zdo.GetInt(key, int.MinValue) != i: Fields.Set(zdo, rule.Component, rule.Field, i); changed = true; break;
					case float f when zdo.GetFloat(key, float.NaN) != f: Fields.Set(zdo, rule.Component, rule.Field, f); changed = true; break;
					// The game stores a bool as an int 0/1.
					case bool b when zdo.GetInt(key, -1) != (b ? 1 : 0): Fields.Set(zdo, rule.Component, rule.Field, b); changed = true; break;
					case string str when zdo.GetString(key, "\0") != str: Fields.Set(zdo, rule.Component, rule.Field, str); changed = true; break;
					case Vector3 v when zdo.GetVec3(key, new Vector3(float.NaN, 0, 0)) != v: Fields.Set(zdo, rule.Component, rule.Field, v); changed = true; break;
				}
			}
			if (changed)
			{
				QoLPlugin.Log.LogInfo($"{World.PieceName(zdo)} at {zdo.GetPosition():F0}: {list.Count} field override(s) applied");
				if (Fields.IsLoaded(zdo))
				{
					ZDO fresh = Fields.Recreate(zdo);
					done.Add(fresh.m_uid);
				}
			}
		}
	}
}
