using System;
using System.Collections.Generic;
using SarkasticQoL.Features;
using UnityEngine;

namespace SarkasticQoL
{
	/*
		The scan: every ScanSeconds, for one player per frame, the objects in the zones around
		them (3x3, the zones a client has loaded) are handed to the features that care about
		their kind. A few thousand objects at most per player; the features do nothing unless
		something needs changing. Also finds the piece a player looks at for the chat commands,
		and whether a ward lets them change it.
	*/
	internal static class World
	{
		internal static readonly PlayerState Players = new PlayerState();
		internal static readonly List<IFeature> Features = new List<IFeature>();

		private static readonly List<ZDO> s_near = new List<ZDO>();
		// Objects replaced during the current scan (created afresh under a new id): the old ones are still in the list.
		private static readonly HashSet<ZDOID> s_retired = new HashSet<ZDOID>();

		public static void Retire(ZDO zdo)
		{
			s_retired.Add(zdo.m_uid);
		}

		public static bool IsRetired(ZDO zdo)
		{
			return s_retired.Contains(zdo.m_uid);
		}

		// The objects around the player being scanned, for features that look at neighbours (containers near a smelter).
		public static IReadOnlyList<ZDO> Near => s_near;
		private static readonly SimulationDistance s_zonesAround = new SimulationDistance(1, 0, classic: true);
		private static readonly Dictionary<int, Kind> s_kinds = new Dictionary<int, Kind>();
		private static float s_timer;
		private static int s_next;
		private static readonly List<ZNetPeer> s_peers = new List<ZNetPeer>();

		[Flags]
		public enum Kind
		{
			None = 0,
			Door = 1,
			Turret = 2,
			Container = 4,
			Tameable = 8,
			Egg = 16,
			Growup = 32,
			Fireplace = 64,
			Smelter = 128,
			Sign = 256,
			Piece = 512,
			ShieldGenerator = 1024,
			Location = 2048,
			Portal = 4096,
			MapTable = 8192,
			PinObject = 16384, // a prefab named in the pins' lists (berries, ore deposits)
			Ward = 32768,
		}

		// Adds a kind to a prefab's, for the objects a feature picks by name (the pins' lists) rather than by component.
		public static void Flag(int prefabHash, Kind kind)
		{
			s_kinds[prefabHash] = (s_kinds.TryGetValue(prefabHash, out Kind current) ? current : Kind.None) | kind;
		}

		public static void Start()
		{
			Players.Load();
			QoLPlugin.Settings.BindContainerSizes();
			s_kinds.Clear();
			foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
			{
				if (!prefab)
				{
					continue;
				}
				Kind kind = Kind.None;
				if (prefab.GetComponent<Door>()) kind |= Kind.Door;
				if (prefab.GetComponent<Turret>()) kind |= Kind.Turret;
				if (prefab.GetComponent<Container>()) kind |= Kind.Container;
				if (prefab.GetComponent<Tameable>()) kind |= Kind.Tameable;
				if (prefab.GetComponent<EggGrow>()) kind |= Kind.Egg;
				if (prefab.GetComponent<Growup>()) kind |= Kind.Growup;
				if (prefab.GetComponent<Fireplace>()) kind |= Kind.Fireplace;
				if (prefab.GetComponent<Smelter>()) kind |= Kind.Smelter;
				if (prefab.GetComponent<Sign>()) kind |= Kind.Sign;
				if (prefab.GetComponent<Piece>()) kind |= Kind.Piece;
				if (prefab.GetComponent<ShieldGenerator>()) kind |= Kind.ShieldGenerator;
				if (prefab.GetComponent<LocationProxy>()) kind |= Kind.Location;
				if (prefab.GetComponent<TeleportWorld>()) kind |= Kind.Portal;
				if (prefab.GetComponent<MapTable>()) kind |= Kind.MapTable;
				if (prefab.GetComponent<PrivateArea>()) kind |= Kind.Ward;
				if (kind != Kind.None)
				{
					s_kinds[prefab.name.GetStableHashCode()] = kind;
				}
			}
			Features.Clear();
			Features.Add(new AutoDoors());
			Features.Add(new Ballistas());
			Features.Add(new TameProgress());
			Features.Add(new ContainerSizes());
			Features.Add(new PrefabFields());
			Features.Add(new Feeding());
			Features.Add(new Labels());
			Features.Add(new Clocks());
			Features.Add(new Sorting());
			Features.Add(new Pins());
			foreach (IFeature feature in Features)
			{
				feature.Start();
			}
			SleepVote.Reset();
			Motd.Reset();
			TameDeaths.Load();
			s_timer = 0f;
		}

		public static void Stop()
		{
			foreach (IFeature feature in Features)
			{
				feature.Stop();
			}
			Features.Clear();
		}

		public static Kind KindOf(ZDO zdo)
		{
			return s_kinds.TryGetValue(zdo.GetPrefab(), out Kind kind) ? kind : Kind.None;
		}

		public static void Tick(float dt)
		{
			Motd.Tick();
			try
			{
				Pins.Tick(dt);
			}
			catch (Exception e)
			{
				QoLPlugin.Log.LogWarning($"Pins failed: {e}");
			}
			s_timer -= dt;
			if (s_timer > 0f)
			{
				return;
			}
			// One player per frame; a full round takes as many frames as there are players.
			if (s_next == 0)
			{
				s_peers.Clear();
				foreach (ZNetPeer peer in ZNet.instance.GetPeers())
				{
					if (peer.IsReady())
					{
						s_peers.Add(peer);
					}
				}
			}
			if (s_next < s_peers.Count)
			{
				try
				{
					Scan(s_peers[s_next]);
				}
				catch (Exception e)
				{
					QoLPlugin.Log.LogWarning($"Scan around {s_peers[s_next].m_playerName} failed: {e}");
				}
				s_next++;
			}
			if (s_next >= s_peers.Count)
			{
				s_next = 0;
				s_timer = Mathf.Max(0.5f, QoLPlugin.Settings.ScanSeconds.Value);
			}
		}

		private static void Scan(ZNetPeer peer)
		{
			Vector3 at = Position(peer);
			s_near.Clear();
			s_retired.Clear();
			ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(at), s_zonesAround, s_near);
			foreach (IFeature feature in Features)
			{
				(feature as IScanAware)?.BeginScan(peer);
			}
			foreach (ZDO zdo in s_near)
			{
				Kind kind = KindOf(zdo);
				if (kind == Kind.None || s_retired.Contains(zdo.m_uid))
				{
					continue;
				}
				foreach (IFeature feature in Features)
				{
					// A feature may have replaced the object (created it afresh under a new id) a moment ago.
					if ((feature.Kinds & kind) != 0 && !s_retired.Contains(zdo.m_uid))
					{
						feature.Visit(zdo, kind, s_peers);
					}
				}
			}
			foreach (IFeature feature in Features)
			{
				(feature as IScanAware)?.EndScan(peer);
			}
		}

		public static Vector3 Position(ZNetPeer peer)
		{
			ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
			return character != null ? character.GetPosition() : peer.GetRefPos();
		}

		public static bool AnyPlayerWithin(List<ZNetPeer> peers, Vector3 position, float distance)
		{
			float sq = distance * distance;
			foreach (ZNetPeer peer in peers)
			{
				if ((Position(peer) - position).sqrMagnitude <= sq)
				{
					return true;
				}
			}
			return false;
		}

		private static readonly RaycastHit[] s_hits = new RaycastHit[32];
		private static int s_lookMask;
		private static float s_reach;

		// How far a player reaches with the use key: the Player prefab's m_maxInteractDistance (3.5 m in 1.0).
		public static float Reach
		{
			get
			{
				if (s_reach <= 0f)
				{
					GameObject prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab("Player") : null;
					Player player = prefab ? prefab.GetComponent<Player>() : null;
					s_reach = player ? player.m_maxInteractDistance : 3.5f;
				}
				return s_reach;
			}
		}

		/*
			The object of that kind the player is looking at, for the chat commands: what their
			crosshair is on. A client writes where its player looks (a point 100 m ahead of the
			eyes, s_lookTarget) into the player's data five times a second, so that the other
			clients can turn the head. The first thing a ray from the eyes towards that point meets,
			if it is within Reach of the eyes, is the answer -- the game's own hover check
			(Player.FindHoverObject), with the ray from the eyes instead of the camera behind the
			player. Chat typing freezes the camera, so the look stays on the object while the player
			types. Null: nothing, or something else.
		*/
		public static ZDO Looked(ZNetPeer peer, Kind kind)
		{
			ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
			Vector3 target = character != null ? character.GetVec3(ZDOVars.s_lookTarget, Vector3.zero) : Vector3.zero;
			if (target == Vector3.zero)
			{
				return null;
			}
			GameObject body = ZNetScene.instance.FindInstance(peer.m_characterID);
			Player player = body ? body.GetComponent<Player>() : null;
			Vector3 eye = player && player.m_eye ? player.m_eye.position : character.GetPosition() + Vector3.up * 1.6f;
			Vector3 direction = target - eye;
			if (direction.sqrMagnitude < 1f)
			{
				return null;
			}
			if (s_lookMask == 0)
			{
				// Player.m_interactMask
				s_lookMask = LayerMask.GetMask("item", "piece", "piece_nonsolid", "Default", "static_solid", "Default_small", "character", "character_net", "terrain", "vehicle", "character_ghost");
			}
			int count = Physics.RaycastNonAlloc(eye, direction.normalized, s_hits, Reach + 2f, s_lookMask);
			Array.Sort(s_hits, 0, count, ByDistance.Instance);
			for (int i = 0; i < count; i++)
			{
				Collider collider = s_hits[i].collider;
				if (body && collider.attachedRigidbody && collider.attachedRigidbody.gameObject == body)
				{
					continue;
				}
				Hoverable hoverable = collider.GetComponentInParent<Hoverable>();
				if (s_hits[i].distance >= Reach + (hoverable != null ? hoverable.GetHoverOffset() : 0f))
				{
					return null;
				}
				ZNetView view = collider.GetComponentInParent<ZNetView>();
				ZDO zdo = view && view.IsValid() ? view.GetZDO() : null;
				return zdo != null && (KindOf(zdo) & kind) != 0 ? zdo : null;
			}
			return null;
		}

		private class ByDistance : IComparer<RaycastHit>
		{
			public static readonly ByDistance Instance = new ByDistance();

			public int Compare(RaycastHit x, RaycastHit y)
			{
				return x.distance.CompareTo(y.distance);
			}
		}

		public static long PlayerId(ZNetPeer peer)
		{
			ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
			return character != null ? character.GetLong(ZDOVars.s_playerID, 0L) : 0L;
		}

		/*
			Whether a player may change a piece with a chat command: anyone where no ward stands;
			under an active ward, whoever the game lets open a chest there -- the ward's owner and
			the players they added, one ward that lets them in is enough (PrivateArea.CheckAccess).
			Read from the wards' data, so it holds whether or not the ward is loaded.
		*/
		public static bool MayChange(ZNetPeer peer, ZDO piece)
		{
			long player = PlayerId(peer);
			Vector3 at = piece.GetPosition();
			List<ZDO> list = new List<ZDO>();
			ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(at), s_zonesAround, list);
			bool warded = false;
			foreach (ZDO zdo in list)
			{
				if ((KindOf(zdo) & Kind.Ward) == 0 || !zdo.GetBool(ZDOVars.s_enabled))
				{
					continue;
				}
				PrivateArea ward = Fields.PrefabComponent<PrivateArea>(zdo.GetPrefab());
				if (!ward || Utils.DistanceXZ(zdo.GetPosition(), at) >= ward.m_radius)
				{
					continue;
				}
				if (player != 0L && (zdo.GetLong(ZDOVars.s_creator) == player || Permitted(zdo, player)))
				{
					return true;
				}
				warded = true;
			}
			return !warded;
		}

		// PrivateArea.GetPermittedPlayers
		private static bool Permitted(ZDO ward, long player)
		{
			int count = ward.GetInt(ZDOVars.s_permitted);
			for (int i = 0; i < count; i++)
			{
				if (ward.GetLong("pu_id" + i, 0L) == player)
				{
					return true;
				}
			}
			return false;
		}

		public static string PrefabName(ZDO zdo)
		{
			GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
			return prefab ? prefab.name : zdo.GetPrefab().ToString();
		}

		// For the log: the prefab name (the piece's display name is a localisation key, and the server has no translations).
		public static string PieceName(ZDO zdo)
		{
			return PrefabName(zdo);
		}

		// "CopperOre" -> "Copper ore", "piece_chest_wood" -> "chest wood": readable without the game's translations.
		public static string PrettyName(string prefab)
		{
			if (string.IsNullOrEmpty(prefab))
			{
				return "";
			}
			string name = prefab.StartsWith("piece_") ? prefab.Substring(6) : prefab;
			name = name.Replace('_', ' ');
			System.Text.StringBuilder text = new System.Text.StringBuilder(name.Length + 4);
			for (int i = 0; i < name.Length; i++)
			{
				char c = name[i];
				if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]) && name[i - 1] != ' ')
				{
					text.Append(' ');
					text.Append(char.ToLowerInvariant(c));
				}
				else
				{
					text.Append(c);
				}
			}
			return text.ToString();
		}
	}

	internal interface IFeature
	{
		World.Kind Kinds { get; }
		void Start();
		void Stop();
		void Visit(ZDO zdo, World.Kind kind, List<ZNetPeer> peers);
	}

	// A feature that looks at the objects around a player as a whole (clusters of bushes), not one by one.
	internal interface IScanAware
	{
		void BeginScan(ZNetPeer peer);
		void EndScan(ZNetPeer peer);
	}
}
