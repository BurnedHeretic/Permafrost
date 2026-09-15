using Permafrost.Core.Assets;

namespace Permafrost.Core.Maps;

public enum MapEra
{
    Prequel,
    Original,
    Sequel
}

public enum MapKind
{
    Ground,
    Space,
    CapitalShip
}

public sealed record MapShortcutDefinition(
    string DisplayName,
    MapEra Era,
    MapKind Kind,
    string[] ExactPaths,
    string[] MatchTokens,
    string[] ExcludeTokens)
{
    public override string ToString() => DisplayName;
}

public sealed class MapShortcutItem
{
    public required MapShortcutDefinition Definition { get; init; }
    public GameAssetEntry? Asset { get; init; }

    public string DisplayName => Definition.DisplayName;
    public string EraName => Definition.Era switch
    {
        MapEra.Prequel => "PREQUEL ERA",
        MapEra.Original => "ORIGINAL ERA",
        MapEra.Sequel => "SEQUEL ERA",
        _ => Definition.Era.ToString().ToUpperInvariant()
    };
    public bool IsAvailable => Asset != null;
    public string AvailabilityText => IsAvailable ? "Detected" : "Not detected";
    public string KindName => Definition.Kind switch
    {
        MapKind.Space => "SPACE",
        MapKind.CapitalShip => "CAPITAL SHIP",
        _ => "GROUND"
    };
    public string Path => Asset?.Name ?? "No matching retail level root was detected";
    public string PickerText => IsAvailable
        ? $"[{KindName}] {Definition.DisplayName}"
        : $"[{KindName}] {Definition.DisplayName}  (not detected)";
}

public static class MapShortcutCatalog
{
    // Friendly multiplayer-map shortcuts. Exact paths are tried first; MatchTokens provide a
    // version-tolerant fallback for season-prefixed/DLC layouts and alternate retail builds.
    public static IReadOnlyList<MapShortcutDefinition> All { get; } = new[]
    {
        // Clone Wars / prequel era
        M("Kamino — Cloning Facility", MapEra.Prequel,
            ["levels/mp/kamino_01/kamino_01"], ["kamino_01"], ["levels/space/", "sb_kamino"]),
        M("Naboo — Theed", MapEra.Prequel,
            ["levels/mp/naboo_01/naboo_01"], ["naboo_01"], ["levels/space/", "sb_naboo"]),
        M("Kashyyyk — Kachirho Beach", MapEra.Prequel,
            ["levels/mp/kashyyyk_01/kashyyyk_01"], ["kashyyyk_01", "kashyyyk"], ["levels/space/", "sb_kashyyyk"]),
        M("Geonosis — Trippa Hive", MapEra.Prequel,
            ["levels/mp/geonosis_01/geonosis_01"], ["geonosis_01"], ["levels/space/"]),
        M("Geonosis — Pipeline Junction West", MapEra.Prequel,
            ["levels/geonosis_02/geonosis_02", "levels/mp/geonosis_02/geonosis_02"], ["geonosis_02"], ["levels/space/"]),
        M("Felucia — Tagata", MapEra.Prequel,
            ["levels/mp/felucia_01/felucia_01", "levels/felucia_01/felucia_01"], ["felucia_01", "felucia"], ["levels/space/"]),
        M("Republic Attack Cruiser", MapEra.Prequel, MapKind.CapitalShip,
            ["levels/mp/venator_01/venator_01", "levels/venator_01/venator_01"], ["venator", "republicattackcruiser", "republic_cruiser"], ["levels/space/"]),
        M("Separatist Dreadnought", MapEra.Prequel, MapKind.CapitalShip,
            ["levels/mp/dreadnought_01/dreadnought_01", "levels/dreadnought_01/dreadnought_01"], ["dreadnought", "separatist"], ["levels/space/"]),

        // Galactic Civil War / original era
        M("Yavin 4 — Great Temple", MapEra.Original,
            ["levels/mp/yavin_01/yavin_01", "levels/mp/yavin4_01/yavin4_01"], ["yavin_01", "yavin4_01"], ["levels/space/", "sb_yavin"]),
        M("Death Star II", MapEra.Original,
            ["levels/mp/deathstar02_01/deathstar02_01"], ["deathstar02_01", "deathstar02"], ["levels/space/"]),
        M("Endor — Research Station 9", MapEra.Original,
            ["levels/mp/endor_01/endor_01", "levels/endor_01/endor_01"], ["endor_01"], ["levels/space/"]),
        M("Endor — Ewok Hunt / Night", MapEra.Original,
            ["levels/endor_02/endor_02", "levels/mp/endor_02/endor_02"], ["endor_02"], ["levels/space/"]),
        M("Hoth — Outpost Delta", MapEra.Original,
            ["levels/mp/hoth_01/hoth_01", "levels/hoth_01/hoth_01"], ["hoth_01"], ["levels/space/"]),
        M("Hoth — Battlefield / Sunset", MapEra.Original,
            ["levels/hoth_02/hoth_02", "levels/mp/hoth_02/hoth_02"], ["hoth_02"], ["levels/space/"]),
        M("Tatooine — Mos Eisley", MapEra.Original,
            ["levels/mp/tatooine_01/tatooine_01", "levels/tatooine_01/tatooine_01"], ["tatooine_01"], ["levels/space/"]),
        M("Tatooine — Jabba's Palace", MapEra.Original,
            ["levels/mp/tatooine_02/tatooine_02", "levels/tatooine_02/tatooine_02", "levels/jabbaspalace_01/jabbaspalace_01"], ["jabbaspalace", "jabba", "tatooine_02"], ["levels/space/"]),
        M("Kessel — Coaxium Mine", MapEra.Original,
            ["levels/mp/kessel_01/kessel_01", "levels/kessel_01/kessel_01"], ["kessel_01", "kessel"], ["levels/space/"]),
        M("Bespin — Administrator's Palace", MapEra.Original,
            ["levels/mp/bespin_01/bespin_01", "levels/bespin_01/bespin_01"], ["bespin_01", "bespin"], ["levels/space/"]),
        M("Scarif — Beach", MapEra.Original,
            ["levels/mp/scarif_01/scarif_01", "levels/scarif_01/scarif_01"], ["scarif_01", "scarif"], ["levels/space/"]),

        // First Order / Resistance / sequel era
        M("Jakku — Graveyard of Giants", MapEra.Sequel,
            ["levels/mp/jakku_01/jakku_01", "levels/jakku_01/jakku_01"], ["jakku_01"], ["levels/space/", "levels/sp/"]),
        M("Takodana — Maz's Castle", MapEra.Sequel,
            ["levels/mp/takodana_01/takodana_01", "levels/takodana_01/takodana_01"], ["takodana_01", "takodana"], ["levels/space/"]),
        M("Starkiller Base", MapEra.Sequel,
            ["levels/mp/starkiller_01/starkiller_01", "levels/starkiller_01/starkiller_01"], ["starkiller_01", "starkiller"], ["levels/space/"]),
        M("Crait — Abandoned Rebel Outpost", MapEra.Sequel,
            ["levels/crait_01/crait_01", "levels/mp/crait_01/crait_01"], ["crait_01", "crait"], ["levels/space/"]),
        M("Ajan Kloss", MapEra.Sequel,
            ["levels/mp/ajankloss_01/ajankloss_01", "levels/ajankloss_01/ajankloss_01", "levels/mp/ajan_kloss_01/ajan_kloss_01"], ["ajankloss", "ajan_kloss", "ajan-kloss"], ["levels/space/"]),
        M("MC85 Star Cruiser", MapEra.Sequel, MapKind.CapitalShip,
            ["levels/mp/mc85_01/mc85_01", "levels/mc85_01/mc85_01"], ["mc85", "starcruiser", "resistance_cruiser"], ["levels/space/"]),
        M("Resurgent-class Star Destroyer", MapEra.Sequel, MapKind.CapitalShip,
            ["levels/mp/resurgent_01/resurgent_01", "levels/resurgent_01/resurgent_01"], ["resurgent", "firstorder_stardestroyer", "fo_stardestroyer"], ["levels/space/"]),

        // Starfighter Assault / dedicated space battle roots. The retail scanner also discovers
        // any additional Levels/Space roots dynamically for diagnostics, so aliases can be refined
        // without hiding a valid map from the raw level browser.
        M("Kamino — Starfighter Assault", MapEra.Prequel, MapKind.Space,
            ["levels/space/sb_kamino_01/sb_kamino_01"], ["sb_kamino_01", "space/sb_kamino"], []),
        M("Ryloth — Starfighter Assault", MapEra.Prequel, MapKind.Space,
            ["levels/space/sb_droidbattleship_01/sb_droidbattleship_01", "levels/space/sb_ryloth_01/sb_ryloth_01"], ["sb_droidbattleship_01", "sb_ryloth_01", "space/sb_ryloth"], []),
        M("Fondor — Imperial Shipyard", MapEra.Original, MapKind.Space,
            ["levels/space/sb_fondor_01/sb_fondor_01"], ["sb_fondor_01", "space/sb_fondor"], []),
        M("Endor — Death Star Debris", MapEra.Original, MapKind.Space,
            ["levels/space/sb_endor_01/sb_endor_01"], ["sb_endor_01", "space/sb_endor"], []),
        M("D'Qar — Starfighter Assault", MapEra.Sequel, MapKind.Space,
            ["s1/levels/space/sb_spacebear_01/sb_spacebear_01", "levels/space/sb_spacebear_01/sb_spacebear_01", "levels/space/sb_dqar_01/sb_dqar_01", "levels/space/sb_d_qar_01/sb_d_qar_01"], ["sb_spacebear_01", "sb_dqar", "sb_d_qar", "space/dqar"], []),
        M("Unknown Regions — Starfighter Assault", MapEra.Sequel, MapKind.Space,
            ["levels/space/sb_resurgent_01/sb_resurgent_01", "levels/space/sb_unknownregions_01/sb_unknownregions_01", "levels/space/sb_unknown_regions_01/sb_unknown_regions_01"], ["sb_resurgent_01", "sb_unknown", "unknownregions", "unknown_regions"], [])
    };

    private static MapShortcutDefinition M(string name, MapEra era, string[] exact, string[] tokens, string[] excludes) =>
        new(name, era, MapKind.Ground, exact, tokens, excludes);

    private static MapShortcutDefinition M(string name, MapEra era, MapKind kind, string[] exact, string[] tokens, string[] excludes) =>
        new(name, era, kind, exact, tokens, excludes);

    public static GameAssetEntry? Resolve(MapShortcutDefinition map, IEnumerable<GameAssetEntry> levelAssets)
    {
        var candidates = levelAssets.ToArray();

        foreach (var exact in map.ExactPaths)
        {
            var normalizedExact = Normalize(exact);
            var hit = candidates.FirstOrDefault(x => Normalize(x.Name).Equals(normalizedExact, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
                return hit;

            // Season content sometimes adds a prefix before "levels/" while retaining the same
            // internal level path. Treat an exact path suffix as an exact match as well.
            hit = candidates.FirstOrDefault(x => Normalize(x.Name).EndsWith("/" + normalizedExact, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
                return hit;
        }

        GameAssetEntry? best = null;
        var bestScore = int.MinValue;
        foreach (var asset in candidates)
        {
            var path = Normalize(asset.Name);
            if (map.ExcludeTokens.Any(x => path.Contains(Normalize(x), StringComparison.OrdinalIgnoreCase)))
                continue;

            var score = 0;
            foreach (var token in map.MatchTokens)
            {
                if (path.Contains(Normalize(token), StringComparison.OrdinalIgnoreCase))
                    score += 30;
            }
            if (score == 0)
                continue;

            // Prefer the actual level root over LayerData, lighting, gameplay and helper EBXs.
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && segments[^1].Equals(segments[^2], StringComparison.OrdinalIgnoreCase))
                score += 80;
            if (path.Contains("/mp/", StringComparison.OrdinalIgnoreCase))
                score += 15;
            var isSpacePath = path.StartsWith("levels/space/", StringComparison.OrdinalIgnoreCase) ||
                              path.Contains("/levels/space/", StringComparison.OrdinalIgnoreCase);
            if (map.Kind == MapKind.Space)
                score += isSpacePath ? 55 : -80;
            else if (isSpacePath)
                score -= 90;
            if (path.Contains("/levels/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("levels/", StringComparison.OrdinalIgnoreCase))
                score += 10;
            if (path.Contains("lighting", StringComparison.OrdinalIgnoreCase) || path.Contains("prefab", StringComparison.OrdinalIgnoreCase) || path.Contains("gameplay", StringComparison.OrdinalIgnoreCase))
                score -= 30;

            if (score > bestScore || (score == bestScore && best != null && path.Length < Normalize(best.Name).Length))
            {
                best = asset;
                bestScore = score;
            }
        }

        return bestScore >= 60 ? best : null;
    }

    public static IReadOnlyList<GameAssetEntry> DiscoverSpaceLevelRoots(IEnumerable<GameAssetEntry> levelAssets)
    {
        return levelAssets
            .Where(x =>
            {
                var path = Normalize(x.Name);
                if (!(path.StartsWith("levels/space/", StringComparison.OrdinalIgnoreCase) ||
                      path.Contains("/levels/space/", StringComparison.OrdinalIgnoreCase)))
                    return false;
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return segments.Length >= 2 && segments[^1].Equals(segments[^2], StringComparison.OrdinalIgnoreCase);
            })
            .GroupBy(x => Normalize(x.Name), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Normalize(string value) => value.Replace('\\', '/').Trim('/').ToLowerInvariant();
}
