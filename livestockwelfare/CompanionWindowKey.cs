using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace livestockwelfare;

/// <summary>
/// Feral Kinship Companions opens its companion window on an empty-hand right click, which is also
/// how animals are petted, so the window covers the petting. By default the window moves to its own
/// key (K, can be changed in the controls), and right click only pets. Players who like the window on
/// right click can switch it back with CompanionWindowOnRightClick in livestockwelfare-client.json.
/// </summary>
public class CompanionWindowKey : ModSystem
{
    public const string ConfigFileName = "livestockwelfare-client.json";
    const string HotkeyCode = "livestockwelfare-companionwindow";
    const string SocialBehaviorCode = "feralkinshipfoxsocial";

    static bool windowOnRightClick;
    static MethodInfo? isGuideFox;

    ICoreClientAPI? capi;
    Harmony? harmony;
    object? fkcSystem;
    MethodInfo? openWindow;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        var behaviorType = AccessTools.TypeByName("FeralKinshipCompanions.EntityBehaviorFeralKinshipFoxSocial");
        var systemType = AccessTools.TypeByName("FeralKinshipCompanions.FeralKinshipCompanionSystem");
        if (behaviorType == null || systemType == null) return;

        capi = api;
        windowOnRightClick = LoadConfig(api).CompanionWindowOnRightClick;
        if (windowOnRightClick) return;

        isGuideFox = AccessTools.Method(systemType, "IsGuideFox", [typeof(Entity)]);
        openWindow = AccessTools.Method(systemType, "TryOpenFoxSocialGui", [typeof(Entity), typeof(string)]);
        var onInteract = AccessTools.Method(behaviorType, nameof(EntityBehavior.OnInteract));
        if (isGuideFox == null || openWindow == null || onInteract == null)
        {
            Mod.Logger.Warning("Feral Kinship Companions has changed, its companion window stays on right click.");
            return;
        }

        fkcSystem = api.ModLoader.GetModSystem(systemType.FullName);
        harmony = new Harmony(Mod.Info.ModID + ".companionwindow");
        harmony.Patch(onInteract, prefix: new HarmonyMethod(typeof(CompanionWindowKey), nameof(SkipCompanionWindow)));

        api.Input.RegisterHotKey(HotkeyCode, Lang("hotkey-companionwindow"), GlKeys.K, HotkeyType.GUIOrOtherControls);
        api.Input.SetHotKeyHandler(HotkeyCode, _ => OpenWindowOfTarget());
    }

    static string Lang(string key) => Vintagestory.API.Config.Lang.Get("livestockwelfare:" + key);

    static CompanionWindowConfig LoadConfig(ICoreAPI api)
    {
        try
        {
            var config = api.LoadModConfig<CompanionWindowConfig>(ConfigFileName) ?? new CompanionWindowConfig();
            api.StoreModConfig(config, ConfigFileName);
            return config;
        }
        catch (Exception e)
        {
            api.Logger.Error("Could not read {0}, using defaults: {1}", ConfigFileName, e.Message);
            return new CompanionWindowConfig();
        }
    }

    // Lets the empty-hand right click through to petting (on the server) instead of opening the window.
    // Sneaking (the companion backpack), items in hand and Feral Kinship's guide fox are left alone.
    static bool SkipCompanionWindow(EntityBehavior __instance, EntityAgent byEntity, ItemSlot itemslot, EnumInteractMode mode)
    {
        var entity = __instance.entity;
        if (entity.Api.Side != EnumAppSide.Client || mode != EnumInteractMode.Interact) return true;
        if (byEntity.Controls.Sneak || itemslot is { Empty: false }) return true;
        return isGuideFox?.Invoke(null, [entity]) is true;
    }

    bool OpenWindowOfTarget()
    {
        if (capi?.World.Player.CurrentEntitySelection?.Entity is not { } target) return false;
        if (!target.HasBehavior(SocialBehaviorCode)) return false;
        return openWindow?.Invoke(fkcSystem, [target, "overview"]) is true;
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(harmony.Id);
    }
}

public class CompanionWindowConfig
{
    /// <summary>
    /// Feral Kinship Companions' companion window opens on an empty-hand right click, as in that mod,
    /// and covers the petting. When false it opens with its own key (K by default), and right click pets.
    /// </summary>
    public bool CompanionWindowOnRightClick { get; set; } = false;
}
