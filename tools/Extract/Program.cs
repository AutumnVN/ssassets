using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AssetStudio;

const string AreaEffectOutput = "./areaEffect.json";
const string BulletOutput = "./bullet.json";

var bundlePattern = new Regex(@"^char_\d+(_(?:buff|breakout|combos|combosmonster|config|configs|fx|soldier|towerdefense|weapon|weaponmodel|weapons))?\.unity3d$", RegexOptions.Compiled);
var bulletClasses = new HashSet<string>(StringComparer.Ordinal) { "AdventureBullet", "AdventureBomb" };
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

if (mode is "areaEffect" or "all")
{
    WriteAreaEffect(ExtractAreaEffects(bundles, shapes));
}

if (mode is "bullet" or "all")
{
    WriteBullet(ExtractBullets(bundles, bulletClasses, featureClasses, shapes));
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

static Dictionary<int, AreaEntry> ExtractAreaEffects(List<string> bundles, string[] shapes)
{
    var map = new Dictionary<int, AreaEntry>();
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
            Console.Error.WriteLine($"areaEffect: failed {Path.GetFileName(bundle)}: {exception.Message}");
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

                    specials.Add(new Special(
                        new List<int> { (int)hitDamageId.Value },
                        GetGameObjectName(mb, names),
                        duration,
                        GetFirstHit(0, hitInterval, hitImmediately),
                        hitInterval,
                        GetEntityHits(duration, hitInterval, hitImmediately)));
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
                var componentHits = GetComponentHits(entity.Duration, attack);
                var triggers = new List<string>();

                if (attack.TriggerEnter) triggers.Add("triggerEnter");
                if (attack.TriggerStay) triggers.Add("triggerStay");
                if (attack.TriggerExit) triggers.Add("triggerExit");
                if (attack.FinishTriggerEnter) triggers.Add("finish_TriggerEnter");
                if (attack.FinishTriggerExit) triggers.Add("finish_TriggerExit");

                for (var index = 0; index < attack.Ids.Count; index++)
                {
                    var idHits = attack.Ids.Count > 1
                        ? (int)Math.Ceiling((double)(componentHits - index) / attack.Ids.Count)
                        : componentHits;

                    AddVariant(map, attack.Ids[index], new Variant(
                        entity.Name,
                        entity.Duration,
                        GetFirstHit(attack.HitDelay, attack.HitInterval, attack.HitImmediately),
                        attack.HitInterval,
                        idHits,
                        new List<string>(triggers),
                        entity.HitBox), entity.Copy);
                }
            }
        }

        foreach (var special in specials)
        {
            foreach (var id in special.Ids)
            {
                AddVariant(map, id, new Variant(
                    special.Name,
                    special.Duration,
                    special.HitDelay,
                    special.HitInterval,
                    special.Hits,
                    new List<string>(),
                    null), null);
            }
        }

        manager.Clear();
    }

    Console.WriteLine($"areaEffect: {resolved} attack component(s) matched an AreaEffectEntity, {unresolved} unresolved");
    return map;
}

static Dictionary<string, BulletEntry> ExtractBullets(List<string> bundles, HashSet<string> bulletClasses, HashSet<string> featureClasses, string[] shapes)
{
    var map = new Dictionary<string, BulletEntry>(StringComparer.Ordinal);
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
            Console.Error.WriteLine($"bullet: failed {Path.GetFileName(bundle)}: {exception.Message}");
            continue;
        }

        var prefabs = new Dictionary<string, BulletPrefab>(StringComparer.Ordinal);

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

                var isBullet = bulletClasses.Contains(className);
                if (!isBullet && !featureClasses.Contains(className)) continue;

                var name = GetGameObjectName(mb, names);
                if (name == null)
                {
                    if (isBullet) unresolved++;
                    continue;
                }

                var type = ReadType(mb);
                if (type == null) continue;

                if (!prefabs.TryGetValue(name, out var prefab))
                {
                    prefab = new BulletPrefab();
                    prefabs[name] = prefab;
                }

                if (isBullet)
                {
                    prefab.Ids = ParseBulletIds(type);
                    prefab.Stats = ParseBulletStats(type, shapes);
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
            if (prefab.Stats == null) continue;

            resolved++;

            if (!map.TryGetValue(name, out var entry))
            {
                entry = new BulletEntry();
                map[name] = entry;
            }

            entry.Hits.UnionWith(prefab.Ids);

            var stats = prefab.Stats;

            if (stats.Speed.HasValue && stats.Speed.Value != 0) entry.Speed = stats.Speed;
            if (stats.Lifetime.HasValue && stats.Lifetime.Value != 0) entry.Lifetime = stats.Lifetime;
            if (stats.Acceleration.HasValue && stats.Acceleration.Value != 0) entry.Acceleration = stats.Acceleration;

            entry.Shape = stats.Shape;

            if (stats.Shape == "circle" && stats.Radius.HasValue && stats.Radius.Value != 0) entry.Radius = stats.Radius;

            if (prefab.Features.TryGetValue("WeaponFeatureTraceTarget", out var trace)) entry.Homing = BuildHoming(trace);

            if (prefab.Features.ContainsKey("WeaponFeaturePenetratingActor") || prefab.Features.ContainsKey("WeaponFeaturePenetratingObstacle")) entry.Pierce = true;

            if (prefab.Features.TryGetValue("WeaponFeatureSplitWhenFinish", out var split))
            {
                var splitCount = ReadInt(split, "splitBulletCount");
                if (splitCount.HasValue && splitCount.Value > 0) entry.SplitCount = (int)splitCount.Value;
            }
        }

        manager.Clear();
    }

    Console.WriteLine($"bullet: {resolved} damage source(s) matched a GameObject, {unresolved} unresolved");
    return map;
}

static void WriteAreaEffect(Dictionary<int, AreaEntry> map)
{
    var existing = ReadJsonObject(AreaEffectOutput);
    var merged = new JsonObject();

    if (existing != null)
    {
        foreach (var (key, value) in existing)
        {
            if (value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var copy))
            {
                merged[key] = new JsonObject
                {
                    ["copyBattleDataBeforeAttack"] = copy,
                    ["hits"] = 2,
                };
            }
            else
            {
                merged[key] = value?.DeepClone();
            }
        }
    }

    foreach (var (id, entry) in map)
    {
        merged[id.ToString(CultureInfo.InvariantCulture)] = BuildAreaEntry(entry);
    }

    var sorted = new JsonObject();

    foreach (var key in merged.Select(pair => pair.Key).OrderBy(key => long.Parse(key, CultureInfo.InvariantCulture)))
    {
        sorted[key] = merged[key]?.DeepClone();
    }

    if (sorted.Count == 0)
    {
        Console.Error.WriteLine("Area effect export failed: no area effect hitDamage ids found");
        Environment.Exit(1);
    }

    WriteJson(AreaEffectOutput, sorted);
    Console.WriteLine($"{sorted.Count} areaEffect hitDamage ids");
}

static void WriteBullet(Dictionary<string, BulletEntry> map)
{
    var existing = ReadJsonObject(BulletOutput);
    var merged = new JsonObject();

    if (existing != null)
    {
        foreach (var (name, value) in existing)
        {
            merged[name] = value is JsonArray array
                ? new JsonObject { ["hits"] = array.DeepClone() }
                : value?.DeepClone();
        }
    }

    foreach (var (name, entry) in map)
    {
        if (merged[name] is not JsonObject target)
        {
            target = new JsonObject { ["hits"] = new JsonArray() };
            merged[name] = target;
        }

        var hits = new SortedSet<int>();

        if (target["hits"] is JsonArray existingHits)
        {
            foreach (var node in existingHits)
            {
                if (node == null) continue;
                var hit = node.GetValue<int>();
                if (hit > 0) hits.Add(hit);
            }
        }

        hits.UnionWith(entry.Hits);

        var hitsArray = new JsonArray();
        foreach (var hit in hits) hitsArray.Add(hit);
        target["hits"] = hitsArray;

        if (entry.Speed.HasValue) target["speed"] = entry.Speed.Value;
        if (entry.Lifetime.HasValue) target["lifetime"] = entry.Lifetime.Value;
        if (entry.Acceleration.HasValue) target["acceleration"] = entry.Acceleration.Value;

        target["shape"] = entry.Shape;

        if (entry.Radius.HasValue) target["radius"] = entry.Radius.Value;
        if (entry.Homing != null) target["homing"] = entry.Homing.DeepClone();
        if (entry.Pierce) target["pierce"] = true;
        if (entry.SplitCount.HasValue) target["splitCount"] = entry.SplitCount.Value;
    }

    var sorted = new JsonObject();

    foreach (var name in merged.Select(pair => pair.Key).OrderBy(name => name, StringComparer.Ordinal))
    {
        sorted[name] = merged[name]?.DeepClone();
    }

    if (sorted.Count == 0)
    {
        Console.Error.WriteLine("Bullet export failed: no bullet hitDamage ids found");
        Environment.Exit(1);
    }

    WriteJson(BulletOutput, sorted);
    Console.WriteLine($"{sorted.Count} bullet prefabs");
}

static JsonObject? ReadJsonObject(string path)
{
    if (!File.Exists(path)) return null;
    return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
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

static JsonObject BuildAreaEntry(AreaEntry entry)
{
    var result = new JsonObject();
    var variants = new JsonArray();

    foreach (var variant in entry.Variants) variants.Add(BuildVariant(variant));

    result["variants"] = variants;

    if (entry.Copy.HasValue) result["copyBattleDataBeforeAttack"] = entry.Copy.Value;

    var best = entry.Best;

    result["duration"] = best.Duration;
    result["hitDelay"] = best.HitDelay;
    result["hitInterval"] = best.HitInterval;
    result["hits"] = best.Hits;

    var triggers = new JsonArray();
    foreach (var trigger in best.Triggers) triggers.Add(trigger);
    result["triggers"] = triggers;

    ApplyShape(result, best.Box);
    return result;
}

static JsonObject BuildVariant(Variant variant)
{
    var result = new JsonObject();

    if (variant.Alias != null) result["alias"] = variant.Alias;

    result["duration"] = variant.Duration;
    result["hitDelay"] = variant.HitDelay;
    result["hitInterval"] = variant.HitInterval;
    result["hits"] = variant.Hits;

    var triggers = new JsonArray();
    foreach (var trigger in variant.Triggers) triggers.Add(trigger);
    result["triggers"] = triggers;

    ApplyShape(result, variant.Box);
    return result;
}

static void ApplyShape(JsonObject target, HitBox? box)
{
    if (box == null) return;

    target["shape"] = box.Shape;
    if (box.Radius.HasValue) target["radius"] = box.Radius.Value;
    if (box.InnerRadius.HasValue) target["innerRadius"] = box.InnerRadius.Value;
    if (box.Angle.HasValue) target["angle"] = box.Angle.Value;
    if (box.Width.HasValue) target["width"] = box.Width.Value;
    if (box.Length.HasValue) target["length"] = box.Length.Value;
    if (box.Height.HasValue) target["height"] = box.Height.Value;
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

static void AddVariant(Dictionary<int, AreaEntry> map, int id, Variant variant, bool? copy)
{
    if (!map.TryGetValue(id, out var entry))
    {
        entry = new AreaEntry();
        map[id] = entry;
    }

    if (copy.HasValue) entry.Copy = (entry.Copy ?? false) || copy.Value;

    if (!entry.Variants.Any(other => VariantKey(other).Equals(VariantKey(variant)))) entry.Variants.Add(variant);

    entry.Best = entry.Variants.OrderByDescending(candidate => candidate.Hits).First();
}

static (string?, double, double, double, int, string?, double?, double?, double?, double?, double?, double?) VariantKey(Variant variant)
{
    return (
        variant.Alias,
        variant.Duration,
        variant.HitDelay,
        variant.HitInterval,
        variant.Hits,
        variant.Box?.Shape,
        variant.Box?.Radius,
        variant.Box?.InnerRadius,
        variant.Box?.Angle,
        variant.Box?.Width,
        variant.Box?.Length,
        variant.Box?.Height);
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

static int GetEntityHits(double duration, double hitInterval, bool hitImmediately)
{
    var ratio = hitInterval > 0 ? RoundRatio(duration / hitInterval) : 0;
    var timer = hitInterval > 0 ? (int)Math.Floor(ratio) : 0;

    return (hitImmediately ? 1 : 0) + timer;
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

static BulletStats ParseBulletStats(OrderedDictionary type, string[] shapes)
{
    var shapeIndex = ReadInt(type, "shape") ?? 1;
    var shape = shapeIndex >= 0 && shapeIndex < shapes.Length ? shapes[(int)shapeIndex] : "circle";

    return new BulletStats(
        ReadFloat(type, "moveSpeed"),
        ReadFloat(type, "lifeTime"),
        ReadFloat(type, "acceleratedVelocity"),
        shape,
        shape == "circle" ? ReadFloat(type, "damageRadius") : null);
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

record Special(List<int> Ids, string? Name, double Duration, double HitDelay, double HitInterval, int Hits);

record Variant(string? Alias, double Duration, double HitDelay, double HitInterval, int Hits, List<string> Triggers, HitBox? Box);

record HitBox(string Shape, double? Radius, double? InnerRadius, double? Angle, double? Width, double? Length, double? Height);

record BulletStats(double? Speed, double? Lifetime, double? Acceleration, string Shape, double? Radius);

class AreaEntry
{
    public List<Variant> Variants { get; } = new();
    public bool? Copy { get; set; }
    public Variant Best { get; set; } = null!;
}

class BulletEntry
{
    public SortedSet<int> Hits { get; } = new();
    public double? Speed { get; set; }
    public double? Lifetime { get; set; }
    public double? Acceleration { get; set; }
    public string Shape { get; set; } = "circle";
    public double? Radius { get; set; }
    public JsonObject? Homing { get; set; }
    public bool Pierce { get; set; }
    public int? SplitCount { get; set; }
}

class BulletPrefab
{
    public List<int>? Ids { get; set; }
    public BulletStats? Stats { get; set; }
    public Dictionary<string, OrderedDictionary> Features { get; } = new(StringComparer.Ordinal);
}
