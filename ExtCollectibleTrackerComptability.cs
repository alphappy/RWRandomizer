﻿using MonoMod.RuntimeDetour;
using ExtendedCollectiblesTracker;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Permissions;

namespace RainWorldRandomizer
{
    public static class ExtCollectibleTrackerComptability
    {
        private static bool? _enabled;

        public static bool Enabled
        {
            get
            {
                if (_enabled == null)
                {
                    _enabled = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("franklygd.extendedcollectiblestracker");
                }
                return (bool)_enabled;
            }
        }

        public static void ApplyHooks()
        {
            _ = new Hook(typeof(ExtendedCollectiblesTracker.Plugin).Assembly.GetType("ExtendedCollectiblesTracker.Mod", true).GetMethod("IsPearlRead"), 
                typeof(ExtCollectibleTrackerComptability).GetMethod(nameof(OnIsPearlRead)));
        }

        public static bool OnIsPearlRead(Func<RainWorld, DataPearl.AbstractDataPearl.DataPearlType, bool> orig, RainWorld rainWorld, DataPearl.AbstractDataPearl.DataPearlType pearlType)
        {
            bool origResult = orig(rainWorld, pearlType);
            string keyString = $"Pearl-{pearlType}";

            if (Generation.randomizerKey.ContainsKey(keyString))
            {
                return Generation.randomizerKey[keyString]?.IsGiven ?? false;
            }

            return origResult;
        }
    }
}
