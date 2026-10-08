//
// Copyright (c) 2026 7Bpencil
//
// This source code is licensed under the MIT license found in the
// LICENSE file in the root directory of this source tree.
//

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

namespace SevenBoldPencil.ModularVests;

[Serializable]
public class AnimationBehavioursData
{
	public List<AnimationBehaviourData> AnimationBehaviours;
}

[Serializable]
public class AnimationBehaviourData
{
	public string FullName;
	public int FullNameHash;
	public int EventsListId;
	public List<AnimationEvent> AnimationEvents;
}

[BepInPlugin("7Bpencil.ModularVests", "7Bpencil.ModularVests", "0.0.1")]
public class Plugin : BaseUnityPlugin
{
	public static Plugin Instance;

	public Dictionary<int, Item> Items = new();

    private void Awake()
	{
		Instance = this;
		new Patch_ObjectsFactory_CreateItemAsync().Enable();
		new Patch_PlayerBody_SlotView_CreateAndParent().Enable();
		new Patch_AssetPoolObject_ReturnToPool().Enable();
		new Patch_AssetPoolObject_OnDestroy().Enable();
		new Patch_ObjectsFactory_CreateCleanLootPrefab().Enable();
		new Patch_ObjectsFactory_CreateCleanLootPrefabAsync().Enable();

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

    public void OnCreatedItemGameObject(Item item, GameObject itemGameObject)
	{
		var instanceID = itemGameObject.GetInstanceID();
		Items.Add(instanceID, item);
	}

    public void OnEquippedInSlot(PlayerBody playerBody, GameObject gameObject)
	{
		var instanceID = gameObject.GetInstanceID();
		if (!Items.TryGetValue(instanceID, out var item))
		{
			return;
		}

		// TODO rewrite!

		foreach (var dressItem in gameObject.GetComponentsInChildren<DressItem>())
		{
			// skip root dress, it is already init
			if (dressItem.gameObject != gameObject)
			{
				dressItem.EnableLoot(false);
				foreach (var dress in dressItem.DressPrefab.GetComponentsInChildren<Dress>(includeInactive: true))
				{
					dress.Init(playerBody);
					dress.Skin(playerBody.PlayerBones.RootJoint, playerBody._meshTransform);
				}
			}
		}
	}

    public void OnItemDestroyed(AssetPoolObject assetPoolObject)
	{
		var instanceID = assetPoolObject.gameObject.GetInstanceID();
		if (Items.Remove(instanceID))
		{

		}
	}

	public void EnableLoot(GameObject gameObject)
	{
		foreach (var dressItem in gameObject.GetComponentsInChildren<DressItem>())
		{
			// skip root dress, it is already init
			if (dressItem.gameObject != gameObject)
			{
				dressItem.EnableLoot(true);
			}
		}
	}
}

public class Patch_ObjectsFactory_CreateItemAsync : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
		Type[] parameters = [typeof(Item), typeof(ECameraType), typeof(IPlayer), typeof(bool), typeof(YieldDelegate), typeof(CancellationToken)];
        return AccessTools.Method(typeof(ObjectsFactory), nameof(ObjectsFactory.CreateItemAsync), parameters);
    }

    [PatchPostfix]
    public static void Postfix(ref Task<GameObject> __result, Item item)
	{
		// TODO we should tighten filter, but whats the right approach?
		if (item is CompoundItem)
		{
			__result = WrapTask(__result, item);
		}
	}

    private static async Task<GameObject> WrapTask(Task<GameObject> task, Item item)
    {
        var itemGameObject = await task;
		Plugin.Instance.OnCreatedItemGameObject(item, itemGameObject);
		return itemGameObject;
    }
}

public class Patch_PlayerBody_SlotView_CreateAndParent : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(PlayerBody.SlotView), nameof(PlayerBody.SlotView.CreateAndParent));
    }

    [PatchPostfix]
    public static void Postfix(PlayerBody playerBody, GameObject model)
	{
		Plugin.Instance.OnEquippedInSlot(playerBody, model);
	}
}

public class Patch_AssetPoolObject_ReturnToPool : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(AssetPoolObject), nameof(AssetPoolObject.ReturnToPool));
    }

    [PatchPrefix]
    public static void Prefix(AssetPoolObject __instance)
	{
		Plugin.Instance.OnItemDestroyed(__instance);
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
		Plugin.Instance.OnItemDestroyed(__instance);
	}
}

public class Patch_ObjectsFactory_CreateCleanLootPrefab : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
		Type[] parameters = [typeof(Item), typeof(ECameraType), typeof(IPlayer)];
        return AccessTools.Method(typeof(ObjectsFactory), nameof(ObjectsFactory.CreateCleanLootPrefab));
    }

    [PatchPostfix]
    public static void Postfix(GameObject __result, Item item)
	{
		// TODO we should tighten filter, but whats the right approach?
		if (item is CompoundItem)
		{
			Plugin.Instance.EnableLoot(__result);
		}
	}
}

public class Patch_ObjectsFactory_CreateCleanLootPrefabAsync : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
		Type[] parameters = [typeof(Item), typeof(IPlayer)];
        return AccessTools.Method(typeof(ObjectsFactory), nameof(ObjectsFactory.CreateCleanLootPrefabAsync));
    }

    [PatchPostfix]
    public static void Postfix(ref Task<GameObject> __result, Item item)
	{
		// TODO we should tighten filter, but whats the right approach?
		if (item is CompoundItem)
		{
			__result = WrapTask(__result, item);
		}
	}

    private static async Task<GameObject> WrapTask(Task<GameObject> task, Item item)
	{
        var itemGameObject = await task;
		Plugin.Instance.EnableLoot(itemGameObject);
		return itemGameObject;
	}
}
