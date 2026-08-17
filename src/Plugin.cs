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
using Unity.Netcode;
using UnityEngine;

namespace GsLethalStatsEmitter
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "net.cproudlock.gslethalstatsemitter";
        public const string NAME = "gs Lethal Company Stats";
        public const string VERSION = "0.5.0";

        // Key we persist inside Lethal Company's own ES3 save file so that every
        // sitting on the same save reports the same playthrough. Dies with the
        // file when a new game overwrites the slot, which is exactly the lifetime
        // a playthrough has.
        internal const string PlaythroughKey = "gsPlaythroughId";

        internal static ManualLogSource Log;
        internal static ConfigEntry<string> IngestUrl;
        internal static ConfigEntry<string> IngestToken;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> EmitOnHostOnly;
        internal static ConfigEntry<int> EmitIntervalSeconds;

        internal static SessionState Session;
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        private Harmony harmony;
        private float lastSnapshotAt;

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
                "Only POST when running as the lobby host (avoids duplicate sittings from co-op clients). Playthrough continuity only works from the host, since only the host owns the save file.");
            EmitIntervalSeconds = Config.Bind("Ingest", "EmitIntervalSeconds", 120,
                "Post a progress snapshot this often while playing, so a crash can't lose the whole sitting. The server upserts on sessionIdLocal, so snapshots replace each other. 0 disables snapshots (upload only when the sitting ends). Floored at 30s.");

            harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{NAME} v{VERSION} loaded · ingest={IngestUrl.Value}");
        }

        // Periodic snapshot. Each one is a complete cumulative document, so the
        // latest simply replaces the previous server-side; nothing is stitched.
        private void Update()
        {
            if (Session == null) return;
            int iv = EmitIntervalSeconds.Value;
            if (iv <= 0) return;
            float now = Time.realtimeSinceStartup;
            if (now - lastSnapshotAt < Math.Max(30, iv)) return;
            lastSnapshotAt = now;
            Send(Session, "in_progress", blocking: false, snapshot: true);
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
            // Item bookkeeping is keyed on NetworkObjectId, which Unity Netcode
            // RECYCLES (RecycleNetworkIds defaults true, 120s delay) and which
            // restarts near 1 in a fresh lobby. Carrying these maps across sittings
            // would credit a new item's delivery to whoever grabbed the long-gone
            // item that used to own its id.
            ResetItemTracking();

            string saveFile;
            string playthroughId = ResolvePlaythroughId(out saveFile);
            Session = new SessionState
            {
                sessionIdLocal = Guid.NewGuid().ToString(),
                playthroughIdLocal = playthroughId,
                saveFileName = saveFile,
                hostSteamId = HostSteamId(sor),
                startQuota = CurrentQuota(),
                hostName = host,
                seed = (sor != null ? sor.randomMapSeed : 0).ToString(CultureInfo.InvariantCulture),
                startedAtUtc = DateTime.UtcNow,
            };
            Log.LogInfo($"[gs] sitting start id={Session.sessionIdLocal} host={host} playthrough={playthroughId ?? "(none)"} save={saveFile ?? "(none)"}");
            return Session;
        }

        // The lobby host's Steam id. Visible to clients, unlike the save file, so
        // it is what lets a non-host reporter's sittings be stitched into one
        // playthrough server-side (host + continuing day numbers).
        internal static string HostSteamId(StartOfRound sor)
        {
            try
            {
                var s = sor ?? StartOfRound.Instance;
                if (s == null || s.allPlayerScripts == null) return null;
                foreach (var pc in s.allPlayerScripts)
                {
                    if (pc != null && pc.isHostPlayerObject && pc.playerSteamId != 0UL)
                        return pc.playerSteamId.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch { }
            return null;
        }

        // A Lethal Company run spans many sittings: you save, quit, and resume days
        // later. The sitting is not the run. We stamp a GUID into the save file
        // itself the first time we see it, so every later sitting on that save
        // reports the same playthrough and the server can merge them.
        //
        // Host only: a client's local save file has nothing to do with the lobby it
        // joined, so reading it would group unrelated sittings together. Client
        // uploads stay standalone, exactly as they were before.
        internal static string ResolvePlaythroughId(out string saveFileName)
        {
            saveFileName = null;
            try
            {
                if (!IsHost()) return null;
                var gnm = GameNetworkManager.Instance;
                if (gnm == null || string.IsNullOrEmpty(gnm.currentSaveFileName)) return null;
                saveFileName = gnm.currentSaveFileName;

                if (ES3.KeyExists(PlaythroughKey, saveFileName))
                {
                    // Overload note: passing a null default is ambiguous between
                    // the ES3Settings and defaultValue overloads.
                    string existing = ES3.Load<string>(PlaythroughKey, saveFileName, string.Empty);
                    if (!string.IsNullOrEmpty(existing)) return existing;
                }
                string fresh = Guid.NewGuid().ToString();
                ES3.Save(PlaythroughKey, fresh, saveFileName);
                Log.LogInfo($"[gs] new playthrough {fresh} stamped into {saveFileName}");
                return fresh;
            }
            catch (Exception e)
            {
                Log.LogWarning($"[gs] playthrough id unavailable: {e.Message}");
                return null;
            }
        }

        // Called when the playthrough truly ends (fired, or the whole crew died).
        // Lethal Company wipes the save itself in most of these cases; clearing our
        // key too means a reused file can never merge a new run into the old one.
        internal static void ClearPlaythroughId(string saveFileName)
        {
            if (string.IsNullOrEmpty(saveFileName)) return;
            try
            {
                if (ES3.KeyExists(PlaythroughKey, saveFileName))
                    ES3.DeleteKey(PlaythroughKey, saveFileName);
            }
            catch (Exception e) { Log.LogWarning($"[gs] could not clear playthrough key: {e.Message}"); }
        }

        // The game's own absolute day counter for this save, which is what makes
        // days from different sittings line up: resuming on day 12 records day 12.
        //
        // HOST ONLY, and that is not a precaution — it is how the game works.
        // gameStats.daysSpent is loaded from the save inside a host-only branch
        // (StartOfRound.SetTimeAndPlanetToSavedSettings, called under IsServer) and
        // is never synced to clients: no NetworkVariable wraps it, and the join RPC
        // OnPlayerConnectedClientRpc does not carry it. A client's copy therefore
        // starts at 0 and only counts departures it witnessed this sitting, so
        // treating it as absolute would silently report day 1 on a save that is
        // really on day 12. Returns -1 when the value cannot be trusted.
        internal static int AbsoluteDayIndex(StartOfRound sor)
        {
            try
            {
                if (!IsHost()) return -1;
                var s = sor ?? StartOfRound.Instance;
                if (s == null) return -1;
                return s.gameStats.daysSpent + 1;
            }
            catch { return -1; }
        }

        // Quota at the start of the sitting. Unlike daysSpent, profitQuota IS
        // synced to clients on join, so this is the one progress signal a non-host
        // reporter can trust, and it is what lets the server stitch a client's
        // sittings into one playthrough.
        internal static int CurrentQuota()
        {
            try { return TimeOfDay.Instance != null ? TimeOfDay.Instance.profitQuota : 0; }
            catch { return 0; }
        }

        // Start (or continue) the day. Hooked to the landing sequence rather than
        // ArriveAtLevel: ArriveAtLevel runs from TravelToLevelEffects, i.e. when the
        // ship finishes ROUTING in orbit. That fires twice if the crew re-routes
        // before landing, and not at all when they land on the moon they are
        // already orbiting, so days were being miscounted both ways.
        internal static void BeginDay(StartOfRound sor)
        {
            var s = EnsureSession(sor);
            if (s == null) return;
            try
            {
                var lvl = sor != null ? sor.currentLevel : null;
                string moon = CleanMoonName(lvl != null ? lvl.PlanetName : null);

                int dayIdx = AbsoluteDayIndex(sor);
                if (dayIdx > 0) s.dayIndexAbsolute = true;
                else dayIdx = s.days.Count + 1; // client: relative to this sitting

                // Quitting mid-day and reloading replays the same day. Overwrite
                // the earlier attempt rather than recording the day twice.
                var day = s.days.Find(d => d.dayIndex == dayIdx);
                if (day == null)
                {
                    day = new DayState { dayIndex = dayIdx };
                    s.days.Add(day);
                }
                day.moon = moon;
                try { day.weather = lvl != null ? lvl.currentWeather.ToString() : null; } catch { }
                day.quotaBefore = CurrentQuota();
                day.creditsBefore = CurrentCredits();
                day.startedAtUtc = DateTime.UtcNow;
                day.endedAtUtc = null;
                Log.LogInfo($"[gs] day {dayIdx}{(s.dayIndexAbsolute ? " (absolute)" : " (this sitting)")} start moon={moon}");
            }
            catch (Exception e) { Log.LogWarning($"[gs] day start err: {e.Message}"); }
        }

        // ---- Item attribution ------------------------------------------------
        // Who last held each item, so the Company desk can pay the right player.
        static readonly Dictionary<ulong, string> holderByItem = new Dictionary<ulong, string>();
        static readonly Dictionary<ulong, KeyValuePair<string, DateTime>> lastGrabAt =
            new Dictionary<ulong, KeyValuePair<string, DateTime>>();

        internal static void ResetItemTracking()
        {
            holderByItem.Clear();
            lastGrabAt.Clear();
            deliveredItems.Clear();
            deliveredBy.Clear();
            lastPurchaseAt.Clear();
            lastSaleAt = DateTime.MinValue;
        }

        internal static ulong ItemKey(GrabbableObject obj)
        {
            try
            {
                if (obj.NetworkObject != null) return obj.NetworkObjectId;
            }
            catch { }
            return unchecked((ulong)(uint)obj.GetInstanceID());
        }

        // Called from BOTH grab hooks. GrabItemOnClient only ever ran for the local
        // player, which is why every co-op partner's haul was missing; the
        // GrabObjectClientRpc hook fires on all clients for any player. Running
        // both means a pickup can arrive twice, so identical (item, player) grabs
        // inside a short window are collapsed — the same shape as the death dedupe.
        internal static void RecordGrab(PlayerControllerB player, GrabbableObject obj)
        {
            if (Session == null || player == null || obj == null) return;
            try
            {
                string name = player.playerUsername;
                if (string.IsNullOrEmpty(name)) return;
                ulong key = ItemKey(obj);
                var now = DateTime.UtcNow;
                if (lastGrabAt.TryGetValue(key, out var prev)
                    && prev.Key == name
                    && (now - prev.Value).TotalSeconds < 1.5)
                    return;
                lastGrabAt[key] = new KeyValuePair<string, DateTime>(name, now);
                // Don't reassign ownership for scrap that is already aboard.
                // Otherwise whoever tidies the cupboard becomes the "deliverer" of
                // everyone else's haul, and carrying scrap to the desk steals the
                // credit that belongs to the player who hauled it off the moon.
                bool alreadyAboard = obj.isInShipRoom || obj.isInElevator;
                if (!alreadyAboard || !holderByItem.ContainsKey(key)) holderByItem[key] = name;

                var slot = GetPlayerSlot(name);
                if (slot == null) return;
                string itemName = SafeItemName(obj);
                int value = obj.scrapValue;
                if (!slot.items.TryGetValue(itemName, out var bag))
                    slot.items[itemName] = bag = new PlayerItemBag { itemName = itemName };
                bag.picks++;
                bag.totalValue += value;
                slot.scrapValueCollected += value;
                var d = CurrentDay();
                if (d != null) d.scrapPiecesCollected += 1;

                if (obj.itemProperties != null
                    && string.Equals(obj.itemProperties.itemName, "Apparatus", StringComparison.OrdinalIgnoreCase)
                    && d != null)
                    d.apparatusPulled = true;
            }
            catch (Exception e) { Log.LogWarning($"[gs] grab hook err: {e.Message}"); }
        }

        internal static string SafeItemName(GrabbableObject g)
        {
            try
            {
                if (g.itemProperties != null && !string.IsNullOrEmpty(g.itemProperties.itemName))
                    return g.itemProperties.itemName.Replace(" ", "");
            }
            catch { }
            return g.GetType().Name;
        }

        // ---- Delivery to the ship -------------------------------------------
        // Credit for scrap belongs to whoever got it aboard, not to whoever
        // carried it from the ship to the Company desk — anyone can do that last
        // part. So we snapshot the ship's contents the moment it leaves the moon
        // and attribute each item to its last holder at that point.
        static readonly HashSet<ulong> deliveredItems = new HashSet<ulong>();
        static readonly Dictionary<ulong, string> deliveredBy = new Dictionary<ulong, string>();

        // Is this item actually aboard? The two flags cover the ordinary carry and
        // drop flows, but they are stamped by the thrower/holder, so scrap tossed
        // in through the door from outside, and bagged or truck-hauled loot, all
        // read false until the game's own late server-side sweep fixes them. The
        // ship-bounds test is the same authority the game uses in GetValueOfAllScrap.
        static bool IsAboard(GrabbableObject obj)
        {
            if (obj == null) return false;
            if (obj.isInShipRoom || obj.isInElevator) return true;
            try
            {
                var bounds = StartOfRound.Instance != null ? StartOfRound.Instance.shipInnerRoomBounds : null;
                if (bounds != null && obj.transform != null) return bounds.bounds.Contains(obj.transform.position);
            }
            catch { }
            return false;
        }

        internal static void RecordDelivery()
        {
            if (Session == null) return;
            try
            {
                // A wipe deletes every piece of scrap aboard (DespawnPropsAtEndOfRound
                // despawns ship scrap when allPlayersDead), so nothing was delivered.
                if (StartOfRound.Instance != null && StartOfRound.Instance.allPlayersDead)
                {
                    Log.LogInfo("[gs] crew wiped, no delivery credited");
                    return;
                }

                var all = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
                if (all == null) return;

                // Scrap inside a belt bag is aboard too, but the game only stamps the
                // contents' flags in its late server-side pass.
                var aboard = new List<GrabbableObject>();
                foreach (var obj in all)
                {
                    if (obj == null) continue;
                    if (IsAboard(obj)) aboard.Add(obj);
                    var bag = obj as BeltBagItem;
                    if (bag != null && bag.objectsInBag != null && IsAboard(obj))
                    {
                        foreach (var inner in bag.objectsInBag)
                            if (inner != null) aboard.Add(inner);
                    }
                }

                int credited = 0;
                foreach (var obj in aboard)
                {
                    if (obj.scrapValue <= 0) continue;
                    ulong key = ItemKey(obj);
                    // Scrap left aboard across days must not be credited again.
                    if (!deliveredItems.Add(key)) continue;
                    string holder;
                    if (!holderByItem.TryGetValue(key, out holder) || string.IsNullOrEmpty(holder)) continue;
                    deliveredBy[key] = holder;
                    var slot = GetPlayerSlot(holder);
                    if (slot == null) continue;
                    slot.scrapDeliveredValue += obj.scrapValue;
                    slot.deliveredPending += obj.scrapValue;
                    string itemName = SafeItemName(obj);
                    if (!slot.items.TryGetValue(itemName, out var ibag))
                        slot.items[itemName] = ibag = new PlayerItemBag { itemName = itemName };
                    ibag.deliveredValue += obj.scrapValue;
                    credited++;
                }
                if (credited > 0) Log.LogInfo($"[gs] delivered {credited} item(s) to ship");
            }
            catch (Exception e) { Log.LogWarning($"[gs] delivery hook err: {e.Message}"); }
        }

        // ---- Selling ---------------------------------------------------------
        // soldValue was declared, serialised, and never written by any code path,
        // so the dashboard's "sold for" column was structurally always zero.
        // Selling is a crew action; the money is attributed back to whoever
        // delivered the scrap.
        static DateTime lastSaleAt = DateTime.MinValue;

        // ---- Purchases -------------------------------------------------------
        // ServerRpc bodies run twice on the host: once at the send stage and again
        // through the loopback execute stage. Grabs, deaths and sales already
        // collapsed their double fire; purchases did not, so every item the host
        // bought was counted twice. Keyed on the purchase contents so two genuinely
        // different purchases in the same second still both count.
        static readonly Dictionary<string, DateTime> lastPurchaseAt = new Dictionary<string, DateTime>();

        internal static bool PurchaseAlreadyCounted(string signature)
        {
            var now = DateTime.UtcNow;
            DateTime prev;
            if (lastPurchaseAt.TryGetValue(signature, out prev) && (now - prev).TotalSeconds < 1.0) return true;
            lastPurchaseAt[signature] = now;
            return false;
        }

        internal static void RecordSale(DepositItemsDesk desk, int profit)
        {
            if (Session == null || profit <= 0) return;
            try
            {
                // Only stamp the guard once the sale is real. Stamping before
                // validating would let a zero-profit call burn the window and
                // swallow the genuine one that follows milliseconds later.
                var now = DateTime.UtcNow;
                if ((now - lastSaleAt).TotalSeconds < 3) return;
                lastSaleAt = now;

                var d = CurrentDay();
                int credAfter = CurrentCredits();

                // Read the desk's object container, NOT itemsOnCounter: that list is
                // only ever filled inside AddObjectToDeskServerRpc, so on a client it
                // is always empty. The container is what the game itself enumerates
                // here to animate the sold items, so it is populated everywhere.
                var items = new List<GrabbableObject>();
                try
                {
                    if (desk != null && desk.deskObjectsContainer != null)
                    {
                        var found = desk.deskObjectsContainer.GetComponentsInChildren<GrabbableObject>();
                        if (found != null)
                            foreach (var it in found) if (it != null && it.scrapValue > 0) items.Add(it);
                    }
                }
                catch { }

                int rawTotal = 0;
                foreach (var it in items) rawTotal += it.scrapValue;

                if (rawTotal > 0)
                {
                    // Each item's share of the payout goes to whoever delivered it.
                    // Scaling by the paid profit rather than recomputing the company
                    // buying rate keeps the parts summing to the whole.
                    foreach (var it in items)
                    {
                        int share = (int)Math.Round((double)profit * it.scrapValue / rawTotal);
                        ulong key = ItemKey(it);
                        string owner;
                        if (!deliveredBy.TryGetValue(key, out owner) || string.IsNullOrEmpty(owner))
                            holderByItem.TryGetValue(key, out owner);
                        // The item is gone after this; drop its keys so a recycled
                        // network id cannot inherit its owner.
                        deliveredBy.Remove(key);
                        holderByItem.Remove(key);
                        deliveredItems.Remove(key);
                        lastGrabAt.Remove(key);
                        if (string.IsNullOrEmpty(owner)) continue;
                        var slot = GetPlayerSlot(owner);
                        if (slot == null) continue;
                        slot.scrapSoldValue += share;
                        slot.deliveredPending = Math.Max(0, slot.deliveredPending - it.scrapValue);
                        string itemName = SafeItemName(it);
                        if (!slot.items.TryGetValue(itemName, out var bag))
                            slot.items[itemName] = bag = new PlayerItemBag { itemName = itemName };
                        bag.soldValue += share;
                    }
                }
                else
                {
                    // Last resort: split across each player's delivered-but-unsold
                    // total, reducing pending by what was actually paid out. Zeroing
                    // it would destroy the ledger on a partial sell, which is the
                    // common case when the crew sells just enough to meet quota.
                    int pending = 0;
                    foreach (var p in Session.players.Values) pending += p.deliveredPending;
                    if (pending > 0)
                    {
                        foreach (var p in Session.players.Values)
                        {
                            if (p.deliveredPending <= 0) continue;
                            int share = (int)Math.Round((double)profit * p.deliveredPending / pending);
                            p.scrapSoldValue += share;
                            p.deliveredPending = Math.Max(0, p.deliveredPending - share);
                        }
                    }
                }

                if (d != null)
                {
                    d.scrapValueCollected += profit;
                    d.creditsAfter = credAfter;
                    d.returnedToCompany = true;
                    Log.LogInfo($"[gs] sold on day {d.dayIndex} profit=${profit} items={items.Count}");
                }
            }
            catch (Exception e) { Log.LogWarning($"[gs] sell hook err: {e.Message}"); }
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
        internal static void EmitAndReset(string outcome, bool blocking = false, bool playthroughEnded = false)
        {
            var s = Session;
            if (s == null) return;
            Session = null; // immediately so concurrent hooks don't double-emit
            s.playthroughEnded = playthroughEnded;
            Send(s, outcome, blocking, snapshot: false);
            if (playthroughEnded) ClearPlaythroughId(s.saveFileName);
        }

        // Shared by the end-of-sitting emit and the periodic snapshot. A snapshot
        // leaves Session intact and does not stamp endedAtUtc, so the sitting keeps
        // accumulating; the server upserts on sessionIdLocal so the newest document
        // wins. That means a crash costs one interval, not the whole sitting.
        internal static void Send(SessionState s, string outcome, bool blocking, bool snapshot)
        {
            if (s == null) return;
            try
            {
                if (EmitOnHostOnly.Value && !IsHost())
                {
                    if (!snapshot) Log.LogInfo($"[gs] not host, skip POST for sitting {s.sessionIdLocal}");
                    return;
                }
                s.outcome = outcome;
                s.isSnapshot = snapshot;
                if (!snapshot) s.endedAtUtc = DateTime.UtcNow;
                ComputeAggregates(s);
                var json = SessionJson.Serialize(s);
                Log.LogInfo($"[gs] {(snapshot ? "snapshot" : "emit")} sitting id={s.sessionIdLocal} playthrough={s.playthroughIdLocal ?? "(none)"} outcome={outcome} days={s.daysSurvived} players={s.players.Count} deaths={s.deaths.Count} bytes={json.Length} blocking={blocking}");
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
            s.daysSurvived = s.days.Count; // days played in THIS sitting
            int total = 0, peak = 0;
            // Absolute day span, so the server can stitch sittings into one
            // playthrough timeline without guessing.
            s.firstDayIndex = 0;
            s.lastDayIndex = 0;
            foreach (var d in s.days)
            {
                total += d.scrapValueCollected;
                if (d.scrapValueCollected > peak) peak = d.scrapValueCollected;
                if (s.firstDayIndex == 0 || d.dayIndex < s.firstDayIndex) s.firstDayIndex = d.dayIndex;
                if (d.dayIndex > s.lastDayIndex) s.lastDayIndex = d.dayIndex;
            }
            s.totalScrapValue = total;
            s.peakScrapValue = peak;
            try { s.finalQuota = TimeOfDay.Instance != null ? TimeOfDay.Instance.profitQuota : 0; } catch { }
            // Trustworthy on the host (loaded from the save) or on a client that has
            // since seen a quota completion; otherwise left unknown.
            if (IsHost()) s.quotasMetKnown = true;
            if (s.quotasMetKnown)
            {
                try { s.quotasMet = TimeOfDay.Instance != null ? TimeOfDay.Instance.timesFulfilledQuota : 0; } catch { }
            }
            s.finalCredits = CurrentCredits();
            s.quotaMargin = s.finalCredits - s.finalQuota;
            foreach (var p in s.players.Values)
            {
                p.metersTraveled = (int)Math.Round(p.metersTraveledFloat);
                // is_host was 0 for every player in every row until now: slots are
                // created by gameplay hooks that don't know who is hosting.
                if (!string.IsNullOrEmpty(s.hostName) && p.playerName == s.hostName) p.isHost = true;
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
        // Stable across every sitting on the same save file. Null when we are not
        // the host, or when the save file could not be read.
        public string playthroughIdLocal;
        public string saveFileName;
        // Sent as a string: Steam ids exceed JS safe-integer range.
        public string hostSteamId;
        public bool playthroughEnded;
        public bool isSnapshot;
        public int firstDayIndex;
        public int lastDayIndex;
        // True only when day numbers came from the save (host). See AbsoluteDayIndex.
        public bool dayIndexAbsolute;
        public int startQuota;
        // timesFulfilledQuota is NOT synced to a joining client; it only arrives
        // with SyncNewProfitQuotaClientRpc when a quota is completed during the
        // sitting. Until then a client's copy reads 0, which is not the same as
        // "no quotas met", so we mark it unknown instead of reporting a lie.
        public bool quotasMetKnown;
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
        // Value this player got aboard the ship. This is the real contribution
        // stat: picking an item up means nothing if it never leaves the moon, and
        // whoever hauls it from the ship to the desk did none of the work.
        public int scrapDeliveredValue;
        // Credits this player's delivered scrap actually earned at the desk.
        public int scrapSoldValue;
        // Delivered but not yet sold; drives the payout split.
        public int deliveredPending;
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
        public int deliveredValue;
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
            Kv(sb, "schemaVersion", 2); Comma(sb);
            Kv(sb, "sessionIdLocal", s.sessionIdLocal); Comma(sb);
            Kv(sb, "playthroughIdLocal", s.playthroughIdLocal); Comma(sb);
            Kv(sb, "saveFileName", s.saveFileName); Comma(sb);
            Kv(sb, "hostSteamId", s.hostSteamId); Comma(sb);
            Kv(sb, "playthroughEnded", s.playthroughEnded); Comma(sb);
            Kv(sb, "isSnapshot", s.isSnapshot); Comma(sb);
            Kv(sb, "firstDayIndex", s.firstDayIndex); Comma(sb);
            Kv(sb, "lastDayIndex", s.lastDayIndex); Comma(sb);
            Kv(sb, "dayIndexAbsolute", s.dayIndexAbsolute); Comma(sb);
            Kv(sb, "startQuota", s.startQuota); Comma(sb);
            Kv(sb, "quotasMetKnown", s.quotasMetKnown); Comma(sb);
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
                Kv(sb, "scrapDeliveredValue", p.scrapDeliveredValue); Comma(sb);
                Kv(sb, "scrapSoldValue", p.scrapSoldValue); Comma(sb);
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
                    Kv(sb, "deliveredValue", it.deliveredValue); Comma(sb);
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

    // The only moment a client learns how many quotas the save has passed: it is
    // not part of the join sync, so before this fires the local value is 0
    // regardless of the truth.
    [HarmonyPatch(typeof(TimeOfDay), nameof(TimeOfDay.SyncNewProfitQuotaClientRpc))]
    static class P_TimeOfDay_SyncNewProfitQuota
    {
        static void Postfix(int newProfitQuota, int overtimeBonus, int fulfilledQuota)
        {
            if (Plugin.Session == null) return;
            Plugin.Session.quotasMetKnown = true;
            Plugin.Session.quotasMet = fulfilledQuota;
        }
    }

    // Routing to a moon in orbit. Only used to open the sitting early (player
    // names are real by now); the day itself begins on landing, below.
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.ArriveAtLevel))]
    static class P_StartOfRound_ArriveAtLevel
    {
        static void Postfix(StartOfRound __instance) { Plugin.EnsureSession(__instance); }
    }

    // Day start — the ship has actually landed. RoundManager's
    // FinishGeneratingNewLevelClientRpc starts this coroutine on every client,
    // exactly once per landing, so it is correct for a client reporter too.
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.openingDoorsSequence))]
    static class P_StartOfRound_OpeningDoorsSequence
    {
        static void Postfix(StartOfRound __instance) { Plugin.BeginDay(__instance); }
    }

    // Day end — ShipHasLeft is invoked once the ship is back in orbit.
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.ShipHasLeft))]
    static class P_StartOfRound_ShipHasLeft
    {
        static void Postfix()
        {
            // Whatever is aboard now is what the crew actually extracted.
            Plugin.RecordDelivery();
            var d = Plugin.CurrentDay();
            if (d == null) return;
            d.endedAtUtc = DateTime.UtcNow;
            d.creditsAfter = Plugin.CurrentCredits();
            if (d.totalScrapValueOnMoon > 0)
                d.scrapValueLeftBehind = Math.Max(0, d.totalScrapValueOnMoon - d.scrapValueCollected);
            Plugin.Log.LogInfo($"[gs] day {d.dayIndex} end credits={d.creditsAfter} left=${d.scrapValueLeftBehind}");
        }
    }

    // Sell at the Company desk. Deliberately NOT hooked on SellItemsOnServer: that
    // is an animation event which fires on every machine before credits move, so
    // it carries no usable profit, and hooking it only served to burn the dedupe
    // window that the real call needs. SellAndDisplayItemProfits runs exactly once
    // per sale per machine (direct call on the host, SellItemsClientRpc on
    // clients) and carries the true paid profit.
    //
    // Resolved by name so a signature change can't break the build; the desk is
    // looked up rather than passed.
    [HarmonyPatch]
    static class P_SellAndDisplayItemProfits
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            foreach (var typeName in new[] { "DepositItemsDesk", "Terminal", "StartOfRound" })
            {
                var t = AccessTools.TypeByName(typeName);
                if (t == null) continue;
                var m = AccessTools.Method(t, "SellAndDisplayItemProfits");
                if (m != null) return m;
            }
            return null;
        }
        static bool Prepare() => TargetMethod() != null;
        static void Prefix(int profit)
        {
            var desk = UnityEngine.Object.FindObjectOfType<DepositItemsDesk>();
            Plugin.RecordSale(desk, profit);
        }
    }

    // Per-item attribution. This hook only ever fires for the LOCAL player, which
    // is why co-op partners had no items and no collected total. Kept because it
    // is the reliable path for our own pickups; the ClientRpc hook below covers
    // everyone else.
    [HarmonyPatch(typeof(GrabbableObject), nameof(GrabbableObject.GrabItemOnClient))]
    static class P_GrabbableObject_GrabItemOnClient
    {
        static void Postfix(GrabbableObject __instance)
        {
            if (Plugin.Session == null || __instance == null) return;
            Plugin.RecordGrab(__instance.playerHeldBy, __instance);
        }
    }

    // The multiplayer path: fires on every client whenever ANY player grabs
    // something, with __instance being the grabbing player.
    [HarmonyPatch(typeof(PlayerControllerB), "GrabObjectClientRpc")]
    static class P_PlayerControllerB_GrabObjectClientRpc
    {
        static void Postfix(PlayerControllerB __instance, bool grabValidated, NetworkObjectReference grabbedObject)
        {
            if (Plugin.Session == null || __instance == null || !grabValidated) return;
            try
            {
                if (!grabbedObject.TryGet(out var netObj) || netObj == null) return;
                var obj = netObj.GetComponentInChildren<GrabbableObject>();
                if (obj == null) return;
                Plugin.RecordGrab(__instance, obj);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[gs] grab rpc hook err: {e.Message}"); }
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
        // Being fired ends the playthrough, not just the sitting.
        static void Postfix() { Plugin.EmitAndReset("quota_failed", playthroughEnded: true); }
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
                // NOT playthroughEnded: a crew wipe does not reset the save. Only
                // being fired calls GameNetworkManager.ResetSavedGameValues, so
                // quota progress survives a wipe and the playthrough continues.
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
                if (Plugin.PurchaseAlreadyCounted($"items:{string.Join(",", boughtItems)}:{newGroupCredits}:{numItemsInShip}")) return;
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
                if (Plugin.PurchaseAlreadyCounted($"unlockable:{unlockableID}:{newGroupCreditsAmount}")) return;
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

    // Apparatus pulls are flagged inside Plugin.RecordGrab, so every pickup path
    // marks the day, not just the local player's.
}
