//
// Copyright (c) 2026 7Bpencil
//
// This source code is licensed under the MIT license found in the
// LICENSE file in the root directory of this source tree.
//

// TODO just make custom animation events
// TODO patch first moment event fires

using AnimationSystem;
using AnimationEventSystem;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using Diz.Skinning;
using Diz.Jobs;
using Diz.Utils;
using EFT;
using EFT.CameraControl;
using EFT.GameTriggers;
using EFT.Impostors;
using EFT.Interactive;
using EFT.Settings.Graphics;
using EFT.UI.Settings;
using EFT;
using EFT.Animations;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using EFT.Visual;
using EFT.UI;
using EFT.UI.WeaponModding;
using EFT.Utilities;
using Newtonsoft.Json;
using HarmonyLib;
using SevenBoldPencil.Common;
using SPT.Reflection.Patching;
using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using JsonType;
using GPUInstancer;
using Koenigz.PerfectCulling.EFT;
using FirearmController = EFT.Player.FirearmController;
using ReloadExternalMagOperation = EFT.Player.FirearmController.ReloadExternalMagOperation;
using AnimationEvent = AnimationEventSystem.AnimationEvent;

namespace SevenBoldPencil.TacticalReload;

[Serializable]
public class AnimationBehaviourData
{
	public string FullName;
	public int FullNameHash;
	public int EventsListId;
	public List<AnimationEvent> AnimationEvents;
}

[Serializable]
public class ReloadData
{
	public Transform Bone;
	public Item Item;
	public Transform ItemView;
	public Transform NewMag;
	public Transform OldBone;
}

[BepInPlugin("7Bpencil.TacticalReload", "7Bpencil.TacticalReload", "0.0.1")]
public class Plugin : BaseUnityPlugin
{
	public static Plugin Instance;

	public List<AnimationBehaviourData> CustomAnimationsData;
	public Dictionary<int, AnimationBehaviourData> CustomAnimationsDataDict;
	public Dictionary<int, Item> Items = new();
	public Stack<ReloadData> Reloads = new();

    private void Awake()
	{
		Instance = this;

		new Patch_WeaponPrefab_InitHotObjects().Enable();
		new Patch_FirearmsAnimator_Reload().Enable();
		new Patch_ReloadExternalMagOperation_OnMagPuttedToRig().Enable();
		new Patch_ReloadExternalMagOperation_OnMagAppeared().Enable();
		new Patch_ReloadExternalMagOperation_UseSecondMagForReload().Enable();
		new Patch_ReloadExternalMagOperation_OnMagInsertedToWeapon().Enable();

		new PrintDebug<byte>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.Start)).Enable();
		new PrintDebug<short>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagPulledOutFromWeapon)).Enable();
		new PrintDebug<int>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagPuttedToRig)).Enable();
		new PrintDebug<long>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagAppeared)).Enable();
		new PrintDebug<float>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagInsertedToWeapon)).Enable();
		new PrintDebug<double>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.UseSecondMagForReload)).Enable();

        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var dataPath = Path.Combine(assemblyDir, "data", "animation-events-ak103.json");
        if (SafeIO.ReadAllText(dataPath).Ok(out var json, out var e))
        {
            CustomAnimationsData = JsonConvert.DeserializeObject<List<AnimationBehaviourData>>(json);
			CustomAnimationsDataDict = new();
			foreach (var animData in CustomAnimationsData)
			{
				CustomAnimationsDataDict[animData.FullNameHash] = animData;
			}
        }
	}

	public void DumpAnimationData(WeaponPrefab weaponPrefab)
	{
		var result = new List<AnimationBehaviourData>();
		foreach (var ieventBehaviour in weaponPrefab.AnimationEventsEmitter._animationEventsStateBehaviours)
		{
			if (ieventBehaviour is not AnimationEventsStateBehaviour eventBehaviour)
			{
				continue;
			}

			var resultAnimationEvents = new List<AnimationEvent>();
			foreach (var animationEvent in eventBehaviour.AnimationEvents)
			{
				resultAnimationEvents.Add((AnimationEvent)animationEvent.Clone());
			}

			result.Add(new()
			{
				FullName = eventBehaviour.FullName,
				FullNameHash = eventBehaviour.FullNameHash,
				EventsListId = eventBehaviour.EventsListId,
				AnimationEvents = resultAnimationEvents,
			});
		}

        var json = JsonConvert.SerializeObject(result, Formatting.Indented);
        var filePath = "animation-events.json";
        SafeIO.WriteAllTextAsync(filePath, json);
	}
}

public class Patch_WeaponPrefab_InitHotObjects : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(WeaponPrefab), nameof(WeaponPrefab.InitHotObjects));
    }

    [PatchPostfix]
    public static void Postfix(WeaponPrefab __instance, Weapon ____weaponData)
	{
		// old but reliable way of tracking weapon spawn
		if (____weaponData == null)
		{
			return;
		}
		if (__instance.AnimationEventsEmitter == null)
		{
			return;
		}
		foreach (var ieventBehaviour in __instance.AnimationEventsEmitter._animationEventsStateBehaviours)
		{
			if (ieventBehaviour is not AnimationEventsStateBehaviour eventBehaviour)
			{
				continue;
			}
			if (!Plugin.Instance.CustomAnimationsDataDict.TryGetValue(eventBehaviour.FullNameHash, out var customData))
			{
				continue;
			}
			eventBehaviour.EventsData._stateHashToEventsCollection[eventBehaviour.EventsListId]._animationEvents = customData.AnimationEvents;
		}
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
		var slot = __instance.ReloadExternalMagResult.MagazineSlot;
		var viewSlot =  __instance.Firearms.ContainerCollectionView.GetViewForSlot(slot);
		var item = viewSlot.ItemView;
		var oldBone = viewSlot.Bone;
		var newBone = oldBone.parent.Find("mod_magazine_new"); // TODO cache
		item.SetParent(newBone, false);

		var reload = Plugin.Instance.Reloads.Peek();
		reload.NewMag = item;
		reload.OldBone = oldBone;
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
		var reload = Plugin.Instance.Reloads.Peek();
		AssetPoolObject.ReturnToPool(reload.ItemView.gameObject);
		return false;
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
		var reload = Plugin.Instance.Reloads.Peek();
		reload.NewMag.SetParent(reload.OldBone);
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
		if (!__instance._magPuttedToRig)
		{
			__instance._magPuttedToRig = true;

			var slot = __instance.ReloadExternalMagResult.MagazineSlot;
			var slotView = __instance.Firearms.ContainerCollectionView.GetViewForSlot(slot);

			Logger.LogWarning("Keep mag");
			Plugin.Instance.Reloads.Push(new()
			{
				Bone = slotView.Bone,
				Item = slotView.Item,
				ItemView = slotView.ItemView,
			});

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
}

public class Patch_FirearmsAnimator_Reload : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
		Type[] parameters = [typeof(int), typeof(int), typeof(bool)];
        return AccessTools.Method(typeof(FirearmsAnimator), nameof(FirearmsAnimator.Reload), parameters);
    }

    [PatchPrefix]
    public static bool Prefix(FirearmsAnimator __instance, int currentMagType, int nextMagType, bool isFast)
	{
		if (isFast)
		{
			return true;
		}

		__instance.SetMagTypeCurrent(currentMagType);
		__instance.SetMagTypeNew(nextMagType);

		if (__instance.Animator is UnityAnimatorWrapper animator)
		{
			var layerIndex = animator.GetLayerIndex("Hands"); // TODO cache
			animator.Play("RELOAD PROGRESSIVE", layerIndex, 0);
		}

		return false;
	}
}

// event names
// 1551431816 MagShow -> OnMagAppeared
// 798565163  MagHide -> OnMagPuttedToRig
// 1258896930 MagIn   -> OnMagInsertedToWeapon
// 1947938901 MagOut  -> OnMagPulledOutFromWeapon
// PutMagToRig -> Unused
// 443236115 ReplaceSecondMag -> Unused
// 67499002  UseSecondMagForReload -> UseSecondMagForReload/SwitchToReloadWithNewMag

// original:
// [Warning:ModulePatch] Start
// [Warning:ModulePatch] OnMagPulledOutFromWeapon -> MagOut
// [Warning:ModulePatch] OnMagPuttedToRig -> MagHide
// [Warning:ModulePatch] OnMagAppeared -> MagShow
// [Warning:ModulePatch] OnMagInsertedToWeapon -> MagIn
// [Warning:ModulePatch] Start

// new
// [Warning:ModulePatch] Start
// [Warning:ModulePatch] OnMagPulledOutFromWeapon
// [Warning:ModulePatch] OnMagPuttedToRig
// [Warning:ModulePatch] OnMagAppeared
// [Warning:ModulePatch] UseSecondMagForReload
// [Warning:ModulePatch] Start

// hit
// [Warning:ModulePatch] Start
// [Warning:ModulePatch] OnMagAppeared
// [Warning:ModulePatch] OnMagPulledOutFromWeapon
// [Warning:ModulePatch] OnMagInsertedToWeapon
// [Warning:ModulePatch] Start

// admin
// [Warning:ModulePatch] Start
// [Warning:ModulePatch] OnMagAppeared
// [Warning:ModulePatch] OnMagPulledOutFromWeapon
// [Warning:ModulePatch] OnMagInsertedToWeapon
// [Warning:ModulePatch] OnMagPuttedToRig
// [Warning:ModulePatch] Start

// T is a dummy class, to keep all classes separate,
// otherwise they will share method name
public class PrintDebug<T> : ModulePatch
{
	private readonly Type Type;
	private static string MethodName;

	public PrintDebug(Type type, string methodName)
	{
		Type = type;
		MethodName = methodName;
	}

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(Type, MethodName);
    }

    [PatchPrefix]
    public static void Prefix()
	{
		Logger.LogWarning(MethodName);
	}
}
