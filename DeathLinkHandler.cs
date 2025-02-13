﻿using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace RainWorldRandomizer
{
    public static class DeathLinkHandler
    {
        private static DeathLinkService service = null;
        
        /// <summary>Cooldown to ensure we don't send a packet for a received death</summary>
        private static int receiveDeathCooldown = 0;
        /// <summary>When True, the mod is waiting for a proper state to kill the player</summary>
        private static bool deathPending = false;
        private static bool lastDeathWasMe = false;

        public static bool Active
        {
            get
            {
                return service != null && ArchipelagoConnection.Session.ConnectionInfo.Tags.Contains("DeathLink");
            }
            set
            {
                if (value) service?.EnableDeathLink();
                else service?.DisableDeathLink();
            }
        }

        public static void ApplyHooks()
        {
            On.Player.Die += OnPlayerDie;
            On.RainWorldGame.Update += OnRainWorldGameUpdate;
        }

        public static void RemoveHooks()
        {
            On.Player.Die -= OnPlayerDie;
            On.RainWorldGame.Update -= OnRainWorldGameUpdate;
        }

        public static void Init(ArchipelagoSession session)
        {
            service = session.CreateDeathLinkService();
            service.OnDeathLinkReceived += OnReceiveDeath;
        }

        private static void OnReceiveDeath(DeathLink deathLink)
        {
            Plugin.Log.LogInfo($"Received DeathLink packet from {deathLink.Source}");

            if (Plugin.archipelagoIgnoreMenuDL.Value // Ignore menu DeathLinks if setting
                && Plugin.Singleton.rainWorld.processManager.currentMainLoop is RainWorldGame)
            {
                receiveDeathCooldown = 40; // 1 second
                deathPending = true;
            }
            else
            {
                Plugin.Log.LogInfo($"Ignoring DeathLink as main process is {Plugin.Singleton.rainWorld.processManager.currentMainLoop.GetType().Name}");
            }
        }

        private static void OnPlayerDie(On.Player.orig_Die orig, Player self)
        {
            orig(self);
            if (!Active || receiveDeathCooldown > 0 || self.AI != null) return;

            Plugin.Log.LogInfo("Sending DeathLink packet...");
            service.SendDeathLink(new DeathLink(ArchipelagoConnection.playerName));
        }

        private static void OnRainWorldGameUpdate(On.RainWorldGame.orig_Update orig, RainWorldGame self)
        {
            orig(self);
            if (self.GamePaused || !self.processActive) return;

            // TODO: depending on poll results, logic may have to move to a global update to ignore on sleep screen
            if (deathPending 
                && self.FirstAlivePlayer?.realizedCreature?.room != null // Player exists
                && self.manager.fadeToBlack == 0 // The screen has fully faded in
                && (self.FirstAlivePlayer.realizedCreature as Player).controller == null) // There are no external forces controlling us
            {
                deathPending = false;
                lastDeathWasMe = true;
                foreach (AbstractCreature abstractPlayer in self.AlivePlayers)
                {
                    // Make sure player is realized
                    if (abstractPlayer.realizedCreature is Player player)
                    {
                        Plugin.Log.LogInfo("Deathlink Killing Player...");
                        // This is the same effect played when Pebbles kills the player
                        player.mainBodyChunk.vel += RWCustom.Custom.RNV() * 12f;
                        for (int k = 0; k < 20; k++)
                        {
                            player.room.AddObject(new Spark(player.mainBodyChunk.pos, RWCustom.Custom.RNV() * UnityEngine.Random.value * 40f, new Color(1f, 1f, 1f), null, 30, 120));
                        }
                        player.Die();
                    }
                }
            }

            // Cooldown Counter
            if (receiveDeathCooldown > 0) receiveDeathCooldown--;
            else if (deathPending) receiveDeathCooldown = 40;
        }
    }
}
