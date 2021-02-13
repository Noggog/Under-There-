using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnderThere
{
    public class Program
    {
        public static Task<int> Main(string[] args)
        {
            return SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .Run(args, new RunPreferences()
                {
                    ActionsForEmptyArgs = new RunDefaultPatcher()
                    {
                        TargetRelease = GameRelease.SkyrimSE
                    }
                });
        }

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            string SPIDpath = Path.Combine(state.Settings.DataFolderPath, "skse\\plugins\\po3_SpellPerkItemDistributor.dll");
            if (File.Exists(SPIDpath) == false) //SPIDtest (dual-level pun - whoa!)
            {
                throw new Exception("Spell Perk Item Distributor was not detected at " + SPIDpath + "\nAborting patch");
            }

            var settingsPath = Path.Combine(state.ExtraSettingsDataPath, "UnderThereConfig.json");

            UTconfig settings = new UTconfig();

            settings = JsonUtils.FromJson<UTconfig>(settingsPath);

            Validator.validateSettings(settings);

            // create underwear items
            List<string> UWsourcePlugins = new List<string>(); // list of source mod names for the underwear (to report to user so that they can be disabled)
            ItemImport.createItems(settings, UWsourcePlugins, state);

            // created leveled item lists (to be added to outfits)
            FormKey UT_DefaultItem = getDefaultItemFormKey(settings.Sets, settings.Assignments, state.LinkCache, state.PatchMod);
            FormKey UT_LeveledItemsAll = createLeveledList_AllItems(settings.Sets, state.LinkCache, state.PatchMod);
            Dictionary<string, FormKey> UT_LeveledItemsByWealth = createLeveledList_ByWealth(settings.Sets, settings.Assignments, state.LinkCache, state.PatchMod);

            // modify NPC outfits
            assignOutfits(settings, UT_DefaultItem, UT_LeveledItemsByWealth, UT_LeveledItemsAll, state);

            // Add slots used by underwear items to clothes and armors with 32 - Body slot active
            List<BipedObjectFlag> usedSlots = Auxil.getItemSetARMAslots(settings.Sets, state.LinkCache);
            patchBodyARMAslots(usedSlots, settings.PatchableRaces, state);

            // create and distribute gendered item inventory spell 
            copyUTScript(state);
            createInventoryFixSpell(settings.Sets, state);

            // message user
            reportARMAslots(usedSlots);
            reportDeactivatablePlugins(UWsourcePlugins);

            Console.WriteLine("\nDon't forget to install Spell Perk Item Distributor to properly manage gender-specific items.");
            Console.WriteLine("\nEnjoy the underwear. Goodbye.");
        }

        public static FormKey getDefaultItemFormKey(List<UTSet> sets, Dictionary<string, List<string>> assignments, ILinkCache lk, ISkyrimMod PatchMod)
        {
            if (assignments["Default"] == null || assignments["Default"].Count == 0)
            {
                throw new Exception("Error: could not find a default underwear defined in the settings file.");
            }

            string defaultUWname = assignments["Default"][0];

            foreach (UTSet set in sets)
            {
                if (set.Name == defaultUWname)
                {
                    return set.LeveledListFormKey;
                }
            }

            throw new Exception("Error: Could not find a Set with name " + defaultUWname);
        }

        public static FormKey createLeveledList_AllItems(List<UTSet> sets, ILinkCache lk, ISkyrimMod PatchMod)
        {
            var allItems = PatchMod.LeveledItems.AddNew();
            allItems.EditorID = "UnderThereAllItems";
            allItems.Entries = new Noggog.ExtendedList<LeveledItemEntry>();
            foreach (UTSet set in sets)
            {
                addUTitemsToLeveledList(set.Items_Mutual, allItems);
                addUTitemsToLeveledList(set.Items_Male, allItems);
                addUTitemsToLeveledList(set.Items_Female, allItems);
            }

            return allItems.FormKey;
        }

        public static void addUTitemsToLeveledList(List<UTitem> items, LeveledItem allItems)
        {
            if (allItems.Entries == null)
            {
                return;
            }

            foreach (UTitem item in items)
            {
                LeveledItemEntry entry = new LeveledItemEntry();
                LeveledItemEntryData data = new LeveledItemEntryData();
                data.Reference = item.formKey;
                data.Level = 1;
                data.Count = 1;
                entry.Data = data;
                allItems.Entries.Add(entry);
            }
        }

        public static Dictionary<string, FormKey> createLeveledList_ByWealth(List<UTSet> sets, Dictionary<string, List<string>> assignments, ILinkCache lk, ISkyrimMod PatchMod)
        {
            Dictionary<string, FormKey> itemsByWealth = new Dictionary<string, FormKey>();

            foreach (KeyValuePair<string, List<string>> assignment in assignments)
            {
                if (assignment.Value.Count == 0)
                {
                    continue;
                }

                var currentItems = PatchMod.LeveledItems.AddNew();
                currentItems.EditorID = "UnderThereItems_" + assignment.Key;
                currentItems.Entries = new Noggog.ExtendedList<LeveledItemEntry>();

                foreach (UTSet set in sets)
                {
                    if (assignment.Value.Contains(set.Name))
                    {
                        LeveledItemEntry entry = new LeveledItemEntry();
                        LeveledItemEntryData data = new LeveledItemEntryData();
                        data.Reference = set.LeveledListFormKey;
                        data.Level = 1;
                        data.Count = 1;
                        entry.Data = data;
                        currentItems.Entries.Add(entry);
                    }
                }

                itemsByWealth[assignment.Key] = currentItems.FormKey;
            }

            return itemsByWealth;
        }

        public static void assignOutfits(UTconfig settings, FormKey UT_DefaultItem, Dictionary<string, FormKey> UT_LeveledItemsByWealth, FormKey UT_LeveledItemsAll, IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            string npcGroup = "";
            FormKey currentOutfitKey = new FormKey();
            FormKey currentUWkey = new FormKey();
            List<string> GroupLookupFailures = new List<string>();
            List<string> NPClookupFailures = new List<string>();
            Dictionary<FormKey, Dictionary<string, Outfit>> OutfitMap = new Dictionary<FormKey, Dictionary<string, Outfit>>();

            string mode = settings.AssignmentMode.ToLower();

            Outfit underwearOnly = state.PatchMod.Outfits.AddNew();
            underwearOnly.EditorID = "No_Clothes";
            underwearOnly.Items = new Noggog.ExtendedList<IFormLink<IOutfitTargetGetter>>();

            foreach (var npc in state.LoadOrder.PriorityOrder.WinningOverrides<INpcGetter>())
            {
                NPCassignment specificAssignment = NPCassignment.getSpecificNPC(npc.FormKey, settings.SpecificNPCs);

                // check if NPC race should be patched
                bool isInventoryTemplate = npc.DefaultOutfit.IsNull == false && npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Inventory) == false;
                bool isGhost = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.IsGhost) || npc.Voice.FormKey.ToString() == "0854EC:Skyrim.esm" || npc.Voice.FormKey.ToString() == "0854ED:Skyrim.esm" || Auxil.hasGhostAbility(npc) || Auxil.hasGhostScript(npc);

                if (!state.LinkCache.TryResolve<IRaceGetter>(npc.Race.FormKey, out var currentRace) || 
                    currentRace == null || 
                    currentRace.EditorID == null || 
                    settings.NonPatchableRaces.Contains(currentRace.EditorID) || 
                    Auxil.isNonHumanoid(npc, currentRace, state.LinkCache) || 
                    (settings.bPatchSummonedNPCs == false && npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Summonable)) ||
                    (settings.bPatchGhosts == false && isGhost) ||
                    currentRace.EditorID.Contains("Child") ||
                    (settings.PatchableRaces.Contains(currentRace.EditorID) == false && isInventoryTemplate == false) ||
                    NPCassignment.isBlocked(npc.FormKey, settings.BlockedNPCs))
                {
                    continue;
                }

                // check if NPC gender should be patched
                if (npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) && settings.bPatchFemales == false)
                {
                    continue;
                }
                else if (!npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) && settings.bPatchMales == false)
                {
                    continue;
                }

                // check if NPC is a preset
                if (npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.IsCharGenFacePreset))
                {
                    continue;
                }

                // check if NPC is player
                if (npc.FormKey.ToString() == "000007:Skyrim.esm" || npc.FormKey.ToString() == "0361F3:Skyrim.esm")
                {
                    continue;
                }


                // check if NPC has clothes and decide if it should be patched based on user settings
                currentOutfitKey = npc.DefaultOutfit.FormKey;
                if (currentOutfitKey.IsNull)
                {
                    if (npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Inventory) && specificAssignment.isNull) // npc inherits inventory from a template - no need to patch
                    {
                        continue;
                    }
                    else if (settings.bPatchNakedNPCs == false && specificAssignment.isNull)
                    {
                        continue;
                    }
                    else
                    {
                        currentOutfitKey = underwearOnly.FormKey;
                    }
                }
                else
                {
                    if (state.LinkCache.TryResolve<IOutfitGetter>(npc.DefaultOutfit.FormKey, out var NPCoutfit) && NPCoutfit != null && NPCoutfit.Items != null && NPCoutfit.Items.Count == 0 && settings.bPatchNakedNPCs == false)
                    {
                        continue;
                    }
                }

                var NPCoverride = state.PatchMod.Npcs.GetOrAddAsOverride(npc);

                if (!specificAssignment.isNull)
                {
                    switch(specificAssignment.Type)
                    {
                        case "set":
                            npcGroup = specificAssignment.Assignment_Set;
                            currentUWkey = specificAssignment.Assignment_Set_Obj.LeveledListFormKey;
                            break;
                        case "group":
                            npcGroup = specificAssignment.Assignment_Group;
                            currentUWkey = UT_LeveledItemsByWealth[npcGroup];
                            break;
                    }    
                }
                else
                {
                    // get the wealth of current NPC
                    switch (mode)
                    {
                        case "default":
                            npcGroup = "Default";
                            currentUWkey = UT_DefaultItem; break;
                        case "class":
                            if (state.LinkCache.TryResolve<IClassGetter>(npc.Class.FormKey, out var NPCclass) && NPCclass != null && NPCclass.EditorID != null)
                            {
                                if (npc.EditorID == "Hroki" && npc.FormKey.IDString() == "01339E")
                                {
                                    npcGroup = "Default"; // hardcoded due to a particular idiosyncratic issue caused by Bethesda's weird choice of Class for Hroki.
                                    break;
                                }
                                npcGroup = getWealthGroupByEDID(NPCclass.EditorID, settings.ClassDefinitions, GroupLookupFailures);
                                currentUWkey = UT_LeveledItemsByWealth[npcGroup];
                                if (npcGroup == "Default") { NPClookupFailures.Add(npc.EditorID + " (" + npc.FormKey.ToString() + ")"); }
                            }
                            break;
                        case "faction":
                            npcGroup = getWealthGroupByFactions(npc, settings.FactionDefinitions, settings.FallBackFactionDefinitions, settings.IgnoreFactionsWhenScoring, GroupLookupFailures, state);
                            currentUWkey = UT_LeveledItemsByWealth[npcGroup];
                            if (npcGroup == "Default") { NPClookupFailures.Add(npc.EditorID + " (" + npc.FormKey.ToString() + ")"); }
                            break;
                        case "random":
                            npcGroup = "Random";
                            currentUWkey = UT_LeveledItemsAll;
                            break;
                    }
                }

                // if the current outfit modified by the current wealth group doesn't exist, create it
                if (OutfitMap.ContainsKey(currentOutfitKey) == false || OutfitMap[currentOutfitKey].ContainsKey(npcGroup) == false)
                {
                    if (!state.LinkCache.TryResolve<IOutfitGetter>(currentOutfitKey, out var NPCoutfit) || NPCoutfit == null) { continue; }
                    Outfit newOutfit = state.PatchMod.Outfits.AddNew();
                    newOutfit.DeepCopyIn(NPCoutfit);
                    if (newOutfit.EditorID != null)
                    {
                        newOutfit.EditorID += "_" + npcGroup;
                    }
                    if (newOutfit.Items != null)
                    {
                        newOutfit.Items.Add(currentUWkey);
                    }
                    if (OutfitMap.ContainsKey(currentOutfitKey) == false)
                    {
                        OutfitMap[currentOutfitKey] = new Dictionary<string, Outfit>();
                    }
                    OutfitMap[currentOutfitKey][npcGroup] = newOutfit;
                }

                NPCoverride.DefaultOutfit = OutfitMap[currentOutfitKey][npcGroup]; // assign the correct outfit to the current NPC
            }

            //report failed lookups
            if (GroupLookupFailures.Count > 0 || NPClookupFailures.Count > 0)
            {
                Auxil.LogDefaultNPCs(NPClookupFailures, GroupLookupFailures, state.ExtraSettingsDataPath);
            }
        }

        public static string getWealthGroupByFactions(INpcGetter npc, Dictionary<string, List<string>> factionDefinitions, Dictionary<string, List<string>> fallbackFactionDefinitions, List<string> ignoredFactions, List<string> GroupLookupFailures, IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            Dictionary<string, int> wealthCounts = new Dictionary<string, int>();
            Dictionary<string, int> fallBackwealthCounts = new Dictionary<string, int>();

            string tmpWealthGroup = "";
            bool bPrimaryWealthGroupFound = false;

            // initialize wealth counts
            foreach (KeyValuePair<string, List<string>> Def in factionDefinitions)
            {
                wealthCounts.Add(Def.Key, 0);
                fallBackwealthCounts.Add(Def.Key, 0);
            }
            wealthCounts.Add("Default", 0);

            // add each faction by appropriate wealth count
            foreach (var fact in npc.Factions)
            {
                if (!state.LinkCache.TryResolve<IFactionGetter>(fact.Faction.FormKey, out var currentFaction) || currentFaction == null || currentFaction.EditorID == null) { continue; }

                if (ignoredFactions.Contains(currentFaction.EditorID))
                {
                    wealthCounts["Default"]++; // "Default" will be ignored if other factions are matched
                    continue;
                }

                tmpWealthGroup = getWealthGroupByEDID(currentFaction.EditorID, factionDefinitions, GroupLookupFailures);

                if (wealthCounts.ContainsKey(tmpWealthGroup))
                {
                    wealthCounts[tmpWealthGroup]++;
                }
                
                if (tmpWealthGroup == "Default") // check fallback factions
                {
                    tmpWealthGroup = getWealthGroupByEDID(currentFaction.EditorID, fallbackFactionDefinitions, GroupLookupFailures);
                    if (fallBackwealthCounts.ContainsKey(tmpWealthGroup))
                    {
                        fallBackwealthCounts[tmpWealthGroup]++;
                    }
                }
                else
                {
                    bPrimaryWealthGroupFound = true;
                }
            }

            // fallback if NPC has no factions
            if (npc.Factions == null || npc.Factions.Count == 0)
            {
                tmpWealthGroup = getWealthGroupByEDID("*NONE", factionDefinitions, GroupLookupFailures);
                if (wealthCounts.ContainsKey(tmpWealthGroup))
                {
                    wealthCounts[tmpWealthGroup]++;
                }

                if (tmpWealthGroup == "Default")
                {
                    tmpWealthGroup = getWealthGroupByEDID("*NONE", fallbackFactionDefinitions, GroupLookupFailures);
                    if (fallBackwealthCounts.ContainsKey(tmpWealthGroup))
                    {
                        fallBackwealthCounts[tmpWealthGroup]++;
                    }
                }
                else
                {
                    bPrimaryWealthGroupFound = true;
                }
            }

            // get the wealth group with the highest number of corresponding factions.

            // If no primary wealth groups were matched, use fallback wealth groups if they were matched
            if (bPrimaryWealthGroupFound == false && fallBackwealthCounts.Values.Max() > 0)
            {
                wealthCounts = fallBackwealthCounts;
            }

            // first remove the "Default" wealth group if others are populated
            foreach (string wGroup in wealthCounts.Keys)
            {
                if (wGroup != "Default" && wealthCounts[wGroup] > 0)
                {
                    wealthCounts.Remove("Default");
                    break;
                }
            } // if "Default" was the only matched wealth group, then it remains in the wealthCounts dictionary and will necessarily be chosen

            // then figure out which wealth group was matched to the highest number of factions
            int maxFactionsMatched = wealthCounts.Values.Max();
            List<string> bestMatches = new List<string>();
            foreach (string x in wealthCounts.Keys)
            {
                if (wealthCounts[x] == maxFactionsMatched)
                {
                    bestMatches.Add(x);
                }
            }

            // return the wealth group that was matched to the highest number of factions (choose random if tied)
            var random = new Random();
            return bestMatches[random.Next(bestMatches.Count)]; 
        }

        public static string getWealthGroupByEDID(string EDID, Dictionary<string, List<string>> Definitions, List<string> GroupLookupFailures)
        {
            foreach (KeyValuePair<string, List<string>> Def in Definitions)
            {
                if (Def.Value.Contains(EDID))
                {
                    return Def.Key;
                }
            }

            // if EDID wasn't found in definitions
            if (GroupLookupFailures.Contains(EDID) == false)
            {
                GroupLookupFailures.Add(EDID);
            }

            return "Default";
        }

        public static void copyUTScript(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            string UTscriptPath = Path.Combine(state.ExtraSettingsDataPath, "UnderThereGenderedItemFix.pex");

            if (File.Exists(UTscriptPath) == false)
            {
                throw new Exception("Could not find " + UTscriptPath);
            }
            else
            {
                string destPath = Path.Combine(state.Settings.DataFolderPath, "Scripts\\UnderThereGenderedItemFix.pex");
                try
                {
                    File.Copy(UTscriptPath, destPath, true);
                }
                catch
                {
                    throw new Exception("Could not copy " + UTscriptPath + " to " + destPath);
                }
            }
        }

        public static void createInventoryFixSpell(List<UTSet> sets, IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            // get all gendered items
            Dictionary<string, List<FormKey>> genderedItems = getGenderedItems(sets);

            // create gendered item FormLists
            FormList maleItems = state.PatchMod.FormLists.AddNew();
            maleItems.EditorID = "UT_FLST_MaleOnly";
            foreach (var fk in genderedItems["male"])
            {
                maleItems.Items.Add(fk);
            }

            FormList femaleItems = state.PatchMod.FormLists.AddNew();
            femaleItems.EditorID = "UT_FLST_FemaleOnly";
            foreach (var fk in genderedItems["female"])
            {
                femaleItems.Items.Add(fk);
            }

            // create spell for SPID distribution
            // create MGEF first
            MagicEffect utItemFixEffect = state.PatchMod.MagicEffects.AddNew();
            utItemFixEffect.EditorID = "UT_MGEF_GenderedInventoryFix";
            utItemFixEffect.Name = "Removes female-only items from males and vice-versa";
            utItemFixEffect.Flags |= MagicEffect.Flag.HideInUI;
            utItemFixEffect.Flags |= MagicEffect.Flag.NoDeathDispel;
            utItemFixEffect.Archetype.Type = MagicEffectArchetype.TypeEnum.Script;
            utItemFixEffect.TargetType = TargetType.Self;
            utItemFixEffect.CastType = CastType.ConstantEffect;
            utItemFixEffect.VirtualMachineAdapter = new VirtualMachineAdapter();

            ScriptEntry UTinventoryFixScript = new ScriptEntry();
            UTinventoryFixScript.Name = "UnderThereGenderedItemFix";

            ScriptObjectProperty mProp = new ScriptObjectProperty();
            mProp.Name = "maleItems";
            mProp.Flags |= ScriptProperty.Flag.Edited;
            mProp.Object = maleItems;
            UTinventoryFixScript.Properties.Add(mProp);

            ScriptObjectProperty fProp = new ScriptObjectProperty();
            fProp.Name = "femaleItems";
            fProp.Flags |= ScriptProperty.Flag.Edited;
            fProp.Object = femaleItems;
            UTinventoryFixScript.Properties.Add(fProp);

            utItemFixEffect.VirtualMachineAdapter.Scripts.Add(UTinventoryFixScript);

            // create Spell

            //the following does not fix the issue - check later if it's deletable
            
            if (!FormKey.TryFactory("013F44:skyrim.esm", out var equipTypeEitherHandKey) || equipTypeEitherHandKey.IsNull)
            {
                throw new Exception("Could not create FormKey 013F44:skyrim.esm");
            }
            if (!state.LinkCache.TryResolve<IEquipTypeGetter>(equipTypeEitherHandKey, out var equipTypeEitherHand) || equipTypeEitherHand == null)
            {
                throw new Exception("Could not resolve FormKey 013F44:skyrim.esm");
            }
            ///

            Spell utItemFixSpell = state.PatchMod.Spells.AddNew();
            utItemFixSpell.EditorID = "UT_SPEL_GenderedInventoryFix";
            utItemFixSpell.Name = "Fixes gendered UnderThere inventory";
            utItemFixSpell.CastType = CastType.ConstantEffect;
            utItemFixSpell.TargetType = TargetType.Self;
            utItemFixSpell.Type = SpellType.Ability;
            utItemFixSpell.EquipmentType = equipTypeEitherHandKey;
            Effect utItemFixShellEffect = new Effect();
            utItemFixShellEffect.BaseEffect = utItemFixEffect;
            utItemFixShellEffect.Data = new EffectData();
            utItemFixSpell.Effects.Add(utItemFixShellEffect);

            // distribute spell via SPID
            string distr = "Spell = " + utItemFixSpell.FormKey.IDString() + " - " + utItemFixSpell.FormKey.ModKey.ToString() + " | ActorTypeNPC | NONE | NONE | NONE";
            string destPath = Path.Combine(state.Settings.DataFolderPath, "UnderThereGenderedItemFix_DISTR.ini");
            try
            {
                File.WriteAllLines(destPath, new List<string> { distr });
            }

            catch
            {
                throw new Exception("Could not write " + destPath);
            }
        }

        public static Dictionary<string, List<FormKey>> getGenderedItems(List<UTSet> sets)
        {
            Dictionary<string, List<FormKey>> genderedItems = new Dictionary<string, List<FormKey>>();
            genderedItems["male"] = new List<FormKey>();
            genderedItems["female"] = new List<FormKey>();

            foreach (UTSet set in sets)
            {
                getGenderedItemsFromList(set.Items_Male, genderedItems["male"]);
                getGenderedItemsFromList(set.Items_Female, genderedItems["female"]);
            }

            //make sure that gendered items aren't mixed
            foreach (FormKey maleItem in genderedItems["male"])
            {
                if (genderedItems["female"].Contains(maleItem))
                {
                    throw new Exception("Error: found item " + maleItem.ToString() + " in both Items_Male and Items_Female. Please move it to Items_Mutual.");
                }
            }
            foreach (FormKey femaleItem in genderedItems["female"])
            {
                if (genderedItems["male"].Contains(femaleItem))
                {
                    throw new Exception("Error: found item " + femaleItem.ToString() + " in both Items_Male and Items_Female. Please move it to Items_Mutual.");
                }
            }

            return genderedItems;
        }

        public static void getGenderedItemsFromList(List<UTitem> items, List<FormKey> uniqueFormKeys)
        {
            foreach (UTitem item in items)
            {
                if (uniqueFormKeys.Contains(item.formKey) == false)
                {
                    uniqueFormKeys.Add(item.formKey);
                }
            }
        }

        

        public static void patchBodyARMAslots(List<BipedObjectFlag> usedSlots, List<string> patchableRaces, IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            if (!FormKey.TryFactory("000019:Skyrim.esm", out var defaultRaceKey) || defaultRaceKey.IsNull)
            {
                throw new Exception("Could not get FormKey " + defaultRaceKey.ToString());
            }

            foreach (var arma in state.LoadOrder.PriorityOrder.WinningOverrides<IArmorAddonGetter>())
            {
                if (!state.LinkCache.TryResolve<IRaceGetter>(arma.Race.FormKey, out var armaRace) || armaRace == null || armaRace.EditorID == null || armaRace.EditorID.Contains("Child"))
                {
                    continue;
                }

                if (arma.Race.FormKey == defaultRaceKey || patchableRaces.Contains(armaRace.EditorID))
                { 
                    if (arma.BodyTemplate != null && arma.BodyTemplate.FirstPersonFlags.HasFlag(BipedObjectFlag.Body))
                    {
                        var patchedAA = state.PatchMod.ArmorAddons.GetOrAddAsOverride(arma);
                        if (patchedAA.BodyTemplate == null) { continue; }
                        foreach (var uwSlot in usedSlots)
                        {
                            patchedAA.BodyTemplate.FirstPersonFlags |= uwSlot;
                        }
                    }
                }
            }
        }

        public static void reportARMAslots(List<BipedObjectFlag> usedSlots)
        {
            Console.WriteLine("\nThe following slots are being used by underwear. Please make sure they don't conflict with any other modded armors.");
            foreach (var slot in usedSlots)
            {
                Console.WriteLine(Auxil.mapSlotToInt(slot));
            }
        }

        public static void reportDeactivatablePlugins(List<string> plugins)
        {
            Console.WriteLine("\nThe following plugins have been absorbed into the synthesis patch and may now be deactivated. Make sure to keep the associated meshes and textures enabled.");
            foreach (string p in plugins)
            {
                Console.WriteLine(p);
            }
        }
    }

    public class UTconfig
    {
        public string AssignmentMode { get; set; }
        public bool bPatchMales { get; set; }
        public bool bPatchFemales { get; set; }
        public bool bPatchNakedNPCs { get; set; }
        public bool bPatchSummonedNPCs { get; set; }
        public bool bPatchGhosts { get; set; }
        public bool bMakeItemsEquippable { get; set; }
        public List<string> PatchableRaces { get; set; }
        public List<string> NonPatchableRaces { get; set; }
        public Dictionary<string, List<string>> ClassDefinitions { get; set; }
        public Dictionary<string, List<string>> FactionDefinitions { get; set; }
        public Dictionary<string, List<string>> FallBackFactionDefinitions { get; set; }
        public List<string> IgnoreFactionsWhenScoring { get; set; }
        public List<NPCassignment> SpecificNPCs { get; set; }
        public List<NPCassignment> BlockedNPCs { get; set; }
        public Dictionary<string, List<string>> Assignments { get; set; }
        public List<UTSet> Sets { get; set; }

        public UTconfig()
        {
            AssignmentMode = "";
            bPatchMales = true;
            bPatchFemales = true;
            bPatchNakedNPCs = true;
            bPatchSummonedNPCs = false;
            bPatchGhosts = true;
            PatchableRaces = new List<string>();
            NonPatchableRaces = new List<string>();
            ClassDefinitions = new Dictionary<string, List<string>>();
            FactionDefinitions = new Dictionary<string, List<string>>();
            FallBackFactionDefinitions = new Dictionary<string, List<string>>();
            IgnoreFactionsWhenScoring = new List<string>();
            SpecificNPCs = new List<NPCassignment>();
            BlockedNPCs = new List<NPCassignment>();
            Assignments = new Dictionary<string, List<string>>();
            Sets = new List<UTSet>();
        }
    }

    public class UTSet
    {
        public string Name { get; set; }
        public List<UTitem> Items_Mutual { get; set; }
        public List<UTitem> Items_Male { get; set; }
        public List<UTitem> Items_Female { get; set; }
        public FormKey LeveledListFormKey { get; set; }

        public UTSet()
        {
            Name = "";
            Items_Mutual = new List<UTitem>();
            Items_Male = new List<UTitem>();
            Items_Female = new List<UTitem>();
            LeveledListFormKey = new FormKey();
        }
    }
    public class UTitem
    {
        public string Record { get; set; }

        public string DispName { get; set; }
        public float Weight { get; set; }
        public UInt32 Value { get; set; }
        public List<int> Slots { get; set; }
        public FormKey formKey { get; set; }
        public UTitem()
        {
            Record = "";
            DispName = "";
            Weight = -1;
            Value = 4294967295; // max uInt32 value
            Slots = new List<int>();
            formKey = new FormKey();
        }
    }

    public class NPCassignment
    {
        public string Name { get; set; }
        public string FormKey { get; set; }
        public string Type { get; set; }
        public string Assignment_Set { get; set; }
        public string Assignment_Group { get; set; }
        public UTSet Assignment_Set_Obj { get; set; }
        public FormKey FormKeyObj { get; set; }
        public bool isNull { get; set; }
        public NPCassignment()
        {
            Name = "";
            FormKey = "";
            Type = "";
            Assignment_Set = "";
            Assignment_Group = "";
            Assignment_Set_Obj = new UTSet();
            FormKeyObj = new FormKey();
            isNull = true;
        }

        public static NPCassignment getSpecificNPC(FormKey fk, List<NPCassignment> assigments)
        {
            foreach (var assignment in assigments)
            {
               if (assignment.FormKeyObj == fk)
               {
                    return assignment;
               }
            }

            return new NPCassignment();
        }

        public static bool isBlocked(FormKey fk, List<NPCassignment> assigments)
        {
            foreach (var assignment in assigments)
            {
                if (assignment.FormKeyObj == fk)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
