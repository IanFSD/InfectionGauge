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
    private static GameObject _infectionUIContainer = null;

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
                _infectionUIContainer = null;
            }

            // Exit early if in lobby - don't increase infection
            bool isInLobby = currentLevel.Contains("Lobby") || currentLevel.Contains("lobby");
            if (isInLobby)
            {
                if (!_hasLoggedLobbyStatus)
                {
                    _hasLoggedLobbyStatus = true;
                }
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
            
            if (_infectionTextUI == null)
                return;

            // Update the text with current infection value
            _infectionTextUI.text = $"Infection: {_customInfection:F2}%";
            
            // Color based on infection level
            if (_customInfection >= 75f)
            {
                _infectionTextUI.color = Color.red;
            }
            else if (_customInfection >= 50f)
            {
                _infectionTextUI.color = new Color(1f, 0.5f, 0f); // Orange
            }
            else if (_customInfection >= 25f)
            {
                _infectionTextUI.color = Color.yellow;
            }
            else
            {
                _infectionTextUI.color = Color.white;
            }
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
                }
                else
                {
                    // Create new UI element parented to inventory
                    _infectionUIContainer = new GameObject("InfectionCounter");
                    _infectionUIContainer.transform.SetParent(inventoryTransform, false);

                    var containerRect = _infectionUIContainer.AddComponent<RectTransform>();
                    containerRect.anchorMin = new Vector2(1f, 1f);
                    containerRect.anchorMax = new Vector2(1f, 1f);
                    containerRect.pivot = new Vector2(1f, 1f);
                    containerRect.anchoredPosition = new Vector2(450f, 140f);
                    containerRect.sizeDelta = new Vector2(200, 30);

                    _infectionTextUI = _infectionUIContainer.AddComponent<TextMeshProUGUI>();
                    _infectionTextUI.fontSize = 16;
                    _infectionTextUI.alignment = TextAlignmentOptions.Right;
                    _infectionTextUI.enableAutoSizing = false;
                    _infectionTextUI.outlineWidth = 0.15f;
                    _infectionTextUI.outlineColor = new Color(0f, 0f, 0f, 0.8f);
                    
                    _infectionUIContainer.SetActive(true);
                    Logger.LogInfo("[UI] Created infection UI");
                }
            }

            // Update the text with current infection value
            if (_infectionTextUI != null)
            {
                _infectionTextUI.text = $"Infection: {_customInfection:F2}%";
                
                // Color based on infection level
                if (_customInfection >= 75f)
                {
                    _infectionTextUI.color = Color.red;
                }
                else if (_customInfection >= 50f)
                {
                    _infectionTextUI.color = new Color(1f, 0.5f, 0f); // Orange
                }
                else if (_customInfection >= 25f)
                {
                    _infectionTextUI.color = Color.yellow;
                }
                else
                {
                    _infectionTextUI.color = Color.white;
                }
            }
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
}
