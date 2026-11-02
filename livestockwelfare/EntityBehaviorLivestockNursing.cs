using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// Lambs, kids and piglets suckle from their mother. The mother is remembered at birth (vanilla
/// spawns the baby right where she stands); babies that were not born in the world, e.g. wild ones,
/// take the nearest adult female of their species. A suckling baby's nourishment follows the
/// mother's milk, which is poorer when she is thirsty, sick or stressed by the weather. Without its
/// mother (away or dead) the baby gets hungry, but food from a trough also nourishes it - once it has
/// learned to eat from one, by spending a few days with its mother or by being fed by hand. Until
/// then a motherless baby needs the player: sneak + right click with milk or its food. Poorly fed
/// babies grow more slowly; starving ones stop healing and slowly lose health. A sick mother can pass
/// her illness on to the baby she feeds.
/// </summary>
public class EntityBehaviorLivestockNursing : EntityBehavior
{
    public const string Code = "livestockwelfare.nursing";

    const string MotherIdAttr = "lwMotherId";
    // Set when the mother died: orphans never adopt another female.
    const string OrphanAttr = "lwOrphan";
    const string NourishedAttr = "lwNourished";
    const string StateAttr = "lwNursingState";
    const string LastUpdateAttr = "lwNursingLastHours";
    const string LastSaturationAttr = "lwNursingLastSaturation";
    const string KnowsTroughAttr = "lwKnowsTrough";
    const string WithMotherHoursAttr = "lwWithMotherHours";
    const string HandFedAttr = "lwHandFed";

    // Vanilla data: "origin" is set on babies born from EntityBehaviorMultiply; the "grow" tree holds
    // EntityBehaviorGrow's timer, and "hunger"/"saturation" what the baby ate at a trough.
    const string VanillaOrigin = "reproduction";
    const string VanillaGrowTree = "grow";
    const string VanillaGrowStart = "timeSpawned";

    enum NursingState { Nursing, MotherUnwell, MotherAway, Orphan }

    const float CheckIntervalSeconds = 5;
    // Like the other needs, at most a day counts after a gap (e.g. the chunk was unloaded).
    const double MaxCatchUpHours = 24;
    // Below this milk quality the info text says the mother is unwell.
    const double UnwellMilk = 0.75;
    // A baby this well fed refuses more food by hand.
    const float FullNourished = 0.95f;
    const float ChewSeconds = 4;

    static readonly AssetLocation DrinkSound = new("game", "sounds/player/drink1");
    static readonly AssetLocation EatSound = new("game", "sounds/creature/animal-eat-small");

    string species = "";
    CreatureDiet? diet;
    float accum;

    public EntityBehaviorLivestockNursing(Entity entity) : base(entity) { }

    public override string PropertyName() => Code;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        species = attributes["species"].AsString("");
        diet = properties.Attributes?["creatureDiet"].AsObject<CreatureDiet?>(null);
    }

    public override void AfterInitialized(bool onFirstSpawn)
    {
        base.AfterInitialized(onFirstSpawn);
        if (entity.World.Side != EnumAppSide.Server) return;

        // Keeps the baby away from troughs until it has learned to eat from them.
        if (entity.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager is { } tasks)
        {
            tasks.OnShouldExecuteTask += task => task is not AiTaskSeekFoodAndEat || KnowsTrough;
        }
    }

    bool KnowsTrough => LivestockUtil.BabySettings == null || entity.WatchedAttributes.GetBool(KnowsTroughAttr);

    public float Nourished => entity.WatchedAttributes.GetFloat(NourishedAttr, 1);

    NursingState State => (NursingState)entity.WatchedAttributes.GetInt(StateAttr);

    public override void OnEntitySpawn()
    {
        base.OnEntitySpawn();
        if (entity.World.Side != EnumAppSide.Server) return;

        // Called while the mother still stands exactly where the baby appeared.
        if (entity.Attributes.GetString("origin") == VanillaOrigin && FindFemale(1) is { } mother)
        {
            entity.WatchedAttributes.SetLong(MotherIdAttr, mother.EntityId);
        }
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        base.OnEntityDespawn(despawn);
        if (entity.World.Side == EnumAppSide.Server && entity.Alive) LivestockUtil.ReleaseRegen(entity);
    }

    /// <summary>The nearest living adult female of this species within the range.</summary>
    Entity? FindFemale(float range) =>
        entity.World.GetNearestEntity(entity.Pos.XYZ, range, 2, e =>
            e != entity && e.Alive && e.Code.Path.EndsWith("-female")
            && e.GetBehavior<EntityBehaviorLivestockAge>() is { IsBaby: false } age && age.Species == species);

    public override void OnGameTick(float deltaTime)
    {
        if (entity.World.Side != EnumAppSide.Server || !entity.Alive) return;

        accum += deltaTime;
        if (accum < CheckIntervalSeconds) return;
        accum = 0;

        var settings = LivestockUtil.BabySettings;
        if (settings == null)
        {
            LivestockUtil.SetRegenBlocked(entity, "nursing", false);
            return;
        }

        // Checked often so the info text stays current; nourishment changes hourly.
        var mother = FindMother(settings);
        double milk = mother == null ? 0 : MilkQuality(mother, settings);
        var state = mother != null ? (milk < UnwellMilk ? NursingState.MotherUnwell : NursingState.Nursing)
            : entity.WatchedAttributes.GetBool(OrphanAttr) ? NursingState.Orphan : NursingState.MotherAway;
        if (state != State) entity.WatchedAttributes.SetInt(StateAttr, (int)state);

        var attrs = entity.WatchedAttributes;
        double nowHours = entity.World.Calendar.TotalHours;
        if (!attrs.HasAttribute(LastUpdateAttr))
        {
            attrs.SetDouble(LastUpdateAttr, nowHours);
            attrs.SetDouble(LastSaturationAttr, Saturation);
            return;
        }

        double hours = Math.Min(nowHours - attrs.GetDouble(LastUpdateAttr), MaxCatchUpHours);
        if (hours < 1) return;
        attrs.SetDouble(LastUpdateAttr, nowHours);
        if (!LivestockUtil.ShouldSimulate(entity)) return;

        if (mother != null)
        {
            double withMother = attrs.GetDouble(WithMotherHoursAttr) + hours;
            attrs.SetDouble(WithMotherHoursAttr, withMother);
            if (withMother >= settings.LearnTroughWithMotherDays * 24) attrs.SetBool(KnowsTroughAttr, true);
        }
        UpdateNourishment(settings, hours, mother, milk);
        if (mother?.GetBehavior<EntityBehaviorLivestockHygiene>() is { IsSick: true }
            && LivestockUtil.Roll(entity, settings.SickMotherContagionPerDay, hours))
        {
            entity.GetBehavior<EntityBehaviorLivestockHygiene>()?.Infect();
        }
        ApplyEffects(settings, hours);
    }

    /// <summary>
    /// The mother if she is alive and close enough to suckle from. Notices when she has died, and lets
    /// a baby without a known mother (wild or placed) take the nearest female.
    /// </summary>
    Entity? FindMother(BabySettings settings)
    {
        var attrs = entity.WatchedAttributes;
        if (attrs.GetBool(OrphanAttr)) return null;

        if (!attrs.HasAttribute(MotherIdAttr))
        {
            var female = FindFemale(settings.NursingDistance);
            if (female == null) return null;
            attrs.SetLong(MotherIdAttr, female.EntityId);
            return female;
        }

        // Null if she is unloaded or her carcass is gone; then she just counts as away.
        var mother = entity.World.GetEntityById(attrs.GetLong(MotherIdAttr));
        if (mother == null) return null;
        if (!mother.Alive)
        {
            attrs.SetBool(OrphanAttr, true);
            attrs.RemoveAttribute(MotherIdAttr);
            return null;
        }
        return mother.Pos.SquareDistanceTo(entity.Pos) <= settings.NursingDistance * settings.NursingDistance ? mother : null;
    }

    /// <summary>How good the mother's milk is, from 0 (none) to 1 (healthy mother).</summary>
    static double MilkQuality(Entity mother, BabySettings settings)
    {
        var hygiene = mother.GetBehavior<EntityBehaviorLivestockHygiene>();
        if (hygiene?.IsCollapsed == true) return 0;

        double milk = 1;
        if (hygiene?.IsSick == true) milk *= settings.SickMotherMilk;
        // A little thirst doesn't matter; a parched mother has no milk.
        float thirst = mother.GetBehavior<EntityBehaviorLivestockThirst>()?.Thirst ?? 0;
        milk *= 1 - Math.Clamp((thirst - 0.25) / 0.7, 0, 1);
        float stress = mother.GetBehavior<EntityBehaviorLivestockShelter>()?.Stress ?? 0;
        milk *= 1 - stress * settings.StressedMotherMilkLoss;
        return Math.Clamp(milk, 0, 1);
    }

    float Saturation => entity.WatchedAttributes.GetTreeAttribute("hunger")?.GetFloat("saturation") ?? 0;

    void UpdateNourishment(BabySettings settings, double hours, Entity? mother, double milk)
    {
        double nourished = Nourished;
        if (mother != null)
        {
            // Moves towards the milk quality; after NourishAdaptHours about 63% of the way.
            nourished += (milk - nourished) * (1 - Math.Exp(-hours / settings.NourishAdaptHours));
        }
        else
        {
            nourished -= settings.HungerPerDay * hours / 24;
        }

        // Food eaten at a trough since the last check (vanilla adds up the portions as "saturation").
        float saturation = Saturation;
        double eaten = saturation - entity.WatchedAttributes.GetDouble(LastSaturationAttr, saturation);
        if (eaten > 0) nourished += eaten * settings.NourishmentPerPortion;
        entity.WatchedAttributes.SetDouble(LastSaturationAttr, saturation);

        entity.WatchedAttributes.SetFloat(NourishedAttr, (float)Math.Clamp(nourished, 0, 1));
    }

    /// <summary>Whether the baby would take milk or food by hand now: it is not suckling and still hungry.</summary>
    public bool WantsHandFeeding => entity.Alive && State != NursingState.Nursing && Nourished < FullNourished;

    /// <summary>Why the baby turns down food by hand right now (a lang key), or null if it would eat.</summary>
    public string? HandFeedRefusal =>
        !entity.Alive ? null
        : State == NursingState.Nursing ? "livestockwelfare:handfeed-suckling"
        : Nourished >= FullNourished ? "livestockwelfare:handfeed-full"
        : null;

    static bool IsMilk(ItemStack stack) =>
        stack.Collectible is BlockLiquidContainerBase container
        && container.GetContent(stack)?.Collectible.Code is { Domain: "game", Path: "milkportion" };

    /// <summary>Milk in a container, or one of the foods this animal eats from a trough.</summary>
    public bool CanHandFeedWith(ItemStack? stack) =>
        stack != null && (IsMilk(stack) || (stack.Collectible is not BlockLiquidContainerBase && diet?.Matches(stack) == true));

    long lastHandFedMs = long.MinValue / 2;

    /// <summary>
    /// Feeds the baby from the player's hand (server side): takes some milk or one food item and adds
    /// nourishment. Being fed this way a few times teaches it to eat from a trough.
    /// </summary>
    public void HandFeed(ItemSlot slot, EntityAgent byEntity)
    {
        var settings = LivestockUtil.BabySettings;
        if (settings == null || !WantsHandFeeding || !CanHandFeedWith(slot.Itemstack)) return;
        // The game may end one held use twice (when the item says it is done, and again when the
        // player lets go): one feeding, not two.
        long now = entity.World.ElapsedMilliseconds;
        if (now - lastHandFedMs < 1000) return;
        lastHandFedMs = now;

        bool creative = (byEntity as EntityPlayer)?.Player?.WorldData.CurrentGameMode == EnumGameMode.Creative;
        double nourishment;
        if (slot.Itemstack!.Collectible is BlockLiquidContainerBase container)
        {
            double litres = Math.Min(container.GetCurrentLitres(slot.Itemstack), settings.HandFeedMilkLitres);
            if (litres <= 0) return;
            if (!creative)
            {
                container.SplitStackAndPerformAction(byEntity, slot, stack => container.TryTakeLiquid(stack, (float)litres)?.StackSize ?? 0);
                slot.MarkDirty();
            }
            nourishment = settings.HandFeedMilkNourishment * litres / settings.HandFeedMilkLitres;
            entity.World.PlaySoundAt(DrinkSound, entity, null, randomizePitch: true, range: 12);
        }
        else
        {
            // It takes the food from the hand and chews it, holding it in its mouth meanwhile.
            entity.GetBehavior<EntityBehaviorLivestockMouth>()?.Hold(slot.Itemstack, ChewSeconds);
            entity.GetBehavior<EntityBehaviorLivestockHygiene>()?.HoldStill(ChewSeconds, "eat");
            if (!creative)
            {
                slot.TakeOut(1);
                slot.MarkDirty();
            }
            nourishment = settings.HandFeedFoodNourishment;
            entity.World.PlaySoundAt(EatSound, entity, null, randomizePitch: true, range: 12);
        }

        var attrs = entity.WatchedAttributes;
        float before = Nourished;
        attrs.SetFloat(NourishedAttr, (float)Math.Clamp(before + nourishment, 0, 1));
        int fed = attrs.GetInt(HandFedAttr) + 1;
        attrs.SetInt(HandFedAttr, fed);
        bool knew = attrs.GetBool(KnowsTroughAttr);
        if (fed >= settings.HandFeedingsToLearnTrough) attrs.SetBool(KnowsTroughAttr, true);

        // Tells the player it worked and how far the baby is from learning the trough.
        if ((byEntity as EntityPlayer)?.Player is IServerPlayer player)
        {
            string lang = player.LanguageCode;
            string message = LivestockUtil.Translate(lang, "livestockwelfare:handfed", entity.GetName(),
                (int)Math.Round(before * 100), (int)Math.Round(Nourished * 100));
            if (!knew)
            {
                int left = settings.HandFeedingsToLearnTrough - fed;
                message += " " + (left <= 0
                    ? LivestockUtil.Translate(lang, "livestockwelfare:handfed-learned")
                    : LivestockUtil.Translate(lang, "livestockwelfare:handfed-left", left));
            }
            player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
        }
        entity.World.Logger.Debug("[livestockwelfare] {0} #{1} fed by hand ({2} times), nourished {3:0.00}",
            entity.Code.Path, entity.EntityId, fed, Nourished);
    }

    void ApplyEffects(BabySettings settings, double hours)
    {
        float nourished = Nourished;

        // Poorly fed babies grow more slowly: push vanilla's growth timer forward, fully at 0 nourishment.
        if (nourished < settings.SlowGrowthBelow && entity.WatchedAttributes.GetTreeAttribute(VanillaGrowTree) is { } grow)
        {
            double delayHours = hours * (1 - nourished / settings.SlowGrowthBelow);
            grow.SetDouble(VanillaGrowStart, grow.GetDouble(VanillaGrowStart) + delayHours);
            entity.WatchedAttributes.MarkPathDirty(VanillaGrowTree);
        }

        bool harmed = nourished < settings.HarmBelow;
        LivestockUtil.SetRegenBlocked(entity, "nursing", harmed);
        if (!harmed) return;

        var health = entity.GetBehavior<EntityBehaviorHealth>();
        if (health == null) return;

        LivestockUtil.Harm(entity, health, settings.HarmPerDay * hours / 24, LivestockUtil.MalnutritionDeathCause, $"malnutrition (nourished {nourished:0.00})");
    }

    /// <summary>Nursing status for the info text and chat, e.g. "Nourished: 80% (suckling from its mother)".</summary>
    /// <param name="langCode">Language to use, or null for the local game language.</param>
    public string StatusText(string? langCode = null)
    {
        string key = State switch
        {
            NursingState.MotherUnwell => "livestockwelfare:nursing-unwell",
            NursingState.MotherAway => "livestockwelfare:nursing-away",
            NursingState.Orphan when !entity.WatchedAttributes.GetBool(KnowsTroughAttr) => "livestockwelfare:nursing-orphan-handfeed",
            NursingState.Orphan => "livestockwelfare:nursing-orphan",
            _ => "livestockwelfare:nursing-well"
        };
        return LivestockUtil.Translate(langCode, "livestockwelfare:nourished-percent", (int)Math.Round(Nourished * 100))
            + " (" + LivestockUtil.Translate(langCode, key) + ")";
    }

    public override void GetInfoText(StringBuilder infotext)
    {
        if (entity.Alive) infotext.AppendLine(StatusText());
    }

    public override WorldInteraction[]? GetInteractionHelp(IClientWorldAccessor world, EntitySelection es, IClientPlayer player, ref EnumHandling handled)
    {
        if (!WantsHandFeeding) return base.GetInteractionHelp(world, es, player, ref handled);
        return new[]
        {
            new WorldInteraction
            {
                ActionLangCode = "livestockwelfare:interact-handfeed",
                MouseButton = EnumMouseButton.Right,
                HotKeyCode = "shift"
            }
        };
    }
}
