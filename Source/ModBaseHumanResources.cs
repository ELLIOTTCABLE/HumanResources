﻿using HarmonyLib;
using HugsLib;
using HugsLib.Settings;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;

namespace HumanResources
{
    public class ModBaseHumanResources : ModBase
    {

        public static FieldInfo ScenPartThingDefInfo = AccessTools.Field(typeof(ScenPart_ThingCount), "thingDef");

        public static List<ThingDef> UniversalCrops = new List<ThingDef>();

        public static List<ThingDef> UniversalWeapons = new List<ThingDef>();

        public static UnlockManager unlocked = new UnlockManager();

        public enum FactionTechPool { Both, TechLevel, Starting }

        public static FactionTechPool TechPoolMode;

        public static bool TechPoolIncludesTechLevel => TechPoolMode < FactionTechPool.Starting;

        public static bool TechPoolIncludesStarting => TechPoolMode != FactionTechPool.TechLevel;

        public static SettingHandle<bool> TechPoolIncludesScenario;

        public ModBaseHumanResources()
        {
            Settings.EntryName = "Human Resources";
        }

        public override string ModIdentifier
        {
            get
            {
                return "JPT_HumanResources";
            }
        }

        public override void DefsLoaded()
        {
            //1. Adding Tech Tab to Pawns
            //ThingDef injection stolen from the work of notfood for Psychology
            var zombieThinkTree = DefDatabase<ThinkTreeDef>.GetNamedSilentFail("Zombie");
            IEnumerable<ThingDef> things = (from def in DefDatabase<ThingDef>.AllDefs
                                            where def.race?.intelligence == Intelligence.Humanlike && !def.defName.Contains("Android") && !def.defName.Contains("Robot") && (zombieThinkTree == null || def.race.thinkTreeMain != zombieThinkTree)
                                            select def);
            List<string> registered = new List<string>();
            foreach (ThingDef t in things)
            {
                if (t.inspectorTabsResolved == null)
                {
                    t.inspectorTabsResolved = new List<InspectTabBase>(1);
                }
                t.inspectorTabsResolved.Add(InspectTabManager.GetSharedInstance(typeof(ITab_PawnKnowledge)));
                if (t.comps == null)
                {
                    t.comps = new List<CompProperties>(1);
                }
                t.comps.Add(new CompProperties_Knowledge());
                registered.Add(t.defName);
            }

            //2. Preparing knowledge support infrastructure

            //a. Things everyone knows
            UniversalWeapons.AddRange(DefDatabase<ThingDef>.AllDefs.Where(x => x.IsWeapon));
            UniversalCrops.AddRange(DefDatabase<ThingDef>.AllDefs.Where(x => x.plant != null && x.plant.Sowable));

            //b. Minus things unlocked on research
            ThingFilter lateFilter = new ThingFilter();
            foreach (ResearchProjectDef tech in DefDatabase<ResearchProjectDef>.AllDefs)
            {
                tech.InferSkillBias();
                tech.CreateStuff(lateFilter, unlocked);
                foreach (ThingDef weapon in tech.UnlockedWeapons()) UniversalWeapons.Remove(weapon);
                foreach (ThingDef plant in tech.UnlockedPlants()) UniversalCrops.Remove(plant);
            };

            //b. Also removing atipical weapons
            List<string> ForbiddenWeaponTags = DefDatabase<ThingDef>.GetNamed("ForbiddenWeaponTags").weaponTags;
            if (Prefs.LogVerbose) Log.Message("[HumanResources] Preventing these weapon types from being commom: " + ForbiddenWeaponTags.ToStringSafeEnumerable());
            foreach (string tag in ForbiddenWeaponTags)
            {
                UniversalWeapons.RemoveAll(x => !x.weaponTags.NullOrEmpty() && x.weaponTags.Any(t => t.Contains(tag)));
            }
            AccessTools.Method(typeof(DefDatabase<ThingDef>), "Remove").Invoke(this, new object[] { DefDatabase<ThingDef>.GetNamed("ForbiddenWeaponTags") });

            //c. Telling humans what's going on
            ThingCategoryDef knowledgeCat = DefDatabase<ThingCategoryDef>.GetNamed("Knowledge");
            IEnumerable<ThingDef> codifiedTech = DefDatabase<ThingDef>.AllDefs.Where(x => x.IsWithinCategory(knowledgeCat));
            if (Prefs.LogVerbose)
            {
                Log.Message("[HumanResources] Codified technologies:" + codifiedTech.Select(x => x.label).ToStringSafeEnumerable());
                Log.Message("[HumanResources] Basic crops: " + UniversalCrops.ToStringSafeEnumerable());
                Log.Message("[HumanResources] Basic weapons: " + UniversalWeapons.ToStringSafeEnumerable());
            }
            else Log.Message("[HumanResources] This is what we know: " + codifiedTech.EnumerableCount() + " technologies processed, " + UniversalCrops.Count() + " basic crops, " + UniversalWeapons.Count() + " basic weapons");

            //3. Filling gaps on the database

            //a. TechBook dirty trick, but only now this is possible!
            foreach (ThingDef t in DefDatabase<ThingDef>.AllDefsListForReading.Where(x => x.defName.Contains("TechBook")))
            {
                t.stuffCategories.Add(DefDatabase<StuffCategoryDef>.GetNamed("Technic"));
            }

            //b. Filling main technic category with subcategories
            foreach (ThingDef t in lateFilter.AllowedThingDefs.Where(t => !t.thingCategories.NullOrEmpty()))
            {
                foreach (ThingCategoryDef c in t.thingCategories)
                {
                    c.childThingDefs.Add(t);
                    if (!knowledgeCat.childCategories.Contains(c))
                    {
                        knowledgeCat.childCategories.Add(c);
                    }
                }
            }

            //c. Populating knowledge recipes and book shelves
            foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefs.Where(x => x.fixedIngredientFilter.AnyAllowedDef == null))
            {
                r.fixedIngredientFilter.ResolveReferences();
            }
            foreach (ThingDef t in DefDatabase<ThingDef>.AllDefs.Where(x => x.thingClass == typeof(Building_BookStore)))
            {
                t.building.fixedStorageSettings.filter.ResolveReferences();
                t.building.defaultStorageSettings.filter.ResolveReferences();
            }

            //4. Finally, preparing settings
            TechPoolMode = Settings.GetHandle("TechPoolMode", "TechPoolModeTitle".Translate(), "TechPoolModeDesc".Translate(), FactionTechPool.Both, null, "TechPoolMode_");
            TechPoolIncludesScenario = Settings.GetHandle<bool>("TechPoolIncludesScenario", "TechPoolIncludesScenarioTitle".Translate(), "TechPoolIncludesScenarioDesc".Translate(), true);
        }

        public override void MapComponentsInitializing(Map map)
        {
            if (GenScene.InPlayScene)
            {
                unlocked.RegisterStartingResources();
                unlocked.RecacheUnlockedWeapons();
            }
        }

        //public override void WorldLoaded()
        //{
        //}
    }
}