using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace livestockwelfare;

/// <summary>
/// Development aid, like SelfTest (LW_SELFTEST=1): checks that the patches in patches/othermods
/// reached the other mods' animals that are installed, and that the config knows their species.
/// Logs "[lwselftest] othermods" lines; a mod that is not installed is only noted.
/// </summary>
public class SelfTestOtherMods : ModSystem
{
    // Entity code to check, its species, and whether it is a baby (nursing instead of thirst).
    static readonly (string Code, string Species, bool Baby)[] Animals =
    {
        ("teapotsgiantboar:giantboar-giant-01-female", "giantboar", false),
        ("teapotsgiantboar:tame-giantboar-giant-01-male", "giantboar", false),
        ("teapotsgiantboar:giantboar-baby-female", "giantboar", true),
        ("teapotsgiantboar:tame-giantboar-baby-male", "giantboar", true),
        ("teapotsgreatbighorn:greathorn-01-female", "greathorn", false),
        ("teapotsgreatbighorn:tame-greathorn-04-male", "greathorn", false),
        ("teapotsgreatbighorn:greathorn-baby-female", "greathorn", true),
        ("teapotsgreatbighorn:tame-greathorn-baby-male", "greathorn", true),
        ("teapotsgiantredriver:giantredriver-female", "giantredriver", false),
        ("teapotsgiantredriver:tame-giantredriver-male", "giantredriver", false),
        ("teapotsgiantredriver:giantredriver-baby-female", "giantredriver", true),
        ("teapotsgiantredriver:tame-giantredriver-baby-male", "giantredriver", true),
        ("bovinae:bovinae-adult-female-bosprimigeniusprimigenius", "cattle", false),
        ("bovinae:bovinae-adult-male-synceruscaffer", "capebuffalo", false),
        ("bovinae:bovinae-adult-female-tragelaphusstrepsiceros", "antelope", false),
        ("bovinae:bovinae-adult-female-bostaurustaurus-tamed", "cattle", false),
        ("bovinae:bovinae-adult-male-bubalusbubalis-semitamed", "waterbuffalo", false),
        ("bovinae:bovinae-baby-female-bostaurustaurus", "cattle", true),
        ("tamablesfotsa:tame-bovinae-bovinae-adult-female-bosprimigeniusprimigenius", "cattle", false),
        ("elephantidae:elephantidae-adult-female-mammuthusprimigenius", "mammoth", false),
        ("elephantidae:elephantidae-adult-male-loxodontaafricana-tamed", "elephant", false),
        ("elephantidae:elephantidae-adult-female-elephasmaximus-semitamed", "elephant", false),
        ("elephantidae:elephantidae-baby-male-mammuthusprimigenius", "mammoth", true),
        ("cervinae:cervidae-cervini-adult-female-megalocerosgiganteus", "giantdeer", false),
        ("cervinae:cervidae-cervini-adult-male-megalocerosgiganteus-semitamed", "giantdeer", false),
        ("cervinae:cervidae-cervini-baby-female-megalocerosgiganteus", "giantdeer", true),
        ("caninae:caninae-canina-adult-female-canis-lupus", "wolf", false),
        ("caninae:caninae-canina-adult-male-canis-aureus", "jackal", false),
        ("caninae:caninae-canina-adult-female-canis-familiaris-semitamed", "wolf", false),
        ("caninae:caninae-canina-baby-male-canis-lupus", "wolf", true),
        ("caninae:caninae-vulpini-adult-female-vulpes-lagopus", "arcticfox", false),
        ("caninae:caninae-vulpini-adult-male-vulpes-zerda", "desertfox", false),
        ("caninae:caninae-vulpini-baby-female-vulpes-vulpes", "fox", true),
        ("caninae:caninae-cerdocyonina-adult-female-chrysocyon-brachyurus", "smallcanid", false),
        ("caninae:caninae-urocyonini-adult-male-urocyon-cinereoargenteus", "fox", false),
        ("felinae:felinae-adult-female-lynxlynx", "lynx", false),
        ("felinae:felinae-adult-male-caracalcaracal", "caracal", false),
        ("felinae:felinae-baby-female-lynxlynx", "lynx", true),
        ("pantherinae:pantherinae-panthera-adult-female-leo-normal", "bigcat", false),
        ("pantherinae:pantherinae-panthera-adult-male-spelaea-normal", "cavelion", false),
        ("pantherinae:pantherinae-panthera-baby-female-uncia-normal", "snowleopard", true),
        ("pantherinae:pantherinae-neofelis-adult-female-nebulosa-normal", "cloudedleopard", false),
        ("tamablesfotsa:tame-pantherinae-pantherinae-panthera-adult-female-leo-normal", "bigcat", false),
        ("machairodontinae:machairodontinae-adult-female-smilodon-fatalis", "sabertooth", false),
        ("machairodontinae:machairodontinae-baby-male-homotherium-serum", "sabertooth", true),
        ("viverridae:viverridae-adult-female-genettagenetta", "civet", false),
        ("viverridae:nandiniidae-baby-male-nandiniabinotata", "civet", true),
        ("manidae:manidae-adult-female-gigantea", "pangolin", false),
        ("manidae:manidae-baby-male-javanica", "pangolin", true),
    };

    // Small animals that get the lighter set: no dirt and sickness (patches/othermods/fotsa-predators.json).
    static readonly string[] LightSpecies = { "fox", "arcticfox", "desertfox", "smallcanid", "civet", "pangolin" };

    ICoreServerAPI sapi = null!;

    public override bool ShouldLoad(EnumAppSide forSide) =>
        forSide == EnumAppSide.Server && Environment.GetEnvironmentVariable("LW_SELFTEST") == "1";

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, Check);
    }

    void Check()
    {
        var config = livestockwelfareModSystem.Config;
        foreach (var (code, species, baby) in Animals)
        {
            var type = sapi.World.GetEntityType(new AssetLocation(code));
            if (type == null)
            {
                sapi.Logger.Notification("[lwselftest] othermods {0}: not installed", code);
                continue;
            }

            string[] wanted = baby
                ? new[] { "livestockwelfare.age", "livestockwelfare.hygiene", "livestockwelfare.shelter", "livestockwelfare.nursing" }
                : new[] { "livestockwelfare.age", "livestockwelfare.hygiene", "livestockwelfare.shelter", "livestockwelfare.thirst" };
            if (LightSpecies.Contains(species)) wanted = wanted.Where(w => w != "livestockwelfare.hygiene").ToArray();
            var server = type.Server?.BehaviorsAsJsonObj?.Select(b => b["code"].AsString()).ToArray() ?? Array.Empty<string>();
            var client = type.Client?.BehaviorsAsJsonObj?.Select(b => b["code"].AsString()).ToArray() ?? Array.Empty<string>();
            var missing = wanted.Where(w => !server.Contains(w) || !client.Contains(w)).ToArray();
            bool speciesOk = server.Length > 0 && type.Server!.BehaviorsAsJsonObj
                .Where(b => b["code"].AsString()?.StartsWith("livestockwelfare.") == true)
                .All(b => b["species"].AsString() == species || b["code"].AsString() is "livestockwelfare.mouth");
            bool configOk = config?.Species.ContainsKey(species) == true && config.Weather.Species.ContainsKey(species)
                && config.Thirst.LitresPerFullDrink.ContainsKey(species);

            sapi.Logger.Notification("[lwselftest] othermods {0}: missing [{1}], species {2}, config {3} — {4}",
                code, string.Join(", ", missing), speciesOk ? "ok" : "WRONG", configOk ? "ok" : "MISSING",
                missing.Length == 0 && speciesOk && configOk ? "OK" : "FAILED");
        }
        DumpVanillaLivestock();
    }

    // The server behaviors and AI tasks of vanilla livestock after all mods' patches, to spot
    // other mods whose index-based patches ("/server/behaviors/10/...") landed on the wrong one.
    void DumpVanillaLivestock()
    {
        foreach (var code in new[] { "game:sheep-bighorn-adult-female", "game:goat-nubian-adult-female", "game:pig-eurasian-adult-female", "game:chicken-hen" })
        {
            var type = sapi.World.GetEntityType(new AssetLocation(code));
            if (type?.Server?.BehaviorsAsJsonObj is not { } behaviors)
            {
                sapi.Logger.Notification("[lwselftest] vanilla {0}: not found", code);
                continue;
            }
            sapi.Logger.Notification("[lwselftest] vanilla {0} behaviors: {1}", code,
                string.Join(", ", behaviors.Select((b, i) => i + "=" + b["code"].AsString())));
            foreach (var taskai in behaviors.Where(b => b["code"].AsString() == "taskai"))
            {
                sapi.Logger.Notification("[lwselftest] vanilla {0} aitasks: {1}", code,
                    string.Join(", ", taskai["aitasks"].AsArray().Select((t, i) =>
                        i + "=" + t["code"].AsString() + "@" + t["priority"].AsFloat())));
            }
        }
    }
}
