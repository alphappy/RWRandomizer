﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RainWorldRandomizer.Generation
{
    public class State(SlugcatStats.Name slugcat, SlugcatStats.Timeline timeline, int startKarma)
    {
        /// <summary>
        /// Every location in the state. Does not change after initialization.
        /// </summary>
        public HashSet<Location> AllLocations { get; private set; }
        /// <summary>
        /// All locations with unsatisfied requirements. Must be empty at end of generation.
        /// </summary>
        public HashSet<Location> UnreachedLocations { get; private set; }
        /// <summary>
        /// All locations that are currently in logic and have not been placed
        /// </summary>
        public HashSet<Location> AvailableLocations { get; private set; }

        public SlugcatStats.Name Slugcat { get; private set; } = slugcat;
        public SlugcatStats.Timeline Timeline { get; private set; } = timeline;
        public int MaxKarma { get; private set; } = startKarma;
        public HashSet<string> SpecialProg { get; private set; } = [];
        public HashSet<string> Regions { get; private set; } = [];
        public HashSet<string> Gates { get; private set; } = [];
        public HashSet<CreatureTemplate.Type> Creatures { get; private set; } = [];
        public HashSet<AbstractPhysicalObject.AbstractObjectType> Objects { get; private set; } = [];

        public void DefineLocs(HashSet<Location> allLocs)
        {
            AllLocations = [];

            foreach (Location loc in allLocs)
            {
                // If a location with the same name already exists, combine their AccessRules
                // and consider them the same location. This effectively means when the same
                // location is collectible in multiple places, it will be deemed accessible
                // once either condition is met.
                if (AllLocations.Contains(loc, new LocationIDComparer()))
                {
                    Location mergedLoc = AllLocations.First(l => l.id == loc.id);
                    mergedLoc.accessRule = new CompoundAccessRule(
                        [ mergedLoc.accessRule, loc.accessRule], 
                        CompoundAccessRule.CompoundOperation.Any);
                }
                else
                {
                    AllLocations.Add(loc);
                }
            }

            UnreachedLocations = [.. AllLocations];
            AvailableLocations = [];
        }

        /// <summary>
        /// Directly add a region to this state
        /// </summary>
        /// <param name="regionShort"></param>
        public void AddRegion(string regionShort)
        {
            Regions.Add(regionShort);
            RecalculateState();
        }

        /// <summary>
        /// Mark a gate as accessible to this state
        /// </summary>
        public void AddGate(string gateName)
        {
            Gates.Add(gateName);
            UpdateGate(gateName);
            RecalculateState();
        }

        /// <summary>
        /// Add a special progression item to this state
        /// </summary>
        public void AddOtherProgItem(string itemName)
        {
            if (itemName.Equals("Karma")) MaxKarma++;
            else SpecialProg.Add(itemName);
            RecalculateState();
        }

        public Location PopRandomLocation(ref Random random)
        {
            if (AvailableLocations.Count == 0) return null;

            Location chosen = AvailableLocations.ElementAt(random.Next(AvailableLocations.Count));
            AvailableLocations.Remove(chosen);
            return chosen;
        }

        /// <summary>
        /// Recompute the current State by searching unreached locations for newly reachable ones
        /// </summary>
        private void RecalculateState()
        {
            // Loop through and update every gate in state
            bool madeProgress;
            do
            {
                madeProgress = false;
                foreach (string gate in Gates)
                {
                    madeProgress |= UpdateGate(gate);
                }
            }
            // If the last loop added a region, it must be searched again
            while (madeProgress);

            // Add any new region's placed objects
            foreach (string region in Regions)
            {
                string regionLower = region.ToLowerInvariant();
                for (int i = 0; i < TokenCachePatcher.regionObjects[regionLower].Count; i++)
                {
                    if (TokenCachePatcher.regionObjectsAccessibility[regionLower][i].Contains(Slugcat))
                    {
                        Objects.Add(TokenCachePatcher.regionObjects[regionLower][i]);
                    }
                }
                for (int j = 0; j < TokenCachePatcher.regionCreatures[regionLower].Count; j++)
                {
                    if (TokenCachePatcher.regionCreaturesAccessibility[regionLower][j].Contains(Slugcat))
                    {
                        Creatures.Add(TokenCachePatcher.regionCreatures[regionLower][j]);
                    }
                }
            }

            List<Location> newLocs = [.. UnreachedLocations.Where(r => r.CanReach(this))];
            UnreachedLocations.ExceptWith(newLocs);
            AvailableLocations.UnionWith(newLocs);
        }

        /// <summary>
        /// Checks if a gate opens up more regions, and does so if true.
        /// </summary>
        /// <param name="gateName">GATE_XX_YY</param>
        /// <returns>Whether a new region was added from this gate</returns>
        private bool UpdateGate(string gateName)
        {
            string[] gate = Regex.Split(gateName, "_");
            string regLeft = Plugin.ProperRegionMap[gate[1]];
            string regRight = Plugin.ProperRegionMap[gate[2]];

            // One way gate logic
            if (Constants.OneWayGates.ContainsKey(gateName))
            {
                // If the gate only travels right, check for left region access
                if (Regions.Contains(regLeft)
                    && !Constants.OneWayGates[gateName]
                    && !Regions.Contains(regRight))
                {
                    Regions.Add(regRight);
                    return true;
                }
                // If the gate only travels left, check for right region access
                if (Regions.Contains(regRight)
                    && Constants.OneWayGates[gateName]
                    && !Regions.Contains(regLeft))
                {
                    Regions.Add(regLeft);
                    return true;
                }
                return false;
            }

            if (Regions.Contains(regLeft) ^ Regions.Contains(regRight))
            {
                Regions.Add(regLeft);
                Regions.Add(regRight);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Custom <see cref="IEqualityComparer{T}"/> allowing <see cref="Location"/>s to be considered equal if they have the same ID
    /// </summary>
    public class LocationIDComparer : IEqualityComparer<Location>
    {
        public bool Equals(Location x, Location y)
        {
            return x.id == y.id;
        }

        public int GetHashCode(Location obj)
        {
            return obj.id.GetHashCode();
        }
    }
}
