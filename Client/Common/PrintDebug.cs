//
// Copyright (c) 2026 7Bpencil
//
// This source code is licensed under the MIT license found in the
// LICENSE file in the root directory of this source tree.
//

using HarmonyLib;
using SPT.Reflection.Patching;
using System.Reflection;
using System;

namespace SevenBoldPencil.Common
{
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
}
