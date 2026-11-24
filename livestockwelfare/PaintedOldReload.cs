using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace livestockwelfare;

/// <summary>
/// Client command ".lwreloadold": reloads the hand-painted old coats
/// (livestockwelfare:textures/old/..., see DirtTextures.PaintedOldLocation) without restarting,
/// so a texture artist can save a file and look at the animal right away.
/// </summary>
public class PaintedOldReload : ModSystem
{
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        api.ChatCommands.Create("lwreloadold")
            .WithDescription("Livestock Welfare: reloads the hand-painted old-coat textures, so animals show changes without a restart")
            .HandleWith(_ =>
            {
                int found = DirtTextures.ReloadPainted(api);
                return TextCommandResult.Success($"Reloaded {found} painted old-coat textures.");
            });
    }
}
