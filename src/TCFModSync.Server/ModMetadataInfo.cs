using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace TCFModSync.Server;

public record ModMetadataInfo : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.thecrimsonfuckr.tcfmodsync";
    public override string Name { get; init; } = "TCF-ModSync";
    public override string Author { get; init; } = "TheCrimsonFuckr";
    public override List<string>? Contributors { get; init; } = null;
    public override Version Version { get; init; } = new("2.0.0");
    public override Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; } = null;
    public override Dictionary<string, Range>? ModDependencies { get; init; } = null;
    public override string? Url { get; init; } = null;
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";
}
