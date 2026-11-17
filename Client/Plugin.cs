//
// Copyright (c) 2026 7Bpencil
//
// This source code is licensed under the MIT license found in the
// LICENSE file in the root directory of this source tree.
//

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using EFT.AssetsManager;
using EFT.CameraControl;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.WeaponModding;
using Newtonsoft.Json;
using SevenBoldPencil.Common;
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using UnityStandardAssets.ImageEffects;
using BlurSampleCount = UnityStandardAssets.ImageEffects.DepthOfField.BlurSampleCount;
using BlurType = UnityStandardAssets.ImageEffects.DepthOfField.BlurType;

namespace SevenBoldPencil.TransparentSights
{
    public readonly record struct CurrentAiming
    (
        Player Player,
        Firearms Firearms
    );

    public readonly record struct CurrentPatchedScope
    (
        Player Player,
        Firearms Firearms,
        Option<DOFData> DOFDataOption
    );

    public readonly record struct DOFData
    (
        DepthOfField DOF,
        SettingsDOF OriginalSettings
    );

    public readonly record struct SettingsDOF
    (
        bool enabled,
        BlurSampleCount blurSampleCount,
        float aperture,
        float focalLength,
        float focalSize,
        float foregroundOverlap,
        float maxBlurSize,
        BlurType blurType,
        bool highResolution
    );

    public readonly record struct PatchedItem
    (
        List<PatchedRenderer> PatchedRenderers
    );

    public readonly record struct PatchedRenderer
    (
        Renderer Renderer,
        Material[] Original,
        Material[] Patched
    );

	public enum ScopeTransparencyMode
	{
		Disabled,
		Enabled,
		EnabledWithMount,
	}

    [BepInPlugin("7Bpencil.TransparentSights", "7Bpencil.TransparentSights", "0.2.27")]
    public class Plugin : BaseUnityPlugin
    {
        public static readonly int _Cull = Shader.PropertyToID("_Cull");
        private const double SaveLagTime = 10;

        public static Plugin Instance;

        private ConfigEntry<bool> MakeOpticHousingTransparent;
        private ConfigEntry<bool> WeaponBlur;
        private ConfigEntry<float> WeaponBlurSize;
        private ConfigEntry<float> OpticBlurSize;
        private ConfigEntry<float> ForegroundSpreadLimit;
        private ConfigEntry<bool> UseHousingLayer;
        private ConfigEntry<float> LayerOpacity;
        private ConfigEntry<float> LayerBlurRadius;
        private HousingLayer SeparateHousing;

		public ManualLogSource LoggerInstance;

        private string ConfigPath;
        private Shader SightShader;
        private Dictionary<string, ScopeTransparencyMode> TransparentScopes;
        private Dictionary<string, Dictionary<ItemSpecificationPanel, ContextMenuButton>> ScopesItemPanels;
        private Dictionary<int, PatchedItem> PatchedItems;
        private List<int> CurrentTransparentItems;
        private Option<CurrentPatchedScope> CurrentPatchedScope;
        private Option<CurrentAiming> CurrentAiming;
        private Option<double> LastSaveTime;

        private void Awake()
        {
            Instance = this;
			LoggerInstance = Logger;

            MakeOpticHousingTransparent = Config.Bind("General", "Make optic housing transparent", false);

            const string layerGroup = "Housing Layer";
            UseHousingLayer = Config.Bind(layerGroup, "Separate housing blur", false,
                "Blur the housing independently of the weapon while aiming with a compatible sight.");
            LayerOpacity = Config.Bind(layerGroup, "Housing opacity", 0.45f,
                new ConfigDescription("Opacity of the blurred housing.", new AcceptableValueRange<float>(0f, 1f)));
            LayerBlurRadius = Config.Bind(layerGroup, "Housing blur radius", 18f,
                new ConfigDescription("Housing blur radius in screen pixels.", new AcceptableValueRange<float>(0f, 48f)));

            WeaponBlur = Config.Bind("General", "Blur transparent sights", true,
                "Enable camera depth of field while aiming. With separate housing blur, this controls weapon blur.");
            const string dofGroup = "Depth of Field";
            WeaponBlurSize = Config.Bind(dofGroup, "Blur Size", 0.94f,
                new ConfigDescription("Camera blur strength for non-magnified views.", new AcceptableValueRange<float>(0f, 10f)));
            OpticBlurSize = Config.Bind(dofGroup, "Blur Size in Optic", 7.418873f,
                new ConfigDescription("Camera blur strength for magnified views.", new AcceptableValueRange<float>(0f, 10f)));
            ForegroundSpreadLimit = Config.Bind("Depth of Field Advanced", "Foreground spread limit", 4f,
                new ConfigDescription("Maximum spread past foreground edges. Zero confines blur to the foreground mask.",
                    new AcceptableValueRange<float>(0f, 20f)));

            MakeOpticHousingTransparent.SettingChanged += (_, _) => Change_TransparencySettings();
            UseHousingLayer.SettingChanged += (_, _) => Change_TransparencySettings();
            LayerOpacity.SettingChanged += (_, _) => SeparateHousing.SetAppearance(LayerBlurRadius.Value, LayerOpacity.Value);
            LayerBlurRadius.SettingChanged += (_, _) => SeparateHousing.SetAppearance(LayerBlurRadius.Value, LayerOpacity.Value);
            WeaponBlur.SettingChanged += (_, _) => Change_DOF_Settings();
            WeaponBlurSize.SettingChanged += (_, _) => Change_DOF_Settings();
            OpticBlurSize.SettingChanged += (_, _) => Change_DOF_Settings();
            ForegroundSpreadLimit.SettingChanged += (_, _) => Change_DOF_Settings();

            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            SightShader = Shader.Find("Transparent/DepthZwriteDithered");
            SeparateHousing = new HousingLayer(Logger);
            ConfigPath = Path.Combine(assemblyDir, "config.json");
            TransparentScopes = LoadTransparentScopes(ConfigPath);
            ScopesItemPanels = new();
            PatchedItems = new();
            CurrentTransparentItems = new();

            new Patch_PWA_OnAimOrPoseChanged().Enable();
            new Patch_AssetPoolObject_OnDestroy().Enable();
            new Patch_ItemSpecificationPanel_Show().Enable();
            new Patch_ItemSpecificationPanel_Close().Enable();
            new Patch_BaseLocalGame_GameEnd().Enable();
            new Patch_DepthOfField_ForegroundMask().Enable();
            new Patch_HousingLayer_SsaaOutput().Enable();
#if DEBUG
            new Patch_FirearmController_Idling_DisableAimingOnReload().Enable();
#endif
        }

        internal bool ShouldFilterForegroundMask(DepthOfField effect)
        {
            return WeaponBlur.Value && CurrentPatchedScope.Some(out var scope) &&
                scope.DOFDataOption.Some(out var data) && data.DOF == effect;
        }

        public void Change_DOF_Settings()
        {
            if (!CurrentPatchedScope.Some(out var scope))
            {
                return;
            }

            if (!WeaponBlur.Value)
            {
                if (scope.DOFDataOption.Some(out var saved))
                {
                    Set_DOF_Settings_Config(saved.DOF, saved.OriginalSettings);
                    CurrentPatchedScope = new(scope with { DOFDataOption = default });
                }
                return;
            }

            if (!scope.DOFDataOption.Some(out var data) || !data.DOF)
            {
                var snapshot = TryGetDOFData();
                if (!snapshot.Some(out data))
                {
                    return;
                }
                CurrentPatchedScope = new(scope with { DOFDataOption = snapshot });
            }

            Set_DOF_Settings_Config(data.DOF, Get_DOF_Config(data, IsOptic(scope.Firearms)));
        }

        private SettingsDOF Get_DOF_Config(DOFData data, bool isOptic)
        {
            var blurSize = isOptic ? OpticBlurSize.Value : WeaponBlurSize.Value;
            // DiscBlur doubles the internal width in full resolution.
            var overlap = Mathf.Min(2.63f, ForegroundSpreadLimit.Value / (2f * Mathf.Max(0.1f, blurSize)));
            return data.OriginalSettings with
            {
                enabled = true,
                blurSampleCount = BlurSampleCount.High,
                aperture = 4f,
                focalLength = 1.53f,
                focalSize = 0.61f,
                foregroundOverlap = overlap,
                maxBlurSize = blurSize,
                blurType = BlurType.DiscBlur,
                highResolution = true
            };
        }

        public void Set_DOF_Settings_Config(DepthOfField DOF, SettingsDOF settings)
        {
            if (!DOF)
            {
                return;
            }
            DOF.blurSampleCount = settings.blurSampleCount;
            DOF.aperture = settings.aperture;
            DOF.focalLength = settings.focalLength;
            DOF.focalSize = settings.focalSize;
            DOF.foregroundOverlap = settings.foregroundOverlap;
            DOF.maxBlurSize = settings.maxBlurSize;
            DOF.blurType = settings.blurType;
            DOF.highResolution = settings.highResolution;
            DOF.enabled = settings.enabled;
        }

        public Dictionary<string, ScopeTransparencyMode> LoadTransparentScopes(string filePath)
        {
            if (SafeIO.ReadAllText(filePath).Ok(out var json, out var e))
            {
                var result = JsonConvert.DeserializeObject<Dictionary<string, ScopeTransparencyMode>>(json);
                return result;
            }
            else
            {
                Logger.LogError($"Failed to load transparent scopes, rolling back to default config: {e}");
            }

            return new()
            {
                { "616442e4faa1272e43152193", ScopeTransparencyMode.EnabledWithMount },
                { "5b3116595acfc40019476364", ScopeTransparencyMode.EnabledWithMount },
                { "58d399e486f77442e0016fe7", ScopeTransparencyMode.EnabledWithMount },
                { "61657230d92c473c770213d7", ScopeTransparencyMode.EnabledWithMount },
                { "609bab8b455afd752b2e6138", ScopeTransparencyMode.Disabled },
            };
        }

        public void SaveTransparentScopesToFile(string filePath, Dictionary<string, ScopeTransparencyMode> transparentScopes)
        {
            var json = JsonConvert.SerializeObject(transparentScopes, Formatting.Indented);
            SafeIO.WriteAllTextAsync(filePath, json);
        }

#if DEBUG
        public bool IsFrozen;
#endif
        public void Update()
        {
            // SaveLagTime and LastSaveTime are needed to not write to file
            // every time user changes scope transparency mode

            if (LastSaveTime.Some(out var lastSaveTime))
            {
                if (Time.realtimeSinceStartupAsDouble - lastSaveTime >= SaveLagTime)
                {
                    SaveTransparentScopesToFile(ConfigPath, TransparentScopes);
                    LastSaveTime = default;
                }
            }
#if DEBUG
            if (Input.GetKeyDown(KeyCode.F13))
            {
                IsFrozen = !IsFrozen;
                Time.timeScale = IsFrozen ? 0.01f : 1f;
            }
#endif
        }

        public static ScopeTransparencyMode GetNextScopeTransparencyMode(ScopeTransparencyMode value)
        {
            return value switch
            {
        		ScopeTransparencyMode.Disabled => ScopeTransparencyMode.Enabled,
        		ScopeTransparencyMode.Enabled => ScopeTransparencyMode.EnabledWithMount,
        		ScopeTransparencyMode.EnabledWithMount => ScopeTransparencyMode.Disabled,
                _ => throw new ArgumentException($"Unknown ScopeTransparencyMode: {value}"),
            };
        }

		private string GetScopeTransparencyModeName(ScopeTransparencyMode value)
		{
            return value switch
            {
        		ScopeTransparencyMode.Disabled => "TRANSP. OFF",
        		ScopeTransparencyMode.Enabled => "TRANSP. ON",
        		ScopeTransparencyMode.EnabledWithMount => "TRANSP. ON + MOUNT",
                _ => throw new ArgumentException($"Unknown ScopeTransparencyMode: {value}"),
            };
		}

        public ScopeTransparencyMode GetScopeTransparencyMode(string scopeTemplateId)
        {
            if (TransparentScopes.TryGetValue(scopeTemplateId, out var transparencyMode))
            {
                return transparencyMode;
            }

            // by default all sights are transparent
            return ScopeTransparencyMode.Enabled;
        }

        public string GetScopeTransparencyModeName(string scopeTemplateId)
        {
            var mode = GetScopeTransparencyMode(scopeTemplateId);
            var modeName = GetScopeTransparencyModeName(mode);
            return modeName;
        }

        public void SwitchScopeTransparencyMode(string scopeTemplateId)
        {
            // notice that we dont immediately reflect this change in CurrentPatchedScope,
            // because its impossible to change scope setting while ADS (player needs to open inventory first),
            // so OnAimingEnabled can take care of that

            var currentMode = GetScopeTransparencyMode(scopeTemplateId);
            var nextMode = GetNextScopeTransparencyMode(currentMode);
            TransparentScopes[scopeTemplateId] = nextMode;

            LastSaveTime = new(Time.realtimeSinceStartupAsDouble);
        }

        public void AddPanel(string scopeTemplateId, ItemSpecificationPanel panel, ContextMenuButton toggleButton)
        {
            if (ScopesItemPanels.TryGetValue(scopeTemplateId, out var panels))
            {
                panels.Add(panel, toggleButton);
            }
            else
            {
                ScopesItemPanels.Add(scopeTemplateId, new(){{ panel, toggleButton }});
            }
        }

        public void RemovePanel(string scopeTemplateId, ItemSpecificationPanel panel)
        {
            if (ScopesItemPanels.TryGetValue(scopeTemplateId, out var panels))
            {
                panels.Remove(panel);
                if (panels.Count == 0)
                {
                    ScopesItemPanels.Remove(scopeTemplateId);
                }
            }
        }

        public void UpdateAllPanels(string scopeTemplateId)
        {
            var modeName = GetScopeTransparencyModeName(scopeTemplateId);
            var panels = ScopesItemPanels[scopeTemplateId];
            foreach (var (panel, toggleButton) in panels)
            {
                toggleButton._text.text = modeName;
                panel.RecreateAttributeBars();
            }
        }

        public void OnAimingEnabled(Player player, Firearms firearms)
        {
#if DEBUG
            // infinite stamina for testing
            player.Physical.Stamina.Multiplier = 0;
            player.Physical.HandsStamina.Multiplier = 0;
#endif
            LogInfo("OnAimingEnabled");

            if (CurrentPatchedScope.HasValue)
            {
                OnAimingDisabled();
            }

            var isOptic = IsOptic(firearms);
            RebuildCurrentTransparentItems(firearms, isOptic);

            if (CurrentTransparentItems.Count != 0)
            {
                CurrentPatchedScope = new(new CurrentPatchedScope
                (
                    Player: player,
                    Firearms: firearms,
                    DOFDataOption: default
                ));
            }

            CurrentAiming = new(new CurrentAiming
            (
                Player: player,
                Firearms: firearms
            ));
            RefreshSeparateHousing(firearms);
            Change_DOF_Settings();
        }

        private void RefreshSeparateHousing(Firearms firearms)
        {
            var aimingMod = firearms.ProceduralWeaponAnimation.CurrentAimingMod;
            if (!UseHousingLayer.Value || aimingMod == null ||
                !TryGetScope(aimingMod.Item, firearms.WeaponPrefab).Some(out var sight) ||
                !CurrentTransparentItems.Contains(sight.gameObject.GetInstanceID()) ||
                !PatchedItems.TryGetValue(sight.gameObject.GetInstanceID(), out var item))
            {
                return;
            }
            SeparateHousing.Attach(CameraManager.Instance?._camera, sight.name, item.PatchedRenderers,
                LayerBlurRadius.Value, LayerOpacity.Value);
        }

        internal void CompositeUpscaledHousing(SSAAImpl instance, RenderTexture source, SSAAPropagator propagator)
        {
            SeparateHousing?.CompositeUpscaled(instance, source, propagator);
        }

        public Option<DOFData> TryGetDOFData()
        {
            var DOF = CameraManager.Instance?._depthOfField;

            // for some reason DOF is sometimes null...
            if (DOF)
            {
                var originalSettings = new SettingsDOF
                (
                    enabled: DOF.enabled,
                    blurSampleCount: DOF.blurSampleCount,
                    aperture: DOF.aperture,
                    focalLength: DOF.focalLength,
                    focalSize: DOF.focalSize,
                    foregroundOverlap: DOF.foregroundOverlap,
                    maxBlurSize: DOF.maxBlurSize,
                    blurType: DOF.blurType,
                    highResolution: DOF.highResolution
                );
                return new(new(DOF, originalSettings));
            }

            return default;
        }

        public bool IsOptic(Firearms firearms)
        {
            return firearms.ProceduralWeaponAnimation.CurrentScope.IsOptic;
        }

        // weapon can change between OnAimingDisabled and OnAimingEnabled,
        // so we have to update a list of items that get transparent,
        // hopefully its not that expensive
        public void RebuildCurrentTransparentItems(Firearms firearms, bool isOptic)
        {
            if (isOptic && !MakeOpticHousingTransparent.Value)
            {
                return;
            }

            var currentAimingMod = firearms.ProceduralWeaponAnimation.CurrentAimingMod;
            if (currentAimingMod == null)
            {
                return;
            }

            var scopeItem = currentAimingMod.Item;
            var weaponPrefab = firearms.WeaponPrefab;
            var mode = GetScopeTransparencyMode(scopeItem.StringTemplateId);
            if (mode == ScopeTransparencyMode.Disabled)
            {
                return;
            }
            if (mode == ScopeTransparencyMode.EnabledWithMount && TryGetScopeMount(scopeItem, weaponPrefab).Some(out var mount))
            {
                TryPatchCompoundItem(mount.Item1, mount.Item2, weaponPrefab);
            }
            else if (TryGetScope(scopeItem, weaponPrefab).Some(out var scopeVisual))
            {
                TryPatchCompoundItem(scopeItem, scopeVisual, weaponPrefab);
            }
        }

        public static Option<AssetPoolObject> TryGetScope(Item scope, WeaponPrefab weaponPrefab)
        {
            if (!GetParentSlot(scope).Some(out var mountScopeSlot))
            {
                return default;
            }

            var allWeaponContainers = weaponPrefab.ContainerCollectionView.ContainerBones;
            if (!allWeaponContainers.TryGetValue(mountScopeSlot, out var containerData))
            {
                return default;
            }
            if (containerData.Item == null)
            {
                return default;
            }
            if (!containerData.ItemView)
            {
                return default;
            }
            if (!containerData.ItemView.TryGetComponent<AssetPoolObject>(out var scopeAssetPoolObject))
            {
                return default;
            }

            return new(scopeAssetPoolObject);
        }

        // we get scope mount Item from scope parent slot,
        // then we get scope mount AssetPoolObject from WeaponPrefab
        // by keying list of all slots by slot that contains mount itself,
        // looks complicated, but most of the code is null checks (thanks BSG)
        public Option<(Item, AssetPoolObject)> TryGetScopeMount(Item scope, WeaponPrefab weaponPrefab)
        {
            if (!GetParentSlot(scope).Some(out var mountScopeSlot))
            {
                return default;
            }

            var mountItem = mountScopeSlot.ParentItem;
            if (mountItem == null)
            {
                return default;
            }
            if (!GetParentSlot(mountItem).Some(out var mountParentSlot))
            {
                return default;
            }

            var allWeaponContainers = weaponPrefab.ContainerCollectionView.ContainerBones;
            if (!allWeaponContainers.TryGetValue(mountParentSlot, out var containerData))
            {
                return default;
            }
            if (containerData.Item == null)
            {
                return default;
            }
            if (!containerData.ItemView)
            {
                return default;
            }
            if (!containerData.ItemView.TryGetComponent<AssetPoolObject>(out var mountAssetPoolObject))
            {
                return default;
            }

            return new((mountItem, mountAssetPoolObject));
        }

        public static Option<Slot> GetParentSlot(Item item)
        {
            var currentAddress = item.CurrentAddress;
            if (currentAddress == null)
            {
                return default;
            }
            if (currentAddress is not SlotItemAddress slotAddress)
            {
                return default;
            }

            var parentSlot = slotAddress.Slot;
            if (parentSlot == null)
            {
                return default;
            }

            return new(parentSlot);
        }

        // some scopes can have subitems, examples:
        // - rubber eyecups on some optic scopes
        // - iron sight attachment on acog
        public void TryPatchCompoundItem(Item item, AssetPoolObject assetPoolObject, WeaponPrefab weaponPrefab)
        {
            TryPatchItem(assetPoolObject);

            if (item is not CompoundItem compoundItem)
            {
                return;
            }

            var allWeaponContainers = weaponPrefab.ContainerCollectionView.ContainerBones;
            foreach (var slot in compoundItem.Slots)
            {
                var containedItem = slot.ContainedItem;
                if (containedItem == null)
                {
                    continue;
                }
                if (!allWeaponContainers.TryGetValue(slot, out var containerData))
                {
                    continue;
                }
                if (containerData.Item == null)
                {
                    continue;
                }
                if (!containerData.ItemView)
                {
                    continue;
                }
                if (containerData.ItemView.TryGetComponent<AssetPoolObject>(out var subItemAssetPoolObject))
                {
                    TryPatchCompoundItem(containedItem, subItemAssetPoolObject, weaponPrefab);
                }
            }
        }

        public void Change_TransparencySettings()
        {
            if (CurrentAiming.Some(out var currentAiming))
            {
                OnAimingEnabled(currentAiming.Player, currentAiming.Firearms);
            }
        }

        public void OnAimingDisabled()
        {
            SeparateHousing?.Detach();
            if (CurrentPatchedScope.Some(out var currentPatchedScope))
            {
                LogInfo("OnAimingDisabled");

                foreach (var tranparentItem in CurrentTransparentItems)
                {
                    ForPatchedItem(tranparentItem, SetOriginalMaterials);
                }
                CurrentTransparentItems.Clear();

                if (currentPatchedScope.DOFDataOption.Some(out var DOFData))
                {
                    Set_DOF_Settings_Config(DOFData.DOF, DOFData.OriginalSettings);
                }
                CurrentPatchedScope = default;
            }

            CurrentAiming = default;
        }

        private void TryPatchItem(AssetPoolObject item)
        {
            if (!item)
            {
                return;
            }
            var instanceID = item.gameObject.GetInstanceID();
            if (!PatchedItems.ContainsKey(instanceID))
            {
                var patchedRenderers = PatchRenderers(item);
                var patchedItem = new PatchedItem(patchedRenderers);
                PatchedItems.Add(instanceID, patchedItem);
            }
            CurrentTransparentItems.Add(instanceID);
            ForPatchedItem(instanceID, SetPatchedMaterials);
        }

        private List<PatchedRenderer> PatchRenderers(AssetPoolObject assetPoolObject)
        {
            var renderers = assetPoolObject.Renderers;
            var result = new List<PatchedRenderer>(renderers.Count);
            foreach (var renderer in renderers)
            {
                if (PatchRenderer(renderer).Some(out var patchedRenderer))
                {
    				result.Add(patchedRenderer);
                }
            }

            return result;
        }

		public Option<PatchedRenderer> PatchRenderer(Renderer renderer)
		{
            if (!renderer)
            {
                return default;
            }

            var oldMaterials = renderer.materials;
            if (oldMaterials == null)
            {
                return default;
            }

            LogInfo("patch renderer:", renderer.name);

            var newMaterials = new Material[oldMaterials.Length];
            for (var i = 0; i < oldMaterials.Length; i++)
            {
                var oldMaterial = oldMaterials[i];
                if (oldMaterial && IsOpaqueMaterial(oldMaterial))
                {
                    var newMaterial = new Material(SightShader);
                    newMaterial.CopyPropertiesFromMaterial(oldMaterial);
                    newMaterial.SetFloat(_Cull, 2); // set backface culling, because some scopes have front face culling for some reasons
                    newMaterials[i] = newMaterial;
                }
                else
                {
                    newMaterials[i] = oldMaterial;
                }
            }

            return new(new PatchedRenderer
            (
                Renderer: renderer,
                Original: oldMaterials,
                Patched: newMaterials
            ));
		}

        public bool IsOpaqueMaterial(Material material)
        {
            var shaderName = material.shader.name;
            return
                shaderName == "p0/Reflective/Bumped Specular SMap" ||
                shaderName == "p0/Reflective/Specular" ||
                shaderName == "CW FX/BackLens" ||
                shaderName == "Tarkov Custom/Specular/Lit/Opaque/Epic SMAP PBR_Icon" ||
                shaderName == "Unlit/Color2";
        }

        public void ForPatchedItem(int instanceID, Action<PatchedItem> doAction)
        {
            if (PatchedItems.TryGetValue(instanceID, out var patchedItem))
            {
                doAction(patchedItem);
            }
        }

        public void SetPatchedMaterials(PatchedItem item)
        {
            foreach (var entry in item.PatchedRenderers)
            {
                if (entry.Renderer)
                {
                    entry.Renderer.sharedMaterials = entry.Patched;
                }
            }
        }

        public void SetOriginalMaterials(PatchedItem item)
        {
            foreach (var entry in item.PatchedRenderers)
            {
                if (entry.Renderer)
                {
                    entry.Renderer.sharedMaterials = entry.Original;
                }
            }
        }

        public void OnAssetPoolObjectDestroyed(AssetPoolObject assetPoolObject)
        {
            var instanceID = assetPoolObject.gameObject.GetInstanceID();
            OnPatchedItemDestroyed(instanceID);
        }

        public void OnPatchedItemDestroyed(int instanceID)
        {
            if (PatchedItems.Remove(instanceID, out var patchedItem))
            {
                CleanPatchedRenderers(patchedItem.PatchedRenderers);
    			LogInfo("OnPatchedItemDestroyed:", instanceID);
            }
        }

        public void CleanPatchedRenderers(List<PatchedRenderer> patchedRenderers)
        {
            foreach (var patchedRenderer in patchedRenderers)
            {
                for (var i = 0; i < patchedRenderer.Patched.Length; i++)
                {
                    var patched = patchedRenderer.Patched[i];
                    if (patched && patched != patchedRenderer.Original[i])
                    {
                        Destroy(patched);
                    }
                }
            }

            patchedRenderers.Clear();
        }

        private void OnDestroy()
        {
            if (PatchedItems != null)
            {
                OnAimingDisabled();
                foreach (var item in PatchedItems.Values)
                {
                    CleanPatchedRenderers(item.PatchedRenderers);
                }
            }
            SeparateHousing?.Dispose();
        }

        public void LogInfo<A>(A a)
        {
#if DEBUG
			Logger.LogInfo(a);
#endif
        }

        public void LogInfo<A, B>(A a, B b)
        {
#if DEBUG
			Logger.LogInfo($"{a} {b}");
#endif
        }

        public void LogInfo<A, B, C>(A a, B b, C c)
        {
#if DEBUG
			Logger.LogInfo($"{a} {b} {c}");
#endif
        }
    }
}
