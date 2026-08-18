using UnityEngine;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;
using CoreLibrary;
using System.Reflection;
using System.Collections;

// Adds a passive infection mechanic that can kill players — directly affects gameplay.
[assembly: CoreLibrary.AffectsGameplay(true)]

namespace InfectionMod;

[BepInPlugin("rer.wmo.mods.infectionmod", "Infection Mod", "1.0.0")]
[BepInDependency("rer.wmo.mods.corelibrary", BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;

    private Harmony _harmony;

    private const float INFECTION_KILL_THRESHOLD = 100f;  
    private const float INFECTION_CHECK_INTERVAL = 1f; 
    
    // Health-based multipliers
    private const float INFECTION_CAUTION_MULTIPLIER = 3f; 
    private const float INFECTION_DANGER_MULTIPLIER = 5f;
    private const float INFECTION_DOWNED_MULTIPLIER = 10f;
    
    // Health thresholds (percentages)
    private const float HEALTH_CAUTION_THRESHOLD = 0.5f; 
    private const float HEALTH_DANGER_THRESHOLD = 0.25f;
    
    // Item constants
    private const string ANTIVIRAL_ITEM_ID   = "997";
    private const string ANTIVIRAL_RECIPE_ID = "TemporalAntiviral";
    private const string ANTIVIRUS_ITEM_ID   = "998";
    private const string ANTIVIRUS_RECIPE_ID = "Antivirus";
    private const float  INFECTION_HEAL_AMOUNT    = 20f;
    private const float  INFECTION_PAUSE_DURATION = 120f;
    private const string CHEMICAL_MATERIAL_ID  = "Chemicals";
    private const string SCRAPS_MATERIAL_ID    = "Scraps";
    private const string RED_HERB_SOURCE_ID    = "202"; // Red Herb sprite → Temporal Antiviral
    private const string BLUE_HERB_SOURCE_ID   = "203"; // Blue Herb sprite → Antivirus

    // Damage-based infection amplification
    private const float INFECTION_HIT_INCREASE   = 1.5f;   // any enemy hit (melee, ranged, status)
    
    private static System.Collections.Generic.Dictionary<string, float> _jobInfectionRates = new System.Collections.Generic.Dictionary<string, float>();
    private static bool _jobRatesInitialized = false;

    private static float _lastCheckTime = 0f;
    private static bool _hasLoggedKill = false;
    
    // Custom infection tracker
    private static float _customInfection = 0f;
    private static float _infectionPausedUntil = 0f;
    private static int _lastLoggedMilestone = 0;
    private static string _currentLevel = "";

    // SFX
    private const float INFECTION_HEARTBEAT_THRESHOLD = 90f;
    private static bool _infectionHeartbeatPlaying = false;
    // AudioManager is internal — access via cached reflection
    private static MethodInfo _audioPlaySFX = null;
    private static MethodInfo _audioStopSFX = null;

    private static void EnsureAudioReflection()
    {
        if (_audioPlaySFX != null) return;
        var audioType = typeof(PlayerController).Assembly.GetType("Toked.AudioManager");
        if (audioType == null) { Logger.LogError("[SFX] Could not find Toked.AudioManager via reflection"); return; }
        _audioPlaySFX = audioType.GetMethod("PlaySFX", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(string) }, null);
        _audioStopSFX = audioType.GetMethod("StopSFX", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(string) }, null);
    }
    
    private static string _currentPlayerJob = "Default";

    // Milestone notifications (every 10%)
    // Monologue term IDs for the speech bubble sent to all players at 50% and 90%.
    // We register these as custom I2 Localization terms at startup.
    private const int MONOLOGUE_ID_50  = 9990;   // "Interaction/Monologue-9990"
    private const int MONOLOGUE_ID_90  = 9991;   // "Interaction/Monologue-9991"
    private static readonly int[] CHAT_BUBBLE_MILESTONES = { 50, 90 };
    private static readonly int[] MONOLOGUE_IDS           = { MONOLOGUE_ID_50, MONOLOGUE_ID_90 };

    // Rotating on-screen messages for the 10% milestones (local notification only).
    // Two pools: below 50% (mild) and 50%+ (severe). Picked by milestone index so
    // messages escalate naturally as infection climbs.
    private static readonly string[] MILD_MESSAGES = {
        "You feel more sickly...",
        "You have a headache...",
        "Your skin feels warm...",
        "You feel a slight dizziness...",
    };
    private static readonly string[] SEVERE_MESSAGES = {
        "Your vision blurs for a moment...",
        "Your hands are trembling...",
        "You can barely keep your eyes open...",
        "Something is very wrong...",
    };

    
    private static TextMeshProUGUI _infectionTextUI = null;
    private static Image _infectionCircleBg = null;
    private static GameObject _infectionUIContainer = null;
    private static RectTransform[] _orbiterRects = null;
    private static Image[] _orbiterImages = null;
    private static Vector2[] _orbiterVelocities = null;
    private const int ORBITER_COUNT = 5;
    private const float CIRCLE_INNER_RADIUS = 25f;
    private const float ORBITER_SIZE = 5f;
    private static float _lastOrbiterTime = 0f;

    private void Awake()
    {
        Logger = base.Logger;
        Logger.LogInfo("[InfectionMod] Awake() start");

        try { RegisterItems(); }
        catch (System.Exception ex) { Logger.LogError($"[InfectionMod] RegisterItems failed: {ex}"); }

        try
        {
            GameEvents.OnDataManagerAwake += OnDataManagerAwake;
            GameEvents.OnPlayerUpdate += OnPlayerUpdate;
            GameEvents.OnInventoryShown += OnInventoryShown;
            Logger.LogInfo("[InfectionMod] CoreLibrary events subscribed.");
        }
        catch (System.Exception ex) { Logger.LogError($"[InfectionMod] Event subscription failed: {ex}"); }

        Logger.LogInfo("[InfectionMod] Awake() complete.");
    }

    private void OnDestroy() => _harmony?.UnpatchSelf();
    
    private static void RegisterItems()
    {
        try
        {
            // --- Temporal Antiviral (997) — Red Herb sprite, lowers infection by 20% ---
            var antiviralItem = new CoreLibrary.CustomItemDefinition
            {
                ItemId = ANTIVIRAL_ITEM_ID,
                ItemName = "Temporal Antiviral",
                ItemDescription = "Lowers total infection rate by 20%",
                ItemType = 3, // HealingItem
                ItemCategory = 0,
                IsUsable = true,
                IsStackable = false,
                MaxStack = 10,
                SpriteSourceItemId = RED_HERB_SOURCE_ID,
                OnItemUsed = (invObj) =>
                {
                    _customInfection = Mathf.Max(0f, _customInfection - INFECTION_HEAL_AMOUNT);
                    _lastLoggedMilestone = Mathf.FloorToInt(_customInfection / 10f);
                    UpdateInfectionText();
                }
            };
            CoreLibrary.CustomItemHelper.RegisterItem(antiviralItem);

            var antiviralRecipe = new CoreLibrary.CustomRecipeDefinition
            {
                RecipeId = ANTIVIRAL_RECIPE_ID,
                ItemId = ANTIVIRAL_ITEM_ID,
                CraftStation = 1,
                RecipeItemType = 1,
                RecipeCategory = 0,
                CraftAmount = 1,
                Ingredients = new System.Collections.Generic.List<CoreLibrary.RecipeIngredient>
                {
                    new CoreLibrary.RecipeIngredient { MaterialId = SCRAPS_MATERIAL_ID, Amount = 10 }
                }
            };
            CoreLibrary.CustomItemHelper.RegisterRecipe(antiviralRecipe);

            // --- Antivirus (998) — Blue Herb sprite, pauses infection for 120 seconds ---
            var antivirusItem = new CoreLibrary.CustomItemDefinition
            {
                ItemId = ANTIVIRUS_ITEM_ID,
                ItemName = "Antivirus",
                ItemDescription = "Pauses infection spread for 2 minutes",
                ItemType = 3, // HealingItem
                ItemCategory = 0,
                IsUsable = true,
                IsStackable = false,
                MaxStack = 10,
                SpriteSourceItemId = BLUE_HERB_SOURCE_ID,
                OnItemUsed = (invObj) =>
                {
                    _infectionPausedUntil = Time.time + INFECTION_PAUSE_DURATION;
                    UpdateInfectionText();
                }
            };
            CoreLibrary.CustomItemHelper.RegisterItem(antivirusItem);

            var antivirusRecipe = new CoreLibrary.CustomRecipeDefinition
            {
                RecipeId = ANTIVIRUS_RECIPE_ID,
                ItemId = ANTIVIRUS_ITEM_ID,
                CraftStation = 1,
                RecipeItemType = 1,
                RecipeCategory = 0,
                CraftAmount = 1,
                Ingredients = new System.Collections.Generic.List<CoreLibrary.RecipeIngredient>
                {
                    new CoreLibrary.RecipeIngredient { MaterialId = CHEMICAL_MATERIAL_ID, Amount = 2 }
                }
            };
            CoreLibrary.CustomItemHelper.RegisterRecipe(antivirusRecipe);

            Logger.LogInfo($"[Items] Registered Temporal Antiviral ({ANTIVIRAL_ITEM_ID}) and Antivirus ({ANTIVIRUS_ITEM_ID})");
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[Items] Failed to register: {ex.Message}");
        }
    }
    
    private static void InitializeJobInfectionRates()
    {
        if (_jobRatesInitialized)
        {
            return;
        }

        try
        {
            _jobInfectionRates.Clear();
            // Explicit per-perk infection rates
            _jobInfectionRates["BODYBUILDER"] = 0.0283f;
            _jobInfectionRates["ROOKIEAGENT"] = 0.0185f;
            _jobInfectionRates["SPRINTER"] = 0.0340f;
            _jobInfectionRates["SECURITYGUARD"] = 0.0247f;
            _jobInfectionRates["MEDIC"] = 0.0211f;
            _jobInfectionRates["FIREFIGHTER"] = 0.0311f;

            _jobRatesInitialized = true;
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[JobInfection] Failed to set infection rates: {ex.Message}");
        }
    }
    
    private static float GetJobInfectionRate(string jobId)
    {
        const float DEFAULT_RATE = 0.0185f;
        
        if (!_jobRatesInitialized)
        {
            Logger.LogWarning("[JobInfection] Job rates not initialized yet, using default rate");
            return DEFAULT_RATE;
        }
        
        if (string.IsNullOrEmpty(jobId))
        {
            Logger.LogWarning("[JobInfection] Job ID is null or empty, using default rate");
            return DEFAULT_RATE;
        }
        
        if (_jobInfectionRates.TryGetValue(jobId, out float rate))
        {
            return rate;
        }
        
        Logger.LogWarning($"[JobInfection] Unknown job ID '{jobId}', using default rate");
        return DEFAULT_RATE;
    }
    
    private void OnDataManagerAwake()
    {
        try
        {
            InitializeJobInfectionRates();
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[Init] Failed to initialize: {ex.Message}");
        }

        // Register custom monologue terms used by the speech bubble at 50% and 90%.
        // LocalizationHelper maps these to "Interaction/Monologue-{id}" which is what
        // ChatSystem.ShowBaloonChat looks up for ChatType.MONOLOGUE.
        try
        {
            LocalizationHelper.RegisterTerm($"Interaction/Monologue-{MONOLOGUE_ID_50}", "I don't feel so good...");
            LocalizationHelper.RegisterTerm($"Interaction/Monologue-{MONOLOGUE_ID_90}", "I don't feel so good...");
            Logger.LogInfo("[InfectionMod] Monologue localization terms registered.");
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[InfectionMod] Localization registration failed: {ex.Message}");
        }

        // Apply Harmony patches here — DataManager.Awake fires well after HarmonyX
        // finalization, so patches will not be clobbered by the Chainloader finalization pass.
        if (_harmony == null)
        {
            try
            {
                _harmony = new Harmony("rer.wmo.mods.infectionmod");
                _harmony.PatchAll(typeof(AddSubHealthPatch));
                _harmony.PatchAll(typeof(InfectionReviveTimerPatch));
                Logger.LogInfo("[InfectionMod] Harmony patches applied (PlayerNetwork.AddSubHealth, PlayerController.FixedUpdate).");
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"[InfectionMod] Harmony patching failed in OnDataManagerAwake: {ex}");
            }
        }
    }
    
    private static void OnPlayerUpdate(object player)
    {
        try
        {
            string currentLevel = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            bool isInLobby = currentLevel.Contains("Lobby") || currentLevel.Contains("lobby");

            if (currentLevel != _currentLevel)
            {
                _currentLevel = currentLevel;
                _hasLoggedKill = false;
                _infectionPausedUntil = 0f;
                _infectionKillPlayerIdx = -1;
                _infectionHeartbeatPlaying = false; // flag reset; actual StopSFX handled per-branch below

                // Clear stale Unity object references from previous scene
                _infectionTextUI = null;
                _infectionCircleBg = null;
                _infectionUIContainer = null;
                _orbiterRects = null;
                _orbiterImages = null;
                _orbiterVelocities = null;

                // On every lobby entry: clear any local permadeath state so the
                // player appears alive (infection total persists intentionally —
                // they can use Antivirus between missions).
                if (isInLobby)
                {
                    var pc = player as PlayerController;
                    if (pc != null && pc.isPermadeath)
                    {
                        pc.isPermadeath = false;
                        Logger.LogInfo("[Lobby] Reset permadeath state on level change - player alive with infection");
                    }
                    StopInfectionHeartbeat(pc);
                    Logger.LogInfo($"[Lobby] Entered lobby scene '{currentLevel}', infection={_customInfection:F2}%");
                }
            }

            if (isInLobby)
            {
                UpdateInfectionUIIfOpen();
                return;
            }

            try
            {
                string perkId = PlayerHelper.GetPerkId(player);
                
                if (!string.IsNullOrEmpty(perkId) && _currentPlayerJob != perkId)
                {
                    _currentPlayerJob = perkId;
                    float jobInfectionRate = GetJobInfectionRate(_currentPlayerJob);
                }
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"[Job] Exception getting perk: {ex.Message}");
            }

            float currentTime = Time.time;
            if (currentTime - _lastCheckTime >= INFECTION_CHECK_INTERVAL)
            {
                _lastCheckTime = currentTime;
                UpdateInfection(player);
            }
            
            UpdateInfectionUIIfOpen();
            UpdateInfectionHeartbeat(player);

            if (_customInfection >= INFECTION_KILL_THRESHOLD && !_hasLoggedKill)
            {
                _hasLoggedKill = true;
                KillPlayerFromInfection(player);
            }
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[Update] Exception: {ex.Message}");
        }
    }
    
    private static void UpdateInfection(object player)
    {
        try
        {
            if (Time.time < _infectionPausedUntil) return;

            float currentHealth = PlayerHelper.GetHealth(player);
            if (currentHealth <= 0f)
            {
                // Player is downed — accumulate at maximum (10x) rate; very punishing
                _customInfection = Mathf.Min(INFECTION_KILL_THRESHOLD,
                    _customInfection + GetJobInfectionRate(_currentPlayerJob) * INFECTION_DOWNED_MULTIPLIER);
                int downedMilestone = Mathf.FloorToInt(_customInfection / 10f);
                if (downedMilestone > _lastLoggedMilestone)
                {
                    int newMilestone = downedMilestone * 10;
                    _lastLoggedMilestone = downedMilestone;
                    FireMilestoneNotification(player, newMilestone);
                }
                return;
            }

            float baseRate = GetJobInfectionRate(_currentPlayerJob);
            float maxHealth = PlayerHelper.GetMaxHealth(player);
            float healthPercent = currentHealth / maxHealth;
            float multiplier = 1f;

            if (healthPercent <= HEALTH_DANGER_THRESHOLD)
            {
                multiplier = INFECTION_DANGER_MULTIPLIER;
            }
            else if (healthPercent <= HEALTH_CAUTION_THRESHOLD)
            {
                multiplier = INFECTION_CAUTION_MULTIPLIER;
            }

            float increaseAmount = baseRate * multiplier;
            _customInfection = Mathf.Min(INFECTION_KILL_THRESHOLD, _customInfection + increaseAmount);

            int currentMilestone = Mathf.FloorToInt(_customInfection / 10f);
            if (currentMilestone > _lastLoggedMilestone)
            {
                int newMilestone = currentMilestone * 10; // e.g. 3 → 30%
                _lastLoggedMilestone = currentMilestone;
                FireMilestoneNotification(player, newMilestone);
            }
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[Update] Exception updating infection: {ex.Message}");
        }
    }

    private static void UpdateInfectionText()
    {
        if (_infectionTextUI == null)
            return;
        
        _infectionTextUI.text = $"{_customInfection:00.00}%";
        
        float t = _customInfection / INFECTION_KILL_THRESHOLD;
        Color gaugeColor = GetInfectionColor(_customInfection);
        
        _infectionTextUI.color = gaugeColor;
        
        if (_infectionCircleBg != null)
        {
            _infectionCircleBg.color = new Color(
                gaugeColor.r * 0.25f,
                gaugeColor.g * 0.25f,
                gaugeColor.b * 0.25f,
                0.75f
            );
        }
        
        UpdateOrbiters(t, gaugeColor);
    }
    
    private static Color GetInfectionColor(float infection)
    {
        if (infection < 40f)
            return new Color(0.2f, 0.85f, 0.2f, 1f);       // Green
        if (infection < 50f)
            return new Color(0.1f, 0.55f, 0.1f, 1f);       // Darker green
        if (infection < 70f)
            return new Color(0.95f, 0.85f, 0.1f, 1f);      // Yellow
        if (infection < 90f)
            return new Color(0.9f, 0.15f, 0.1f, 1f);       // Red

        return new Color(0.5f, 0.05f, 0.05f, 1f);          // Dark red
    }
    
    private static void UpdateOrbiters(float t, Color gaugeColor)
    {
        if (_orbiterRects == null || _orbiterImages == null || _orbiterVelocities == null)
            return;

        if (Time.time < _infectionPausedUntil) return;

        float currentTime = Time.time;
        float dt = Mathf.Min(currentTime - _lastOrbiterTime, 0.05f);
        _lastOrbiterTime = currentTime;
        
        if (dt <= 0f) return;
        
        float speed = 15f + t * 40f;
        float orbiterRadius = ORBITER_SIZE * 0.5f;
        
        for (int i = 0; i < ORBITER_COUNT; i++)
        {
            if (_orbiterRects[i] == null) continue;
            
            Vector2 pos = _orbiterRects[i].anchoredPosition;
            pos += _orbiterVelocities[i] * speed * dt;
            
            float dist = pos.magnitude;
            float maxDist = CIRCLE_INNER_RADIUS - orbiterRadius;
            if (dist > maxDist && dist > 0f)
            {
                Vector2 normal = pos.normalized;
                _orbiterVelocities[i] = _orbiterVelocities[i] - 2f * Vector2.Dot(_orbiterVelocities[i], normal) * normal;
                pos = normal * maxDist;
            }
            
            _orbiterRects[i].anchoredPosition = pos;
        }
        
        for (int i = 0; i < ORBITER_COUNT; i++)
        {
            if (_orbiterRects[i] == null) continue;
            for (int j = i + 1; j < ORBITER_COUNT; j++)
            {
                if (_orbiterRects[j] == null) continue;
                
                Vector2 posA = _orbiterRects[i].anchoredPosition;
                Vector2 posB = _orbiterRects[j].anchoredPosition;
                Vector2 diff = posA - posB;
                float distSq = diff.sqrMagnitude;
                float minDist = ORBITER_SIZE;
                
                if (distSq < minDist * minDist && distSq > 0.001f)
                {
                    Vector2 normal = diff.normalized;
                    float overlap = minDist - Mathf.Sqrt(distSq);
                    _orbiterRects[i].anchoredPosition = posA + normal * (overlap * 0.5f);
                    _orbiterRects[j].anchoredPosition = posB - normal * (overlap * 0.5f);
                    
                    float dotI = Vector2.Dot(_orbiterVelocities[i], normal);
                    float dotJ = Vector2.Dot(_orbiterVelocities[j], normal);
                    _orbiterVelocities[i] += (dotJ - dotI) * normal;
                    _orbiterVelocities[j] += (dotI - dotJ) * normal;
                }
            }
        }
        
        float alpha = Mathf.Lerp(0.25f, 0.85f, t);
        for (int i = 0; i < ORBITER_COUNT; i++)
        {
            if (_orbiterImages[i] == null) continue;
            _orbiterImages[i].color = new Color(gaugeColor.r, gaugeColor.g, gaugeColor.b, alpha);
        }
    }
    
    private static void UpdateInfectionUIIfOpen()
    {
        try
        {
            if (!UIHelper.IsInventoryOpen())
                return;
            
            if (_infectionTextUI == null)
                CreateOrUpdateInfectionUI();
            
            UpdateInfectionText();
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[UI] Exception updating UI: {ex.Message}");
        }
    }
    
    private static void CreateOrUpdateInfectionUI()
    {
        try
        {
            var inventoryTransform = UIHelper.GetInventoryTransform();
            if (inventoryTransform == null)
                return;

            if (_infectionTextUI == null)
            {
                var existingUI = inventoryTransform.Find("InfectionCounter");
                
                if (existingUI != null)
                {
                    _infectionUIContainer = existingUI.gameObject;
                    _infectionTextUI = _infectionUIContainer.GetComponentInChildren<TextMeshProUGUI>();
                    _infectionCircleBg = _infectionUIContainer.transform.Find("CircleBg")?.GetComponent<Image>();
                }
                else
                {
                    TMP_FontAsset gameFont = null;
                    var existingTmp = inventoryTransform.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (existingTmp != null)
                        gameFont = existingTmp.font;
                    
                    var circleSprite = CreateCircleSprite(64);
                    
                    _infectionUIContainer = new GameObject("InfectionCounter");
                    _infectionUIContainer.transform.SetParent(inventoryTransform, false);

                    var containerRect = _infectionUIContainer.AddComponent<RectTransform>();
                    containerRect.anchorMin = new Vector2(1f, 1f);
                    containerRect.anchorMax = new Vector2(1f, 1f);
                    containerRect.pivot = new Vector2(0.5f, 0.5f);
                    containerRect.anchoredPosition = new Vector2(350f, 100f);
                    containerRect.sizeDelta = new Vector2(200, 100);
                    
                    // top
                    var textObj = new GameObject("Label");
                    textObj.transform.SetParent(_infectionUIContainer.transform, false);
                    var textRect = textObj.AddComponent<RectTransform>();
                    textRect.anchorMin = new Vector2(0f, 1f);
                    textRect.anchorMax = new Vector2(1f, 1f);
                    textRect.pivot = new Vector2(0.5f, 1f);
                    textRect.anchoredPosition = new Vector2(0f, 0f);
                    textRect.sizeDelta = new Vector2(200f, 24f);

                    _infectionTextUI = textObj.AddComponent<TextMeshProUGUI>();
                    if (gameFont != null)
                        _infectionTextUI.font = gameFont;
                    _infectionTextUI.fontSize = 16;
                    _infectionTextUI.alignment = TextAlignmentOptions.Center;
                    _infectionTextUI.enableAutoSizing = false;
                    _infectionTextUI.outlineWidth = 0.15f;
                    _infectionTextUI.outlineColor = new Color(0f, 0f, 0f, 0.8f);
                    
                    // Circle background
                    var bgObj = new GameObject("CircleBg");
                    bgObj.transform.SetParent(_infectionUIContainer.transform, false);
                    var bgRect = bgObj.AddComponent<RectTransform>();
                    bgRect.anchorMin = new Vector2(0.5f, 0f);
                    bgRect.anchorMax = new Vector2(0.5f, 0f);
                    bgRect.pivot = new Vector2(0.5f, 0f);
                    bgRect.anchoredPosition = new Vector2(0f, 0f);
                    bgRect.sizeDelta = new Vector2(60f, 60f);
                    _infectionCircleBg = bgObj.AddComponent<Image>();
                    _infectionCircleBg.sprite = circleSprite;
                    _infectionCircleBg.color = new Color(0.15f, 0.15f, 0.15f, 0.7f);
                    
                    // Floating circles
                    var orbitContainer = new GameObject("Orbiters");
                    orbitContainer.transform.SetParent(_infectionUIContainer.transform, false);
                    var orbitRect = orbitContainer.AddComponent<RectTransform>();
                    orbitRect.anchorMin = new Vector2(0.5f, 0f);
                    orbitRect.anchorMax = new Vector2(0.5f, 0f);
                    orbitRect.pivot = new Vector2(0.5f, 0.5f);
                    orbitRect.anchoredPosition = new Vector2(0f, 30f); // Center of the 60px circle
                    orbitRect.sizeDelta = Vector2.zero;
                    
                    var smallCircleSprite = CreateCircleSprite(16);
                    _orbiterRects = new RectTransform[ORBITER_COUNT];
                    _orbiterImages = new Image[ORBITER_COUNT];
                    _orbiterVelocities = new Vector2[ORBITER_COUNT];
                    _lastOrbiterTime = Time.time;
                    
                    for (int i = 0; i < ORBITER_COUNT; i++)
                    {
                        var orb = new GameObject($"Orb{i}");
                        orb.transform.SetParent(orbitContainer.transform, false);
                        _orbiterRects[i] = orb.AddComponent<RectTransform>();
                        _orbiterRects[i].sizeDelta = new Vector2(ORBITER_SIZE, ORBITER_SIZE);
                        
                        // Random start position inside circle
                        float angle = Random.Range(0f, Mathf.PI * 2f);
                        float r = Random.Range(0f, CIRCLE_INNER_RADIUS * 0.6f);
                        _orbiterRects[i].anchoredPosition = new Vector2(Mathf.Cos(angle) * r, Mathf.Sin(angle) * r);
                        
                        // Random initial velocity direction
                        float vAngle = Random.Range(0f, Mathf.PI * 2f);
                        _orbiterVelocities[i] = new Vector2(Mathf.Cos(vAngle), Mathf.Sin(vAngle));
                        
                        _orbiterImages[i] = orb.AddComponent<Image>();
                        _orbiterImages[i].sprite = smallCircleSprite;
                        _orbiterImages[i].color = new Color(0.2f, 0.85f, 0.2f, 0.3f);
                    }
                    
                    _infectionUIContainer.SetActive(true);
                    Logger.LogInfo("[UI] Created infection UI with circle gauge");
                }
            }

            UpdateInfectionText();
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[UI] Exception: {ex.Message}");
        }
    }
    
    private static void FireMilestoneNotification(object player, int milestone)
    {
        try
        {
            // ── One-shot heartbeat thump ──────────────────────────────────────
            // Play once regardless of the looping heartbeat state — it acts as
            // an audio "punctuation" for the milestone crossing.
            EnsureAudioReflection();
            _audioPlaySFX?.Invoke(null, new object[] { "ui-heartbeat" });

            // ── On-screen message (local only) ────────────────────────────────
            if (UIGameManager.Instance != null)
            {
                string msg;
                if (milestone >= 50)
                {
                    int idx = ((milestone / 10) - 5) % SEVERE_MESSAGES.Length; // 50→0, 60→1, 70→2, 80→3, 90→0...
                    msg = SEVERE_MESSAGES[idx];
                }
                else
                {
                    int idx = ((milestone / 10) - 1) % MILD_MESSAGES.Length;   // 10→0, 20→1, 30→2, 40→3
                    msg = MILD_MESSAGES[idx];
                }
                UIGameManager.Instance.ShowPlayerInfo(msg);
            }

            // ── Speech bubble RPC at 50% and 90% (visible to all players) ────
            for (int i = 0; i < CHAT_BUBBLE_MILESTONES.Length; i++)
            {
                if (milestone == CHAT_BUBBLE_MILESTONES[i])
                {
                    var pc = player as PlayerController;
                    if (pc != null)
                        pc.network.ShowBaloonChat(ChatType.MONOLOGUE, MONOLOGUE_IDS[i], -1, -1, -1, 10);
                    break;
                }
            }

            Logger.LogInfo($"[Milestone] Fired notification at {milestone}%");
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[Milestone] Exception: {ex.Message}");
        }
    }

    private static void UpdateInfectionHeartbeat(object player)
    {
        try
        {
            var pc = player as PlayerController;
            if (pc == null) return;

            bool criticalInfection = _customInfection >= INFECTION_HEARTBEAT_THRESHOLD;

            if (criticalInfection && !_infectionHeartbeatPlaying)
            {
                // Only start it ourselves if the game isn't already playing it for low HP.
                // If isLowHealth is true the game owns the sound — we don't touch it.
                if (!pc.isLowHealth)
                {
                    EnsureAudioReflection();
                    _audioPlaySFX?.Invoke(null, new object[] { "ui-heartbeat" });
                }

                _infectionHeartbeatPlaying = true;
                Logger.LogInfo($"[SFX] Infection heartbeat started at {_customInfection:F1}%");
            }
            else if (!criticalInfection && _infectionHeartbeatPlaying)
            {
                StopInfectionHeartbeat(pc);
            }
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[SFX] Exception in UpdateInfectionHeartbeat: {ex.Message}");
        }
    }

    // Call this anywhere the heartbeat should stop (scene change, death, cure).
    // Only sends StopSFX if the game itself isn't keeping it alive for low HP.
    private static void StopInfectionHeartbeat(PlayerController pc)
    {
        if (!_infectionHeartbeatPlaying) return;
        _infectionHeartbeatPlaying = false;

        // If the player is currently low HP the game owns the heartbeat — leave it alone.
        if (pc != null && pc.isLowHealth) return;

        EnsureAudioReflection();
        _audioStopSFX?.Invoke(null, new object[] { "ui-heartbeat" });
        Logger.LogInfo("[SFX] Infection heartbeat stopped");
    }

    
    // Set just before SetHealth(0) is called; cleared by the host patch after use.
    internal static int _infectionKillPlayerIdx = -1;

    private static void KillPlayerFromInfection(object player)
    {
        try
        {
            var deathPosition = PlayerHelper.GetPosition(player);
            var pc = player as PlayerController;

            // Clean up our heartbeat before the game's own death code runs
            // (OnReviveTimeChanged will call StopSFX("ui-heartbeat") anyway,
            // but stopping it here keeps state consistent).
            StopInfectionHeartbeat(pc);

            if (NetworkHelper.IsServer())
            {
                // ── HOST PATH ────────────────────────────────────────────────
                // On the host we have StateAuthority over this player's
                // PlayerPhotonNetwork, so we can write networked properties
                // directly.  Set the flag so the ReviveTimer patch (below)
                // immediately snaps the countdown to 0 the moment the game
                // starts it, triggering the full networked permadeath sequence
                // on every client.
                if (pc != null)
                    _infectionKillPlayerIdx = pc.network.GetIDX();

                PlayerHelper.SetHealth(player, 0f);

                // Give the game one frame to start the revive timer, then the
                // InfectionReviveTimerPatch Postfix will snap it to 0.

                bool spawnSuccess = CoreLibrary.EliteSpawnHelper.SpawnEliteAtPosition(deathPosition);
                Logger.LogInfo($"[KillPlayer] Host: infection kill sent, elite spawn={spawnSuccess}");
            }
            else
            {
                // ── CLIENT PATH ──────────────────────────────────────────────
                // We cannot write [Networked] properties from a client.
                // SetHealth sends RpcAddHealth to StateAuthority (host) which
                // will bring HP to 0 and start the revive countdown on the
                // host.  The host's InfectionReviveTimerPatch will NOT fire
                // for us (it only knows about the host's own infection kill).
                // The player will be downed and the revive timer will run its
                // normal 90-second course before the game triggers permadeath
                // automatically.  This is the best we can do without adding new
                // [Networked] state or a custom RPC.
                PlayerHelper.SetHealth(player, 0f);
                Logger.LogInfo("[KillPlayer] Client: sent RpcAddHealth(0) to host, revive timer will expire normally");
            }
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[KillPlayer] Exception: {ex.Message}");
            Logger.LogError($"[KillPlayer] Stack trace: {ex.StackTrace}");
        }
    }
    
    private static void OnInventoryShown()
    {
        try
        {
            CreateOrUpdateInfectionUI();
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[UI] Exception: {ex.Message}");
        }
    }
    
    private static Sprite CreateCircleSprite(int resolution)
    {
        var tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
        float center = resolution / 2f;
        float radius = center - 1f;
        
        for (int y = 0; y < resolution; y++)
        {
            for (int x = 0; x < resolution; x++)
            {
                float dx = x - center + 0.5f;
                float dy = y - center + 0.5f;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                
                // Anti-aliased edge
                float alpha = Mathf.Clamp01(radius - dist + 0.5f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, resolution, resolution), new Vector2(0.5f, 0.5f));
    }

    // -------------------------------------------------------------------------
    // Damage-based infection
    // -------------------------------------------------------------------------

    private static bool IsInMission()
    {
        string level = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        return !level.Contains("Lobby") && !level.Contains("lobby");
    }

    private static void ApplyHitInfection(string source)
    {
        if (!IsInMission())
        {
            Logger.LogInfo($"[DmgInfection] {source} — skipped (not in mission)");
            return;
        }
        if (Time.time < _infectionPausedUntil)
        {
            Logger.LogInfo($"[DmgInfection] {source} — skipped (infection paused)");
            return;
        }
        float before = _customInfection;
        _customInfection = Mathf.Min(INFECTION_KILL_THRESHOLD, _customInfection + INFECTION_HIT_INCREASE);
        _lastLoggedMilestone = Mathf.FloorToInt(_customInfection / 10f);
        Logger.LogInfo($"[DmgInfection] {source} HIT +{INFECTION_HIT_INCREASE}% → {before:F2} → {_customInfection:F2}");
        UpdateInfectionText();
    }

    // Called by collider patch classes
    public static void OnEnemyHit(string source)
    {
        Logger.LogInfo($"[DmgInfection] {source} — enemy hit callback fired");
        ApplyHitInfection(source);
    }

    // Exposed for the patch class to read in log messages
    public static float GetCurrentInfection() => _customInfection;

    // -------------------------------------------------------------------------
    // Harmony patches — plain MonoBehaviour colliders (not Fusion-woven)
    // -------------------------------------------------------------------------
}

// ─────────────────────────────────────────────────────────────────────────────
// HOST-ONLY: Intercept FixedUpdate on the player whose infection kill was
// requested.  When the revive timer starts for that player, stop it immediately
// and set reviveTimerSecond = 0 so OnReviveTimeChanged fires on every client,
// triggering the full networked permadeath sequence (isPermadeath = true,
// reviveArea disabled, items dropped, etc.).
// ─────────────────────────────────────────────────────────────────────────────
[HarmonyPatch(typeof(PlayerController), "FixedUpdate")]
internal static class InfectionReviveTimerPatch
{
    [HarmonyPostfix]
    public static void Postfix(PlayerController __instance)
    {
        try
        {
            // Only the server writes reviveTimerSecond
            if (!NetworkGameManager.Instance || !NetworkGameManager.Instance.isServer) return;

            int targetIdx = InfectionMod.Plugin._infectionKillPlayerIdx;
            if (targetIdx < 0) return;

            // Check this is the right player
            if (__instance.network == null) return;
            if (__instance.network.GetIDX() != targetIdx) return;

            // Wait until the revive timer has actually started (player is downed)
            if (!__instance.reviveTimer.isRunning) return;

            // Snap timer to 0 — this writes reviveTimerSecond = 0 which
            // fires OnReviveTimeChanged on all clients.
            __instance.reviveTimer.StopDuration();
            __instance.network.playerPhoton.reviveTimerSecond = 0;

            // Clear the flag so we only do this once per infection kill
            InfectionMod.Plugin._infectionKillPlayerIdx = -1;

            Plugin.Logger.LogInfo($"[InfectionReviveTimer] Snapped reviveTimerSecond to 0 for player idx={targetIdx} — networked permadeath triggered");
        }
        catch (System.Exception ex)
        {
            Plugin.Logger.LogError($"[InfectionReviveTimer] Exception: {ex.Message}");
        }
    }
}
// Fires AFTER the guards inside AddSubHealth have already been evaluated, but BEFORE
// the value is written to the network. The `value` parameter here is the ORIGINAL
// pre-scaled float (negative = damage, positive = heal).
//
// We mirror the same invincibility guards that AddSubHealth uses so we only count
// hits where HP was actually reduced.
//
// Verification: search BepInEx/LogOutput.log for "[DmgInfection] AddSubHealth fired"
// after taking any enemy hit. If it never appears, the patch is not being applied.
[HarmonyPatch(typeof(PlayerNetwork), nameof(PlayerNetwork.AddSubHealth))]
internal static class AddSubHealthPatch
{
    [HarmonyPrefix]
    public static void Prefix(PlayerNetwork __instance, float value)
    {
        // Only damage (negative value), not heals or regen
        if (value >= 0f) return;

        // Only the local player's client
        if (!__instance.isLocalPlayer) return;

        // Mirror the invincibleTimer guard from AddSubHealth itself —
        // if invincibility would block the damage, don't count it as a hit.
        if (__instance.playerController == null) return;
        if (__instance.playerController.invincibleTimer.isRunning) return;

        // Mirror the god mode guard — god mode blocks damage, so skip.
        if (__instance.playerController.IsGod) return;

        // Mirror the dead check — don't add infection to an already-downed player
        // via direct damage (the passive infection tick handles downed state separately).
        if (__instance.playerController.network.GetHealth() <= 0f) return;

        InfectionMod.Plugin.Logger.LogInfo(
            $"[DmgInfection] AddSubHealth fired — raw value={value:F4}, " +
            $"infection before={InfectionMod.Plugin.GetCurrentInfection():F2}");

        InfectionMod.Plugin.OnEnemyHit("EnemyDamage");
    }
}
