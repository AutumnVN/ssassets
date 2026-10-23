using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AssetStudio;

const string HitSourceOutput = "./hitSource.json";

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

var mode = args.Length > 0 ? args[0] : "all";
var source = args.Length > 1 ? args[1] : FindStoreDir();

if (source == null)
{
    Console.Error.WriteLine("No unpacked store found under ResourceTool/output/Unpack");
    return 1;
}

if (!Directory.Exists(source))
{
    Console.Error.WriteLine($"Source directory not found: {source}");
    return 1;
}

var bundles = Directory.GetFiles(source, "*.unity3d")
    .Where(path => bundlePattern.IsMatch(Path.GetFileName(path)))
    .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
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

return 0;

static string? FindStoreDir()
{
    const string baseDir = "./ResourceTool/output/Unpack";

    foreach (var dir in new[] { $"{baseDir}/EN/win", $"{baseDir}/EN", $"{baseDir}/win", baseDir })
    {
        if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.unity3d").Any()) return dir;
    }

    return null;
}

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

static void AddNumber(JsonObject target, string key, double? value)
{
    if (value.HasValue && value.Value != 0) target[key] = value.Value;
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
    if (duration <= firstHit) return 0;

    var ratio = RoundRatio((duration - firstHit) / hitInterval);

    return Math.Max(1, (int)Math.Ceiling(ratio));
}

static (int Hits, bool Ends) GetTriggerHits(double duration, Attack attack)
{
    if (attack.TriggerEnter && attack.FinishTriggerEnter) return (1, true);
    if (attack.TriggerStay && attack.FinishTriggerExit) return (1, true);
    if (attack.TriggerExit && attack.FinishTriggerExit) return (1, true);

    if (attack.TriggerStay)
    {
        var every = attack.TimeTriggerStay;

        return (
            every <= 0
                ? (duration > 0 ? 2 : 0)
                : (duration > every ? (int)Math.Ceiling(duration / every) : 0),
            false);
    }

    if (attack.TriggerEnter || attack.TriggerExit) return (1, false);

    return (0, false);
}

static int GetSourceHits(double duration, double hitDelay, double hitInterval)
{
    if (hitInterval <= 0) return hitDelay <= duration ? 1 : 0;
    if (duration <= hitDelay) return 0;

    var ratio = RoundRatio((duration - hitDelay) / hitInterval);

    return Math.Max(1, (int)Math.Ceiling(ratio));
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

static double Round(double value)
{
    var result = Math.Floor(value * 1e7 + 0.5) / 1e7;
    return result == 0 ? 0 : result;
}

static double RoundRatio(double value)
{
    var result = Math.Floor(value * 1e9 + 0.5) / 1e9;
    return result == 0 ? 0 : result;
}

record Entity(bool Copy, double Duration, HitBox HitBox, string? Name);

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
