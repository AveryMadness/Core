using System.Diagnostics;

using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.Utils;

using Core.Cloud.Objects;
using Core.Resources.Framework.Base;

using Microsoft.AspNetCore.Mvc;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: Mesh Diagnostics                                                                                          */
/*                                                                                                                                  */
/* Why a mesh did or did not come out of a build. A mesh that will not export says so in one of several places: the profile is on   */
/* the wrong engine version for the build, the file mounted from an editor container and carries no cooked geometry, its LOD bulk   */
/* data sits in a payload file that is not there, the cook stripped a LOD, or the reader threw partway through and the endpoint     */
/* swallowed it. This walks every one of those in order and writes down what it found, and the reader's own complaints with it.     */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    private sealed record DiagnosticError(string Stage, string Type, string Message, string? Stack);

    private sealed record DiagnosedFile(string Path, string Extension, long Size, bool Chosen, string Container, string? ContainerGame, string? ContainerVersion, bool? ContainerEncrypted, bool Encrypted, string Compression, bool IsPackage, bool IsPayload);

    private sealed record DiagnosedResolution(bool Found, string Profile, bool IsMainProfile, DiagnosedFile? File, List<DiagnosedFile> Duplicates, List<DiagnosedFile> Payloads);

    private sealed record DiagnosedExport(int Index, string? Name, string? Type, string? Class, string? Flags, int? Properties, string? Failure);

    private sealed record DiagnosedPackage(bool Loaded, string? Kind, string? Name, object? FileVersion, bool Unversioned, string? Flags, bool Cooked, bool FilterEditorOnly, bool UnversionedProperties, string? SavedByEngine, string? CompatibleWithEngine, object? CustomVersions, int Names, int Imports, int ExportCount, bool Mappings, List<DiagnosedExport> Exports);

    private sealed record DiagnosedServe(int Vertices, int Indices, int Sections, int TexCoords, int BonesPerVertex);

    private sealed record DiagnosedLod(int Index, bool SkipLod, string? SkipReason, string? VertexArray, object? Detail, DiagnosedServe? Served, string? ServeFailure);

    private sealed record DiagnosedMesh(string Name, string Type, bool Failed, bool HasGeometry, object? Detail, List<DiagnosedLod> Lods);

    private sealed class MeshDiagnosis
    {
        public string Path = "";
        public string? ExportName;
        public object? Build;
        public string? LoadedMappings;
        public string? Game;
        public Dictionary<string, bool> VersionOptions = new();
        public DiagnosedResolution? Resolution;
        public DiagnosedPackage? Package;
        public List<DiagnosedMesh> Meshes = [];
        public List<DiagnosticError> Errors = [];
        public IReadOnlyList<LogCapture.CapturedLine> Log = [];
        public List<string> Verdict = [];
        public double ElapsedMs;
    }

    /* Everything there is to know about why a mesh at Path exports the way it does */
    [HttpGet("diagnose/mesh")]
    public ActionResult DiagnoseMesh(string? path, string? export_name)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new
        {
            errorCode = "cloud.diagnose.no_path",
            errorMessage = "No asset supplied",
            numericErrorCode = 1007
        });

        var watch = Stopwatch.StartNew();
        var diagnosis = new MeshDiagnosis { Path = path.SubstringBefore('.'), ExportName = export_name };

        using (LogCapture.Instance.Begin())
        {
            Serilog.Log.Information("[Core.Cloud]: diagnosing mesh {Path}", diagnosis.Path);

            var profile = Attempt(diagnosis, "resolve", () => DescribeResolution(diagnosis));

            if (profile is not null)
            {
                diagnosis.Build = Attempt(diagnosis, "build", () => DescribeBuild(profile));
                diagnosis.LoadedMappings = profile.LoadedMappingsFile;
                diagnosis.Game = profile.Provider.Versions.Game.ToString();
                diagnosis.VersionOptions = profile.VersionOptions;

                var package = Attempt(diagnosis, "package", () => DescribePackage(diagnosis, profile.Provider));

                if (package is not null)
                {
                    Attempt<object?>(diagnosis, "meshes", () =>
                    {
                        DescribeMeshes(diagnosis, package);

                        return null;
                    });
                }
            }
        }

        diagnosis.Log = LogCapture.Instance.Drain();
        diagnosis.ElapsedMs = watch.Elapsed.TotalMilliseconds;

        Conclude(diagnosis);

        foreach (var line in diagnosis.Verdict)
        {
            Serilog.Log.Warning("[Core.Cloud]: {Path}: {Verdict}", diagnosis.Path, line);
        }

        return new JsonResult(new
        {
            path = diagnosis.Path,
            exportName = diagnosis.ExportName,
            verdict = diagnosis.Verdict,
            build = diagnosis.Build,
            resolution = diagnosis.Resolution,
            package = diagnosis.Package,
            meshes = diagnosis.Meshes,
            errors = diagnosis.Errors,
            log = diagnosis.Log,
            elapsedMs = diagnosis.ElapsedMs
        });
    }

    /* Runs one stage, and writes the exception down under its name rather than letting it end the
     * report: what the later stages would have said is usually the point */
    private static T? Attempt<T>(MeshDiagnosis diagnosis, string stage, Func<T?> work)
    {
        try
        {
            return work();
        }
        catch (Exception exception)
        {
            diagnosis.Errors.Add(new DiagnosticError(stage, exception.GetType().FullName ?? exception.GetType().Name, exception.Message, exception.ToString()));

            Serilog.Log.Error(exception, "[Core.Cloud]: diagnosing {Path} failed while {Stage}", diagnosis.Path, stage);

            return default;
        }
    }

    /* The build the profile reads as, which is the first thing to be wrong when a mesh out of a
     * particular game will not read: geometry is versioned by the engine, not by the properties */
    private static object DescribeBuild(BaseProfile profile)
    {
        var versions = profile.Provider.Versions;

        return new
        {
            profile = profile.Name,
            archiveDirectory = profile.ArchiveDirectory,
            game = versions.Game.ToString(),
            gameValue = $"0x{(uint)versions.Game:X8}",
            packageFileVersion = new { ue4 = versions.Ver.FileVersionUE4, ue5 = versions.Ver.FileVersionUE5, explicitlySet = versions.bExplicitVer },
            customVersions = versions.CustomVersions?.Versions.Length ?? 0,
            texturePlatform = versions.Platform.ToString(),
            options = versions.Options,
            versionOptions = profile.VersionOptions,
            mappings = new
            {
                file = profile.MappingsContainer.Path,
                loaded = profile.LoadedMappingsFile,
                overridden = profile.MappingsContainer.Override,
                cooked = profile.CookedMappings is not null,
                editor = profile.EditorMappings is not null,
                onProvider = profile.Provider.MappingsContainer is not null
            },
            encryption = new
            {
                mainKey = profile.Encryption.IsValid,
                keys = profile.Encryption.Keys.Count,
                unknownKeys = profile.Encryption.UnknownKeys.Count
            },
            containers = new
            {
                mounted = profile.Provider.MountedVfs.Count,
                unloaded = profile.Provider.UnloadedVfs.Count,
                files = profile.Provider.Files.Count
            }
        };
    }

    /* Which profile answers for the path, which file that is, and the payload files beside it */
    private static BaseProfile? DescribeResolution(MeshDiagnosis diagnosis)
    {
        var path = diagnosis.Path;

        var profile = FindBaseProfileForPath(path, found: out var found);

        var provider = profile.Provider;
        var mounted = provider.TryGetGameFile(path, out var file) ? file : null;

        /* Every container mounting this same path, since the one that wins is whichever mounted
         * last and that is not always the cooked one */
        var duplicates = new List<DiagnosedFile>();

        if (mounted is not null && provider.Files.TryGetValues(mounted.Path, out var candidates))
        {
            foreach (var candidate in candidates)
            {
                duplicates.Add(DescribeFile(candidate, ReferenceEquals(candidate, mounted)));
            }
        }

        /* The payload files a cooked package keeps its heavy data in. A LOD that is not inlined
         * lives in one of these, and a build shipped without it (an optional chunk, a .uptnl that
         * was never installed) reads as a mesh with no vertices. */
        var payloads = new List<DiagnosedFile>();

        if (mounted is not null)
        {
            provider.Files.FindPayloads(mounted, out var uexp, out var ubulks, out var uptnls);

            if (uexp is not null) payloads.Add(DescribeFile(uexp, true));

            foreach (var ubulk in ubulks) payloads.Add(DescribeFile(ubulk, true));
            foreach (var uptnl in uptnls) payloads.Add(DescribeFile(uptnl, true));

            /* Looked for by name as well, since a payload the dictionary does not pair with its
             * package is still a file that exists */
            foreach (var extension in new[] { "uexp", "ubulk", "uptnl", "o.uasset", "o.uexp" })
            {
                var sibling = $"{mounted.PathWithoutExtension}.{extension}";

                if (provider.Files.TryGetValue(sibling, out var siblingFile) &&
                    payloads.All(existing => !string.Equals(existing.Path, siblingFile.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    payloads.Add(DescribeFile(siblingFile, true));
                }
            }
        }

        diagnosis.Resolution = new DiagnosedResolution(
            found,
            profile.Name,
            ReferenceEquals(profile, MainProfile),
            mounted is not null ? DescribeFile(mounted, true) : null,
            duplicates,
            payloads);

        return found && mounted is not null ? profile : null;
    }

    private static DiagnosedFile DescribeFile(GameFile file, bool chosen)
    {
        var container = "disk";
        string? containerGame = null;
        string? containerVersion = null;
        bool? containerEncrypted = null;

        if (file is VfsEntry { Vfs: { } vfs })
        {
            container = vfs.Name;
            containerGame = vfs.Game.ToString();
            containerVersion = $"{vfs.Ver.FileVersionUE4}/{vfs.Ver.FileVersionUE5}";

            if (vfs is IAesVfsReader aes) containerEncrypted = aes.IsEncrypted;
        }

        return new DiagnosedFile(
            file.Path,
            file.Extension,
            file.Size,
            chosen,
            container,
            containerGame,
            containerVersion,
            containerEncrypted,
            file.IsEncrypted,
            file.CompressionMethod.ToString(),
            file.IsUePackage,
            file.IsUePackagePayload);
    }

    /* The package as read: its versions, its flags, and every export in it by name and type.
     * Whether the geometry was cooked at all is decided here, by the flags. */
    private static IPackage? DescribePackage(MeshDiagnosis diagnosis, BaseProvider provider)
    {
        if (!provider.TryLoadPackage(diagnosis.Path, out var package))
        {
            diagnosis.Package = new DiagnosedPackage(false, null, null, null, false, null, false, false, false, null, null, null, 0, 0, 0, false, []);

            return null;
        }

        var summary = package.Summary;

        var exports = new List<DiagnosedExport>();

        for (var index = 0; index < package.ExportsLazy.Length; index++)
        {
            UObject? export = null;
            string? failure = null;

            try
            {
                export = package.ExportsLazy[index].Value;
            }
            catch (Exception exception)
            {
                failure = $"{exception.GetType().Name}: {exception.Message}";

                diagnosis.Errors.Add(new DiagnosticError($"export {index}", exception.GetType().FullName ?? "", exception.Message, exception.ToString()));
            }

            exports.Add(new DiagnosedExport(
                index,
                export?.Name,
                export?.ExportType,
                export?.Class?.GetPathName(),
                export?.Flags.ToString(),
                export?.Properties.Count,
                failure));
        }

        var customVersions = summary.CustomVersionContainer?.Versions
            .Select(version => new { key = version.Key.ToString(), version = version.Version })
            .ToArray();

        diagnosis.Package = new DiagnosedPackage(
            true,
            package.GetType().Name,
            package.Name,
            new { ue4 = summary.FileVersionUE.FileVersionUE4, ue5 = summary.FileVersionUE.FileVersionUE5, licensee = summary.FileVersionLicenseeUE.ToString() },
            summary.bUnversioned,
            summary.PackageFlags.ToString(),
            package.HasFlags(EPackageFlags.PKG_Cooked),
            package.HasFlags(EPackageFlags.PKG_FilterEditorOnly),
            package.HasFlags(EPackageFlags.PKG_UnversionedProperties),
            summary.SavedByEngineVersion?.ToString(),
            summary.CompatibleWithEngineVersion?.ToString(),
            customVersions,
            package.NameMap.Length,
            package.ImportMapLength,
            package.ExportMapLength,
            package.Mappings is not null,
            exports);

        return package;
    }

    /* Every mesh in the package, LOD by LOD, with what the endpoints would make of each one */
    private static void DescribeMeshes(MeshDiagnosis diagnosis, IPackage package)
    {
        foreach (var lazyExport in package.ExportsLazy)
        {
            UObject export;

            try
            {
                export = lazyExport.Value;
            }
            catch
            {
                /* Already written down by the package stage */
                continue;
            }

            if (diagnosis.ExportName is { Length: > 0 } wanted && !string.Equals(export.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (export)
            {
                case UStaticMesh staticMesh:
                    diagnosis.Meshes.Add(Attempt(diagnosis, $"static mesh {staticMesh.Name}", () => DescribeStaticMesh(staticMesh))
                        ?? new DiagnosedMesh(staticMesh.Name, "StaticMesh", true, false, null, []));
                    break;

                case USkeletalMesh skeletalMesh:
                    diagnosis.Meshes.Add(Attempt(diagnosis, $"skeletal mesh {skeletalMesh.Name}", () => DescribeSkeletalMesh(skeletalMesh))
                        ?? new DiagnosedMesh(skeletalMesh.Name, "SkeletalMesh", true, false, null, []));
                    break;
            }
        }
    }

    private static DiagnosedMesh DescribeStaticMesh(UStaticMesh staticMesh)
    {
        var renderData = staticMesh.RenderData;

        var lods = new List<DiagnosedLod>();

        if (renderData?.LODs is { } lodResources)
        {
            for (var index = 0; index < lodResources.Length; index++)
            {
                var lod = lodResources[index];
                var screenSize = index < renderData.ScreenSize.Length ? renderData.ScreenSize[index] : 0.0f;

                var vertexBuffer = lod.VertexBuffer;
                var positions = lod.PositionVertexBuffer;
                var indices = lod.IndexBuffer;

                /* The same call the endpoint makes, so the report says what it would have served */
                DiagnosedServe? served = null;
                string? serveFailure = null;

                try
                {
                    var built = BuildStaticLod(lod, index, screenSize);

                    served = built is null ? null : new DiagnosedServe(built.Vertices.Count, built.Indices.Length, built.Sections.Count, built.Vertices.NumTexCoords, 0);
                }
                catch (Exception exception)
                {
                    serveFailure = $"{exception.GetType().Name}: {exception.Message}";
                }

                lods.Add(new DiagnosedLod(
                    index,
                    lod.SkipLod,
                    lod.SkipLod ? SkipReason(vertexBuffer is null, indices?.Buffer is null, positions is null) : null,
                    null,
                    new
                    {
                        screenSize,
                        maxDeviation = lod.MaxDeviation,
                        positions = positions is null ? null : new { positions.NumVertices, read = positions.Verts.Length, positions.Stride },
                        vertices = vertexBuffer is null ? null : new { vertexBuffer.NumVertices, read = vertexBuffer.UV.Length, vertexBuffer.NumTexCoords, vertexBuffer.UseFullPrecisionUVs, vertexBuffer.UseHighPrecisionTangentBasis, vertexBuffer.Strides },
                        indices = indices?.Buffer is null ? null : new { count = indices.Buffer.Length },
                        colors = lod.ColorVertexBuffer is null ? null : new { lod.ColorVertexBuffer.NumVertices, read = lod.ColorVertexBuffer.Data.Length },
                        sections = lod.Sections.Select(section => new { section.MaterialIndex, section.FirstIndex, section.NumTriangles, section.MinVertexIndex, section.MaxVertexIndex, section.bEnableCollision, section.bCastShadow }).ToArray()
                    },
                    served,
                    serveFailure));
            }
        }

        var nanite = renderData?.NaniteResources;

        return new DiagnosedMesh(
            staticMesh.Name,
            "StaticMesh",
            false,
            renderData is not null,
            new
            {
                cooked = staticMesh.bCooked,
                bounds = renderData?.Bounds?.ToString(),
                lodCount = renderData?.LODs?.Length ?? 0,
                screenSizes = renderData?.ScreenSize,
                materials = staticMesh.StaticMaterials.Select(material => new
                {
                    slot = material.MaterialSlotName.Text,
                    imported = material.ImportedMaterialSlotName?.Text,
                    material = material.MaterialInterface?.Name
                }).ToArray(),
                nanite = nanite is null ? null : new
                {
                    pages = nanite.PageStreamingStates.Length,
                    rootPages = nanite.NumRootPages,
                    clusters = nanite.NumClusters,
                    inputTriangles = nanite.NumInputTriangles,
                    inputVertices = nanite.NumInputVertices,
                    inputTexCoords = nanite.NumInputTexCoords,
                    streamablePages = nanite.StreamablePages?.Header.ElementCount,
                    flags = nanite.ResourceFlags.ToString()
                }
            },
            lods);
    }

    private static string SkipReason(bool noVertexBuffer, bool noIndices, bool noPositions)
    {
        var reasons = new List<string>();

        if (noVertexBuffer) reasons.Add("no vertex buffer");
        if (noIndices) reasons.Add("no index buffer");
        if (noPositions) reasons.Add("no position buffer");

        return string.Join(", ", reasons);
    }

    private static DiagnosedMesh DescribeSkeletalMesh(USkeletalMesh skeletalMesh)
    {
        var boneInfo = skeletalMesh.ReferenceSkeleton?.FinalRefBoneInfo ?? [];

        var lods = new List<DiagnosedLod>();

        if (skeletalMesh.LODModels is { } lodModels)
        {
            for (var index = 0; index < lodModels.Length; index++)
            {
                var lod = lodModels[index];
                var buffer = lod.VertexBufferGPUSkin;

                DiagnosedServe? served = null;
                string? serveFailure = null;

                try
                {
                    var built = BuildLodModel(lod, boneInfo, index);

                    served = built is null ? null : new DiagnosedServe(built.Vertices.Count, built.Indices.Length, built.Sections.Count, built.Vertices.NumTexCoords, built.Vertices.BonesPerVertex);
                }
                catch (Exception exception)
                {
                    serveFailure = $"{exception.GetType().Name}: {exception.Message}";
                }

                /* Which of the four vertex arrays the cook wrote into. The endpoint reads two of
                 * them; a build cooking the packed ones comes back empty from it. */
                string? vertexArray = null;
                var vertexCount = 0;

                if (buffer is not null)
                {
                    if (buffer.VertsFloat is { Length: > 0 }) { vertexArray = "VertsFloat"; vertexCount = buffer.VertsFloat.Length; }
                    else if (buffer.VertsHalf is { Length: > 0 }) { vertexArray = "VertsHalf"; vertexCount = buffer.VertsHalf.Length; }
                    else if (buffer.VertsFloatPacked is { Length: > 0 }) { vertexArray = "VertsFloatPacked"; vertexCount = buffer.VertsFloatPacked.Length; }
                    else if (buffer.VertsHalfPacked is { Length: > 0 }) { vertexArray = "VertsHalfPacked"; vertexCount = buffer.VertsHalfPacked.Length; }
                }

                var influences = 0;
                var bones16 = false;

                if (buffer?.VertsFloat is { Length: > 0 } floats && floats[0].Infs is { } floatInfs) { influences = floatInfs.BoneIndex.Length; bones16 = floatInfs.bUse16BitBoneWeight; }
                else if (buffer?.VertsHalf is { Length: > 0 } halves && halves[0].Infs is { } halfInfs) { influences = halfInfs.BoneIndex.Length; bones16 = halfInfs.bUse16BitBoneWeight; }

                lods.Add(new DiagnosedLod(
                    index,
                    lod.SkipLod,
                    lod.SkipLod ? "no index buffer" : null,
                    vertexArray,
                    new
                    {
                        lod.NumVertices,
                        lod.NumTexCoords,
                        lod.Size,
                        vertexBuffer = buffer is null ? null : new
                        {
                            read = vertexCount,
                            buffer.NumTexCoords,
                            buffer.bUseFullPrecisionUVs,
                            buffer.bExtraBoneInfluences,
                            influencesPerVertex = influences,
                            sixteenBitWeights = bones16
                        },
                        indices = lod.Indices?.Buffer is null ? null : new { count = lod.Indices.Buffer.Length },
                        colors = lod.ColorVertexBuffer?.Data is null ? null : new { count = lod.ColorVertexBuffer.Data.Length },
                        requiredBones = lod.RequiredBones?.Length ?? 0,
                        activeBones = lod.ActiveBoneIndices?.Length ?? 0,
                        chunks = lod.Chunks?.Length ?? 0,
                        cloth = lod.HasClothData(),
                        morphTargetBuffers = lod.MorphTargetVertexInfoBuffers is not null,
                        sections = lod.Sections.Select(section => new
                        {
                            section.MaterialIndex,
                            section.BaseIndex,
                            section.NumTriangles,
                            section.BaseVertexIndex,
                            section.NumVertices,
                            bones = section.BoneMap?.Length ?? 0,
                            section.MaxBoneInfluences,
                            section.bUse16BitBoneIndex,
                            section.bDisabled,
                            softVertices = section.SoftVertices?.Length ?? 0,
                            clothAsset = section.CorrespondClothAssetIndex
                        }).ToArray()
                    },
                    served,
                    serveFailure));
            }
        }

        return new DiagnosedMesh(
            skeletalMesh.Name,
            "SkeletalMesh",
            false,
            skeletalMesh.LODModels is not null,
            new
            {
                lodCount = skeletalMesh.LODModels?.Length ?? 0,
                lodInfo = skeletalMesh.LODInfo?.Length ?? 0,
                bones = boneInfo.Length,
                skeleton = skeletalMesh.Skeleton?.Name,
                physicsAsset = skeletalMesh.PhysicsAsset?.Name,
                morphTargets = skeletalMesh.MorphTargets?.Length ?? 0,
                hasVertexColors = skeletalMesh.bHasVertexColors,
                bounds = skeletalMesh.ImportedBounds.ToString(),
                materials = (skeletalMesh.SkeletalMaterials ?? []).Select(material => new
                {
                    slot = material.MaterialSlotName.Text,
                    imported = material.ImportedMaterialSlotName?.Text,
                    material = material.Material?.Name
                }).ToArray(),
                nanite = skeletalMesh.NaniteResources is { } nanite ? new { pages = nanite.PageStreamingStates.Length, clusters = nanite.NumClusters } : null
            },
            lods);
    }

    /* What the report adds up to, in the order the questions are asked */
    private static void Conclude(MeshDiagnosis diagnosis)
    {
        var verdict = diagnosis.Verdict;

        if (diagnosis.Resolution is null)
        {
            verdict.Add("The path could not be resolved at all; see the errors.");

            return;
        }

        if (!diagnosis.Resolution.Found || diagnosis.Resolution.File is null)
        {
            verdict.Add("No profile has a file at this path. Check the path against the game's content tree, and that the container holding it is mounted (a key it needs may be missing).");

            return;
        }

        if (diagnosis.Package is not { Loaded: true })
        {
            verdict.Add("The file is there and the package would not load. This is nearly always the profile's engine version not matching the build; see the errors and the log for the reader's complaint.");

            return;
        }

        if (!diagnosis.Package.FilterEditorOnly)
        {
            verdict.Add("The package is not marked FilterEditorOnly, so it is an uncooked (editor) package: the reader stops before the render data, and there is no cooked geometry in it to serve. If a cooked copy is mounted from another container, see the duplicates.");
        }

        if (diagnosis.Meshes.Count == 0)
        {
            verdict.Add("The package loaded and holds no StaticMesh or SkeletalMesh export; see the export list.");

            return;
        }

        foreach (var mesh in diagnosis.Meshes)
        {
            if (mesh.Failed)
            {
                verdict.Add($"{mesh.Type} \"{mesh.Name}\" threw while being read; see the errors.");

                continue;
            }

            if (!mesh.HasGeometry)
            {
                verdict.Add($"{mesh.Type} \"{mesh.Name}\" has no cooked geometry at all (no render data). Either the package is uncooked, or the reader threw before reaching it and the export is only partly read.");

                continue;
            }

            var servedCount = 0;

            foreach (var lod in mesh.Lods)
            {
                if (lod.Served is not null)
                {
                    servedCount++;

                    continue;
                }

                if (lod.ServeFailure is not null)
                {
                    verdict.Add($"{mesh.Type} \"{mesh.Name}\" LOD {lod.Index} throws when built for serving: {lod.ServeFailure}");
                }
                else if (lod.SkipLod)
                {
                    verdict.Add($"{mesh.Type} \"{mesh.Name}\" LOD {lod.Index} is skipped: {lod.SkipReason}. A cooked LOD with these missing has its buffers in a payload file that did not read; see the payloads and the log.");
                }
                else if (lod.VertexArray is "VertsFloatPacked" or "VertsHalfPacked")
                {
                    verdict.Add($"{mesh.Type} \"{mesh.Name}\" LOD {lod.Index} was cooked into {lod.VertexArray}, which the LOD model endpoint does not read yet.");
                }
                else
                {
                    verdict.Add($"{mesh.Type} \"{mesh.Name}\" LOD {lod.Index} read as empty: no vertices, no indices or no influences came back; see the LOD's buffers and the log.");
                }
            }

            if (mesh.Lods.Count == 0)
            {
                verdict.Add($"{mesh.Type} \"{mesh.Name}\" has render data with no LODs in it.");
            }
            else if (servedCount == mesh.Lods.Count)
            {
                verdict.Add($"{mesh.Type} \"{mesh.Name}\": all {mesh.Lods.Count} LOD(s) read and would be served. If the import still fails, the problem is on the importer's side; compare its log against this.");
            }
            else if (servedCount > 0)
            {
                verdict.Add($"{mesh.Type} \"{mesh.Name}\": {servedCount} of {mesh.Lods.Count} LOD(s) would be served.");
            }
        }

        /* Unversioned properties are counted through the mappings, so a mappings file from another
         * build reads every property off by some, and the reader falls over on the first one that
         * makes no sense: a bool that is not 0 or 1, an array with a negative count. The mesh's
         * geometry sits after its properties and is never reached. */
        var propertiesFellOver = diagnosis.Log.Any(line =>
            line.Level is "Error" &&
            line.Exception is { } exception &&
            (exception.Contains("DeserializePropertiesUnversioned") || exception.Contains("Invalid bool value") || exception.Contains("Non-negative number required")));

        if (propertiesFellOver && diagnosis.Package.UnversionedProperties)
        {
            verdict.Add($"The properties would not read, and the package counts them through the mappings (unversioned). The mappings loaded are \"{diagnosis.LoadedMappings}\"; if that is not the usmap for this exact build, that is the whole problem: set the profile's mappings file to the build's own usmap.");
        }

        /* The properties read and the geometry behind them did not: the cooked mesh format of this
         * build is not quite the one its engine version says. Titles cooked off a main-branch engine
         * between two releases do this, and the reader adjusts for it through named version options
         * rather than a game id, so the switch to flip is a profile setting. */
        var geometryFellOver = !propertiesFellOver && diagnosis.Log.Any(line =>
            line.Level is "Error" &&
            line.Exception is { } exception &&
            (exception.Contains("FStaticMeshRenderData") || exception.Contains("FStaticMeshLODResources") || exception.Contains("FNaniteResources") ||
             exception.Contains("FStaticLODModel") || exception.Contains("USkeletalMesh.Deserialize") || exception.Contains("UStaticMesh.Deserialize")));

        if (geometryFellOver)
        {
            var overrides = diagnosis.VersionOptions.Count == 0
                ? "none set"
                : string.Join(", ", diagnosis.VersionOptions.Select(pair => $"{pair.Key}={pair.Value}"));

            verdict.Add($"The properties read fine and the cooked geometry after them did not: this build's mesh format differs from stock {diagnosis.Game}. That is adjusted per profile with version options (VersionOptions in the profile file; currently {overrides}). The exception names the reader that stopped.");

            if (diagnosis.Game == "GAME_UE5_1" && !diagnosis.VersionOptions.TryGetValue("StaticMesh.HasCardMostlyTwoSided", out var twoSided) | !twoSided)
            {
                verdict.Add("Known case: Fortnite 23.x is 5.1 carrying 5.2 formats. Set \"StaticMesh.HasCardMostlyTwoSided\": true (Lumen card data) and \"ExpressionInput.HasExpressionName\": false (material expression inputs in the editor companion) in the profile's VersionOptions.");
            }
        }

        if (diagnosis.Log.Any(line => line.Level is "Error" or "Fatal" || line.Message.Contains("bulk", StringComparison.OrdinalIgnoreCase)))
        {
            verdict.Add("The reader logged errors or bulk data complaints while reading this mesh; they are in the log section.");
        }
    }
}
