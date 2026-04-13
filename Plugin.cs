using UnityEngine;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;
using CoreLibrary;

namespace InfectionGauge;

[BepInPlugin("rer.wmo.mods.infectiongauge", "Infection Gauge", "1.0.0")]
[BepInDependency("rer.wmo.mods.corelibrary", BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;

    private const float INFECTION_KILL_THRESHOLD = 100f;  
    private const float INFECTION_CHECK_INTERVAL = 1f; 
    
    // Health-based multipliers
    private const float INFECTION_CAUTION_MULTIPLIER = 3f; 
    private const float INFECTION_DANGER_MULTIPLIER = 5f;   
    private const float INFECTION_DOWNED_MULTIPLIER = 10f;
    
    // Health thresholds (as percentages)
    private const float HEALTH_CAUTION_THRESHOLD = 0.5f; 
    private const float HEALTH_DANGER_THRESHOLD = 0.25f;
    
    // Antivirus item constants
    private const string ANTIVIRUS_ITEM_ID = "997";
    private const string ANTIVIRUS_RECIPE_ID = "Antivirus";
    private const float INFECTION_HEAL_AMOUNT = 20f;
    private const string CHEMICAL_MATERIAL_ID = "Chemicals";
    private const int CHEMICAL_AMOUNT = 2;
    private const string PLACEHOLDER_BASE_ITEM_ID = "213"; // Tonic sprite
    
    private static System.Collections.Generic.Dictionary<string, float> _jobInfectionRates = new System.Collections.Generic.Dictionary<string, float>();
    private static bool _jobRatesInitialized = false;

    private static float _lastCheckTime = 0f;
    private static bool _hasLoggedKill = false;
    
    // Custom infection tracker
    private static float _customInfection = 0f;
    private static int _lastLoggedMilestone = 0;
    private static string _currentLevel = "";
    private static bool _hasLoggedLobbyStatus = false;
    
    private static string _currentPlayerJob = "Default";
    
    // UI elements
    private static TextMeshProUGUI _infectionTextUI = null;
    private static Image _infectionCircleBg = null;
    private static GameObject _infectionUIContainer = null;
    private static RectTransform[] _orbiterRects = null;
    private static Image[] _orbiterImages = null;
    private static Vector2[] _orbiterVelocities = null;
    private const int ORBITER_COUNT = 5;
    private const float CIRCLE_INNER_RADIUS = 25f; // Stay inside the 60px circle
    private const float ORBITER_SIZE = 5f;
    private static float _lastOrbiterTime = 0f;

    private void Awake()
    {
        Logger = base.Logger;
        Logger.LogInfo("=== INFECTION GAUGE PLUGIN INITIALIZED (v2.0) ===");
        
        // Register antivirus item and recipe
        RegisterAntivirusItem();
        
        // Subscribe to CoreLibrary game events
        GameEvents.OnDataManagerAwake += OnDataManagerAwake;
        GameEvents.OnPlayerUpdate += OnPlayerUpdate;
        GameEvents.OnInventoryShown += OnInventoryShown;
        
        Logger.LogInfo("Subscribed to game events via CoreLibrary!");
    }
    
    private static void RegisterAntivirusItem()
    {
        try
        {
            Logger.LogInfo("[Antivirus] Registering antivirus item and recipe...");
            
            // Register the item
            var antivirusItem = new CoreLibrary.CustomItemDefinition
            {
                ItemId = ANTIVIRUS_ITEM_ID,
                ItemName = "Antivirus",
                ItemDescription = "Lowers total infection rate by 20%",
                ItemType = 3, // Health item
                ItemCategory = 0, // Medical
                IsUsable = true,
                IsStackable = false,
                MaxStack = 10,
                SpriteSourceItemId = PLACEHOLDER_BASE_ITEM_ID,
                OnItemUsed = (invObj) =>
                {
                    _customInfection = Mathf.Max(0f, _customInfection - INFECTION_HEAL_AMOUNT);
                    _lastLoggedMilestone = Mathf.FloorToInt(_customInfection / 10f);
                    UpdateInfectionText();
                }
            };
            
            CoreLibrary.CustomItemHelper.RegisterItem(antivirusItem);
            
            var antivirusRecipe = new CoreLibrary.CustomRecipeDefinition
            {
                RecipeId = ANTIVIRUS_RECIPE_ID,
                ItemId = ANTIVIRUS_ITEM_ID,
                CraftStation = 1, // Workbench
                RecipeItemType = 1, // Medicine
                RecipeCategory = 0,
                CraftAmount = 1,
                Ingredients = new System.Collections.Generic.List<CoreLibrary.RecipeIngredient>
                {
                    new CoreLibrary.RecipeIngredient
                    {
                        MaterialId = CHEMICAL_MATERIAL_ID,
                        Amount = CHEMICAL_AMOUNT
                    }
                }
            };
            
            CoreLibrary.CustomItemHelper.RegisterRecipe(antivirusRecipe);
            
            Logger.LogInfo($"[Antivirus] Item ID: {ANTIVIRUS_ITEM_ID}, Recipe: {ANTIVIRUS_RECIPE_ID}");
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[Antivirus] Failed to register: {ex.Message}");
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

            foreach (var kv in _jobInfectionRates)
            {
                Logger.LogInfo($"[JobInfection] Perk '{kv.Key}' Infection Rate: {kv.Value:F4}");
            }

            _jobRatesInitialized = true;
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[JobInfection] Failed to set infection rates: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Gets the base Infection Rate for a specific job
    /// </summary>
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
    
    private static void OnDataManagerAwake()
    {
        try
        {
            Logger.LogInfo("[Init] DataManager awakened, initializing infection rates...");
            InitializeJobInfectionRates();
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[Init] Failed to initialize: {ex.Message}");
        }
    }
    
    private static void OnPlayerUpdate(object player)
    {
        try
        {
            // Check if in game level
            string currentLevel = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (currentLevel != _currentLevel)
            {
                _currentLevel = currentLevel;
                _hasLoggedLobbyStatus = false;
                _hasLoggedKill = false;
                
                // Clear stale Unity object references from previous scene
                _infectionTextUI = null;
                _infectionCircleBg = null;
                _infectionUIContainer = null;
                _orbiterRects = null;
                _orbiterImages = null;
                _orbiterVelocities = null;
            }

            // Exit early if in lobby - don't increase infection
            bool isInLobby = currentLevel.Contains("Lobby") || currentLevel.Contains("lobby");
            if (isInLobby)
            {
                if (!_hasLoggedLobbyStatus)
                {
                    _hasLoggedLobbyStatus = true;
                    
                    // Reset permadeath state so player is alive in lobby
                    // (infection stays, giving them a chance to use antivirus)
                    var pc = player as PlayerController;
                    if (pc != null && pc.isPermadeath)
                    {
                        pc.isPermadeath = false;
                        Logger.LogInfo("[Lobby] Reset permadeath state - player alive with infection");
                    }
                }
                // Still update UI in lobby so the gauge animation runs
                UpdateInfectionUIIfOpen();
                return;
            }

            // Get player job
            try
            {
                string perkId = PlayerHelper.GetPerkId(player);
                
                // Only update if perk actually changed
                if (!string.IsNullOrEmpty(perkId) && _currentPlayerJob != perkId)
                {
                    _currentPlayerJob = perkId;
                    float jobInfectionRate = GetJobInfectionRate(_currentPlayerJob);
                    Logger.LogInfo($"[Job] Perk ID: {perkId}");
                    Logger.LogInfo($"[Job] Infection Base Rate: {jobInfectionRate:F4}/s");
                }
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"[Job] Exception getting perk: {ex.Message}");
            }

            // Update infection
            float currentTime = Time.time;
            if (currentTime - _lastCheckTime >= INFECTION_CHECK_INTERVAL)
            {
                _lastCheckTime = currentTime;
                UpdateInfection(player);
            }
            
            // Update UI if inventory is open
            UpdateInfectionUIIfOpen();

            // Check for death
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
    
    /// <summary>
    /// Updates the player's infection level
    /// </summary>
    private static void UpdateInfection(object player)
    {
        try
        {
            // Check if player is downed
            float currentHealth = PlayerHelper.GetHealth(player);
            if (currentHealth <= 0f)
            {
                _customInfection = 0f;
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

            // Log milestones
            int currentMilestone = Mathf.FloorToInt(_customInfection / 10f);
            if (currentMilestone > _lastLoggedMilestone)
            {
                _lastLoggedMilestone = currentMilestone;
                Logger.LogInfo($"[Infection] Reached {currentMilestone * 10}% infection");
            }
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[Update] Exception updating infection: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Updates the infection text and color on the UI element
    /// </summary>
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
        return new Color(0.5f, 0.05f, 0.05f, 1f);           // Dark red
    }
    
    private static void UpdateOrbiters(float t, Color gaugeColor)
    {
        if (_orbiterRects == null || _orbiterImages == null || _orbiterVelocities == null)
            return;
        
        float currentTime = Time.time;
        float dt = Mathf.Min(currentTime - _lastOrbiterTime, 0.05f);
        _lastOrbiterTime = currentTime;
        
        if (dt <= 0f) return;
        
        float speed = 15f + t * 40f;
        float orbiterRadius = ORBITER_SIZE * 0.5f;
        
        // Move each orbiter
        for (int i = 0; i < ORBITER_COUNT; i++)
        {
            if (_orbiterRects[i] == null) continue;
            
            Vector2 pos = _orbiterRects[i].anchoredPosition;
            pos += _orbiterVelocities[i] * speed * dt;
            
            // Bounce off circle boundary
            float dist = pos.magnitude;
            float maxDist = CIRCLE_INNER_RADIUS - orbiterRadius;
            if (dist > maxDist && dist > 0f)
            {
                // Reflect velocity off the circle wall
                Vector2 normal = pos.normalized;
                _orbiterVelocities[i] = _orbiterVelocities[i] - 2f * Vector2.Dot(_orbiterVelocities[i], normal) * normal;
                pos = normal * maxDist;
            }
            
            _orbiterRects[i].anchoredPosition = pos;
        }
        
        // Check collisions between orbiters
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
                    // Push apart
                    float overlap = minDist - Mathf.Sqrt(distSq);
                    _orbiterRects[i].anchoredPosition = posA + normal * (overlap * 0.5f);
                    _orbiterRects[j].anchoredPosition = posB - normal * (overlap * 0.5f);
                    
                    // Swap velocity components along collision normal
                    float dotI = Vector2.Dot(_orbiterVelocities[i], normal);
                    float dotJ = Vector2.Dot(_orbiterVelocities[j], normal);
                    _orbiterVelocities[i] += (dotJ - dotI) * normal;
                    _orbiterVelocities[j] += (dotI - dotJ) * normal;
                }
            }
        }
        
        // Update visuals
        float alpha = Mathf.Lerp(0.25f, 0.85f, t);
        for (int i = 0; i < ORBITER_COUNT; i++)
        {
            if (_orbiterImages[i] == null) continue;
            _orbiterImages[i].color = new Color(gaugeColor.r, gaugeColor.g, gaugeColor.b, alpha);
        }
    }
    
    /// <summary>
    /// Updates infection UI if inventory is currently open
    /// </summary>
    private static void UpdateInfectionUIIfOpen()
    {
        try
        {
            if (!UIHelper.IsInventoryOpen())
                return;
            
            // Create UI if it doesn't exist yet
            if (_infectionTextUI == null)
                CreateOrUpdateInfectionUI();
            
            UpdateInfectionText();
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[UI] Exception updating UI: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Creates or updates the infection UI display
    /// </summary>
    private static void CreateOrUpdateInfectionUI()
    {
        try
        {
            var inventoryTransform = UIHelper.GetInventoryTransform();
            if (inventoryTransform == null)
                return;

            // Find or create the infection UI
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
                    // Clone font from an existing in-scene TMP element
                    TMP_FontAsset gameFont = null;
                    var existingTmp = inventoryTransform.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (existingTmp != null)
                        gameFont = existingTmp.font;
                    
                    // Create a procedural circle sprite
                    var circleSprite = CreateCircleSprite(64);
                    
                    // Container
                    _infectionUIContainer = new GameObject("InfectionCounter");
                    _infectionUIContainer.transform.SetParent(inventoryTransform, false);

                    var containerRect = _infectionUIContainer.AddComponent<RectTransform>();
                    containerRect.anchorMin = new Vector2(1f, 1f);
                    containerRect.anchorMax = new Vector2(1f, 1f);
                    containerRect.pivot = new Vector2(0.5f, 0.5f);
                    containerRect.anchoredPosition = new Vector2(350f, 100f);
                    containerRect.sizeDelta = new Vector2(200, 100);
                    
                    // Text label (top)
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
                    
                    // Circle background (dark, behind fill)
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
                    
                    // Floating circles inside the gauge
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
    
    /// <summary>
    /// Kills the player and spawns an elite zombie at their location
    /// </summary>
    private static void KillPlayerFromInfection(object player)
    {
        try
        {
            Logger.LogWarning("=== EXECUTING INSTANT DEATH + ELITE SPAWN DUE TO INFECTION ===");

            var deathPosition = PlayerHelper.GetPosition(player);

            // Kill the player by setting health to 0
            PlayerHelper.SetHealth(player, 0f);
            
            // Prevent reviving - mark as permanently dead
            var pc = player as PlayerController;
            if (pc != null)
            {
                pc.isPermadeath = true;
                if (pc.reviveArea != null)
                    pc.reviveArea.enabled = false;
                Logger.LogInfo("[KillPlayer] Marked player as permanently dead (non-revivable)");
            }

            // Spawn elite zombie using BossSpawner framework
            if (NetworkHelper.IsServer())
            {
                Logger.LogInfo("[KillPlayer] Server is spawning elite enemy...");
                bool spawnSuccess = CoreLibrary.EliteSpawnHelper.SpawnEliteAtPosition(deathPosition);
                
                if (spawnSuccess)
                {
                    Logger.LogInfo("[KillPlayer] ✓ Elite enemy spawned successfully!");
                }
                else
                {
                    Logger.LogError("[KillPlayer] Failed to spawn elite enemy");
                }
            }
            else
            {
                Logger.LogInfo("[KillPlayer] Client detected death - server will spawn elite");
            }

            Logger.LogWarning("=== INSTANT DEATH + ELITE SPAWN EXECUTED ===");
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
            // Create or update infection UI
            CreateOrUpdateInfectionUI();
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[UI] Exception: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Creates a white filled circle sprite procedurally
    /// </summary>
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
}
