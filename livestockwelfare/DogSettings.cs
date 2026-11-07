using System;
using System.Linq;

namespace livestockwelfare;

/// <summary>
/// Kept dogs: raised wolves (vanilla, generation 1 and up) and the dogs of the Wolf taming mod.
/// Their thirst, shelter, hygiene and age use the other sections with the species "dog"; this
/// section holds what only dogs have: the bond with people, guarding the herd and barking.
/// </summary>
public class DogSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Bond (0-1) of a dog that is first seen by the mod.</summary>
    public double StartBond { get; set; } = 0.5;
    /// <summary>Bond gained by petting (right click with an empty hand).</summary>
    public double BondPerPet { get; set; } = 0.1;
    /// <summary>A dog can be petted for bond once per this many in-game hours.</summary>
    public double PetCooldownHours { get; set; } = 2;
    /// <summary>Bond lost per in-game day. Half as much while a player is near (CompanyRadius).</summary>
    public double BondLossPerDay { get; set; } = 0.08;
    /// <summary>Distance (blocks) at which a player keeps a dog company.</summary>
    public int CompanyRadius { get; set; } = 8;
    /// <summary>
    /// Below this bond the dog is lonely: it mopes (lies down now and then), whines at night,
    /// doesn't breed and doesn't guard the herd.
    /// </summary>
    public double LonelyBelowBond { get; set; } = 0.25;
    /// <summary>From this bond on the dog is loyal.</summary>
    public double LoyalAboveBond { get; set; } = 0.75;

    /// <summary>
    /// Width (blocks) of a dog's collision box. Vanilla wolves are 1.2 wide and get stuck in doorways;
    /// 0 keeps the size the animal comes with. The selection box (aiming at it) is not changed.
    /// </summary>
    public double Width { get; set; } = 0.6;

    /// <summary>
    /// Wolf pups (wild ones too) follow a player holding meat or a bone, and don't run away from them,
    /// the way chickens follow grain (TreatSettings.FollowRadius). With Wolf taming, feeding them by
    /// hand (right click) tames them.
    /// </summary>
    public bool LurePups { get; set; } = true;
    /// <summary>What lures pups (wildcards): what Wolf taming lets you tame them with.</summary>
    public string[] PupLureCodes { get; set; } =
    {
        "game:bone",
        "game:redmeat-raw",
        "game:bushmeat-raw",
        "game:poultry-raw",
    };

    /// <summary>
    /// Daily chance that a kept dog needs to go (not at night while dogs sleep, see SleepInBedAtNight) dog poop next to it, which counts as a dropping (dirt) and
    /// can be shovelled up. Only outdoors: dogs are house-trained. 0 turns it off.
    /// </summary>
    public double PoopChancePerDay { get; set; } = 0.5;
    /// <summary>
    /// Indoors (under a roof) a dog that needs to go sits at the nearest door within this many blocks
    /// and whines until it is opened, then goes outside. 0: it just waits until it is outside.
    /// </summary>
    public int DoorSearchRadius { get; set; } = 16;
    /// <summary>
    /// Once done outside, a dog that went out a door comes back in through it; if it was shut, the dog
    /// sits outside it and whines for up to this many in-game hours, then stays out. 0: it stays out.
    /// </summary>
    public double ComeBackHours { get; set; } = 2;
    /// <summary>Held in this long (in-game hours) and it can't wait any more: it goes where it is. 0 never.</summary>
    public double AccidentAfterHours { get; set; } = 8;

    /// <summary>Kept dogs never hunt kept animals (the raised wolf no longer takes your chickens).</summary>
    public bool SpareKeptAnimals { get; set; } = true;
    /// <summary>Kept dogs never hunt players, unless a player hurt them in the last half minute.</summary>
    public bool SparePlayers { get; set; } = true;

    /// <summary>
    /// Kept dogs don't hunt wild game on their own: only animals a player wounded in the last half
    /// minute (and predators, see GuardHerd). Off: they hunt like the wolves they are.
    /// </summary>
    public bool HuntOnlyWoundedGame { get; set; } = true;
    /// <summary>A kept dog near a player runs at and bites game that player just wounded.</summary>
    public bool HelpHunt { get; set; } = true;

    /// <summary>A kept dog near kept livestock chases off and bites predators coming close.</summary>
    public bool GuardHerd { get; set; } = true;
    /// <summary>Distance (blocks) around the dog in which it looks for livestock and predators.</summary>
    public int GuardRadius { get; set; } = 12;
    /// <summary>Damage of one bite.</summary>
    public double GuardBiteDamage { get; set; } = 4;
    /// <summary>Predators the dog chases (wildcards, wild animals only).</summary>
    public string[] ChaseCodes { get; set; } =
    {
        "game:wolf-*",
        "game:fox-*",
        "game:hyena-*",
        "caninae:*",
    };

    /// <summary>
    /// A kept dog chases wild animals away from crops: hares, deer and the like nibble crops in
    /// vanilla. It only chases them off, it doesn't bite. Uses GuardRadius around the dog.
    /// </summary>
    public bool GuardGarden { get; set; } = true;
    /// <summary>
    /// Distance (blocks) from which the dog notices crop eaters at the garden, asleep too: it wakes
    /// up and runs out (Wolf taming's dogs nap by day). Below GuardRadius, GuardRadius is used.
    /// </summary>
    public int GardenWatchRadius { get; set; } = 24;
    /// <summary>An animal counts as at the garden when a crop is within this many blocks of it.</summary>
    public int GardenCropRadius { get; set; } = 4;
    /// <summary>The chase ends once the animal is this many blocks from the nearest crop it was after.</summary>
    public int GardenChaseOffDistance { get; set; } = 16;
    /// <summary>Wild animals that eat crops (wildcards). The player's own animals are never chased.</summary>
    public string[] GardenPestCodes { get; set; } =
    {
        "game:hare-*",
        "game:deer-*",
        "game:gazelle-*",
        "game:moose-*",
        "game:goat-*",
        "game:sheep-*",
        "game:pig-*",
    };

    // The list before wild goats, sheep and boars were added; a config still holding it gets the new one.
    static readonly string[] OldGardenPestCodes = { "game:hare-*", "game:deer-*", "game:gazelle-*", "game:moose-*" };

    /// <summary>
    /// Back from a trip (a Feral Kinship Companions expedition): the dog comes home dirty, stressed
    /// (petting calms it) and tired, and lies down in its bed (or where it is, if there is none).
    /// </summary>
    public bool TripAftermath { get; set; } = true;
    /// <summary>Filth (0-1) the coat gets on a trip.</summary>
    public double TripFilth { get; set; } = 0.5;
    /// <summary>Stress (0-1) the dog comes home with.</summary>
    public double TripStress { get; set; } = 0.4;
    /// <summary>How long (in-game hours) the dog rests after a trip.</summary>
    public double TripRestHours { get; set; } = 4;
    /// <summary>Distance (blocks) in which a tired dog looks for its bed.</summary>
    public int BedRadius { get; set; } = 24;
    /// <summary>Dog beds (wildcards): Pet AI's pet nests and Feral Kinship Companions' beds.</summary>
    public string[] BedCodes { get; set; } =
    {
        "petai:petnest-*",
        "feralkinshipcompanions:foxbed-*",
    };

    /// <summary>
    /// At night a kept dog sleeps in a free dog bed (BedCodes) within BedRadius, if there is one. Wolf
    /// taming's dogs only seek their bed by day and from 1 to 3 at night on their own.
    /// </summary>
    public bool SleepInBedAtNight { get; set; } = true;
    /// <summary>In-game hour the night's sleep starts.</summary>
    public double BedtimeHour { get; set; } = 21;
    /// <summary>In-game hour it ends.</summary>
    public double WakeUpHour { get; set; } = 6;

    /// <summary>Whether it is the dogs' night (BedtimeHour to WakeUpHour, which may run over midnight).</summary>
    public bool IsNight(Vintagestory.API.Common.IGameCalendar calendar)
    {
        double hour = calendar.HourOfDay / calendar.HoursPerDay * 24;
        return BedtimeHour > WakeUpHour
            ? hour >= BedtimeHour || hour < WakeUpHour
            : hour >= BedtimeHour && hour < WakeUpHour;
    }

    /// <summary>A kept dog growls and barks when a threat comes close.</summary>
    public bool Bark { get; set; } = true;
    /// <summary>Distance (blocks) at which the dog notices a threat and barks.</summary>
    public int BarkRadius { get; set; } = 20;
    public double BarkCooldownSeconds { get; set; } = 8;
    /// <summary>Besides the chased predators, the dog barks at these (too dangerous to chase).</summary>
    public string[] BarkCodes { get; set; } =
    {
        "game:bear-*",
        "game:drifter-*",
        "game:bowtorn-*",
        "game:shiver-*",
        "game:locust-*",
    };

    /// <summary>Whether the guard task has anything to do.</summary>
    public bool AnyGuarding => GuardHerd || GuardGarden || HelpHunt;

    public void Sanitize()
    {
        StartBond = Math.Clamp(StartBond, 0, 1);
        BondPerPet = Math.Clamp(BondPerPet, 0, 1);
        if (PetCooldownHours < 0) PetCooldownHours = 0;
        if (BondLossPerDay < 0) BondLossPerDay = 0;
        CompanyRadius = Math.Clamp(CompanyRadius, 0, 32);
        LonelyBelowBond = Math.Clamp(LonelyBelowBond, 0, 1);
        LoyalAboveBond = Math.Clamp(LoyalAboveBond, LonelyBelowBond, 1);
        GuardRadius = Math.Clamp(GuardRadius, 0, 32);
        if (GuardBiteDamage < 0) GuardBiteDamage = 0;
        BarkRadius = Math.Clamp(BarkRadius, 0, 48);
        if (BarkCooldownSeconds < 1) BarkCooldownSeconds = 1;
        ChaseCodes ??= Array.Empty<string>();
        BarkCodes ??= Array.Empty<string>();
        GardenCropRadius = Math.Clamp(GardenCropRadius, 1, 8);
        GardenWatchRadius = Math.Clamp(GardenWatchRadius, 0, 48);
        GardenChaseOffDistance = Math.Clamp(GardenChaseOffDistance, 4, 48);
        GardenPestCodes ??= Array.Empty<string>();
        if (GardenPestCodes.SequenceEqual(OldGardenPestCodes)) GardenPestCodes = new DogSettings().GardenPestCodes;
        PoopChancePerDay = Math.Clamp(PoopChancePerDay, 0, 1);
        DoorSearchRadius = Math.Clamp(DoorSearchRadius, 0, 32);
        if (AccidentAfterHours < 0) AccidentAfterHours = 0;
        if (ComeBackHours < 0) ComeBackHours = 0;
        TripFilth = Math.Clamp(TripFilth, 0, 1);
        TripStress = Math.Clamp(TripStress, 0, 1);
        if (TripRestHours < 0) TripRestHours = 0;
        BedRadius = Math.Clamp(BedRadius, 0, 48);
        BedCodes ??= Array.Empty<string>();
        BedtimeHour = Math.Clamp(BedtimeHour, 0, 24);
        WakeUpHour = Math.Clamp(WakeUpHour, 0, 24);
        PupLureCodes ??= Array.Empty<string>();
        Width = Math.Clamp(Width, 0, 2);
    }
}
