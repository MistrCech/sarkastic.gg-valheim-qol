using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using SarkasticQoL.Features;
using UnityEngine;

namespace SarkasticQoL
{
	/*
		Server-side only: runs on a dedicated server and needs nothing on clients. Talks to
		vanilla clients through what they already understand: the world objects' data, per-object
		field overrides (ZNetView.LoadFields) and the game's own messages (ShowMessage,
		RPC_DamageText). Depends on BepInEx (with its Harmony) and nothing else.

		Players control it from the chat with !commands (a vanilla client runs anything starting
		with / locally and never sends it); the admin from the server console with `qol ...`.
	*/
	[BepInPlugin(GUID, PluginName, PluginVersion)]
	public class QoLPlugin : BaseUnityPlugin
	{
		public const string GUID = "sarkasticeu.qol";
		public const string PluginName = "Sarkastic.gg QoL";
		public const string PluginVersion = "0.5.0";

		internal static ManualLogSource Log;
		internal static QoLPlugin Instance;
		internal static Settings Settings;

		private Harmony harmony;
		private bool started;

		private void Awake()
		{
			Instance = this;
			Log = Logger;
			Settings = new Settings(Config);
			if (!new ZNet().IsDedicated())
			{
				Log.LogInfo($"{PluginName} is off: not a dedicated server.");
				return;
			}
			harmony = new Harmony(GUID);
			harmony.PatchAll(typeof(QoLPlugin).Assembly);
			Log.LogInfo($"{PluginName} {PluginVersion} loaded; waiting for the world.");
		}

		private void Update()
		{
			if (harmony == null)
			{
				return;
			}
			bool ready = Settings.Enabled.Value && WorldReady();
			if (!started && ready)
			{
				started = true;
				World.Start();
				AdminConsole.Register();
				Log.LogInfo($"{PluginName} running. Chat commands start with '{Settings.ChatPrefix.Value}', console: qol help");
			}
			else if (started && !ready)
			{
				started = false;
				World.Stop();
			}
			if (started)
			{
				World.Tick(Time.unscaledDeltaTime);
			}
		}

		private void OnDestroy()
		{
			if (started)
			{
				World.Stop();
			}
		}

		internal static bool WorldReady()
		{
			return ZNet.instance && ZNet.instance.enabled && ZNet.instance.IsDedicated() && ZNet.instance.IsServer() && ZNet.World != null
				&& ZDOMan.instance != null && ZNetScene.instance && ObjectDB.instance && ZRoutedRpc.instance != null
				&& ZoneSystem.instance && ZoneSystem.instance.LocationsGenerated;
		}
	}
}
