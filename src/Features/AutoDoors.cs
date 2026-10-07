using System;
using System.Collections.Generic;

namespace SarkasticQoL.Features
{
	/*
		A player-built door left open closes once nobody has been within PlayerDistance for
		CloseAfterSeconds. The door's state lives in its data (0 closed, 1/-1 open); every client
		animates whatever the data says, so setting it back to 0 closes the door for everyone.
		Doors that need a key or that the game marks as never closing are left alone. Only a door
		a player switched on with !door auto on (stored in the door itself) closes, unless the
		config's default is on.
	*/
	internal class AutoDoors : IFeature
	{
		public static readonly int AutoKey = "SarkasticQoL.AutoDoor".GetStableHashCode();

		private readonly Dictionary<ZDOID, DateTime> clearSince = new Dictionary<ZDOID, DateTime>();

		public World.Kind Kinds => World.Kind.Door;

		public void Start() { }
		public void Stop() { clearSince.Clear(); }

		public static bool Wanted(ZDO door)
		{
			return door.GetBool(AutoKey, QoLPlugin.Settings.DoorsDefault.Value);
		}

		public void Visit(ZDO zdo, World.Kind kind, List<ZNetPeer> peers)
		{
			if (zdo.GetInt(ZDOVars.s_state) == 0)
			{
				clearSince.Remove(zdo.m_uid);
				return;
			}
			if (!QoLPlugin.Settings.DoorsEnabled.Value || zdo.GetLong(ZDOVars.s_creator) == 0L || !Wanted(zdo))
			{
				return;
			}
			Door door = Fields.PrefabComponent<Door>(zdo.GetPrefab());
			if (!door || door.m_keyItem != null || door.m_canNotBeClosed)
			{
				return;
			}
			Settings s = QoLPlugin.Settings;
			if (World.AnyPlayerWithin(peers, zdo.GetPosition(), s.DoorsPlayerDistance.Value))
			{
				clearSince.Remove(zdo.m_uid);
				return;
			}
			DateTime now = DateTime.UtcNow;
			if (!clearSince.TryGetValue(zdo.m_uid, out DateTime since))
			{
				clearSince[zdo.m_uid] = now;
				return;
			}
			if ((now - since).TotalSeconds >= s.DoorsCloseAfterSeconds.Value)
			{
				zdo.Set(ZDOVars.s_state, 0);
				clearSince.Remove(zdo.m_uid);
			}
		}
	}
}
