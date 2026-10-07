using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace SarkasticQoL
{
	/*
		What a player switched on or off for themselves (!pins on, !tame off ...), by their platform
		id, in a text file in BepInEx/config. A vanilla client stores nothing for us. A feature the
		player never chose follows its default in the config, which is off. Lines are
		`<platform id> +<feature> -<feature> ...`; a bare feature is an off from 0.4 and earlier,
		when only offs were kept.
	*/
	internal class PlayerState
	{
		private readonly string path = Path.Combine(Paths.ConfigPath, "sarkasticeu.qol.players.txt");
		private readonly Dictionary<string, Dictionary<string, bool>> chosen = new Dictionary<string, Dictionary<string, bool>>();

		public void Load()
		{
			chosen.Clear();
			if (!File.Exists(path))
			{
				return;
			}
			foreach (string line in File.ReadAllLines(path))
			{
				string[] f = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				if (f.Length < 2 || f[0].StartsWith("#"))
				{
					continue;
				}
				Dictionary<string, bool> choices = Choices(f[0]);
				for (int i = 1; i < f.Length; i++)
				{
					bool on = f[i][0] == '+';
					string feature = f[i][0] == '+' || f[i][0] == '-' ? f[i].Substring(1) : f[i];
					if (feature.Length > 0)
					{
						choices[feature] = on;
					}
				}
			}
		}

		private void Save()
		{
			List<string> lines = new List<string> { "# Sarkastic.gg QoL: per player, what they switched on (+) or off (-) for themselves. <platform id> +<feature> -<feature> ..." };
			foreach (KeyValuePair<string, Dictionary<string, bool>> entry in chosen)
			{
				if (entry.Value.Count > 0)
				{
					List<string> words = new List<string> { entry.Key };
					foreach (KeyValuePair<string, bool> choice in entry.Value)
					{
						words.Add((choice.Value ? "+" : "-") + choice.Key);
					}
					lines.Add(string.Join(" ", words));
				}
			}
			File.WriteAllLines(path, lines);
		}

		private Dictionary<string, bool> Choices(string id)
		{
			if (!chosen.TryGetValue(id, out Dictionary<string, bool> choices))
			{
				choices = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
				chosen[id] = choices;
			}
			return choices;
		}

		public static string IdOf(ZNetPeer peer)
		{
			return peer.m_socket.GetHostName();
		}

		// The config default for a player who never chose.
		private static bool Default(string feature)
		{
			Settings s = QoLPlugin.Settings;
			switch (feature.ToLowerInvariant())
			{
				case "pins": return s.PinsDefault.Value;
				case "tame": return s.TamesProgressDefault.Value;
				default: return false;
			}
		}

		public bool Wants(ZNetPeer peer, string feature)
		{
			return chosen.TryGetValue(IdOf(peer), out Dictionary<string, bool> choices) && choices.TryGetValue(feature, out bool on)
				? on
				: Default(feature);
		}

		public void Set(ZNetPeer peer, string feature, bool on)
		{
			Dictionary<string, bool> choices = Choices(IdOf(peer));
			if (!choices.TryGetValue(feature, out bool was) || was != on)
			{
				choices[feature] = on;
				Save();
			}
		}
	}
}
