using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Services;
using Range = SemanticVersioning.Range;
using Path = System.IO.Path;

namespace SalcosArsenal;

public sealed record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "de.salco.salcosarsenalv2";
    public override string Name { get; init; } = "Salco's Arsenal";
    public override string Author { get; init; } = "Salco";
    public override SemanticVersioning.Version Version { get; init; } = new("1.0.1");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.3");
    public override string License { get; init; } = "MIT";
    public override bool? IsBundleMod { get; init; } = true;

    public override Dictionary<string, Range>? ModDependencies { get; init; } = new()
    {
        ["com.wtt.commonlib"] = new Range("~2.0.11"),
		["com.wtt.contentbackport"] = new Range("~1.0.4"),
		
    };

    public override string? Url { get; init; }
    public override List<string>? Contributors { get; init; }
    public override List<string>? Incompatibilities { get; init; }
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 3)]
public sealed class SalcosArsenalMod(
    WTTServerCommonLib.WTTServerCommonLib wttCommon,
    DatabaseService databaseService,
    ILogger<SalcosArsenalMod> logger
) : IOnLoad
{
    public async Task OnLoad()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var modRoot = Path.GetDirectoryName(assembly.Location) ?? "";

        var itemFolders = new[]
        {
            "Weapons",
            "Ammo",
            "Attachments",
            "Items",
            "Armor"
        };

        foreach (var folder in itemFolders)
        {
            var absoluteFolder = Path.Combine(modRoot, folder);
            if (!Directory.Exists(absoluteFolder))
                continue;

            await wttCommon.CustomItemServiceExtended.CreateCustomItems(assembly, Path.Join(folder));
        }

        try
        {
            BuiltInPlatePatcher.Apply(databaseService);
        }
        catch
        {
            // IMPORTANT: Never crash server because of optional runtime patches.
        }

        await wttCommon.CustomHideoutRecipeService.CreateHideoutRecipes(assembly, Path.Join("Recipes"));

        try
        {
            SalcosCompat.Apply(databaseService, assembly);
        }
        catch
        {
            // IMPORTANT: Compat layer must never stop startup.
        }
		try
		{
			BallisticPlateCompat.Apply(databaseService);
		}
		catch
		{
			// IMPORTANT: Never crash server because of global compat patches.
		}

        logger.LogInformation("[SALCO'S ARSENAL v1.0.0 successfully loaded]");
    }
}
