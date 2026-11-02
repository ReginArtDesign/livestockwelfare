using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace livestockwelfare;

/// <summary>
/// Tracks how old a livestock animal is, makes it infertile when old and lets it die of old age.
/// All state lives in WatchedAttributes, so it survives saving and is synced to the client.
/// </summary>
public class EntityBehaviorLivestockAge : EntityBehavior
{
    public const string Code = "livestockwelfare.age";

    const string BirthAttr = "lwBirthTotalDays";
    const string LifespanAttr = "lwLifespanYears";
    const string OldAttr = "lwOld";
    // Set by vanilla EntityBehaviorGrow when a baby becomes an adult. Careful: vanilla
    // EntityBehaviorAntlerGrowth (sheep, goats, pigs...) also sets it on every new adult, to a random
    // day up to 900 days in the past, so it only means "grown up" together with the marker below.
    const string VanillaAdultSinceAttr = "birthTotalDays";
    // Set (true or false) in entity.Attributes by EntityBehaviorGrow on adults that grew from a baby.
    const string VanillaGrownUpMarker = "wasFedToAdulthood";

    const float CheckIntervalSeconds = 5;

    string species = "";
    float accum;

    public EntityBehaviorLivestockAge(Entity entity) : base(entity) { }

    public override string PropertyName() => Code;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        species = attributes["species"].AsString("");
        baby = attributes["baby"].AsBool(false);
    }

    // Babies only count their age; old age starts once they have grown up (and their age is carried over).
    bool baby;

    public override void OnEntitySpawn()
    {
        base.OnEntitySpawn();
        InitAgeData();
    }

    public override void OnEntityLoaded()
    {
        base.OnEntityLoaded();
        InitAgeData();
    }

    SpeciesAging? Aging =>
        livestockwelfareModSystem.Config?.Species.TryGetValue(species, out var aging) == true ? aging : null;

    int DaysPerYear => entity.World.Calendar.DaysPerYear;

    void InitAgeData()
    {
        if (entity.World.Side != EnumAppSide.Server) return;
        var attrs = entity.WatchedAttributes;
        var rand = entity.World.Rand;

        if (baby)
        {
            // Newborn, or a baby whose real age is unknown: either way young enough to start now.
            if (!attrs.HasAttribute(BirthAttr)) attrs.SetDouble(BirthAttr, entity.World.Calendar.TotalDays);
            return;
        }

        if (!attrs.HasAttribute(LifespanAttr) && Aging is { } aging)
        {
            double lifespan = aging.LifespanYearsMin + rand.NextDouble() * (aging.LifespanYearsMax - aging.LifespanYearsMin);
            attrs.SetDouble(LifespanAttr, lifespan);
        }

        if (attrs.HasAttribute(BirthAttr)) return;

        string source;
        if (attrs.HasAttribute(VanillaAdultSinceAttr) && entity.Attributes.HasAttribute(VanillaGrownUpMarker))
        {
            // Grew up from a baby: the time spent as a baby is only a few days, so it is ignored.
            attrs.SetDouble(BirthAttr, attrs.GetDouble(VanillaAdultSinceAttr));
            source = "grown up";
        }
        else
        {
            // Wild animal or one that existed before the mod: the real age is unknown,
            // so pick a random adult age in the first half of its life.
            double lifespan = attrs.GetDouble(LifespanAttr, 2);
            double assumedYears = 1 + rand.NextDouble() * Math.Max(0, lifespan / 2 - 1);
            attrs.SetDouble(BirthAttr, entity.World.Calendar.TotalDays - assumedYears * DaysPerYear);
            source = "assumed";
        }
        entity.World.Logger.Debug("[livestockwelfare] {0} #{1}: birth set ({2}) — {3}", entity.Code, entity.EntityId, source, DebugState());
    }

    string DebugState() => string.Format(
        "age {0:0.00} y, lifespan {1:0.00} y, birth day {2:0.0}, now day {3:0.0}, {4} days/year",
        (AgeDays ?? -1) / DaysPerYear, entity.WatchedAttributes.GetDouble(LifespanAttr),
        entity.WatchedAttributes.GetDouble(BirthAttr), entity.World.Calendar.TotalDays, DaysPerYear);

    /// <summary>Age in in-game days, or null if not known yet (e.g. client before first sync).</summary>
    public double? AgeDays
    {
        get
        {
            if (!entity.WatchedAttributes.HasAttribute(BirthAttr)) return null;
            return Math.Max(0, entity.World.Calendar.TotalDays - entity.WatchedAttributes.GetDouble(BirthAttr));
        }
    }

    public bool IsOld => entity.WatchedAttributes.GetBool(OldAttr);
    public string Species => species;
    public bool IsBaby => baby;

    public override void OnGameTick(float deltaTime)
    {
        if (entity.World.Side != EnumAppSide.Server || !entity.Alive || baby) return;

        accum += deltaTime;
        if (accum < CheckIntervalSeconds) return;
        accum = 0;

        var config = livestockwelfareModSystem.Config;
        if (config?.Safety.PauseWhenNoPlayerNearby == true) PauseAgingWhileAlone();

        double? ageDays = AgeDays;
        double lifespanYears = entity.WatchedAttributes.GetDouble(LifespanAttr);
        if (config == null || ageDays == null || lifespanYears <= 0) return;

        double ageYears = ageDays.Value / DaysPerYear;

        if (config.DieOfOldAge && ageYears >= lifespanYears && LivestockUtil.CanKill(LivestockUtil.OldAgeDeathCause))
        {
            entity.World.Logger.Notification("[livestockwelfare] {0} #{1} died of old age — {2}", entity.Code, entity.EntityId, DebugState());
            LivestockUtil.Kill(entity, LivestockUtil.OldAgeDeathCause);
            return;
        }

        bool old = ageYears >= lifespanYears * config.OldAgeFraction;
        if (old != IsOld) entity.WatchedAttributes.SetBool(OldAttr, old);

        if (old && config.OldAnimalsInfertile) LivestockUtil.BlockBreeding(entity);
    }

    const string LastAgingTickAttr = "lwAgingLastDays";
    // Like the other needs, at most one day counts after a gap (e.g. the chunk was unloaded).
    const double MaxCountedGapDays = 1;

    // Age is "now minus birth", so time that should not count is skipped by moving the birth date
    // forward: time with no player nearby, and anything beyond a day after the animal was unloaded.
    void PauseAgingWhileAlone()
    {
        var attrs = entity.WatchedAttributes;
        double now = entity.World.Calendar.TotalDays;
        double last = attrs.GetDouble(LastAgingTickAttr, now);
        attrs.SetDouble(LastAgingTickAttr, now);

        double gap = now - last;
        if (gap <= 0 || !attrs.HasAttribute(BirthAttr)) return;

        double notCounted = LivestockUtil.ShouldSimulate(entity) ? Math.Max(0, gap - MaxCountedGapDays) : gap;
        if (notCounted > 0) attrs.SetDouble(BirthAttr, attrs.GetDouble(BirthAttr) + notCounted);
    }

    /// <param name="langCode">Language to use, or null for the local game language.</param>
    public string FormatAge(string? langCode = null)
    {
        double? ageDays = AgeDays;
        if (ageDays == null) return LivestockUtil.Translate(langCode, "livestockwelfare:age-unknown");

        int years = (int)(ageDays.Value / DaysPerYear);
        int days = (int)(ageDays.Value % DaysPerYear);
        return LivestockUtil.Translate(langCode, "livestockwelfare:age-value", years, days);
    }

    // The carcass text ("Died of old age") comes from vanilla, see LivestockUtil.DeathByEntityAttr.
    public override void GetInfoText(StringBuilder infotext)
    {
        if (!entity.Alive) return;

        infotext.AppendLine(Lang.Get("livestockwelfare:age-info", FormatAge()));
        if (IsOld) infotext.AppendLine(Lang.Get("livestockwelfare:old"));
    }

    // Holding the button (e.g. petting) repeats the interaction several times a second; only a new
    // click, after this long without one, prints the status again.
    const long StatusRepeatMs = 1000;
    long lastStatusInteractMs = -StatusRepeatMs;

    // Sneak + right-click with an empty hand prints the age to chat, so it can be checked
    // without relying on the hover info.
    public override void OnInteract(EntityAgent byEntity, ItemSlot itemslot, Vec3d hitPosition, EnumInteractMode mode, ref EnumHandling handled)
    {
        if (mode != EnumInteractMode.Interact || !byEntity.Controls.ShiftKey || !itemslot.Empty) return;
        if (entity.World.Side != EnumAppSide.Server || !entity.Alive) return;
        if (byEntity is not EntityPlayer { Player: IServerPlayer player }) return;

        long now = entity.World.ElapsedMilliseconds;
        bool held = now - lastStatusInteractMs < StatusRepeatMs;
        lastStatusInteractMs = now;
        if (held) return;

        string lang = player.LanguageCode;
        string message = LivestockUtil.Translate(lang, "livestockwelfare:age-chat", entity.GetName(), FormatAge(lang));
        if (IsOld) message += " — " + LivestockUtil.Translate(lang, "livestockwelfare:old");

        if (entity.GetBehavior<EntityBehaviorLivestockHygiene>() is { } hygiene && hygiene.StatusText(lang) is { Length: > 0 } hygieneText)
        {
            message += " — " + hygieneText;
        }
        if (entity.GetBehavior<EntityBehaviorLivestockNursing>() is { } nursing)
        {
            message += " — " + nursing.StatusText(lang);
        }
        if (entity.GetBehavior<EntityBehaviorLivestockShelter>() is { } shelter)
        {
            message += " — " + shelter.StatusText(lang);
        }
        if (entity.GetBehavior<EntityBehaviorLivestockThirst>() is { } thirst)
        {
            message += " — " + thirst.StatusText(lang);
            if (thirst.WaterSourceText(lang) is { Length: > 0 } water) message += " — " + water;
        }
        player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
    }
}
