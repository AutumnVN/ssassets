using System.Buffers.Binary;
using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AssetStudio;

const string HitSourceOutput = "./hitSource.json";
const string ComboSourceOutput = "./comboSource.json";

var bundlePattern = new Regex(@"^char_\d+(_(?:buff|breakout|combos|combosmonster|config|configs|fx|soldier|towerdefense|weapon|weaponmodel|weapons))?\.unity3d$", RegexOptions.Compiled);
var bulletClasses = new HashSet<string>(StringComparer.Ordinal)
{
    "AdventureBullet",
    "AdventureBomb",
    "AdventureProjectile",
    "AdventureTimeBomb",
    "AdventureBezierBullet",
    "AdventureBrokenLineBullet",
    "AdventureLaser",
    "AdventureAreaLaser",
    "AdventureVariableSizeBullet",
    "AdventureVerticalProjectile",
};
var weaponClasses = new HashSet<string>(bulletClasses, StringComparer.Ordinal)
{
    "AdventureBoomerang",
    "AdventureFlyShield",
    "AdventureRotateWeapon",
    "AdventureWindBlade",
};
var featureClasses = new HashSet<string>(StringComparer.Ordinal)
{
    "WeaponFeatureTraceTarget",
    "WeaponFeaturePenetratingActor",
    "WeaponFeaturePenetratingObstacle",
    "WeaponFeatureSplitWhenFinish",
};
var shapes = new[] { "rectangle", "circle", "fan" };

const double FixedScale = 4294967296.0;
const long FixedStep = (long)(FixedScale / 30);

var mode = args.Length > 0 ? args[0] : "all";
var source = args.Length > 1 ? args[1] : "./ResourceTool/output/Unpack/EN/win";

if (!Directory.Exists(source))
{
    Console.Error.WriteLine($"Source directory not found: {source}");
    return 1;
}

var allBundles = Directory.GetFiles(source, "*.unity3d")
    .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
    .ToList();
var bundles = allBundles
    .Where(path => bundlePattern.IsMatch(Path.GetFileName(path)))
    .ToList();

if (bundles.Count == 0)
{
    Console.Error.WriteLine($"No matching bundles found in {source}");
    return 1;
}

if (mode is "hitSource" or "all")
{
    var areaSources = ExtractAreaEffects(bundles, shapes);
    var weaponSources = ExtractWeapons(bundles, weaponClasses, bulletClasses, featureClasses, shapes);
    WriteHitSource(MergeSources(areaSources, weaponSources));
}

if (mode is "comboSource" or "all")
{
    WriteComboSource(ExtractCombos(bundles, allBundles, shapes));
}

return 0;

static Dictionary<int, List<HitSource>> ExtractAreaEffects(List<string> bundles, string[] shapes)
{
    var map = new Dictionary<int, List<HitSource>>();
    var resolved = 0;
    var unresolved = 0;

    foreach (var bundle in bundles)
    {
        var manager = new AssetsManager();

        try
        {
            manager.LoadFilesAndFolders(bundle);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"hitSource: failed {Path.GetFileName(bundle)}: {exception.Message}");
            continue;
        }

        var entities = new Dictionary<(long, long), Entity>();
        var attacks = new List<Attack>();
        var specials = new List<Special>();

        foreach (var file in manager.AssetsFileList)
        {
            var scripts = new Dictionary<long, string>();
            var names = new Dictionary<long, string>();

            foreach (var obj in file.Objects)
            {
                if (obj is MonoScript script && !string.IsNullOrEmpty(script.m_ClassName)) scripts[obj.m_PathID] = script.m_ClassName;
                if (obj is GameObject gameObject && !string.IsNullOrEmpty(gameObject.m_Name)) names[obj.m_PathID] = gameObject.m_Name;
            }

            foreach (var obj in file.Objects)
            {
                if (obj is not MonoBehaviour mb) continue;

                var className = GetClassName(mb, scripts);
                if (className == null) continue;

                var type = ReadType(mb);
                if (type == null) continue;

                if (className == "AreaEffectEntity")
                {
                    var copy = ReadBool(type, "copyBattleDataBeforeAttack");
                    if (!copy.HasValue) continue;

                    entities[GetGameObjectKey(mb)] = new Entity(
                        copy.Value,
                        ReadFloat(type, "duration") ?? 1,
                        ReadHitBox(type, shapes),
                        GetGameObjectName(mb, names));
                }
                else if (className is "LineAreaWeaponEntity" or "SpikeweedEneity")
                {
                    var hitDamageId = ReadInt(type, "hitDamageId");
                    if (!hitDamageId.HasValue || hitDamageId.Value <= 0) continue;

                    var duration = ReadFloat(type, "duration") ?? 1;
                    var hitInterval = ReadFloat(type, "hitInterval") ?? 1;
                    var hitImmediately = ReadBool(type, "hitImmediately") ?? false;
                    var firstHit = GetFirstHit(0, hitInterval, hitImmediately);

                    specials.Add(new Special(
                        new List<int> { (int)hitDamageId.Value },
                        GetGameObjectName(mb, names),
                        className,
                        duration,
                        firstHit,
                        hitInterval,
                        GetSourceHits(duration, firstHit, hitInterval)));
                }
                else if (className == "AreaEffect_AttackComponent" || className.StartsWith("AreaEffect_AttackComponent_", StringComparison.Ordinal))
                {
                    var list = ReadIntArray(type, "hitDamageIdList");
                    var single = ReadInt(type, "hitDamageId");
                    var ids = list.Count > 0
                        ? list
                        : single.HasValue && single.Value > 0
                            ? new List<int> { (int)single.Value }
                            : new List<int>();

                    if (ids.Count == 0) continue;

                    attacks.Add(new Attack(
                        GetGameObjectKey(mb),
                        ids,
                        ReadFloat(type, "hitDelay") ?? 0,
                        ReadFloat(type, "hitInterval") ?? 1,
                        ReadBool(type, "hitImmediately") ?? true,
                        ReadFloat(type, "time_TriggerStay") ?? 1,
                        ReadBool(type, "triggerEnter") ?? false,
                        ReadBool(type, "triggerStay") ?? false,
                        ReadBool(type, "triggerExit") ?? false,
                        ReadBool(type, "finish_TriggerEnter") ?? false,
                        ReadBool(type, "finish_TriggerExit") ?? false));
                }
            }
        }

        var attacksByEntity = new Dictionary<(long, long), List<Attack>>();

        foreach (var attack in attacks)
        {
            if (!attacksByEntity.TryGetValue(attack.GameObject, out var list))
            {
                list = new List<Attack>();
                attacksByEntity[attack.GameObject] = list;
            }

            list.Add(attack);
        }

        foreach (var (gameObject, entityAttacks) in attacksByEntity)
        {
            if (!entities.TryGetValue(gameObject, out var entity))
            {
                unresolved += entityAttacks.Count;
                continue;
            }

            resolved += entityAttacks.Count;

            foreach (var attack in entityAttacks)
            {
                var triggers = new List<string>();

                if (attack.TriggerEnter) triggers.Add("triggerEnter");
                if (attack.TriggerStay) triggers.Add("triggerStay");
                if (attack.TriggerExit) triggers.Add("triggerExit");
                if (attack.FinishTriggerEnter) triggers.Add("finish_TriggerEnter");
                if (attack.FinishTriggerExit) triggers.Add("finish_TriggerExit");

                var componentHits = triggers.Count == 0
                    ? GetSourceHits(entity.Duration, GetFirstHit(attack.HitDelay, attack.HitInterval, attack.HitImmediately), attack.HitInterval)
                    : GetComponentHits(entity.Duration, attack);

                for (var index = 0; index < attack.Ids.Count; index++)
                {
                    var idHits = attack.Ids.Count > 1
                        ? (int)Math.Ceiling((double)(componentHits - index) / attack.Ids.Count)
                        : componentHits;

                    var source = new HitSource
                    {
                        Class = "AreaEffectEntity",
                        Snapshot = !entity.Copy,
                        Name = entity.Name,
                        Alias = entity.Name,
                        Duration = entity.Duration,
                        HitDelay = GetFirstHit(attack.HitDelay, attack.HitInterval, attack.HitImmediately),
                        HitInterval = attack.HitInterval,
                        Hits = idHits,
                        Triggers = new List<string>(triggers),
                    };

                    ApplyShape(source, entity.HitBox);
                    AddSource(map, attack.Ids[index], source);
                }
            }
        }

        foreach (var special in specials)
        {
            foreach (var id in special.Ids)
            {
                AddSource(map, id, new HitSource
                {
                    Class = special.Class,
                    Snapshot = false,
                    Name = special.Name,
                    Alias = special.Name,
                    Duration = special.Duration,
                    HitDelay = special.HitDelay,
                    HitInterval = special.HitInterval,
                    Hits = special.Hits,
                    Triggers = new List<string>(),
                });
            }
        }

        manager.Clear();
    }

    Console.WriteLine($"hitSource: {resolved} attack component(s) matched an AreaEffectEntity, {unresolved} unresolved");
    return map;
}

static Dictionary<int, List<HitSource>> ExtractWeapons(List<string> bundles, HashSet<string> weaponClasses, HashSet<string> bulletClasses, HashSet<string> featureClasses, string[] shapes)
{
    var map = new Dictionary<int, List<HitSource>>();
    var resolved = 0;
    var unresolved = 0;

    foreach (var bundle in bundles)
    {
        var manager = new AssetsManager();

        try
        {
            manager.LoadFilesAndFolders(bundle);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"hitSource: failed {Path.GetFileName(bundle)}: {exception.Message}");
            continue;
        }

        var prefabs = new Dictionary<string, WeaponPrefab>(StringComparer.Ordinal);

        foreach (var file in manager.AssetsFileList)
        {
            var scripts = new Dictionary<long, string>();
            var names = new Dictionary<long, string>();

            foreach (var obj in file.Objects)
            {
                if (obj is MonoScript script && !string.IsNullOrEmpty(script.m_ClassName)) scripts[obj.m_PathID] = script.m_ClassName;
                if (obj is GameObject gameObject && !string.IsNullOrEmpty(gameObject.m_Name)) names[obj.m_PathID] = gameObject.m_Name;
            }

            foreach (var obj in file.Objects)
            {
                if (obj is not MonoBehaviour mb) continue;

                var className = GetClassName(mb, scripts);
                if (className == null) continue;

                var isWeapon = weaponClasses.Contains(className);
                if (!isWeapon && !featureClasses.Contains(className)) continue;

                var name = GetGameObjectName(mb, names);
                if (name == null)
                {
                    if (isWeapon) unresolved++;
                    continue;
                }

                var type = ReadType(mb);
                if (type == null) continue;

                if (!prefabs.TryGetValue(name, out var prefab))
                {
                    prefab = new WeaponPrefab();
                    prefabs[name] = prefab;
                }

                if (isWeapon)
                {
                    prefab.Ids = ParseBulletIds(type);
                    prefab.Source = ParseWeaponSource(className, type, bulletClasses, shapes);
                }
                else
                {
                    prefab.Features[className] = type;
                }
            }
        }

        foreach (var (name, prefab) in prefabs)
        {
            if (prefab.Ids == null || prefab.Ids.Count == 0) continue;
            if (prefab.Source == null) continue;

            resolved++;

            prefab.Source.Name = name;
            ApplyFeatures(prefab.Source, prefab.Features);

            foreach (var hit in prefab.Ids) AddSource(map, hit, prefab.Source);
        }

        manager.Clear();
    }

    Console.WriteLine($"hitSource: {resolved} weapon source(s) matched a GameObject, {unresolved} unresolved");
    return map;
}

static void WriteHitSource(Dictionary<int, List<HitSource>> map)
{
    var newKeys = new HashSet<string>(StringComparer.Ordinal);

    foreach (var sources in map.Values)
    {
        foreach (var source in sources) newKeys.Add(SourceKey(source.ToJson()));
    }

    var merged = new Dictionary<int, List<JsonObject>>();

    if (File.Exists(HitSourceOutput))
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(HitSourceOutput)) is JsonObject existing)
            {
                foreach (var (key, value) in existing)
                {
                    if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
                    if (value is not JsonObject entry || entry["sources"] is not JsonArray existingSources) continue;

                    var sources = new List<JsonObject>();

                    foreach (var node in existingSources)
                    {
                        if (node is not JsonObject source) continue;
                        if (newKeys.Contains(SourceKey(source))) continue;

                        sources.Add(source.DeepClone().AsObject());
                    }

                    if (sources.Count > 0) merged[id] = sources;
                }
            }
        }
        catch
        {
        }
    }

    foreach (var (id, sources) in map)
    {
        if (!merged.TryGetValue(id, out var list))
        {
            list = new List<JsonObject>();
            merged[id] = list;
        }

        var seen = new HashSet<string>(list.Select(source => source.ToJsonString()), StringComparer.Ordinal);

        foreach (var source in sources)
        {
            var json = source.ToJson();
            var key = json.ToJsonString();

            if (!seen.Add(key)) continue;

            list.Add(json);
        }
    }

    var root = new JsonObject();

    foreach (var id in merged.Keys.OrderBy(id => id))
    {
        var sources = merged[id];

        if (sources.Count == 0) continue;

        var array = new JsonArray();

        foreach (var source in sources) array.Add(source.DeepClone());

        root[id.ToString(CultureInfo.InvariantCulture)] = new JsonObject { ["sources"] = array };
    }

    if (root.Count == 0)
    {
        Console.Error.WriteLine("Hit source export failed: no hitDamage ids found");
        Environment.Exit(1);
    }

    WriteJson(HitSourceOutput, root);
    Console.WriteLine($"{root.Count} hitDamage ids");
}

static string SourceKey(JsonObject source)
{
    var className = source["class"]?.GetValue<string>() ?? "";
    var name = source["name"]?.GetValue<string>();

    return string.IsNullOrEmpty(name) ? source.ToJsonString() : $"{className}|{name}";
}

static Dictionary<string, JsonObject> ExtractCombos(List<string> bundles, List<string> allBundles, string[] shapes)
{
    var files = bundles
        .Where(bundle => Path.GetFileName(bundle).Contains("_combos", StringComparison.Ordinal))
        .ToArray();

    if (files.Length == 0)
    {
        Console.Error.WriteLine("comboSource: no combo bundles found");
        return new Dictionary<string, JsonObject>(StringComparer.Ordinal);
    }

    var probe = LoadBundles(files);

    if (probe == null) return new Dictionary<string, JsonObject>(StringComparer.Ordinal);

    var draft = ParseComboGroups(probe, shapes, false, out _, out var draftUnresolved);
    probe.Clear();

    if (draftUnresolved > 0)
        Console.Error.WriteLine($"comboSource: {draftUnresolved} clip reference(s) unresolved while collecting prefab references");

    var externalFiles = new HashSet<string>(StringComparer.Ordinal);
    var unresolved = 0;

    foreach (var group in draft.Values) CollectUnresolvedFiles(group, externalFiles, ref unresolved);

    var index = BuildCabIndex(allBundles);
    var owners = new List<string>();
    var mapped = 0;

    foreach (var cab in externalFiles.OrderBy(cab => cab, StringComparer.Ordinal))
    {
        if (index.TryGetValue(cab, out var matches))
        {
            mapped++;
            owners.AddRange(matches);
        }
        else
        {
            Console.Error.WriteLine($"comboSource: no bundle contains {cab}");
        }
    }

    Console.WriteLine($"comboSource: {mapped}/{externalFiles.Count} external files mapped to bundles");

    var manager = LoadBundles(files.Concat(owners).Distinct(StringComparer.Ordinal).ToArray());

    if (manager == null) return new Dictionary<string, JsonObject>(StringComparer.Ordinal);

    var result = ParseComboGroups(manager, shapes, true, out var resolved, out var missing);
    manager.Clear();

    var prefabs = 0;

    foreach (var group in result.Values) CollectUnresolvedFiles(group, externalFiles, ref prefabs);

    Console.WriteLine($"comboSource: {resolved} clip reference(s) resolved, {missing} unresolved");
    Console.WriteLine($"comboSource: {prefabs} prefab reference(s) unresolved");

    return result;
}

static AssetsManager? LoadBundles(string[] files)
{
    var manager = new AssetsManager();

    try
    {
        manager.LoadFilesAndFolders(files);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"comboSource: failed to load bundles: {exception.Message}");
        manager.Clear();
        return null;
    }

    return manager;
}

static Dictionary<string, JsonObject> ParseComboGroups(AssetsManager manager, string[] shapes, bool report, out int resolved, out int unresolved)
{
    var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
    var clips = new Dictionary<(SerializedFile File, long PathId), ComboClipAsset>();
    var groups = new List<MonoBehaviour>();

    resolved = 0;
    unresolved = 0;

    foreach (var file in manager.AssetsFileList)
    {
        var scripts = new Dictionary<long, string>();

        foreach (var obj in file.Objects)
            if (obj is MonoScript script && !string.IsNullOrEmpty(script.m_ClassName))
                scripts[obj.m_PathID] = script.m_ClassName;

        foreach (var obj in file.Objects)
        {
            if (obj is not MonoBehaviour mb) continue;

            var className = GetClassName(mb, scripts);

            if (className == "ComboClip")
            {
                var type = ReadType(mb);
                if (type == null) continue;

                clips[(file, mb.m_PathID)] = new ComboClipAsset(mb.m_Name, type);
            }
            else if (className == "ComboGroup")
            {
                groups.Add(mb);
            }
        }
    }

    foreach (var group in groups)
    {
        var type = ReadType(group);
        if (type == null) continue;

        var json = BuildComboGroup(group, type, clips, shapes, ref resolved, ref unresolved);
        if (json == null) continue;

        var key = group.m_Name;

        if (result.ContainsKey(key))
        {
            key = $"{group.m_Name}#{group.m_PathID}";
            if (report) Console.Error.WriteLine($"comboSource: duplicate {group.m_Name} in {Path.GetFileName(group.assetsFile.originalPath)} kept as {key}");
        }

        result[key] = json;
    }

    return result;
}

static Dictionary<string, List<string>> BuildCabIndex(List<string> bundles)
{
    var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    var failures = new List<string>();

    Parallel.ForEach(bundles, bundle =>
    {
        string[] nodes;

        try
        {
            nodes = ReadBundleNodes(bundle);
        }
        catch (Exception exception)
        {
            lock (failures) failures.Add($"{Path.GetFileName(bundle)}: {exception.Message}");
            return;
        }

        lock (index)
        {
            foreach (var node in nodes)
            {
                if (!index.TryGetValue(node, out var owners))
                {
                    owners = new List<string>();
                    index[node] = owners;
                }

                owners.Add(bundle);
            }
        }
    });

    if (failures.Count > 0)
        Console.Error.WriteLine($"comboSource: {failures.Count} bundle(s) could not be indexed, first: {failures[0]}");

    return index;
}

static string[] ReadBundleNodes(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
    using var reader = new BinaryReader(stream, Encoding.UTF8);

    var signature = ReadNullString(reader);

    if (signature != "UnityFS") throw new InvalidDataException($"signature {signature}");

    var version = BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));

    if (version is < 6 or > 9) throw new InvalidDataException($"version {version}");

    ReadNullString(reader);
    ReadNullString(reader);
    reader.ReadBytes(8);

    var compressedSize = BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
    var uncompressedSize = BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
    var flags = BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));

    if (version >= 7 || (flags & 0x200) != 0) stream.Position = (stream.Position + 15) & ~15L;
    if ((flags & 0x80) != 0) stream.Position = stream.Length - compressedSize;

    var compressed = reader.ReadBytes((int)compressedSize);
    var compression = (CompressionType)(flags & 0x3F);
    byte[] blocksInfo;

    if (compression == CompressionType.None)
    {
        blocksInfo = compressed;
    }
    else
    {
        blocksInfo = new byte[uncompressedSize];
        var error = string.Empty;
        var written = BundleDecompressionHelper.DecompressBlock(compression, compressed, blocksInfo, ref error);

        if (written != uncompressedSize) throw new InvalidDataException($"blocks info {written} of {uncompressedSize} bytes: {error}");
    }

    using var infoReader = new BinaryReader(new MemoryStream(blocksInfo), Encoding.UTF8);
    infoReader.ReadBytes(16);

    var blockCount = BinaryPrimitives.ReadInt32BigEndian(infoReader.ReadBytes(4));

    if (blockCount < 0) throw new InvalidDataException($"block count {blockCount}");

    infoReader.ReadBytes(blockCount * 10);

    var nodeCount = BinaryPrimitives.ReadInt32BigEndian(infoReader.ReadBytes(4));

    if (nodeCount < 0) throw new InvalidDataException($"node count {nodeCount}");

    var nodes = new string[nodeCount];

    for (var i = 0; i < nodeCount; i++)
    {
        infoReader.ReadBytes(version >= 6 ? 16 : 8);
        infoReader.ReadBytes(4);
        nodes[i] = ReadNullString(infoReader);
    }

    return nodes;
}

static string ReadNullString(BinaryReader reader)
{
    var bytes = new List<byte>();
    byte value;

    while ((value = reader.ReadByte()) != 0) bytes.Add(value);

    return Encoding.UTF8.GetString(bytes.ToArray());
}

static void CollectUnresolvedFiles(JsonNode? node, HashSet<string> files, ref int count)
{
    switch (node)
    {
        case JsonObject obj:
            foreach (var (key, value) in obj)
            {
                if (key.EndsWith("Unresolved", StringComparison.Ordinal) && value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
                {
                    files.Add(text[..text.LastIndexOf(':')]);
                    count++;
                }
                else
                {
                    CollectUnresolvedFiles(value, files, ref count);
                }
            }

            break;
        case JsonArray array:
            foreach (var item in array) CollectUnresolvedFiles(item, files, ref count);
            break;
    }
}

static JsonObject? BuildComboGroup(
    MonoBehaviour group,
    OrderedDictionary type,
    Dictionary<(SerializedFile File, long PathId), ComboClipAsset> clips,
    string[] shapes,
    ref int resolved,
    ref int unresolved)
{
    var combos = new JsonArray();
    var order = new List<(SerializedFile File, long PathId)>();

    if (type.Contains("combos") && type["combos"] is IEnumerable comboList)
    {
        foreach (var item in comboList)
        {
            if (item is not OrderedDictionary combo) continue;

            var entry = new JsonObject();
            var actionTag = ReadString(combo, "actionTag");
            if (!string.IsNullOrEmpty(actionTag)) entry["actionTag"] = actionTag;
            if (ReadInt(combo, "comboClipMode") == 1) entry["loop"] = true;

            var chain = new JsonArray();

            if (combo.Contains("compositeComboClips") && combo["compositeComboClips"] is IEnumerable compositeList)
            {
                foreach (var element in compositeList)
                {
                    if (element is not OrderedDictionary composite) continue;

                    if (IsEmptyPPtr(composite, "comboClip")) continue;

                    if (!TryResolvePPtr<MonoBehaviour>(composite, "comboClip", group.assetsFile, out var clipObject) ||
                        clipObject == null ||
                        clipObject.assetsFile == null ||
                        !clips.TryGetValue((clipObject.assetsFile, clipObject.m_PathID), out var clip))
                    {
                        unresolved++;
                        continue;
                    }

                    resolved++;

                    var key = (clipObject.assetsFile, clipObject.m_PathID);
                    if (!order.Contains(key)) order.Add(key);

                    var link = new JsonObject { ["name"] = clip.Name };
                    var eventGroupTag = ReadString(composite, "eventGroupTag");
                    if (!string.IsNullOrEmpty(eventGroupTag)) link["eventGroupTag"] = eventGroupTag;

                    chain.Add(link);
                }
            }

            if (chain.Count == 0) continue;

            entry["clips"] = chain;
            combos.Add(entry);
        }
    }

    if (order.Count == 0) return null;

    var clipMap = new JsonObject();

    foreach (var key in order)
    {
        var clip = clips[key];
        clipMap[clip.Name] = BuildComboClip(clip, key.File, shapes);
    }

    var result = new JsonObject();

    if (combos.Count > 0) result["combos"] = combos;
    result["clips"] = clipMap;

    return result;
}

static JsonObject BuildComboClip(ComboClipAsset clip, SerializedFile? assetsFile, string[] shapes)
{
    var type = clip.Type;
    var result = new JsonObject();

    AddNumber(result, "animationLength", ReadFloat(type, "animationLength"));
    AddNumber(result, "playSpeed", ReadFloat(type, "playSpeed"));
    AddNumber(result, "transitionIn", ReadFloat(type, "transitionIn"), true);
    AddNumber(result, "transitionOut", ReadFloat(type, "transitionOut"), true);
    AddNumber(result, "skillTakeEffectNormalizedTime", ReadFloat(type, "skillTakeEffectNormalizedTime"));
    AddNumber(result, "allowOtherSkillNormalizedTime", ReadFloat(type, "skillAllowOtherSkillExecuteNormalizedTime"));
    AddNumber(result, "canExitNormalizedTime", ReadFloat(type, "canExitNormalizedTime"));

    var phaseRange = ReadVector2(type, "comboPhaseRange");
    if (phaseRange != null) result["comboPhaseRange"] = ToJson(phaseRange);

    AddNumber(result, "actionAnimType", ReadInt(type, "actionAnimType"), true);

    if (TryGetPPtr(type, "animationClip", out var clipFileId, out var clipPathId))
        result["animationClipRef"] = DescribePPtr(assetsFile, clipFileId, clipPathId);

    if (ReadBool(type, "enableRootMotion") == true)
    {
        result["enableRootMotion"] = true;

        AddCurve(result, "rootMotionSides", type, "rootMotionSides");
        AddCurve(result, "rootMotionVertical", type, "rootMotionVertical");
        AddCurve(result, "rootMotionForward", type, "rootMotionForward");

        var multiplier = ReadVector3(type, "rootMotionMultiplier");
        if (multiplier != null) result["rootMotionMultiplier"] = ToJson(multiplier);

        AddNumber(result, "rootMotionCheckDistance", NonZero(ReadFloat(type, "rootMotionCheckDistance")));
        AddNumber(result, "rootMotionCheckDistanceLimitUp", NonZero(ReadFloat(type, "rootMotionCheckDistanceLimitUp")));
    }

    if (ReadBool(type, "enableRootMotionRotate") == true)
    {
        result["enableRootMotionRotate"] = true;

        AddCurve(result, "rootMotionRotateQuaternionX", type, "rootMotionRotateQuaternionX");
        AddCurve(result, "rootMotionRotateQuaternionY", type, "rootMotionRotateQuaternionY");
        AddCurve(result, "rootMotionRotateQuaternionZ", type, "rootMotionRotateQuaternionZ");
        AddCurve(result, "rootMotionRotateQuaternionW", type, "rootMotionRotateQuaternionW");
    }

    var skins = BuildSkinEvents(type, "comboEventSkins", assetsFile, shapes);
    if (skins.Count > 0) result["skins"] = skins;

    var eventGroups = new JsonArray();

    if (type.Contains("EventGroups") && type["EventGroups"] is IEnumerable groupList)
    {
        foreach (var item in groupList)
        {
            if (item is not OrderedDictionary group) continue;

            var inner = BuildSkinEvents(group, "ComboEventSkins", assetsFile, shapes);
            if (inner.Count == 0) continue;

            var entry = new JsonObject();
            var tag = ReadString(group, "tag");
            if (!string.IsNullOrEmpty(tag)) entry["tag"] = tag;
            entry["skins"] = inner;

            eventGroups.Add(entry);
        }
    }

    if (eventGroups.Count > 0) result["eventGroups"] = eventGroups;

    return result;
}

static JsonObject BuildSkinEvents(OrderedDictionary owner, string field, SerializedFile? assetsFile, string[] shapes)
{
    var skins = new JsonObject();

    if (!owner.Contains(field) || owner[field] is not IEnumerable skinList) return skins;

    foreach (var item in skinList)
    {
        if (item is not OrderedDictionary skin) continue;

        var skinId = ReadInt(skin, "skinId");
        if (!skinId.HasValue) continue;

        var events = new JsonArray();

        if (skin.Contains("comboEvents") && skin["comboEvents"] is IEnumerable eventList)
        {
            foreach (var element in eventList)
            {
                if (element is not OrderedDictionary data) continue;

                var json = BuildComboEvent(data, assetsFile, shapes);
                if (json != null) events.Add(json);
            }
        }

        if (events.Count == 0) continue;

        skins[skinId.Value.ToString(CultureInfo.InvariantCulture)] = events;
    }

    return skins;
}

static JsonObject? BuildComboEvent(OrderedDictionary type, SerializedFile? assetsFile, string[] shapes)
{
    var eventType = ReadInt(type, "type") ?? 0;

    var name = eventType switch
    {
        0 => "Custom",
        1 => "Hit",
        2 => "Shoot",
        3 => "AddBuff",
        5 => "AreaEffect",
        7 => "AddShield",
        10 => "FrozenTime",
        20 => "TriggerCharGamePlayLogicCommonEvent",
        _ => null,
    };

    if (name == null) return null;

    var result = new JsonObject { ["type"] = name };

    var range = ReadVector2(type, "activeNormalizedTimeRange");
    if (range != null)
    {
        result["start"] = range[0];
        result["end"] = range[1];
    }

    if (eventType == 0) return result;

    var isHit = eventType == 1;
    var isShoot = eventType == 2;
    var isAreaEffect = eventType == 5;

    if (ReadBool(type, "playOnceInLoopMode") == true) result["playOnceInLoop"] = true;

    var precondition = ReadInt(type, "clipPreconditionType");
    if (precondition is 1 or 2)
    {
        result["precondition"] = precondition == 1 ? "only" : "except";
        AddMonoBehaviourPPtr(result, "preconditionClip", type, "preconditionClip", assetsFile);
    }

    if (isHit)
    {
        var hitDamageId = ReadInt(type, "hitDamageId");
        if (hitDamageId.HasValue && hitDamageId.Value > 0) result["hitDamageId"] = hitDamageId.Value;

        var hitFlags = ReadInt(type, "hitFlags");
        if (hitFlags.HasValue && hitFlags.Value != 0)
        {
            var flags = new JsonArray();

            foreach (var (value, flagName) in new (long Value, string Name)[]
            {
                (0x1, "knockBack"),
                (0x2, "airBorne"),
                (0x8, "changeArea"),
                (0x10, "addBuffForSelf"),
                (0x20, "addBuffForTarget"),
                (0x40, "removeBuffForSelf"),
                (0x80, "removeBuffForTarget"),
                (0x1000, "stiff"),
                (0x10000, "kill"),
                (0x20000, "noDiffDamage"),
                (0x80000, "killDestructible"),
                (0x10000000, "damageFreezeFrame"),
            })
            {
                if ((hitFlags.Value & value) != 0) flags.Add(flagName);
            }

            result["hitFlags"] = flags;
        }
    }

    if (isHit || isShoot)
    {
        var shape = ReadInt(type, "hitBoxShape");

        if (shape.HasValue && shape.Value >= 0 && shape.Value < shapes.Length)
        {
            result["shape"] = shapes[(int)shape.Value];

            switch (shape.Value)
            {
                case 0:
                    AddNumber(result, "width", ReadFloat(type, "hitBoxWidth"));
                    AddNumber(result, "length", ReadFloat(type, "hitBoxLength"));
                    break;
                case 1:
                    AddNumber(result, "radius", ReadFloat(type, "hitBoxRadius"));
                    AddNumber(result, "innerRadius", ReadFloat(type, "hitBoxInnerRadius"));
                    break;
                case 2:
                    AddNumber(result, "radius", ReadFloat(type, "hitBoxRadius"));
                    AddNumber(result, "innerRadius", ReadFloat(type, "hitBoxInnerRadius"));
                    AddNumber(result, "angle", ReadFloat(type, "hitBoxAngle"));
                    break;
            }
        }

        var gameDataHitBox = ReadInt(type, "hitBoxConfigureWithGameData");
        if (gameDataHitBox.HasValue && gameDataHitBox.Value != 0)
        {
            result["hitBoxFromGameData"] = gameDataHitBox.Value switch
            {
                10 => "vision",
                11 => "visionDistance",
                12 => "visionAngle",
                20 => "hear",
                31 => "body",
                _ => gameDataHitBox.Value.ToString(CultureInfo.InvariantCulture),
            };
        }

        var hitBoxOffset = ReadVector2(type, "hitBoxOffset");
        if (hitBoxOffset != null && (hitBoxOffset[0] != 0 || hitBoxOffset[1] != 0)) result["offset"] = ToJson(hitBoxOffset);

        AddNumber(result, "offsetY", NonZero(ReadFloat(type, "hitBoxOffsetY")));
        AddNumber(result, "rotation", NonZero(ReadFloat(type, "hitBoxRotation")));

        if (ReadBool(type, "offsetFollowActor") == true) result["followActor"] = true;
        if (ReadBool(type, "areaWithActorScale") == false) result["areaWithActorScale"] = false;
        if (ReadBool(type, "lockHitBoxTransformOnEnd") == false) result["lockHitBoxOnEnd"] = false;

        AddNumber(result, "rotationOnEnd", NonZero(ReadFloat(type, "hitBoxRotationOnEnd")));

        var offsetOnEnd = ReadVector2(type, "hitBoxOffsetOnEnd");
        if (offsetOnEnd != null && (offsetOnEnd[0] != 0 || offsetOnEnd[1] != 0)) result["offsetOnEnd"] = ToJson(offsetOnEnd);

        if (ReadBool(type, "enableHitBoxCurveMotion") == true)
        {
            var controlPoints = new JsonArray();
            var point1 = ReadVector2(type, "hitBoxCurveMotionControlPoint1");
            var point2 = ReadVector2(type, "hitBoxCurveMotionControlPoint2");

            controlPoints.Add(point1 == null ? new JsonArray() : ToJson(point1));
            controlPoints.Add(point2 == null ? new JsonArray() : ToJson(point2));
            result["curveControlPoints"] = controlPoints;
        }
    }

    if (isHit)
    {
        var immuneDuration = ReadFloat(type, "damageImmuneDuration");
        if (immuneDuration.HasValue && immuneDuration.Value != -1) result["damageImmuneDuration"] = immuneDuration.Value;

        AddNumber(result, "damageDetectInterval", NonZero(ReadFloat(type, "damageDetectInterval")));

        if (ReadBool(type, "blockBullet") == true) result["blockBullet"] = true;
    }

    if (isShoot)
    {
        AddGameObjectPPtr(result, "bulletPrefab", type, "bulletPrefab", assetsFile);

        var bulletCount = ReadInt(type, "bulletCount");
        if (bulletCount.HasValue && bulletCount.Value > 1) result["bulletCount"] = bulletCount.Value;

        var reduceAmmo = ReadInt(type, "reduceAmmoCount");
        if (reduceAmmo.HasValue && reduceAmmo.Value > 0) result["reduceAmmoCount"] = reduceAmmo.Value;

        var shootStyle = ReadInt(type, "multipleShootStyle");
        if (shootStyle.HasValue && shootStyle.Value != 0)
        {
            result["multipleShootStyle"] = shootStyle.Value switch
            {
                1 => "fromCenterToSide",
                2 => "fromSideToCenter",
                3 => "fromLeftToRight",
                4 => "fromRightToLeft",
                5 => "randomDirection",
                _ => null,
            };
        }

        if (ReadBool(type, "useShootDeviationCalcScatteringRadius") == true) result["scatterFromDeviation"] = true;

        AddNumber(result, "scatterRadius", NonZero(ReadFloat(type, "shootScatteringRadius")));
        AddNumber(result, "scatterDirectionOffset", NonZero(ReadFloat(type, "multipleShootBulletDirectionOffset")));

        if (ReadBool(type, "alignShoot") == true)
        {
            result["alignShoot"] = true;
            AddNumber(result, "alignShootDelay", NonZero(ReadFloat(type, "alignShootDelay")));
        }

        AddNumber(result, "range", ReadFloat(type, "range"));
        AddNumber(result, "shootInnerRadius", NonZero(ReadFloat(type, "innerRadius")));

        var aimType = ReadInt(type, "aimType");
        if (aimType.HasValue && aimType.Value != 0)
        {
            result["aimType"] = aimType.Value switch
            {
                1 => "forward",
                2 => "position",
                3 => "target",
                _ => null,
            };
        }

        var bulletDirection = ReadInt(type, "bulletDirectionType");
        if (bulletDirection.HasValue && bulletDirection.Value != 1)
        {
            result["bulletDirection"] = bulletDirection.Value switch
            {
                0 => "bulletToTarget",
                1 => "actorToBullet",
                _ => null,
            };
        }

        var controlIndex = ReadInt(type, "ControlIndex");
        if (controlIndex.HasValue && controlIndex.Value > 0) result["controlIndex"] = controlIndex.Value;
    }

    if (isAreaEffect)
    {
        AddGameObjectPPtr(result, "areaEffectPrefab", type, "AreaEffectPrefab", assetsFile);

        AddNumber(result, "distance", NonZero(ReadFloat(type, "distance")));
        AddNumber(result, "spawnOffset", NonZero(ReadFloat(type, "offset")));
        AddNumber(result, "rotationOffset", NonZero(ReadFloat(type, "rotationAngleOffset")));

        if (ReadBool(type, "useOwnerDirection") == true) result["useOwnerDirection"] = true;
        if (ReadBool(type, "lockTarget") == false) result["lockTarget"] = false;
    }

    if (ReadBool(type, "triggerFateCardTheme") == true) result["triggerFateCardTheme"] = true;

    var buffIds = ReadIntArray(type, "buffIds");
    if (buffIds.Count > 0)
    {
        var buffs = new JsonArray();
        foreach (var id in buffIds) buffs.Add(id);
        result["buffIds"] = buffs;
    }

    var shieldId = ReadInt(type, "shiledID");
    if (shieldId.HasValue && shieldId.Value > 0) result["shieldId"] = shieldId.Value;

    return result;
}

static void AddGameObjectPPtr(JsonObject target, string key, OrderedDictionary type, string field, SerializedFile? assetsFile)
{
    if (!TryGetPPtr(type, field, out var fileId, out var pathId)) return;

    var reference = new PPtr<GameObject> { m_FileID = fileId, m_PathID = pathId };

    if (reference.TryGet(out var gameObject, assetsFile) && gameObject != null)
    {
        target[key] = gameObject.m_Name;
        return;
    }

    target[$"{key}Unresolved"] = DescribePPtr(assetsFile, fileId, pathId);
}

static void AddMonoBehaviourPPtr(JsonObject target, string key, OrderedDictionary type, string field, SerializedFile? assetsFile)
{
    if (!TryGetPPtr(type, field, out var fileId, out var pathId)) return;

    var reference = new PPtr<MonoBehaviour> { m_FileID = fileId, m_PathID = pathId };

    if (reference.TryGet(out var behaviour, assetsFile) && behaviour != null)
    {
        target[key] = behaviour.m_Name;
        return;
    }

    target[$"{key}Unresolved"] = DescribePPtr(assetsFile, fileId, pathId);
}

static void AddCurve(JsonObject target, string key, OrderedDictionary owner, string field)
{
    if (!owner.Contains(field) || owner[field] is not OrderedDictionary curve) return;

    var keys = new JsonArray();

    if (curve.Contains("m_keys") && curve["m_keys"] is IEnumerable list)
    {
        foreach (var item in list)
        {
            if (item is not OrderedDictionary keyframe) continue;

            keys.Add(new JsonArray(
                ReadFloat(keyframe, "m_Time"),
                ReadFloat(keyframe, "m_Value"),
                ReadFloat(keyframe, "m_InTangent"),
                ReadFloat(keyframe, "m_OutTangent")));
        }
    }

    var result = new JsonObject { ["keys"] = keys };

    AddNumber(result, "preWrap", ReadInt(curve, "m_preWrapMode"), true);
    AddNumber(result, "postWrap", ReadInt(curve, "m_postWrapMode"), true);

    target[key] = result;
}

static bool TryGetPPtr(OrderedDictionary type, string field, out int fileId, out long pathId)
{
    fileId = 0;
    pathId = 0;

    if (!type.Contains(field) || type[field] is not OrderedDictionary pptr) return false;

    var file = ReadInt(pptr, "m_FileID");
    var path = ReadInt(pptr, "m_PathID");

    if (!file.HasValue || !path.HasValue || path.Value == 0) return false;

    fileId = (int)file.Value;
    pathId = path.Value;

    return true;
}

static string DescribePPtr(SerializedFile? assetsFile, int fileId, long pathId)
{
    var file = assetsFile != null && fileId > 0 && fileId <= assetsFile.m_Externals.Count
        ? assetsFile.m_Externals[fileId - 1].fileName
        : Path.GetFileName(assetsFile?.originalPath);

    return $"{file}:{pathId}";
}

static bool IsEmptyPPtr(OrderedDictionary type, string field)
{
    if (!type.Contains(field) || type[field] is not OrderedDictionary pptr) return true;

    return ReadInt(pptr, "m_PathID") is 0;
}

static bool TryResolvePPtr<T>(OrderedDictionary type, string field, SerializedFile? assetsFile, out T? obj)
    where T : AssetStudio.Object
{
    obj = null;

    if (!type.Contains(field) || type[field] is not OrderedDictionary pptr) return false;

    var fileId = ReadInt(pptr, "m_FileID");
    var pathId = ReadInt(pptr, "m_PathID");

    if (!fileId.HasValue || !pathId.HasValue || pathId.Value == 0) return false;

    var reference = new PPtr<T> { m_FileID = (int)fileId.Value, m_PathID = pathId.Value };

    if (!reference.TryGet(out obj, assetsFile) || obj == null) return false;

    return true;
}

static void WriteComboSource(Dictionary<string, JsonObject> map)
{
    var root = new JsonObject();

    foreach (var key in map.Keys.OrderBy(key => key, StringComparer.Ordinal))
        root[key] = map[key];

    if (root.Count == 0)
    {
        Console.Error.WriteLine("Combo source export failed: no combo groups found");
        Environment.Exit(1);
    }

    WriteJson(ComboSourceOutput, root);
    Console.WriteLine($"{root.Count} combo groups");
}

static JsonArray ToJson(double[] values)
{
    var array = new JsonArray();

    foreach (var value in values) array.Add(value);

    return array;
}

static Dictionary<int, List<HitSource>> MergeSources(params Dictionary<int, List<HitSource>>[] maps)
{
    var result = new Dictionary<int, List<HitSource>>();

    foreach (var map in maps)
    {
        foreach (var (id, sources) in map)
        {
            if (!result.TryGetValue(id, out var list))
            {
                list = new List<HitSource>();
                result[id] = list;
            }

            list.AddRange(sources);
        }
    }

    return result;
}

static void ApplyFeatures(HitSource source, Dictionary<string, OrderedDictionary> features)
{
    if (features.TryGetValue("WeaponFeatureTraceTarget", out var trace)) source.Homing = BuildHoming(trace);

    if (features.ContainsKey("WeaponFeaturePenetratingActor") || features.ContainsKey("WeaponFeaturePenetratingObstacle")) source.Pierce = true;

    if (features.TryGetValue("WeaponFeatureSplitWhenFinish", out var split))
    {
        var splitCount = ReadInt(split, "splitBulletCount");
        if (splitCount.HasValue && splitCount.Value > 0) source.SplitCount = (int)splitCount.Value;
    }
}

static void AddSource(Dictionary<int, List<HitSource>> map, int id, HitSource source)
{
    if (id <= 0) return;

    if (!map.TryGetValue(id, out var list))
    {
        list = new List<HitSource>();
        map[id] = list;
    }

    list.Add(source);
}

static void WriteJson(string path, JsonObject root)
{
    var options = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    File.WriteAllText(path, root.ToJsonString(options).Replace("\r\n", "\n") + "\n");
}

static void ApplyShape(HitSource source, HitBox? box)
{
    if (box == null) return;

    source.Shape = box.Shape;
    source.Radius = box.Radius;
    source.InnerRadius = box.InnerRadius;
    source.Angle = box.Angle;
    source.Width = box.Width;
    source.Length = box.Length;
    source.Height = box.Height;
}

static HitSource ParseWeaponSource(string className, OrderedDictionary type, HashSet<string> bulletClasses, string[] shapes)
{
    var source = new HitSource
    {
        Class = className,
        Snapshot = bulletClasses.Contains(className),
        Speed = NonZero(ReadFloat(type, "moveSpeed") ?? ReadFloat(type, "speed")),
        Lifetime = NonZero(ReadFloat(type, "lifeTime")),
        Acceleration = NonZero(ReadFloat(type, "acceleratedVelocity")),
    };

    var shapeIndex = ReadInt(type, "shape");
    if (!shapeIndex.HasValue && className == "AdventureBomb") shapeIndex = ReadInt(type, "hitBoxShape");
    if (shapeIndex.HasValue && shapeIndex.Value >= 0 && shapeIndex.Value < shapes.Length) source.Shape = shapes[(int)shapeIndex.Value];

    switch (source.Shape)
    {
        case "rectangle":
            source.Width = NonZero(ReadFloat(type, "damageWidth") ?? ReadFloat(type, "hitBoxWidth") ?? ReadFloat(type, "shape_width"));
            source.Length = NonZero(ReadFloat(type, "damageHeight") ?? ReadFloat(type, "hitBoxLength") ?? ReadFloat(type, "shape_length"));
            break;
        case "fan":
            source.Radius = NonZero(ReadFloat(type, "fanOuterRadius") ?? ReadFloat(type, "shape_radius") ?? ReadFloat(type, "hitBoxRadius"));
            source.InnerRadius = NonZero(ReadFloat(type, "fanInnerRadius") ?? ReadFloat(type, "shape_innerRadius") ?? ReadFloat(type, "hitBoxInnerRadius"));
            source.Angle = NonZero(ReadFloat(type, "fanAngle") ?? ReadFloat(type, "shape_angle") ?? ReadFloat(type, "hitBoxAngle"));
            break;
        case "circle":
            source.Radius = NonZero(ReadFloat(type, "damageRadius") ?? ReadFloat(type, "fanOuterRadius") ?? ReadFloat(type, "shape_radius") ?? ReadFloat(type, "hitBoxRadius") ?? ReadFloat(type, "damageArea"));
            break;
        default:
            {
                var radius = NonZero(ReadFloat(type, "damageRadius") ?? ReadFloat(type, "damageArea") ?? ReadFloat(type, "hitBoxRadius"));

                if (radius.HasValue)
                {
                    source.Shape = "circle";
                    source.Radius = radius;
                }

                break;
            }
    }

    source.Height = NonZero(ReadFloat(type, "hitBoxHeight"));
    source.HitInterval = NonZero(ReadFloat(type, "hitTestInterval") ?? ReadFloat(type, "hitInterval"));

    source.Duration = className switch
    {
        "AdventureBoomerang" => NonZero(Sum(ReadFloat(type, "maxBeginStageTime"), ReadFloat(type, "maxCatchFailedStageTime"))),
        "AdventureFlyShield" => NonZero(ReadFloat(type, "maxBeginTime")),
        "AdventureRotateWeapon" => NonZero(ReadFloat(type, "_duration")),
        "AdventureWindBlade" => NonZero(Sum(ReadFloat(type, "searchTargetTime"), ReadFloat(type, "endStageTime"))),
        "AdventureTimeBomb" => NonZero(ReadFloat(type, "delayTime")),
        "AdventureBezierBullet" => NonZero(ReadFloat(type, "flyTime")),
        "AdventureBrokenLineBullet" => NonZero(Sum(ReadFloat(type, "forwardLifeTime"), ReadFloat(type, "backwardLifeTime"), ReadFloat(type, "stopDuration"))),
        _ => null,
    };

    if (className == "AdventureBomb") source.HitDelay = NonZero(ReadFloat(type, "delayExplosionTime"));

    if (className == "AdventureRotateWeapon" && source.Duration.HasValue && source.HitInterval.HasValue && source.HitInterval.Value > 0)
    {
        source.Hits = (int)Math.Ceiling(RoundRatio(source.Duration.Value / source.HitInterval.Value));
    }

    source.RotateSpeed = NonZero(ReadFloat(type, "rotateSpeed"));
    source.SearchRadius = NonZero(ReadFloat(type, "searchRadius"));
    source.TriggerRadius = NonZero(ReadFloat(type, "triggerRadius"));
    source.CatchRadius = NonZero(ReadFloat(type, "catchRadius"));

    var rebounds = ReadInt(type, "reboundTime") ?? ReadInt(type, "reboundCount");
    if (rebounds.HasValue && rebounds.Value > 0) source.ReboundCount = (int)rebounds.Value;

    if (className is "AdventureLaser" or "AdventureAreaLaser") source.Length = NonZero(ReadFloat(type, "maxLaserLength"));
    if (ReadBool(type, "enablePenetratingActor") == true || ReadBool(type, "enablePenetratingObstacle") == true) source.Pierce = true;

    return source;
}

static JsonObject BuildHoming(OrderedDictionary trace)
{
    var homing = new JsonObject();

    AddNumber(homing, "range", ReadFloat(trace, "traceRange"));
    AddNumber(homing, "rotateSpeed", ReadFloat(trace, "traceRotateSpeed"));
    AddNumber(homing, "startTime", ReadFloat(trace, "traceStartTime"));
    AddNumber(homing, "duration", ReadFloat(trace, "traceDuration"));

    return homing;
}

static void AddNumber(JsonObject target, string key, double? value, bool keepZero = false)
{
    if (value.HasValue && (keepZero || value.Value != 0)) target[key] = value.Value;
}

static double? NonZero(double? value)
{
    return value.HasValue && value.Value != 0 ? value : null;
}

static double? Sum(params double?[] values)
{
    var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();

    return present.Length == 0 ? null : present.Sum();
}

static int GetComponentHits(double duration, Attack attack)
{
    var timer = GetTimerHits(duration, attack.HitDelay, attack.HitInterval, attack.HitImmediately);
    var (triggerHits, ends) = GetTriggerHits(duration, attack);

    if (ends) return Math.Max(timer, triggerHits);

    return Math.Max(timer, Math.Max(triggerHits, timer >= 1 && triggerHits >= 1 ? 2 : 0));
}

static int GetTimerHits(double duration, double hitDelay, double hitInterval, bool hitImmediately)
{
    if (hitInterval <= 0)
    {
        if (hitDelay > 0) return duration > hitDelay ? 1 : 0;
        return hitImmediately ? 1 : 0;
    }

    var firstHit = hitDelay > 0 ? hitDelay : hitImmediately ? 0 : hitInterval;
    var durationFrames = GetLogicFrames(duration);
    var firstFrame = GetLogicFrames(firstHit);

    if (firstFrame >= durationFrames) return 0;

    return 1 + (int)((durationFrames - 1 - firstFrame) / GetLogicFrames(hitInterval));
}

static (int Hits, bool Ends) GetTriggerHits(double duration, Attack attack)
{
    if (attack.TriggerEnter && attack.FinishTriggerEnter) return (1, true);
    if (attack.TriggerStay && attack.FinishTriggerExit) return (1, true);
    if (attack.TriggerExit && attack.FinishTriggerExit) return (1, true);

    if (attack.TriggerStay)
    {
        var every = attack.TimeTriggerStay;

        if (every <= 0) return (duration > 0 ? 2 : 0, false);

        var durationFrames = GetLogicFrames(duration);
        var everyFrames = GetLogicFrames(every);

        return (
            everyFrames > durationFrames
                ? 0
                : (int)(durationFrames / everyFrames),
            false);
    }

    if (attack.TriggerEnter || attack.TriggerExit) return (1, false);

    return (0, false);
}

static int GetSourceHits(double duration, double hitDelay, double hitInterval)
{
    var durationFrames = GetLogicFrames(duration);
    var delayFrames = GetLogicFrames(hitDelay);

    if (delayFrames >= durationFrames) return 0;
    if (hitInterval <= 0) return 1;

    var intervalFrames = GetLogicFrames(hitInterval);

    return 1 + (int)((durationFrames - 1 - delayFrames) / intervalFrames);
}

static long GetLogicFrames(double seconds)
{
    var units = GetFixedUnits(seconds);
    if (units <= 0) return 0;

    return (units + FixedStep - 1) / FixedStep;
}

static long GetFixedUnits(double seconds)
{
    return (long)Math.Truncate((double)(float)seconds * FixedScale);
}

static double GetFirstHit(double hitDelay, double hitInterval, bool hitImmediately)
{
    if (hitDelay > 0) return hitDelay;
    return hitImmediately ? 0 : hitInterval;
}

static HitBox ReadHitBox(OrderedDictionary type, string[] shapes)
{
    var shapeIndex = ReadInt(type, "hitBoxShape") ?? 1;
    var shape = shapeIndex >= 0 && shapeIndex < shapes.Length ? shapes[(int)shapeIndex] : "circle";

    var radius = ReadFloat(type, "hitBoxRadius") ?? 1;
    var innerRadius = ReadFloat(type, "hitBoxInnerRadius") ?? 0;
    var angle = ReadFloat(type, "hitBoxAngle") ?? 45;
    var width = ReadFloat(type, "hitBoxWidth") ?? 1;
    var length = ReadFloat(type, "hitBoxLength") ?? 1;
    var height = ReadFloat(type, "hitBoxHeight") ?? 1;

    return new HitBox(
        shape,
        shape == "rectangle" ? null : Round(radius),
        shape == "fan" && innerRadius != 0 ? Round(innerRadius) : null,
        shape == "fan" ? Round(angle) : null,
        shape == "rectangle" ? Round(width) : null,
        shape == "rectangle" ? Round(length) : null,
        Math.Abs(height - 1) > 1e-9 ? Round(height) : null);
}

static List<int> ParseBulletIds(OrderedDictionary type)
{
    var ids = new List<int>();
    var single = ReadInt(type, "hitDamageId");

    if (single.HasValue && single.Value > 0) ids.Add((int)single.Value);

    ids.AddRange(ReadHitDamageData(type));
    ids.AddRange(ReadIntArray(type, "hitDamageIdList"));
    ids.AddRange(ReadIntArray(type, "_hitDamageIdList"));

    return ids.Where(id => id > 0).Distinct().ToList();
}

static List<int> ReadHitDamageData(OrderedDictionary type)
{
    var ids = new List<int>();

    if (!type.Contains("_hitdamagedatas") || type["_hitdamagedatas"] is not IEnumerable list) return ids;

    foreach (var item in list)
    {
        if (item is not OrderedDictionary data) continue;

        var id = ReadInt(data, "HitDamageId") ?? ReadInt(data, "hitDamageId");
        if (id.HasValue) ids.Add((int)id.Value);
    }

    return ids;
}

static List<int> ReadIntArray(OrderedDictionary type, string field)
{
    var ids = new List<int>();

    if (!type.Contains(field) || type[field] is not IEnumerable list || list is string) return ids;

    foreach (var item in list)
    {
        if (item is IConvertible convertible)
        {
            ids.Add((int)convertible.ToInt64(CultureInfo.InvariantCulture));
        }
        else if (item is OrderedDictionary data)
        {
            var id = ReadInt(data, "HitDamageId") ?? ReadInt(data, "hitDamageId");
            if (id.HasValue) ids.Add((int)id.Value);
        }
    }

    return ids;
}

static OrderedDictionary? ReadType(MonoBehaviour mb)
{
    try
    {
        return mb.ToType();
    }
    catch
    {
        return null;
    }
}

static string? GetClassName(MonoBehaviour mb, Dictionary<long, string> scripts)
{
    if (mb.m_Script.TryGet(out var script) && !string.IsNullOrEmpty(script.m_ClassName)) return script.m_ClassName;
    return scripts.TryGetValue(mb.m_Script.m_PathID, out var className) ? className : null;
}

static string? GetGameObjectName(MonoBehaviour mb, Dictionary<long, string> names)
{
    var name = mb.m_GameObject.TryGet(out var gameObject) ? gameObject.m_Name : names.GetValueOrDefault(mb.m_GameObject.m_PathID);
    return string.IsNullOrEmpty(name) ? null : name;
}

static (long, long) GetGameObjectKey(MonoBehaviour mb)
{
    return (mb.m_GameObject.m_FileID, mb.m_GameObject.m_PathID);
}

static double? ReadFloat(OrderedDictionary type, string field)
{
    if (!type.Contains(field)) return null;

    var value = type[field];

    if (value is OrderedDictionary fp && fp.Contains("_serializedValue"))
    {
        return Round(Convert.ToInt64(fp["_serializedValue"], CultureInfo.InvariantCulture) / 4294967296.0);
    }

    if (value is IConvertible convertible) return Round(convertible.ToDouble(CultureInfo.InvariantCulture));

    return null;
}

static long? ReadInt(OrderedDictionary type, string field)
{
    if (!type.Contains(field)) return null;

    var value = type[field];

    if (value is OrderedDictionary nested && nested.Contains("_serializedValue"))
    {
        return Convert.ToInt64(nested["_serializedValue"], CultureInfo.InvariantCulture);
    }

    if (value is IConvertible convertible) return convertible.ToInt64(CultureInfo.InvariantCulture);

    return null;
}

static bool? ReadBool(OrderedDictionary type, string field)
{
    if (!type.Contains(field)) return null;

    var value = type[field];

    if (value is bool boolean) return boolean;
    if (value is IConvertible convertible) return convertible.ToInt64(CultureInfo.InvariantCulture) != 0;

    return null;
}

static string? ReadString(OrderedDictionary type, string field)
{
    if (!type.Contains(field)) return null;

    return type[field] as string;
}

static double[]? ReadVector2(OrderedDictionary type, string field)
{
    if (!type.Contains(field) || type[field] is not OrderedDictionary value) return null;

    var x = ReadFloat(value, "x");
    var y = ReadFloat(value, "y");

    if (x == null && y == null) return null;

    return new[] { x ?? 0, y ?? 0 };
}

static double[]? ReadVector3(OrderedDictionary type, string field)
{
    if (!type.Contains(field) || type[field] is not OrderedDictionary value) return null;

    var x = ReadFloat(value, "x");
    var y = ReadFloat(value, "y");
    var z = ReadFloat(value, "z");

    if (x == null && y == null && z == null) return null;

    return new[] { x ?? 0, y ?? 0, z ?? 0 };
}

static double Round(double value)
{
    var result = Math.Floor(value * 1e6 + 0.5) / 1e6;
    return result == 0 ? 0 : result;
}

static double RoundRatio(double value)
{
    var result = Math.Floor(value * 1e9 + 0.5) / 1e9;
    return result == 0 ? 0 : result;
}

record Entity(bool Copy, double Duration, HitBox HitBox, string? Name);

record ComboClipAsset(string Name, OrderedDictionary Type);

record Attack(
    (long, long) GameObject,
    List<int> Ids,
    double HitDelay,
    double HitInterval,
    bool HitImmediately,
    double TimeTriggerStay,
    bool TriggerEnter,
    bool TriggerStay,
    bool TriggerExit,
    bool FinishTriggerEnter,
    bool FinishTriggerExit);

record Special(List<int> Ids, string? Name, string Class, double Duration, double HitDelay, double HitInterval, int Hits);

record HitBox(string Shape, double? Radius, double? InnerRadius, double? Angle, double? Width, double? Length, double? Height);

class WeaponPrefab
{
    public List<int>? Ids { get; set; }
    public HitSource? Source { get; set; }
    public Dictionary<string, OrderedDictionary> Features { get; } = new(StringComparer.Ordinal);
}

class HitSource
{
    public string Class { get; set; } = "";
    public bool Snapshot { get; set; }
    public string? Name { get; set; }
    public string? Alias { get; set; }
    public double? Speed { get; set; }
    public double? Lifetime { get; set; }
    public double? Acceleration { get; set; }
    public string? Shape { get; set; }
    public double? Radius { get; set; }
    public double? InnerRadius { get; set; }
    public double? Angle { get; set; }
    public double? Width { get; set; }
    public double? Length { get; set; }
    public double? Height { get; set; }
    public JsonObject? Homing { get; set; }
    public bool Pierce { get; set; }
    public int? SplitCount { get; set; }
    public double? Duration { get; set; }
    public double? HitDelay { get; set; }
    public double? HitInterval { get; set; }
    public int? Hits { get; set; }
    public List<string>? Triggers { get; set; }
    public double? SearchRadius { get; set; }
    public double? TriggerRadius { get; set; }
    public double? CatchRadius { get; set; }
    public int? ReboundCount { get; set; }
    public double? RotateSpeed { get; set; }

    public JsonObject ToJson()
    {
        var result = new JsonObject
        {
            ["class"] = Class,
            ["snapshot"] = Snapshot,
        };

        if (!string.IsNullOrEmpty(Name)) result["name"] = Name;
        if (!string.IsNullOrEmpty(Alias)) result["alias"] = Alias;

        Add(result, "speed", Speed);
        Add(result, "lifetime", Lifetime);
        Add(result, "acceleration", Acceleration);

        if (!string.IsNullOrEmpty(Shape)) result["shape"] = Shape;

        Add(result, "radius", Radius);
        Add(result, "innerRadius", InnerRadius);
        Add(result, "angle", Angle);
        Add(result, "width", Width);
        Add(result, "length", Length);
        Add(result, "height", Height);

        if (Homing != null) result["homing"] = Homing.DeepClone();
        if (Pierce) result["pierce"] = true;
        if (SplitCount.HasValue) result["splitCount"] = SplitCount.Value;

        Add(result, "duration", Duration);
        Add(result, "hitDelay", HitDelay);
        Add(result, "hitInterval", HitInterval);
        if (Hits.HasValue) result["hits"] = Hits.Value;

        if (Triggers != null)
        {
            var triggers = new JsonArray();
            foreach (var trigger in Triggers) triggers.Add(trigger);
            result["triggers"] = triggers;
        }

        Add(result, "searchRadius", SearchRadius);
        Add(result, "triggerRadius", TriggerRadius);
        Add(result, "catchRadius", CatchRadius);
        if (ReboundCount.HasValue) result["reboundCount"] = ReboundCount.Value;
        Add(result, "rotateSpeed", RotateSpeed);

        return result;
    }

    private static void Add(JsonObject target, string key, double? value)
    {
        if (value.HasValue) target[key] = value.Value;
    }
}
