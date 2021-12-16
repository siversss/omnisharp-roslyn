#nullable enable

using OmniSharp.Models.Metadata;
using OmniSharp.Models.v1.SourceGeneratedFile;
using System.Collections.Generic;

namespace OmniSharp.Models.V2.GotoTypeDefinition
{
    public record GotoTypeDefinitionResponse
    {
        public List<TypeDefinition>? Definitions { get; init; }
    }

    public record TypeDefinition
    {
        public Location Location { get; init; } = null!;
        public MetadataSource? MetadataSource { get; init; }
        public SourceGeneratedFileInfo? SourceGeneratedFileInfo { get; init; }
    }
}
