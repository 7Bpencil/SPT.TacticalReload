//
// Copyright (c) 2026 7Bpencil
//
// This source code is licensed under the MIT license found in the
// LICENSE file in the root directory of this source tree.
//

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
using ReloadExternalMagOperation = EFT.Player.FirearmController.ReloadExternalMagOperation;
using ReloadExternalMagResult = EFT.Player.FirearmController.ReloadExternalMagResult;

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

namespace SevenBoldPencil.TacticalReload;

public class WeaponData : IDisposable
{
	public AssetBundle Bundle;
	public RuntimeAnimatorController AnimatorController;

	public void Dispose()
	{
		Bundle.Unload(false);
	}
}

public class WeaponInstanceData : IDisposable
{
	public WeaponData Data;

	public void Dispose()
	{

	}
}

public class ReloadData
{
	public ReloadExternalMagOperation Operation;
	public Item OldMagItem;
	public Transform OldMagView;
	public Transform NewMagView;
	public Transform OldBone;
}

[BepInPlugin("7Bpencil.TacticalReload", "7Bpencil.TacticalReload", "0.0.1")]
public class Plugin : BaseUnityPlugin
{
	public static Plugin Instance;

	public ManualLogSource LoggerInstance;

	public List<ModulePatch> Patches;

	public Dictionary<string, WeaponData> Weapons;
	public Dictionary<WeaponPrefab, WeaponInstanceData> WeaponInstances;
	public Dictionary<FirearmsAnimator, ReloadData> Reloads;

    private void Awake()
	{
		Instance = this;

		LoggerInstance = Logger;

		Patches = new();

		RegisterPatch(new Patch_WeaponPrefab_CacheInternalObjects());
		RegisterPatch(new Patch_AssetPoolObject_OnDestroy());
		RegisterPatch(new Patch_ReloadExternalMagOperation_Reset());
		RegisterPatch(new Patch_ReloadExternalMagOperation_Start());
		RegisterPatch(new Patch_FirearmsAnimator_Reload());
		RegisterPatch(new Patch_ReloadExternalMagOperation_OnMagPuttedToRig());
		RegisterPatch(new Patch_ReloadExternalMagOperation_OnMagAppeared());
		RegisterPatch(new Patch_ReloadExternalMagOperation_UseSecondMagForReload());
		RegisterPatch(new Patch_ReloadExternalMagOperation_OnMagInsertedToWeapon());

#if DEBUG
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.Reset)));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.Start), [typeof(ReloadExternalMagResult), typeof(Callback)]));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagPulledOutFromWeapon)));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagPuttedToRig)));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagAppeared)));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.UseSecondMagForReload)));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagInsertedToWeapon)));
#endif

		var assemblyPath = Assembly.GetExecutingAssembly().Location;
		if (string.IsNullOrWhiteSpace(assemblyPath))
		{
			// happens for plugins loaded from scripts folder by BepInEx.ScriptEngine
			assemblyPath = Path.Combine(BepInEx.Paths.BepInExRootPath, "scripts", "7Bpencil.TacticalReload", "7Bpencil.TacticalReload.dll");
		}

        var assemblyDir = Path.GetDirectoryName(assemblyPath);
		var bundlePath = Path.Combine(assemblyDir, "data", "7b_ak74m.bundle");
		var bundle = AssetBundle.LoadFromFile(bundlePath);
		Weapons = new();
		Weapons["weapon_izhmash_ak74m_545x39_container(Clone)"] = new()
		{
			Bundle = bundle,
			AnimatorController = bundle.LoadAsset<RuntimeAnimatorController>("Assets/BundleDumps/weapon_izhmash_ak74m_545x39_container/Content/Weapons/ak74m/weapon_izhmash_ak74m_545x39.controller"),
		};
		WeaponInstances = new();
		Reloads = new();
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

		Reloads.Clear();

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

	public void AssertEqual(ReloadExternalMagOperation a, ReloadExternalMagOperation b)
	{
		if (a != b)
		{
			Logger.LogError($"Reload operations not equal!");
		}
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

		if (Plugin.Instance.Reloads.Remove(__instance.FirearmsAnimator, out var reload))
		{
			Plugin.Instance.AssertEqual(__instance, reload.Operation);
			// TODO any extra cleanup?
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
		if (!Plugin.Instance.WeaponInstances.TryGetValue(__instance.Controller._weaponPrefab, out var weaponInstanceData))
		{
			return;
		}
		if (!IsMagPairCompatible(
			reloadExternalMagResult.OldMagazine.magAnimationIndex,
			reloadExternalMagResult.NextMagazine.magAnimationIndex))
		{
			return;
		}

		Plugin.Instance.Reloads.Add(__instance.FirearmsAnimator, new()
		{
			Operation = __instance,
		});
	}

	// TODO hardcoded!
   	public static bool IsMagPairCompatible(int oldMag, int newMag)
	{
		return oldMag == 0 && newMag == 0;
	}
}

public class Patch_FirearmsAnimator_Reload : ModulePatch
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
		if (Plugin.Instance.Reloads.TryGetValue(__instance, out var reloadData))
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

		if (Plugin.Instance.Reloads.TryGetValue(__instance.FirearmsAnimator, out var reload))
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

		if (Plugin.Instance.Reloads.TryGetValue(__instance.FirearmsAnimator, out var reload))
		{
			Plugin.Instance.AssertEqual(__instance, reload.Operation);

			var slot = __instance.ReloadExternalMagResult.MagazineSlot;
			var viewSlot =  __instance.Firearms.ContainerCollectionView.GetViewForSlot(slot);
			var newMagView = viewSlot.ItemView;
			var oldBone = viewSlot.Bone;
			var newBone = oldBone.parent.Find("mod_magazine_new"); // TODO if not here, create it, also cache for future use
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

		if (Plugin.Instance.Reloads.TryGetValue(__instance.FirearmsAnimator, out var reload))
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

		if (Plugin.Instance.Reloads.TryGetValue(__instance.FirearmsAnimator, out var reload))
		{
			Plugin.Instance.AssertEqual(__instance, reload.Operation);
			reload.NewMagView.SetParent(reload.OldBone);
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
		// weapon container name looks unique, but probably not

		if (__instance._objectInstance &&
			!____cacheInternalObjectsBeenCalled &&
			Plugin.Instance.Weapons.TryGetValue(__instance.gameObject.name, out var weaponData))
		{
			var animator = __instance._objectInstance.GetComponent<Animator>();
			animator.runtimeAnimatorController = weaponData.AnimatorController;
			Plugin.Instance.WeaponInstances.Add(__instance, new()
			{
				Data = weaponData,
			});

			Logger.LogWarning($"Patch_WeaponPrefab_CacheInternalObjects: {__instance.gameObject.name} {__instance.gameObject.GetInstanceID()}|{__instance._objectInstance.GetInstanceID()}");
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
		if (__instance is WeaponPrefab weaponPrefab &&
			Plugin.Instance.WeaponInstances.Remove(weaponPrefab, out var weaponInstanceData))
		{
			weaponInstanceData.Dispose();
			Logger.LogWarning($"Patch_AssetPoolObject_OnDestroy: {weaponPrefab.gameObject.name} {weaponPrefab.gameObject.GetInstanceID()}|{weaponPrefab._objectInstance.GetInstanceID()}");
		}
	}
}

#if DEBUG
public class PrintDebug : ModulePatch
{
	private readonly Type Type;
	private readonly string MethodName;
	private readonly Type[] Parameters;

	public PrintDebug(Type type, string methodName, Type[] parameters = null)
	{
		Type = type;
		MethodName = methodName;
		Parameters = parameters;
	}

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(Type, MethodName, Parameters);
    }

    [PatchPrefix]
    public static void Prefix(MethodBase __originalMethod)
	{
		Logger.LogWarning($"{__originalMethod.DeclaringType.Name}.{__originalMethod.Name}");
	}
}
#endif
