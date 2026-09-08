using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using Penumbra.GameData.Data;
using Penumbra.GameData.Enums;
using Penumbra.GameData.Structs;
using Penumbra.String;
using Penumbra.String.Classes;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Intoner.Objects.Collections;

internal enum PenumbraModGroupType
{
    Single,
    Multi,
    Combining,
    Imc,
}

[Flags]
internal enum PenumbraModLayout
{
    None = 0,
    Hide = 1 << 0,
    Space = 1 << 1,
    ParentHeader = 1 << 2,
    Separator = 1 << 3,
    DefaultClosed = 1 << 4,
    HideOptionLabel = 1 << 5,
}

internal sealed class PenumbraModData
{
    public static readonly PenumbraModData Empty = new(
        ImmutableDictionary<string, string>.Empty,
        ImmutableDictionary<string, string>.Empty,
        false);

    private PenumbraModData(
        IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, string> fileSwaps,
        bool hasManipulations)
    {
        Files = files;
        FileSwaps = fileSwaps;
        HasManipulations = hasManipulations;
    }

    public IReadOnlyDictionary<string, string> Files { get; }

    public IReadOnlyDictionary<string, string> FileSwaps { get; }

    public bool HasManipulations { get; }

    public static PenumbraModData Create(
        JsonElement? files,
        JsonElement? fileSwaps,
        JsonElement? manipulations)
        => new(
            NormalizeMap(files, true),
            NormalizeMap(fileSwaps, false),
            ContainsManipulations(manipulations));

    private static bool ContainsManipulations(JsonElement? manipulations)
    {
        if (manipulations is null
         || manipulations.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        if (manipulations.Value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Penumbra manipulation data is not a JSON array");
        }

        return manipulations.Value.GetArrayLength() > 0;
    }

    private static IReadOnlyDictionary<string, string> NormalizeMap(
        JsonElement? paths,
        bool requireRelativePath)
    {
        if (paths is null || paths.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        if (paths.Value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Penumbra redirect data is not a JSON object");
        }

        Dictionary<string, string> normalizedPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty redirect in paths.Value.EnumerateObject())
        {
            string gamePath = redirect.Name;
            if (!TryNormalizeGamePath(gamePath, out string normalizedGamePath))
            {
                continue;
            }

            if (redirect.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"Penumbra redirect for '{gamePath}' has no target path");
            }

            string targetPath = redirect.Value.GetString() ?? string.Empty;
            string normalizedTargetPath;
            if (requireRelativePath)
            {
                if (!Utf8RelPath.FromString(targetPath, out Utf8RelPath relativePath))
                {
                    throw new InvalidDataException($"Penumbra redirect for '{gamePath}' has an invalid relative path");
                }

                using (relativePath)
                {
                    normalizedTargetPath = relativePath.ToString();
                }
            }
            else
            {
                normalizedTargetPath = TextUtility.TrimOrEmpty(targetPath);
            }

            normalizedPaths.TryAdd(normalizedGamePath, normalizedTargetPath);
        }

        return normalizedPaths.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeGamePath(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        byte[] utf8Path = Encoding.UTF8.GetBytes(path);
        if (!Utf8GamePath.FromSpan(utf8Path, MetaDataComputation.All, out Utf8GamePath gamePath)
         || gamePath.IsEmpty)
        {
            return false;
        }

        using (gamePath)
        {
            normalizedPath = gamePath.ToString();
        }

        return true;
    }
}

internal sealed class PenumbraModOption
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Priority { get; init; }
    public PenumbraModLayout Layout { get; init; }
    public PenumbraModCondition? Condition { get; init; }
    public PenumbraModData Data { get; init; } = PenumbraModData.Empty;
}

internal sealed class PenumbraModGroup
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public PenumbraModGroupType Type { get; init; }
    public int Priority { get; init; }
    public ulong DefaultSettings { get; init; }
    public PenumbraModLayout Layout { get; init; }
    public PenumbraModCondition? Condition { get; init; }
    public Guid? ParentSettingId { get; init; }
    public IReadOnlyList<PenumbraModOption> Options { get; init; } = [];
    public IReadOnlyList<PenumbraModData> Containers { get; init; } = [];
}

internal sealed record PenumbraModMetadata(
    IReadOnlyList<PenumbraModGroup> Groups,
    PenumbraModData DefaultData);

internal sealed class PenumbraModCondition
{
    private enum Kind
    {
        True,
        False,
        And,
        Or,
        Not,
        Setting,
    }

    private PenumbraModCondition(
        Kind kind,
        Guid settingId,
        IReadOnlyList<PenumbraModCondition> children)
    {
        _kind = kind;
        _settingId = settingId;
        _children = children;
    }

    private readonly Kind _kind;
    private readonly Guid _settingId;
    private readonly IReadOnlyList<PenumbraModCondition> _children;

    public bool Evaluate(IReadOnlySet<Guid> enabledOptions)
        => _kind switch
        {
            Kind.True => true,
            Kind.False => false,
            Kind.And => _children.All(child => child.Evaluate(enabledOptions)),
            Kind.Or => _children.Any(child => child.Evaluate(enabledOptions)),
            Kind.Not => !_children[0].Evaluate(enabledOptions),
            Kind.Setting => enabledOptions.Contains(_settingId),
            _ => false,
        };

    public IEnumerable<Guid> EnumerateSettingIds()
    {
        if (_kind == Kind.Setting)
        {
            yield return _settingId;
        }

        foreach (PenumbraModCondition child in _children)
        {
            foreach (Guid settingId in child.EnumerateSettingIds())
            {
                yield return settingId;
            }
        }
    }

    public static PenumbraModCondition? Parse(JsonElement? json)
    {
        if (json is null || json.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        PenumbraModCondition reduced = ParseCore(json.Value).Reduce();
        return reduced._kind == Kind.True ? null : reduced;
    }

    private static PenumbraModCondition ParseCore(JsonElement condition)
    {
        if (condition.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidDataException("Penumbra condition is null");
        }

        if (condition.ValueKind != JsonValueKind.Object
         || !condition.TryGetProperty("Type", out JsonElement typeElement)
         || typeElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Penumbra condition does not specify a valid type");
        }

        string type = typeElement.GetString() ?? string.Empty;
        return type switch
        {
            "True" => new PenumbraModCondition(Kind.True, Guid.Empty, []),
            "False" => new PenumbraModCondition(Kind.False, Guid.Empty, []),
            "And" => ParseList(Kind.And, condition),
            "Or" => ParseList(Kind.Or, condition),
            "Not" => ParseNot(condition),
            "Setting" => ParseSetting(condition),
            _ => throw new InvalidDataException($"Penumbra condition type '{type}' is not supported"),
        };
    }

    private static PenumbraModCondition ParseList(Kind kind, JsonElement condition)
    {
        if (!condition.TryGetProperty("Conditions", out JsonElement conditionsElement))
        {
            throw new InvalidDataException($"Penumbra {kind} condition has no conditions array");
        }

        if (conditionsElement.ValueKind == JsonValueKind.Null)
        {
            return new PenumbraModCondition(kind, Guid.Empty, []);
        }

        if (conditionsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Penumbra {kind} condition has an invalid conditions array");
        }

        List<PenumbraModCondition> children = [];
        foreach (JsonElement childElement in conditionsElement.EnumerateArray())
        {
            children.Add(ParseCore(childElement));
        }

        return new PenumbraModCondition(kind, Guid.Empty, children);
    }

    private static PenumbraModCondition ParseNot(JsonElement condition)
    {
        if (!condition.TryGetProperty("Condition", out JsonElement childElement))
        {
            throw new InvalidDataException("Penumbra Not condition has no child condition");
        }

        return new PenumbraModCondition(Kind.Not, Guid.Empty, [ParseCore(childElement)]);
    }

    private static PenumbraModCondition ParseSetting(JsonElement condition)
    {
        if (!condition.TryGetProperty("Setting", out JsonElement settingElement)
         || settingElement.ValueKind != JsonValueKind.String
         || !Guid.TryParse(settingElement.GetString(), out Guid settingId)
         || settingId == Guid.Empty)
        {
            throw new InvalidDataException("Penumbra Setting condition does not reference a valid option ID");
        }

        return new PenumbraModCondition(Kind.Setting, settingId, []);
    }

    private PenumbraModCondition Reduce()
        => _kind switch
        {
            Kind.And => ReduceList(Kind.And),
            Kind.Or => ReduceList(Kind.Or),
            Kind.Not => ReduceNot(),
            _ => this,
        };

    private PenumbraModCondition ReduceList(Kind kind)
    {
        List<PenumbraModCondition> children = [];
        foreach (PenumbraModCondition child in _children)
        {
            PenumbraModCondition reduced = child.Reduce();
            if ((kind == Kind.And && reduced._kind == Kind.False)
             || (kind == Kind.Or && reduced._kind == Kind.True))
            {
                return reduced;
            }

            if ((kind == Kind.And && reduced._kind == Kind.True)
             || (kind == Kind.Or && reduced._kind == Kind.False))
            {
                continue;
            }

            if (reduced._kind == kind)
            {
                children.AddRange(reduced._children);
            }
            else
            {
                children.Add(reduced);
            }
        }

        return children.Count switch
        {
            0 => new PenumbraModCondition(
                kind == Kind.And ? Kind.True : Kind.False,
                Guid.Empty,
                []),
            1 => children[0],
            _ => new PenumbraModCondition(kind, Guid.Empty, children),
        };
    }

    private PenumbraModCondition ReduceNot()
    {
        PenumbraModCondition child = _children[0].Reduce();
        return child._kind switch
        {
            Kind.True => new PenumbraModCondition(Kind.False, Guid.Empty, []),
            Kind.False => new PenumbraModCondition(Kind.True, Guid.Empty, []),
            Kind.Not => child._children[0],
            _ => new PenumbraModCondition(Kind.Not, Guid.Empty, [child]),
        };
    }
}

internal sealed class PenumbraModMetadataReader
{
    private const uint SupportedFileVersion = 4;
    private const int MaxMultiOptions = 32;
    private const int MaxCombiningOptions = 8;
    private const int FileReadRetryCount = 3;
    private const int FileReadRetryDelayMilliseconds = 25;
    private const PenumbraModLayout GroupLayoutMask = PenumbraModLayout.Hide
        | PenumbraModLayout.Space
        | PenumbraModLayout.ParentHeader
        | PenumbraModLayout.DefaultClosed;
    private const PenumbraModLayout OptionLayoutMask = PenumbraModLayout.Hide
        | PenumbraModLayout.Space
        | PenumbraModLayout.Separator
        | PenumbraModLayout.HideOptionLabel;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed record DataModel(
        JsonElement? Files,
        JsonElement? FileSwaps,
        JsonElement? Manipulations);

    private sealed record OptionModel(
        JsonElement Id,
        JsonElement Name,
        string? Description,
        JsonElement Priority,
        JsonElement? Layout,
        JsonElement? Condition,
        JsonElement? Files,
        JsonElement? FileSwaps,
        JsonElement? Manipulations,
        JsonElement AttributeMask,
        JsonElement IsDisableSubMod);

    private sealed record ContainerModel(
        JsonElement? Files,
        JsonElement? FileSwaps,
        JsonElement? Manipulations);

    private sealed record GroupModel(
        JsonElement Id,
        JsonElement Name,
        string? Description,
        JsonElement Type,
        JsonElement Priority,
        JsonElement DefaultSettings,
        JsonElement? Layout,
        JsonElement? Condition,
        JsonElement ParentSetting,
        List<OptionModel?>? Options,
        JsonElement Containers,
        JsonElement Identifier,
        JsonElement DefaultEntry);

    private sealed record MetadataModel(
        JsonElement FileVersion,
        DataModel? DefaultData,
        List<GroupModel?>? Groups);

    private sealed record CacheEntry(
        string Signature,
        PenumbraModMetadata Metadata);

    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public PenumbraModMetadata Load(string modRootPath, CancellationToken cancellationToken)
    {
        string normalizedRootPath = PenumbraModPath.NormalizeRoot(modRootPath);
        string metadataPath = Path.Combine(normalizedRootPath, "meta.json");
        Exception? lastException = null;

        for (var attempt = 1; attempt <= FileReadRetryCount; ++attempt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string signature = BuildSignature(metadataPath);
                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(normalizedRootPath, out CacheEntry? cached)
                     && string.Equals(cached.Signature, signature, StringComparison.Ordinal))
                    {
                        return cached.Metadata;
                    }
                }

                PenumbraModMetadata metadata = ReadMetadata(metadataPath);
                string parsedSignature = BuildSignature(metadataPath);
                if (!string.Equals(signature, parsedSignature, StringComparison.Ordinal))
                {
                    throw new IOException($"Penumbra metadata changed while reading '{metadataPath}'");
                }

                lock (_cacheLock)
                {
                    _cache[normalizedRootPath] = new CacheEntry(parsedSignature, metadata);
                }

                return metadata;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                lastException = ex;
                if (attempt == FileReadRetryCount)
                {
                    break;
                }

                DelayBeforeRetry(cancellationToken);
            }
        }

        throw new InvalidDataException($"failed to read Penumbra metadata from '{metadataPath}'", lastException);
    }

    public void Invalidate(string modRootPath)
    {
        string normalizedRootPath = PenumbraModPath.NormalizeRoot(modRootPath);
        lock (_cacheLock)
        {
            _cache.Remove(normalizedRootPath);
        }
    }

    public void Clear()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
        }
    }

    private static PenumbraModMetadata ReadMetadata(string metadataPath)
    {
        using FileStream stream = ObjectAssetFileUtility.OpenSharedRead(metadataPath);
        MetadataModel model = JsonSerializer.Deserialize<MetadataModel>(stream, JsonOptions)
            ?? throw new InvalidDataException("Penumbra meta.json did not contain a JSON document");
        uint fileVersion = ReadUInt32(model.FileVersion, "metadata file version");
        if (fileVersion != SupportedFileVersion)
        {
            throw new InvalidDataException(
                $"Penumbra metadata version {fileVersion} is not supported; expected version {SupportedFileVersion}");
        }

        PenumbraModData defaultData = model.DefaultData is null
            ? PenumbraModData.Empty
            : PenumbraModData.Create(
                model.DefaultData.Files,
                model.DefaultData.FileSwaps,
                model.DefaultData.Manipulations);
        List<PenumbraModGroup> groups = [];
        foreach (GroupModel? groupModel in model.Groups ?? [])
        {
            if (groupModel is not null && CreateGroup(groupModel) is { } group)
            {
                groups.Add(group);
            }
        }

        ValidateMetadata(groups);
        return new PenumbraModMetadata(groups, defaultData);
    }

    private static PenumbraModGroup? CreateGroup(GroupModel model)
    {
        PenumbraModGroupType type = ParseGroupType(model.Type);
        if (type == PenumbraModGroupType.Imc && !IsValidImcGroup(model))
        {
            return null;
        }

        IReadOnlyList<PenumbraModOption> options = CreateOptions(type, model.Options);
        return new PenumbraModGroup
        {
            Id = ReadObjectGuid(model.Id, "group ID"),
            Name = ReadOptionalString(model.Name, GetDefaultGroupName(type), "group name"),
            Description = model.Description ?? string.Empty,
            Type = type,
            Priority = ReadInt32(model.Priority, "group priority"),
            DefaultSettings = ReadUInt64(model.DefaultSettings, "default settings", true),
            Layout = ParseLayout(model.Layout, GroupLayoutMask),
            Condition = PenumbraModCondition.Parse(model.Condition),
            ParentSettingId = ReadOptionalGuid(model.ParentSetting, null, "parent setting"),
            Options = options,
            Containers = CreateContainers(type, options.Count, model.Containers),
        };
    }

    private static IReadOnlyList<PenumbraModOption> CreateOptions(
        PenumbraModGroupType type,
        IReadOnlyList<OptionModel?>? models)
    {
        if (type == PenumbraModGroupType.Imc)
        {
            return CreateImcOptions(models);
        }

        int maximumCount = type switch
        {
            PenumbraModGroupType.Multi => MaxMultiOptions,
            PenumbraModGroupType.Combining => MaxCombiningOptions,
            _ => int.MaxValue,
        };
        return models?.Take(maximumCount).Select(model => CreateOption(RequireValue(model, "option"), type)).ToArray() ?? [];
    }

    private static IReadOnlyList<PenumbraModOption> CreateImcOptions(IReadOnlyList<OptionModel?>? models)
    {
        List<PenumbraModOption> options = [];
        ushort usedAttributeMask = 0;
        bool hasDisableOption = false;
        foreach (OptionModel? nullableModel in models ?? [])
        {
            OptionModel model = RequireValue(nullableModel, "option");
            ushort attributeMask = (ushort)(ReadUInt16Value(model.AttributeMask, "AttributeMask") & ImcEntry.AttributesMask);
            bool isDisableSubMod = ReadBoolean(model.IsDisableSubMod, "IsDisableSubMod");
            if ((isDisableSubMod && hasDisableOption)
             || (attributeMask & usedAttributeMask) != 0)
            {
                continue;
            }

            options.Add(CreateOption(model, PenumbraModGroupType.Imc));
            usedAttributeMask |= attributeMask;
            hasDisableOption |= isDisableSubMod;
        }

        return options;
    }

    private static IReadOnlyList<PenumbraModData> CreateContainers(
        PenumbraModGroupType type,
        int optionCount,
        JsonElement modelsElement)
    {
        if (type != PenumbraModGroupType.Combining)
        {
            return [];
        }

        IReadOnlyList<ContainerModel?>? models = modelsElement.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            JsonValueKind.Array => modelsElement.Deserialize<List<ContainerModel?>?>(JsonOptions),
            _ => throw new InvalidDataException("Penumbra combining containers are not an array"),
        };
        int requiredCount = 1 << optionCount;
        List<PenumbraModData> containers = models?
            .Take(requiredCount)
            .Select(model => CreateContainer(RequireValue(model, "combining data container")))
            .ToList() ?? [];
        while (containers.Count < requiredCount)
        {
            containers.Add(PenumbraModData.Empty);
        }

        return containers;
    }

    private static PenumbraModOption CreateOption(OptionModel model, PenumbraModGroupType groupType)
        => new()
        {
            Id = ReadObjectGuid(model.Id, "option ID"),
            Name = ReadOptionalString(model.Name, GetDefaultOptionName(groupType), "option name"),
            Description = model.Description ?? string.Empty,
            Priority = groupType == PenumbraModGroupType.Multi
                ? ReadInt32(model.Priority, "option priority")
                : 0,
            Layout = ParseLayout(model.Layout, OptionLayoutMask),
            Condition = PenumbraModCondition.Parse(model.Condition),
            Data = groupType is PenumbraModGroupType.Single or PenumbraModGroupType.Multi
                ? PenumbraModData.Create(model.Files, model.FileSwaps, model.Manipulations)
                : PenumbraModData.Empty,
        };

    private static PenumbraModData CreateContainer(ContainerModel model)
        => PenumbraModData.Create(model.Files, model.FileSwaps, model.Manipulations);

    private static bool IsValidImcGroup(GroupModel model)
    {
        if (!TryReadImcObject(model.Identifier, "identifier", out JsonElement identifier)
         || !TryReadImcObject(model.DefaultEntry, "default entry", out JsonElement defaultEntry)
         || !IsValidImcDefaultEntry(defaultEntry))
        {
            return false;
        }

        ObjectType objectType = ReadObjectType(identifier);
        if (objectType == ObjectType.Unknown)
        {
            return false;
        }

        PrimaryId primaryId = new(ReadUInt16(identifier, "PrimaryId"));
        ushort variant = ReadUInt16(identifier, "Variant");
        if (variant > byte.MaxValue)
        {
            return false;
        }

        EquipSlot equipSlot = ReadEquipSlot(identifier);
        ushort secondaryId = ReadUInt16(identifier, "SecondaryId");
        return objectType switch
        {
            ObjectType.Equipment or ObjectType.Accessory => (equipSlot.IsEquipment() || equipSlot.IsAccessory())
                && secondaryId == 0,
            ObjectType.DemiHuman => equipSlot.IsEquipment() || equipSlot.IsAccessory(),
            _ => equipSlot == EquipSlot.Unknown
              && !ItemData.AdaptOffhandImc(primaryId, out _),
        };
    }

    private static bool IsValidImcDefaultEntry(JsonElement entry)
    {
        byte materialId = ReadByte(entry, "MaterialId");
        _ = ReadByte(entry, "DecalId");
        _ = ReadByte(entry, "VfxId");
        _ = ReadByte(entry, "MaterialAnimationId");
        _ = ReadUInt16(entry, "AttributeMask");
        _ = ReadByte(entry, "SoundId");
        return materialId != 0;
    }

    private static ObjectType ReadObjectType(JsonElement identifier)
    {
        string value = ReadEnumName(identifier, "ObjectType");
        if (Enum.TryParse(value, true, out ObjectType objectType)
         && string.Equals(Enum.GetName(objectType), value, StringComparison.OrdinalIgnoreCase))
        {
            return objectType;
        }

        return ObjectType.Unknown;
    }

    private static EquipSlot ReadEquipSlot(JsonElement identifier)
    {
        if (!identifier.TryGetProperty("EquipSlot", out JsonElement property))
        {
            return EquipSlot.Unknown;
        }

        string value = ReadEnumValue(property, "EquipSlot");
        if (Enum.TryParse(value, true, out EquipSlot equipSlot)
         && string.Equals(Enum.GetName(equipSlot), value, StringComparison.OrdinalIgnoreCase))
        {
            return equipSlot;
        }

        return EquipSlot.Unknown;
    }

    private static string ReadEnumName(JsonElement container, string propertyName)
    {
        if (!container.TryGetProperty(propertyName, out JsonElement property))
        {
            return "Unknown";
        }

        return ReadEnumValue(property, propertyName);
    }

    private static string ReadEnumValue(JsonElement property, string propertyName)
    {
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Penumbra IMC {propertyName} is not a string");
        }

        return property.GetString() ?? string.Empty;
    }

    private static byte ReadByte(JsonElement container, string propertyName)
    {
        if (!container.TryGetProperty(propertyName, out JsonElement property))
        {
            return 0;
        }

        if (TryReadByte(property, out byte value))
        {
            return value;
        }

        throw new InvalidDataException($"Penumbra IMC {propertyName} is not a byte value");
    }

    private static bool TryReadByte(JsonElement property, out byte value)
    {
        value = 0;
        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetByte(out value),
            JsonValueKind.String => byte.TryParse(
                property.GetString(),
                provider: null,
                out value),
            _ => false,
        };
    }

    private static ushort ReadUInt16(JsonElement container, string propertyName)
    {
        if (!container.TryGetProperty(propertyName, out JsonElement property))
        {
            return 0;
        }

        if (TryReadUInt16(property, out ushort value))
        {
            return value;
        }

        throw new InvalidDataException($"Penumbra IMC {propertyName} is not an unsigned 16-bit value");
    }

    private static bool TryReadUInt16(JsonElement property, out ushort value)
    {
        value = 0;
        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetUInt16(out value),
            JsonValueKind.String => ushort.TryParse(
                property.GetString(),
                provider: null,
                out value),
            _ => false,
        };
    }

    private static ushort ReadUInt16Value(JsonElement value, string label)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return 0;
        }

        if (TryReadUInt16(value, out ushort result))
        {
            return result;
        }

        throw new InvalidDataException($"Penumbra IMC {label} is not an unsigned 16-bit value");
    }

    private static int ReadInt32(JsonElement value, string label)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return 0;
        }

        if (TryReadInt32(value, out int result))
        {
            return result;
        }

        throw new InvalidDataException($"Penumbra {label} is not a 32-bit integer");
    }

    private static bool TryReadInt32(JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out result),
            JsonValueKind.String => int.TryParse(
                value.GetString(),
                provider: null,
                out result),
            _ => false,
        };
    }

    private static uint ReadUInt32(JsonElement value, string label)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return 0;
        }

        uint result = 0;
        bool parsed = value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetUInt32(out result),
            JsonValueKind.String => uint.TryParse(
                value.GetString(),
                provider: null,
                out result),
            _ => false,
        };
        return parsed
            ? result
            : throw new InvalidDataException($"Penumbra {label} is not an unsigned 32-bit integer");
    }

    private static ulong ReadUInt64(JsonElement value, string label, bool allowNegative)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetUInt64(out ulong result))
            {
                return result;
            }

            if (allowNegative && value.TryGetInt64(out long signedResult))
            {
                return unchecked((ulong)signedResult);
            }
        }
        else if (value.ValueKind == JsonValueKind.String
              && ulong.TryParse(
                  value.GetString(),
                  provider: null,
                  out ulong result))
        {
            return result;
        }

        throw new InvalidDataException($"Penumbra {label} is not an unsigned 64-bit integer");
    }

    private static bool ReadBoolean(JsonElement value, string label)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Penumbra IMC {label} is not a boolean"),
        };
    }

    private static PenumbraModGroupType ParseGroupType(JsonElement type)
    {
        if (type.ValueKind != JsonValueKind.String)
        {
            return PenumbraModGroupType.Single;
        }

        return (type.GetString() ?? string.Empty).ToLowerInvariant() switch
        {
            "single" => PenumbraModGroupType.Single,
            "multi" => PenumbraModGroupType.Multi,
            "combining" => PenumbraModGroupType.Combining,
            "imc" => PenumbraModGroupType.Imc,
            _ => PenumbraModGroupType.Single,
        };
    }

    private static PenumbraModLayout ParseLayout(
        JsonElement? values,
        PenumbraModLayout allowedFlags)
    {
        if (values is null || values.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return PenumbraModLayout.None;
        }

        if (values.Value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Penumbra layout is not an array");
        }

        PenumbraModLayout layout = PenumbraModLayout.None;
        foreach (JsonElement value in values.Value.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            PenumbraModLayout flag = value.GetString()?.ToLowerInvariant() switch
            {
                "hide" => PenumbraModLayout.Hide,
                "space" => PenumbraModLayout.Space,
                "parentheader" => PenumbraModLayout.ParentHeader,
                "separator" => PenumbraModLayout.Separator,
                "defaultclosed" => PenumbraModLayout.DefaultClosed,
                "hideoptionlabel" => PenumbraModLayout.HideOptionLabel,
                _ => PenumbraModLayout.None,
            };
            layout |= flag;
        }

        return layout & allowedFlags;
    }

    private static Guid ReadObjectGuid(JsonElement value, string label)
        => value.ValueKind == JsonValueKind.Undefined ? Guid.NewGuid() : ReadGuid(value, label);

    private static Guid? ReadOptionalGuid(JsonElement value, Guid? defaultValue, string label)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return defaultValue;
        }

        return ReadGuid(value, label);
    }

    private static Guid ReadGuid(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out Guid result))
        {
            throw new InvalidDataException($"Penumbra {label} is not a GUID");
        }

        return result;
    }

    private static bool TryReadImcObject(JsonElement value, string label, out JsonElement result)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            result = default;
            return false;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Penumbra IMC {label} is not an object");
        }

        result = value;
        return true;
    }

    private static string ReadOptionalString(JsonElement value, string defaultValue, string label)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return defaultValue;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Penumbra {label} is not a string");
        }

        return value.GetString() ?? string.Empty;
    }

    private static string GetDefaultGroupName(PenumbraModGroupType type)
        => type is PenumbraModGroupType.Multi or PenumbraModGroupType.Combining
            ? "Group"
            : "Option";

    private static string GetDefaultOptionName(PenumbraModGroupType type)
        => type == PenumbraModGroupType.Imc ? "Part" : "Option";

    private static T RequireValue<T>(T? value, string label) where T : class
        => value ?? throw new InvalidDataException($"Penumbra {label} is null");

    private static void ValidateMetadata(IReadOnlyList<PenumbraModGroup> groups)
    {
        Dictionary<Guid, PenumbraModGroup> groupsById = [];
        Dictionary<Guid, (PenumbraModGroup Group, PenumbraModOption Option)> optionsById = [];
        HashSet<Guid> objectIds = [];
        foreach (PenumbraModGroup group in groups)
        {
            AddObjectId(group.Id, group.Name, objectIds);
            groupsById.Add(group.Id, group);
            foreach (PenumbraModOption option in group.Options)
            {
                AddObjectId(option.Id, option.Name, objectIds);
                optionsById.Add(option.Id, (group, option));
            }
        }

        ValidateParentSettings(groups, groupsById, optionsById);
        foreach (PenumbraModGroup group in groups)
        {
            ValidateCondition(group, null, group.Condition, optionsById);
            foreach (PenumbraModOption option in group.Options)
            {
                ValidateCondition(group, option, option.Condition, optionsById);
            }
        }
    }

    private static void AddObjectId(Guid id, string name, ISet<Guid> objectIds)
    {
        if (!objectIds.Add(id))
        {
            throw new InvalidDataException($"Penumbra group or option ID '{id}' is duplicated at '{name}'");
        }
    }

    private static void ValidateParentSettings(
        IReadOnlyList<PenumbraModGroup> groups,
        IReadOnlyDictionary<Guid, PenumbraModGroup> groupsById,
        IReadOnlyDictionary<Guid, (PenumbraModGroup Group, PenumbraModOption Option)> optionsById)
    {
        Dictionary<PenumbraModGroup, PenumbraModGroup> parentGroups = [];
        foreach (PenumbraModGroup group in groups)
        {
            if (group.ParentSettingId is not { } parentId)
            {
                continue;
            }

            PenumbraModGroup? parentGroup = groupsById.GetValueOrDefault(parentId);
            if (parentGroup is null && optionsById.TryGetValue(parentId, out var parentOption))
            {
                parentGroup = parentOption.Group;
            }

            if (parentGroup is null)
            {
                throw new InvalidDataException(
                    $"Penumbra parent setting '{parentId}' for group '{group.Name}' does not exist");
            }

            parentGroups.Add(group, parentGroup);
        }

        HashSet<PenumbraModGroup> validatedGroups = [];
        foreach (PenumbraModGroup group in groups)
        {
            HashSet<PenumbraModGroup> path = [];
            PenumbraModGroup current = group;
            while (!validatedGroups.Contains(current) && parentGroups.TryGetValue(current, out PenumbraModGroup? parent))
            {
                if (!path.Add(current))
                {
                    throw new InvalidDataException($"Penumbra parent setting for group '{group.Name}' creates a cycle");
                }

                current = parent;
            }

            validatedGroups.UnionWith(path);
        }
    }

    private static void ValidateCondition(
        PenumbraModGroup ownerGroup,
        PenumbraModOption? ownerOption,
        PenumbraModCondition? condition,
        IReadOnlyDictionary<Guid, (PenumbraModGroup Group, PenumbraModOption Option)> optionsById)
    {
        foreach (Guid settingId in condition?.EnumerateSettingIds() ?? [])
        {
            if (!optionsById.TryGetValue(settingId, out var target))
            {
                throw new InvalidDataException($"Penumbra condition references missing option ID '{settingId}'");
            }

            if (ReferenceEquals(ownerOption, target.Option))
            {
                throw new InvalidDataException($"Penumbra option '{ownerOption!.Name}' cannot depend on itself");
            }

            if (ownerOption is null && ReferenceEquals(ownerGroup, target.Group))
            {
                throw new InvalidDataException(
                    $"Penumbra group '{ownerGroup.Name}' cannot depend on one of its own options");
            }

            if (ownerOption is not null
             && ownerGroup.Type == PenumbraModGroupType.Single
             && ReferenceEquals(ownerGroup, target.Group))
            {
                throw new InvalidDataException(
                    $"Penumbra option '{ownerOption.Name}' cannot depend on another option in its single group");
            }
        }
    }

    private static string BuildSignature(string metadataPath)
    {
        FileInfo file = new(metadataPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Penumbra mod does not contain meta.json", metadataPath);
        }

        return $"{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    }

    private static void DelayBeforeRetry(CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(FileReadRetryDelayMilliseconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}

internal static class PenumbraModPath
{
    public static string NormalizeRoot(string modRootPath)
    {
        string normalizedRootPath = Path.GetFullPath(modRootPath);
        if (!normalizedRootPath.EndsWith(Path.DirectorySeparatorChar))
        {
            normalizedRootPath += Path.DirectorySeparatorChar;
        }

        return normalizedRootPath;
    }

    public static bool Contains(string normalizedRootPath, string fullPath)
    {
        string normalizedFullPath = Path.GetFullPath(fullPath);
        string trimmedFullPath = Path.TrimEndingDirectorySeparator(normalizedFullPath);
        string trimmedRootPath = Path.TrimEndingDirectorySeparator(normalizedRootPath);
        return string.Equals(trimmedFullPath, trimmedRootPath, StringComparison.OrdinalIgnoreCase)
            || normalizedFullPath.StartsWith(normalizedRootPath, StringComparison.OrdinalIgnoreCase);
    }
}
