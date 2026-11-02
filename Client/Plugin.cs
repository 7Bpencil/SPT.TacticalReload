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
using SPT.Reflection.Patching;
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using ReloadInternalMagOperation = EFT.Player.FirearmController.ReloadInternalMagOperation;

namespace SevenBoldPencil.TacticalReload;

public class ShotgunData : IDisposable
{
	public RuntimeAnimatorController AnimatorController;
	public string OriginalPatronBonePath;
	public string[] ExtraPatronBoneNames;
	public int[][] PatronIndicesPerReloadType; // note: this must be sorted in ascending order

	public void Dispose() { }
}

public class ShotgunInstanceData : IDisposable
{
	public ShotgunData Data;

	public void Dispose() {	}
}

public class ShotgunReloadData : IDisposable
{
	public ShotgunInstanceData ShotgunInstanceData;
	public ReloadInternalMagOperation Operation;
	public int NextReloadType;

	public void Dispose() {	}
}

[BepInPlugin("7Bpencil.TacticalReload", "7Bpencil.TacticalReload", "0.0.1")]
public class Plugin : BaseUnityPlugin
{
	public static Plugin Instance;

	public ManualLogSource LoggerInstance;

	public Dictionary<string, ShotgunData> Shotguns = new();
	public Dictionary<WeaponPrefab, ShotgunInstanceData> ShotgunInstances = new();
	public Dictionary<FirearmsAnimator, ShotgunReloadData> ShotgunReloads = new();

    private void Awake()
	{
		Instance = this;

		LoggerInstance = Logger;

		new Patch_WeaponPrefab_CacheInternalObjects().Enable();
		new Patch_AssetPoolObject_OnDestroy().Enable();

		new Patch_ReloadInternalMagOperation_Reset().Enable();
		new Patch_ReloadInternalMagOperation_Start().Enable();
		new Patch_ReloadInternalMagOperation_OnShowAmmo().Enable();
		new Patch_ReloadInternalMagOperation_AddAmmoToMag().Enable();

		var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var assemblyDir = Path.GetDirectoryName(assemblyPath);
		{
			var bundleName = "7b_benelli.bundle";
			var controllerPath = "Assets/BundleDumps/weapon_benelli_m3_s90_12g_container/Content/Weapons/m3s90/weapon_benelli_m3_s90_12g.controller";
			var prefabName = "weapon_benelli_m3_s90_12g_model.generated";
			var bundlePath = Path.Combine(assemblyDir, "data", bundleName);
			var bundle = AssetBundle.LoadFromFile(bundlePath);
			Shotguns[prefabName + "(Clone)"] = new()
			{
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
			bundle.Unload(false);
		}
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
			return;
		}

		Plugin.Instance.ShotgunReloads.Add(__instance.FirearmsAnimator, new()
		{
			ShotgunInstanceData = shotgunInstanceData,
			Operation = __instance,
		});
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
			var patronIndicesPerReloadType = reload.ShotgunInstanceData.Data.PatronIndicesPerReloadType;
			var nextReloadType = GetNextReloadType(__instance, patronIndicesPerReloadType);
			var patronIndices = patronIndicesPerReloadType[nextReloadType];
			for (var i = 0; i < patronIndices.Length; i++)
			{
				var patronIndex = patronIndices[i];
				var patronAmmo = __instance._ammoPack.GetAmmoToReload(__instance.AmmoToLoadIntoMag + i);
				__instance.Firearms.SetRoundIntoWeapon(patronAmmo, patronIndex);
			}
			__instance.FirearmsAnimator.SetMagTypeCurrent(nextReloadType);
			reload.NextReloadType = nextReloadType;
		}
	}

	public static int GetNextReloadType(ReloadInternalMagOperation operation, int[][] patronIndicesPerReloadType)
	{
		var availableAmmoCount = operation._ammoPack.AmmoCount;
		var mag = operation.Weapon.GetCurrentMagazine();
		var magFreeSpace = mag.MaxCount - mag.Count;
		var ammoToLoadTotal = Math.Min(magFreeSpace, availableAmmoCount);
		var ammoToLoad = ammoToLoadTotal - operation.AmmoToLoadIntoMag;
		for (var i = 0; i < patronIndicesPerReloadType.Length; i++)
		{
			var reloadType = patronIndicesPerReloadType.Length - 1 - i;
			var patronsCount = patronIndicesPerReloadType[reloadType].Length;
			if (ammoToLoad >= patronsCount)
			{
				return reloadType;
			}
		}
		return 0;
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

		if (Plugin.Instance.Shotguns.TryGetValue(__instance._objectInstance.name, out var shotgunData))
		{
			var animator = __instance._objectInstance.GetComponent<Animator>();
			animator.runtimeAnimatorController = shotgunData.AnimatorController;

			var bonesRoot = __instance._objectInstance.transform;
			var originalPatronBone = bonesRoot.Find(shotgunData.OriginalPatronBonePath);
			var patronBonesParent = originalPatronBone.parent;

			foreach (var boneName in shotgunData.ExtraPatronBoneNames)
			{
				GetOrCreateExtraBone(boneName, patronBonesParent, originalPatronBone);
			}

			Plugin.Instance.ShotgunInstances.Add(__instance, new()
			{
				Data = shotgunData,
			});
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
		if (Plugin.Instance.ShotgunInstances.Remove(weaponPrefab, out var shotgunInstanceData))
		{
			shotgunInstanceData.Dispose();
		}
	}
}
