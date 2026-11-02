//
// Copyright (c) 2026 7Bpencil
//
// This source code is licensed under the MIT license found in the
// LICENSE file in the root directory of this source tree.
//

using AnimationEventSystem;
using BepInEx;
using BepInEx.Logging;
using Comfort.Common;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using HarmonyLib;
using SevenBoldPencil.Common;
using SPT.Reflection.Patching;
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using ReloadExternalMagOperation = EFT.Player.FirearmController.ReloadExternalMagOperation;
using ReloadExternalMagResult = EFT.Player.FirearmController.ReloadExternalMagResult;
using ReloadInternalMagOperation = EFT.Player.FirearmController.ReloadInternalMagOperation;

// How to add tactical reload states to animator:
// 1) Create states "TACTICAL RELOAD START", "TACTICAL RELOAD" and "TACTICAL RELOAD START FAST", "TACTICAL RELOAD FAST",
// add AnimationEventsStateBehaviour component on them and configure it
// 2) Add transitions:
// - "IDLE" -> "TACTICAL RELOAD START" (use "IDLE" -> "RELOAD START" as an example, but with "ReloadTactical" as trigger)
// - "IDLE" -> "TACTICAL RELOAD START FAST" (use "IDLE" -> "RELOAD OUT ALL" as an example, but with "ReloadTacticalFast" as trigger)
// Tip: Use "Copy Transition Parameters" and "Paste Both"
// - "TACTICAL RELOAD START" -> "TACTICAL RELOAD" (use "RELOAD PROGRESSIVE" -> "RELOAD ADMIN" as an exmaple if they exist)
// - "TACTICAL RELOAD START FAST" -> "TACTICAL RELOAD FAST" (use "RELOAD PROGRESSIVE" -> "RELOAD HIT" as an exmaple if they exist)
// - "TACTICAL RELOAD" and "TACTICAL RELOAD FAST" -> various reload end variants, look at "RELOAD IN" for example
// Tip: Use AnimatorTransitionCopierWindow tool via "Custom Windows/7Bpencil/Animator Transition Copier"
// Tip: Dont forget to set "SpeedReload" parameter on states

// Animation events in the order which they should appear during reload:
// event name -> called function
// MagOut  -> OnMagPulledOutFromWeapon
// MagHide -> OnMagPuttedToRig
// MagShow -> OnMagAppeared
// UseSecondMagForReload -> UseSecondMagForReload
// MagIn   -> OnMagInsertedToWeapon

// TODO in inventory reload (from "INV TO RELOAD" state)
// TODO they are still not "inplace" in inventory
// how are we gonna do it?
// - ride on "Mag Retention Reload" by Tosox
// - disable RetentionRules.IsRetaining if weapon or mags are wrong (put weapon template id into config)
// - tactical reload is slower, so would be good to run it only when theres no space in rig
// - we still dont know if mags were swapped in place or there was free space (maybe can I just compare mag addresses?)
// - add option to not disable other weapons, just to keep it visual?
// TODO potential settings:
// - always use tactical reload (by default only when rig is full)
// - min mastery level

// SINGLE events: anim last key: 35
// 0.02693603 0.94 Sound ShellOutRig
// 0.2171717 7.6 Sound ShellIn (when shell comes near port)
// 0.5785299 20.2 AddAmmoInMag
// 0.7056451 24.6 Sound HandOff (when metal piece closes)

// TWIN events: anim last key: 35
// 0.02693603 0.94 Sound ShellOutRig
// 0.34285714285 Sound ShellInsert
// 0.51428571428 18 Sound ShellIn
// 0.5785299 20.2 AddAmmoInMag
// 0.7056451 24.6 Sound HandOff (when metal piece closes)

// QUAD events: anim last key: 60
// 0.015 0.94 Sound ShellOutRig
// 0.1884328 11.3 Sound ShellRattle
// 0.25 15 Sound ShellInsert
// 0.3507463 21 Sound ShellIn
// 0.5951493 35.7 Sound ShellInsert (when first shell touches port)
// 0.6921642 41.5 Sound ShellIn (when both shells are inside)
// 0.7333333 43.9 AddAmmoInMag
// 0.81666666666 49 Sound HandOff

// TODO quad loading breaks because AddAmmoInMag event comes later than
// transition exit time, so animation goes to second loop instead of interrupting

namespace SevenBoldPencil.TacticalReload;

// TODO 2 ways
// - one state with a blend tree on parameter, change parameter in code, all animation events are interleaved
// - multiple states, but each state needs transitions in and out

public class WeaponData : IDisposable
{
	public AssetBundle Bundle;
	public RuntimeAnimatorController AnimatorController;
	public string OriginalMagBonePath;
	public string ExtraMagBoneName;
	public int[] SupportedMags;
	public bool HasReloadTacticalFast; // AK has fast version of tactical reload, M4 doesnt
	public bool CanTacticalReloadWhileFolded; // AK cannot tactical reload if stock is folded

	public void Dispose()
	{
		Bundle.Unload(false);
	}
}

public class WeaponInstanceData : IDisposable
{
	public WeaponData Data;
	public Transform ExtraMagBone;

	public void Dispose()
	{

	}
}

public class WeaponReloadData : IDisposable
{
	public WeaponInstanceData WeaponInstanceData;
	public ReloadExternalMagOperation Operation;
	public Item OldMagItem;
	public Transform OldMagView;
	public Transform NewMagView;
	public Transform OldBone;

	public void Dispose()
	{

	}
}

public class ShotgunData : IDisposable
{
	public AssetBundle Bundle;
	public RuntimeAnimatorController AnimatorController;
	public string OriginalPatronBonePath;
	public string[] ExtraPatronBoneNames;
	public int[][] PatronIndicesPerReloadType; // note: this must be sorted in ascending order

	public void Dispose()
	{
		Bundle.Unload(false);
	}
}

public class ShotgunInstanceData : IDisposable
{
	public ShotgunData Data;

	public void Dispose()
	{

	}
}

public class ShotgunReloadData : IDisposable
{
	public ShotgunInstanceData ShotgunInstanceData;
	public ReloadInternalMagOperation Operation;
	public int NextReloadType;

	public int GetNextReloadType()
	{
		var availableAmmoCount = Operation._ammoPack.AmmoCount;
		var mag = Operation.Weapon.GetCurrentMagazine();
		var magFreeSpace = mag.MaxCount - mag.Count;
		var ammoToLoadTotal = Math.Min(magFreeSpace, availableAmmoCount);
		var ammoToLoad = ammoToLoadTotal - Operation.AmmoToLoadIntoMag;
		Plugin.Instance.LoggerInstance.LogWarning($"availableAmmoCount: {availableAmmoCount}, magFreeSpace: {magFreeSpace}, ammoToLoadTotal: {ammoToLoadTotal}, ammoLoaded: {Operation.AmmoToLoadIntoMag}, ammoToLoad: {ammoToLoad}");
		var patronsPerReloadType = ShotgunInstanceData.Data.PatronIndicesPerReloadType;
		for (var i = 0; i < patronsPerReloadType.Length; i++)
		{
			var reloadType = patronsPerReloadType.Length - 1 - i;
			var patronsCount = patronsPerReloadType[reloadType].Length;
			if (ammoToLoad >= patronsCount)
			{
				return reloadType;
			}
		}
		return 0;
	}

	public void Dispose()
	{

	}
}

[BepInPlugin("7Bpencil.TacticalReload", "7Bpencil.TacticalReload", "0.0.1")]
[BepInDependency("com.wtt.contentbackport", BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BaseUnityPlugin
{
	public static Plugin Instance;

	public ManualLogSource LoggerInstance;

	public List<ModulePatch> Patches;

	public Dictionary<string, WeaponData> Weapons = new();
	public Dictionary<WeaponPrefab, WeaponInstanceData> WeaponInstances = new();
	public Dictionary<FirearmsAnimator, WeaponReloadData> WeaponReloads = new();

	public Dictionary<string, ShotgunData> Shotguns = new();
	public Dictionary<WeaponPrefab, ShotgunInstanceData> ShotgunInstances = new();
	public Dictionary<FirearmsAnimator, ShotgunReloadData> ShotgunReloads = new();

    private void Awake()
	{
		Instance = this;

		LoggerInstance = Logger;

		Patches = new();

		RegisterPatch(new Patch_WeaponPrefab_CacheInternalObjects());
		RegisterPatch(new Patch_AssetPoolObject_OnDestroy());
		RegisterPatch(new Patch_ReloadExternalMagOperation_Reset());
		RegisterPatch(new Patch_ReloadExternalMagOperation_Start());
		RegisterPatch(new Patch_FirearmsAnimator_Reload_Weapons());
		RegisterPatch(new Patch_ReloadExternalMagOperation_OnMagPuttedToRig());
		RegisterPatch(new Patch_ReloadExternalMagOperation_OnMagAppeared());
		RegisterPatch(new Patch_ReloadExternalMagOperation_UseSecondMagForReload());
		RegisterPatch(new Patch_ReloadExternalMagOperation_OnMagInsertedToWeapon());

		RegisterPatch(new Patch_ReloadInternalMagOperation_Reset());
		RegisterPatch(new Patch_ReloadInternalMagOperation_Start());
		RegisterPatch(new Patch_ReloadInternalMagOperation_OnShowAmmo());
		RegisterPatch(new Patch_ReloadInternalMagOperation_AddAmmoToMag());

#if DEBUG
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.Reset)));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.Start), [typeof(ReloadExternalMagResult), typeof(Callback)]));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagPulledOutFromWeapon)));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagPuttedToRig)));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagAppeared)));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.UseSecondMagForReload)));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagInsertedToWeapon)));

		RegisterPatch(new PrintDebug(typeof(ReloadInternalMagOperation), nameof(ReloadInternalMagOperation.Reset)));
		RegisterPatch(new PrintDebug(typeof(ReloadInternalMagOperation), nameof(ReloadInternalMagOperation.Start), [typeof(AmmoPack), typeof(Callback)]));
		RegisterPatch(new PrintDebug(typeof(ReloadInternalMagOperation), nameof(ReloadInternalMagOperation.OnAddAmmoInChamber)));
		RegisterPatch(new PrintDebug(typeof(ReloadInternalMagOperation), nameof(ReloadInternalMagOperation.OnShowAmmo)));
		RegisterPatch(new PrintDebug(typeof(ReloadInternalMagOperation), nameof(ReloadInternalMagOperation.AddAmmoToMag)));

		RegisterPatch(new Patch_AnimationEventsEmitter_SendEvent());
#endif

		var assemblyPath = Assembly.GetExecutingAssembly().Location;
		if (string.IsNullOrWhiteSpace(assemblyPath))
		{
			// happens for plugins loaded from scripts folder by BepInEx.ScriptEngine
			assemblyPath = Path.Combine(BepInEx.Paths.BepInExRootPath, "scripts", "7Bpencil.TacticalReload", "7Bpencil.TacticalReload.dll");
		}

        var assemblyDir = Path.GetDirectoryName(assemblyPath);
		{
			var bundleName = "7b_ak74m.bundle";
			var controllerPath = "Assets/BundleDumps/weapon_izhmash_ak74m_545x39_container/Content/Weapons/ak74m/weapon_izhmash_ak74m_545x39.controller";
			var prefabName = "weapon_izhmash_ak74m_545x39.generated";
			var bundlePath = Path.Combine(assemblyDir, "data", bundleName);
			var bundle = AssetBundle.LoadFromFile(bundlePath);
			Weapons[prefabName + "(Clone)"] = new()
			{
				Bundle = bundle,
				AnimatorController = bundle.LoadAsset<RuntimeAnimatorController>(controllerPath),
				OriginalMagBonePath = "Weapon_root/Weapon_root_anim/weapon/mod_magazine",
				ExtraMagBoneName = "mod_magazine_new",
				SupportedMags =
				[
					0, // 20rd/30rd standard mags
					4, // 30rd ak-12 mags
				],
				HasReloadTacticalFast = true,
				CanTacticalReloadWhileFolded = false,
			};
		}
		{
			var bundleName = "7b_m4a1.bundle";
			var controllerPath = "Assets/BundleDumps/weapon_colt_m4a1_556x45_container 1/AnimatorController/weapon_colt_m4a1_556x45.controller";
			var prefabName = "M4A1";
			var bundlePath = Path.Combine(assemblyDir, "data", bundleName);
			var bundle = AssetBundle.LoadFromFile(bundlePath);
			Weapons[prefabName + "(Clone)"] = new()
			{
				Bundle = bundle,
				AnimatorController = bundle.LoadAsset<RuntimeAnimatorController>(controllerPath),
				OriginalMagBonePath = "Weapon_root/Weapon_root_anim/weapon/mod_magazine",
				ExtraMagBoneName = "mod_magazine_new",
				SupportedMags =
				[
					0, // 30rd standard mags
				],
				HasReloadTacticalFast = false,
				CanTacticalReloadWhileFolded = false,
			};
		}
		{
			var bundleName = "7b_benelli.bundle";
			var controllerPath = "Assets/BundleDumps/weapon_benelli_m3_s90_12g_container/Content/Weapons/m3s90/weapon_benelli_m3_s90_12g.controller";
			var prefabName = "weapon_benelli_m3_s90_12g_model.generated";
			var bundlePath = Path.Combine(assemblyDir, "data", bundleName);
			var bundle = AssetBundle.LoadFromFile(bundlePath);
			Shotguns[prefabName + "(Clone)"] = new()
			{
				Bundle = bundle,
				AnimatorController = bundle.LoadAsset<RuntimeAnimatorController>(controllerPath),
				OriginalPatronBonePath = "Weapon_root/Weapon_root_anim/weapon/patron_in_weapon",
				ExtraPatronBoneNames =
				[
					"patron_in_weapon_002",
					"patron_in_weapon_003",
					"patron_in_weapon_004",
					"patron_in_weapon_005",
				],
				PatronIndicesPerReloadType =
				[
					[0],
					[0, 1],
					[2, 3, 0, 1],
				]
			};
		}
	}

	public void RegisterPatch(ModulePatch patch)
	{
		patch.Enable();
		Patches.Add(patch);
	}

	private void OnDestroy()
	{
		foreach (var patch in Patches)
		{
			patch.Disable();
		}
		Patches.Clear();

		WeaponReloads.Clear();

		foreach (var weaponInstance in WeaponInstances.Values)
		{
			weaponInstance.Dispose();
		}
		WeaponInstances.Clear();

		foreach (var weapon in Weapons.Values)
		{
			weapon.Dispose();
		}
		Weapons.Clear();
	}

	public bool AssertEqual(ReloadExternalMagOperation a, ReloadExternalMagOperation b)
	{
		if (a != b)
		{
			Logger.LogError($"Reload operations not equal!");
			return false;
		}
		return true;
	}

	public bool AssertEqual(ReloadInternalMagOperation a, ReloadInternalMagOperation b)
	{
		if (a != b)
		{
			Logger.LogError($"Reload operations not equal!");
			return false;
		}
		return true;
	}
}

public class Patch_ReloadExternalMagOperation_Reset : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.Reset));
    }

    [PatchPrefix]
    public static void Prefix(ReloadExternalMagOperation __instance)
	{
		// Operations get reused when possible, so we should clean old data too

		if (Plugin.Instance.WeaponReloads.Remove(__instance.FirearmsAnimator, out var reload))
		{
			Plugin.Instance.AssertEqual(__instance, reload.Operation);
			reload.Dispose();
		}
	}
}

public class Patch_ReloadExternalMagOperation_Start : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.Start), [typeof(ReloadExternalMagResult), typeof(Callback)]);
    }

    [PatchPrefix]
    public static void Prefix(ReloadExternalMagOperation __instance, ReloadExternalMagResult reloadExternalMagResult)
	{
		// cannot tactical reload when theres no mag in weapon
		if (reloadExternalMagResult.OldMagazine == null)
		{
			return;
		}

		var weaponPrefab = __instance.Controller._weaponPrefab;
		if (!Plugin.Instance.WeaponInstances.TryGetValue(weaponPrefab, out var weaponInstanceData))
		{
			return;
		}

		var weaponData = weaponInstanceData.Data;
		if (reloadExternalMagResult.QuickReload && !weaponData.HasReloadTacticalFast)
		{
			return;
		}
		if (!IsMagPairCompatible(reloadExternalMagResult.OldMagazine, reloadExternalMagResult.NextMagazine, weaponData.SupportedMags))
		{
			return;
		}

		var weapon = new WeaponPrefab_Proxy(weaponPrefab)._weaponData;
		if (weapon.Folded && !weaponData.CanTacticalReloadWhileFolded)
		{
			return;
		}

		Plugin.Instance.WeaponReloads.Add(__instance.FirearmsAnimator, new()
		{
			WeaponInstanceData = weaponInstanceData,
			Operation = __instance,
		});
	}

   	public static bool IsMagPairCompatible(Magazine oldMag, Magazine newMag, int[] supportedMags)
	{
		// when mags have the same size we can be 100% sure they get swapped inplace,
		// also both mags should be of supported type
		return oldMag.Width == newMag.Width &&
			   oldMag.Height == newMag.Height &&
		       Array.IndexOf(supportedMags, oldMag.magAnimationIndex) != -1 &&
		       Array.IndexOf(supportedMags, newMag.magAnimationIndex) != -1;
	}
}

public class Patch_FirearmsAnimator_Reload_Weapons : ModulePatch
{
	public static readonly int TRIGGER_RELOAD_TACTICAL = Animator.StringToHash("ReloadTactical");
	public static readonly int TRIGGER_RELOAD_TACTICAL_FAST = Animator.StringToHash("ReloadTacticalFast");

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(FirearmsAnimator), nameof(FirearmsAnimator.Reload), [typeof(int), typeof(int), typeof(bool)]);
    }

    [PatchPrefix]
    public static bool Prefix(FirearmsAnimator __instance, int currentMagType, int nextMagType, bool isFast)
	{
		// copypaste of original method with our own triggers
		if (Plugin.Instance.WeaponReloads.TryGetValue(__instance, out var reloadData))
		{
			__instance.SetMagTypeCurrent(currentMagType);
			__instance.SetMagTypeNew(nextMagType);
			__instance.ResetLeftHand();
			if (isFast)
			{
				__instance.Animator.SetTrigger(TRIGGER_RELOAD_TACTICAL_FAST);
			}
			else
			{
				__instance.Animator.SetTrigger(TRIGGER_RELOAD_TACTICAL);
			}
			return false;
		}
		return true;
	}
}

public class Patch_ReloadExternalMagOperation_OnMagPuttedToRig : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagPuttedToRig));
    }

    [PatchPrefix]
    public static bool Prefix(ReloadExternalMagOperation __instance)
	{
		// In "normal" reload animations character first pulls old mag out,
		// its item gets removed from slot and item view is destroyed, then
		// new mag is spawned, item gets inserted into slot and animation of putting it in is played.
		// This means player never sees two mags at the same time.
		// To not break anything I keep order of events the same,
		// but do not dispawn old mag object until UseSecondMagForReload event

		if (Plugin.Instance.WeaponReloads.TryGetValue(__instance.FirearmsAnimator, out var reload))
		{
			Plugin.Instance.AssertEqual(__instance, reload.Operation);

			if (!__instance._magPuttedToRig)
			{
				__instance._magPuttedToRig = true;

				var slot = __instance.ReloadExternalMagResult.MagazineSlot;
				var slotView = __instance.Firearms.ContainerCollectionView.GetViewForSlot(slot);

				reload.OldMagItem = slotView.Item;
				reload.OldMagView = slotView.ItemView;

				// TODO at clean up we will need to copy paste most of the code from RemoveMod

				// if (__instance.ReloadExternalMagResult.RemoveOldMagResult != null)
				// {
				// 	Item item = ReloadExternalMagResult.RemoveOldMagResult.Item;
				// 	DropMod(item, EWeaponModType.mod_magazine);
				// }
				// Firearms.RemoveMod(ReloadExternalMagResult.MagazineSlot);
			}
			return false;
		}
		return true;
	}
}

public class Patch_ReloadExternalMagOperation_OnMagAppeared : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagAppeared));
    }

    [PatchPostfix]
    public static void Postfix(ReloadExternalMagOperation __instance)
	{
		// This event is fired when new mag first appears on the screen,
		// usually its almost at the start of the animation

		if (Plugin.Instance.WeaponReloads.TryGetValue(__instance.FirearmsAnimator, out var reload))
		{
			Plugin.Instance.AssertEqual(__instance, reload.Operation);

			var slot = __instance.ReloadExternalMagResult.MagazineSlot;
			var viewSlot =  __instance.Firearms.ContainerCollectionView.GetViewForSlot(slot);
			var newMagView = viewSlot.ItemView;
			var oldBone = viewSlot.Bone;
			var newBone = reload.WeaponInstanceData.ExtraMagBone;
			newMagView.SetParent(newBone, false);

			reload.NewMagView = newMagView;
			reload.OldBone = oldBone;

			// this code is originally run on OnMagInsertedToWeapon, but for animation reasons
			// we emit that event pretty late, sometimes so late, that animation can
			// transition into wrong state because MagInWeapon and AmmoOnMag are not setup in time,
			// so the easiest solution is to set them here beforehand

			__instance.FirearmsAnimator.SetMagInWeapon(ok: true);
			__instance.FirearmsAnimator.SetAmmoOnMag(__instance.ReloadExternalMagResult.NextMagazine.Count + ((__instance.ReloadExternalMagResult.PopNewAmmoResult != null) ? 1 : 0));
		}
	}
}

public class Patch_ReloadExternalMagOperation_UseSecondMagForReload : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.UseSecondMagForReload));
    }

    [PatchPrefix]
    public static bool Prefix(ReloadExternalMagOperation __instance)
	{
		// I time this event when old mag is offscreen and can be despawned without player noticing
		// (or replaced with loot version in fast reload animation)

		if (Plugin.Instance.WeaponReloads.TryGetValue(__instance.FirearmsAnimator, out var reload))
		{
			Plugin.Instance.AssertEqual(__instance, reload.Operation);
			if (__instance.ReloadExternalMagResult.RemoveOldMagResult != null)
			{
				__instance.DropMod(__instance.ReloadExternalMagResult.RemoveOldMagResult.Item, EWeaponModType.mod_magazine);
			}
			AssetPoolObject.ReturnToPool(reload.OldMagView.gameObject);
			reload.OldMagView = null;
			return false;
		}
		return true;
	}
}

public class Patch_ReloadExternalMagOperation_OnMagInsertedToWeapon : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagInsertedToWeapon));
    }

    [PatchPostfix]
    public static void Postfix(ReloadExternalMagOperation __instance)
	{
		// I time this event when magazine_new bone is exactly in the same position/rotation
		// as ordinary magazine bone, this way snapping magazine object back to the bone game expects
		// wont produce any weird teleporting mags.

		if (Plugin.Instance.WeaponReloads.TryGetValue(__instance.FirearmsAnimator, out var reload))
		{
			Plugin.Instance.AssertEqual(__instance, reload.Operation);
			reload.NewMagView.SetParent(reload.OldBone);
		}
	}
}

public class Patch_ReloadInternalMagOperation_Reset : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadInternalMagOperation), nameof(ReloadInternalMagOperation.Reset));
    }

    [PatchPrefix]
    public static void Prefix(ReloadInternalMagOperation __instance)
	{
		// Operations get reused when possible, so we should clean old data too

		if (Plugin.Instance.ShotgunReloads.Remove(__instance.FirearmsAnimator, out var reload))
		{
			Plugin.Instance.AssertEqual(__instance, reload.Operation);
			reload.Dispose();
			Logger.LogWarning($"Patch_ReloadInternalMagOperation_Reset: Cleaned reload operation");
		}
		else
		{
			Logger.LogError($"Patch_ReloadInternalMagOperation_Reset: Unknown reload operation?");
		}
	}
}

public class Patch_ReloadInternalMagOperation_Start : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadInternalMagOperation), nameof(ReloadInternalMagOperation.Start), [typeof(AmmoPack), typeof(Callback)]);
    }

    [PatchPrefix]
    public static void Prefix(ReloadInternalMagOperation __instance, AmmoPack ammoPack)
	{
		var weaponPrefab = __instance.Controller._weaponPrefab;
		if (!Plugin.Instance.ShotgunInstances.TryGetValue(weaponPrefab, out var shotgunInstanceData))
		{
			Logger.LogError($"Patch_ReloadInternalMagOperation_Start: Unknown weapon?");
			return;
		}

		Plugin.Instance.ShotgunReloads.Add(__instance.FirearmsAnimator, new()
		{
			ShotgunInstanceData = shotgunInstanceData,
			Operation = __instance,
		});
		Logger.LogWarning($"Patch_ReloadInternalMagOperation_Start");
	}
}

public class Patch_ReloadInternalMagOperation_OnShowAmmo : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadInternalMagOperation), nameof(ReloadInternalMagOperation.OnShowAmmo));
    }

    [PatchPrefix]
    public static void Prefix(ReloadInternalMagOperation __instance)
	{
		// Note, BSG shotgun reload animation always uses the same model of shotgun shell
		// during reload - with which it started, because animation is looped and theres
		// no animation events to switch shell model.
		// So we have to do it ourselves by adding OnShowAmmo event
		// and switching shell model to the next one on it

		// TODO we need to destroy old shell, noticable how they clip in each other
		if (Plugin.Instance.ShotgunReloads.TryGetValue(__instance.FirearmsAnimator, out var reload) && Plugin.Instance.AssertEqual(__instance, reload.Operation))
		{
			var nextReloadType = reload.GetNextReloadType();
			var patronIndices = reload.ShotgunInstanceData.Data.PatronIndicesPerReloadType[nextReloadType];
			for (var i = 0; i < patronIndices.Length; i++)
			{
				var patronIndex = patronIndices[i];
				var patronAmmo = __instance._ammoPack.GetAmmoToReload(__instance.AmmoToLoadIntoMag + i);
				__instance.Firearms.SetRoundIntoWeapon(patronAmmo, patronIndex);
			}
			__instance.FirearmsAnimator.SetMagTypeCurrent(nextReloadType);
			reload.NextReloadType = nextReloadType;
			Logger.LogWarning($"Patch_ReloadInternalMagOperation_OnShowAmmo");
		}
		else
		{
			Logger.LogError($"Patch_ReloadInternalMagOperation_OnShowAmmo: Unknown reload operation?");
		}
	}
}

public class Patch_ReloadInternalMagOperation_AddAmmoToMag : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadInternalMagOperation), nameof(ReloadInternalMagOperation.AddAmmoToMag));
    }

    [PatchPrefix]
    public static void Prefix(ReloadInternalMagOperation __instance)
	{
		if (Plugin.Instance.ShotgunReloads.TryGetValue(__instance.FirearmsAnimator, out var reload) && Plugin.Instance.AssertEqual(__instance, reload.Operation))
		{
			var patronIndices = reload.ShotgunInstanceData.Data.PatronIndicesPerReloadType[reload.NextReloadType];
			__instance.AmmoToLoadIntoMag += patronIndices.Length - 1;
			Logger.LogWarning($"Patch_ReloadInternalMagOperation_AddAmmoToMag");
		}
		else
		{
			Logger.LogError($"Patch_ReloadInternalMagOperation_AddAmmoToMag: Unknown reload operation?");
		}
	}
}

public class Patch_WeaponPrefab_CacheInternalObjects : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(WeaponPrefab), nameof(WeaponPrefab.CacheInternalObjects));
    }

    [PatchPrefix]
    public static void Prefix(WeaponPrefab __instance, bool ____cacheInternalObjectsBeenCalled)
	{
		// TODO dillema: I need to hook into animator as early as possible,
		// but theres no Weapon to check what template id this weapon has...
		// weapon object name looks unique, but probably not

		if (____cacheInternalObjectsBeenCalled)
		{
			// if true, this weapon has already been patched
			return;
		}
		if (!__instance._objectInstance)
		{
			return;
		}

		if (Plugin.Instance.Weapons.TryGetValue(__instance._objectInstance.name, out var weaponData))
		{
			var animator = __instance._objectInstance.GetComponent<Animator>();
			animator.runtimeAnimatorController = weaponData.AnimatorController;

			var bonesRoot = __instance._objectInstance.transform;
			var originalMagBone = bonesRoot.Find(weaponData.OriginalMagBonePath);
			var magBonesParent = originalMagBone.parent;
			var extraMagBone = GetOrCreateExtraBone(weaponData.ExtraMagBoneName, magBonesParent, originalMagBone);

			Plugin.Instance.WeaponInstances.Add(__instance, new()
			{
				Data = weaponData,
				ExtraMagBone = extraMagBone,
			});

			Logger.LogWarning($"Patch_WeaponPrefab_CacheInternalObjects: {__instance._objectInstance.name} {__instance.gameObject.GetInstanceID()}|{__instance._objectInstance.GetInstanceID()}");
		}
		if (Plugin.Instance.Shotguns.TryGetValue(__instance._objectInstance.name, out var shotgunData))
		{
			var animator = __instance._objectInstance.GetComponent<Animator>();
			animator.runtimeAnimatorController = shotgunData.AnimatorController;

			var bonesRoot = __instance._objectInstance.transform;
			var originalPatronBone = bonesRoot.Find(shotgunData.OriginalPatronBonePath);
			var patronBonesParent = originalPatronBone.parent;

			// note: we dont need to cache patron bones because EFT.Firarms class
			// already does this for us with _patronInWeaponTransforms

			foreach (var boneName in shotgunData.ExtraPatronBoneNames)
			{
				GetOrCreateExtraBone(boneName, patronBonesParent, originalPatronBone);
			}

			Plugin.Instance.ShotgunInstances.Add(__instance, new()
			{
				Data = shotgunData,
			});

			Logger.LogWarning($"Patch_WeaponPrefab_CacheInternalObjects: {__instance._objectInstance.name} {__instance.gameObject.GetInstanceID()}|{__instance._objectInstance.GetInstanceID()}");
		}
	}

	public static Transform GetOrCreateExtraBone(string boneName, Transform boneParent, Transform originalBone)
	{
		var boneTransform = boneParent.Find(boneName);
		if (boneTransform)
		{
			return boneTransform;
		}
		else
		{
			var extraBone = new GameObject(boneName);
			var extraBoneTransform = extraBone.transform;
			extraBoneTransform.parent = boneParent;
			extraBoneTransform.localPosition = originalBone.localPosition;
			extraBoneTransform.localRotation = originalBone.localRotation;
			extraBoneTransform.localScale = originalBone.localScale;
			return extraBoneTransform;
		}
	}
}

public class Patch_AssetPoolObject_OnDestroy : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(AssetPoolObject), nameof(AssetPoolObject.OnDestroy));
    }

    [PatchPrefix]
    public static void Prefix(AssetPoolObject __instance)
	{
		if (__instance is not WeaponPrefab weaponPrefab)
		{
			return;
		}
		if (Plugin.Instance.WeaponInstances.Remove(weaponPrefab, out var weaponInstanceData))
		{
			weaponInstanceData.Dispose();
			Logger.LogWarning($"Patch_AssetPoolObject_OnDestroy: {weaponPrefab.gameObject.name} {weaponPrefab.gameObject.GetInstanceID()}|{weaponPrefab._objectInstance.GetInstanceID()}");
		}
		if (Plugin.Instance.ShotgunInstances.Remove(weaponPrefab, out var shotgunInstanceData))
		{
			shotgunInstanceData.Dispose();
			Logger.LogWarning($"Patch_AssetPoolObject_OnDestroy: {weaponPrefab.gameObject.name} {weaponPrefab.gameObject.GetInstanceID()}|{weaponPrefab._objectInstance.GetInstanceID()}");
		}
	}
}

public class Patch_AnimationEventsEmitter_SendEvent : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(AnimationEventsEmitter), nameof(AnimationEventsEmitter.SendEvent));
    }

    [PatchPrefix]
    public static void Prefix(AnimationEventsEmitter __instance, string functionName, int functionNameHash, AnimationEventParameter parameter, int stateShortNameHash)
	{
		if (functionName == "Sound")
		{
			Logger.LogWarning($"AnimEvent: {functionName} {parameter.StringParam}");
		}
		else
		{
			Logger.LogWarning($"AnimEvent: {functionName}");
		}
	}
}

public struct WeaponPrefab_Proxy(WeaponPrefab instance)
{
    private readonly WeaponPrefab __instance = instance;

	private static TypedFieldInfo<WeaponPrefab, Weapon> __weaponData = new("_weaponData");

	public Weapon _weaponData { get { return __weaponData.Get(__instance); } set { __weaponData.Set(__instance, value); } }
}
