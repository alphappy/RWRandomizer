using MonoMod.Utils;
using MoreSlugcats;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Random = System.Random;

namespace RainWorldRandomizer.Generation
{
    public class VanillaGenerator
    {
        public const float OTHER_PROG_PLACEMENT_CHANCE = 0.6f;

        /// <summary>
        /// Used to override rules for locations. To modify the rules of a location, add its location ID to this dict with the new rule it should follow.
        /// </summary>
        public static Dictionary<string, AccessRule> GlobalRuleOverrides = [];
        /// <summary>
        /// Used to override rules for locations as specific slugcats. Applies on top of <see cref="GlobalRuleOverrides"/>, taking priority over all if playing as the relevant slugcat
        /// </summary>
        public static Dictionary<SlugcatStats.Name, Dictionary<string, AccessRule>> SlugcatRuleOverrides = [];

        /// <summary>
        /// Combination of <see cref="GlobalRuleOverrides"/> and <see cref="SlugcatRuleOverrides"/> populated in instance constructor.
        /// Any additions to rule overrides must be completed by the time the generator instance is created
        /// </summary>
        private Dictionary<string, AccessRule> RuleOverrides = [];

        private SlugcatStats.Name slugcat;
        private SlugcatStats.Timeline timeline;

        public enum GenerationStep
        {
            NotStarted,
            InitializingState,
            BalancingItems,
            PlacingProg,
            PlacingFiller,
            Complete,
            FailedGen
        }
        public GenerationStep CurrentStage { get; private set; }
        public bool InProgress
        {
            get
            {
                return CurrentStage is > GenerationStep.NotStarted
                    and < GenerationStep.Complete;
            }
        }

        private Task generationThread;
        private Random randomState;

        private State state;
        private List<Item> ItemsToPlace = [];
        private HashSet<string> AllRegions = [];
        public HashSet<string> AllGates { get; private set; }
        public HashSet<string> UnplacedGates { get; private set; }
        public HashSet<string> AllPassages { get; private set; }
        public Dictionary<Location, Item> RandomizedGame { get; private set; }

        public StringBuilder generationLog = new();
        public string customStartDen = "";
        public int generationSeed;


        public VanillaGenerator(SlugcatStats.Name slugcat, SlugcatStats.Timeline timeline, int generationSeed = 0)
        {
            this.slugcat = slugcat;
            this.timeline = timeline;
            CurrentStage = GenerationStep.NotStarted;

            AllGates = [];
            UnplacedGates = [];
            AllPassages = [];
            RandomizedGame = [];

            // Initialize RNG
            // Using inferior System.Random because it's instanced rather than static.
            // UnityEngine.Random doesn't play well with threads
            this.generationSeed = generationSeed;
            randomState = new Random(generationSeed);

            // Combine custom rules together
            RuleOverrides = GlobalRuleOverrides;
            // Slugcat specific rules take priority over global ones
            foreach (var slugcatRule in SlugcatRuleOverrides[slugcat])
            {
                if (RuleOverrides.ContainsKey(slugcatRule.Key))
                {
                    RuleOverrides[slugcatRule.Key] = slugcatRule.Value;
                }
                else
                {
                    RuleOverrides.Add(slugcatRule.Key, slugcatRule.Value);
                }
            }
        }

        public Task BeginGeneration()
        {
            generationThread = new Task(Generate);
            generationThread.Start();
            return generationThread;
        }

        private void Generate()
        {
            generationLog.AppendLine("Begin Generation");
            Stopwatch sw = Stopwatch.StartNew();

            InitializeState();
            ApplyRuleOverrides();
            BalanceItems();
            PlaceProgression();
            PlaceFiller();
            generationLog.AppendLine("Generation complete!");
            generationLog.AppendLine($"Gen time: {sw.ElapsedMilliseconds} ms");
            CurrentStage = GenerationStep.Complete;
        }

        private void InitializeState()
        {
            generationLog.AppendLine("INITIALIZE STATE");
            CurrentStage = GenerationStep.InitializingState;
            state = new State(slugcat, timeline, RandoOptions.StartMinimumKarma ? 1 : 5);
            HashSet<Location> locations = [];

            // Load Tokens
            if (RandoOptions.UseSandboxTokenChecks)
            {
                lock (Plugin.Singleton.collectTokenHandler)
                {
                    if (Plugin.Singleton.collectTokenHandler.tokensLoadedFor != slugcat)
                    {
                        Plugin.Singleton.collectTokenHandler.LoadAvailableTokens(Plugin.Singleton.rainWorld, slugcat);
                    }
                }
            }

            // Regions loop
            bool regionKitEchoes = RandoOptions.UseEchoChecks && RegionKitCompatibility.Enabled;
            bool doPearlLocations = RandoOptions.UsePearlChecks && (ModManager.MSC || slugcat != SlugcatStats.Name.Yellow);
            bool spearBroadcasts = ModManager.MSC && slugcat == MoreSlugcatsEnums.SlugcatStatsName.Spear && RandoOptions.UseSMBroadcasts;
            List<string> slugcatRegions = [.. SlugcatStats.SlugcatStoryRegions(slugcat).Union(SlugcatStats.SlugcatOptionalRegions(slugcat))];
            foreach (string region in Region.GetFullRegionOrder(timeline))
            {
                AccessRule regionAccessRule = new RegionAccessRule(region);

                // Apply any overrides that should modify the rules of the entire region
                if (RuleOverrides.TryGetValue($"Region-{region}", out AccessRule newRules))
                {
                    if (!newRules.IsPossible(state))
                    {
                        generationLog.AppendLine($"Skip adding locations for impossible region: {region}");
                        continue;
                    }
                    regionAccessRule = new CompoundAccessRule(
                    [
                        regionAccessRule,
                        newRules
                    ], CompoundAccessRule.CompoundOperation.All);
                    generationLog.AppendLine($"Applied custom rules for region: {region}");
                }
                // Filter out slugcat inaccessible regions unless there is a special rule defined
                else if (!slugcatRegions.Contains(region)) continue;

                AllRegions.Add(region);
                string regionLower = region.ToLowerInvariant();

                // Add Echoes from RegionKit if present
                if (regionKitEchoes && RegionKitCompatibility.RegionHasEcho(region, slugcat))
                {
                    locations.Add(new Location($"Echo-{region}", Location.Type.Echo, regionAccessRule));
                }

                // Create Pearl locations
                if (doPearlLocations && Plugin.Singleton.rainWorld.regionDataPearls.ContainsKey(region.ToLowerInvariant()))
                {
                    for (int i = 0; i < Plugin.Singleton.rainWorld.regionDataPearls[regionLower].Count; i++)
                    {
                        if (Plugin.Singleton.rainWorld.regionDataPearlsAccessibility[regionLower][i].Contains(slugcat))
                        {
                            locations.Add(new Location($"Pearl-{Plugin.Singleton.rainWorld.regionDataPearls[regionLower][i].value}",
                                Location.Type.Pearl, regionAccessRule));
                        }
                    }
                }

                // Create Token locations
                if (RandoOptions.UseSandboxTokenChecks
                    && Plugin.Singleton.collectTokenHandler.availableTokens.ContainsKey(region))
                {
                    foreach (string token in Plugin.Singleton.collectTokenHandler.availableTokens[region])
                    {
                        locations.Add(new Location($"Token-{token}", Location.Type.Token, regionAccessRule));
                    }
                }

                // Create Broadcast locations
                if (spearBroadcasts && Plugin.Singleton.rainWorld.regionGreyTokens.ContainsKey(regionLower))
                {
                    foreach (ChatlogData.ChatlogID token in Plugin.Singleton.rainWorld.regionGreyTokens[regionLower])
                    {
                        locations.Add(new Location($"Broadcast-{token.value}", Location.Type.Token, regionAccessRule));
                    }
                }
            }

            // Create Gate items
            foreach (string karmaLock in Plugin.Singleton.rainWorld.progression.karmaLocks)
            {
                string gate = Regex.Split(karmaLock, " : ")[0];
                string[] split = Regex.Split(gate, "_");
                if (split.Length < 3) continue; // Ignore abnormal gates
                string[] regions = [split[1], split[2]];

                // Skip if gate already accounted for
                if (AllGates.Contains(gate)) continue;
                // Gates that have to always be open to avoid softlocks
                if (Constants.ForceOpenGates.Contains(gate)) continue;

                bool skipThisGate = false;
                foreach (string region in regions)
                {
                    // If this region does not exist in the timeline
                    // and is not an alias of an existing region, skip the gate
                    if (!AllRegions.Contains(region)
                        && (!Plugin.ProperRegionMap.TryGetValue(region, out string alias)
                        || region == alias))
                    {
                        skipThisGate = true;
                    }

                    // If this gate is impossible to reach for the current slugcat, skip it
                    if (TokenCachePatcher
                        .GetRoomAccessibility(region)
                        .TryGetValue(gate.ToLowerInvariant(), out List<SlugcatStats.Name> accessibleTo)
                        && !accessibleTo.Contains(slugcat))
                    {
                        skipThisGate = true;
                    }
                }

                if (skipThisGate) continue;

                AllGates.Add(gate);

                // TODO: Un-hardcode check for marking GATE_UW_SL as non-progression
                if (gate.Equals("GATE_UW_SL")
                    && SlugcatStats.AtOrAfterTimeline(timeline, SlugcatStats.Timeline.Sofanthiel))
                {
                    ItemsToPlace.Add(new Item(gate, Item.Type.Gate, Item.Importance.Filler));
                    continue;
                }

                ItemsToPlace.Add(new Item(gate, Item.Type.Gate, Item.Importance.Progression));
            }

            // Create Passage locations and items
            foreach (string passage in ExtEnumBase.GetNames(typeof(WinState.EndgameID)))
            {
                bool motherUnlocked = ModManager.MSC && (Plugin.Singleton.rainWorld.progression.miscProgressionData.beaten_Gourmand_Full || MoreSlugcats.MoreSlugcats.chtUnlockSlugpups.Value);
                bool canFindSlugpups = slugcat == SlugcatStats.Name.White || slugcat == SlugcatStats.Name.Red || (ModManager.MSC && slugcat == MoreSlugcatsEnums.SlugcatStatsName.Gourmand);

                // Filter out impossible passages
                if (ModManager.MSC)
                {
                    switch (passage)
                    {
                        // Gourmand is handled later
                        case "Gourmand":
                            continue;
                        case "Mother":
                            if (!motherUnlocked || !canFindSlugpups) continue;
                            break;
                        case "Chieftain":
                            if (slugcat == MoreSlugcatsEnums.SlugcatStatsName.Artificer) continue;
                            break;
                        case "Monk":
                        case "Saint":
                            if (slugcat == MoreSlugcatsEnums.SlugcatStatsName.Spear) continue;
                            break;
                        case "Hunter":
                        case "Outlaw":
                        case "DragonSlayer":
                        case "Scholar":
                            if (slugcat == MoreSlugcatsEnums.SlugcatStatsName.Saint
                                || slugcat == MoreSlugcatsEnums.SlugcatStatsName.Sofanthiel) continue;
                            break;
                    }
                }

                // Passage locations
                if (RandoOptions.UsePassageChecks)
                {
                    // TODO: Mother, Hunter, Monk, Outlaw, and Saint currently have placeholder rules as in-depth requirements are a difficult problem to solve
                    AccessRule accessRule = new();
                    switch (passage)
                    {
                        case "Martyr":
                            accessRule = new CompoundAccessRule(AccessRuleConstants.Regions,
                                CompoundAccessRule.CompoundOperation.AtLeast, 5);
                            break;
                        case "Mother":
                            // Surely there's a pup spawnable region within a group of 5
                            // The correct way to do this is by reading region properties files
                            accessRule = new CompoundAccessRule(AccessRuleConstants.Regions,
                                CompoundAccessRule.CompoundOperation.AtLeast, 5);
                            break;
                        case "Pilgrim":
                            List<string> echoRegions = [];
                            foreach (string region in SlugcatStats.SlugcatStoryRegions(slugcat))
                            {
                                if (World.CheckForRegionGhost(slugcat, region)) echoRegions.Add(region);
                            }
                            accessRule = new CompoundAccessRule(
                                [.. echoRegions.Select(r => new RegionAccessRule(r))],
                                CompoundAccessRule.CompoundOperation.All);
                            break;
                        case "Survivor":
                            accessRule = new KarmaAccessRule(5);
                            break;
                        case "DragonSlayer":
                            accessRule = new CompoundAccessRule(AccessRuleConstants.Lizards,
                                CompoundAccessRule.CompoundOperation.AtLeast, 6);
                            break;
                        case "Friend":
                            accessRule = new CompoundAccessRule(AccessRuleConstants.Lizards,
                                CompoundAccessRule.CompoundOperation.Any);
                            break;
                        case "Traveller":
                            List<AccessRule> regions = [];
                            foreach (string reg in SlugcatStats.SlugcatStoryRegions(slugcat))
                            {
                                regions.Add(new RegionAccessRule(reg));
                            }
                            accessRule = new CompoundAccessRule([.. regions],
                                CompoundAccessRule.CompoundOperation.All);
                            break;
                        case "Chieftain":
                            accessRule = new CreatureAccessRule(CreatureTemplate.Type.Scavenger);
                            break;
                        case "Hunter":
                            accessRule = new CompoundAccessRule(AccessRuleConstants.Regions,
                                CompoundAccessRule.CompoundOperation.AtLeast, 5);
                            break;
                        case "Monk":
                            accessRule = new CompoundAccessRule(AccessRuleConstants.Regions,
                                CompoundAccessRule.CompoundOperation.AtLeast, 5);
                            break;
                        case "Nomad":
                            accessRule = new CompoundAccessRule(AccessRuleConstants.Regions,
                                CompoundAccessRule.CompoundOperation.AtLeast, 4);
                            break;
                        case "Outlaw":
                            accessRule = new CompoundAccessRule(AccessRuleConstants.Regions,
                                CompoundAccessRule.CompoundOperation.AtLeast, 3);
                            break;
                        case "Saint":
                            accessRule = new CompoundAccessRule(AccessRuleConstants.Regions,
                                CompoundAccessRule.CompoundOperation.AtLeast, 5);
                            break;
                        case "Scholar":
                            List<AccessRule> rules =
                            [
                                new AccessRule("The_Mark"),
                                new CompoundAccessRule(AccessRuleConstants.Regions,
                                    CompoundAccessRule.CompoundOperation.AtLeast, 3)
                            ];
                            if (slugcat == SlugcatStats.Name.White || slugcat == SlugcatStats.Name.Yellow
                                || (ModManager.MSC && slugcat == MoreSlugcatsEnums.SlugcatStatsName.Gourmand))
                            {
                                rules.Add(new RegionAccessRule("SL"));
                            }
                            accessRule = new CompoundAccessRule([.. rules],
                                CompoundAccessRule.CompoundOperation.All);
                            break;
                    }

                    locations.Add(new Location($"Passage-{passage}", Location.Type.Passage, accessRule));
                }

                // Passage items
                if (RandoOptions.GivePassageItems && passage != "Gourmand")
                {
                    AllPassages.Add(passage);
                    ItemsToPlace.Add(new Item(passage, Item.Type.Passage, Item.Importance.Filler));
                }
            }

            // Create Echo locations
            if (RandoOptions.UseEchoChecks)
            {
                foreach (string echo in ExtEnumBase.GetNames(typeof(GhostWorldPresence.GhostID)))
                {
                    // No worry for duplicates from the RegionKit check,
                    // as the HashSet should ignore duplicate additions
                    if (!echo.Equals("NoGhost")
                        && World.CheckForRegionGhost(slugcat, echo)
                        && AllRegions.Contains(echo))
                    {
                        locations.Add(new Location($"Echo-{echo}", Location.Type.Echo, new RegionAccessRule(echo)));
                    }
                }
            }

            // Create Karma items
            // TODO: Add a setting to change the amount of Karma increases in pool
            for (int i = 0; i < 10; i++)
            {
                ItemsToPlace.Add(new Item("Karma", Item.Type.Karma, Item.Importance.Progression));
            }

            // TODO: Add support for expanded food quest
            // Create Food Quest locations
            if (ModManager.MSC && RandoOptions.UseFoodQuest)
            {
                List<AccessRule> allGourmRules = [];
                foreach (WinState.GourmandTrackerData data in WinState.GourmandPassageTracker)
                {
                    if (data.type == AbstractPhysicalObject.AbstractObjectType.Creature)
                    {
                        List<CreatureAccessRule> rules = [];
                        foreach (CreatureTemplate.Type type in data.crits)
                        {
                            rules.Add(new CreatureAccessRule(type));
                        }

                        AccessRule rule;
                        if (rules.Count > 1) rule = new CompoundAccessRule(rules.ToArray(), CompoundAccessRule.CompoundOperation.Any);
                        else rule = rules[0];

                        // TODO: Add effect detection for Batflies and Neurons
                        // This is temporary until there's a way to detect batflies in a region
                        if (data.crits[0] == CreatureTemplate.Type.Fly)
                        {
                            rule = new CompoundAccessRule(AccessRuleConstants.Regions,
                                CompoundAccessRule.CompoundOperation.AtLeast, 5);
                        }
                        // TODO: Add support for creatures that are PlacedObjects
                        else if (data.crits[0] == CreatureTemplate.Type.Hazer)
                        {
                            rule = new CompoundAccessRule(
                            [
                                new RegionAccessRule("LF"),
                                new RegionAccessRule("DS"),
                                new RegionAccessRule("GW"),
                                new RegionAccessRule("HI"),
                                new RegionAccessRule("SL")
                            ], CompoundAccessRule.CompoundOperation.Any);
                        }

                        allGourmRules.Add(rule);
                        locations.Add(new Location($"FoodQuest-{data.crits[0].value}", Location.Type.Food, rule));
                    }
                    else
                    {
                        AccessRule rule = new ObjectAccessRule(data.type);
                        allGourmRules.Add(rule);
                        locations.Add(new Location($"FoodQuest-{data.type.value}", Location.Type.Food, rule));
                    }
                }
                locations.Add(new Location("Passage-Gourmand", Location.Type.Passage,
                        new CompoundAccessRule([.. allGourmRules], CompoundAccessRule.CompoundOperation.All)));
            }

            // Create Special locations
            if (RandoOptions.UseSpecialChecks)
            {
                locations.Add(new Location("Eat_Neuron", Location.Type.Story, new ObjectAccessRule(AbstractPhysicalObject.AbstractObjectType.SSOracleSwarmer)));

                switch (slugcat.value)
                {
                    // Normal Iterator goals
                    case "White":
                    case "Yellow":
                    case "Gourmand":
                    case "Sofanthiel":
                        locations.Add(new Location("Meet_LttM", Location.Type.Story,
                            new CompoundAccessRule(
                            [
                                new RegionAccessRule("SL"),
                                new("The_Mark")
                            ], CompoundAccessRule.CompoundOperation.All)));
                        locations.Add(new Location("Meet_FP", Location.Type.Story,
                            new RegionAccessRule("SS")));
                        break;
                    // Spear finds LttM in LM
                    case "Spear":
                        locations.Add(new Location("Meet_LttM_Spear", Location.Type.Story,
                            new RegionAccessRule("LM")));
                        locations.Add(new Location("Meet_FP", Location.Type.Story,
                            new RegionAccessRule("SS")));
                        break;
                    // Hunter Saves LttM, which is a seperate check
                    case "Red":
                        locations.Add(new Location("Save_LttM", Location.Type.Story,
                            new CompoundAccessRule(
                            [
                                new RegionAccessRule("SL"),
                                new("Object-NSHSwarmer")
                            ], CompoundAccessRule.CompoundOperation.All)));
                        locations.Add(new Location("Meet_FP", Location.Type.Story,
                            new RegionAccessRule("SS")));
                        break;
                    // Artificer cannot meet LttM
                    case "Artificer":
                        locations.Add(new Location("Meet_FP", Location.Type.Story,
                            new RegionAccessRule("SS")));
                        break;
                    // Rivulet does a murder in RM, seperate check
                    case "Rivulet":
                        locations.Add(new Location("Meet_LttM", Location.Type.Story,
                            new CompoundAccessRule(
                            [
                                new RegionAccessRule("SL"),
                                new("The_Mark")
                            ], CompoundAccessRule.CompoundOperation.All)));
                        if (RandoOptions.UseEnergyCell)
                        {
                            locations.Add(new Location("Kill_FP", Location.Type.Story,
                                new RegionAccessRule("RM")));
                        }
                        break;
                    // Saint has 2 seperate checks here for ascending
                    case "Saint":
                        locations.Add(new Location("Ascend_LttM", Location.Type.Story,
                            new CompoundAccessRule(
                            [
                                new RegionAccessRule("SL"),
                                new KarmaAccessRule(10)
                            ], CompoundAccessRule.CompoundOperation.All)));
                        locations.Add(new Location("Ascend_FP", Location.Type.Story,
                            new CompoundAccessRule(
                            [
                                new RegionAccessRule("CL"),
                                new KarmaAccessRule(10)
                            ], CompoundAccessRule.CompoundOperation.All)));
                        break;
                }
            }

            // Create Special items
            if (!ModManager.MSC || slugcat != MoreSlugcatsEnums.SlugcatStatsName.Saint)
            {
                ItemsToPlace.Add(new Item("Neuron_Glow", Item.Type.Other, Item.Importance.Progression));
                ItemsToPlace.Add(new Item("The_Mark", Item.Type.Other, Item.Importance.Progression));
            }

            switch (slugcat.value)
            {
                case "Red":
                    ItemsToPlace.Add(new Item("Object-NSHSwarmer", Item.Type.Object, Item.Importance.Progression));
                    ItemsToPlace.Add(new Item("PearlObject-Red_stomach", Item.Type.Object, Item.Importance.Progression));
                    break;
                case "Artificer":
                    ItemsToPlace.Add(new Item("IdDrone", Item.Type.Other, Item.Importance.Progression));
                    break;
                case "Rivulet":
                    if (RandoOptions.UseEnergyCell)
                    {
                        ItemsToPlace.Add(new Item("Object-EnergyCell", Item.Type.Object, Item.Importance.Progression));
                        ItemsToPlace.Add(new Item("DisconnectFP", Item.Type.Other, Item.Importance.Progression));
                    }
                    break;
                case "Spear":
                    ItemsToPlace.Add(new Item("PearlObject-Spearmasterpearl", Item.Type.Object, Item.Importance.Progression));
                    ItemsToPlace.Add(new Item("RewriteSpearPearl", Item.Type.Other, Item.Importance.Progression));
                    break;
            }

            state.DefineLocs(locations);
        }

        // TODO: Pearl-LC is filtered out by token cache (rightfully), but needs to be present if setting enabled
        /// <summary>
        /// Applies special location rules defined by <see cref="GlobalRuleOverrides"/> and <see cref="SlugcatRuleOverrides"/>
        /// </summary>
        private void ApplyRuleOverrides()
        {
            generationLog.AppendLine("APPLY SPECIAL RULES");
            foreach (var rule in RuleOverrides)
            {
                // Find the location by id and set its rule to the override
                Location loc = state.AllLocations.FirstOrDefault(l => l.id == rule.Key);
                if (loc != null)
                {
                    if (rule.Value.IsPossible(state))
                    {
                        loc.accessRule = rule.Value;
                        generationLog.AppendLine($"Applied custom rule to {rule.Key}");
                    }
                    else
                    {
                        // Impossible locations are removed from state
                        state.AllLocations.Remove(loc);
                        state.UnreachedLocations.Remove(loc);
                        generationLog.AppendLine($"Removed impossible location: {rule.Key}");
                    }
                }
            }

            generationLog.AppendLine("Final location list:");
            foreach (Location loc in state.AllLocations)
            {
                generationLog.AppendLine($"\t{loc.id}");
                generationLog.AppendLine($"\t\t{loc.accessRule}");
            }
            generationLog.AppendLine();
        }

        private void BalanceItems()
        {
            generationLog.AppendLine("BALANCE ITEMS");
            CurrentStage = GenerationStep.BalancingItems;
            generationLog.AppendLine($"Item balancing start with {state.AllLocations.Count} locations and {ItemsToPlace.Count} items");

            // Manage case where there are not enough locations for the amount of items in pool
            while (state.AllLocations.Count < ItemsToPlace.Count)
            {
                // Remove a passage token
                IEnumerable<Item> passageTokens = ItemsToPlace.Where(i => i.type == Item.Type.Passage);
                if (passageTokens.Count() > ManagerVanilla.MIN_PASSAGE_TOKENS)
                {
                    ItemsToPlace.Remove(passageTokens.First());
                    continue;
                }

                // Cannot remove more passages, unlock gates
                IEnumerable<Item> gateItems = ItemsToPlace.Where(i => i.type == Item.Type.Gate);
                if (gateItems.Count() > 0)
                {
                    Item item = gateItems.ElementAt(randomState.Next(gateItems.Count()));
                    ItemsToPlace.Remove(item);
                    UnplacedGates.Add(item.id);
                    state.AddGate(item.ToString());
                    generationLog.AppendLine($"Pre-open gate: {item}");
                    continue;
                }

                generationLog.AppendLine("Too few locations present to make a valid seed");
                generationLog.AppendLine("Generation Failed");
                CurrentStage = GenerationStep.FailedGen;
                throw new GenerationFailureException("Too few locations present to make a valid seed");
            }

            List<Item> itemsToAdd = [];
            int hunterCyclesAdded = 0;
            while (state.AllLocations.Count > ItemsToPlace.Count + itemsToAdd.Count)
            {
                if (slugcat == SlugcatStats.Name.Red
                    && hunterCyclesAdded < state.AllLocations.Count * RandoOptions.HunterCycleIncreaseDensity)
                {
                    // Add cycle increases for Hunter
                    itemsToAdd.Add(new Item("HunterCycles", Item.Type.Other, Item.Importance.Filler));
                    hunterCyclesAdded++;
                }
                else if (RandoOptions.GiveObjectItems)
                {
                    // Add junk items
                    itemsToAdd.Add(Item.RandomJunkItem(ref randomState));
                }
                else
                {
                    // Duplicate a random gate item
                    IEnumerable<Item> gateItems = ItemsToPlace.Where(i => i.type == Item.Type.Gate);
                    Item gate = new(gateItems.ElementAt(randomState.Next(gateItems.Count())))
                    {
                        importance = Item.Importance.Filler
                    };
                    itemsToAdd.Add(gate);
                    generationLog.AppendLine($"Added duplicate gate item: {gate}");
                }
            }

            if (itemsToAdd.Count > 0)
            {
                ItemsToPlace.AddRange(itemsToAdd);
            }

            generationLog.AppendLine($"Item balancing ended with {state.AllLocations.Count} locations and {ItemsToPlace.Count} items");
        }

        private void PlaceProgression()
        {
            generationLog.AppendLine("PLACE PROGRESSION");
            CurrentStage = GenerationStep.PlacingProg;
            // Determine starting region
            if (RandoOptions.RandomizeSpawnLocation)
            {
                customStartDen = FindRandomStart(slugcat);
                generationLog.AppendLine($"Using custom start den: {customStartDen}");
                state.AddRegion(Plugin.ProperRegionMap[Regex.Split(customStartDen, "_")[0]]);
            }
            else
            {
                customStartDen = Constants.SlugcatDefaultStartingDen[slugcat];
                state.AddRegion(Constants.SlugcatStartingRegion[slugcat]);
            }

            generationLog.AppendLine($"First region: {state.Regions.First()}");

            // Continue until all regions are accessible
            // Note that a region is considered "accessible" by state regardless of
            // if there is some other rule blocking access to checks in that region
            while (state.Regions.Count != AllRegions.Count)
            {
                // All gates adjacent to exactly one of the currently accessible regions
                // Additionally includes other progression to spread them throughout play
                List<Item> placeableGates = [];
                List<Item> placeableOtherProg = [];
                foreach (Item i in ItemsToPlace)
                {
                    if (i.importance == Item.Importance.Progression)
                    {
                        if (i.type == Item.Type.Gate)
                        {
                            string[] gate = Regex.Split(i.id, "_");
                            if (state.Gates.Contains(i.id)) continue;
                            if (state.Regions.Contains(Plugin.ProperRegionMap[gate[1]]) ^ state.Regions.Contains(Plugin.ProperRegionMap[gate[2]]))
                            {
                                placeableGates.Add(i);
                            }
                        }
                        else
                        {
                            placeableOtherProg.Add(i);
                        }
                    }
                }
                // Determine which type of prog to place
                bool useOtherProgThisCycle;
                if (placeableOtherProg.Count == 0) useOtherProgThisCycle = false;
                else if (placeableGates.Count == 0) useOtherProgThisCycle = true;
                else useOtherProgThisCycle = state.AvailableLocations.Count > 5 && randomState.NextDouble() < OTHER_PROG_PLACEMENT_CHANCE;
                List<Item> placeableProg = useOtherProgThisCycle ? placeableOtherProg : placeableGates;

                // Check if we have failed
                if (state.AvailableLocations.Count == 0 || placeableProg.Count == 0)
                {
                    string errorMessage = $"Ran out of " +
                        $"{(placeableProg.Count == 0 ? "placeable progression" : "possible locations")}.";
                    generationLog.AppendLine($"ERROR: {errorMessage}");

                    generationLog.AppendLine("Failed to connect to:");
                    foreach (string region in AllRegions.Except(state.Regions))
                    {
                        generationLog.AppendLine($"\t{Plugin.RegionNamesMap[region]}");
                    }
                    // TODO: Print full final state on error
                    CurrentStage = GenerationStep.FailedGen;
                    throw new GenerationFailureException(errorMessage);
                }

                Location chosenLocation = state.PopRandomLocation(ref randomState);
                Item chosenItem = placeableProg[randomState.Next(placeableProg.Count)];
                RandomizedGame.Add(chosenLocation, chosenItem);

                if (chosenItem.type == Item.Type.Gate)
                {
                    state.AddGate(chosenItem.id);
                }
                else
                {
                    state.AddOtherProgItem(chosenItem.id);
                }

                ItemsToPlace.Remove(chosenItem);
                generationLog.AppendLine($"Placed progression \"{chosenItem.id}\" at {chosenLocation.id}");
            }

            generationLog.AppendLine("PROGRESSION STEP 2");

            // Place the remaining progression items indiscriminately
            int progLeftToPlace;
            do
            {
                List<Item> placeableProg = [.. ItemsToPlace.Where((i) =>
                {
                    return i.importance == Item.Importance.Progression;
                })];

                // Detect possible failure
                if (state.AvailableLocations.Count == 0)
                {
                    generationLog.AppendLine($"ERROR: Ran out of possible locations");

                    generationLog.AppendLine("Failed to aquire access to:");
                    foreach (Location loc in state.UnreachedLocations)
                    {
                        generationLog.AppendLine($"\t{loc.id}; {loc.accessRule}");
                    }
                    CurrentStage = GenerationStep.FailedGen;
                    throw new GenerationFailureException("Ran out of possible locations");
                }

                Location chosenLocation = state.PopRandomLocation(ref randomState);
                Item chosenItem = placeableProg[randomState.Next(placeableProg.Count)];
                RandomizedGame.Add(chosenLocation, chosenItem);

                if (chosenItem.type == Item.Type.Gate) state.AddGate(chosenItem.id);
                else state.AddOtherProgItem(chosenItem.id);

                progLeftToPlace = placeableProg.Count - 1;
                ItemsToPlace.Remove(chosenItem);
                generationLog.AppendLine($"Placed progression \"{chosenItem.id}\" at {chosenLocation.id}");
            }
            while (progLeftToPlace > 0);

            if (state.UnreachedLocations.Count > 0)
            {
                generationLog.AppendLine($"ERROR: Progression step ended with impossible locations");
                generationLog.AppendLine("Failed to aquire access to:");
                foreach (Location loc in state.UnreachedLocations)
                {
                    generationLog.AppendLine($"\t{loc.id}; {loc.accessRule}");
                }
                CurrentStage = GenerationStep.FailedGen;
                throw new GenerationFailureException("Failed to reach all locations");
            }
        }

        private void PlaceFiller()
        {
            // It is assumed there is no more progression in the pool
            // State should have full access by this point
            generationLog.AppendLine("PLACE FILLER");
            CurrentStage = GenerationStep.PlacingFiller;
            while (state.AvailableLocations.Count > 0)
            {
                Location chosenLocation = state.PopRandomLocation(ref randomState);
                Item chosenItem = ItemsToPlace[randomState.Next(ItemsToPlace.Count)];
                RandomizedGame.Add(chosenLocation, chosenItem);
                ItemsToPlace.Remove(chosenItem);
                generationLog.AppendLine($"Placed filler \"{chosenItem.id}\" at {chosenLocation.id}");
            }
        }

        public Dictionary<string, Unlock> GetCompletedSeed()
        {
            if (CurrentStage != GenerationStep.Complete) return null;

            Dictionary<string, Unlock> output = [];
            foreach (var placement in RandomizedGame)
            {
                if (!output.ContainsKey(placement.Key.id))
                {
                    output.Add(placement.Key.id, ItemToUnlock(placement.Value));
                }
                else
                {
                    Plugin.Log.LogWarning($"Tried to place double location: {placement.Key.id}");
                }
            }
            return output;
        }

        public static Unlock ItemToUnlock(Item item)
        {
            Unlock.UnlockType outputType = null;
            switch (item.type)
            {
                case Item.Type.Gate:
                    outputType = Unlock.UnlockType.Gate;
                    break;
                case Item.Type.Passage:
                    outputType = Unlock.UnlockType.Token;
                    break;
                case Item.Type.Karma:
                    outputType = Unlock.UnlockType.Karma;
                    break;
                case Item.Type.Object:
                    if (item.id.StartsWith("PearlObject-")) outputType = Unlock.UnlockType.ItemPearl;
                    else outputType = Unlock.UnlockType.Item;
                    break;
                case Item.Type.Other:
                    if (ExtEnumBase.TryParse(typeof(Unlock.UnlockType), item.id, false, out ExtEnumBase type))
                    {
                        outputType = (Unlock.UnlockType)type;
                    }
                    else
                    {
                        Plugin.Log.LogError($"ItemToUnlock could not find matching UnlockType for {item.id}");
                        return null;
                    }
                    break;
            }

            if (outputType == Unlock.UnlockType.Item)
            {
                return new Unlock(Unlock.UnlockType.Item, Unlock.IDToItem(item.id.Substring(7)));
            }
            if (outputType == Unlock.UnlockType.ItemPearl)
            {
                return new Unlock(Unlock.UnlockType.ItemPearl, Unlock.IDToItem(item.id.Substring(12), true));
            }
            return new Unlock(outputType, item.id);
        }

        public static void GenerateCustomRules()
        {
            SlugcatRuleOverrides.Add(SlugcatStats.Name.White, []);
            SlugcatRuleOverrides.Add(SlugcatStats.Name.Yellow, []);
            SlugcatRuleOverrides.Add(SlugcatStats.Name.Red, []);

            if (ModManager.MSC)
            {
                SlugcatRuleOverrides.Add(MoreSlugcatsEnums.SlugcatStatsName.Gourmand, []);
                SlugcatRuleOverrides.Add(MoreSlugcatsEnums.SlugcatStatsName.Artificer, []);
                SlugcatRuleOverrides.Add(MoreSlugcatsEnums.SlugcatStatsName.Rivulet, []);
                SlugcatRuleOverrides.Add(MoreSlugcatsEnums.SlugcatStatsName.Spear, []);
                SlugcatRuleOverrides.Add(MoreSlugcatsEnums.SlugcatStatsName.Saint, []);
                SlugcatRuleOverrides.Add(MoreSlugcatsEnums.SlugcatStatsName.Sofanthiel, []);
            }

            // SB ravine checks only reachable from LF side
            AccessRule subRavineRule = new CompoundAccessRule(
            [
                new GateAccessRule("GATE_LF_SB"),
                new RegionAccessRule("LF"),
                new RegionAccessRule("SB")
            ], CompoundAccessRule.CompoundOperation.All);
            GlobalRuleOverrides.Add("Echo-SB", subRavineRule);
            GlobalRuleOverrides.Add("Pearl-SB_ravine", subRavineRule);

            // MSC specific rules
            if (ModManager.MSC)
            {
                // OE isn't filtered out by timeline so it needs a manual rule here
                GlobalRuleOverrides.Add("Region-OE", new MultiSlugcatAccessRule(
                [
                    SlugcatStats.Name.White,
                    SlugcatStats.Name.Yellow,
                    MoreSlugcatsEnums.SlugcatStatsName.Gourmand
                ]));
                // Outer Expanse requires the Mark for Gourmand
                SlugcatRuleOverrides[MoreSlugcatsEnums.SlugcatStatsName.Gourmand].Add("Region-OE", new AccessRule("The_Mark"));

                // if Arty, Metro requires drone and mark
                // else require option
                GlobalRuleOverrides.Add("Region-LC", new OptionAccessRule("ForceOpenMetropolis"));
                SlugcatRuleOverrides[MoreSlugcatsEnums.SlugcatStatsName.Artificer].Add("Region-LC", new CompoundAccessRule(
                [
                    new("The_Mark"),
                    new("IdDrone")
                ], CompoundAccessRule.CompoundOperation.All));

                // Submerged Superstructure should only be open if playing Riv or setting allows it
                GlobalRuleOverrides.Add("Region-MS", new CompoundAccessRule(
                [
                    new SlugcatAccessRule(MoreSlugcatsEnums.SlugcatStatsName.Rivulet),
                    new OptionAccessRule("ForceOpenSubmerged")
                ], CompoundAccessRule.CompoundOperation.Any));

                // Filtration pearl only reachable from OE
                GlobalRuleOverrides.Add("Pearl-SU_filt", new CompoundAccessRule(
                [
                    new RegionAccessRule("OE"),
                    new RegionAccessRule("SU"),
                    new MultiSlugcatAccessRule(
                    [
                        SlugcatStats.Name.White,
                        SlugcatStats.Name.Yellow,
                        MoreSlugcatsEnums.SlugcatStatsName.Gourmand
                    ])
                ], CompoundAccessRule.CompoundOperation.All));
                // Spearmaster can easily reach SU_filt if spawn is not randomized
                SlugcatRuleOverrides[MoreSlugcatsEnums.SlugcatStatsName.Spear].Add("Pearl-SU_filt", new OptionAccessRule("RandomizeSpawnLocation", true));

                // Token cache fails to filter this pearl to only Past GW
                GlobalRuleOverrides.Add("Pearl-MS", new CompoundAccessRule(
                [
                    new TimelineAccessRule(SlugcatStats.Timeline.Artificer, TimelineAccessRule.TimelineOperation.AtOrBefore),
                    new RegionAccessRule("GW")
                ], CompoundAccessRule.CompoundOperation.All));

                // This chatlog is also in SB ravine
                GlobalRuleOverrides.Add("Broadcast-Chatlog_SB0", subRavineRule);

                // Rubicon isn't blocked by a gate so it never gets marked as accessible
                // Remove Rubicon from requirements and substitute Karma 10
                List<AccessRule> travellerRule = [.. SlugcatStats.SlugcatStoryRegions(MoreSlugcatsEnums.SlugcatStatsName.Saint)
                    .Except(["HR"])
                    .Select<string, AccessRule>(r => new RegionAccessRule(r))];
                travellerRule.Add(new KarmaAccessRule(10));
                SlugcatRuleOverrides[MoreSlugcatsEnums.SlugcatStatsName.Saint].Add("Passage-Traveller", new CompoundAccessRule(
                    [.. travellerRule], CompoundAccessRule.CompoundOperation.All));
            }

            // Inbuilt custom region rules
            GlobalRuleOverrides.AddRange(CustomRegionCompatability.GlobalRuleOverrides);
            SlugcatRuleOverrides.AddRange(CustomRegionCompatability.SlugcatRuleOverrides);
        }

        public string FindRandomStart(SlugcatStats.Name slugcat)
        {
            Dictionary<string, List<string>> contenders = [];
            if (File.Exists(AssetManager.ResolveFilePath($"chkrand_randomstarts.txt")))
            {
                string[] file = File.ReadAllLines(AssetManager.ResolveFilePath($"chkrand_randomstarts.txt"));
                foreach (string line in file)
                {
                    if (!line.StartsWith("//") && line.Length > 0)
                    {
                        string region = Regex.Split(line, "_")[0];
                        if (SlugcatStats.SlugcatStoryRegions(slugcat).Contains(region))
                        {
                            if (!contenders.ContainsKey(region))
                            {
                                contenders.Add(region, []);
                            }
                            contenders[region].Add(line);
                        }
                    }
                }

                string selectedRegion = contenders.Keys.ToArray()[randomState.Next(0, contenders.Count)];
                return contenders[selectedRegion][randomState.Next(contenders[selectedRegion].Count)];
            }

            return "NONE";
        }

        public class GenerationFailureException : Exception
        {
            public GenerationFailureException() : base() { }

            public GenerationFailureException(string error) : base(error) { }
        }
    }
}
