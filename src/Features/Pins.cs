using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;
using UnityEngine;

namespace SarkasticQoL.Features
{
	/*
		Map pins for what players have found, written into every cartography table in the world:
		clusters of berries, mushrooms and the like ("Raspberries x7"), ore deposits, dungeon
		entrances and player-built portals. A vanilla client reads them off a table as usual.

		Found: while a player is within DiscoverRange of it (the scan hands us the objects around
		each player). A cluster is ClusterMin or more of the same pickable within ClusterRadius,
		pinned at its centre; lone bushes stay off the map, and so does anything within
		FarmDistance of a player-built piece (a farm, or a base the players know anyway).

		Table data (MapTable, Minimap.GetSharedMapData / AddSharedMapData, version 3): int version,
		int count, count bools of explored map (textureSize squared), int pins, then per pin long
		owner, string name, Vector3, int type, bool checked, string author. The server's pins carry
		Owner as their owner, so a client treats them as another player's: it shows them, lets a
		player delete one, and drops them again when a later read of the table no longer lists
		them. A player who deletes one of ours and writes the table takes it off every table for
		good (the deletion is remembered in the pins file). Everything a player wrote -- the
		explored map, their pins -- is kept as it is; the tables are only rewritten with a check
		that nobody changed them meanwhile, and the decompress/compress of the map happens off the
		main thread.
	*/
	internal class Pins : IFeature, IScanAware
	{
		// Owner of the server's pins in a table: not 0 (the reader's own) and unlike any player id.
		public const long Owner = 0x53514F4C50494E53L;
		private const float TableDistance = 5f;     // no rewrite while a player stands at the table
		private const float DeleteGraceSeconds = 30f; // a pin written this recently and missing from a player's write was not deleted by them

		public enum Category { Pickables, Ores, Dungeons, Portals }

		public class Pin
		{
			public Category category;
			public string prefab;  // what it stands for: the cluster's pickable, the deposit, the location, the portal
			public int prefabHash;
			public Vector3 pos;
			public int count;
			public ZDOID uid;      // the object itself (ores, dungeons, portals); None for clusters
			public string name;
			public Minimap.PinType type;
			public string Key => KeyOf(name, pos, (int)type);
		}

		public static string KeyOf(string name, Vector3 pos, int type)
		{
			return $"{name}@{Mathf.RoundToInt(pos.x)},{Mathf.RoundToInt(pos.z)}#{type}";
		}

		private struct TablePin
		{
			public long owner;
			public string name;
			public Vector3 pos;
			public int type;
			public bool check;
			public string author;
		}

		private class Table
		{
			public ZDOID id;
			public Vector3 pos;
			public uint revision;                   // the data revision last read or written
			public bool known;                      // read at least once (or had no data)
			public readonly Dictionary<string, float> ours = new Dictionary<string, float>(); // our pins in it -> when written
			public float nextWrite;
			public Job job;
		}

		private class Job
		{
			public ZDOID id;
			public uint baseRevision;
			public byte[] input;
			public List<TablePin> wanted;           // null: only read
			public int emptySize;                   // texture size for a table without data
			public volatile bool done;
			public string error;
			public byte[] output;
			public int exploredLength;
			public readonly HashSet<string> ours = new HashSet<string>();
			public int others;
		}

		private static readonly List<Pin> s_pins = new List<Pin>();
		private static readonly List<Pin> s_removed = new List<Pin>();
		private static readonly Dictionary<ZDOID, Table> s_tables = new Dictionary<ZDOID, Table>();
		private static readonly HashSet<string> s_desired = new HashSet<string>();
		private static int s_pinsVersion, s_desiredVersion = -1;
		private static bool s_desiredShared;
		// Per player (platform id): the keys of the pins put on their map.
		private static readonly Dictionary<string, HashSet<string>> s_sent = new Dictionary<string, HashSet<string>>();
		private static int s_learnedTextureSize;
		private static int s_reading;
		private static bool s_saveDue, s_maxReported;
		private static float s_saveTimer, s_checkTimer;
		private static int s_tableHash;

		// Config lists, parsed once per change of the config text.
		private static readonly Dictionary<int, string> s_pickableNames = new Dictionary<int, string>();
		private static readonly Dictionary<int, string> s_oreNames = new Dictionary<int, string>();
		private static readonly Dictionary<int, string> s_dungeonNames = new Dictionary<int, string>();
		private static readonly Dictionary<int, string> s_prefabOf = new Dictionary<int, string>(); // hash -> the listed prefab or location name
		private static string s_pickableRaw, s_oreRaw, s_dungeonRaw;

		// Per scan.
		private static ZNetPeer s_peer;
		private static readonly Dictionary<int, List<ZDO>> s_pickables = new Dictionary<int, List<ZDO>>();
		private static readonly List<Pin> s_dropped = new List<Pin>();
		private static readonly List<ZDO> s_search = new List<ZDO>();

		public World.Kind Kinds => World.Kind.MapTable | World.Kind.Location | World.Kind.Portal | World.Kind.PinObject;

		public static bool Enabled => QoLPlugin.Settings.Enabled.Value && QoLPlugin.Settings.PinsEnabled.Value;
		public static int Count => s_pins.Count;
		public static int RemovedCount => s_removed.Count;
		public static int TableCount => s_tables.Count;

		public void Start()
		{
			s_pins.Clear();
			s_removed.Clear();
			s_sent.Clear();
			s_tables.Clear();
			s_pickableRaw = s_oreRaw = s_dungeonRaw = null;
			s_learnedTextureSize = 0;
			s_reading = 0;
			s_maxReported = false;
			s_tableHash = "piece_cartographytable".GetStableHashCode();
			Reload();
			Load();
			Relabel();
			foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
			{
				if (zdo.GetPrefab() == s_tableHash)
				{
					Register(zdo);
				}
			}
			QoLPlugin.Log.LogInfo($"Pins: {s_pins.Count} known, {s_removed.Count} removed by players, {s_tables.Count} map tables, map texture {TextureSize()} (game: {(Minimap.instance ? Minimap.instance.m_textureSize.ToString() : "no minimap")})");
			CheckNames();
		}

		public void Stop()
		{
			if (s_saveDue)
			{
				Save();
			}
			s_tables.Clear();
		}

		// Re-reads the name lists and flags their prefabs for the scan; after a config change.
		public static void Reload()
		{
			Settings s = QoLPlugin.Settings;
			if (!ReferenceEquals(s_pickableRaw, s.PinsPickableNames.Value) && s_pickableRaw != s.PinsPickableNames.Value)
			{
				s_pickableRaw = s.PinsPickableNames.Value;
				ParseNames(s_pickableRaw, s_pickableNames);
			}
			if (!ReferenceEquals(s_oreRaw, s.PinsOreNames.Value) && s_oreRaw != s.PinsOreNames.Value)
			{
				s_oreRaw = s.PinsOreNames.Value;
				ParseNames(s_oreRaw, s_oreNames);
			}
			if (!ReferenceEquals(s_dungeonRaw, s.PinsDungeonNames.Value) && s_dungeonRaw != s.PinsDungeonNames.Value)
			{
				s_dungeonRaw = s.PinsDungeonNames.Value;
				ParseNames(s_dungeonRaw, s_dungeonNames);
			}
			foreach (int hash in s_pickableNames.Keys)
			{
				World.Flag(hash, World.Kind.PinObject);
			}
			foreach (int hash in s_oreNames.Keys)
			{
				World.Flag(hash, World.Kind.PinObject);
			}
			Relabel();
		}

		// "RaspberryBush=Raspberries, Pickable_Tin=Tin" -> prefab hash -> label. Without '=' the label is the prefab's readable name.
		private static void ParseNames(string raw, Dictionary<int, string> into)
		{
			into.Clear();
			foreach (string part in (raw ?? "").Split(','))
			{
				string entry = part.Trim();
				if (entry.Length == 0)
				{
					continue;
				}
				int eq = entry.IndexOf('=');
				string prefab = eq < 0 ? entry : entry.Substring(0, eq).Trim();
				string label = eq < 0 ? World.PrettyName(entry) : entry.Substring(eq + 1).Trim();
				if (prefab.Length > 0 && label.Length > 0)
				{
					into[prefab.GetStableHashCode()] = label;
					s_prefabOf[prefab.GetStableHashCode()] = prefab;
				}
			}
		}

		// The pins' names follow the lists: after a change of the config (short names, say) every pin is
		// renamed, and one whose thing is no longer listed is dropped. Icons likewise.
		private static void Relabel()
		{
			bool changed = Relabel(s_pins, true) | Relabel(s_removed, false);
			if (changed)
			{
				s_pinsVersion++;
				s_saveDue = true;
			}
		}

		private static bool Relabel(List<Pin> pins, bool dropUnlisted)
		{
			bool changed = false;
			for (int i = pins.Count - 1; i >= 0; i--)
			{
				Pin pin = pins[i];
				string label = LabelFor(pin);
				if (label == null)
				{
					if (dropUnlisted)
					{
						pins.RemoveAt(i);
						changed = true;
						QoLPlugin.Log.LogInfo($"Pins: {pin.name} at ({pin.pos.x:0}, {pin.pos.z:0}) dropped: {pin.prefab} is not in the lists");
					}
					continue;
				}
				Minimap.PinType type = Icon(pin.category);
				if (label != pin.name || type != pin.type)
				{
					pin.name = label;
					pin.type = type;
					changed = true;
				}
			}
			return changed;
		}

		private static string LabelFor(Pin pin)
		{
			switch (pin.category)
			{
				case Category.Pickables:
					return s_pickableNames.TryGetValue(pin.prefabHash, out string pickable) ? $"{pickable} x{pin.count}" : null;
				case Category.Ores:
					return s_oreNames.TryGetValue(pin.prefabHash, out string ore) ? ore : null;
				case Category.Dungeons:
					return s_dungeonNames.TryGetValue(pin.prefabHash, out string dungeon) ? dungeon : null;
				default:
					ZDO portal = pin.uid.IsNone() || ZDOMan.instance == null ? null : ZDOMan.instance.GetZDO(pin.uid);
					return portal == null ? pin.name : PortalName(portal);
			}
		}

		private static string PortalName(ZDO portal)
		{
			string tag = portal.GetString(ZDOVars.s_tag).Trim();
			try
			{
				return string.Format(QoLPlugin.Settings.PinsPortalName.Value, tag).Trim();
			}
			catch (FormatException)
			{
				return ("Portal " + tag).Trim();
			}
		}

		public static string Unknown()
		{
			List<string> unknown = new List<string>();
			foreach (string entry in Split(s_pickableRaw))
			{
				if (!ZNetScene.instance.GetPrefab(entry.GetStableHashCode())) unknown.Add(entry);
			}
			foreach (string entry in Split(s_oreRaw))
			{
				if (!ZNetScene.instance.GetPrefab(entry.GetStableHashCode())) unknown.Add(entry);
			}
			// ZoneSystem sets m_prefabName from the prefab reference when it sets the locations up (SetupLocations).
			HashSet<string> locations = new HashSet<string>();
			foreach (ZoneSystem.ZoneLocation location in ZoneSystem.instance.m_locations)
			{
				if (!string.IsNullOrEmpty(location.m_prefabName))
				{
					locations.Add(location.m_prefabName);
				}
			}
			foreach (string entry in Split(s_dungeonRaw))
			{
				if (!locations.Contains(entry)) unknown.Add(entry);
			}
			return string.Join(", ", unknown);
		}

		private static void CheckNames()
		{
			string unknown = Unknown();
			if (unknown.Length > 0)
			{
				QoLPlugin.Log.LogWarning($"Pins: names in the config this game does not have: {unknown}");
			}
		}

		private static IEnumerable<string> Split(string raw)
		{
			foreach (string part in (raw ?? "").Split(','))
			{
				string entry = part.Trim();
				int eq = entry.IndexOf('=');
				string prefab = eq < 0 ? entry : entry.Substring(0, eq).Trim();
				if (prefab.Length > 0)
				{
					yield return prefab;
				}
			}
		}

		private static Minimap.PinType Icon(Category category)
		{
			Settings s = QoLPlugin.Settings;
			string icon;
			switch (category)
			{
				case Category.Pickables: icon = s.PinsPickablesIcon.Value; break;
				case Category.Ores: icon = s.PinsOresIcon.Value; break;
				case Category.Dungeons: icon = s.PinsDungeonsIcon.Value; break;
				default: icon = s.PinsPortalsIcon.Value; break;
			}
			switch ((icon ?? "").Trim().ToLowerInvariant())
			{
				case "fire": return Minimap.PinType.Icon0;
				case "house": return Minimap.PinType.Icon1;
				case "hammer": return Minimap.PinType.Icon2;
				case "portal": return Minimap.PinType.Icon4;
				default: return Minimap.PinType.Icon3;
			}
		}

		private static bool CategoryOn(Category category)
		{
			Settings s = QoLPlugin.Settings;
			switch (category)
			{
				case Category.Pickables: return s.PinsPickables.Value;
				case Category.Ores: return s.PinsOres.Value;
				case Category.Dungeons: return s.PinsDungeons.Value;
				default: return s.PinsPortals.Value;
			}
		}

		// ---- the scan: finding things ----

		public void BeginScan(ZNetPeer peer)
		{
			s_peer = peer;
			foreach (List<ZDO> list in s_pickables.Values)
			{
				list.Clear();
			}
		}

		public void Visit(ZDO zdo, World.Kind kind, List<ZNetPeer> peers)
		{
			if ((kind & World.Kind.MapTable) != 0)
			{
				Register(zdo);
			}
			if (!Enabled || s_peer == null)
			{
				return;
			}
			if ((kind & World.Kind.PinObject) != 0)
			{
				int prefab = zdo.GetPrefab();
				if (s_pickableNames.ContainsKey(prefab))
				{
					if (!s_pickables.TryGetValue(prefab, out List<ZDO> list))
					{
						s_pickables[prefab] = list = new List<ZDO>();
					}
					list.Add(zdo);
				}
				else if (s_oreNames.TryGetValue(prefab, out string label))
				{
					Single(Category.Ores, zdo, label, prefab, World.PrefabName(zdo));
				}
			}
			if ((kind & World.Kind.Location) != 0)
			{
				// The pin stands for the location, not for the proxy object that marks it.
				int location = zdo.GetInt(ZDOVars.s_location);
				if (s_dungeonNames.TryGetValue(location, out string dungeon))
				{
					Single(Category.Dungeons, zdo, dungeon, location, s_prefabOf.TryGetValue(location, out string named) ? named : location.ToString());
				}
			}
			if ((kind & World.Kind.Portal) != 0 && zdo.GetLong(ZDOVars.s_creator) != 0L)
			{
				Single(Category.Portals, zdo, PortalName(zdo), zdo.GetPrefab(), World.PrefabName(zdo));
			}
		}

		// One object, one pin: a deposit, a dungeon entrance, a portal.
		private static void Single(Category category, ZDO zdo, string name, int prefabHash, string prefab)
		{
			Settings s = QoLPlugin.Settings;
			Pin existing = ByUid(zdo.m_uid);
			if (existing != null)
			{
				if (existing.name != name && existing.category == Category.Portals)
				{
					Drop(existing, "renamed");
					existing = null;
				}
				else
				{
					return;
				}
			}
			if (!CategoryOn(category) || !World.Players.Wants(s_peer, "pins"))
			{
				return;
			}
			Vector3 pos = zdo.GetPosition();
			float range = s.PinsDiscoverRange.Value;
			if ((pos - World.Position(s_peer)).sqrMagnitude > range * range || Covered(category, prefabHash, name, pos, 3f))
			{
				return;
			}
			Add(new Pin { category = category, prefab = prefab, prefabHash = prefabHash, pos = pos, count = 1, uid = zdo.m_uid, name = name, type = Icon(category) });
		}

		public void EndScan(ZNetPeer peer)
		{
			try
			{
				if (Enabled)
				{
					Clusters(peer);
					Deliver(peer);
				}
			}
			finally
			{
				s_peer = null;
			}
		}

		/*
			The player's own map gets every pin they have been near and not had yet, the way a
			runestone gives one: Game.RPC_DiscoverLocationResponse adds it as the player's own saved
			pin (theirs to keep or delete, on nobody else's map unless they write a table). A client
			with a map shown (the usual case; only a world without a map turns the player to face the
			spot) adds it silently. What each player has had is remembered, so a pin they deleted is
			not given again; !pins reset forgets that.
		*/
		private static void Deliver(ZNetPeer peer)
		{
			if (peer.m_characterID.IsNone() || !World.Players.Wants(peer, "pins"))
			{
				return;
			}
			float range = QoLPlugin.Settings.PinsDiscoverRange.Value;
			Vector3 at = World.Position(peer);
			HashSet<string> had = SentTo(PlayerState.IdOf(peer));
			List<string> names = null;
			foreach (Pin pin in s_pins)
			{
				// A portal counts by its object, not its name: players retag portals all the time, and the pin
				// on the map cannot be taken back, so one pin per portal, under its tag at the time.
				if (!CategoryOn(pin.category) || (pin.pos - at).sqrMagnitude > range * range || !had.Add(SentKey(pin)))
				{
					continue;
				}
				ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "RPC_DiscoverLocationResponse", pin.name, (int)pin.type, pin.pos, false);
				(names ?? (names = new List<string>())).Add(pin.name);
			}
			if (names != null)
			{
				s_saveDue = true;
				QoLPlugin.Log.LogInfo($"Pins: {names.Count} to {peer.m_playerName}'s map: {string.Join(", ", names)}");
			}
		}

		// What a player has had: a portal by its object (players retag portals all the time), anything else by
		// name and place; the icon is left out, so a change of icons does not put a second pin on the map.
		private static string SentKey(Pin pin)
		{
			return pin.category == Category.Portals && !pin.uid.IsNone() ? $"portal {pin.uid}" : $"{pin.name}@{Mathf.RoundToInt(pin.pos.x)},{Mathf.RoundToInt(pin.pos.z)}";
		}

		private static HashSet<string> SentTo(string playerId)
		{
			if (!s_sent.TryGetValue(playerId, out HashSet<string> set))
			{
				s_sent[playerId] = set = new HashSet<string>();
			}
			return set;
		}

		// !pins reset: the pins near the player come once more.
		public static string ForgetPlayer(ZNetPeer peer)
		{
			int n = s_sent.TryGetValue(PlayerState.IdOf(peer), out HashSet<string> set) ? set.Count : 0;
			s_sent.Remove(PlayerState.IdOf(peer));
			s_saveDue = true;
			return n == 0 ? "You had no pins from the server yet" : $"Forgotten which {n} pins you had; those near you come again in a moment";
		}

		private static void Clusters(ZNetPeer peer)
		{
			Settings s = QoLPlugin.Settings;
			Vector3 at = World.Position(peer);
			float range = s.PinsDiscoverRange.Value, radius = s.PinsClusterRadius.Value;
			int min = Mathf.Max(1, s.PinsClusterMin.Value);
			// Clusters near the player: still enough of them?
			s_dropped.Clear();
			foreach (Pin pin in s_pins)
			{
				if (pin.category == Category.Pickables && (pin.pos - at).sqrMagnitude <= range * range)
				{
					int count = s_pickables.TryGetValue(pin.prefabHash, out List<ZDO> members) ? CountWithin(members, pin.pos, radius) : 0;
					if (count < min)
					{
						s_dropped.Add(pin);
					}
				}
			}
			foreach (Pin pin in s_dropped)
			{
				Drop(pin, "gone");
			}
			if (!s.PinsPickables.Value || !World.Players.Wants(peer, "pins"))
			{
				return;
			}
			foreach (KeyValuePair<int, List<ZDO>> entry in s_pickables)
			{
				if (entry.Value.Count < min || !s_pickableNames.TryGetValue(entry.Key, out string label))
				{
					continue;
				}
				foreach (ZDO zdo in entry.Value)
				{
					Vector3 p = zdo.GetPosition();
					if ((p - at).sqrMagnitude > range * range || Covered(Category.Pickables, entry.Key, label, p, radius))
					{
						continue;
					}
					Vector3 centre = Centroid(entry.Value, p, radius, out int count);
					if (count >= min)
					{
						centre = Centroid(entry.Value, centre, radius, out count);
					}
					if (count < min || NearPlayerBuilt(centre, s.PinsFarmDistance.Value))
					{
						continue;
					}
					Add(new Pin { category = Category.Pickables, prefab = World.PrefabName(zdo), prefabHash = entry.Key, pos = centre, count = count, uid = ZDOID.None, name = $"{label} x{count}", type = Icon(Category.Pickables) });
				}
			}
		}

		private static int CountWithin(List<ZDO> members, Vector3 centre, float radius)
		{
			int count = 0;
			float sq = radius * radius;
			foreach (ZDO zdo in members)
			{
				if ((zdo.GetPosition() - centre).sqrMagnitude <= sq)
				{
					count++;
				}
			}
			return count;
		}

		private static Vector3 Centroid(List<ZDO> members, Vector3 around, float radius, out int count)
		{
			Vector3 sum = Vector3.zero;
			count = 0;
			float sq = radius * radius;
			foreach (ZDO zdo in members)
			{
				Vector3 p = zdo.GetPosition();
				if ((p - around).sqrMagnitude <= sq)
				{
					sum += p;
					count++;
				}
			}
			return count > 0 ? sum / count : around;
		}

		private static bool NearPlayerBuilt(Vector3 centre, float distance)
		{
			if (distance <= 0f)
			{
				return false;
			}
			float sq = distance * distance;
			foreach (ZDO zdo in World.Near)
			{
				if ((World.KindOf(zdo) & World.Kind.Piece) != 0 && zdo.GetLong(ZDOVars.s_creator) != 0L && (zdo.GetPosition() - centre).sqrMagnitude <= sq)
				{
					return true;
				}
			}
			return false;
		}

		// A known or player-deleted pin for the same thing within the distance: the same pickable for a
		// cluster (its name carries a count), the same name otherwise (a deposit keeps its name when it
		// becomes its fractured prefab).
		private static bool Covered(Category category, int prefabHash, string name, Vector3 pos, float distance)
		{
			return Covered(s_pins, category, prefabHash, name, pos, distance) || Covered(s_removed, category, prefabHash, name, pos, distance);
		}

		private static bool Covered(List<Pin> pins, Category category, int prefabHash, string name, Vector3 pos, float distance)
		{
			float sq = distance * distance;
			foreach (Pin pin in pins)
			{
				if (pin.category != category)
				{
					continue;
				}
				bool same = category == Category.Pickables ? pin.prefabHash == prefabHash : pin.name == name;
				if (same && (pin.pos - pos).sqrMagnitude <= sq)
				{
					return true;
				}
			}
			return false;
		}

		private static Pin ByUid(ZDOID uid)
		{
			foreach (Pin pin in s_pins)
			{
				if (pin.uid == uid)
				{
					return pin;
				}
			}
			return null;
		}

		private static void Add(Pin pin)
		{
			string key = pin.Key;
			foreach (Pin known in s_pins)
			{
				if (known.Key == key)
				{
					return;
				}
			}
			int max = QoLPlugin.Settings.PinsMaxPins.Value;
			if (s_pins.Count >= max)
			{
				if (!s_maxReported)
				{
					s_maxReported = true;
					QoLPlugin.Log.LogWarning($"Pins: {max} pins (MaxPins), nothing more is added");
				}
				return;
			}
			s_pins.Add(pin);
			s_pinsVersion++;
			s_saveDue = true;
			QoLPlugin.Log.LogInfo($"Pins: {pin.name} at ({pin.pos.x:0}, {pin.pos.z:0}) found by {(s_peer != null ? s_peer.m_playerName : "?")}");
		}

		private static void Drop(Pin pin, string why)
		{
			if (s_pins.Remove(pin))
			{
				s_pinsVersion++;
				s_saveDue = true;
				QoLPlugin.Log.LogInfo($"Pins: {pin.name} at ({pin.pos.x:0}, {pin.pos.z:0}) dropped: {why}");
			}
		}

		// ---- the tables ----

		private static void Register(ZDO zdo)
		{
			if (!s_tables.ContainsKey(zdo.m_uid))
			{
				s_tables[zdo.m_uid] = new Table { id = zdo.m_uid, pos = zdo.GetPosition() };
			}
		}

		private static int TextureSize()
		{
			if (s_learnedTextureSize > 0)
			{
				return s_learnedTextureSize;
			}
			if (Minimap.instance && Minimap.instance.m_textureSize > 0)
			{
				return Minimap.instance.m_textureSize;
			}
			return Mathf.Max(64, QoLPlugin.Settings.PinsMapTextureSize.Value);
		}

		public static void Tick(float dt)
		{
			if (!Enabled)
			{
				return;
			}
			float now = Time.realtimeSinceStartup;
			s_checkTimer -= dt;
			if (s_checkTimer <= 0f)
			{
				s_checkTimer = 10f;
				CheckObjects();
				DropDisabled();
			}
			bool shared = QoLPlugin.Settings.PinsSharedTables.Value;
			if (s_desiredVersion != s_pinsVersion || s_desiredShared != shared)
			{
				s_desiredVersion = s_pinsVersion;
				s_desiredShared = shared;
				s_desired.Clear();
				foreach (Pin pin in s_pins)
				{
					// With sharing off nothing of ours belongs in a table: one that has our pins is rewritten without them.
					if (shared && CategoryOn(pin.category))
					{
						s_desired.Add(pin.Key);
					}
				}
			}
			List<ZDOID> gone = null;
			foreach (Table table in s_tables.Values)
			{
				ZDO zdo = ZDOMan.instance.GetZDO(table.id);
				if (zdo == null || zdo.GetPrefab() != s_tableHash)
				{
					(gone ?? (gone = new List<ZDOID>())).Add(table.id);
					continue;
				}
				if (table.job != null)
				{
					if (table.job.done)
					{
						Finish(table, zdo, now);
					}
					continue;
				}
				byte[] data = zdo.GetByteArray(ZDOVars.s_data);
				if (!table.known || zdo.DataRevision != table.revision)
				{
					if (data == null)
					{
						table.known = true;
						table.revision = zdo.DataRevision;
						table.ours.Clear();
					}
					else
					{
						StartJob(table, zdo, data, null);
						s_reading++;
					}
					continue;
				}
				if (s_reading > 0 || now < table.nextWrite || Same(table) || World.AnyPlayerWithin(ZNet.instance.GetPeers(), table.pos, TableDistance))
				{
					continue;
				}
				// With sharing off the list is empty: the table is written once more without ours, then left alone.
				List<TablePin> wanted = new List<TablePin>();
				if (shared)
				{
					foreach (Pin pin in s_pins)
					{
						if (CategoryOn(pin.category))
						{
							wanted.Add(new TablePin { owner = Owner, name = pin.name, pos = pin.pos, type = (int)pin.type, check = false, author = "" });
						}
					}
				}
				StartJob(table, zdo, data, wanted);
			}
			if (gone != null)
			{
				foreach (ZDOID id in gone)
				{
					s_tables.Remove(id);
				}
			}
			s_saveTimer -= dt;
			if (s_saveDue && s_saveTimer <= 0f)
			{
				s_saveTimer = 5f;
				Save();
			}
		}

		private static bool Same(Table table)
		{
			if (table.ours.Count != s_desired.Count)
			{
				return false;
			}
			foreach (string key in s_desired)
			{
				if (!table.ours.ContainsKey(key))
				{
					return false;
				}
			}
			return true;
		}

		private static void StartJob(Table table, ZDO zdo, byte[] data, List<TablePin> wanted)
		{
			Job job = new Job { id = table.id, baseRevision = zdo.DataRevision, input = data, wanted = wanted, emptySize = TextureSize() };
			table.job = job;
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					Work(job);
				}
				catch (Exception e)
				{
					job.error = e.ToString();
				}
				job.done = true;
			});
		}

		// Off the main thread: read the table's data, and with `wanted` write it anew with our pins.
		private static void Work(Job job)
		{
			byte[] explored;
			List<TablePin> pins;
			if (job.input != null)
			{
				Parse(job.input, out explored, out pins);
			}
			else
			{
				explored = new byte[job.emptySize * job.emptySize];
				pins = new List<TablePin>();
			}
			job.exploredLength = explored.Length;
			List<TablePin> kept = new List<TablePin>();
			Dictionary<string, TablePin> before = new Dictionary<string, TablePin>();
			foreach (TablePin pin in pins)
			{
				if (pin.owner == Owner)
				{
					string key = KeyOf(pin.name, pin.pos, pin.type);
					job.ours.Add(key);
					before[key] = pin;
				}
				else
				{
					kept.Add(pin);
				}
			}
			job.others = kept.Count;
			if (job.wanted == null)
			{
				return;
			}
			job.ours.Clear();
			foreach (TablePin wanted in job.wanted)
			{
				// A client adds a table's pin only where it has none within a metre; a player's own pin there wins.
				bool taken = false;
				foreach (TablePin other in kept)
				{
					if ((other.pos - wanted.pos).sqrMagnitude < 1f)
					{
						taken = true;
						break;
					}
				}
				if (taken)
				{
					continue;
				}
				string key = KeyOf(wanted.name, wanted.pos, wanted.type);
				TablePin pin = wanted;
				if (before.TryGetValue(key, out TablePin old))
				{
					pin.check = old.check; // a player may have crossed it out
				}
				kept.Add(pin);
				job.ours.Add(key);
			}
			job.output = Build(explored, kept);
		}

		private static void Finish(Table table, ZDO zdo, float now)
		{
			Job job = table.job;
			table.job = null;
			if (job.wanted == null)
			{
				s_reading = Mathf.Max(0, s_reading - 1);
			}
			if (job.error != null)
			{
				QoLPlugin.Log.LogWarning($"Pins: map table at ({table.pos.x:0}, {table.pos.z:0}): {(job.wanted == null ? "reading" : "writing")} its data failed, leaving it alone: {job.error}");
				table.known = true;
				table.revision = zdo.DataRevision;
				table.nextWrite = now + 600f;
				return;
			}
			if (job.wanted == null)
			{
				if (job.exploredLength > 0 && s_learnedTextureSize == 0)
				{
					int size = Mathf.RoundToInt(Mathf.Sqrt(job.exploredLength));
					if (size * size == job.exploredLength)
					{
						s_learnedTextureSize = size;
						if (Minimap.instance && Minimap.instance.m_textureSize != size)
						{
							QoLPlugin.Log.LogWarning($"Pins: the map tables hold a {size}x{size} map, the game says {Minimap.instance.m_textureSize}; using {size}");
						}
					}
				}
				// A player wrote the table (or it is the first look): what we had put in and is gone now, they deleted.
				List<string> missing = null;
				foreach (KeyValuePair<string, float> entry in table.ours)
				{
					if (!job.ours.Contains(entry.Key) && now - entry.Value > DeleteGraceSeconds)
					{
						(missing ?? (missing = new List<string>())).Add(entry.Key);
					}
				}
				if (missing != null)
				{
					foreach (string key in missing)
					{
						Pin pin = s_pins.Find(p => p.Key == key);
						if (pin != null)
						{
							s_removed.Add(pin);
							Drop(pin, $"removed by a player at the map table at ({table.pos.x:0}, {table.pos.z:0})");
						}
					}
				}
				Dictionary<string, float> before = new Dictionary<string, float>(table.ours);
				table.ours.Clear();
				foreach (string key in job.ours)
				{
					table.ours[key] = before.TryGetValue(key, out float at) ? at : now;
				}
				table.known = true;
				table.revision = job.baseRevision;
				return;
			}
			if (zdo.DataRevision != job.baseRevision)
			{
				// Somebody wrote it meanwhile: read again first.
				return;
			}
			zdo.Set(ZDOVars.s_data, job.output);
			table.revision = zdo.DataRevision;
			int added = 0, removed = 0;
			foreach (string key in job.ours)
			{
				if (!table.ours.ContainsKey(key)) added++;
			}
			foreach (string key in table.ours.Keys)
			{
				if (!job.ours.Contains(key)) removed++;
			}
			Dictionary<string, float> kept = new Dictionary<string, float>(table.ours);
			table.ours.Clear();
			foreach (string key in job.ours)
			{
				table.ours[key] = kept.TryGetValue(key, out float at) ? at : now;
			}
			table.nextWrite = now + Mathf.Max(5f, QoLPlugin.Settings.PinsWriteSeconds.Value);
			QoLPlugin.Log.LogInfo($"Pins: map table at ({table.pos.x:0}, {table.pos.z:0}) written: {job.ours.Count} pins of ours (+{added} -{removed}), {job.others} of the players', {job.output.Length} bytes");
		}

		// Pins whose object is gone: a mined-out deposit (or one hit for the first time, which becomes another
		// prefab in the same place), a broken portal.
		private static void CheckObjects()
		{
			s_dropped.Clear();
			foreach (Pin pin in s_pins)
			{
				if (pin.uid.IsNone() || ZDOMan.instance.GetZDO(pin.uid) != null)
				{
					continue;
				}
				if (pin.category == Category.Ores && Rebind(pin))
				{
					continue;
				}
				s_dropped.Add(pin);
			}
			foreach (Pin pin in s_dropped)
			{
				Drop(pin, "the object is gone");
			}
		}

		private static bool Rebind(Pin pin)
		{
			s_search.Clear();
			ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(pin.pos), new SimulationDistance(1, 0, classic: true), s_search);
			foreach (ZDO zdo in s_search)
			{
				if (s_oreNames.TryGetValue(zdo.GetPrefab(), out string label) && label == pin.name && (zdo.GetPosition() - pin.pos).sqrMagnitude <= 9f)
				{
					pin.uid = zdo.m_uid;
					pin.prefab = World.PrefabName(zdo);
					pin.prefabHash = zdo.GetPrefab();
					s_saveDue = true;
					return true;
				}
			}
			return false;
		}

		private static void DropDisabled()
		{
			s_dropped.Clear();
			foreach (Pin pin in s_pins)
			{
				if (!CategoryOn(pin.category))
				{
					s_dropped.Add(pin);
				}
			}
			foreach (Pin pin in s_dropped)
			{
				Drop(pin, "its kind is switched off");
			}
		}

		// ---- table data ----

		private static void Parse(byte[] compressed, out byte[] explored, out List<TablePin> pins)
		{
			byte[] raw = Utils.Decompress(compressed);
			using (BinaryReader reader = new BinaryReader(new MemoryStream(raw), Encoding.UTF8))
			{
				int version = reader.ReadInt32();
				int count = reader.ReadInt32();
				explored = reader.ReadBytes(count);
				pins = new List<TablePin>();
				if (version < 2)
				{
					return;
				}
				int pinCount = reader.ReadInt32();
				for (int i = 0; i < pinCount; i++)
				{
					TablePin pin = new TablePin { owner = reader.ReadInt64(), name = reader.ReadString() };
					pin.pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
					pin.type = reader.ReadInt32();
					pin.check = reader.ReadBoolean();
					pin.author = version >= 3 ? reader.ReadString() : "";
					pins.Add(pin);
				}
			}
		}

		private static byte[] Build(byte[] explored, List<TablePin> pins)
		{
			using (MemoryStream stream = new MemoryStream(explored.Length + 16 + pins.Count * 48))
			{
				using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
				{
					writer.Write(3);
					writer.Write(explored.Length);
					writer.Write(explored);
					writer.Write(pins.Count);
					foreach (TablePin pin in pins)
					{
						writer.Write(pin.owner);
						writer.Write(pin.name);
						writer.Write(pin.pos.x);
						writer.Write(pin.pos.y);
						writer.Write(pin.pos.z);
						writer.Write(pin.type);
						writer.Write(pin.check);
						writer.Write(pin.author ?? "");
					}
				}
				return Utils.Compress(stream.ToArray());
			}
		}

		// ---- the file: the pins, and the ones players removed, per world ----

		private static string FilePath()
		{
			string world = ZNet.World != null ? ZNet.World.m_name : "world";
			foreach (char c in Path.GetInvalidFileNameChars())
			{
				world = world.Replace(c, '_');
			}
			return Path.Combine(Paths.ConfigPath, $"sarkasticeu.qol.pins.{world}.txt");
		}

		private static void Load()
		{
			string path = FilePath();
			if (!File.Exists(path))
			{
				return;
			}
			CultureInfo ic = CultureInfo.InvariantCulture;
			foreach (string line in File.ReadAllLines(path))
			{
				if (line.StartsWith("sent "))
				{
					string[] sent = line.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
					if (sent.Length == 3)
					{
						SentTo(sent[1]).Add(sent[2]);
					}
					continue;
				}
				string[] f = line.Split(new[] { ' ' }, 10, StringSplitOptions.RemoveEmptyEntries);
				try
				{
					if (f.Length < 10 || (f[0] != "pin" && f[0] != "removed"))
					{
						continue;
					}
					Pin pin = new Pin
					{
						category = (Category)Enum.Parse(typeof(Category), f[1]),
						prefab = f[2],
						prefabHash = f[2].GetStableHashCode(),
						pos = new Vector3(float.Parse(f[3], ic), float.Parse(f[4], ic), float.Parse(f[5], ic)),
						count = int.Parse(f[6], ic),
						type = (Minimap.PinType)int.Parse(f[7], ic),
						name = f[9],
					};
					string[] id = f[8].Split(':');
					pin.uid = new ZDOID(long.Parse(id[0], ic), uint.Parse(id[1], ic));
					(f[0] == "pin" ? s_pins : s_removed).Add(pin);
				}
				catch (Exception e)
				{
					QoLPlugin.Log.LogWarning($"Pins: skipping a line of {Path.GetFileName(path)}: {e.Message}");
				}
			}
			s_pinsVersion++;
		}

		private static void Save()
		{
			s_saveDue = false;
			CultureInfo ic = CultureInfo.InvariantCulture;
			List<string> lines = new List<string> { "# Sarkastic.gg QoL map pins: pin|removed <category> <prefab> <x> <y> <z> <count> <icon> <object id> <name>; sent <player> <pin key>" };
			foreach (Pin pin in s_pins)
			{
				lines.Add(Line("pin", pin, ic));
			}
			foreach (Pin pin in s_removed)
			{
				lines.Add(Line("removed", pin, ic));
			}
			foreach (KeyValuePair<string, HashSet<string>> player in s_sent)
			{
				foreach (string key in player.Value)
				{
					lines.Add($"sent {player.Key} {key}");
				}
			}
			try
			{
				File.WriteAllLines(FilePath(), lines);
			}
			catch (Exception e)
			{
				QoLPlugin.Log.LogWarning($"Pins: could not write the pins file: {e.Message}");
			}
		}

		private static string Line(string what, Pin pin, CultureInfo ic)
		{
			return $"{what} {pin.category} {pin.prefab} {pin.pos.x.ToString("R", ic)} {pin.pos.y.ToString("R", ic)} {pin.pos.z.ToString("R", ic)} {pin.count} {(int)pin.type} {pin.uid.UserID.ToString(ic)}:{pin.uid.ID.ToString(ic)} {pin.name}";
		}

		// ---- the admin's side ----

		public static string Status()
		{
			Settings s = QoLPlugin.Settings;
			int[] counts = new int[4];
			foreach (Pin pin in s_pins)
			{
				counts[(int)pin.category]++;
			}
			int withJobs = 0;
			foreach (Table table in s_tables.Values)
			{
				if (table.job != null) withJobs++;
			}
			string unknown = Unknown();
			return $"pins: {(Enabled ? "on" : "off")}; pickables {(s.PinsPickables.Value ? counts[0].ToString() : "off")}, ores {(s.PinsOres.Value ? counts[1].ToString() : "off")}, dungeons {(s.PinsDungeons.Value ? counts[2].ToString() : "off")}, portals {(s.PinsPortals.Value ? counts[3].ToString() : "off")}"
				+ $"; on {s_sent.Count} players' maps; tables {(s.PinsSharedTables.Value ? $"shared, {s_removed.Count} removed by players" : "not written")}, {s_tables.Count} of them ({withJobs} being written); map texture {TextureSize()}"
				+ (unknown.Length > 0 ? $"; unknown names in the config: {unknown}" : "")
				+ "\nqol pins list [pickables|ores|dungeons|portals] | forget (let pins removed from tables come back) | clear (drop every pin; players keep what is on their maps)";
		}

		public static string List(string category)
		{
			StringBuilder text = new StringBuilder();
			int shown = 0;
			foreach (Pin pin in s_pins)
			{
				if (category != null && !pin.category.ToString().Equals(category, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				if (shown++ < 60)
				{
					text.Append($"\n  {pin.category}: {pin.name} at ({pin.pos.x:0}, {pin.pos.z:0})");
				}
			}
			return shown == 0 ? "no pins" : $"{shown} pins{(shown > 60 ? ", the first 60" : "")}:{text}";
		}

		public static string Forget()
		{
			int n = s_removed.Count;
			s_removed.Clear();
			s_saveDue = true;
			return $"{n} removed pins forgotten; they come back when a player is near them again";
		}

		public static string Clear()
		{
			int n = s_pins.Count;
			s_pins.Clear();
			s_removed.Clear();
			s_pinsVersion++;
			s_saveDue = true;
			foreach (Table table in s_tables.Values)
			{
				table.nextWrite = 0f;
			}
			return $"{n} pins dropped; the map tables are rewritten without them in a moment";
		}
	}
}
