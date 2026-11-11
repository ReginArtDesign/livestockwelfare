using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// Weather stress: rain, snow, cold and strong sun stress animals without shelter. A roof keeps
/// off rain, snow and sun; a closed room (vanilla room rules, as for the player) also keeps out
/// the cold. Stress counts towards sickness (see EntityBehaviorLivestockHygiene), stops breeding
/// and, when severe, stops healing and slowly hurts the animal. In the hot sun animals also lose
/// their appetite (they skip the trough) and, once stressed, are listless and lie down now and then.
/// Kept animals with no roof over them at night are stressed too: they have no safe place to sleep.
/// </summary>
public class EntityBehaviorLivestockShelter : EntityBehavior
{
    public const string Code = "livestockwelfare.shelter";

    const string StressAttr = "lwWeatherStress";
    const string ConditionsAttr = "lwWeatherConditions";
    const string LastUpdateAttr = "lwWeatherLastHours";

    [Flags]
    enum Conditions
    {
        None = 0,
        Wet = 1,
        Snowed = 2,
        Cold = 4,
        Hot = 8,
        Roofed = 16,
        Enclosed = 32,
        // Rain, snow, strong heat or cold right now, whether or not this animal is sheltered from it.
        BadWeather = 64,
        Night = 128,
        // Too hot to eat (in the hot sun, with HeatLossOfAppetite on); kept here so the client can show it.
        NoAppetite = 256,
        // Kept animal out in the open at night, with no safe place to sleep.
        SleepingOutside = 512
    }

    const float CheckIntervalSeconds = 5;
    // Longer gaps (e.g. the chunk was unloaded) are not simulated: the weather back then is unknown.
    const double MaxCatchUpHours = 24;
    // Precipitation below this is just drizzle or a few flakes.
    const float MinPrecipitation = 0.05f;
    // Daylight strength above which the sun counts as strong (it ranges roughly 0..1.2).
    const float StrongSunlight = 0.7f;
    // Daylight strength below which it counts as night (dusk and dawn included).
    const float NightDaylight = 0.3f;

    string species = "";
    float accum;
    double targetStress;

    public EntityBehaviorLivestockShelter(Entity entity) : base(entity) { }

    public override string PropertyName() => Code;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        species = attributes["species"].AsString("");
        walkSpeed = attributes["walkSpeed"].AsFloat(0.006f);
        baby = attributes["baby"].AsBool(false);
    }

    // Babies feel the cold and the heat sooner (see BabySettings).
    bool baby;

    float walkSpeed;
    AiTaskLivestockSeekShelter? shelterTask;
    AiTaskLivestockBedding? beddingTask;
    // Above wandering and idling, below vanilla eating: sheltered animals still go to the trough.
    const float SeekShelterPriority = 1.29f;
    // The task raises its own priority while it is wanted (see AiTaskLivestockBedding).
    const float BeddingPriority = 1.29f;

    public override void AfterInitialized(bool onFirstSpawn)
    {
        base.AfterInitialized(onFirstSpawn);
        if (entity.World.Side != EnumAppSide.Server || entity is not EntityAgent agent) return;
        // Off its food in the heat: no trough until it is cooler or in the shade.
        if (entity.GetBehavior<EntityBehaviorTaskAI>() is { } taskAi)
        {
            // Sitting out bad weather, food and water wait until the animal really needs them.
            taskAi.TaskManager.OnShouldExecuteTask += task => (task is not AiTaskSeekFoodAndEat || !NoAppetite)
                && !(AiTaskLivestockSeekShelter.IsFoodOrWater(task) && HoldsBackFoodAndWater)
                // Resting on her bedding she doesn't get up to graze on the spot or look about
                // (vanilla sheep "idle: eat" 1.3 had her up every few seconds).
                && !(task is AiTaskIdle && beddingTask?.Resting == true);
        }
        // Mating and pregnancy on straw bedding (hens lay eggs, so they never take part); the young
        // lie there next to their mother for the first days.
        if (LivestockBedding.Settings is { } bedding && (!baby || bedding.NewbornDays > 0))
        {
            LivestockUtil.AddAiTask(agent, AiTaskLivestockBedding.Code, BeddingPriority, walkSpeed, 0, 0,
                (taskConfig, aiConfig) => beddingTask = new AiTaskLivestockBedding(agent, taskConfig, aiConfig));
        }
        if (livestockwelfareModSystem.Config?.Weather is not { Enabled: true, SeekShelter: true } || Settings == null) return;

        LivestockUtil.AddAiTask(agent, AiTaskLivestockSeekShelter.Code, SeekShelterPriority, walkSpeed, 0.05, 0.1,
            (taskConfig, aiConfig) => shelterTask = new AiTaskLivestockSeekShelter(agent, taskConfig, aiConfig));
    }

    public float Stress => entity.WatchedAttributes.GetFloat(StressAttr);

    /// <summary>Stresses the animal at once (e.g. a dog back from a trip); it fades like weather stress, and petting helps.</summary>
    public void AddStress(float amount) => entity.WatchedAttributes.SetFloat(StressAttr, Math.Clamp(Stress + amount, 0, 1));

    Conditions Current => (Conditions)entity.WatchedAttributes.GetInt(ConditionsAttr);

    /// <summary>Standing in strong sun above the species' heat limit (as of the last check).</summary>
    public bool InHotSun => (Current & Conditions.Hot) != 0;

    /// <summary>The weather is bad right now (as of the last check), even if this animal is sheltered.</summary>
    public bool BadWeather => (Current & Conditions.BadWeather) != 0;

    // Sitting out bad weather under the roof, or resting on its straw bedding: it goes for food and
    // water once it really needs them. Otherwise it stepped out to the trough and was called straight
    // back, over and over (and a vanilla trough walk starts every few seconds, hungry or not).
    bool HoldsBackFoodAndWater =>
        shelterTask?.HoldsBackFoodAndWater == true || (beddingTask?.Resting == true && shelterTask?.NeedsFoodOrWater == false);
    public bool Roofed => (Current & Conditions.Roofed) != 0;
    public bool Night => (Current & Conditions.Night) != 0;
    public bool NoAppetite => (Current & Conditions.NoAppetite) != 0;

    WeatherSettings? Settings =>
        livestockwelfareModSystem.Config?.Weather is { Enabled: true } settings && (!baby || LivestockUtil.BabySettings != null) ? settings : null;

    public override void OnGameTick(float deltaTime)
    {
        if (entity.World.Side != EnumAppSide.Server || !entity.Alive) return;

        accum += deltaTime;
        if (accum < CheckIntervalSeconds) return;
        accum = 0;

        var settings = Settings;
        if (settings == null)
        {
            if (Stress > 0) entity.WatchedAttributes.SetFloat(StressAttr, 0);
            LivestockUtil.SetRegenBlocked(entity, "weather", false);
            return;
        }

        // Conditions are checked often so the info text stays current; stress changes hourly.
        CheckConditions(settings);
        UpdateListless(settings);

        var attrs = entity.WatchedAttributes;
        double nowHours = entity.World.Calendar.TotalHours;
        if (!attrs.HasAttribute(LastUpdateAttr))
        {
            attrs.SetDouble(LastUpdateAttr, nowHours);
            return;
        }

        double hours = Math.Min(nowHours - attrs.GetDouble(LastUpdateAttr), MaxCatchUpHours);
        if (hours < 1) return;
        attrs.SetDouble(LastUpdateAttr, nowHours);
        // Nobody around (option): the time passes without the animal getting dirtier, sicker, thirstier.
        if (!LivestockUtil.ShouldSimulate(entity)) return;

        UpdateStress(settings, hours);
        ApplyEffects(settings, hours);
    }

    void CheckConditions(WeatherSettings settings)
    {
        var world = entity.World;
        var accessor = world.BlockAccessor;
        var pos = entity.Pos.AsBlockPos;

        var climate = accessor.GetClimateAt(pos, EnumGetClimateMode.NowValues);
        if (climate == null) return;

        var conditions = Conditions.None;
        // Same checks vanilla uses for the player: anything above that stops rain is a roof;
        // a room with no open exits, or more roof than skylight, is closed.
        bool roofed = accessor.GetRainMapHeightAt(pos) > pos.Y;
        var room = world.Api.ModLoader.GetModSystem<RoomRegistry>()?.GetRoomForPosition(pos);
        bool enclosed = room != null && (room.ExitCount == 0 || room.SkylightCount < room.NonSkylightCount);
        if (roofed) conditions |= Conditions.Roofed;
        if (enclosed) conditions |= Conditions.Enclosed;

        double target = 0;
        float temperature = climate.Temperature;
        var tolerance = settings.ToleranceFor(species);
        if (baby && LivestockUtil.BabySettings is { } babies)
        {
            tolerance = new WeatherTolerance
            {
                ColdBelowC = tolerance.ColdBelowC + babies.ColdSensitivityC,
                HotAboveC = tolerance.HotAboveC - babies.HeatSensitivityC
            };
        }

        if (!roofed && climate.Rainfall > MinPrecipitation)
        {
            double intensity = Math.Min(1, climate.Rainfall * 2);
            if (temperature < -1)
            {
                conditions |= Conditions.Snowed;
                target += settings.SnowStress * intensity;
            }
            else
            {
                conditions |= Conditions.Wet;
                target += settings.RainStress * intensity;
            }
        }

        if (temperature < tolerance.ColdBelowC && !enclosed)
        {
            conditions |= Conditions.Cold;
            double cold = (tolerance.ColdBelowC - temperature) * settings.ColdStressPerDegree;
            target += roofed ? cold * settings.RoofColdFactor : cold;
        }

        // Strong sun needs a clear sky: GetDayLightStrength only knows the time of day, not the clouds.
        bool sunny = climate.Rainfall <= MinPrecipitation && world.Calendar.GetDayLightStrength(pos) > StrongSunlight;
        if (!roofed && temperature > tolerance.HotAboveC && sunny)
        {
            conditions |= Conditions.Hot;
            if (settings.HeatLossOfAppetite) conditions |= Conditions.NoAppetite;
            target += (temperature - tolerance.HotAboveC) * settings.HeatStressPerDegree;
        }

        bool badWeather = climate.Rainfall > MinPrecipitation || temperature < tolerance.ColdBelowC
            || (temperature > tolerance.HotAboveC && sunny);
        if (badWeather) conditions |= Conditions.BadWeather;
        // Dark, or vanilla sleeping hours (22:00-05:00, when livestock lies down to sleep anyway).
        float hour = world.Calendar.HourOfDay;
        if (world.Calendar.GetDayLightStrength(pos) < NightDaylight || hour >= 22 || hour < 5) conditions |= Conditions.Night;
        // A dog that stays out with its people at night is fine; alone it wants its kennel.
        if ((conditions & Conditions.Night) != 0 && !roofed && settings.SleepOutsideStress > 0 && LivestockUtil.IsKept(entity)
            && entity.GetBehavior<EntityBehaviorLivestockDog>()?.HasCompany != true)
        {
            conditions |= Conditions.SleepingOutside;
            target += settings.SleepOutsideStress;
        }

        targetStress = Math.Min(1, target);
        if (conditions != Current) entity.WatchedAttributes.SetInt(ConditionsAttr, (int)conditions);
    }

    const float HeatRestMinSeconds = 20, HeatRestMaxSeconds = 60;

    // Worn out by the heat: now and then it lies down where it is (like a sick animal does).
    void UpdateListless(WeatherSettings settings)
    {
        if (!InHotSun || Stress < settings.HeatListlessAboveStress) return;
        if (entity.GetBehavior<EntityBehaviorLivestockHygiene>() is not { IsCollapsed: false, IsHeldStill: false } hygiene) return;
        if (entity.World.Rand.NextDouble() >= settings.HeatLieDownChance) return;
        hygiene.HoldStill(HeatRestMinSeconds + (float)entity.World.Rand.NextDouble() * (HeatRestMaxSeconds - HeatRestMinSeconds));
    }

    void UpdateStress(WeatherSettings settings, double hours)
    {
        double stress = Stress;
        // Moves towards the current weather; after StressAdaptHours about 63% of the way.
        stress += (targetStress - stress) * (1 - Math.Exp(-hours / settings.StressAdaptHours));
        entity.WatchedAttributes.SetFloat(StressAttr, (float)Math.Clamp(stress, 0, 1));
    }

    void ApplyEffects(WeatherSettings settings, double hours)
    {
        float stress = Stress;
        if (stress >= settings.NoBreedingAboveStress) LivestockUtil.BlockBreeding(entity);

        // Wild animals feel the weather but don't die of it.
        bool harmed = stress >= settings.HarmAboveStress && LivestockUtil.IsKept(entity);
        LivestockUtil.SetRegenBlocked(entity, "weather", harmed);
        if (!harmed) return;

        var health = entity.GetBehavior<EntityBehaviorHealth>();
        if (health == null) return;

        LivestockUtil.Harm(entity, health, settings.HarmPerDay * hours / 24, LivestockUtil.ExposureDeathCause, $"exposure (stress {stress:0.00})");
    }

    const string LastPettedAttr = "lwLastPettedHours";
    const float PetRestSeconds = 5;
    static SimpleParticleProperties? hearts;

    // Petting: right click with an empty hand (without sneaking, which shows the status in chat)
    // calms the animal down. Handled on the server; the particles are sent to nearby players.
    // Other mods' petting (e.g. Better Husbandry) still runs, as we don't stop the interaction.
    public override void OnInteract(EntityAgent byEntity, ItemSlot itemslot, Vec3d hitPosition, EnumInteractMode mode, ref EnumHandling handled)
    {
        if (mode == EnumInteractMode.Interact && itemslot.Empty) ShowFirstTip(byEntity);
        if (mode != EnumInteractMode.Interact || byEntity.Controls.ShiftKey || !itemslot.Empty) return;
        if (entity.World.Side != EnumAppSide.Server || !entity.Alive || Settings is not { } settings) return;

        double now = entity.World.Calendar.TotalHours;
        var attrs = entity.WatchedAttributes;
        if (attrs.HasAttribute(LastPettedAttr) && now - attrs.GetDouble(LastPettedAttr) < settings.PetCooldownHours) return;
        attrs.SetDouble(LastPettedAttr, now);

        attrs.SetFloat(StressAttr, (float)Math.Max(0, Stress - settings.PetStressReduction));
        SpawnHearts();
        // It settles down for a moment, enjoying it.
        entity.GetBehavior<EntityBehaviorLivestockHygiene>()?.HoldStill(PetRestSeconds, EntityBehaviorLivestockHygiene.RestAnimation);
    }

    const string TipShownAttr = "lwTipShown";

    // The first time a player touches a kept or wild animal with an empty hand, a chat line points
    // to the status report and the handbook page. Remembered on the player, so once per world.
    void ShowFirstTip(EntityAgent byEntity)
    {
        if (entity.World.Side != EnumAppSide.Server || byEntity is not EntityPlayer { Player: IServerPlayer player } playerEntity) return;
        if (playerEntity.WatchedAttributes.GetBool(TipShownAttr)) return;
        playerEntity.WatchedAttributes.SetBool(TipShownAttr, true);
        player.SendMessage(GlobalConstants.GeneralChatGroup, LivestockUtil.Translate(player.LanguageCode, "livestockwelfare:tip-first-animal"), EnumChatType.Notification);
    }

    void SpawnHearts()
    {
        hearts ??= new SimpleParticleProperties(
            4, 6, ColorUtil.ToRgba(230, 240, 120, 170),
            new Vec3d(), new Vec3d(),
            new Vec3f(-0.15f, 0.25f, -0.15f), new Vec3f(0.15f, 0.5f, 0.15f),
            1.2f, -0.05f, 0.15f, 0.25f, EnumParticleModel.Quad);

        var box = entity.SelectionBox;
        var pos = entity.Pos;
        hearts.MinPos.Set(pos.X + box.X1, pos.Y + box.Y2, pos.Z + box.Z1);
        hearts.AddPos.Set(box.Width, 0.2, box.Length);
        entity.World.SpawnParticles(hearts);
    }

    const int MildHeatBelowPercent = 20;

    /// <summary>Weather status for the info text and chat, e.g. "Getting wet, cold (weather stress: 45%)".</summary>
    /// <param name="langCode">Language to use, or null for the local game language.</param>
    public string StatusText(string? langCode = null)
    {
        var conditions = Current;
        int stressPercent = (int)Math.Round(Stress * 100);

        var problems = new List<string>();
        if ((conditions & Conditions.Wet) != 0) problems.Add("livestockwelfare:weather-wet");
        if ((conditions & Conditions.Snowed) != 0) problems.Add("livestockwelfare:weather-snow");
        if ((conditions & Conditions.Cold) != 0) problems.Add("livestockwelfare:weather-cold");
        // "Suffering" only once the heat has really started to stress the animal.
        if ((conditions & Conditions.Hot) != 0) problems.Add(stressPercent < MildHeatBelowPercent ? "livestockwelfare:weather-hot-mild" : "livestockwelfare:weather-hot");
        if ((conditions & Conditions.NoAppetite) != 0) problems.Add("livestockwelfare:weather-noappetite");
        if ((conditions & Conditions.SleepingOutside) != 0) problems.Add("livestockwelfare:weather-sleepoutside");

        var text = new StringBuilder();
        if (problems.Count > 0)
        {
            for (int i = 0; i < problems.Count; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(LivestockUtil.Translate(langCode, problems[i]));
            }
            text.Append(" (").Append(LivestockUtil.Translate(langCode, "livestockwelfare:weather-stress", stressPercent)).Append(')');
            return text.ToString();
        }

        string shelter = (conditions & Conditions.Enclosed) != 0 ? "livestockwelfare:weather-enclosed"
            : (conditions & Conditions.Roofed) != 0 ? "livestockwelfare:weather-roofed"
            : "livestockwelfare:weather-open";
        text.Append(LivestockUtil.Translate(langCode, shelter));
        if (stressPercent > 0) text.Append(" (").Append(LivestockUtil.Translate(langCode, "livestockwelfare:weather-stress", stressPercent)).Append(')');
        return text.ToString();
    }

    public override void GetInfoText(StringBuilder infotext)
    {
        if (!entity.Alive) return;
        infotext.AppendLine(StatusText());
        if (entity.WatchedAttributes.GetBool(LivestockBedding.NeedsBeddingAttr)) infotext.AppendLine(Lang.Get("livestockwelfare:needs-bedding"));
    }
}
