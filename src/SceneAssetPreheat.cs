using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

using UnityEngine;

namespace Quest3TriggerUI
{
    // Shared prefab-realization engine, used by the headless startup preheat
    // (ScenePreheat) to fetch live atom prefabs without loading a scene.
    //
    // Path: atomAssetByType lookup → GetCachedPrefab → (miss)
    // AssetBundleManager.LoadAssetAsync → GetAsset<GameObject> →
    // RegisterPrefab + two GetCachedPrefab holds so the bundle stays
    // resident across scene switches (VaM's own hold mechanism —
    // UnregisterAllPrefabsFromAtoms would otherwise drop the entry and
    // UnloadAssetBundle it).
    //
    // The BrowserAssist 预热 button that used to drive this for a selected
    // scene was removed: measurements showed bundle realization alone buys
    // ~nothing on load time, and Person pre-cloning is now handled by the
    // fixed headless pool (AtomClonePool).
    //
    // Safety: only public VaM entry points, same order the game uses them,
    // main-thread coroutine. No atom, storable, physics or timeline is
    // touched, and no VaM file is written.
    internal static class SceneAssetPreheat
    {
        private const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;

        // ---------- VaM asset entry points (reflection) ----------

        private static FieldInfo _atomAssetByTypeField;
        private static FieldInfo _atomAssetBundleField;
        private static FieldInfo _atomAssetNameField;
        private static MethodInfo _loadAssetAsync;
        private static MethodInfo _registerPrefab;
        private static MethodInfo _getCachedPrefab;
        private static MethodInfo _getAssetTyped;
        private static bool _vamResolved;
        private static object _abmInstance;
        private static bool _abmInstanceTried;

        private static bool ResolveVam()
        {
            if (_vamResolved) return true;

            try
            {
                Assembly asm = typeof(SuperController).Assembly;
                Type cs = typeof(SuperController);
                Type mgr = asm.GetType("AssetBundles.AssetBundleManager");
                Type op = asm.GetType("AssetBundles.AssetBundleLoadAssetOperation");
                Type atomAsset = cs.GetNestedType("AtomAsset", All);
                _atomAssetByTypeField = cs.GetField("atomAssetByType", All);
                _loadAssetAsync = mgr == null ? null : mgr.GetMethod("LoadAssetAsync", All);
                _registerPrefab = cs.GetMethod("RegisterPrefab", All, null,
                    new Type[] { typeof(string), typeof(string), typeof(GameObject) }, null);
                _getCachedPrefab = cs.GetMethod("GetCachedPrefab", All, null,
                    new Type[] { typeof(string), typeof(string) }, null);
                if (op != null)
                {
                    MethodInfo generic = op.GetMethod("GetAsset", All, null,
                        Type.EmptyTypes, null);
                    if (generic != null) _getAssetTyped = generic.MakeGenericMethod(
                        new Type[] { typeof(GameObject) });
                }
                if (atomAsset != null)
                {
                    _atomAssetBundleField = atomAsset.GetField("assetBundleName", All);
                    _atomAssetNameField = atomAsset.GetField("assetName", All);
                }
                bool ok = _atomAssetByTypeField != null && _loadAssetAsync != null &&
                    _registerPrefab != null && _getCachedPrefab != null &&
                    _getAssetTyped != null && _atomAssetBundleField != null &&
                    _atomAssetNameField != null;
                if (!ok)
                    Log("VaM 资产入口解析不全（abc=" +
                        (_atomAssetByTypeField != null) + " load=" +
                        (_loadAssetAsync != null) + " reg=" +
                        (_registerPrefab != null) + " get=" +
                        (_getCachedPrefab != null) + " asset=" +
                        (_getAssetTyped != null) + "）");
                _vamResolved = ok;
                return ok;
            }
            catch (Exception e)
            {
                Log("VaM 资产入口解析失败：" + e.Message);
                return false;
            }
        }

        // Headless-preheat entry: realize one atom type's prefab through the
        // resolve → LoadAssetAsync → RegisterPrefab → double-hold path and
        // hand the live prefab back through `box`. Used to fetch the generic
        // Person prefab without a scene.
        internal static IEnumerator RealizePrefab(string type,
            List<GameObject> box)
        {
            if (!ResolveVam()) yield break;
            SuperController sc = SuperController.singleton;
            if (sc == null) yield break;
            string bundle, asset;
            if (!ResolveAtomAsset(type, out bundle, out asset))
            {
                Log("无场景预热：类型 " + type + " 无包内资产");
                yield break;
            }
            GameObject prefab = null;
            try
            {
                prefab = _getCachedPrefab.Invoke(sc,
                    new object[] { bundle, asset }) as GameObject;
            }
            catch { }
            if (prefab == null)
            {
                object abmTarget = AssetBundleManagerTarget(_loadAssetAsync);
                if (!_loadAssetAsync.IsStatic && abmTarget == null) yield break;
                object request = null;
                try
                {
                    request = _loadAssetAsync.Invoke(abmTarget,
                        new object[] { bundle, asset, typeof(GameObject) });
                }
                catch (Exception e)
                {
                    Log("无场景预热：资产请求失败 " + type + "：" + e.Message);
                    yield break;
                }
                if (request == null) yield break;
                yield return sc.StartCoroutine((IEnumerator)request);
                try
                {
                    prefab = _getAssetTyped.Invoke(request, null) as GameObject;
                    if (prefab != null)
                    {
                        _registerPrefab.Invoke(sc,
                            new object[] { bundle, asset, prefab });
                        _getCachedPrefab.Invoke(sc,
                            new object[] { bundle, asset });
                        _getCachedPrefab.Invoke(sc,
                            new object[] { bundle, asset });
                    }
                }
                catch (Exception e)
                {
                    Log("无场景预热：资产登记失败 " + type + "：" + e.Message);
                }
            }
            if (prefab != null) box.Add(prefab);
        }

        // AssetBundleManager exposes LoadAssetAsync either way depending
        // on the build; MethodInfo.IsStatic decides which target to pass, so
        // the call cannot silently no-op on the wrong shape.
        private static object AssetBundleManagerTarget(MethodInfo method)
        {
            if (method.IsStatic) return null;
            if (_abmInstance != null) return _abmInstance;
            if (_abmInstanceTried) return null;
            _abmInstanceTried = true;
            try
            {
                Type mgr = method.DeclaringType;
                if (mgr != null)
                {
                    UnityEngine.Object found = UnityEngine.Object.FindObjectOfType(mgr);
                    if (found != null) _abmInstance = found;
                }
                if (_abmInstance == null)
                {
                    SuperController sc = SuperController.singleton;
                    if (sc != null)
                    {
                        FieldInfo[] fields = sc.GetType().GetFields(All);
                        for (int i = 0; i < fields.Length; i++)
                        {
                            if (fields[i].FieldType != mgr) continue;
                            object value = fields[i].GetValue(sc);
                            if (value != null) { _abmInstance = value; break; }
                        }
                    }
                }
            }
            catch { }
            return _abmInstance;
        }

        private static bool ResolveAtomAsset(string type, out string bundle,
            out string asset)
        {
            bundle = null;
            asset = null;
            try
            {
                SuperController sc = SuperController.singleton;
                if (sc == null) return false;
                IDictionary table = _atomAssetByTypeField.GetValue(sc) as IDictionary;
                if (table == null || !table.Contains(type)) return false;
                object entry = table[type];
                if (entry == null) return false;
                bundle = _atomAssetBundleField.GetValue(entry) as string;
                asset = _atomAssetNameField.GetValue(entry) as string;
                return !string.IsNullOrEmpty(bundle) && !string.IsNullOrEmpty(asset);
            }
            catch (Exception e)
            {
                Log("原子类型解析失败 " + type + "：" + e.Message);
                return false;
            }
        }

        private static void Log(string message)
        {
            try { Quest3TriggerUIPlugin.Log.LogInfo("[预热] " + message); } catch { }
        }
    }
}
