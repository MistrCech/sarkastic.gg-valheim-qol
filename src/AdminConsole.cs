using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using SarkasticQoL.Features;

namespace SarkasticQoL
{
	/*
		The admin's side: a `qol` command in the game's own console, which a dedicated server has
		but does not read by itself. Dedicated Simulation passes what it cannot handle from the
		server's standard input (a panel such as AMP) to that console and copies the output back.
	*/
	internal static class AdminConsole
	{
		private static bool s_registered;

		public static void Register()
		{
			if (s_registered)
			{
				return;
			}
			s_registered = true;
			new Terminal.ConsoleCommand("qol", "Sarkastic.eu QoL: qol status | reload | set <Section.Key> <value> | containers | pins | deaths | help",
				delegate (Terminal.ConsoleEventArgs args)
				{
					foreach (string line in Run(args).Split('\n'))
					{
						args.Context.AddString(line);
					}
				}, isCheat: false, isNetwork: false, onlyServer: true);
		}

		private static string Run(Terminal.ConsoleEventArgs args)
		{
			Settings s = QoLPlugin.Settings;
			string sub = args.Length >= 2 ? args[1].ToLowerInvariant() : "help";
			switch (sub)
			{
				case "status":
					return Status();
				case "reload":
					s.Reload();
					Pins.Reload();
					string prefabs = "";
					foreach (IFeature feature in World.Features)
					{
						if (feature is PrefabFields fields)
						{
							prefabs = ", prefabs: " + fields.Load();
						}
					}
					return "config re-read" + prefabs;
				case "set":
					return args.Length >= 4 ? Set(args[2], string.Join(" ", args.Args, 3, args.Length - 3)) : "usage: qol set <Section.Key> <value>, e.g. qol set Sleep.RequiredPercent 50";
				case "containers":
					return Containers();
				case "pins":
					return PinsCommand(args);
				case "deaths":
					{
						int count = args.Length >= 3 && int.TryParse(args[2], out int n) ? System.Math.Max(1, n) : 20;
						List<string> lines = TameDeaths.Lines(count);
						return lines.Count == 0 ? "no deaths of tamed creatures recorded" : $"{TameDeaths.Count} recorded, the latest {lines.Count}:\n  " + string.Join("\n  ", lines);
					}
				default:
					return "qol status | reload | set <Section.Key> <value> | containers | pins | deaths [n]";
			}
		}

		private static string Status()
		{
			Settings s = QoLPlugin.Settings;
			return $"{QoLPlugin.PluginName} {QoLPlugin.PluginVersion}: chat prefix '{s.ChatPrefix.Value}'"
				+ $" | sleep {(s.SleepEnabled.Value ? $"{s.SleepRequiredPercent.Value}%, min {s.SleepMinInBed.Value}, warn {s.SleepWarnSeconds.Value} s" : "off")}"
				+ $" | doors {(s.DoorsEnabled.Value ? $"close after {s.DoorsCloseAfterSeconds.Value:0} s, default {OnOff(s.DoorsDefault.Value)}" : "off")}"
				+ $" | ballistas {(s.BallistasEnabled.Value ? $"players {s.BallistasTargetPlayers.Value}, tames {s.BallistasTargetTames.Value}" : "off")}"
				+ $" | feeding {(s.FeedEnabled.Value ? $"stations default {OnOff(s.FeedSmelters.Value)}, fires default {OnOff(s.FeedFireplaces.Value)}" : "off")}"
				+ $" | tame progress {(s.TamesProgress.Value ? $"default {OnOff(s.TamesProgressDefault.Value)}" : "off")}"
				+ $" | containers {(s.ContainersEnabled.Value ? "on" : "off")} | prefabs {(s.PrefabsEnabled.Value ? "on" : "off")}"
				+ $" | motd {(s.MotdText.Value.Length > 0 ? "set" : "empty")}"
				+ $" | pins {(s.PinsEnabled.Value ? $"default {OnOff(s.PinsDefault.Value)}, {Pins.Count} known, on {Pins.TableCount} tables" : "off")}";
		}

		private static string OnOff(bool on)
		{
			return on ? "on" : "off";
		}

		private static string Set(string key, string value)
		{
			int dot = key.IndexOf('.');
			if (dot <= 0)
			{
				return "usage: qol set <Section.Key> <value>";
			}
			ConfigDefinition definition = new ConfigDefinition(key.Substring(0, dot), key.Substring(dot + 1));
			if (!QoLPlugin.Settings.File.ContainsKey(definition))
			{
				return $"no setting {key}; sections: General, Chat, Guards, Feeding, Labels, Signs, Sorting, Motd, Sleep, Doors, Ballistas, Tames, Containers, Prefabs, Pins";
			}
			ConfigEntryBase entry = QoLPlugin.Settings.File[definition];
			try
			{
				entry.SetSerializedValue(value);
			}
			catch (System.Exception e)
			{
				return $"{key}: '{value}' is not valid: {e.Message}";
			}
			if (definition.Section == "Pins")
			{
				Pins.Reload();
			}
			return $"{key} = {entry.GetSerializedValue()}";
		}

		private static string PinsCommand(Terminal.ConsoleEventArgs args)
		{
			switch (args.Length >= 3 ? args[2].ToLowerInvariant() : "")
			{
				case "list":
					return Pins.List(args.Length >= 4 ? args[3] : null);
				case "forget":
					return Pins.Forget();
				case "clear":
					return Pins.Clear();
				default:
					return Pins.Status();
			}
		}

		private static string Containers()
		{
			StringBuilder text = new StringBuilder("container sizes (qol set Containers.<prefab> WxH):");
			foreach (KeyValuePair<int, ConfigEntry<string>> entry in QoLPlugin.Settings.ContainerSizes)
			{
				text.Append($"\n  {entry.Value.Definition.Key} = {entry.Value.Value} (game {entry.Value.DefaultValue})");
			}
			return text.ToString();
		}
	}
}
