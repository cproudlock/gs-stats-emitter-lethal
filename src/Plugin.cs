using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using GameNetcodeStuff;
using UnityEngine;

namespace GsLethalStatsEmitter
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "net.cproudlock.gslethalstatsemitter";
        public const string NAME = "gs Lethal Company Stats";
        public const string VERSION = "0.2.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<string> IngestUrl;
        internal static ConfigEntry<string> IngestToken;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> EmitOnHostOnly;

        internal static SessionState Session;
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        private Harmony harmony;

        private void Awake()
        {
            Log = Logger;
            IngestUrl = Config.Bind("Ingest", "Url",
                "https://gs.proudtech.net/api/lethal/ingest",
                "POST endpoint that receives the session payload at game-over.");
            IngestToken = Config.Bind("Ingest", "Token", "",
                "Bearer token sent in the Authorization header. Set this in BepInEx/config/net.cproudlock.gslethalstatsemitter.cfg");
            Enabled = Config.Bind("Ingest", "Enabled", true,
                "Master toggle. Set false to keep the mod loaded but skip ingest.");
            EmitOnHostOnly = Config.Bind("Ingest", "EmitOnHostOnly", true,
                "Only POST when running as the lobby host (avoids duplicate sessions from co-op clients).");

            harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{NAME} v{VERSION} loaded · ingest={IngestUrl.Value}");
        }

        // --- Helpers ---------------------------------------------------------

        internal static bool IsHost()
        {
            try
            {
                var nm = Unity.Netcode.NetworkManager.Singleton;
                return nm != null && nm.IsHost;
            }
            catch { return false; }
        }

        internal static SessionState EnsureSession(StartOfRound sor)
        {
            if (Session != null) return Session;
            string host = "host";
            try
            {
                if (sor != null && sor.allPlayerScripts != null)
                {
                    foreach (var pc in sor.allPlayerScripts)
                    {
                        if (pc != null && pc.isHostPlayerObject)
                        {
                            host = pc.playerUsername;
                            break;
                        }
                    }
                    if (host == "host" && sor.allPlayerScripts.Length > 0 && sor.allPlayerScripts[0] != null)
                        host = sor.allPlayerScripts[0].playerUsername ?? "host";
                }
            }
            catch { /* ignore */ }
            Session = new SessionState
            {
                sessionIdLocal = Guid.NewGuid().ToString(),
                hostName = host,
                seed = (sor != null ? sor.randomMapSeed : 0).ToString(CultureInfo.InvariantCulture),
                startedAtUtc = DateTime.UtcNow,
            };
            Log.LogInfo($"[gs] session start id={Session.sessionIdLocal} host={host}");
            return Session;
        }

        internal static PlayerSessionState GetPlayerSlot(string name, bool isHost = false)
        {
            if (string.IsNullOrEmpty(name)) name = "(unknown)";
            if (Session == null) return null;
            if (!Session.players.TryGetValue(name, out var p))
            {
                p = new PlayerSessionState { playerName = name, isHost = isHost };
                Session.players[name] = p;
            }
            else if (isHost) p.isHost = true;
            return p;
        }

        internal static DayState CurrentDay()
        {
            if (Session == null || Session.days.Count == 0) return null;
            return Session.days[Session.days.Count - 1];
        }

        internal static string CurrentMoon()
        {
            try { return CleanMoonName(StartOfRound.Instance != null ? StartOfRound.Instance.currentLevel?.PlanetName : null); }
            catch { return null; }
        }

        // "56 Vow" -> "Vow". Leaves names without a numeric prefix alone.
        internal static string CleanMoonName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            int i = 0;
            while (i < raw.Length && (raw[i] >= '0' && raw[i] <= '9')) i++;
            if (i == 0 || i >= raw.Length) return raw;
            while (i < raw.Length && raw[i] == ' ') i++;
            return raw.Substring(i);
        }

        internal static int CurrentCredits()
        {
            try
            {
                var term = UnityEngine.Object.FindObjectOfType<Terminal>();
                return term != null ? term.groupCredits : 0;
            }
            catch { return 0; }
        }

        // POST + reset. Idempotent on the server via sessionIdLocal dedup.
        // `blocking` = send synchronously (used on disconnect/quit, where the
        // process/lobby tears down before a fire-and-forget Task would finish —
        // that gap is why sessions ending via "host shut down" never uploaded).
        internal static void EmitAndReset(string outcome, bool blocking = false)
        {
            var s = Session;
            if (s == null) return;
            Session = null; // immediately so concurrent hooks don't double-emit
            try
            {
                if (EmitOnHostOnly.Value && !IsHost())
                {
                    Log.LogInfo($"[gs] not host, skip POST for session {s.sessionIdLocal}");
                    return;
                }
                s.outcome = outcome;
                s.endedAtUtc = DateTime.UtcNow;
                ComputeAggregates(s);
                var json = SessionJson.Serialize(s);
                Log.LogInfo($"[gs] emit session id={s.sessionIdLocal} outcome={outcome} days={s.daysSurvived} players={s.players.Count} deaths={s.deaths.Count} bytes={json.Length} blocking={blocking}");
                if (blocking) PostBlocking(json);
                else _ = Task.Run(() => PostJson(json));
            }
            catch (Exception e)
            {
                Log.LogError($"[gs] emit error: {e}");
            }
        }

        // Synchronous send for the disconnect/quit path. Blocks the calling
        // thread up to 6s so the request actually leaves before the game exits.
        static void PostBlocking(string json)
        {
            if (!Enabled.Value) { Log.LogInfo("[gs] disabled, skip POST"); return; }
            if (string.IsNullOrEmpty(IngestToken.Value)) { Log.LogWarning("[gs] no token configured, skipping"); return; }
            try
            {
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6)))
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, IngestUrl.Value)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {IngestToken.Value}");
                    var res = http.SendAsync(req, cts.Token).GetAwaiter().GetResult();
                    Log.LogInfo($"[gs] POST(blocking) {(int)res.StatusCode}");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[gs] POST(blocking) error: {e.Message}");
            }
        }

        static void ComputeAggregates(SessionState s)
        {
            s.daysSurvived = s.days.Count;
            int total = 0, peak = 0;
            foreach (var d in s.days)
            {
                total += d.scrapValueCollected;
                if (d.scrapValueCollected > peak) peak = d.scrapValueCollected;
            }
            s.totalScrapValue = total;
            s.peakScrapValue = peak;
            try { s.finalQuota = TimeOfDay.Instance != null ? TimeOfDay.Instance.profitQuota : 0; } catch { }
            try { s.quotasMet = TimeOfDay.Instance != null ? TimeOfDay.Instance.timesFulfilledQuota : 0; } catch { }
            s.finalCredits = CurrentCredits();
            s.quotaMargin = s.finalCredits - s.finalQuota;
            foreach (var p in s.players.Values)
            {
                p.metersTraveled = (int)Math.Round(p.metersTraveledFloat);
            }
        }

        static async Task PostJson(string json)
        {
            if (!Enabled.Value) { Log.LogInfo("[gs] disabled, skip POST"); return; }
            if (string.IsNullOrEmpty(IngestToken.Value)) { Log.LogWarning("[gs] no token configured, skipping"); return; }
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, IngestUrl.Value)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {IngestToken.Value}");
                var res = await http.SendAsync(req).ConfigureAwait(false);
                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                    Log.LogWarning($"[gs] POST {res.StatusCode}: {body}");
                else
                    Log.LogInfo($"[gs] POST ok: {body}");
            }
            catch (Exception e)
            {
                Log.LogWarning($"[gs] POST error: {e.Message}");
            }
        }
    }

    // ===== Domain types =====================================================

    internal class SessionState
    {
        public string sessionIdLocal;
        public string hostName;
        public string seed;
        public DateTime startedAtUtc;
        public DateTime? endedAtUtc;
        public int daysSurvived;
        public int quotasMet;
        public int finalQuota;
        public int finalCredits;
        public int quotaMargin; // finalCredits − finalQuota; negative = fired short
        public int peakScrapValue;
        public int totalScrapValue;
        public string outcome;
        public List<DayState> days = new List<DayState>();
        public Dictionary<string, PlayerSessionState> players = new Dictionary<string, PlayerSessionState>();
        public List<DeathEvent> deaths = new List<DeathEvent>();
        // Tracks who interacted with the terminal most recently so we can
        // attribute purchases to the buyer (LC RPCs don't carry the player id).
        public string lastTerminalUser;
    }

    internal class DayState
    {
        public int dayIndex;
        public string moon;
        public string weather;
        public int quotaBefore;
        public int creditsBefore;
        public int creditsAfter;
        public int scrapValueCollected;
        public int scrapPiecesCollected;
        public int scrapValueLeftBehind;
        public bool apparatusPulled;
        public int deaths;
        public bool returnedToCompany;
        public DateTime startedAtUtc;
        public DateTime? endedAtUtc;
        // Total scrap value spawned on the moon this day. Sampled when the ship
        // lands (RoundManager.totalScrapValueInLevel becomes valid after scrap
        // spawn); leftBehind is computed at day end.
        public int totalScrapValueOnMoon;
    }

    internal class PlayerSessionState
    {
        public string playerName;
        public ulong steamId;
        public bool isHost;
        public int deaths;
        public int scrapValueCollected;
        public int daysSurvived;
        public int mostCreditsHeld;
        public int metersTraveled;
        public float metersTraveledFloat; // accumulator
        public Dictionary<string, PlayerItemBag> items = new Dictionary<string, PlayerItemBag>();
        public Dictionary<string, PurchaseBag> purchases = new Dictionary<string, PurchaseBag>();
        public Dictionary<string, int> mobKills = new Dictionary<string, int>();
        // Per-weather distance accumulator. Float for precision; emitted as int.
        public Dictionary<string, float> distanceByWeather = new Dictionary<string, float>();

        // damage attribution
        public string lastDamagedByEnemy;
        public DateTime lastDamagedAt;
    }

    internal class PurchaseBag
    {
        public string itemName;
        public int count;
        public int totalSpent;
        public bool isUnlockable;
    }

    internal class PlayerItemBag
    {
        public string itemName;
        public int picks;
        public int totalValue;
        public int soldValue;
    }

    internal class DeathEvent
    {
        public int dayIndex;
        public string playerName;
        public string causeOfDeath;
        public string killer;
        public string moon;
        public float posX, posY, posZ;
        public DateTime tsUtc;
    }

    // ===== Tiny JSON serializer =============================================
    // Hand-rolled to avoid pulling in Newtonsoft. Lethal Company doesn't ship
    // System.Text.Json in its .NET Standard 2.1 surface, so this is the path
    // of least dependency churn.

    internal static class SessionJson
    {
        public static string Serialize(SessionState s)
        {
            var sb = new StringBuilder(4096);
            sb.Append('{');
            Kv(sb, "sessionIdLocal", s.sessionIdLocal); Comma(sb);
            Kv(sb, "hostName", s.hostName); Comma(sb);
            Kv(sb, "seed", s.seed); Comma(sb);
            Kv(sb, "startedAtUtc", Iso(s.startedAtUtc)); Comma(sb);
            Kv(sb, "endedAtUtc", s.endedAtUtc.HasValue ? Iso(s.endedAtUtc.Value) : null); Comma(sb);
            Kv(sb, "daysSurvived", s.daysSurvived); Comma(sb);
            Kv(sb, "quotasMet", s.quotasMet); Comma(sb);
            Kv(sb, "finalQuota", s.finalQuota); Comma(sb);
            Kv(sb, "finalCredits", s.finalCredits); Comma(sb);
            Kv(sb, "peakScrapValue", s.peakScrapValue); Comma(sb);
            Kv(sb, "totalScrapValue", s.totalScrapValue); Comma(sb);
            Kv(sb, "quotaMargin", s.quotaMargin); Comma(sb);
            Kv(sb, "outcome", s.outcome); Comma(sb);
            // days
            sb.Append("\"days\":["); bool first = true;
            foreach (var d in s.days)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                Kv(sb, "dayIndex", d.dayIndex); Comma(sb);
                Kv(sb, "moon", d.moon); Comma(sb);
                Kv(sb, "weather", d.weather); Comma(sb);
                Kv(sb, "quotaBefore", d.quotaBefore); Comma(sb);
                Kv(sb, "creditsBefore", d.creditsBefore); Comma(sb);
                Kv(sb, "creditsAfter", d.creditsAfter); Comma(sb);
                Kv(sb, "scrapValueCollected", d.scrapValueCollected); Comma(sb);
                Kv(sb, "scrapPiecesCollected", d.scrapPiecesCollected); Comma(sb);
                Kv(sb, "scrapValueLeftBehind", d.scrapValueLeftBehind); Comma(sb);
                Kv(sb, "apparatusPulled", d.apparatusPulled); Comma(sb);
                Kv(sb, "deaths", d.deaths); Comma(sb);
                Kv(sb, "returnedToCompany", d.returnedToCompany); Comma(sb);
                Kv(sb, "startedAtUtc", Iso(d.startedAtUtc)); Comma(sb);
                Kv(sb, "endedAtUtc", d.endedAtUtc.HasValue ? Iso(d.endedAtUtc.Value) : null);
                sb.Append('}');
            }
            sb.Append("],");
            // players
            sb.Append("\"players\":["); first = true;
            foreach (var p in s.players.Values)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                Kv(sb, "name", p.playerName); Comma(sb);
                Kv(sb, "isHost", p.isHost); Comma(sb);
                Kv(sb, "deaths", p.deaths); Comma(sb);
                Kv(sb, "scrapValueCollected", p.scrapValueCollected); Comma(sb);
                Kv(sb, "daysSurvived", p.daysSurvived); Comma(sb);
                Kv(sb, "mostCreditsHeld", p.mostCreditsHeld); Comma(sb);
                Kv(sb, "metersTraveled", p.metersTraveled); Comma(sb);
                sb.Append("\"items\":["); bool firstItem = true;
                foreach (var it in p.items.Values)
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    sb.Append('{');
                    Kv(sb, "itemName", it.itemName); Comma(sb);
                    Kv(sb, "picks", it.picks); Comma(sb);
                    Kv(sb, "totalValue", it.totalValue); Comma(sb);
                    Kv(sb, "soldValue", it.soldValue);
                    sb.Append('}');
                }
                sb.Append("],");
                sb.Append("\"purchases\":["); bool firstPur = true;
                foreach (var pr in p.purchases.Values)
                {
                    if (!firstPur) sb.Append(',');
                    firstPur = false;
                    sb.Append('{');
                    Kv(sb, "itemName", pr.itemName); Comma(sb);
                    Kv(sb, "count", pr.count); Comma(sb);
                    Kv(sb, "totalSpent", pr.totalSpent); Comma(sb);
                    Kv(sb, "isUnlockable", pr.isUnlockable);
                    sb.Append('}');
                }
                sb.Append("],");
                sb.Append("\"mobKills\":["); bool firstMob = true;
                foreach (var mk in p.mobKills)
                {
                    if (!firstMob) sb.Append(',');
                    firstMob = false;
                    sb.Append('{');
                    Kv(sb, "enemyType", mk.Key); Comma(sb);
                    Kv(sb, "kills", mk.Value);
                    sb.Append('}');
                }
                sb.Append("],");
                sb.Append("\"distanceByWeather\":["); bool firstW = true;
                foreach (var w in p.distanceByWeather)
                {
                    if (!firstW) sb.Append(',');
                    firstW = false;
                    sb.Append('{');
                    Kv(sb, "weather", w.Key); Comma(sb);
                    Kv(sb, "meters", (int)Math.Round(w.Value));
                    sb.Append('}');
                }
                sb.Append(']');
                sb.Append('}');
            }
            sb.Append("],");
            // mods (loaded BepInEx plugins, for the session detail page)
            sb.Append("\"mods\":["); first = true;
            try
            {
                var infos = BepInEx.Bootstrap.Chainloader.PluginInfos;
                if (infos != null)
                {
                    foreach (var kvp in infos)
                    {
                        var info = kvp.Value;
                        if (info == null || info.Metadata == null) continue;
                        // r2modman puts each mod in <Author>-<Name>/ — split for a Thunderstore URL.
                        string folder = "", author = "";
                        try
                        {
                            var dir = System.IO.Path.GetDirectoryName(info.Location ?? "");
                            if (!string.IsNullOrEmpty(dir))
                            {
                                folder = System.IO.Path.GetFileName(dir);
                                int dash = folder.IndexOf('-');
                                if (dash > 0) author = folder.Substring(0, dash);
                            }
                        }
                        catch { }
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('{');
                        Kv(sb, "guid", info.Metadata.GUID ?? ""); Comma(sb);
                        Kv(sb, "name", info.Metadata.Name ?? ""); Comma(sb);
                        Kv(sb, "version", info.Metadata.Version?.ToString() ?? ""); Comma(sb);
                        Kv(sb, "author", author); Comma(sb);
                        Kv(sb, "folder", folder);
                        sb.Append('}');
                    }
                }
            }
            catch { /* ignore enumeration errors */ }
            sb.Append("],");

            // deaths
            sb.Append("\"deaths\":["); first = true;
            foreach (var d in s.deaths)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                Kv(sb, "dayIndex", d.dayIndex); Comma(sb);
                Kv(sb, "playerName", d.playerName); Comma(sb);
                Kv(sb, "causeOfDeath", d.causeOfDeath); Comma(sb);
                Kv(sb, "killer", d.killer); Comma(sb);
                Kv(sb, "moon", d.moon); Comma(sb);
                Kv(sb, "posX", d.posX); Comma(sb);
                Kv(sb, "posY", d.posY); Comma(sb);
                Kv(sb, "posZ", d.posZ); Comma(sb);
                Kv(sb, "tsUtc", Iso(d.tsUtc));
                sb.Append('}');
            }
            sb.Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        static void Kv(StringBuilder sb, string k, string v) { sb.Append('"').Append(k).Append("\":"); WriteString(sb, v); }
        static void Kv(StringBuilder sb, string k, int v) { sb.Append('"').Append(k).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture)); }
        static void Kv(StringBuilder sb, string k, float v) { sb.Append('"').Append(k).Append("\":").Append(v.ToString("R", CultureInfo.InvariantCulture)); }
        static void Kv(StringBuilder sb, string k, bool v) { sb.Append('"').Append(k).Append("\":").Append(v ? "true" : "false"); }
        static void Comma(StringBuilder sb) => sb.Append(',');

        static void WriteString(StringBuilder sb, string v)
        {
            if (v == null) { sb.Append("null"); return; }
            sb.Append('"');
            foreach (var c in v)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        static string Iso(DateTime dt) => dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    // ===== Harmony patches =====================================================

    // Note: we used to create the session on StartOfRound.Start, but that
    // fires while still in the main menu / pre-lobby state, where every
    // PlayerControllerB.playerUsername is the placeholder "Player #N".
    // Real Steam names aren't populated until the lobby is fully joined.
    // Creating the session on ArriveAtLevel (first moon landing) means
    // hostName + per-player names are always real, and runs that never
    // landed (quit from orbit) correctly don't produce a session row.

    // Day start — fires when the ship arrives at a moon and players can disembark.
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.ArriveAtLevel))]
    static class P_StartOfRound_ArriveAtLevel
    {
        static void Postfix(StartOfRound __instance)
        {
            var s = Plugin.EnsureSession(__instance);
            // LC's PlanetName is "<routePrice> <name>" (e.g. "56 Vow"). Strip
            // the leading digits + space so the DB stores just "Vow".
            string rawMoon = __instance.currentLevel != null ? __instance.currentLevel.PlanetName : null;
            string moon = Plugin.CleanMoonName(rawMoon);
            int dayIdx = s.days.Count + 1;
            s.days.Add(new DayState
            {
                dayIndex = dayIdx,
                moon = moon,
                weather = SafeWeather(__instance.currentLevel),
                quotaBefore = TimeOfDay.Instance != null ? TimeOfDay.Instance.profitQuota : 0,
                creditsBefore = Plugin.CurrentCredits(),
                startedAtUtc = DateTime.UtcNow,
            });
            Plugin.Log.LogInfo($"[gs] day {dayIdx} start moon={moon}");
        }

        static string SafeWeather(SelectableLevel lvl)
        {
            try { return lvl != null ? lvl.currentWeather.ToString() : null; } catch { return null; }
        }
    }

    // Day end — ShipHasLeft is invoked once the ship is back in orbit.
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.ShipHasLeft))]
    static class P_StartOfRound_ShipHasLeft
    {
        static void Postfix()
        {
            var d = Plugin.CurrentDay();
            if (d == null) return;
            d.endedAtUtc = DateTime.UtcNow;
            d.creditsAfter = Plugin.CurrentCredits();
            if (d.totalScrapValueOnMoon > 0)
                d.scrapValueLeftBehind = Math.Max(0, d.totalScrapValueOnMoon - d.scrapValueCollected);
            Plugin.Log.LogInfo($"[gs] day {d.dayIndex} end credits={d.creditsAfter} left=${d.scrapValueLeftBehind}");
        }
    }

    // Sell at the Company desk — finalises the day's scrap value.
    [HarmonyPatch]
    static class P_DepositItemsDesk_SellItemsOnServer
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("DepositItemsDesk");
            if (t == null) return null;
            return AccessTools.Method(t, "SellItemsOnServer");
        }
        static void Postfix()
        {
            try
            {
                var d = Plugin.CurrentDay();
                if (d == null) return;
                int credAfter = Plugin.CurrentCredits();
                int delta = credAfter - d.creditsBefore;
                if (delta > 0) d.scrapValueCollected += delta;
                d.creditsAfter = credAfter;
                d.returnedToCompany = true;
                Plugin.Log.LogInfo($"[gs] sold on day {d.dayIndex} delta=${delta}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[gs] sell hook err: {e.Message}"); }
        }
    }

    // Per-item attribution — fires on every client when an item is picked up.
    [HarmonyPatch(typeof(GrabbableObject), nameof(GrabbableObject.GrabItemOnClient))]
    static class P_GrabbableObject_GrabItemOnClient
    {
        static void Postfix(GrabbableObject __instance)
        {
            if (Plugin.Session == null) return;
            try
            {
                var player = __instance.playerHeldBy;
                if (player == null) return;
                var slot = Plugin.GetPlayerSlot(player.playerUsername);
                if (slot == null) return;
                var name = SafeItemName(__instance);
                int value = __instance.scrapValue;
                if (!slot.items.TryGetValue(name, out var bag))
                    slot.items[name] = bag = new PlayerItemBag { itemName = name };
                bag.picks++;
                bag.totalValue += value;
                slot.scrapValueCollected += value;
                var d = Plugin.CurrentDay();
                if (d != null) d.scrapPiecesCollected += 1;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[gs] grab hook err: {e.Message}"); }
        }

        static string SafeItemName(GrabbableObject g)
        {
            try
            {
                if (g.itemProperties != null && !string.IsNullOrEmpty(g.itemProperties.itemName))
                    return g.itemProperties.itemName.Replace(" ", "");
            }
            catch { }
            return g.GetType().Name;
        }
    }

    // Track recent enemy contact for death attribution.
    [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.OnCollideWithPlayer))]
    static class P_EnemyAI_OnCollideWithPlayer
    {
        static void Postfix(EnemyAI __instance, Collider other)
        {
            if (Plugin.Session == null || __instance == null || other == null) return;
            try
            {
                var pc = other.GetComponent<PlayerControllerB>();
                if (pc == null) return;
                var slot = Plugin.GetPlayerSlot(pc.playerUsername);
                if (slot == null) return;
                slot.lastDamagedByEnemy = __instance.GetType().Name;
                slot.lastDamagedAt = DateTime.UtcNow;
            }
            catch { /* ignore */ }
        }
    }

    // Death event. We hook KillPlayerClientRpc (not KillPlayer): KillPlayer only
    // runs on the dying player's own client, so co-op clients missed every other
    // player's deaths. The ClientRpc fires on ALL clients for ANY player's death,
    // so a single reporter now records the whole crew. Signature confirmed via
    // metadata dump. The host invokes it twice (send + execute stages), so we
    // dedupe per playerId within a 1s window.
    [HarmonyPatch(typeof(PlayerControllerB), "KillPlayerClientRpc")]
    static class P_PlayerControllerB_KillPlayerClientRpc
    {
        static readonly Dictionary<int, DateTime> lastDeathAt = new Dictionary<int, DateTime>();

        static void Postfix(int playerId, int causeOfDeath)
        {
            if (Plugin.Session == null) return;
            try
            {
                var now = DateTime.UtcNow;
                if (lastDeathAt.TryGetValue(playerId, out var t) && (now - t).TotalSeconds < 1.0) return;
                lastDeathAt[playerId] = now;

                var sor = StartOfRound.Instance;
                PlayerControllerB pc = (sor != null && sor.allPlayerScripts != null && playerId >= 0 && playerId < sor.allPlayerScripts.Length)
                    ? sor.allPlayerScripts[playerId] : null;
                string name = pc != null ? pc.playerUsername : "(unknown)";
                var cod = (CauseOfDeath)causeOfDeath;

                var slot = Plugin.GetPlayerSlot(name);
                if (slot != null) slot.deaths++;
                string killer;
                if (slot != null && !string.IsNullOrEmpty(slot.lastDamagedByEnemy) &&
                    (now - slot.lastDamagedAt).TotalSeconds < 10)
                {
                    killer = slot.lastDamagedByEnemy;
                }
                else
                {
                    switch (cod)
                    {
                        case CauseOfDeath.Gravity:
                        case CauseOfDeath.Crushing:
                        case CauseOfDeath.Drowning:
                        case CauseOfDeath.Suffocation:
                        case CauseOfDeath.Inertia:
                        case CauseOfDeath.Burning:
                        case CauseOfDeath.Electrocution:
                            killer = "environment";
                            break;
                        case CauseOfDeath.Gunshots:
                            killer = "self";
                            break;
                        default:
                            killer = "unknown";
                            break;
                    }
                }
                var pos = pc != null && pc.transform != null ? pc.transform.position : Vector3.zero;
                Plugin.Session.deaths.Add(new DeathEvent
                {
                    dayIndex = Plugin.CurrentDay()?.dayIndex ?? Math.Max(1, Plugin.Session.days.Count),
                    playerName = name,
                    causeOfDeath = cod.ToString(),
                    killer = killer,
                    moon = Plugin.CurrentMoon(),
                    posX = pos.x,
                    posY = pos.y,
                    posZ = pos.z,
                    tsUtc = now,
                });
                var d = Plugin.CurrentDay();
                if (d != null) d.deaths++;
                Plugin.Log.LogInfo($"[gs] death {name} cause={cod} killer={killer}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[gs] death hook err: {e.Message}"); }
        }
    }

    // Distance accumulator — per-player, every Update tick. Skip teleport-scale
    // deltas to keep ship-respawn / drop-ship moves from inflating the total.
    [HarmonyPatch(typeof(PlayerControllerB), "Update")]
    static class P_PlayerControllerB_Update_Distance
    {
        static readonly Dictionary<string, Vector3> lastPos = new Dictionary<string, Vector3>();
        const float TeleportThreshold = 25f;

        static void Postfix(PlayerControllerB __instance)
        {
            if (Plugin.Session == null || __instance == null) return;
            try
            {
                if (!__instance.gameObject.activeInHierarchy) return;
                if (__instance.transform == null) return;
                var name = __instance.playerUsername;
                if (string.IsNullOrEmpty(name)) return;
                var p = __instance.transform.position;
                if (lastPos.TryGetValue(name, out var prev))
                {
                    var d = Vector3.Distance(prev, p);
                    if (d > 0.01f && d < TeleportThreshold)
                    {
                        var slot = Plugin.GetPlayerSlot(name);
                        if (slot != null)
                        {
                            slot.metersTraveledFloat += d;
                            // Bucket by current weather. Inside the ship / orbit
                            // (currentLevel == null) falls into "None".
                            string w = "None";
                            try
                            {
                                if (TimeOfDay.Instance != null && TimeOfDay.Instance.currentLevel != null)
                                    w = TimeOfDay.Instance.currentLevelWeather.ToString();
                            }
                            catch { }
                            slot.distanceByWeather.TryGetValue(w, out var existing);
                            slot.distanceByWeather[w] = existing + d;
                        }
                    }
                }
                lastPos[name] = p;
            }
            catch { /* swallow */ }
        }
    }

    // Outcome: fired by the Company (quota failed).
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.FirePlayersAfterDeadlineClientRpc))]
    static class P_StartOfRound_FirePlayers
    {
        static void Postfix() { Plugin.EmitAndReset("quota_failed"); }
    }

    // Outcome: explicit end-of-game scoreboard. Fires every quota cycle, so we
    // only emit if no scheduled continuation (i.e. the game truly ended).
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.EndOfGameClientRpc))]
    static class P_StartOfRound_EndOfGame
    {
        static void Postfix(StartOfRound __instance, int bodiesInsured, int daysPlayersSurvived, int connectedPlayersOnServer, int scrapCollectedOnServer)
        {
            if (Plugin.Session == null) return;
            try
            {
                // If everyone is fired or there are no connected players, treat as end.
                if (connectedPlayersOnServer <= 0)
                {
                    Plugin.EmitAndReset("all_dead");
                }
                // Otherwise this is the weekly scoreboard; keep the session alive.
            }
            catch { /* ignore */ }
        }
    }

    // Final safety net — ship reset (returning to main menu / new game).
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.ResetShip))]
    static class P_StartOfRound_ResetShip
    {
        static void Postfix() { Plugin.EmitAndReset("abandoned"); }
    }

    // THE key fix: a session that ends by leaving / the host shutting down /
    // alt-F4 never hits the clean end hooks above, so it was never uploaded.
    // Disconnect() fires on any lobby teardown; OnApplicationQuit() on game exit.
    // Both send synchronously (blocking) so the request leaves before teardown.
    [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.Disconnect))]
    static class P_GameNetworkManager_Disconnect
    {
        static void Prefix() { Plugin.EmitAndReset("disconnected", blocking: true); }
    }

    [HarmonyPatch(typeof(GameNetworkManager), "OnApplicationQuit")]
    static class P_GameNetworkManager_OnApplicationQuit
    {
        static void Prefix() { Plugin.EmitAndReset("quit", blocking: true); }
    }

    // ===== Purchases ==========================================================

    // Track whoever has the terminal open right now so the next purchase RPC
    // (which doesn't carry the buyer's id) can be attributed to them.
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.BeginUsingTerminal))]
    static class P_Terminal_BeginUsingTerminal
    {
        static void Postfix()
        {
            if (Plugin.Session == null) return;
            try
            {
                var local = GameNetworkManager.Instance?.localPlayerController;
                if (local != null) Plugin.Session.lastTerminalUser = local.playerUsername;
            }
            catch { }
        }
    }

    // Consumable purchases (Walkie, Pro-Flashlight, Shovel, Stun Grenade,
    // Boombox, TZP-Inhalant, Zap Gun, Lockpicker, Spray Paint, etc.). The
    // RPC carries an array of indices into Terminal.buyableItemsList; we map
    // them to itemName + creditsWorth.
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.BuyItemsServerRpc))]
    static class P_Terminal_BuyItemsServerRpc
    {
        static void Postfix(Terminal __instance, int[] boughtItems, int newGroupCredits, int numItemsInShip)
        {
            if (Plugin.Session == null || __instance == null || boughtItems == null) return;
            try
            {
                var buyer = Plugin.Session.lastTerminalUser ?? Plugin.Session.hostName ?? "(unknown)";
                var slot = Plugin.GetPlayerSlot(buyer);
                if (slot == null) return;
                var list = __instance.buyableItemsList;
                if (list == null) return;
                foreach (var idx in boughtItems)
                {
                    if (idx < 0 || idx >= list.Length) continue;
                    var it = list[idx];
                    if (it == null) continue;
                    string name = (it.itemName ?? "Unknown").Replace(" ", "");
                    int cost = it.creditsWorth;
                    if (!slot.purchases.TryGetValue(name, out var bag))
                        slot.purchases[name] = bag = new PurchaseBag { itemName = name, isUnlockable = false };
                    bag.count++;
                    bag.totalSpent += cost;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[gs] purchase hook err: {e.Message}"); }
        }
    }

    // One-time ship unlockables (Teleporter, Inverse Teleporter, Loud Horn,
    // Romantic Table, Television, Cozy Lights, Disco Ball, Record Player, etc.)
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.BuyShipUnlockableServerRpc))]
    static class P_StartOfRound_BuyShipUnlockableServerRpc
    {
        static void Postfix(StartOfRound __instance, int unlockableID, int newGroupCreditsAmount)
        {
            if (Plugin.Session == null || __instance == null) return;
            try
            {
                var buyer = Plugin.Session.lastTerminalUser ?? Plugin.Session.hostName ?? "(unknown)";
                var slot = Plugin.GetPlayerSlot(buyer);
                if (slot == null) return;
                string name = "ShipUnlockable_" + unlockableID;
                try
                {
                    var list = __instance.unlockablesList;
                    if (list != null && list.unlockables != null && unlockableID >= 0 && unlockableID < list.unlockables.Count)
                    {
                        var unl = list.unlockables[unlockableID];
                        if (unl != null && !string.IsNullOrEmpty(unl.unlockableName))
                            name = unl.unlockableName.Replace(" ", "");
                    }
                }
                catch { }
                if (!slot.purchases.TryGetValue(name, out var bag))
                    slot.purchases[name] = bag = new PurchaseBag { itemName = name, isUnlockable = true };
                bag.count++;
                // Cost derivation: credits before − credits after. We see credits
                // post-purchase in newGroupCreditsAmount; before-credits aren't in
                // scope here. As an estimate, use the unlockable's price if available
                // (router stores it on UnlockableItem.shopSelectionNode?.itemCost).
                // For simplicity, leave totalSpent at 0 — the count is the useful bit.
                bag.isUnlockable = true;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[gs] unlockable hook err: {e.Message}"); }
        }
    }

    // ===== Mob kills ==========================================================

    // Per-enemy last-damager. Map by EnemyAI instance — they don't have stable
    // network IDs we can dictionary by safely across the life of a session.
    [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.HitEnemy))]
    static class P_EnemyAI_HitEnemy
    {
        internal static readonly Dictionary<int, string> LastHitBy = new Dictionary<int, string>();

        static void Postfix(EnemyAI __instance, int force, PlayerControllerB playerWhoHit, bool playHitSFX, int hitID)
        {
            if (Plugin.Session == null || __instance == null || playerWhoHit == null) return;
            try
            {
                LastHitBy[__instance.GetInstanceID()] = playerWhoHit.playerUsername;
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.KillEnemy))]
    static class P_EnemyAI_KillEnemy
    {
        static void Postfix(EnemyAI __instance, bool destroy)
        {
            if (Plugin.Session == null || __instance == null) return;
            try
            {
                int id = __instance.GetInstanceID();
                if (!P_EnemyAI_HitEnemy.LastHitBy.TryGetValue(id, out var killer)) return;
                P_EnemyAI_HitEnemy.LastHitBy.Remove(id);
                var slot = Plugin.GetPlayerSlot(killer);
                if (slot == null) return;
                string type = __instance.GetType().Name;
                slot.mobKills.TryGetValue(type, out int n);
                slot.mobKills[type] = n + 1;
                Plugin.Log.LogInfo($"[gs] mob kill {killer} -> {type}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[gs] kill hook err: {e.Message}"); }
        }
    }

    // ===== Loot left behind ===================================================

    // RoundManager.totalScrapValueInLevel is populated during spawn. Sample it
    // at day start; subtract collected at day end.
    [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnScrapInLevel))]
    static class P_RoundManager_SpawnScrapInLevel
    {
        static void Postfix(RoundManager __instance)
        {
            var d = Plugin.CurrentDay();
            if (d == null || __instance == null) return;
            try { d.totalScrapValueOnMoon = (int)__instance.totalScrapValueInLevel; }
            catch { /* field may not exist on extreme dev branches */ }
        }
    }

    // ===== Apparatus pull =====================================================
    // GrabbableObject hook already records every pickup. Mark the day if the
    // item name is "Apparatus" — it's a single, deliberate action worth flagging.
    // Re-uses the existing P_GrabbableObject_GrabItemOnClient flow via a tiny
    // postfix targeting the same method.
    [HarmonyPatch(typeof(GrabbableObject), nameof(GrabbableObject.GrabItemOnClient))]
    static class P_GrabbableObject_GrabItemOnClient_Apparatus
    {
        static void Postfix(GrabbableObject __instance)
        {
            if (Plugin.Session == null || __instance == null) return;
            try
            {
                if (__instance.itemProperties == null) return;
                if (string.Equals(__instance.itemProperties.itemName, "Apparatus", StringComparison.OrdinalIgnoreCase))
                {
                    var d = Plugin.CurrentDay();
                    if (d != null) d.apparatusPulled = true;
                }
            }
            catch { }
        }
    }
}
