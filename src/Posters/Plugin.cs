using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using ConfigSync;
using MonoMod.RuntimeDetour;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;

namespace Posters;

[BepInPlugin(MyPluginInfo.Id, MyPluginInfo.Name, MyPluginInfo.Version)]
[BepInProcess("Lethal Company.exe")]
[BepInDependency("tech.rethunk.mortar.ConfigSync")]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;

    private static readonly List<Hook> hooks = [];
    private static readonly Dictionary<string, Texture2D> textures = [];
    // The picture each material should end up with, so a slow load can't overwrite a newer pick.
    private static readonly Dictionary<Material, string> wanted = [];
    private static Loader runner = null!;
    // Packs often ship 4K pictures for a poster a few hundred pixels across on screen.
    private const int MaxSide = 1024;
    private const string Message = "tech.rethunk.mortar.Posters";
    private const string SaveKey = "tech.rethunk.mortar.Posters.paintings";
    // Paintings whose picture came from the save, by network id. Only the host has the save, so it sends these to
    // each player who joins.
    private static readonly Dictionary<ulong, string> saved = [];
    // The picture file each painting shows, which the host writes into the save.
    private static readonly ConditionalWeakTable<GrabbableObject, string> shown = new();
    private static string[] posters = [], tips = [], paintings = [];
    private static SyncedEntry<Change> change = null!;
    private static SyncedEntry<bool> mixGame = null!;
    private static int lastSeed;
    private static GameObject? artPlane;
    private static Texture? gamePoster, gameTips;

    public enum Change
    {
        EachLanding,
        Never,
    }

    private void Awake()
    {
        Log = Logger;
        // BepInEx's own object can go when a scene loads, so the loading runs on one that stays.
        var holder = new GameObject("Posters loader");
        DontDestroyOnLoad(holder);
        holder.hideFlags = HideFlags.HideAndDontSave;
        runner = holder.AddComponent<Loader>();
        Watch.Start(holder, Reload, Paths.PluginPath);
        Find();
        Log.LogInfo($"{MyPluginInfo.Name} {MyPluginInfo.Version} loaded {posters.Length} posters, {tips.Length} tips, {paintings.Length} paintings");
        var shared = SyncedConfig.For(this);
        change = shared.Share(Config.Bind("Ship", "Change", Change.EachLanding,
            "When the ship's posters and tips change: each landing, or never (the same pictures every game)."));
        mixGame = shared.Share(Config.Bind("Ship", "Include game posters", false,
            "Keep the game's own poster and tips pictures in the mix."));
        change.Changed += () => Ship(lastSeed);
        mixGame.Changed += () => Ship(lastSeed);
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        hooks.Add(new Hook(typeof(StartOfRound).GetMethod("Start", any), ShipLoaded));
        hooks.Add(new Hook(typeof(StartOfRound).GetMethod("OnPlayerConnectedClientRpc", any), PlayerConnected));
        hooks.Add(new Hook(typeof(RoundManager).GetMethod(nameof(RoundManager.GenerateNewLevelClientRpc)), LevelGenerated));
        hooks.Add(new Hook(typeof(GrabbableObject).GetMethod(nameof(GrabbableObject.SetScrapValue)), ValueSet));
        hooks.Add(new Hook(typeof(RoundManager).GetMethod(nameof(RoundManager.SyncScrapValuesClientRpc)), ScrapSynced));
        hooks.Add(new Hook(typeof(GameNetworkManager).GetMethod("SaveItemsInShip", any), ItemsSaved));
        hooks.Add(new Hook(typeof(StartOfRound).GetMethod("LoadShipGrabbableItems", any), ItemsLoaded));
    }

    // New and changed pictures show straight away: the lists are read again and the ship and paintings redrawn.
    private static void Reload(IReadOnlyCollection<string> changed)
    {
        if (!changed.Any(f => IsImage(f) || Path.GetExtension(f) == ""))
        {
            return;
        }
        Find();
        foreach (string file in changed)
        {
            if (textures.Remove(file, out Texture2D old))
            {
                Destroy(old);
            }
        }
        Log.LogInfo($"Found {posters.Length} posters, {tips.Length} tips, {paintings.Length} paintings");
        foreach (Material key in wanted.Keys.ToArray())
        {
            if (key == null)
            {
                wanted.Remove(key);
            }
        }
        Ship(lastSeed);
        foreach (GrabbableObject item in FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None))
        {
            Paint(item);
        }
    }

    private static bool IsImage(string file) =>
        file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    // A pack's pictures sit in its mortar/<kind> folder. The folders LethalPosters and LethalPaintings read are still
    // read, so their packs work here too.
    private static void Find()
    {
        posters = Images("posters", "LethalPosters", "posters");
        tips = Images("tips", "LethalPosters", "tips");
        paintings = Images("paintings", "LethalPaintings", "paintings");
    }

    // Sorted, so the same pick is the same picture for everyone with the same packs. A file reachable from both
    // layouts counts once.
    private static string[] Images(string kind, string oldPack, string oldKind) =>
        Pick.Order(
            Folders("mortar", kind).Concat(Folders(oldPack, oldKind))
                .SelectMany(Directory.GetFiles)
                .Where(IsImage)
                .Distinct(),
            Paths.PluginPath)
            .ToArray();

    private static IEnumerable<string> Folders(string parent, string kind) =>
        Directory.GetDirectories(Paths.PluginPath, parent, SearchOption.AllDirectories)
            .Select(dir => Path.Combine(dir, kind))
            .Where(Directory.Exists);

    // The host's map seed comes from the save and a joiner gets it once connected, so everyone redraws then.
    private static void ShipLoaded(Action<StartOfRound> orig, StartOfRound self)
    {
        // On the host, Start loads the saved items, which fills saved, so clear it first.
        saved.Clear();
        wanted.Clear();
        orig(self);
        Listen();
        Ship(self.randomMapSeed);
    }

    // A joiner asks the host for the saved pictures once its ship is loaded, so its handler is in place for the answer.
    private static void Listen()
    {
        NetworkManager network = NetworkManager.Singleton;
        if (network == null || network.CustomMessagingManager == null)
        {
            return;
        }
        network.CustomMessagingManager.RegisterNamedMessageHandler(Message, Received);
        if (!network.IsServer)
        {
            using var ask = new FastBufferWriter(1, Allocator.Temp);
            network.CustomMessagingManager.SendNamedMessage(Message, NetworkManager.ServerClientId, ask);
        }
    }

    private static void Received(ulong sender, FastBufferReader reader)
    {
        NetworkManager network = NetworkManager.Singleton;
        if (network.IsServer)
        {
            using var answer = new FastBufferWriter(256, Allocator.Temp, 1 << 20);
            answer.WriteValueSafe(saved.Count);
            foreach (KeyValuePair<ulong, string> painting in saved)
            {
                answer.WriteValueSafe(painting.Key);
                answer.WriteValueSafe(painting.Value);
            }
            network.CustomMessagingManager.SendNamedMessage(Message, sender, answer, NetworkDelivery.ReliableFragmentedSequenced);
            return;
        }
        reader.ReadValueSafe(out int count);
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out ulong id);
            reader.ReadValueSafe(out string name);
            saved[id] = name;
        }
        foreach (GrabbableObject item in FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None))
        {
            Paint(item);
        }
    }

    // Positions are what the game saves items by, and it spawns them back at the same place.
    private static string Where(Vector3 position) => $"{position.x:F2},{position.y:F2},{position.z:F2}";

    private static void ItemsSaved(Action<GameNetworkManager> orig, GameNetworkManager self)
    {
        orig(self);
        if (StartOfRound.Instance == null || StartOfRound.Instance.isChallengeFile)
        {
            return;
        }
        var pictures = new Dictionary<string, string>();
        foreach (GrabbableObject item in FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None))
        {
            if (shown.TryGetValue(item, out string name))
            {
                pictures[Where(item.transform.position)] = name;
            }
        }
        ES3.Save(SaveKey, pictures, self.currentSaveFileName);
    }

    private static void ItemsLoaded(Action<StartOfRound> orig, StartOfRound self)
    {
        orig(self);
        string file = GameNetworkManager.Instance.currentSaveFileName;
        if (!ES3.KeyExists(SaveKey, file))
        {
            return;
        }
        Dictionary<string, string> pictures;
        try
        {
            pictures = ES3.Load<Dictionary<string, string>>(SaveKey, file);
        }
        catch (Exception exception)
        {
            Log.LogWarning($"Could not load saved pictures from {file}: {exception.Message}");
            return;
        }
        foreach (GrabbableObject item in FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None))
        {
            if (IsPainting(item) && pictures.TryGetValue(Where(item.transform.position), out string name))
            {
                saved[item.NetworkObjectId] = name;
                Paint(item);
            }
        }
    }

    private static void PlayerConnected(Action<StartOfRound, ulong, int, ulong[], int, int, int, int, int, int, int, bool> orig, StartOfRound self, ulong clientId, int connectedPlayers, ulong[] connectedPlayerIdsOrdered, int assignedPlayerObjectId, int serverMoneyAmount, int levelID, int profitQuota, int timeUntilDeadline, int quotaFulfilled, int randomSeed, bool isChallenge)
    {
        orig(self, clientId, connectedPlayers, connectedPlayerIdsOrdered, assignedPlayerObjectId, serverMoneyAmount, levelID, profitQuota, timeUntilDeadline, quotaFulfilled, randomSeed, isChallenge);
        Ship(self.randomMapSeed);
    }

    private static void LevelGenerated(Action<RoundManager, int, int, int, int, int[]> orig, RoundManager self, int randomSeed, int levelID, int moldIterations, int moldStartPosition, int[] syncDestroyedMold)
    {
        orig(self, randomSeed, levelID, moldIterations, moldStartPosition, syncDestroyedMold);
        Ship(randomSeed);
    }

    private static void Ship(int seed)
    {
        lastSeed = seed;
        foreach (Material key in wanted.Keys.ToArray())
        {
            if (key == null)
            {
                wanted.Remove(key);
            }
        }
        var plane = GameObject.Find("HangarShip/Plane.001");
        if (plane == null)
        {
            return;
        }
        Material[] materials = plane.GetComponent<MeshRenderer>().materials;
        // Each lobby loads a new ship, so remember its own pictures before replacing any.
        if (plane != artPlane)
        {
            artPlane = plane;
            gamePoster = materials[0].mainTexture;
            gameTips = materials[1].mainTexture;
        }
        if (change.Value == Change.Never)
        {
            seed = 0;
        }
        Show(materials[0], posters, seed, mixGame.Value ? gamePoster : null);
        Show(materials[1], tips, seed, mixGame.Value ? gameTips : null);
    }

    // Paintings loaded from a save.
    private static void ValueSet(Action<GrabbableObject, int> orig, GrabbableObject self, int value)
    {
        orig(self, value);
        Paint(self);
    }

    // New scrap gets its value and then the game's own painting material, so this runs after both.
    private static void ScrapSynced(Action<RoundManager, NetworkObjectReference[], int[]> orig, RoundManager self, NetworkObjectReference[] spawnedScrap, int[] allScrapValue)
    {
        orig(self, spawnedScrap, allScrapValue);
        foreach (NetworkObjectReference scrap in spawnedScrap)
        {
            if (scrap.TryGet(out NetworkObject item) && item.TryGetComponent(out GrabbableObject grabbable))
            {
                Paint(grabbable);
            }
        }
    }

    private static bool IsPainting(GrabbableObject item) => item.itemProperties != null && item.itemProperties.itemName == "Painting";

    // A painting keeps the picture the save recorded while that picture's file is still here; otherwise the value,
    // which is the same for everyone, picks it.
    private static void Paint(GrabbableObject item)
    {
        if (!IsPainting(item) || !item.TryGetComponent(out MeshRenderer renderer))
        {
            return;
        }
        string? file = null;
        if (item.IsSpawned && saved.TryGetValue(item.NetworkObjectId, out string name))
        {
            file = Array.Find(paintings, f => Path.GetFileName(f) == name);
        }
        file = file != null ? ShowFile(renderer.material, file) : Show(renderer.material, paintings, item.scrapValue);
        shown.Remove(item);
        if (file != null)
        {
            shown.Add(item, Path.GetFileName(file));
        }
    }

    // game, when set, is one more pick after the files: the game's own picture. Returns the file shown, if any.
    private static string? Show(Material material, string[] files, int seed, Texture? game = null)
    {
        int count = files.Length + (game != null ? 1 : 0);
        if (count == 0)
        {
            return null;
        }
        int pick = (seed % count + count) % count;
        if (pick == files.Length)
        {
            material.mainTexture = game;
            return null;
        }
        return ShowFile(material, files[pick]);
    }

    private static string ShowFile(Material material, string file)
    {
        wanted[material] = file;
        if (textures.TryGetValue(file, out Texture2D texture))
        {
            material.mainTexture = texture;
        }
        else
        {
            runner.StartCoroutine(Load(file, material));
        }
        return file;
    }

    // Unity reads and decodes the file off the main thread, so a redraw doesn't stall the game.
    private static IEnumerator Load(string file, Material material)
    {
        using UnityWebRequest request = UnityWebRequestTexture.GetTexture(FileUrl(file), nonReadable: true);
        yield return request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success)
        {
            Log.LogError($"{file}: {request.error}");
            yield break;
        }
        if (!textures.TryGetValue(file, out Texture2D texture))
        {
            texture = Fit(DownloadHandlerTexture.GetContent(request));
            textures[file] = texture;
        }
        if (material != null && wanted.TryGetValue(material, out string latest) && latest == file)
        {
            material.mainTexture = texture;
        }
    }

    private static Texture2D Fit(Texture2D full)
    {
        var (width, height) = Pick.FitSize(full.width, full.height, MaxSide);
        if (width == full.width && height == full.height)
        {
            return full;
        }
        var target = RenderTexture.GetTemporary(width, height);
        Graphics.Blit(full, target);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = target;
        var small = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: true);
        small.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        small.Apply(updateMipmaps: true, makeNoLongerReadable: true);
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(target);
        Destroy(full);
        return small;
    }

    // Unity under Wine misreads a percent-escaped non-ASCII name, so only the characters a URL can't hold as they are
    // get escaped.
    private static string FileUrl(string file) =>
        "file:///" + file.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23");

    private sealed class Loader : MonoBehaviour;
}
